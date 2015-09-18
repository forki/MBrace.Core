﻿namespace MBrace.Runtime

open System
open System.Threading
open System.Threading.Tasks
open System.Runtime.Serialization
open System.Collections.Generic
open System.Collections.Concurrent

open Nessos.FsPickler
open Nessos.Vagabond

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Runtime.Utils
open MBrace.Runtime.Utils.PrettyPrinters

/// Represents a cloud computation that is being executed in the cluster.
[<AbstractClass>]
type CloudProcess internal () =

    /// Gets the parent cancellation token for the cloud process
    abstract CancellationToken : ICloudCancellationToken

    /// <summary>
    ///     Asynchronously awaits boxed result of given cloud process.
    /// </summary>
    /// <param name="timeoutMilliseconds">Timeout in milliseconds. Defaults to infinite timeout.</param>
    abstract AwaitResultBoxed : ?timeoutMilliseconds:int -> Async<obj>
    /// <summary>
    ///     Return the result if available or None if not available.
    /// </summary>
    abstract TryGetResultBoxed : unit -> Async<obj option>

    /// Awaits the boxed result of the process.
    abstract ResultBoxed : obj

    /// Date of process execution start.
    abstract StartTime : DateTime option

    /// TimeSpan of executing process.
    abstract ExecutionTime : TimeSpan option

    /// DateTime of cloud process completion
    abstract CompletionTime : DateTime option

    /// Active number of work items related to the process.
    abstract ActiveWorkItems : int
    /// Max number of concurrently executing work items for process.
    abstract MaxActiveWorkItems : int
    /// Number of work items that have been completed for process.
    abstract CompletedWorkItems : int
    /// Number of faults encountered while executing work items for process.
    abstract FaultedWorkItems : int
    /// Total number of work items related to the process.
    abstract TotalWorkItems : int
    /// Process execution status.
    abstract Status : CloudProcessStatus

    /// Cloud process identifier
    abstract Id : string
    /// Cloud process user-supplied name
    abstract Name : string option
    /// Process return type
    abstract Type : Type

    /// Cancels execution of given process
    abstract Cancel : unit -> unit

    /// Cloud process cloud logs observable
    [<CLIEvent>]
    abstract Logs : IEvent<CloudLogEntry>

    /// <summary>
    ///     Asynchronously fetches log all log entries generated by given cloud process.  
    /// </summary>
    /// <param name="filter">User-specified log entry filtering function.</param>
    abstract GetLogsAsync : ?filter:(CloudLogEntry -> bool) -> Async<CloudLogEntry []>

    /// <summary>
    ///     Asynchronously fetches log all log entries generated by given cloud process.  
    /// </summary>
    /// <param name="filter">User-specified log entry filtering function.</param>
    member __.GetLogs(?filter: CloudLogEntry -> bool) = __.GetLogsAsync(?filter = filter) |> Async.RunSync

    /// <summary>
    ///     Asynchronously fetches log all log entries generated by given cloud process.  
    /// </summary>
    /// <param name="filter">User-specified log entry filtering function.</param>
    abstract ShowLogs : ?filter:(CloudLogEntry -> bool) -> unit

    interface ICloudProcess with
        member x.Id: string = x.Id

        member x.AwaitResultBoxed(?timeoutMilliseconds: int): Async<obj> = 
            x.AwaitResultBoxed(?timeoutMilliseconds = timeoutMilliseconds)
    
        member x.CancellationToken = x.CancellationToken
        member x.IsCanceled: bool = 
            match x.Status with
            | CloudProcessStatus.Canceled -> true
            | _ -> false
        
        member x.IsCompleted: bool = 
            match x.Status with
            | CloudProcessStatus.Completed -> true
            | _ -> false
        
        member x.IsFaulted: bool = 
            match x.Status with
            | CloudProcessStatus.Faulted | CloudProcessStatus.UserException -> true
            | _ -> false

        member x.ResultBoxed: obj = x.ResultBoxed
        member x.Status: TaskStatus = x.Status.TaskStatus
        member x.TryGetResultBoxed(): Async<obj option> = x.TryGetResultBoxed()

    /// Gets a printed report on the current process status
    member p.GetInfo() : string = CloudProcessReporter.Report([|p|], "Process", false)

    /// Prints a report on the current process status to stdout
    member p.ShowInfo () : unit = Console.WriteLine(p.GetInfo())

