namespace Ros.Cli

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Application.Git
open Ros.Application.Work
open Ros.Contracts.Work
open Ros.Domain.Git
open Ros.Domain.Provenance
open Ros.Domain.Work
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Git
open Ros.Infrastructure.Work

/// Durable checkpoints on the command line (DF-ROS-2026-A042): `work
/// checkpoint`, `work checkpoint show`, and the `continuity` read model that
/// `work context` and `status` report. This module parses, composes the
/// Application operations with the file and process adapters, and renders.
/// Every decision is made by `Ros.Domain.Work`.
[<RequireQualifiedAccess>]
module CheckpointCommands =
    let usage =
        "work checkpoint --id ID --occurred-at TIMESTAMP --summary TEXT --next-action TEXT [--step STEP-ID] [--execution EXE-ID] [--json] [IDENTITY] | work checkpoint show ID [--json] [--offline]"

    let private jsonOptions =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private optionValue (name: string) (arguments: string list) =
        arguments
        |> List.pairwise
        |> List.tryPick (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private optionValues (name: string) (arguments: string list) =
        arguments
        |> List.pairwise
        |> List.choose (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private isTimestamp (value: string) =
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) |> fst

    // ---- the continuity read model ----

    /// One item's assessment with the repository as it is now.
    let assessItem (git: GitDurability) (policy: ContinuityPolicy) (item: ContinuityItem) : Result<CheckpointAssessment, string list> =
        item.LatestCheckpoint
        |> Result.map (fun checkpoint -> CheckpointOperations.assess git policy item.WorkItemId item.State checkpoint)

    let private invalidNode (item: ContinuityItem) (problems: string list) =
        let node = JsonObject()
        node["workItemId"] <- JsonValue.Create item.WorkItemId
        node["state"] <- JsonValue.Create(FileCheckpointRepository.stateCode item.State)
        node["freshness"] <- JsonValue.Create "unknown"
        let array = JsonArray()
        problems |> List.iter (fun problem -> array.Add(JsonValue.Create problem: JsonNode))
        node["invalidCheckpoint"] <- array
        node

    let continuityNode (root: string) (git: GitDurability) (policy: ContinuityPolicy) (item: ContinuityItem) : JsonObject * ContinuityWarning list =
        match assessItem git policy item with
        | Error problems -> invalidNode item problems, [ ContinuityWarning.StateUnknown(item.WorkItemId, "the recorded latestCheckpoint is invalid") ]
        | Ok assessment ->
            let node =
                CheckpointJson.continuity item.WorkItemId (FileCheckpointRepository.stateCode item.State) assessment (RecoveryInstructions.derive assessment)

            node["unrecoverableLocalState"] <-
                match FileCheckpointRepository.readUnrecoverableNotice root item.WorkItemId with
                | Some notice -> notice :> JsonNode
                | None -> null

            node, assessment.Warnings

    /// The continuity blocks of the named item, or of every active and
    /// blocked item.
    let continuityFor (root: string) (offline: bool) (requestedId: string option) : Result<JsonArray * ContinuityWarning list, string> =
        match FileCheckpointRepository.readItems root with
        | Error message -> Error message
        | Ok items ->
            let selected =
                match requestedId with
                | Some id -> items |> List.filter (fun item -> item.WorkItemId = id)
                | None -> items |> List.filter (fun item -> item.State = LiveWorkState.Active || item.State = LiveWorkState.Blocked)

            let git = ProcessGitDurability.createFor root offline
            let policy = FileCheckpointRepository.readPolicy root
            let results = selected |> List.map (continuityNode root git policy)
            let array = JsonArray()
            results |> List.iter (fun (node, _) -> array.Add(node: JsonNode))
            Ok(array, results |> List.collect snd)

    /// `status`'s additive `continuity` block.
    let statusNode (root: string) (offline: bool) : JsonObject =
        let node = JsonObject()
        node["enforced"] <- JsonValue.Create(FileWorkConfigRepository.readRequireDurableCheckpoint root)
        node["remoteObserved"] <- JsonValue.Create(not offline)

        match continuityFor root offline None with
        | Error message ->
            node["items"] <- JsonArray()
            node["warnings"] <- JsonArray()
            node["error"] <- JsonValue.Create message
        | Ok(items, warnings) ->
            node["items"] <- items
            let array = JsonArray()
            warnings |> List.iter (fun warning -> array.Add(CheckpointJson.warningNode warning: JsonNode))
            node["warnings"] <- array

        node

    // ---- human rendering ----

    let private checkpointLines (recorded: RecordedCheckpoint) =
        let checkpoint = recorded.Recorded
        let git = DurableLocation.git checkpoint.Location
        let remote = git.Remote.Url |> Option.map (fun url -> $"{git.Remote.Name} ({url})") |> Option.defaultValue git.Remote.Name

        [ "LATEST RECOVERABLE CHECKPOINT"
          $"Checkpoint:     {recorded.CheckpointId}"
          $"Repository:     {git.Repository}"
          $"Branch:         {git.Branch}"
          $"Commit:         {git.LocalCommit.Value}"
          $"Remote:         {remote}"
          $"Remote branch:  {git.RemoteBranch}"
          $"Remote commit:  {git.RemoteCommit.Value}"
          $"Recorded:       {checkpoint.RecordedAt}"
          $"Execution:      {checkpoint.ExecutionId}"
          $"""Step:           {checkpoint.StepId |> Option.defaultValue "(none)"}"""
          $"Verification:   verified by {DurabilityMechanism.code checkpoint.Verification.Mechanism} when recorded"
          "Completed:"
          $"  {checkpoint.Summary}"
          "Next action:"
          $"  {checkpoint.NextAction}" ]

    let private stateLines (assessment: CheckpointAssessment) =
        let recoverability =
            match assessment.CurrentRecoverability with
            | Some value ->
                let recoverable =
                    match CurrentRecoverability.isRecoverable value with
                    | Some true -> "recoverable now"
                    | Some false -> "NOT recoverable from its recorded location now"
                    | None -> "recoverability unknown now"

                $"{CurrentRecoverability.code value} ({recoverable})"
            | None -> "not applicable"

        let local =
            match assessment.Local with
            | Some(LocalCheckpointPosition.CommitsAfter(count, _)) -> $"{count} commit(s) with meaningful changes after the checkpoint"
            | Some(LocalCheckpointPosition.OnlyNonMeaningfulCommitsAfter count) -> $"at the checkpoint ({count} later commit(s) carry only Praxis-owned or ignored state)"
            | Some position -> LocalCheckpointPosition.code position
            | None -> "not applicable"

        let tree =
            match assessment.WorkingTree with
            | WorkingTreeState.Clean [] -> "clean"
            | WorkingTreeState.Clean excluded -> $"clean ({excluded.Length} pre-existing baseline path(s) excluded)"
            | WorkingTreeState.Dirty(paths, _) -> $"""{paths.Length} meaningful uncommitted path(s): {paths |> List.truncate 5 |> String.concat ", "}"""
            | WorkingTreeState.NotRepository -> "not a Git repository"
            | WorkingTreeState.Unknown failure -> $"unknown ({failure.Message})"

        [ ""
          "CURRENT STATE (observed now, separately from the historical checkpoint)"
          $"Freshness:              {CheckpointFreshness.code assessment.Freshness}"
          $"Current recoverability: {recoverability}"
          $"Local HEAD:             {local}"
          $"Working tree:           {tree}" ]

    let renderText (root: string) (item: ContinuityItem) (assessment: CheckpointAssessment) =
        let checkpoint =
            match assessment.Checkpoint with
            | Some recorded -> checkpointLines recorded
            | None -> [ "LATEST RECOVERABLE CHECKPOINT"; "  none recorded" ]

        let warnings =
            match assessment.Warnings with
            | [] -> []
            | warnings -> "" :: "WARNINGS" :: (warnings |> List.map (fun warning -> $"  WARNING: {ContinuityWarning.message warning}"))

        let notice =
            match FileCheckpointRepository.readUnrecoverableNotice root item.WorkItemId with
            | Some node ->
                let field (name: string) =
                    match node[name] with
                    | :? JsonValue as value -> value.ToString()
                    | _ -> ""

                let recordedAt = field "recordedAt"
                let reason = field "reason"
                let unrecovered = field "unrecoveredWork"

                [ ""
                  "UNRECOVERABLE LOCAL STATE (recorded when the work was blocked)"
                  $"  {recordedAt}: {reason}"
                  $"  work not made durable: {unrecovered}" ]
            | None -> []

        let recovery =
            "" :: "RECOVERY" :: (RecoveryInstructions.derive assessment |> List.map (fun step -> $"  {RecoveryStep.render step}"))

        [ $"WORK ITEM {item.WorkItemId} ({FileCheckpointRepository.stateCode item.State})"; "" ]
        @ checkpoint
        @ stateLines assessment
        @ warnings
        @ notice
        @ recovery

    // ---- work checkpoint ----

    let private checkpointFlags =
        set
            [ "--id"
              "--occurred-at"
              "--summary"
              "--next-action"
              "--step"
              "--execution"
              "--actor-kind"
              "--agent"
              "--actor"
              "--provider"
              "--model"
              "--model-version"
              "--runtime"
              "--runtime-version"
              "--session"
              "--conversation"
              "--run"
              "--subagent" ]

    let rec private unexpected (arguments: string list) =
        match arguments with
        | [] -> []
        | flag :: _ :: rest when checkpointFlags.Contains flag -> unexpected rest
        | "--json" :: rest -> unexpected rest
        | token :: rest -> token :: unexpected rest

    let private argumentErrors (arguments: string list) =
        [ match optionValues "--id" arguments with
          | [ id ] when WorkItemId.isValid id -> ()
          | [ id ] -> yield $"'{id}' is not a valid work-item ID"
          | [] -> yield "work checkpoint requires --id ID"
          | _ -> yield "work checkpoint names exactly one work item; pass --id once"
          match optionValues "--occurred-at" arguments with
          | [ value ] when isTimestamp value -> ()
          | [ value ] -> yield $"--occurred-at '{value}' is not a timestamp"
          | _ -> yield "work checkpoint requires exactly one --occurred-at TIMESTAMP (the real current time)"
          if (optionValues "--summary" arguments).Length <> 1 then
              yield "work checkpoint requires one --summary TEXT describing the completed work"
          if (optionValues "--next-action" arguments).Length <> 1 then
              yield "work checkpoint requires one --next-action TEXT naming the next intended step"
          for token in unexpected arguments do
              yield $"unexpected argument '{token}'" ]

    let private recordedNode (recorded: RecordedCheckpoint) =
        let node = CheckpointJson.projection recorded
        node["status"] <- JsonValue.Create "verified"
        node

    let private render asJson (workItemId: string) (outcome: Result<RecordedCheckpoint * string list * string list, CheckpointRejection list>) =
        let document = JsonObject()
        document["command"] <- JsonValue.Create "work checkpoint"
        document["schemaVersion"] <- JsonValue.Create 1
        document["workItemId"] <- JsonValue.Create workItemId

        match outcome with
        | Ok(recorded, paths, excluded) ->
            if asJson then
                document["status"] <- JsonValue.Create "recorded"
                document["checkpoint"] <- recordedNode recorded
                let pathArray = JsonArray()
                paths |> List.iter (fun path -> pathArray.Add(JsonValue.Create path: JsonNode))
                document["paths"] <- pathArray
                let excludedArray = JsonArray()
                excluded |> List.iter (fun path -> excludedArray.Add(JsonValue.Create path: JsonNode))
                document["excludedBaselinePaths"] <- excludedArray
                printf "%s" (document.ToJsonString jsonOptions)
            else
                let git = DurableLocation.git recorded.Recorded.Location
                printfn "durable checkpoint recorded for %s (checkpoint %s)" workItemId recorded.CheckpointId
                printfn "  commit:        %s on %s" git.LocalCommit.Value git.Branch
                printfn "  verified at:   %s/%s == local HEAD (read from the remote itself)" git.Remote.Name git.RemoteBranch
                printfn "  execution:     %s" recorded.Recorded.ExecutionId
                recorded.Recorded.StepId |> Option.iter (printfn "  step:          %s")
                printfn "  recorded:      %s" recorded.Recorded.RecordedAt
                printfn "  completed:     %s" recorded.Recorded.Summary
                printfn "  next action:   %s" recorded.Recorded.NextAction
                printfn "  attributed:    %d meaningful path(s)" paths.Length

                if not excluded.IsEmpty then
                    printfn "  not claimed:   %d pre-existing baseline path(s)" excluded.Length

                printfn "Praxis state changed under .ros/; commit and push it so another executor can find this checkpoint."

            0
        | Error rejections ->
            if asJson then
                document["status"] <- JsonValue.Create "rejected"
                let array = JsonArray()
                rejections |> List.iter (fun rejection -> array.Add(CheckpointJson.rejectionNode rejection: JsonNode))
                document["rejections"] <- array
                printf "%s" (document.ToJsonString jsonOptions)
            else
                for rejection in rejections do
                    eprintfn "ERROR [%s] %s" (CheckpointRejection.code rejection) (CheckpointRejection.message rejection)
                    eprintfn "  REMEDY %s" (CheckpointRejection.remedy rejection)

                eprintfn "checkpoint rejected; nothing was recorded"

            if rejections |> List.forall CheckpointRejection.isArgumentError then 2 else 1

    /// Verifies and records one checkpoint under the `work-protocol` lock.
    let private checkpoint root (arguments: string list) (actor: Actor) (workItemId: string) =
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match FileCheckpointRepository.readItem root workItemId with
                        | Error message -> Error message
                        | Ok item ->
                            let git = ProcessGitDurability.create root
                            let policy = FileCheckpointRepository.readPolicy root

                            let execution =
                                match item with
                                | Some candidate when candidate.State = LiveWorkState.Active ->
                                    FileCheckpointRepository.resolveExecution root workItemId (ProvenanceCommands.identityOverridesFrom arguments) (optionValue "--execution" arguments)
                                | _ -> ExecutionObservation.NoneActive

                            let candidate =
                                { WorkItemId = workItemId
                                  Repository = FileWorkConfigRepository.readRepositoryId root
                                  Summary = optionValue "--summary" arguments |> Option.defaultValue ""
                                  NextAction = optionValue "--next-action" arguments |> Option.defaultValue ""
                                  StepId = optionValue "--step" arguments
                                  OccurredAt = (optionValue "--occurred-at" arguments).Value }

                            match CheckpointOperations.verify git policy candidate (item |> Option.map _.State) execution with
                            | Error rejections -> Ok(Error rejections)
                            | Ok verified ->
                                let since =
                                    match item |> Option.map _.LatestCheckpoint with
                                    | Some(Ok(Some previous)) -> Some previous.Recorded.Commit
                                    | _ -> FileCheckpointRepository.readStartCommit root workItemId

                                let paths = CheckpointOperations.attributablePaths git policy since verified

                                let excluded =
                                    WorkingTreeState.observe policy.PathFilter policy.BaselineDirtyPaths (git.Status())
                                    |> WorkingTreeState.excludedBaselinePaths

                                FileCheckpointRepository.record root actor paths verified
                                |> Result.map (fun recorded -> Ok(recorded, paths, excluded))
                with error ->
                    Error $"state persistence failed: {error.Message}"

            match lease.Release(), result with
            | Error failure, Ok _ -> Error failure.Message
            | _, value -> value

    let run root (arguments: string list) (actor: Actor) =
        match argumentErrors arguments with
        | _ :: _ as errors ->
            for error in errors do
                eprintfn "ERROR %s" error

            eprintfn "Usage: ros %s" usage
            2
        | [] ->
            let workItemId = (optionValue "--id" arguments).Value
            let asJson = List.contains "--json" arguments

            match checkpoint root arguments actor workItemId with
            | Error message ->
                if asJson then
                    let document = JsonObject()
                    document["command"] <- JsonValue.Create "work checkpoint"
                    document["schemaVersion"] <- JsonValue.Create 1
                    document["workItemId"] <- JsonValue.Create workItemId
                    document["status"] <- JsonValue.Create "failed"
                    let failure = JsonObject()
                    failure["code"] <- JsonValue.Create "persistence-failed"
                    failure["message"] <- JsonValue.Create message
                    document["failure"] <- failure
                    printf "%s" (document.ToJsonString jsonOptions)
                else
                    eprintfn "ERROR [persistence-failed] %s" message

                1
            | Ok outcome -> render asJson workItemId outcome

    // ---- work checkpoint show ----

    let private historyNode (event: CheckpointJson.EventRead) =
        let node = JsonObject()
        node["id"] <- JsonValue.Create event.EventId
        node["recordedAt"] <- JsonValue.Create event.OccurredAt

        match event.Checkpoint with
        | Ok checkpoint ->
            let git = DurableLocation.git checkpoint.Location
            node["executionId"] <- JsonValue.Create checkpoint.ExecutionId
            checkpoint.StepId |> Option.iter (fun step -> node["stepId"] <- JsonValue.Create step)
            node["branch"] <- JsonValue.Create git.Branch
            node["commit"] <- JsonValue.Create git.LocalCommit.Value
            node["remote"] <- JsonValue.Create git.Remote.Name
            node["remoteBranch"] <- JsonValue.Create git.RemoteBranch
            node["summary"] <- JsonValue.Create checkpoint.Summary
            node["nextAction"] <- JsonValue.Create checkpoint.NextAction
        | Error problems ->
            let array = JsonArray()
            problems |> List.iter (fun problem -> array.Add(JsonValue.Create problem: JsonNode))
            node["invalid"] <- array

        let paths = JsonArray()
        event.Paths |> List.iter (fun path -> paths.Add(JsonValue.Create path: JsonNode))
        node["paths"] <- paths
        node

    let show root (arguments: string list) =
        let requested = arguments |> List.tryHead |> Option.filter (fun value -> not (value.StartsWith "--"))
        let offline = List.contains "--offline" arguments
        let asJson = List.contains "--json" arguments

        match requested with
        | None ->
            eprintfn "ERROR work checkpoint show requires a work-item ID"
            2
        | Some workItemId ->
            match FileCheckpointRepository.readItem root workItemId with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok None ->
                eprintfn "ERROR work item '%s' is not in repository context" workItemId
                1
            | Ok(Some item) ->
                let git = ProcessGitDurability.createFor root offline
                let policy = FileCheckpointRepository.readPolicy root
                let history = FileCheckpointRepository.readHistory root workItemId

                match assessItem git policy item with
                | Error problems ->
                    for problem in problems do
                        eprintfn "ERROR latestCheckpoint: %s" problem

                    1
                | Ok assessment ->
                    if asJson then
                        let document = JsonObject()
                        document["command"] <- JsonValue.Create "work checkpoint show"
                        document["schemaVersion"] <- JsonValue.Create 1
                        document["remoteObserved"] <- JsonValue.Create(not offline)

                        document["continuity"] <-
                            CheckpointJson.continuity workItemId (FileCheckpointRepository.stateCode item.State) assessment (RecoveryInstructions.derive assessment)

                        let array = JsonArray()
                        history |> List.iter (fun event -> array.Add(historyNode event: JsonNode))
                        document["history"] <- array
                        printf "%s" (document.ToJsonString jsonOptions)
                    else
                        renderText root item assessment |> List.iter (printfn "%s")
                        printfn ""
                        printfn "CHECKPOINT HISTORY (%d, oldest first; never rewritten)" history.Length

                        for event in history do
                            match event.Checkpoint with
                            | Ok checkpoint ->
                                printfn "  %s  %s  %s  %s  %s" event.OccurredAt event.EventId (CommitId.short checkpoint.Commit) checkpoint.ExecutionId checkpoint.Summary
                            | Error problems -> printfn "  %s  %s  INVALID: %s" event.OccurredAt event.EventId (String.concat "; " problems)

                    0

    // ---- lifecycle guards (PRAXIS-CONT-05) ----

    let private activeItems root (ids: string list) =
        FileCheckpointRepository.readItems root
        |> Result.map (fun items ->
            ids
            |> List.choose (fun id -> items |> List.tryFind (fun item -> item.WorkItemId = id && item.State = LiveWorkState.Active)))

    /// Meaningful Git-backed work completes only when its final state is the
    /// latest checkpoint, re-verified at the remote now (CONT-040/041). A
    /// repository that has not opted in is unaffected.
    let completionGuard root (ids: string list) : Result<unit, string> =
        if not (FileWorkConfigRepository.readRequireDurableCheckpoint root) then
            Ok()
        else
            match activeItems root ids with
            | Error message -> Error message
            | Ok items ->
                // Never cached: completion re-reads the remote now.
                let git = ProcessGitDurability.create root
                let policy = FileCheckpointRepository.readPolicy root

                let failures =
                    items
                    |> List.collect (fun item ->
                        match item.LatestCheckpoint with
                        | Error problems -> [ $"""'{item.WorkItemId}' has an invalid latestCheckpoint ({String.concat "; " problems})""" ]
                        | Ok latest ->
                            let start = FileCheckpointRepository.readStartCommit root item.WorkItemId

                            match fst (CheckpointOperations.decideCompletion git policy item.WorkItemId latest start) with
                            | CompletionGuardOutcome.NotApplicable _
                            | CompletionGuardOutcome.Satisfied _ -> []
                            | CompletionGuardOutcome.Rejected rejections ->
                                rejections |> List.map (CompletionGuardRejection.message item.WorkItemId))

                match failures with
                | [] -> Ok()
                | failures ->
                    Error(
                        "completion requires a durable final state: "
                        + String.concat "; " failures
                        + " (no meaningless commit is ever required: work that changed nothing completes as before)"
                    )

    /// Blocking after meaningful work since the latest checkpoint needs a
    /// checkpoint first or a truthful `--unrecoverable-reason` (CONT-042).
    /// Returns the `continuity` extension to record on each block event.
    let blockGuard root (ids: string list) (unrecoverableReason: string option) : Result<Map<string, JsonObject>, string> =
        let enforced = FileWorkConfigRepository.readRequireDurableCheckpoint root

        if not enforced && unrecoverableReason.IsNone then
            Ok Map.empty
        else
            match activeItems root ids with
            | Error message -> Error message
            | Ok items ->
                let git = ProcessGitDurability.createFor root false
                let policy = FileCheckpointRepository.readPolicy root

                let decisions =
                    items
                    |> List.map (fun item ->
                        match item.LatestCheckpoint with
                        | Error problems -> item, Error(String.concat "; " problems)
                        | Ok latest ->
                            let start = FileCheckpointRepository.readStartCommit root item.WorkItemId
                            item, Ok(latest, fst (CheckpointOperations.decideBlock git policy item.WorkItemId latest start unrecoverableReason)))

                let failures =
                    decisions
                    |> List.choose (fun (item, decision) ->
                        match decision with
                        | Error problems -> Some $"'{item.WorkItemId}' has an invalid latestCheckpoint ({problems})"
                        | Ok(_, BlockGuardOutcome.Rejected newWork) when enforced ->
                            Some
                                $"cannot block '{item.WorkItemId}': meaningful work exists that no durable checkpoint covers ({newWork}). Record a checkpoint first (commit, push, 'work checkpoint'), or pass --unrecoverable-reason TEXT stating truthfully why the latest local state cannot be made remotely recoverable"
                        | _ -> None)

                match failures with
                | _ :: _ -> Error(String.concat "; " failures)
                | [] ->
                    decisions
                    |> List.choose (fun (item, decision) ->
                        match decision with
                        | Ok(latest, BlockGuardOutcome.RecordedUnrecoverable(reason, newWork)) ->
                            let continuity = JsonObject()
                            continuity["status"] <- JsonValue.Create "not-remotely-recoverable"
                            continuity["reason"] <- JsonValue.Create reason.Value
                            continuity["unrecoveredWork"] <- JsonValue.Create newWork

                            continuity["latestCheckpoint"] <-
                                match latest with
                                | Some recorded -> JsonValue.Create recorded.CheckpointId :> JsonNode
                                | None -> null

                            let extension = JsonObject()
                            extension["continuity"] <- continuity
                            Some(item.WorkItemId, extension)
                        | _ -> None)
                    |> Map.ofList
                    |> Ok

    // ---- work continue (PRAXIS-CONT-06) ----

    let continueUsage = "work continue --id ID --occurred-at TIMESTAMP [--json] [IDENTITY]"

    let private numberText (node: JsonNode) =
        match node with
        | :? JsonValue as value -> value.ToJsonString()
        | _ -> "null"

    /// Test and validation measurements every execution of the work item
    /// recorded, with their evidence quality and source, so a successor sees
    /// what actually ran instead of trusting a summary.
    let private validationEvidence root (workItemId: string) =
        let array = JsonArray()

        for record in FileTelemetryQueryRepository.readByWorkItemId root workItemId do
            let executionId =
                match record["executionId"] with
                | :? JsonValue as value -> value.GetValue<string>()
                | _ -> ""

            match record["metrics"] with
            | :? JsonArray as metrics ->
                for metric in metrics do
                    match metric with
                    | :? JsonObject as measurement ->
                        match measurement["id"] with
                        | :? JsonValue as id when id.GetValue<string>().StartsWith "tests." ->
                            let node = JsonObject()
                            node["executionId"] <- JsonValue.Create executionId
                            node["metric"] <- id.DeepClone()
                            node["value"] <- (match measurement["value"] with null -> null | value -> value.DeepClone())
                            node["quality"] <- (match measurement["quality"] with null -> null | value -> value.DeepClone())
                            node["source"] <- (match measurement["source"] with null -> null | value -> value.DeepClone())
                            array.Add(node: JsonNode)
                        | _ -> ()
                    | _ -> ()
            | _ -> ()

        array

    let private stepsOf root (executionId: string) =
        let array = JsonArray()

        match FileTelemetryQueryRepository.readByExecutionId root executionId with
        | Ok record ->
            for step in FileTelemetryFinalizationRepository.stepEvents record |> Ros.Domain.Telemetry.Steps.project do
                let node = JsonObject()
                node["stepId"] <- JsonValue.Create step.StepId
                step.Name |> Option.iter (fun name -> node["name"] <- JsonValue.Create name)

                node["status"] <-
                    JsonValue.Create(
                        match step.Status with
                        | Ros.Domain.Telemetry.StepStatus.Running -> "running"
                        | Ros.Domain.Telemetry.StepStatus.Completed -> "completed"
                        | Ros.Domain.Telemetry.StepStatus.Failed -> "failed"
                    )

                array.Add(node: JsonNode)
        | Error _ -> ()

        array

    let private strings (values: string list) =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value: JsonNode))
        array

    type private ContinueOutcome =
        | Continued of plan: ContinuationPlan * successor: string option * eventId: string * item: ContinuityItem * assessment: CheckpointAssessment
        | Refused of rejection: ContinuationRejection * item: ContinuityItem option * assessment: CheckpointAssessment option

    let private continueUnderLock root (arguments: string list) (workItemId: string) (occurredAt: string) =
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        let overrides = ProvenanceCommands.identityOverridesFrom arguments

                        match FileCheckpointRepository.readItem root workItemId, FileCheckpointRepository.executionsForContinuation root workItemId overrides with
                        | Error message, _
                        | _, Error message -> Error message
                        | Ok None, _ -> Ok(Refused(ContinuationRejection.WorkItemNotFound, None, None))
                        | Ok(Some item), Ok(actor, mine, others, latest) ->
                            match item.LatestCheckpoint with
                            | Error problems -> Error $"""the recorded latestCheckpoint is invalid: {String.concat "; " problems}"""
                            | Ok latestCheckpoint ->
                                let git = ProcessGitDurability.create root
                                let policy = FileCheckpointRepository.readPolicy root
                                let assessment = CheckpointOperations.assess git policy workItemId item.State latestCheckpoint

                                let input =
                                    { WorkItemId = workItemId
                                      ItemState = Some item.State
                                      Assessment = assessment
                                      CallerExecutions = mine
                                      OtherActiveExecutions = others
                                      LatestExecution = latest }

                                match Continuation.decide input with
                                | Error rejection -> Ok(Refused(rejection, Some item, Some assessment))
                                | Ok plan ->
                                    let request: FileTelemetryExecutionRepository.CreateExecutionRequest =
                                        { WorkItemId = workItemId
                                          WorkType = item.WorkType
                                          Classifications = []
                                          ClassificationRationale = None
                                          ExecutionId = None
                                          IdentityOverrides = { overrides with ParentExecutionId = plan.Predecessor |> Option.map fst } }

                                    match FileTelemetryExecutionRepository.createExecution root request with
                                    | Error message -> Error message
                                    | Ok successor ->
                                        FileCheckpointRepository.recordContinuation root actor occurredAt plan successor
                                        |> Result.map (fun eventId -> Continued(plan, successor, eventId, item, assessment))
                with error ->
                    Error error.Message

            match lease.Release(), result with
            | Error failure, Ok _ -> Error failure.Message
            | _, value -> value

    let runContinue root (arguments: string list) (_: Actor) =
        let ids = optionValues "--id" arguments
        let occurredAt = optionValues "--occurred-at" arguments
        let asJson = List.contains "--json" arguments

        match ids, occurredAt with
        | [ id ], [ at ] when WorkItemId.isValid id && isTimestamp at ->
            let document = JsonObject()
            document["command"] <- JsonValue.Create "work continue"
            document["schemaVersion"] <- JsonValue.Create 1
            document["workItemId"] <- JsonValue.Create id

            match continueUnderLock root arguments id at with
            | Error message ->
                if asJson then
                    document["status"] <- JsonValue.Create "failed"
                    document["message"] <- JsonValue.Create message
                    printf "%s" (document.ToJsonString jsonOptions)
                else
                    eprintfn "ERROR %s" message

                1
            | Ok(Refused(rejection, item, assessment)) ->
                let recovery = assessment |> Option.map RecoveryInstructions.derive |> Option.defaultValue []

                if asJson then
                    document["status"] <- JsonValue.Create "refused"
                    let refusal = JsonObject()
                    refusal["code"] <- JsonValue.Create(ContinuationRejection.code rejection)
                    refusal["message"] <- JsonValue.Create(ContinuationRejection.message id rejection)
                    document["refusal"] <- refusal
                    let steps = JsonArray()
                    recovery |> List.iter (fun step -> steps.Add(CheckpointJson.recoveryNode step: JsonNode))
                    document["recovery"] <- steps
                    printf "%s" (document.ToJsonString jsonOptions)
                else
                    eprintfn "ERROR [%s] %s" (ContinuationRejection.code rejection) (ContinuationRejection.message id rejection)
                    recovery |> List.iter (fun step -> eprintfn "  %s" (RecoveryStep.render step))
                    eprintfn "nothing was recorded; no execution was created"

                ignore item
                1
            | Ok(Continued(plan, successor, eventId, item, assessment)) ->
                let defaultEvidence, byType = FileWorkConfigRepository.readCompletionEvidence root
                let required = byType |> Map.tryFind item.WorkType |> Option.defaultValue defaultEvidence |> Set.toList
                let evidence = validationEvidence root id

                let predecessorSteps =
                    plan.Predecessor |> Option.map (fst >> stepsOf root) |> Option.defaultValue (JsonArray())

                if asJson then
                    document["status"] <- JsonValue.Create "continued"

                    document["executionId"] <-
                        match successor with
                        | Some value -> JsonValue.Create value :> JsonNode
                        | None -> null

                    document["event"] <- JsonValue.Create eventId

                    document["predecessor"] <-
                        match plan.Predecessor with
                        | Some(predecessorId, disposition) ->
                            let node = JsonObject()
                            node["executionId"] <- JsonValue.Create predecessorId
                            node["disposition"] <- JsonValue.Create(PredecessorDisposition.code disposition)
                            node["steps"] <- predecessorSteps
                            node :> JsonNode
                        | None -> null

                    document["otherActiveExecutions"] <- strings plan.OtherActiveExecutions

                    document["continuity"] <-
                        CheckpointJson.continuity id (FileCheckpointRepository.stateCode item.State) assessment (RecoveryInstructions.derive assessment)

                    let obligations = JsonObject()
                    obligations["requiredEvidenceForCompletion"] <- strings required
                    obligations["validationEvidence"] <- evidence
                    document["obligations"] <- obligations
                    printf "%s" (document.ToJsonString jsonOptions)
                else
                    let parent =
                        match plan.Predecessor with
                        | Some(predecessorId, disposition) -> $"parent {predecessorId}, recorded as {PredecessorDisposition.code disposition}"
                        | None -> "no predecessor execution"

                    printfn "continuing %s in new execution %s (%s)" id (successor |> Option.defaultValue "(telemetry disabled)") parent
                    printfn ""
                    renderText root item assessment |> List.iter (printfn "%s")
                    printfn ""
                    printfn "OBLIGATIONS"
                    printfn "  completion evidence required: %s" (if required.IsEmpty then "none" else String.concat ", " required)
                    printfn "  validation evidence recorded (what actually ran, with its source):"

                    if evidence.Count = 0 then
                        printfn "    none recorded; a summary is not proof that tests passed"
                    else
                        for node in evidence do
                            printfn "    %s: %s = %s (%s)" (node["executionId"].GetValue<string>()) (node["metric"].GetValue<string>()) (numberText node["value"]) (numberText node["quality"])

                    if predecessorSteps.Count > 0 then
                        printfn "  predecessor steps:"

                        for node in predecessorSteps do
                            printfn "    %s: %s" (node["stepId"].GetValue<string>()) (node["status"].GetValue<string>())

                    printfn ""
                    printfn "Praxis state changed under .ros/; commit and push it so the handoff itself is durable."

                0
        | _ ->
            eprintfn "ERROR work continue requires exactly one valid --id and one --occurred-at TIMESTAMP (the real current time)"
            eprintfn "Usage: ros %s" continueUsage
            2
