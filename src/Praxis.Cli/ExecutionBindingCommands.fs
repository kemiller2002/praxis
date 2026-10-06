namespace Praxis.Cli

open Praxis.Application.Execution
open Praxis.Domain.Execution
open Praxis.Domain.Provenance
open Praxis.Infrastructure.Execution

/// The work transitions a governed execution envelope follows.
[<RequireQualifiedAccess>]
type WorkGate =
    | Begin
    | Resume
    | Block
    | Complete
    | Abandon
    | Continue

/// Binds work transitions to execution envelopes (PRX-EXEC-030): a check
/// before the transition (resume divergence, completion receipts) and a
/// mirror after it. The work transition kernel stays the authority for work
/// state; this adapter only calls `ExecutionBinding` and reports.
[<RequireQualifiedAccess>]
module ExecutionBindingCommands =
    let private optionValue (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.tryPick (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private ids (arguments: string list) =
        arguments |> List.pairwise |> List.choose (fun (flag, value) -> if flag = "--id" then Some value else None)

    let private requester (actor: Actor) = { Actor = ExecutionCommands.executionActor actor }

    let private report (failure: ExecutionFailure) =
        match failure with
        | ExecutionFailure.Invalid message ->
            eprintfn "ERROR %s" message
            2
        | ExecutionFailure.Refused message ->
            eprintfn "REFUSED %s" message
            3

    let private warn workItem (result: Result<'a, ExecutionFailure>) =
        match result with
        | Ok _ -> ()
        | Error(ExecutionFailure.Invalid message)
        | Error(ExecutionFailure.Refused message) -> eprintfn "WARN execution envelope for '%s' not bound: %s" workItem message

    let private role (arguments: string list) =
        optionValue "--role" arguments |> Option.bind ExecutionRole.tryParse |> Option.defaultValue ExecutionRole.Implementation

    let private origin (ports: ExecutionPorts) transition =
        match ports.Host.RemoteExecutor() with
        | Some executor -> ExecutionOrigin.Remote executor
        | None -> ExecutionOrigin.WorkTransition transition

    /// Bind the execution a work transition created for each item, sharing
    /// the telemetry execution's ID. Called on `work begin`'s success path
    /// (so `plan execute-group`, which begins members in-process, binds
    /// too); it never changes the transition's exit code.
    let bindBegun (root: string) (arguments: string list) (actor: Actor) (workItems: string list) =
        let ports = FileExecutionPorts.create root

        for workItem in workItems do
            ExecutionBinding.bind
                ports
                (requester actor)
                { WorkItem = workItem
                  ExecutionId = FileExecutionPorts.activeTelemetryExecution root workItem actor.Id
                  Origin = origin ports "work.begin"
                  Role = role arguments
                  Parent = None
                  Baseline = None
                  Branch = None }
            |> warn workItem

        0

    let private bindSuccessor (root: string) (ports: ExecutionPorts) (arguments: string list) (actor: Actor) transition workItem (parent: string option) =
        match FileExecutionPorts.activeTelemetryExecution root workItem actor.Id with
        | Some id when not (ports.Store.Exists id) ->
            ExecutionBinding.bind
                ports
                (requester actor)
                { WorkItem = workItem
                  ExecutionId = Some id
                  Origin = origin ports transition
                  Role = role arguments
                  Parent = parent
                  Baseline = None
                  Branch = None }
            |> warn workItem
        | _ -> ()

    /// Wrap a work transition: refuse before it when bound executions say
    /// so, and mirror its outcome after it succeeds.
    let guard (root: string) (gate: WorkGate) (arguments: string list) (inner: Actor -> int) (actor: Actor) =
        let ports = FileExecutionPorts.create root
        let who = requester actor
        let items = ids arguments
        let reason = optionValue "--reason" arguments

        let before =
            match gate with
            | WorkGate.Begin
            | WorkGate.Continue -> ExecutionBinding.preflight ports
            | WorkGate.Resume ->
                items
                |> List.fold (fun acc item -> acc |> Result.bind (fun () -> ExecutionBinding.preflightResume ports who item (optionValue "--rebind-reason" arguments) |> Result.map ignore)) (Ok())
            | WorkGate.Complete -> items |> List.fold (fun acc item -> acc |> Result.bind (fun () -> ExecutionBinding.preflightComplete ports who item)) (Ok())
            | WorkGate.Block
            | WorkGate.Abandon -> Ok()

        let predecessors =
            items |> List.map (fun item -> item, ExecutionBinding.forWorkItem ports item |> List.filter (fun e -> e.Origin <> ExecutionOrigin.Explicit && not (ExecutionState.isTerminal e.State)))

        match before with
        | Error failure -> report failure
        | Ok() ->
            let code = inner actor

            if code = 0 then
                for item in items do
                    match gate with
                    | WorkGate.Begin -> ()
                    | WorkGate.Resume ->
                        ExecutionBinding.mirror ports who item "work.resume" ExecutionState.Active None |> ignore
                        bindSuccessor root ports arguments actor "work.resume" item (ExecutionBinding.forWorkItem ports item |> List.tryLast |> Option.map _.ExecutionId)
                    | WorkGate.Block -> ExecutionBinding.mirror ports who item "work.block" (ExecutionState.Blocked(reason |> Option.defaultValue "")) reason |> ignore
                    | WorkGate.Complete -> ExecutionBinding.mirror ports who item "work.complete" ExecutionState.Completed None |> ignore
                    | WorkGate.Abandon -> ExecutionBinding.mirror ports who item "work.abandon" (ExecutionState.Abandoned(reason |> Option.defaultValue "")) reason |> ignore
                    | WorkGate.Continue ->
                        let parent = predecessors |> List.tryFind (fst >> (=) item) |> Option.bind (snd >> List.tryLast) |> Option.map _.ExecutionId
                        ExecutionBinding.mirror ports who item "work.continue" ExecutionState.Interrupted (Some "taken over by a successor execution") |> ignore
                        bindSuccessor root ports arguments actor "work.continue" item parent

            code

    /// After a runtime-free envelope reconciled (`reconcile --envelope`),
    /// bind and mirror the execution it describes.
    let afterFallback (root: string) (envelopePath: string) (code: int) =
        if code = 0 then
            match Praxis.Infrastructure.Work.ReconciliationEnvelopeJson.read envelopePath with
            | Ok input -> ExecutionBinding.fallback (FileExecutionPorts.create root) input |> warn input.WorkItem
            | Error codes -> eprintfn "WARN execution envelope not bound: %s" (String.concat "," codes)

        code
