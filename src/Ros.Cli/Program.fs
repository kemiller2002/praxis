module Ros.Cli.Program

open System
open System.Collections.Generic
open System.IO
open Ros.Application.Artifacts
open Ros.Application.Git
open Ros.Application.Work
open Ros.Contracts.Cli
open Ros.Contracts.Git
open Ros.Contracts.Work
open Ros.Domain.Artifacts
open Ros.Domain.Git
open Ros.Domain.Telemetry
open Ros.Domain.Work
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Git
open Ros.Infrastructure.Work
open System.Text.Json
open System.Text.Json.Nodes

[<Literal>]
let Version = "0.2.0-shadow"

let private usage =
    "Usage: ros-fs [--root PATH] version | artifacts validate [--json] | registry build [--dry-run] | registry check | git status [--json] | work decide [options] | work plan [options] [--resolve-telemetry --candidate EXECUTIONID=active|finalized]* [--requested-execution-id ID] | work context-plan [options] | work backlog-decide --state STATE --action ACTION [--reason TEXT] | work backlog-promotion-plan --id ID [--queue-state ID=STATE] [--type TYPE] | work validate [--json] | work backlog-validate [--json] | work backlog-transition --id ID --action {ready|block|abandon} --occurred-at TIMESTAMP [--reason TEXT] | work capture --title TITLE --occurred-at TIMESTAMP [--id ID] [--priority {high|medium|low}] [--description TEXT] [--tag TAG]* [--actor NAME] [--source NAME] [--source-reference REF] | work update --id ID --occurred-at TIMESTAMP [--title TEXT] [--description TEXT] [--priority {high|medium|low}] [--tag TAG]* | work attach --id ID --occurred-at TIMESTAMP --file PATH[=NAME] [--file PATH[=NAME]]* | work start --id ID [--id ID]* --occurred-at TIMESTAMP [--type TYPE] [--actor NAME] [--classification NAME]* | work resume --id ID [--id ID]* --occurred-at TIMESTAMP [--actor NAME] | work block --id ID [--id ID]* --occurred-at TIMESTAMP [--reason TEXT] [--actor NAME] | work complete --id ID [--id ID]* --occurred-at TIMESTAMP [--evidence TYPE=PATH]* [--conclusion TEXT] [--actor NAME] | telemetry adapters | telemetry show [TARGET] | telemetry summary|summarize [TARGET] | telemetry finalize [TARGET] [--quiet] | telemetry record [TARGET] --metric ID --value VALUE [--unit TEXT] [--currency TEXT] [--quality {observed|derived|estimated}] [--confidence VALUE] [--scope TEXT] [--source-type TEXT] [--source-name TEXT] [--mechanism TEXT] [--pricing-source TEXT] [--pricing-version TEXT] [--collected-at TIMESTAMP] [--quiet] | telemetry ingest [TARGET] --input FILE [--adapter NAME] [--quiet] | telemetry classify [TARGET] --classification NAME [--classification NAME]* [--rationale TEXT] [--evidence-link LINK]* [--rd-context FILE] [--quiet] | telemetry start WORKITEMID [--classification NAME]* [--classification-rationale TEXT] [--quiet] | adapter call --store FILE --request FILE | adapter publish --target FILE"

let private parseRoot (arguments: string array) =
    let values = ResizeArray<string>(arguments)
    let rootIndex = values.IndexOf("--root")

    if rootIndex < 0 then
        Ok(Path.GetFullPath(Directory.GetCurrentDirectory()), values |> Seq.toList)
    elif rootIndex + 1 >= values.Count || values[rootIndex + 1].StartsWith("--", StringComparison.Ordinal) then
        Error "--root requires a value"
    else
        let root = Path.GetFullPath values[rootIndex + 1]
        values.RemoveAt(rootIndex + 1)
        values.RemoveAt(rootIndex)
        Ok(root, values |> Seq.toList)

let private renderFinding (finding: ArtifactFinding) =
    let location =
        if String.IsNullOrEmpty finding.Field then finding.Path else $"{finding.Path}:{finding.Field}"

    $"{location}: {finding.Message}"

let private reportDependencyFailure (failure: DependencyFailure) =
    let location = failure.Path |> Option.map (fun path -> $" '{path}'") |> Option.defaultValue ""

    let outcome =
        match failure.Outcome with
        | DependencyOutcome.Failed -> "failed"
        | DependencyOutcome.Indeterminate -> "indeterminate"

    eprintfn "ERROR %s%s %s: %s" failure.Operation location outcome failure.Message
    1

let private runValidation asJson repository =
    match ArtifactOperations.validate repository with
    | ValidationOutcome.DependencyFailure failure -> reportDependencyFailure failure
    | ValidationOutcome.Completed findings when asJson ->
        printf "%s" (FindingContract.renderJson findings)
        if findings.IsEmpty then 0 else 1
    | ValidationOutcome.Completed [] ->
        printfn "validation passed"
        0
    | ValidationOutcome.Completed findings ->
        for finding in findings do
            eprintfn "ERROR %s\n  REPAIR %s" (renderFinding finding) (FindingContract.repair finding)

        eprintfn "validation failed with %d error(s)" findings.Length
        1

let private runRegistryCheck repository =
    match ArtifactOperations.checkRegistries repository with
    | RegistryCheckOutcome.DependencyFailure failure -> reportDependencyFailure failure
    | RegistryCheckOutcome.Completed [] ->
        printfn "registries are current"
        0
    | RegistryCheckOutcome.Completed findings ->
        for finding in findings do
            eprintfn "ERROR %s" (renderFinding finding)
        1

let private runRegistryBuild dryRun repository =
    let reportChange (change: RegistryChange) =
        printfn "%s %s" (if dryRun then "WOULD WRITE" else "WROTE") change.Path

    match ArtifactOperations.buildRegistries dryRun repository with
    | RegistryBuildOutcome.DependencyFailure failure -> reportDependencyFailure failure
    | RegistryBuildOutcome.Rejected findings ->
        for finding in findings do
            eprintfn "ERROR %s" (renderFinding finding)
        1
    | RegistryBuildOutcome.Completed changes ->
        changes |> List.iter reportChange
        printfn "%d registry file(s) %s" changes.Length (if dryRun then "would change" else "changed")
        0
    | RegistryBuildOutcome.Incomplete(written, pending, failure) ->
        written |> List.iter reportChange
        reportDependencyFailure failure |> ignore
        eprintfn "registry build incomplete: %d written; %d pending" written.Length pending.Length
        1

let private renderGitChange change =
    match change.OriginalPath with
    | Some originalPath -> $"{GitStatus.code change.Status} {originalPath} -> {change.Path}"
    | None -> $"{GitStatus.code change.Status} {change.Path}"

let private runGitStatus asJson repository =
    let observation = GitOperations.observe repository

    if asJson then
        printf "%s" (GitStatusContract.renderJson observation)

    match observation with
    | GitStatusObservation.Clean ->
        if not asJson then printfn "working tree clean"
        0
    | GitStatusObservation.Changed changes ->
        if not asJson then changes |> List.iter (renderGitChange >> printfn "%s")
        0
    | GitStatusObservation.Unavailable failure ->
        if not asJson then eprintfn "ERROR %s unavailable: %s" failure.Operation failure.Message
        1

let private optionValue name arguments =
    arguments
    |> List.tryFindIndex ((=) name)
    |> Option.bind (fun index -> arguments |> List.tryItem (index + 1))

let private optionValues name arguments =
    arguments
    |> List.mapi (fun index value -> index, value)
    |> List.choose (fun (index, value) -> if value = name then arguments |> List.tryItem (index + 1) else None)

let private parseWorkState value =
    match value with
    | "ready" -> Some LiveWorkState.Ready
    | "active" -> Some LiveWorkState.Active
    | "blocked" -> Some LiveWorkState.Blocked
    | "complete" -> Some LiveWorkState.Complete
    | _ -> None

let private parseWorkAction value =
    match value with
    | "begin" -> Some WorkAction.Begin
    | "block" -> Some WorkAction.Block
    | "resume" -> Some WorkAction.Resume
    | "complete" -> Some WorkAction.Complete
    | _ -> None

let private parseBacklogState value =
    match value with
    | "captured" -> Some BacklogState.Captured
    | "ready" -> Some BacklogState.Ready
    | "blocked" -> Some BacklogState.Blocked
    | "abandoned" -> Some BacklogState.Abandoned
    | _ -> None

let private parseBacklogAction value =
    match value with
    | "ready" -> Some BacklogAction.Ready
    | "block" -> Some BacklogAction.Block
    | "abandon" -> Some BacklogAction.Abandon
    | "start" -> Some BacklogAction.Start
    | _ -> None

let private runWorkDecision arguments =
    let state = optionValue "--state" arguments |> Option.bind parseWorkState
    let action = optionValue "--action" arguments |> Option.bind parseWorkAction

    match state, action with
    | Some current, Some requested ->
        let request =
            { State = current
              Action = requested
              BlockReason = optionValue "--reason" arguments
              RequiredEvidence = optionValues "--required" arguments |> Set.ofList
              ProvidedEvidence = optionValues "--provided" arguments |> Set.ofList }

        let decision = WorkOperations.decideTransition request
        printf "%s" (WorkDecisionContract.renderJson decision)

        match decision with
        | TransitionDecision.Allowed _ -> 0
        | TransitionDecision.Rejected _ -> 1
    | _ ->
        eprintfn "ERROR work decide requires a valid --state and --action"
        2

let private parseEvidence (value: string) : WorkEvidence option =
    match value.Split('=', 2) with
    | [| evidenceType; path |] when evidenceType.Length > 0 && path.Length > 0 ->
        Some
            { Type = evidenceType
              Path = path }
    | _ -> None

let private parseCandidate workItemId (value: string) : ExecutionLinkCandidate option =
    match value.Split('=', 2) with
    | [| executionId; "active" |] when executionId.Length > 0 ->
        Some
            { ExecutionId = executionId
              WorkItemId = workItemId
              Status = ExecutionStatus.Active }
    | [| executionId; "finalized" |] when executionId.Length > 0 ->
        Some
            { ExecutionId = executionId
              WorkItemId = workItemId
              Status = ExecutionStatus.Finalized }
    | _ -> None

let private defaultLocalState (action: WorkAction) =
    match action with
    | WorkAction.Begin
    | WorkAction.Resume -> "active"
    | WorkAction.Block -> "blocked"
    | WorkAction.Complete -> "complete"