/// Represents a cloud computation that is being executed in the cluster.
and [<Sealed; DataContract; NoEquality; NoComparison>] CloudProcess<'T> internal (source : ICloudProcessCompletionSource, runtime : IRuntimeManager) =
    inherit CloudProcess()

    let [<DataMember(Name = "ProcessCompletionSource")>] entry = source
    let [<DataMember(Name = "RuntimeId")>] runtimeId = runtime.Id

    let mkCell () = CacheAtom.Create(async { return! entry.GetState() }, intervalMilliseconds = 500)

    let [<IgnoreDataMember>] mutable lockObj = new obj()
    let [<IgnoreDataMember>] mutable cell = mkCell()
    let [<IgnoreDataMember>] mutable runtime = runtime
    let [<IgnoreDataMember>] mutable logPoller : ILogPoller<CloudLogEntry> option = None

    let getLogEvent() =
        match logPoller with
        | Some l -> l
        | None ->
            lock lockObj (fun () ->
                match logPoller with
                | None ->
                    let l = runtime.CloudLogManager.GetCloudLogPollerByProcess(source.Id) |> Async.RunSync
                    logPoller <- Some l
                    l
                | Some l -> l)

    /// Triggers elevation in event of serialization
    [<OnSerialized>]
    let _onDeserialized (_ : StreamingContext) = 
        lockObj <- new obj()
        cell <- mkCell()
        runtime <- RuntimeManagerRegistry.Resolve runtimeId

    /// <summary>
    ///     Asynchronously awaits cloud process result
    /// </summary>
    /// <param name="timeoutMilliseconds">Timeout in milliseconds. Defaults to infinite timeout.</param>
    member __.AwaitResult (?timeoutMilliseconds:int) : Async<'T> = async {
        let timeoutMilliseconds = defaultArg timeoutMilliseconds Timeout.Infinite
        let! result = Async.WithTimeout(async { return! entry.AwaitResult() }, timeoutMilliseconds) 
        return unbox<'T> result.Value
    }

    /// <summary>
    ///     Attempts to get cloud process result. Returns None if not completed.
    /// </summary>
    member __.TryGetResult () : Async<'T option> = async {
        let! result = entry.TryGetResult()
        return result |> Option.map (fun r -> unbox<'T> r.Value)
    }

    /// Synchronously awaits cloud process result 
    member __.Result : 'T = __.AwaitResult() |> Async.RunSync

    override __.AwaitResultBoxed (?timeoutMilliseconds:int) = async {
        let! r = __.AwaitResult(?timeoutMilliseconds = timeoutMilliseconds)
        return box r
    }

    override __.TryGetResultBoxed () = async {
        let! r = __.TryGetResult()
        return r |> Option.map box
    }

    override __.ResultBoxed = __.Result |> box

    override __.StartTime =
        match cell.Value.ExecutionTime with
        | NotStarted -> None
        | Started(st,_) -> Some st
        | Finished(st,_,_) -> Some st

    override __.ExecutionTime =
        match cell.Value.ExecutionTime with
        | NotStarted -> None
        | Started(_,et) -> Some et
        | Finished(_,et,_) -> Some et

    override __.CompletionTime =
        match cell.Value.ExecutionTime with
        | Finished(_,_,ct) -> Some ct
        | _ -> None

    override __.CancellationToken = entry.Info.CancellationTokenSource.Token
    /// Active number of work items related to the process.
    override __.ActiveWorkItems = cell.Value.ActiveWorkItemCount
    override __.MaxActiveWorkItems = cell.Value.MaxActiveWorkItemCount
    override __.CompletedWorkItems = cell.Value.CompletedWorkItemCount
    override __.FaultedWorkItems = cell.Value.FaultedWorkItemCount
    override __.TotalWorkItems = cell.Value.TotalWorkItemCount
    override __.Status = cell.Value.Status
    override __.Id = entry.Id
    override __.Name = entry.Info.Name
    override __.Type = typeof<'T>
    override __.Cancel() = entry.Info.CancellationTokenSource.Cancel()

    [<CLIEvent>]
    override __.Logs = getLogEvent() :> IEvent<CloudLogEntry>

    override __.GetLogsAsync(?filter : CloudLogEntry -> bool) = async { 
        let! entries = runtime.CloudLogManager.GetAllCloudLogsByProcess __.Id
        let filtered = match filter with None -> entries | Some f -> Seq.filter f entries
        return filtered |> Seq.toArray
    }

    override __.ShowLogs (?filter : CloudLogEntry -> bool) =
        let entries = runtime.CloudLogManager.GetAllCloudLogsByProcess __.Id |> Async.RunSync
        let filtered = match filter with None -> entries | Some f -> Seq.filter f entries
        for e in filtered do Console.WriteLine(CloudLogEntry.Format(e, showDate = true))

    interface ICloudProcess<'T> with
        member x.AwaitResult(timeoutMilliseconds: int option): Async<'T> =
            x.AwaitResult(?timeoutMilliseconds = timeoutMilliseconds)
        
        member x.CancellationToken: ICloudCancellationToken = 
            entry.Info.CancellationTokenSource.Token
        
        member x.Result: 'T = x.Result
        
        member x.Status: TaskStatus = cell.Value.Status.TaskStatus
        
        member x.TryGetResult(): Async<'T option> = x.TryGetResult()

