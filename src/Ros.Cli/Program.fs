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

[<Literal>]
let Version = "0.2.0-shadow"

let private usage =
    "Usage: ros-fs [--root PATH] version | artifacts validate [--json] | registry build [--dry-run] | registry check | git status [--json] | work decide [options] | work plan [options] [--resolve-telemetry --candidate EXECUTIONID=active|finalized]* [--requested-execution-id ID] | work context-plan [options] | work backlog-decide --state STATE --action ACTION [--reason TEXT] | work backlog-promotion-plan --id ID [--queue-state ID=STATE] [--type TYPE] | work validate [--json] | work backlog-validate [--json] | work backlog-transition --id ID --action {ready|block|abandon} --occurred-at TIMESTAMP [--reason TEXT] | work capture --title TITLE --occurred-at TIMESTAMP [--id ID] [--priority {high|medium|low}] [--description TEXT] [--tag TAG]* [--actor NAME] [--source NAME] [--source-reference REF] | work update --id ID --occurred-at TIMESTAMP [--title TEXT] [--description TEXT] [--priority {high|medium|low}] [--tag TAG]*"

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
                            match FileBacklogQueueRepository.readItems root |> List.tryFind (fun item -> item.Id = id) with
                            | None -> Error $"'{id}' is not a captured local work item"
                            | Some item ->
                                let illegalTransition () =
                                    Error $"cannot {actionText} backlog item '{id}' from '{item.Status}'"

                                match parseBacklogState item.Status with
                                | None -> illegalTransition ()
                                | Some currentState ->
                                    match
                                        WorkOperations.decideBacklogTransition
                                            { State = currentState; Action = action; Reason = reason }
                                    with
                                    | BacklogTransitionDecision.Rejected(BacklogTransitionRejection.IllegalTransition _) ->
                                        illegalTransition ()
                                    | BacklogTransitionDecision.Rejected BacklogTransitionRejection.BlockReasonRequired ->
                                        Error "block requires --reason"
                                    | BacklogTransitionDecision.Allowed BacklogTransitionEffect.PromoteToLiveWork ->
                                        Error "work backlog-transition does not support 'start'; use production './ros work start'"
                                    | BacklogTransitionDecision.Allowed(BacklogTransitionEffect.ChangeState(newState, blockedChange, abandonedChange)) ->
                                        match readWorkContext root with
                                        | Error message -> Error message
                                        | Ok context ->
                                            FileBacklogQueueRepository.applyStateChange
                                                root
                                                id
                                                newState
                                                blockedChange
                                                abandonedChange
                                                timestamp
                                                context.WorkItems
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