let private runWorkPlan root arguments =
    let state = optionValue "--state" arguments |> Option.bind parseWorkState
    let action = optionValue "--action" arguments |> Option.bind parseWorkAction
    let workItem = optionValue "--id" arguments
    let workType = optionValue "--type" arguments
    let occurredAt = optionValue "--occurred-at" arguments
    let currentEvidence = optionValues "--current-evidence" arguments |> List.map parseEvidence
    let providedEvidence = optionValues "--evidence" arguments |> List.map parseEvidence

    match state, action, workItem, workType, occurredAt with
    | Some current, Some requested, Some workItemId, Some itemType, Some timestamp
        when currentEvidence |> List.forall Option.isSome
             && providedEvidence |> List.forall Option.isSome ->
        let request =
            { Item =
                { Id = workItemId
                  WorkType = itemType
                  LocalState = optionValue "--local-state" arguments |> Option.defaultValue (WorkDecisionContract.stateName current)
                  SemanticState = current
                  Evidence = currentEvidence |> List.choose id
                  BlockReason = optionValue "--current-block-reason" arguments
                  UpdatedAt = optionValue "--updated-at" arguments
                  CompletedAt = optionValue "--completed-at" arguments
                  TelemetryExecutionIds = optionValues "--telemetry-id" arguments }
              Action = requested
              TargetLocalState = optionValue "--target-local-state" arguments |> Option.defaultValue (defaultLocalState requested)
              BlockReason = optionValue "--reason" arguments
              RequiredEvidence = optionValues "--required" arguments |> Set.ofList
              ProvidedEvidence = providedEvidence |> List.choose id
              Repository = optionValue "--repository" arguments |> Option.defaultValue "repository"
              ProtocolVersion = optionValue "--protocol-version" arguments |> Option.defaultValue "1.0.0"
              OccurredAt = timestamp
              ChangedPaths = optionValues "--path" arguments
              TelemetryEnabled = arguments |> List.contains "--telemetry-enabled" }

        if arguments |> List.contains "--resolve-telemetry" then
            let explicitCandidates = optionValues "--candidate" arguments |> List.map (parseCandidate workItemId)

            if explicitCandidates |> List.forall Option.isSome then
                match WorkOperations.planTransition request with
                | WorkPlanOutcome.Rejected rejection ->
                    printf "%s" (WorkPlanContract.renderJson (WorkPlanOutcome.Rejected rejection))
                    1
                | WorkPlanOutcome.Planned plan ->
                    let repository: TelemetryStateRepository =
                        { Observe =
                            fun observedWorkItemId ->
                                { LinkedExecutionIds = plan.Item.TelemetryExecutionIds
                                  Candidates =
                                    if explicitCandidates.IsEmpty then
                                        FileTelemetryStateRepository.readCandidates root observedWorkItemId
                                    else
                                        explicitCandidates |> List.choose id
                                  RequestedExecutionId = optionValue "--requested-execution-id" arguments } }

                    let outcome = WorkOperations.resolveTelemetry repository plan
                    printf "%s" (WorkPlanContract.renderResolvedTelemetryJson outcome)

                    match outcome with
                    | ResolvedTelemetryOutcome.Resolved _ -> 0
                    | ResolvedTelemetryOutcome.PendingNewExecution _
                    | ResolvedTelemetryOutcome.Rejected _ -> 1
            else
                eprintfn "ERROR work plan --resolve-telemetry requires EXECUTIONID=active|finalized for every --candidate"
                2
        elif arguments |> List.contains "--verify-evidence" then
            let outcome = WorkOperations.planVerifiedTransition (FileEvidenceRepository.create root) request
            printf "%s" (WorkPlanContract.renderVerifiedJson outcome)

            match outcome with
            | VerifiedWorkPlanOutcome.Planned _ -> 0
            | VerifiedWorkPlanOutcome.TransitionRejected _
            | VerifiedWorkPlanOutcome.EvidenceRejected _ -> 1
        else
            let outcome = WorkOperations.planTransition request
            printf "%s" (WorkPlanContract.renderJson outcome)

            match outcome with
            | WorkPlanOutcome.Planned _ -> 0
            | WorkPlanOutcome.Rejected _ -> 1
    | _ ->
        eprintfn "ERROR work plan requires valid --id, --type, --state, --action, --occurred-at, and TYPE=PATH evidence"
        2

/// Mirrors production `gitPaths` (`tools/ros_cli.mjs`): real working-tree
/// changed paths plus, when `$ROS_BASE_REF` resolves to an existing commit,
/// its committed-range diff against `HEAD` — deduped and ordinally sorted.
/// A non-repository directory yields no paths (greenfield compatibility); any
/// other Git or base-ref-diff failure is an error, never a silent empty list.
let private realObservedGitPaths root : Result<string list, GitFailure> =
    let workingTreePaths =
        match GitOperations.observe (ProcessGitRepository.create root) with
        | GitStatusObservation.Clean -> Ok []
        | GitStatusObservation.Changed changes -> Ok(changes |> List.map _.Path)
        | GitStatusObservation.Unavailable failure when failure.Reason = GitUnavailableReason.NotRepository -> Ok []
        | GitStatusObservation.Unavailable failure -> Error failure

    workingTreePaths
    |> Result.bind (fun paths ->
        let baseRef =
            match Environment.GetEnvironmentVariable "ROS_BASE_REF" with
            | null
            | "" -> None
            | value -> Some value

        match GitOperations.compareBase (ProcessGitRepository.createBaseComparison root) baseRef with
        | GitBaseComparisonOutcome.NotConfigured
        | GitBaseComparisonOutcome.RefUnavailable -> Ok paths
        | GitBaseComparisonOutcome.Committed committedPaths -> Ok(paths @ committedPaths)
        | GitBaseComparisonOutcome.Unavailable failure -> Error failure)
    |> Result.map (fun paths -> paths |> List.distinct |> List.sortWith (fun left right -> String.CompareOrdinal(left, right)))

let private formatGitFailure (failure: GitFailure) = $"{failure.Operation} unavailable: {failure.Message}"

let private runWorkContextPlan root arguments =
    let action = optionValue "--action" arguments |> Option.bind parseWorkAction
    let contextPath = optionValue "--context" arguments
    let occurredAt = optionValue "--occurred-at" arguments
    let providedEvidence = optionValues "--evidence" arguments |> List.map parseEvidence

    match action, contextPath, occurredAt with
    | Some requested, Some relativeContextPath, Some timestamp when providedEvidence |> List.forall Option.isSome ->
        let fullContextPath = Path.GetFullPath(Path.Combine(root, relativeContextPath))

        match WorkContextPlanContract.parseJson (File.ReadAllText fullContextPath) with
        | Error message ->
            eprintfn "ERROR %s" message
            2
        | Ok context ->
            let explicitObservedGitPaths = optionValues "--observed-git-path" arguments
            let explicitMeaningfulChangedPaths = optionValues "--path" arguments
            let autoMode = explicitObservedGitPaths.IsEmpty && explicitMeaningfulChangedPaths.IsEmpty

            // Mirrors production's own gate on calling `gitPaths` at all:
            // only completion and a repository's first `begin` observe Git.
            let shouldObserveGit =
                requested = WorkAction.Complete
                || (requested = WorkAction.Begin && context.StartedAt.IsNone)

            let observedGitPathsResult =
                if not autoMode then Ok explicitObservedGitPaths
                elif shouldObserveGit then realObservedGitPaths root
                else Ok []

            match observedGitPathsResult with
            | Error failure ->
                eprintfn "ERROR %s" (formatGitFailure failure)
                1
            | Ok observedGitPaths ->
                let meaningfulChangedPaths =
                    if not autoMode then
                        explicitMeaningfulChangedPaths
                    elif shouldObserveGit then
                        PathFilter.meaningfulPaths (FileWorkConfigRepository.readPathFilterConfig root) observedGitPaths
                    else
                        []

                let request =
                    { Context = context
                      Action = requested
                      WorkItemIds = optionValues "--id" arguments
                      NewItemType = optionValue "--type" arguments |> Option.defaultValue "task"
                      TargetLocalState = optionValue "--target-local-state" arguments |> Option.defaultValue (defaultLocalState requested)
                      BlockReason = optionValue "--reason" arguments
                      DefaultRequiredEvidence = optionValues "--required" arguments |> Set.ofList
                      RequiredEvidenceByType = Map.empty
                      ProvidedEvidence = providedEvidence |> List.choose id
                      Repository = optionValue "--repository" arguments |> Option.defaultValue "repository"
                      ProtocolVersion = optionValue "--protocol-version" arguments |> Option.defaultValue "1.0.0"
                      Actor = optionValue "--actor" arguments |> Option.defaultValue "unknown"
                      OccurredAt = timestamp
                      MeaningfulChangedPaths = meaningfulChangedPaths
                      ObservedGitPaths = observedGitPaths
                      TelemetryEnabled = arguments |> List.contains "--telemetry-enabled" }

                if arguments |> List.contains "--verify-evidence" then
                    let outcome = WorkOperations.planVerifiedContext (FileEvidenceRepository.create root) request
                    printf "%s" (WorkContextPlanContract.renderVerifiedJson outcome)

                    match outcome with
                    | VerifiedWorkContextPlanOutcome.Planned _ -> 0
                    | VerifiedWorkContextPlanOutcome.ContextRejected _
                    | VerifiedWorkContextPlanOutcome.EvidenceRejected _ -> 1
                else
                    let outcome = WorkOperations.planContext request
                    printf "%s" (WorkContextPlanContract.renderJson outcome)

                    match outcome with
                    | WorkContextPlanOutcome.Planned _ -> 0
                    | WorkContextPlanOutcome.Rejected _ -> 1
    | _ ->
        eprintfn "ERROR work context-plan requires valid --context, --id, --action, --occurred-at, and TYPE=PATH evidence"
        2

let private runBacklogDecision arguments =
    let state = optionValue "--state" arguments |> Option.bind parseBacklogState
    let action = optionValue "--action" arguments |> Option.bind parseBacklogAction

    match state, action with
    | Some current, Some requested ->
        let outcome =
            WorkOperations.decideBacklogTransition
                { State = current
                  Action = requested
                  Reason = optionValue "--reason" arguments }

        printf "%s" (BacklogContract.renderDecisionJson outcome)

        match outcome with
        | BacklogTransitionDecision.Allowed _ -> 0
        | BacklogTransitionDecision.Rejected _ -> 1
    | _ ->
        eprintfn "ERROR work backlog-decide requires valid --state and --action"
        2

let private parseQueueState (value: string) =
    match value.Split('=', 2) with
    | [| workItemId; state |] -> parseBacklogState state |> Option.map (fun parsed -> workItemId, parsed)
    | _ -> None

let private runBacklogPromotionPlan arguments =
    let states = optionValues "--queue-state" arguments |> List.map parseQueueState

    if states |> List.forall Option.isSome then
        let outcome =
            WorkOperations.planBacklogPromotion
                { WorkItemIds = optionValues "--id" arguments
                  QueueStates = states |> List.choose id |> Map.ofList
                  WorkType = optionValue "--type" arguments |> Option.defaultValue "task" }

        printf "%s" (BacklogContract.renderPromotionJson outcome)

        match outcome with
        | BacklogPromotionOutcome.Planned _ -> 0
        | BacklogPromotionOutcome.Rejected _ -> 1
    else
        eprintfn "ERROR --queue-state requires ID=STATE with a valid backlog state"
        2