/// Cloud Process client object
and [<AutoSerializable(false)>] internal CloudProcessManagerClient(runtime : IRuntimeManager) =
    static let clients = new ConcurrentDictionary<IRuntimeId, IRuntimeManager> ()
    do clients.TryAdd(runtime.Id, runtime) |> ignore

    member __.Id = runtime.Id

    /// <summary>
    ///     Fetches cloud process by provided cloud process id.
    /// </summary>
    /// <param name="procId">Cloud process identifier.</param>
    member self.GetProcessBySource (entry : ICloudProcessCompletionSource) = async {
        let! assemblies = runtime.AssemblyManager.DownloadAssemblies(entry.Info.Dependencies)
        let loadInfo = runtime.AssemblyManager.LoadAssemblies(assemblies)
        for li in loadInfo do
            match li with
            | NotLoaded id -> runtime.SystemLogger.Logf LogLevel.Error "could not load assembly '%s'" id.FullName 
            | LoadFault(id, e) -> runtime.SystemLogger.Logf LogLevel.Error "error loading assembly '%s':\n%O" id.FullName e
            | Loaded _ -> ()

        let returnType = runtime.Serializer.UnPickleTyped entry.Info.ReturnType
        let ex = Existential.FromType returnType
        let task = ex.Apply { 
            new IFunc<CloudProcess> with 
                member __.Invoke<'T> () = new CloudProcess<'T>(entry, runtime) :> CloudProcess
        }

        return task
    }

    member self.TryGetProcessById(id : string) = async {
        let! source = runtime.ProcessManager.TryGetProcessById id
        match source with
        | None -> return None
        | Some e ->
            let! t = self.GetProcessBySource e
            return Some t
    }


    member self.GetAllProcesses() = async {
        let! entries = runtime.ProcessManager.GetAllProcesses()
        return!
            entries
            |> Seq.map (fun e -> self.GetProcessBySource e)
            |> Async.Parallel
    }

    member __.ClearProcess(proc:CloudProcess) = async {
        do! runtime.ProcessManager.ClearProcess(proc.Id)
    }

    /// <summary>
    ///     Clears all processes from the runtime.
    /// </summary>
    member pm.ClearAllProcesses() = async {
        do! runtime.ProcessManager.ClearAllProcesses()
    }

    /// Gets a printed report of all currently executing processes
    member pm.FormatProcesses() : string =
        let procs = pm.GetAllProcesses() |> Async.RunSync
        CloudProcessReporter.Report(procs, "Processes", borders = false)

    /// Prints a report of all currently executing processes to stdout.
    member pm.ShowProcesses() : unit =
        /// TODO : add support for filtering processes
        Console.WriteLine(pm.FormatProcesses())

    static member TryGetById(id : IRuntimeId) = clients.TryFind id

    interface IDisposable with
        member __.Dispose() = clients.TryRemove runtime.Id |> ignore
        
         
and internal CloudProcessReporter() = 
    static let template : Field<CloudProcess> list = 
        [ Field.create "Name" Left (fun p -> match p.Name with Some n -> n | None -> "")
          Field.create "Cloud Process Id" Right (fun p -> p.Id)
          Field.create "Status" Right (fun p -> sprintf "%A" p.Status)
          Field.create "Execution Time" Left (fun p -> Option.toNullable p.ExecutionTime)
          Field.create "Work items" Center (fun p -> sprintf "%3d / %3d / %3d / %3d"  p.ActiveWorkItems p.FaultedWorkItems p.CompletedWorkItems p.TotalWorkItems)
          Field.create "Result Type" Left (fun p -> Type.prettyPrintUntyped p.Type) 
          Field.create "Start Time" Left (fun p -> Option.toNullable p.StartTime)
          Field.create "Completion Time" Left (fun p -> Option.toNullable p.CompletionTime)
        ]
    
    static member Report(processes : seq<CloudProcess>, title : string, borders : bool) = 
        let ps = processes 
                 |> Seq.sortBy (fun p -> p.StartTime)
                 |> Seq.toList

        sprintf "%s\nWork items : Active / Faulted / Completed / Total\n" <| Record.PrettyPrint(template, ps, title, borders)