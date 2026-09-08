namespace Ros.Tests

open System
open System.IO
open Ros.Application.Work
open Ros.Contracts.Work
open Ros.Domain.Work
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module WorkPlanTests =
    let private item state telemetryIds =
        { Id = "TASK-PLAN"
          WorkType = "feature"
          LocalState = WorkDecisionContract.stateName state
          SemanticState = state
          Evidence = [ { Type = "prior"; Path = "prior.txt" } ]
          BlockReason = Some "prior block"
          UpdatedAt = Some "2026-09-01T00:00:00Z"
          CompletedAt = None
          TelemetryExecutionIds = telemetryIds }

    let private request state action =
        { Item = item state [ "EXE-1" ]
          Action = action
          TargetLocalState =
            match action with
            | WorkAction.Begin
            | WorkAction.Resume -> "active"
            | WorkAction.Block -> "blocked"
            | WorkAction.Complete -> "complete"
          BlockReason = if action = WorkAction.Block then Some "blocked now" else None
          RequiredEvidence = Set.empty
          ProvidedEvidence = []
          Repository = "repo"
          ProtocolVersion = "1.0.0"
          OccurredAt = "2026-09-08T18:30:00Z"
          ChangedPaths = []
          TelemetryEnabled = true }

    let private planned request =
        match WorkOperations.planTransition request with
        | WorkPlanOutcome.Planned plan -> plan
        | other -> failwith $"Expected a plan, received {other}"

    let private contextRequest context action ids =
        { Context = context
          Action = action
          WorkItemIds = ids
          NewItemType = "task"
          TargetLocalState =
            match action with
            | WorkAction.Begin
            | WorkAction.Resume -> "active"
            | WorkAction.Block -> "blocked"
            | WorkAction.Complete -> "complete"
          BlockReason = if action = WorkAction.Block then Some "blocked now" else None
          DefaultRequiredEvidence = Set.empty
          RequiredEvidenceByType = Map.empty
          ProvidedEvidence = []
          Repository = "repo"
          ProtocolVersion = "1.0.0"
          Actor = "test-agent"
          OccurredAt = "2026-09-08T23:45:00Z"
          MeaningfulChangedPaths = []
          ObservedGitPaths = []
          TelemetryEnabled = false }

    let tests =
        [ { Name = "work completion plan replaces evidence and carries changed paths"
            Run =
              fun () ->
                  let evidence =
                      [ { Type = "implementation"; Path = "src/feature.fs" }
                        { Type = "tests"; Path = "tests/feature.fs" } ]

                  let plan =
                      planned
                          { request LiveWorkState.Active WorkAction.Complete with
                              RequiredEvidence = Set.ofList [ "implementation"; "tests" ]
                              ProvidedEvidence = evidence
                              ChangedPaths = [ "src/feature.fs"; "tests/feature.fs" ] }

                  Assert.equal LiveWorkState.Complete plan.Item.SemanticState
                  Assert.equal evidence plan.Item.Evidence
                  Assert.equal (Some "prior block") plan.Item.BlockReason
                  Assert.equal (Some "2026-09-08T18:30:00Z") plan.Item.CompletedAt
                  Assert.equal "work.completed" plan.Event.EventType
                  Assert.equal [ "src/feature.fs"; "tests/feature.fs" ] plan.Event.Paths
                  Assert.equal [ TelemetryIntent.FinalizeExecutions ] plan.Telemetry }
          { Name = "work plans ordered caller-specific telemetry intents"
            Run =
              fun () ->
                  Assert.equal
                      [ TelemetryIntent.EnsureActiveExecution ]
                      (planned (request LiveWorkState.Ready WorkAction.Begin)).Telemetry

                  Assert.equal
                      [ TelemetryIntent.RecordBlocked "blocked now" ]
                      (planned (request LiveWorkState.Active WorkAction.Block)).Telemetry

                  Assert.equal
                      [ TelemetryIntent.RecordResumed; TelemetryIntent.EnsureActiveExecution ]
                      (planned (request LiveWorkState.Blocked WorkAction.Resume)).Telemetry

                  let completion =
                      { request LiveWorkState.Active WorkAction.Complete with
                          Item = item LiveWorkState.Active [] }

                  Assert.equal
                      [ TelemetryIntent.EnsureCompletableExecution; TelemetryIntent.FinalizeExecutions ]
                      (planned completion).Telemetry }
          { Name = "work plan rejection produces no item event or telemetry plan"
            Run =
              fun () ->
                  let invalid =
                      { request LiveWorkState.Active WorkAction.Block with
                          BlockReason = None }

                  Assert.equal
                      (WorkPlanOutcome.Rejected TransitionRejection.BlockReasonRequired)
                      (WorkOperations.planTransition invalid) }
          { Name = "work plan preserves configured local-state mapping"
            Run =
              fun () ->
                  let plan =
                      planned
                          { request LiveWorkState.Ready WorkAction.Begin with
                              TargetLocalState = "doing"
                              TelemetryEnabled = false }

                  Assert.equal "doing" plan.Item.LocalState
                  Assert.equal LiveWorkState.Active plan.Item.SemanticState
                  Assert.equal [] plan.Telemetry }
          { Name = "verified work plan reports every evidence issue in request order"
            Run =
              fun () ->
                  let missing = { Type = "implementation"; Path = "missing.fs" }
                  let unavailable = { Type = "tests"; Path = "unavailable.fs" }
                  let repository =
                      { Observe =
                          fun evidence ->
                              if evidence = missing then EvidencePathObservation.Missing
                              else EvidencePathObservation.Unavailable "denied" }

                  let outcome =
                      WorkOperations.planVerifiedTransition
                          repository
                          { request LiveWorkState.Active WorkAction.Complete with
                              RequiredEvidence = Set.ofList [ "implementation"; "tests" ]
                              ProvidedEvidence = [ missing; unavailable ] }

                  Assert.equal
                      (VerifiedWorkPlanOutcome.EvidenceRejected
                          [ EvidenceIssue.Missing missing
                            EvidenceIssue.Unavailable(unavailable, "denied") ])
                      outcome }
          { Name = "filesystem evidence adapter distinguishes present files directories and missing paths"
            Run =
              fun () ->
                  let root = Path.Combine(Path.GetTempPath(), $"ros-evidence-{Guid.NewGuid():N}")
                  Directory.CreateDirectory root |> ignore

                  try
                      File.WriteAllText(Path.Combine(root, "present.txt"), "evidence")
                      Directory.CreateDirectory(Path.Combine(root, "present-directory")) |> ignore
                      let repository = FileEvidenceRepository.create root

                      Assert.equal
                          EvidencePathObservation.Present
                          (repository.Observe { Type = "tests"; Path = "present.txt" })

                      Assert.equal
                          EvidencePathObservation.Present
                          (repository.Observe { Type = "tests"; Path = "present-directory" })

                      Assert.equal
                          EvidencePathObservation.Missing
                          (repository.Observe { Type = "tests"; Path = "missing.txt" })

                      match repository.Observe { Type = "tests"; Path = "invalid\000path" } with
                      | EvidencePathObservation.Unavailable message -> Assert.isTrue (message.Length > 0) "failure message"
                      | other -> failwith $"Expected unavailable evidence path, received {other}"
                  finally
                      Directory.Delete(root, true) }
          { Name = "context plan retains context order and request event order"
            Run =
              fun () ->
                  let existing = { item LiveWorkState.Ready [] with Id = "TASK-EXIST" }
                  let context = { WorkItems = [ existing ]; StartedAt = None; BaselineDirtyPaths = [] }
                  let request =
                      { contextRequest context WorkAction.Begin [ "TASK-NEW"; "TASK-EXIST" ] with
                          ObservedGitPaths = [ "already-dirty.fs"; "TASK-NEW" ] }

                  match WorkOperations.planContext request with
                  | WorkContextPlanOutcome.Rejected rejection -> failwith $"Expected context plan, received {rejection}"
                  | WorkContextPlanOutcome.Planned plan ->
                      Assert.equal [ "TASK-EXIST"; "TASK-NEW" ] (plan.WorkItems |> List.map _.Id)
                      Assert.equal [ "TASK-NEW"; "TASK-EXIST" ] (plan.ItemPlans |> List.map _.Item.Id)
                      Assert.equal (Some request.OccurredAt) plan.StartedAt
                      Assert.equal [ "already-dirty.fs" ] plan.BaselineDirtyPaths
                      Assert.equal "test-agent" plan.Actor }
          { Name = "context plan rejects atomically when a later item transition is illegal"
            Run =
              fun () ->
                  let ready = { item LiveWorkState.Ready [] with Id = "TASK-READY" }
                  let complete = { item LiveWorkState.Complete [] with Id = "TASK-DONE"; CompletedAt = Some "earlier" }
                  let context = { WorkItems = [ ready; complete ]; StartedAt = Some "earlier"; BaselineDirtyPaths = [ "prior.fs" ] }

                  Assert.equal
                      (WorkContextPlanOutcome.Rejected(
                          WorkContextRejection.ItemTransitionRejected(
                              "TASK-DONE",
                              TransitionRejection.IllegalTransition(LiveWorkState.Complete, WorkAction.Begin))))
                      (WorkOperations.planContext (contextRequest context WorkAction.Begin [ "TASK-READY"; "TASK-DONE" ]))

                  Assert.equal LiveWorkState.Ready context.WorkItems.Head.SemanticState }
          { Name = "context plan creates absent items only for begin"
            Run =
              fun () ->
                  let context = { WorkItems = []; StartedAt = None; BaselineDirtyPaths = [] }

                  Assert.equal
                      (WorkContextPlanOutcome.Rejected(WorkContextRejection.WorkItemNotInContext "TASK-MISSING"))
                      (WorkOperations.planContext (contextRequest context WorkAction.Complete [ "TASK-MISSING" ]))

                  match WorkOperations.planContext (contextRequest context WorkAction.Begin [ "TASK-MISSING" ]) with
                  | WorkContextPlanOutcome.Planned plan ->
                      Assert.equal [ "TASK-MISSING" ] (plan.WorkItems |> List.map _.Id)
                  | other -> failwith $"Expected new begin item, received {other}" }
          { Name = "context plan rejects empty and malformed work item selections"
            Run =
              fun () ->
                  let context = { WorkItems = []; StartedAt = None; BaselineDirtyPaths = [] }

                  Assert.equal
                      (WorkContextPlanOutcome.Rejected WorkContextRejection.NoWorkItems)
                      (WorkOperations.planContext (contextRequest context WorkAction.Begin []))

                  Assert.equal
                      (WorkContextPlanOutcome.Rejected(WorkContextRejection.InvalidWorkItemId "bad id"))
                      (WorkOperations.planContext (contextRequest context WorkAction.Begin [ "bad id" ])) }
          { Name = "context JSON contract rejects an unknown semantic state"
            Run =
              fun () ->
                  let content =
                      """{"workItems":[{"id":"TASK-ONE","type":"task","state":"ready","semanticState":"future","evidence":[]}]}"""

                  match WorkContextPlanContract.parseJson content with
                  | Error message -> Assert.isTrue (message.Contains "unsupported semantic work state") "semantic-state error"
                  | Ok parsed -> failwith $"Expected malformed context rejection, received {parsed}" } ]