let private readWorkContext root : Result<WorkContextPlanningView, string> =
    let path = Path.Combine(root, ".ros", "context", "current.json")

    if not (File.Exists path) then
        Ok { WorkItems = []; StartedAt = None; BaselineDirtyPaths = [] }
    else
        WorkContextPlanContract.parseJson (File.ReadAllText path)

/// Mirrors production `workFindings` (`tools/ros_cli.mjs`): when attribution
/// enforcement is off, no Git observation is ever attempted. Otherwise a
/// broken Git repository yields a single synthetic finding rather than
/// failing outright, matching Node's `error.gitFailure` catch.
let private runWorkAttributionValidate root arguments =
    if not (arguments |> List.forall ((=) "--json")) then
        eprintfn "%s" usage
        2
    else
        let enforce = FileWorkConfigRepository.readEnforceAttribution root

        if not enforce then
            printf "%s" (WorkAttributionContract.renderJson [])
            0
        else
            match readWorkContext root with
            | Error message ->
                eprintfn "ERROR %s" message
                2
            | Ok context ->
                match realObservedGitPaths root with
                | Error failure ->
                    let finding =
                        { Path = ".git"
                          Field = "work_items"
                          Message =
                            $"cannot verify work attribution because {failure.Operation} is unavailable: {GitUnavailableReason.code failure.Reason}" }

                    printf "%s" (WorkAttributionContract.renderJson [ finding ])
                    1
                | Ok observedGitPaths ->
                    let request =
                        { Enforce = true
                          ObservedGitPaths = observedGitPaths
                          PathFilterConfig = FileWorkConfigRepository.readPathFilterConfig root
                          BaselineDirtyPaths = context.BaselineDirtyPaths
                          AttributedPaths = FileEventLogRepository.readAttributedPaths root
                          HasActiveOrBlockedWork =
                            context.WorkItems
                            |> List.exists (fun item ->
                                item.SemanticState = LiveWorkState.Active || item.SemanticState = LiveWorkState.Blocked) }

                    let findings = WorkAttribution.findings request
                    printf "%s" (WorkAttributionContract.renderJson findings)
                    if findings.IsEmpty then 0 else 1

/// Shared by `work backlog-transition` and `work block` (whose backlog-item
/// branch is the same effect): mirrors production `backlogTransitionUnlocked`
/// for one id, without acquiring or releasing any lock itself -- the caller
/// already holds `work-protocol` across the whole operation, exactly like
/// production's own `withWorkProtocol`-wrapped callers never re-acquire it
/// per id.
let private applyBacklogTransition
    root
    (id: string)
    (action: BacklogAction)
    (actionText: string)
    (reason: string option)
    (timestamp: string)
    (contextItems: LiveWorkItem list)
    : Result<BacklogQueueRow, string> =
    match FileBacklogQueueRepository.readItems root |> List.tryFind (fun item -> item.Id = id) with
    | None -> Error $"'{id}' is not a captured local work item"
    | Some item ->
        let illegalTransition () = Error $"cannot {actionText} backlog item '{id}' from '{item.Status}'"

        match parseBacklogState item.Status with
        | None -> illegalTransition ()
        | Some currentState ->
            match WorkOperations.decideBacklogTransition { State = currentState; Action = action; Reason = reason } with
            | BacklogTransitionDecision.Rejected(BacklogTransitionRejection.IllegalTransition _) -> illegalTransition ()
            | BacklogTransitionDecision.Rejected BacklogTransitionRejection.BlockReasonRequired -> Error "block requires --reason"
            | BacklogTransitionDecision.Allowed BacklogTransitionEffect.PromoteToLiveWork ->
                Error "this command does not support 'start'; use production './ros work start'"
            | BacklogTransitionDecision.Allowed(BacklogTransitionEffect.ChangeState(newState, blockedChange, abandonedChange)) ->
                FileBacklogQueueRepository.applyStateChange root id newState blockedChange abandonedChange timestamp contextItems

/// Mirrors production `backlogTransition`/`backlogTransitionUnlocked`
/// (`tools/ros_cli.mjs`): a real effect on `.ros/work/queue.json` and
/// `.ros/work/queue.md`, guarded by the same "work-protocol" file lock and
/// backlog-state recovery journal production's own writer uses. Only the
/// three backlog-only actions (`ready`/`block`/`abandon`) are supported --
/// `start` promotes a backlog item into live work and is production's own
/// `startWork`, a materially larger effect (live context + telemetry) this
/// slice deliberately excludes.
let private runBacklogTransitionEffect root arguments =
    let workItemId = optionValue "--id" arguments
    let rawAction = optionValue "--action" arguments
    let occurredAt = optionValue "--occurred-at" arguments
    let reason = optionValue "--reason" arguments

    match workItemId, rawAction, rawAction |> Option.bind parseBacklogAction, occurredAt with
    | Some id, Some actionText, Some action, Some timestamp ->
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "ERROR %s" failure.Message
            1
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match readWorkContext root with
                            | Error message -> Error message
                            | Ok context -> applyBacklogTransition root id action actionText reason timestamp context.WorkItems
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ ->
                eprintfn "ERROR %s" releaseFailure.Message
                1
            | _, Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(), Ok row ->
                printf "%s" (BacklogTransitionEffectContract.renderJson row)
                0
    | _ ->
        eprintfn "ERROR work backlog-transition requires valid --id, --action, and --occurred-at"
        2

/// Mirrors production `captureWorkUnlocked`/`nextQueueId`
/// (`tools/ros_cli.mjs`) via `Ros.Domain.Work.WorkCapture`, excluding
/// `--file` attachment (a separate, larger effect). A real effect,
/// guarded by the same "work-protocol" lock and `backlog-state` recovery
/// journal as `work backlog-transition`.
let private runWorkCapture root arguments =
    let title = optionValue "--title" arguments
    let occurredAt = optionValue "--occurred-at" arguments

    match title, occurredAt with
    | Some titleText, Some timestamp ->
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "ERROR %s" failure.Message
            1
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match readWorkContext root with
                            | Error message -> Error message
                            | Ok context ->
                                let actor =
                                    optionValue "--actor" arguments
                                    |> Option.orElse (Environment.GetEnvironmentVariable "ROS_ACTOR" |> Option.ofObj)
                                    |> Option.defaultValue "unknown"

                                let request: WorkCaptureRequest =
                                    { Title = titleText
                                      ExplicitId = optionValue "--id" arguments
                                      Priority = optionValue "--priority" arguments
                                      Description = optionValue "--description" arguments
                                      Tags = optionValues "--tag" arguments
                                      Actor = actor
                                      Source = optionValue "--source" arguments
                                      SourceReference = optionValue "--source-reference" arguments
                                      ExistingQueueIds =
                                        FileBacklogQueueRepository.readItems root
                                        |> List.map (fun item -> item.Id)
                                        |> Set.ofList
                                      ExistingContextIds = context.WorkItems |> List.map (fun item -> item.Id) |> Set.ofList
                                      NextSeq = FileBacklogQueueRepository.readNextSeq root
                                      OccurredAt = timestamp }

                                match WorkCapture.plan request with
                                | WorkCaptureOutcome.Rejected rejection ->
                                    Error(
                                        match rejection with
                                        | WorkCaptureRejection.EmptyTitle -> "add requires a non-empty title"
                                        | WorkCaptureRejection.InvalidPriority priority ->
                                            $"invalid priority '{priority}'; use high, medium, or low"
                                        | WorkCaptureRejection.InvalidId id -> $"invalid work-item ID '{id}'"
                                        | WorkCaptureRejection.DuplicateInQueue id -> $"work item '{id}' already exists"
                                        | WorkCaptureRejection.DuplicateInContext id ->
                                            $"work item '{id}' already exists in repository context"
                                    )
                                | WorkCaptureOutcome.Planned plan -> FileBacklogQueueRepository.captureItem root plan context.WorkItems
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ ->
                eprintfn "ERROR %s" releaseFailure.Message
                1
            | _, Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(), Ok row ->
                printf "%s" (BacklogTransitionEffectContract.renderJson row)
                0
    | _ ->
        eprintfn "ERROR work capture requires valid --title and --occurred-at"
        2

/// Mirrors production `updateWorkUnlocked`/`findOrCreateQueueEntry`
/// (`tools/ros_cli.mjs`) via `Ros.Domain.Work.WorkUpdate`, excluding
/// `--file` attachment. Real effect, same lock/journal as the other
/// backlog effects. `--tag`'s presence (not its value) decides whether
/// tags change at all, matching production's own `rest.includes("--tag")`
/// gate -- an update with no `--tag` flag never touches existing tags.
let private runWorkUpdate root arguments =
    let id = optionValue "--id" arguments
    let occurredAt = optionValue "--occurred-at" arguments

    match id, occurredAt with
    | Some workItemId, Some timestamp ->
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "ERROR %s" failure.Message
            1
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match readWorkContext root with
                            | Error message -> Error message
                            | Ok context ->
                                let queueContainsId =
                                    FileBacklogQueueRepository.readItems root |> List.exists (fun item -> item.Id = workItemId)

                                let contextContainsId = context.WorkItems |> List.exists (fun item -> item.Id = workItemId)

                                let request: WorkUpdateRequest =
                                    { Id = workItemId
                                      QueueContainsId = queueContainsId
                                      ContextContainsId = contextContainsId
                                      Title = optionValue "--title" arguments
                                      Description = optionValue "--description" arguments
                                      Tags = if arguments |> List.contains "--tag" then Some(optionValues "--tag" arguments) else None
                                      Priority = optionValue "--priority" arguments
                                      OccurredAt = timestamp }

                                match WorkUpdate.plan request with
                                | WorkUpdateOutcome.Rejected rejection ->
                                    Error(
                                        match rejection with
                                        | WorkUpdateRejection.InvalidId id -> $"invalid work-item ID '{id}'"
                                        | WorkUpdateRejection.NotFound id -> $"work item '{id}' was not found"
                                        | WorkUpdateRejection.EmptyTitle -> "title cannot be empty"
                                        | WorkUpdateRejection.InvalidPriority priority ->
                                            $"invalid priority '{priority}'; use high, medium, or low"
                                    )
                                | WorkUpdateOutcome.Planned plan ->
                                    FileBacklogQueueRepository.applyUpdate root workItemId plan context.WorkItems
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ ->
                eprintfn "ERROR %s" releaseFailure.Message
                1
            | _, Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(), Ok row ->
                printf "%s" (BacklogTransitionEffectContract.renderJson row)
                0
    | _ ->
        eprintfn "ERROR work update requires valid --id and --occurred-at"
        2

/// Mirrors production `fileOptions`' exact linear scan and guard: each
/// `--file` occurrence must be immediately followed by a value that is not
/// itself another flag, split on the first `=` into `PATH` and an optional
/// `NAME`.
let private parseFileArguments (arguments: string list) : Result<(string * string option) list, string> =
    let rec loop remaining acc =
        match remaining with
        | "--file" :: value :: rest when not (value.StartsWith "--") ->
            let separator = value.IndexOf '='

            let entry =
                if separator > 0 then
                    value.Substring(0, separator), Some(value.Substring(separator + 1))
                else
                    value, None

            loop rest (entry :: acc)
        | "--file" :: _ -> Error "--file requires PATH or PATH=NAME"
        | _ :: rest -> loop rest acc
        | [] -> Ok(List.rev acc)

    loop arguments []

/// Mirrors production `attachFileUnlocked`/`findOrCreateQueueEntry`
/// (`tools/ros_cli.mjs`) via `Ros.Domain.Work.WorkAttachment`. Production's
/// own `work attach` calls the fully-locked `attachFile` once per file in
/// its own loop -- not once for the whole batch -- so this attaches
/// exactly one file per lock acquisition too, matching that same
/// per-file commit granularity.
let private attachOneFile root (id: string) (sourcePath: string) (nameOverride: string option) (occurredAt: string) : Result<BacklogQueueRow, string> =
    match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
    | Error failure -> Error failure.Message
    | Ok lease ->
        let result =
            try
                match WorkStateTransaction.recover root with
                | Error failure -> Error failure.Message
                | Ok() ->
                    match BacklogStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match readWorkContext root with
                        | Error message -> Error message
                        | Ok context ->
                            try
                                let resolvedPath = Path.GetFullPath(Path.Combine(root, sourcePath))
                                let fileBytes = File.ReadAllBytes resolvedPath

                                let displayName =
                                    let candidate = nameOverride |> Option.defaultValue (Path.GetFileName sourcePath)
                                    let trimmed = candidate.Trim()
                                    if trimmed = "" then "file" else trimmed

                                let queueContainsId = FileBacklogQueueRepository.readItems root |> List.exists (fun item -> item.Id = id)
                                let contextContainsId = context.WorkItems |> List.exists (fun item -> item.Id = id)

                                let request: WorkAttachmentRequest =
                                    { Id = id
                                      QueueContainsId = queueContainsId
                                      ContextContainsId = contextContainsId
                                      DisplayName = displayName
                                      Size = int64 fileBytes.Length
                                      ExistingAttachmentSequences = FileBacklogQueueRepository.readAttachmentSequences root id
                                      OccurredAt = occurredAt }

                                match WorkAttachment.plan request with
                                | WorkAttachmentOutcome.Rejected rejection ->
                                    Error(
                                        match rejection with
                                        | WorkAttachmentRejection.InvalidId id -> $"invalid work-item ID '{id}'"
                                        | WorkAttachmentRejection.NotFound id -> $"work item '{id}' was not found"
                                    )
                                | WorkAttachmentOutcome.Planned plan ->
                                    FileBacklogQueueRepository.applyAttachment root id plan fileBytes context.WorkItems
                            with error ->
                                Error error.Message
            with error ->
                lease.Release() |> ignore
                reraise ()

        match lease.Release(), result with
        | Error releaseFailure, Ok _ -> Error releaseFailure.Message
        | _, Error message -> Error message
        | Ok(), Ok row -> Ok row

let private runWorkAttach root arguments =
    let id = optionValue "--id" arguments
    let occurredAt = optionValue "--occurred-at" arguments

    match id, occurredAt, parseFileArguments arguments with
    | Some workItemId, Some timestamp, Ok((_ :: _) as files) ->
        let rec attachAll remaining =
            match remaining with
            | [ (sourcePath, nameOverride) ] -> attachOneFile root workItemId sourcePath nameOverride timestamp
            | (sourcePath, nameOverride) :: rest ->
                match attachOneFile root workItemId sourcePath nameOverride timestamp with
                | Error message -> Error message
                | Ok _ -> attachAll rest
            | [] -> Error "work attach requires at least one --file PATH[=NAME]"

        match attachAll files with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok row ->
            printf "%s" (BacklogTransitionEffectContract.renderJson row)
            0
    | _, _, Error message ->
        eprintfn "ERROR %s" message
        2
    | _ ->
        eprintfn "ERROR work attach requires valid --id, --occurred-at, and at least one --file PATH[=NAME]"
        2

let private workActionCode (action: WorkAction) =
    match action with
    | WorkAction.Begin -> "begin"
    | WorkAction.Block -> "block"
    | WorkAction.Resume -> "resume"
    | WorkAction.Complete -> "complete"

let private workStateCode (state: LiveWorkState) =
    match state with
    | LiveWorkState.Ready -> "ready"
    | LiveWorkState.Active -> "active"
    | LiveWorkState.Blocked -> "blocked"
    | LiveWorkState.Complete -> "complete"

let private workContextRejectionMessage (rejection: WorkContextRejection) =
    match rejection with
    | WorkContextRejection.NoWorkItems -> "start requires at least one work-item ID"
    | WorkContextRejection.InvalidWorkItemId id -> $"invalid work-item ID '{id}'"
    | WorkContextRejection.WorkItemNotInContext id -> $"work item '{id}' is not in repository context"
    | WorkContextRejection.ItemTransitionRejected(id, transitionRejection) ->
        match transitionRejection with
        | TransitionRejection.IllegalTransition(state, action) ->
            $"cannot {workActionCode action} '{id}' from '{workStateCode state}'"
        | TransitionRejection.BlockReasonRequired -> "block requires --reason"
        | TransitionRejection.MissingEvidence missing -> $"""completion evidence missing for '{id}': {String.Join(", ", missing)}"""

let private telemetryRejectionMessage (workItemId: string) (rejection: TelemetryResolutionRejection) =
    match rejection with
    | TelemetryResolutionRejection.DetachedConflict _ ->
        $"a detached telemetry execution must be linked before creating a new one for '{workItemId}', which this command does not yet support selecting"
    | TelemetryResolutionRejection.Ambiguous ids ->
        $"""multiple detached telemetry executions require explicit selection for '{workItemId}' ({String.Join(", ", ids)}), which this command does not yet support"""

/// Mirrors production's own `fs.existsSync` check on every provided
/// evidence path (`transitionUnlocked`'s `complete` branch): both
/// `EvidenceIssue` cases surface identical text, since Node's
/// `existsSync` swallows every underlying error (permission denied
/// included) and reports a plain missing path either way.
let private evidenceIssueMessage (issue: EvidenceIssue) =
    match issue with
    | EvidenceIssue.Missing evidence -> $"evidence path does not exist: {evidence.Path}"
    | EvidenceIssue.Unavailable(evidence, _) -> $"evidence path does not exist: {evidence.Path}"

/// Shared by every live-work transition effect (`work start`, `work
/// resume`, ...): resolves a plan's telemetry against currently observable
/// execution files, and whenever resolution halts on `PendingNewExecution`,
/// creates the missing execution (production's own `startExecution`) and
/// re-resolves against freshly re-observed candidates -- a real effect
/// boundary re-reading disk state, not a second pure pass. Bounded to one
/// creation attempt per requested work item so a persistent failure cannot
/// loop forever. `parentExecutionIdFor` supplies `CreateExecutionRequest.
/// ParentExecutionId` per work item: `work resume`'s rare
/// no-active-candidate path supplies the work item's most recently
/// created execution; every other action passes `fun _ -> None` (see
/// `CreateExecutionRequest.ParentExecutionId` for why this corrects,
/// rather than replicates, production's own behavior here).
let private resolveContextTelemetryWithCreation
    root
    (classifications: string list)
    (parentExecutionIdFor: string -> string option)
    attemptsLeft
    (candidatePlan: WorkContextPlan)
    =
    let rec resolve attemptsLeft (candidatePlan: WorkContextPlan) =
        let telemetryRepository: TelemetryStateRepository =
            { Observe =
                fun observedWorkItemId ->
                    { LinkedExecutionIds =
                        candidatePlan.WorkItems
                        |> List.tryFind (fun item -> item.Id = observedWorkItemId)
                        |> Option.map _.TelemetryExecutionIds
                        |> Option.defaultValue []
                      Candidates = FileTelemetryStateRepository.readCandidates root observedWorkItemId
                      RequestedExecutionId = None } }

        match WorkOperations.resolveContextTelemetry telemetryRepository candidatePlan with
        | ResolvedTelemetryContextOutcome.Resolved resolvedPlan -> Ok resolvedPlan
        | ResolvedTelemetryContextOutcome.Rejected(workItemId, rejection) -> Error(telemetryRejectionMessage workItemId rejection)
        | ResolvedTelemetryContextOutcome.PendingNewExecution workItemId ->
            if attemptsLeft <= 0 then
                Error $"unable to resolve a telemetry execution for '{workItemId}'"
            else
                match candidatePlan.WorkItems |> List.tryFind (fun item -> item.Id = workItemId) with
                | None -> Error $"work item '{workItemId}' vanished during telemetry resolution"
                | Some item ->
                    let createRequest: FileTelemetryExecutionRepository.CreateExecutionRequest =
                        { WorkItemId = workItemId
                          WorkType = item.WorkType
                          Classifications = classifications
                          ClassificationRationale = None
                          ParentExecutionId = parentExecutionIdFor workItemId }

                    match FileTelemetryExecutionRepository.createExecution root createRequest with
                    | Error message -> Error message
                    | Ok _ -> resolve (attemptsLeft - 1) candidatePlan

    resolve attemptsLeft candidatePlan

/// Same `{workItems, events}` shape production's own `work start`/`begin`/
/// `resume`/`complete`/`done` CLI handlers print: the freshly written
/// `workItems` array verbatim (unmodeled fields included) and the eventId
/// of every event this invocation produced.
let private renderWorkTransitionOutput (writtenItems: JsonArray) (eventIds: string list) =
    let output = JsonObject()
    output["workItems"] <- writtenItems.DeepClone()
    let eventsNode = JsonArray()
    eventIds |> List.iter (fun id -> eventsNode.Add(JsonValue.Create id: JsonNode))
    output["events"] <- eventsNode
    output.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2))

/// Mirrors production `startWork`/`transitionUnlocked` (`tools/ros_cli.mjs`)
/// for the `begin` action only -- the real effect behind `./ros work
/// start`. Writes `.ros/context/current.json` and appends `.ros/events/
/// events.jsonl` (`FileWorkContextRepository`) under the shared
/// `work-protocol` lock and `work-state` recovery journal (MIG-05),
/// creating a new telemetry execution record
/// (`FileTelemetryExecutionRepository`, production's own `startExecution`)
/// whenever the frozen decision layer's `TelemetryPlanResolution` halts on
/// `PendingNewExecution`, then re-resolving. Deliberately excludes
/// `resume`/`block`/`complete` (a separate, later increment), explicit
/// `--identity-*`/`--execution-id`/`--conclusion` overrides (identity is
/// discovered purely from the environment), and
/// `recordTelemetryLifecycle`'s within-execution "blocked"/"resumed" event
/// bookkeeping (not reachable from `begin`).
let private runWorkStart root arguments =
    let ids = optionValues "--id" arguments
    let occurredAt = optionValue "--occurred-at" arguments

    match ids, occurredAt with
    | (_ :: _), Some timestamp ->
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "ERROR %s" failure.Message
            1
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            let queueItems = FileBacklogQueueRepository.readItems root

                            let guard =
                                ids
                                |> List.tryPick (fun id ->
                                    match queueItems |> List.tryFind (fun item -> item.Id = id) with
                                    | Some item when item.Status <> "ready" ->
                                        Some(
                                            if item.Status = "abandoned" then
                                                $"cannot start backlog item '{id}': it was abandoned"
                                            else
                                                $"cannot start backlog item '{id}' from '{item.Status}'; mark it ready first"
                                        )
                                    | _ -> None)

                            match guard with
                            | Some message -> Error message
                            | None ->
                                match readWorkContext root with
                                | Error message -> Error message
                                | Ok context ->
                                    let repositoryId = FileWorkConfigRepository.readRepositoryId root

                                    let observedGitPathsResult =
                                        if context.StartedAt.IsNone then realObservedGitPaths root else Ok []

                                    match observedGitPathsResult with
                                    | Error failure -> Error(formatGitFailure failure)
                                    | Ok observedGitPaths ->
                                        let actor =
                                            optionValue "--actor" arguments
                                            |> Option.orElse (Environment.GetEnvironmentVariable "ROS_ACTOR" |> Option.ofObj)
                                            |> Option.orElse (FileWorkContextRepository.readExistingActor root)
                                            |> Option.defaultValue "unknown"

                                        let classifications = optionValues "--classification" arguments

                                        let request: WorkContextPlanRequest =
                                            { Context = context
                                              Action = WorkAction.Begin
                                              WorkItemIds = ids
                                              NewItemType = optionValue "--type" arguments |> Option.defaultValue "task"
                                              TargetLocalState = defaultLocalState WorkAction.Begin
                                              BlockReason = None
                                              DefaultRequiredEvidence = Set.empty
                                              RequiredEvidenceByType = Map.empty
                                              ProvidedEvidence = []
                                              Repository = repositoryId
                                              ProtocolVersion = FileWorkConfigRepository.readProtocolVersion root
                                              Actor = actor
                                              OccurredAt = timestamp
                                              MeaningfulChangedPaths = []
                                              ObservedGitPaths = observedGitPaths
                                              TelemetryEnabled = FileWorkConfigRepository.readTelemetryEnabled root }

                                        match WorkContextPlanning.plan request with
                                        | WorkContextPlanOutcome.Rejected rejection -> Error(workContextRejectionMessage rejection)
                                        | WorkContextPlanOutcome.Planned plan ->
                                            match resolveContextTelemetryWithCreation root classifications (fun _ -> None) (ids.Length + 1) plan with
                                            | Error message -> Error message
                                            | Ok resolvedPlan -> FileWorkContextRepository.applyContextPlan root repositoryId resolvedPlan
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ ->
                eprintfn "ERROR %s" releaseFailure.Message
                1
            | _, Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(), Ok(writtenItems, eventIds) ->
                printf "%s" (renderWorkTransitionOutput writtenItems eventIds)
                0
    | [], _ ->
        eprintfn "ERROR start requires at least one work-item ID"
        2
    | _, None ->
        eprintfn "ERROR work start requires valid --id and --occurred-at"
        2

/// Mirrors production `transition(root, "resume", ids, options)`
/// (`tools/ros_cli.mjs`) -- the effect behind `./ros work resume`. Live-work
/// only: unlike `begin`, `resume` never creates a new context item (an id
/// absent from context is rejected) and never observes Git. Reuses `work
/// start`'s effect infrastructure directly (`WorkContextPlanning.plan`,
/// `resolveContextTelemetryWithCreation`, `FileWorkContextRepository.
/// applyContextPlan`). Records `recordTelemetryLifecycle`'s "resumed"
/// bookkeeping (`FileTelemetryFinalizationRepository.recordLifecycle`) on
/// every currently-active execution BEFORE telemetry resolution runs,
/// matching production's own ordering: a brand-new execution `resume`
/// itself creates (when none was active) never receives this "resumed"
/// event, since it did not exist yet when this ran. That rare new-execution
/// path links `parentExecutionId` to the work item's most recently created
/// execution (`FileTelemetryQueryRepository.readLatestExecutionId`),
/// corrected here rather than reproduced as-is: production computes this
/// same value (`prior?.executionId ?? null`) but its own
/// `discoverIdentity(options.identity ?? options)` call discards it, since
/// `options.identity` is always a truthy object (even with every field
/// `undefined`) that wins the `??` over the sibling `options` object
/// `parentExecutionId` was actually set on -- confirmed against real Node,
/// which always writes `null` here. Node is being deprecated rather than
/// patched for this, so this port implements the evidently-intended
/// behavior instead of the bug. Deliberately still excludes explicit
/// `--identity-*`/`--execution-id` overrides (no CLI exposes them for
/// `resume`).
let private runWorkResume root arguments =
    let ids = optionValues "--id" arguments
    let occurredAt = optionValue "--occurred-at" arguments

    match ids, occurredAt with
    | (_ :: _), Some timestamp ->
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "ERROR %s" failure.Message
            1
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match readWorkContext root with
                            | Error message -> Error message
                            | Ok context ->
                                let repositoryId = FileWorkConfigRepository.readRepositoryId root

                                let actor =
                                    optionValue "--actor" arguments
                                    |> Option.orElse (Environment.GetEnvironmentVariable "ROS_ACTOR" |> Option.ofObj)
                                    |> Option.orElse (FileWorkContextRepository.readExistingActor root)
                                    |> Option.defaultValue "unknown"

                                let request: WorkContextPlanRequest =
                                    { Context = context
                                      Action = WorkAction.Resume
                                      WorkItemIds = ids
                                      NewItemType = "task"
                                      TargetLocalState = defaultLocalState WorkAction.Resume
                                      BlockReason = None
                                      DefaultRequiredEvidence = Set.empty
                                      RequiredEvidenceByType = Map.empty
                                      ProvidedEvidence = []
                                      Repository = repositoryId
                                      ProtocolVersion = FileWorkConfigRepository.readProtocolVersion root
                                      Actor = actor
                                      OccurredAt = timestamp
                                      MeaningfulChangedPaths = []
                                      ObservedGitPaths = []
                                      TelemetryEnabled = FileWorkConfigRepository.readTelemetryEnabled root }

                                match WorkContextPlanning.plan request with
                                | WorkContextPlanOutcome.Rejected rejection -> Error(workContextRejectionMessage rejection)
                                | WorkContextPlanOutcome.Planned plan ->
                                    let lifecycleResult =
                                        if request.TelemetryEnabled then
                                            ids
                                            |> List.fold
                                                (fun acc id ->
                                                    match acc with
                                                    | Error _ -> acc
                                                    | Ok() -> FileTelemetryFinalizationRepository.recordLifecycle root id "resumed" timestamp None)
                                                (Ok())
                                        else
                                            Ok()

                                    match lifecycleResult with
                                    | Error message -> Error message
                                    | Ok() ->
                                        match
                                            resolveContextTelemetryWithCreation
                                                root
                                                []
                                                (FileTelemetryQueryRepository.readLatestExecutionId root)
                                                (ids.Length + 1)
                                                plan
                                        with
                                        | Error message -> Error message
                                        | Ok resolvedPlan -> FileWorkContextRepository.applyContextPlan root repositoryId resolvedPlan
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ ->
                eprintfn "ERROR %s" releaseFailure.Message
                1
            | _, Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(), Ok(writtenItems, eventIds) ->
                printf "%s" (renderWorkTransitionOutput writtenItems eventIds)
                0
    | [], _ ->
        eprintfn "ERROR resume requires at least one work-item ID"
        2
    | _, None ->
        eprintfn "ERROR work resume requires valid --id and --occurred-at"
        2

/// Mirrors production `transition(root, "complete", ids, options)`
/// (`tools/ros_cli.mjs`) -- the effect behind `./ros work complete`.
/// Live-work only, like `resume`: an id absent from context is rejected.
/// Git is always observed (production's own `observedGitPaths` gate is
/// `action === "complete" || ...`), and required completion evidence
/// comes from `ros.json`'s `workProtocol.completionEvidence`
/// (`FileWorkConfigRepository.readCompletionEvidence`), verified against
/// the real filesystem via `WorkOperations.planVerifiedContext`/
/// `FileEvidenceRepository` -- matching production's own `fs.existsSync`
/// check on every provided evidence path, not just the required evidence
/// *types* the frozen decision layer already rejects on. When telemetry
/// is enabled, finalizes every currently active telemetry execution for
/// each completing id (`FileTelemetryFinalizationRepository.
/// finalizeWorkExecutions`, production's own unconditional
/// `finalizeWorkExecutions` call) before committing -- the shared
/// `resolveContextTelemetryWithCreation`/`TelemetryPlanResolution`
/// pipeline discards the `FinalizeExecutions` intent signal, so this is
/// called directly rather than threaded through it. A research-type
/// item's `--conclusion` (defaulting to `"inconclusive"`, matching
/// production) is written via `FileWorkContextRepository.
/// applyContextPlanWithConclusions`. Deliberately excludes
/// `options.input`/adapter-ingestion (unreachable from any CLI path) and
/// explicit `--identity-*`/`--execution-id` overrides, matching every
/// prior increment.
let private runWorkComplete root arguments =
    let ids = optionValues "--id" arguments
    let occurredAt = optionValue "--occurred-at" arguments
    let providedEvidence = optionValues "--evidence" arguments |> List.map parseEvidence

    match ids, occurredAt with
    | (_ :: _), Some timestamp when providedEvidence |> List.forall Option.isSome ->
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "ERROR %s" failure.Message
            1
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match readWorkContext root with
                            | Error message -> Error message
                            | Ok context ->
                                let repositoryId = FileWorkConfigRepository.readRepositoryId root

                                match realObservedGitPaths root with
                                | Error failure -> Error(formatGitFailure failure)
                                | Ok observedGitPaths ->
                                    let meaningfulChangedPaths =
                                        PathFilter.meaningfulPaths (FileWorkConfigRepository.readPathFilterConfig root) observedGitPaths

                                    let actor =
                                        optionValue "--actor" arguments
                                        |> Option.orElse (Environment.GetEnvironmentVariable "ROS_ACTOR" |> Option.ofObj)
                                        |> Option.orElse (FileWorkContextRepository.readExistingActor root)
                                        |> Option.defaultValue "unknown"

                                    let defaultRequiredEvidence, requiredEvidenceByType =
                                        FileWorkConfigRepository.readCompletionEvidence root

                                    let telemetryEnabled = FileWorkConfigRepository.readTelemetryEnabled root

                                    let request: WorkContextPlanRequest =
                                        { Context = context
                                          Action = WorkAction.Complete
                                          WorkItemIds = ids
                                          NewItemType = "task"
                                          TargetLocalState = defaultLocalState WorkAction.Complete
                                          BlockReason = None
                                          DefaultRequiredEvidence = defaultRequiredEvidence
                                          RequiredEvidenceByType = requiredEvidenceByType
                                          ProvidedEvidence = providedEvidence |> List.choose id
                                          Repository = repositoryId
                                          ProtocolVersion = FileWorkConfigRepository.readProtocolVersion root
                                          Actor = actor
                                          OccurredAt = timestamp
                                          MeaningfulChangedPaths = meaningfulChangedPaths
                                          ObservedGitPaths = observedGitPaths
                                          TelemetryEnabled = telemetryEnabled }

                                    match WorkOperations.planVerifiedContext (FileEvidenceRepository.create root) request with
                                    | VerifiedWorkContextPlanOutcome.ContextRejected rejection -> Error(workContextRejectionMessage rejection)
                                    | VerifiedWorkContextPlanOutcome.EvidenceRejected issues ->
                                        match issues with
                                        | issue :: _ -> Error(evidenceIssueMessage issue)
                                        | [] -> Error "evidence rejected"
                                    | VerifiedWorkContextPlanOutcome.Planned plan ->
                                        match resolveContextTelemetryWithCreation root [] (fun _ -> None) (ids.Length + 1) plan with
                                        | Error message -> Error message
                                        | Ok resolvedPlan ->
                                            let finalizeResult =
                                                if telemetryEnabled then
                                                    ids
                                                    |> List.fold
                                                        (fun acc workItemId ->
                                                            match acc with
                                                            | Error _ -> acc
                                                            | Ok() -> FileTelemetryFinalizationRepository.finalizeWorkExecutions root workItemId)
                                                        (Ok())
                                                else
                                                    Ok()

                                            match finalizeResult with
                                            | Error message -> Error message
                                            | Ok() ->
                                                let conclusion =
                                                    optionValue "--conclusion" arguments |> Option.defaultValue "inconclusive"

                                                let conclusions =
                                                    resolvedPlan.ItemPlans
                                                    |> List.filter (fun itemPlan -> itemPlan.Item.WorkType = "research")
                                                    |> List.map (fun itemPlan -> itemPlan.Item.Id, conclusion)
                                                    |> Map.ofList

                                                FileWorkContextRepository.applyContextPlanWithConclusions
                                                    root
                                                    repositoryId
                                                    conclusions
                                                    resolvedPlan
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ ->
                eprintfn "ERROR %s" releaseFailure.Message
                1
            | _, Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(), Ok(writtenItems, eventIds) ->
                printf "%s" (renderWorkTransitionOutput writtenItems eventIds)
                0
    | [], _ ->
        eprintfn "ERROR complete requires at least one work-item ID"
        2
    | _ ->
        eprintfn "ERROR work complete requires valid --id, --occurred-at, and TYPE=PATH evidence"
        2

let private readRawQueueItem root (id: string) : JsonObject option =
    let path = Path.Combine(root, ".ros", "work", "queue.json")

    if not (File.Exists path) then
        None
    else
        match JsonNode.Parse(File.ReadAllText path) with
        | :? JsonObject as queue ->
            match queue["items"] with
            | :? JsonArray as items ->
                items
                |> Seq.tryPick (fun node ->
                    match node with
                    | :? JsonObject as candidate ->
                        match candidate["id"] with
                        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String && value.GetValue<string>() = id -> Some candidate
                        | _ -> None
                    | _ -> None)
            | _ -> None
        | _ -> None

/// Mirrors production `blockWork` (`tools/ros_cli.mjs`): a combined effect
/// that splits requested ids between backlog-only items (not yet started)
/// and live-context items, applying the matching real effect to each under
/// ONE held `work-protocol` lock -- production's own `blockWork` never
/// acquires the lock twice either. `--reason` is optional at the argument
/// level, matching production's own CLI: whether it is actually required
/// depends on the item's current state (`block` is illegal from anywhere
/// but `ready`/`active`, and that illegal-transition rejection fires before
/// the missing-reason one ever would), so the check is left to the same
/// decision layer every other real effect uses, not enforced eagerly here.
/// `--occurred-at` is this CLI's own synthetic determinism parameter (as
/// for every other real effect in this migration, none of which
/// production's real CLI actually exposes): production computes its own
/// timestamp per branch. For live-context ids, also records
/// `recordTelemetryLifecycle`'s "blocked" bookkeeping
/// (`FileTelemetryFinalizationRepository.recordLifecycle`) on every
/// currently-active execution before telemetry resolution runs, matching
/// production's own ordering -- backlog-only ids never reach telemetry at
/// all, since a never-started item has no execution to record against.
let private runWorkBlock root arguments =
    let ids = optionValues "--id" arguments
    let reason = optionValue "--reason" arguments
    let occurredAt = optionValue "--occurred-at" arguments

    match ids, occurredAt with
    | (_ :: _), Some timestamp ->
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "ERROR %s" failure.Message
            1
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match readWorkContext root with
                            | Error message -> Error message
                            | Ok context ->
                                let queueItems = FileBacklogQueueRepository.readItems root
                                let contextIdSet = context.WorkItems |> List.map (fun item -> item.Id) |> Set.ofList

                                let backlogIds =
                                    ids
                                    |> List.filter (fun id ->
                                        (queueItems |> List.exists (fun item -> item.Id = id)) && not (contextIdSet.Contains id))

                                let contextIds = ids |> List.filter (fun id -> not (List.contains id backlogIds))

                                let rec applyBacklog remaining acc =
                                    match remaining with
                                    | [] -> Ok(List.rev acc)
                                    | id :: rest ->
                                        match applyBacklogTransition root id BacklogAction.Block "block" reason timestamp context.WorkItems with
                                        | Error message -> Error message
                                        | Ok _ ->
                                            match readRawQueueItem root id with
                                            | None -> Error $"'{id}' vanished during block"
                                            | Some rawItem -> applyBacklog rest (rawItem :: acc)

                                match applyBacklog backlogIds [] with
                                | Error message -> Error message
                                | Ok backlogRows ->
                                    if contextIds.IsEmpty then
                                        Ok(backlogRows, JsonArray())
                                    else
                                        let repositoryId = FileWorkConfigRepository.readRepositoryId root

                                        let actor =
                                            optionValue "--actor" arguments
                                            |> Option.orElse (Environment.GetEnvironmentVariable "ROS_ACTOR" |> Option.ofObj)
                                            |> Option.orElse (FileWorkContextRepository.readExistingActor root)
                                            |> Option.defaultValue "unknown"

                                        let request: WorkContextPlanRequest =
                                            { Context = context
                                              Action = WorkAction.Block
                                              WorkItemIds = contextIds
                                              NewItemType = "task"
                                              TargetLocalState = defaultLocalState WorkAction.Block
                                              BlockReason = reason
                                              DefaultRequiredEvidence = Set.empty
                                              RequiredEvidenceByType = Map.empty
                                              ProvidedEvidence = []
                                              Repository = repositoryId
                                              ProtocolVersion = FileWorkConfigRepository.readProtocolVersion root
                                              Actor = actor
                                              OccurredAt = timestamp
                                              MeaningfulChangedPaths = []
                                              ObservedGitPaths = []
                                              TelemetryEnabled = FileWorkConfigRepository.readTelemetryEnabled root }

                                        match WorkContextPlanning.plan request with
                                        | WorkContextPlanOutcome.Rejected rejection -> Error(workContextRejectionMessage rejection)
                                        | WorkContextPlanOutcome.Planned plan ->
                                            let lifecycleResult =
                                                if request.TelemetryEnabled then
                                                    contextIds
                                                    |> List.fold
                                                        (fun acc id ->
                                                            match acc with
                                                            | Error _ -> acc
                                                            | Ok() -> FileTelemetryFinalizationRepository.recordLifecycle root id "blocked" timestamp reason)
                                                        (Ok())
                                                else
                                                    Ok()

                                            match lifecycleResult with
                                            | Error message -> Error message
                                            | Ok() ->
                                                match resolveContextTelemetryWithCreation root [] (fun _ -> None) (contextIds.Length + 1) plan with
                                                | Error message -> Error message
                                                | Ok resolvedPlan ->
                                                    match FileWorkContextRepository.applyContextPlan root repositoryId resolvedPlan with
                                                    | Error message -> Error message
                                                    | Ok(writtenItems, _) ->
                                                        let contextIdSet = Set.ofList contextIds

                                                        let matching = JsonArray()

                                                        for node in writtenItems do
                                                            match node with
                                                            | :? JsonObject as item ->
                                                                match item["id"] with
                                                                | :? JsonValue as value when
                                                                    value.GetValueKind() = JsonValueKind.String
                                                                    && contextIdSet.Contains(value.GetValue<string>())
                                                                    ->
                                                                    matching.Add(item.DeepClone(): JsonNode)
                                                                | _ -> ()
                                                            | _ -> ()

                                                        Ok(backlogRows, matching)
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ ->
                eprintfn "ERROR %s" releaseFailure.Message
                1
            | _, Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(), Ok(backlogRows, contextItems) ->
                let combined = JsonArray()
                backlogRows |> List.iter (fun item -> combined.Add(item.DeepClone(): JsonNode))
                for item in contextItems do
                    combined.Add(item.DeepClone(): JsonNode)

                printf "%s" (combined.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
                0
    | [], _ ->
        eprintfn "ERROR block requires at least one work-item ID"
        2
    | _, None ->
        eprintfn "ERROR work block requires valid --id and --occurred-at"
        2

/// Mirrors production `queueFindings` (`tools/ros_cli.mjs`): duplicate ids,
/// invalid ids, invalid status, and invalid priority over the raw backlog
/// queue rows in `.ros/work/queue.json`.
let private runBacklogQueueValidate root arguments =
    if not (arguments |> List.forall ((=) "--json")) then
        eprintfn "%s" usage
        2
    else
        let findings = FileBacklogQueueRepository.readItems root |> BacklogQueueValidation.findings
        printf "%s" (BacklogQueueValidationContract.renderJson findings)
        if findings.IsEmpty then 0 else 1

/// Mirrors production `telemetry adapters` (`tools/ros_telemetry.mjs`):
/// prints the static provider-adapter catalog verbatim. No file I/O, no
/// lock -- ingestion itself (mapping each adapter's payload shape into the
/// normalized schema) is a separately-scoped later slice.
let private runTelemetryAdapters () =
    let node = JsonArray()
    TelemetryAdapters.all |> List.iter (fun name -> node.Add(JsonValue.Create name: JsonNode))
    printf "%s" (node.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
    0

/// Mirrors production `telemetry show [TARGET]` (`showTelemetry`,
/// `tools/ros_telemetry.mjs`) -- Phase A/MIG-08's first increment, and the
/// first telemetry-producer command with real F# parity. `TARGET` is
/// positional, not a flag, matching production's own `telemetryTarget(args)`
/// (`args[2]`, undefined when it starts with `--`). No target prints every
/// execution record; an `EXE-`-prefixed target resolves exactly one record
/// or production's exact rejection message; any other target filters by
/// work-item ID, where an empty result is not a rejection (production's own
/// `showTelemetry` never throws for this branch). Read-only: no lock, no
/// write, and no new identity/Git/capability machinery -- it only reads the
/// same `.ros/telemetry/executions/*.json` records `work start`/`work
/// complete` already produce.
let private runTelemetryShow root (arguments: string list) =
    let target =
        arguments
        |> List.tryHead
        |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal)))

    let renderRecords (records: JsonObject list) =
        let node = JsonArray()
        records |> List.iter (fun record -> node.Add(record.DeepClone(): JsonNode))
        printf "%s" (node.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
        0

    match target with
    | None -> renderRecords (FileTelemetryQueryRepository.readAll root)
    | Some value when value.StartsWith("EXE-", StringComparison.Ordinal) ->
        match FileTelemetryQueryRepository.readByExecutionId root value with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok record ->
            printf "%s" (record.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
            0
    | Some workItemId -> renderRecords (FileTelemetryQueryRepository.readByWorkItemId root workItemId)

/// Mirrors production `telemetry summary`/`telemetry summarize [TARGET]`
/// (`summarizeTelemetry`, `tools/ros_telemetry.mjs`) -- MIG-08's third
/// increment, and the largest read-only telemetry command: four real
/// aggregation strategies (`sum`/`maximum`/`latest-per-session`/`none`,
/// falling back to `latest`) plus an interval-sweep timing summary, over
/// every metric recorded across matching execution files. `TARGET` is
/// positional like `telemetry show`, but here it is used purely as a
/// work-item-id filter -- unlike `show`, `summary` never special-cases an
/// `EXE-`-prefixed value, matching production's own `!workItemId ||
/// record.workItemId === workItemId` check exactly (passing an execution id
/// here silently matches nothing, reproducing that real quirk rather than
/// improving on it). Read-only: no lock, no write.
let private runTelemetrySummary root (arguments: string list) =
    let workItemId =
        arguments
        |> List.tryHead
        |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal)))

    let executions = FileTelemetryQueryRepository.readSummaryExecutions root workItemId
    let summary = TelemetrySummary.summarize workItemId executions

    let output = JsonObject()
    output["schemaVersion"] <- JsonValue.Create summary.SchemaVersion

    output["workItemId"] <-
        match summary.WorkItemId with
        | Some id -> JsonValue.Create id
        | None -> null

    output["executionCount"] <- JsonValue.Create summary.ExecutionCount
    let providersNode = JsonArray()
    summary.Providers |> List.iter (fun provider -> providersNode.Add(JsonValue.Create provider: JsonNode))
    output["providers"] <- providersNode
    let runtimesNode = JsonArray()
    summary.Runtimes |> List.iter (fun runtime -> runtimesNode.Add(JsonValue.Create runtime: JsonNode))
    output["runtimes"] <- runtimesNode

    let timing = summary.Timing
    let timingNode = JsonObject()
    timingNode["fullyFinalized"] <- JsonValue.Create timing.FullyFinalized
    timingNode["finalizedExecutionCount"] <- JsonValue.Create timing.FinalizedExecutionCount
    timingNode["activeExecutionCount"] <- JsonValue.Create timing.ActiveExecutionCount

    timingNode["earliestStartedAt"] <-
        match timing.EarliestStartedAt with
        | Some value -> JsonValue.Create value
        | None -> null

    timingNode["latestFinalizedAt"] <-
        match timing.LatestFinalizedAt with
        | Some value -> JsonValue.Create value
        | None -> null

    timingNode["calendarSpanMs"] <-
        match timing.CalendarSpanMs with
        | Some value -> JsonValue.Create value
        | None -> null

    timingNode["totalExecutionWallMs"] <-
        match timing.TotalExecutionWallMs with
        | Some value -> JsonValue.Create value
        | None -> null

    timingNode["overlappingExecutionMs"] <-
        match timing.OverlappingExecutionMs with
        | Some value -> JsonValue.Create value
        | None -> null

    output["timing"] <- timingNode

    let metricsNode = JsonArray()

    summary.Metrics
    |> List.iter (fun metric ->
        let metricNode = JsonObject()
        metricNode["id"] <- JsonValue.Create metric.Id

        metricNode["value"] <-
            match metric.Value with
            | Some value -> JsonValue.Create value
            | None -> null

        metricNode["unit"] <- JsonValue.Create metric.Unit

        metricNode["currency"] <-
            match metric.Currency with
            | Some currency -> JsonValue.Create currency
            | None -> null

        metricNode["dimensions"] <-
            match JsonNode.Parse metric.DimensionsKey with
            | :? JsonObject as dimensions -> dimensions
            | _ -> JsonObject()

        metricNode["aggregation"] <- JsonValue.Create metric.Aggregation
        metricNode["measurements"] <- JsonValue.Create metric.Measurements

        metricNode["note"] <-
            match metric.Note with
            | Some note -> JsonValue.Create note
            | None -> null

        metricsNode.Add(metricNode: JsonNode))

    output["metrics"] <- metricsNode

    printf "%s" (output.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
    0

/// Mirrors production `telemetry finalize [TARGET]` (`finalizeExecution`,
/// `tools/ros_telemetry.mjs`) -- MIG-08's fourth increment, and the first
/// write-path telemetry-producer command. `TARGET` is positional like
/// `telemetry show`/`telemetry summary`, but resolves differently from
/// both: an `EXE-`-prefixed value matches by exact execution id; any other
/// value matches by work-item id regardless of the matching execution's own
/// status (unlike `show`'s work-item branch, an already-finalized match is
/// legal here too); no target at all requires exactly one currently
/// active-or-blocked work item, rejecting production's exact ambiguity
/// message otherwise. An already-finalized resolved execution is returned
/// untouched, with no lock taken at all (matching production's own pre-lock
/// fast return); otherwise it is finalized via the same real effect `work
/// complete` already uses. Deliberately excludes `--input`/adapter-ingestion
/// -- the one place it is reachable in production at all -- a separately
/// scoped later slice; passing `--input` is rejected outright rather than
/// silently ignored.
let private runTelemetryFinalize root (arguments: string list) =
    if arguments |> List.contains "--input" then
        eprintfn "ERROR telemetry finalize --input is not yet supported by this CLI"
        2
    else
        let target = arguments |> List.tryHead |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal)))

        match FileTelemetryFinalizationRepository.finalizeTarget root target with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok record ->
            if not (arguments |> List.contains "--quiet") then
                printf "%s" (record.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))

            0

/// Mirrors production `telemetry record [TARGET] --metric ID --value VALUE`
/// (`recordTelemetryMetric`/`normalizeMetric`/`addMetric`,
/// `tools/ros_telemetry.mjs`) -- MIG-08's fifth increment, and the second
/// write-path telemetry-producer command. Unlike `finalize`, target
/// resolution is `activeOnly`: an execution that is not currently
/// `"active"` (including one finalized between resolution and the lock
/// being acquired, matching production's own re-resolve-under-the-lock
/// race check) is rejected with production's exact `"... was not found or
/// is already finalized"` message. `--confidence` mirrors production's own
/// permissive parsing: a value that parses as a finite number is stored
/// numerically, any other text is stored as-is, and omitting the flag
/// entirely stores `null`. `--quality`/`--scope`/`--source-*` all default
/// exactly as production's CLI does; `--unit`/`--currency`/`--collected-at`
/// are optional overrides of the metric registry's own values. `--value`'s
/// parse failure is deliberately not surfaced here -- it becomes `NaN` and
/// is passed through unvalidated, so the finite-value rejection happens at
/// the same point production's does, after target resolution and lock
/// acquisition, not before.
let private runTelemetryRecord root (arguments: string list) =
    let target = arguments |> List.tryHead |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal)))

    match optionValue "--metric" arguments, optionValue "--value" arguments with
    | None, _
    | _, None ->
        eprintfn "ERROR telemetry record requires --metric and --value"
        1
    | Some metricId, Some rawValue ->
        let value =
            match Double.TryParse(rawValue, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, parsed -> parsed
            | false, _ -> Double.NaN

        let confidence =
            match optionValue "--confidence" arguments with
            | None -> FileTelemetryFinalizationRepository.NoConfidence
            | Some text ->
                match Double.TryParse(text, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                | true, parsed when Double.IsFinite parsed -> FileTelemetryFinalizationRepository.NumericConfidence parsed
                | _ -> FileTelemetryFinalizationRepository.TextConfidence text

        let request: FileTelemetryFinalizationRepository.RecordMetricRequest =
            { MetricId = metricId
              Value = value
              Unit = optionValue "--unit" arguments
              Currency = optionValue "--currency" arguments
              Quality = optionValue "--quality" arguments |> Option.defaultValue "observed"
              Confidence = confidence
              Scope = optionValue "--scope" arguments |> Option.defaultValue "execution"
              Source =
                { Type = optionValue "--source-type" arguments |> Option.defaultValue "agent-report"
                  Name = optionValue "--source-name" arguments |> Option.defaultValue "ros-telemetry-cli"
                  Mechanism = optionValue "--mechanism" arguments |> Option.defaultValue "explicit-metric-record" }
              PricingSource = optionValue "--pricing-source" arguments
              PricingVersion = optionValue "--pricing-version" arguments
              CollectedAt = optionValue "--collected-at" arguments }

        match FileTelemetryFinalizationRepository.recordMetric root target request with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok record ->
            if not (arguments |> List.contains "--quiet") then
                let lastMetric =
                    match record["metrics"] with
                    | :? JsonArray as metrics when metrics.Count > 0 -> Some metrics.[metrics.Count - 1]
                    | _ -> None

                match lastMetric with
                | Some metric -> printf "%s" (metric.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
                | None -> ()

            0

/// Mirrors production `telemetry ingest [TARGET] --input FILE [--adapter
/// NAME]` (`ingestTelemetry`/`adaptInput`/`ingestAdapted`,
/// `tools/ros_telemetry.mjs`) -- MIG-08's sixth increment shipped the
/// `generic` adapter (the one with zero provider-specific field mapping);
/// a later increment added `openai-codex`, the first real provider-specific
/// field mapping ported. Every other adapter name production itself
/// recognizes remains its own future MIG-08 slice and is rejected outright
/// (exit 2) rather than silently treated as generic; a name production
/// itself would not recognize gets production's own exact error (exit 1).
/// `--input`'s file (or `-` for stdin) is read and byte-checked here,
/// matching production's own `readTelemetryInput`; everything past that
/// (adaptation, target resolution, the mutation itself) is
/// `FileTelemetryFinalizationRepository.ingestTarget`.
let private runTelemetryIngest root (arguments: string list) =
    let target = arguments |> List.tryHead |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal)))
    let adapter = optionValue "--adapter" arguments |> Option.defaultValue "generic"

    match optionValue "--input" arguments with
    | None ->
        eprintfn "ERROR --input requires a JSON or JSON Lines file; use '-' for stdin"
        1
    | Some inputPath ->
        try
            let raw = if inputPath = "-" then Console.In.ReadToEnd() else File.ReadAllText(Path.Combine(root, inputPath))
            let maxBytes = FileWorkConfigRepository.readTelemetryMaxRawPayloadBytes root

            if Text.Encoding.UTF8.GetByteCount raw > maxBytes then
                eprintfn "ERROR telemetry input exceeds %d bytes" maxBytes
                1
            else
                match FileTelemetryFinalizationRepository.ingestTarget root target adapter raw with
                | Error message ->
                    eprintfn "ERROR %s" message
                    if message.EndsWith("is not yet supported by this CLI", StringComparison.Ordinal) then 2 else 1
                | Ok record ->
                    if not (arguments |> List.contains "--quiet") then
                        printf "%s" (record.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))

                    0
        with :? IOException as error ->
            eprintfn "ERROR %s" error.Message
            1

/// Mirrors production `telemetry classify --classification NAME [...]
/// [--rationale TEXT] [--evidence-link LINK]* [--rd-context FILE]`
/// (`tools/ros_cli.mjs`) -- MIG-08's seventh increment, a thin wrapper over
/// the same generic-adapter ingest `telemetry ingest` already reuses: a
/// synthetic ingest whose `snapshotId` is `classification-{now-ms}` (a
/// real-clock timestamp, not a content digest, so a repeated call is its
/// own new event rather than deduplicated) and whose only real content is
/// the constructed `classification` object plus an explicitly empty
/// `raw: {}`. `--rd-context`'s file (or `-` for stdin) is read and parsed
/// exactly like `telemetry ingest`'s own `--input` (JSON or JSON Lines,
/// byte-checked against the same configured limit); checking for at least
/// one `--classification` happens before that read, matching production's
/// own check order exactly.
let private runTelemetryClassify root (arguments: string list) =
    let target = arguments |> List.tryHead |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal)))
    let classifications = optionValues "--classification" arguments

    if classifications.IsEmpty then
        eprintfn "ERROR telemetry classify requires at least one --classification"
        1
    else
        let rationale = optionValue "--rationale" arguments
        let evidenceLinks = optionValues "--evidence-link" arguments

        let rdResult =
            match optionValue "--rd-context" arguments with
            | None -> Ok None
            | Some rdPath ->
                try
                    let raw = if rdPath = "-" then Console.In.ReadToEnd() else File.ReadAllText(Path.Combine(root, rdPath))
                    let maxBytes = FileWorkConfigRepository.readTelemetryMaxRawPayloadBytes root

                    if Text.Encoding.UTF8.GetByteCount raw > maxBytes then
                        Error $"telemetry input exceeds {maxBytes} bytes"
                    else
                        FileTelemetryFinalizationRepository.parseIngestInput raw |> Result.map Some
                with :? IOException as error ->
                    Error error.Message

        match rdResult with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok rd ->
            match FileTelemetryFinalizationRepository.classifyTarget root target classifications rationale evidenceLinks rd with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok classification ->
                if not (arguments |> List.contains "--quiet") then
                    printf "%s" (classification.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))

                0

/// Mirrors production `telemetry start WORKITEMID [--classification NAME]*
/// [--classification-rationale TEXT] [--quiet]` (`tools/ros_cli.mjs`) --
/// MIG-08's eighth increment: manually ensures a currently `active`/
/// `blocked` work item has a linked telemetry execution, recovering a
/// detached one or creating a new one, with no state transition of its
/// own (unlike `work start`/`resume`). Deliberately excludes `--execution-
/// id` and every identity-override flag (`--provider`/`--model`/
/// `--model-version`/`--runtime`/`--runtime-version`/`--session`/
/// `--conversation`/`--run`/`--agent`/`--subagent`/`--parent-execution`)
/// production's own CLI exposes here -- the one command that does --
/// rejected outright (exit 2) rather than silently ignored, since
/// supporting them means extending `createExecution` itself, a separately
/// scoped future slice. Prints the literal JSON `null` when telemetry is
/// disabled and no candidate execution exists, matching production's own
/// `console.log(JSON.stringify(null, null, 2))`.
let private runTelemetryStart root (arguments: string list) =
    let unsupportedFlags =
        [ "--execution-id"
          "--provider"
          "--model"
          "--model-version"
          "--runtime"
          "--runtime-version"
          "--session"
          "--conversation"
          "--run"
          "--agent"
          "--subagent"
          "--parent-execution" ]

    match unsupportedFlags |> List.tryFind (fun flag -> arguments |> List.contains flag) with
    | Some flag ->
        eprintfn "ERROR telemetry start %s is not yet supported by this CLI" flag
        2
    | None ->
        match arguments |> List.tryHead |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal))) with
        | None ->
            eprintfn "ERROR telemetry start requires a work-item ID"
            1
        | Some workItemId ->
            let classifications = optionValues "--classification" arguments
            let classificationRationale = optionValue "--classification-rationale" arguments

            match FileTelemetryFinalizationRepository.startTarget root workItemId classifications classificationRationale with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok record ->
                if not (arguments |> List.contains "--quiet") then
                    match record with
                    | Some record -> printf "%s" (record.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)))
                    | None -> printf "null"

                0

/// `adapter call --store FILE --request FILE`: real effect for
/// production's file-based conformance adapter (`DF-ROS-2026-A007`,
/// `docs/work-adapter-contract.md`), exercising the same request/result
/// contract a production adapter must satisfy without choosing a
/// project-management vendor. Exit code mirrors production exactly:
/// 0 success, 1 failure (including a missing/malformed request file or a
/// missing required request field), 2 unknown.
let private runAdapterCall root (arguments: string list) =
    match optionValue "--store" arguments, optionValue "--request" arguments with
    | Some store, Some requestFile ->
        let requestPath = Path.GetFullPath(Path.Combine(root, requestFile))

        if not (File.Exists requestPath) then
            eprintfn "ERROR adapter request not found: %s" requestFile
            1
        else
            // A non-object top-level JSON value (e.g. an array) behaves the
            // same as production's own `request[field]` on such a value:
            // every required field reads as missing, so this falls through
            // to the same "adapter request is missing '...'" rejection.
            let request =
                match JsonNode.Parse(File.ReadAllText requestPath) with
                | :? JsonObject as parsed -> parsed
                | _ -> JsonObject()

            let storePath = Path.GetFullPath(Path.Combine(root, store))

            match FileAdapterRepository.call storePath request with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok result ->
                let options =
                    JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

                printfn "%s" (result.ToJsonString(options))
                FileAdapterRepository.exitCode result
    | _ ->
        eprintfn "ERROR adapter call requires --store and --request"
        1

/// `adapter publish --target FILE`: real effect for production's own
/// event republish (`tools/ros_cli.mjs`), appending every
/// `.ros/events/events.jsonl` event not already present in `target` (by
/// `eventId`) and refreshing `.ros/publications.json`'s receipts.
let private runAdapterPublish root (arguments: string list) =
    match optionValue "--target" arguments with
    | None ->
        eprintfn "ERROR adapter publish requires --target"
        1
    | Some target ->
        match FileAdapterRepository.publish root target with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok outcome ->
            printfn "published %d event(s); %d duplicate(s) skipped" outcome.Published outcome.Duplicates
            0

let private dispatch root arguments =
    let repository = FileArtifactRepository.create root
    let gitRepository = ProcessGitRepository.create root

    match arguments with
    | [ "version" ]
    | [ "--version" ] ->
        printfn "ros-fs %s" Version
        0
    | []
    | [ "help" ]
    | [ "--help" ]
    | [ "-h" ] ->
        printfn "%s" usage
        0
    | "artifacts" :: "validate" :: rest when rest |> List.forall ((=) "--json") ->
        runValidation (rest |> List.contains "--json") repository
    | "registry" :: "build" :: rest when rest |> List.forall ((=) "--dry-run") ->
        runRegistryBuild (rest |> List.contains "--dry-run") repository
    | [ "registry"; "check" ] -> runRegistryCheck repository
    | "git" :: "status" :: rest when rest |> List.forall ((=) "--json") ->
        runGitStatus (rest |> List.contains "--json") gitRepository
    | "work" :: "decide" :: rest -> runWorkDecision rest
    | "work" :: "plan" :: rest -> runWorkPlan root rest
    | "work" :: "context-plan" :: rest -> runWorkContextPlan root rest
    | "work" :: "backlog-decide" :: rest -> runBacklogDecision rest
    | "work" :: "backlog-promotion-plan" :: rest -> runBacklogPromotionPlan rest
    | "work" :: "validate" :: rest -> runWorkAttributionValidate root rest
    | "work" :: "backlog-validate" :: rest -> runBacklogQueueValidate root rest
    | "work" :: "backlog-transition" :: rest -> runBacklogTransitionEffect root rest
    | "work" :: "capture" :: rest -> runWorkCapture root rest
    | "work" :: "update" :: rest -> runWorkUpdate root rest
    | "work" :: "attach" :: rest -> runWorkAttach root rest
    | "work" :: "start" :: rest -> runWorkStart root rest
    | "work" :: "resume" :: rest -> runWorkResume root rest
    | "work" :: "block" :: rest -> runWorkBlock root rest
    | "work" :: "complete" :: rest -> runWorkComplete root rest
    | [ "telemetry"; "adapters" ] -> runTelemetryAdapters ()
    | "telemetry" :: "show" :: rest -> runTelemetryShow root rest
    | "telemetry" :: ("summary" | "summarize") :: rest -> runTelemetrySummary root rest
    | "telemetry" :: "finalize" :: rest -> runTelemetryFinalize root rest
    | "telemetry" :: "record" :: rest -> runTelemetryRecord root rest
    | "telemetry" :: "ingest" :: rest -> runTelemetryIngest root rest
    | "telemetry" :: "classify" :: rest -> runTelemetryClassify root rest
    | "telemetry" :: "start" :: rest -> runTelemetryStart root rest
    | "adapter" :: "call" :: rest -> runAdapterCall root rest
    | "adapter" :: "publish" :: rest -> runAdapterPublish root rest
    | _ ->
        eprintfn "%s" usage
        2

[<EntryPoint>]
let main arguments =
    try
        match parseRoot arguments with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok(root, remaining) -> dispatch root remaining
    with error ->
        eprintfn "ERROR %s" error.Message
        1
