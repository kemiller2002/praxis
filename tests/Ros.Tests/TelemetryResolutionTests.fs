namespace Ros.Tests

open System
open System.IO
open Ros.Application.Work
open Ros.Domain.Telemetry
open Ros.Domain.Work
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module TelemetryResolutionTests =
    let private candidate executionId workItemId status =
        { ExecutionId = executionId
          WorkItemId = workItemId
          Status = status }

    let private state linked candidates =
        { LinkedExecutionIds = linked
          Candidates = candidates
          RequestedExecutionId = None }

    let private item telemetryIds =
        { Id = "TASK-TELEMETRY"
          WorkType = "feature"
          LocalState = "active"
          SemanticState = LiveWorkState.Active
          Evidence = []
          BlockReason = None
          UpdatedAt = None
          CompletedAt = None
          TelemetryExecutionIds = telemetryIds }

    let private plan telemetry telemetryIds =
        { Item = item telemetryIds
          Event =
            { EventType = "work.started"
              WorkItemId = "TASK-TELEMETRY"
              Repository = "repo"
              ProtocolVersion = "1.0.0"
              OccurredAt = "2026-09-09T00:00:00Z"
              Reason = None
              Evidence = []
              Paths = []
              TelemetryExecutionIds = telemetryIds }
          Telemetry = telemetry }

    let tests =
        [ { Name = "begin recovers the single detached active execution"
            Run =
              fun () ->
                  let outcome =
                      TelemetryResolution.resolve
                          "TASK-TELEMETRY"
                          [ TelemetryIntent.EnsureActiveExecution ]
                          (state [] [ candidate "EXE-detached" "TASK-TELEMETRY" ExecutionStatus.Active ])

                  Assert.equal
                      (TelemetryResolutionOutcome.Resolved(
                          [ "EXE-detached" ],
                          [ TelemetryResolutionStep.Recovered "EXE-detached" ],
                          []
                      ))
                      outcome }
          { Name = "begin with no candidates requires a new execution and halts"
            Run =
              fun () ->
                  let outcome =
                      TelemetryResolution.resolve "TASK-TELEMETRY" [ TelemetryIntent.EnsureActiveExecution ] (state [] [])

                  Assert.equal
                      (TelemetryResolutionOutcome.PendingNewExecution(
                          [],
                          [ TelemetryResolutionStep.RequiresNewExecution ],
                          [ TelemetryEffect.CreateExecution ]
                      ))
                      outcome }
          { Name = "begin rejects ambiguous detached active candidates"
            Run =
              fun () ->
                  let outcome =
                      TelemetryResolution.resolve
                          "TASK-TELEMETRY"
                          [ TelemetryIntent.EnsureActiveExecution ]
                          (state
                              []
                              [ candidate "EXE-z" "TASK-TELEMETRY" ExecutionStatus.Active
                                candidate "EXE-a" "TASK-TELEMETRY" ExecutionStatus.Active ])

                  Assert.equal
                      (TelemetryResolutionOutcome.Rejected(TelemetryResolutionRejection.Ambiguous [ "EXE-a"; "EXE-z" ]))
                      outcome }
          { Name = "resume links every active execution without rejecting on multiple candidates"
            Run =
              fun () ->
                  let outcome =
                      TelemetryResolution.resolve
                          "TASK-TELEMETRY"
                          [ TelemetryIntent.RecordResumed; TelemetryIntent.EnsureActiveExecution ]
                          (state
                              []
                              [ candidate "EXE-a" "TASK-TELEMETRY" ExecutionStatus.Active
                                candidate "EXE-b" "TASK-TELEMETRY" ExecutionStatus.Active ])

                  Assert.equal
                      (TelemetryResolutionOutcome.Resolved(
                          [ "EXE-a"; "EXE-b" ],
                          [ TelemetryResolutionStep.NoChange; TelemetryResolutionStep.LinkedActive [ "EXE-a"; "EXE-b" ] ],
                          []
                      ))
                      outcome }
          { Name = "resume with an already-linked active execution appends nothing new"
            Run =
              fun () ->
                  let outcome =
                      TelemetryResolution.resolve
                          "TASK-TELEMETRY"
                          [ TelemetryIntent.RecordResumed; TelemetryIntent.EnsureActiveExecution ]
                          (state [ "EXE-a" ] [ candidate "EXE-a" "TASK-TELEMETRY" ExecutionStatus.Active ])

                  Assert.equal
                      (TelemetryResolutionOutcome.Resolved(
                          [ "EXE-a" ],
                          [ TelemetryResolutionStep.NoChange; TelemetryResolutionStep.LinkedActive [ "EXE-a" ] ],
                          []
                      ))
                      outcome }
          { Name = "resume with no active execution requires a new one"
            Run =
              fun () ->
                  let outcome =
                      TelemetryResolution.resolve
                          "TASK-TELEMETRY"
                          [ TelemetryIntent.RecordResumed; TelemetryIntent.EnsureActiveExecution ]
                          (state [ "EXE-finalized" ] [ candidate "EXE-finalized" "TASK-TELEMETRY" ExecutionStatus.Finalized ])

                  Assert.equal
                      (TelemetryResolutionOutcome.PendingNewExecution(
                          [ "EXE-finalized" ],
                          [ TelemetryResolutionStep.NoChange; TelemetryResolutionStep.RequiresNewExecution ],
                          [ TelemetryEffect.CreateExecution ]
                      ))
                      outcome }
          { Name = "completion with a linked execution skips ensure and finalizes it"
            Run =
              fun () ->
                  let outcome =
                      TelemetryResolution.resolve
                          "TASK-TELEMETRY"
                          [ TelemetryIntent.FinalizeExecutions ]
                          (state [ "EXE-a" ] [ candidate "EXE-a" "TASK-TELEMETRY" ExecutionStatus.Active ])

                  Assert.equal
                      (TelemetryResolutionOutcome.Resolved(
                          [ "EXE-a" ],
                          [ TelemetryResolutionStep.Finalized [ "EXE-a" ] ],
                          [ TelemetryEffect.FinalizeExecutions [ "EXE-a" ] ]
                      ))
                      outcome }
          { Name = "completion finalizes an orphaned active execution never explicitly linked"
            Run =
              fun () ->
                  let outcome =
                      TelemetryResolution.resolve
                          "TASK-TELEMETRY"
                          [ TelemetryIntent.EnsureCompletableExecution; TelemetryIntent.FinalizeExecutions ]
                          (state [ "EXE-linked" ] [ candidate "EXE-orphan" "TASK-TELEMETRY" ExecutionStatus.Active ])

                  Assert.equal
                      (TelemetryResolutionOutcome.Resolved(
                          [ "EXE-linked"; "EXE-orphan" ],
                          [ TelemetryResolutionStep.NoChange; TelemetryResolutionStep.Finalized [ "EXE-orphan" ] ],
                          [ TelemetryEffect.FinalizeExecutions [ "EXE-orphan" ] ]
                      ))
                      outcome }
          { Name = "completion with no linked or candidate execution requires a new one before finalizing"
            Run =
              fun () ->
                  let outcome =
                      TelemetryResolution.resolve
                          "TASK-TELEMETRY"
                          [ TelemetryIntent.EnsureCompletableExecution; TelemetryIntent.FinalizeExecutions ]
                          (state [] [])

                  Assert.equal
                      (TelemetryResolutionOutcome.PendingNewExecution(
                          [],
                          [ TelemetryResolutionStep.RequiresNewExecution ],
                          [ TelemetryEffect.CreateExecution ]
                      ))
                      outcome }
          { Name = "block records lifecycle only and never changes execution IDs"
            Run =
              fun () ->
                  let outcome =
                      TelemetryResolution.resolve
                          "TASK-TELEMETRY"
                          [ TelemetryIntent.RecordBlocked "waiting on review" ]
                          (state [ "EXE-a" ] [ candidate "EXE-a" "TASK-TELEMETRY" ExecutionStatus.Active ])

                  Assert.equal
                      (TelemetryResolutionOutcome.Resolved([ "EXE-a" ], [ TelemetryResolutionStep.NoChange ], []))
                      outcome }
          { Name = "resolving a planned transition freezes the item and event telemetry IDs together"
            Run =
              fun () ->
                  let repository: TelemetryStateRepository =
                      { Observe =
                          fun workItemId ->
                              Assert.equal "TASK-TELEMETRY" workItemId
                              state [] [ candidate "EXE-detached" "TASK-TELEMETRY" ExecutionStatus.Active ] }

                  match WorkOperations.resolveTelemetry repository (plan [ TelemetryIntent.EnsureActiveExecution ] []) with
                  | ResolvedTelemetryOutcome.Resolved resolved ->
                      Assert.equal [ "EXE-detached" ] resolved.Item.TelemetryExecutionIds
                      Assert.equal [ "EXE-detached" ] resolved.Event.TelemetryExecutionIds
                  | other -> failwith $"Expected a resolved plan, received {other}" }
          { Name = "resolving a plan pending a new execution reports the work item without a frozen plan"
            Run =
              fun () ->
                  let repository: TelemetryStateRepository = { Observe = fun _ -> state [] [] }

                  Assert.equal
                      (ResolvedTelemetryOutcome.PendingNewExecution "TASK-TELEMETRY")
                      (WorkOperations.resolveTelemetry repository (plan [ TelemetryIntent.EnsureActiveExecution ] []))

                  Assert.equal
                      (ResolvedTelemetryContextOutcome.PendingNewExecution "TASK-TELEMETRY")
                      (WorkOperations.resolveContextTelemetry
                          repository
                          { WorkItems = [ item [] ]
                            ItemPlans = [ plan [ TelemetryIntent.EnsureActiveExecution ] [] ]
                            Repository = "repo"
                            ProtocolVersion = "1.0.0"
                            Actor = "test-agent"
                            StartedAt = None
                            BaselineDirtyPaths = []
                            UpdatedAt = "2026-09-09T00:00:00Z" }) }
          { Name = "resolving a whole context plan updates every item in request order and stops at the first rejection"
            Run =
              fun () ->
                  let second = { item [] with Id = "TASK-SECOND" }
                  let secondPlan =
                      { plan [ TelemetryIntent.EnsureActiveExecution ] [] with
                          Item = second
                          Event = { (plan [] []).Event with WorkItemId = "TASK-SECOND" } }
                  let repository: TelemetryStateRepository =
                      { Observe =
                          fun workItemId ->
                              if workItemId = "TASK-TELEMETRY" then
                                  state [] [ candidate "EXE-first" "TASK-TELEMETRY" ExecutionStatus.Active ]
                              else
                                  state
                                      []
                                      [ candidate "EXE-x" "TASK-SECOND" ExecutionStatus.Active
                                        candidate "EXE-y" "TASK-SECOND" ExecutionStatus.Active ] }
                  let contextPlan =
                      { WorkItems = [ item []; second ]
                        ItemPlans = [ plan [ TelemetryIntent.EnsureActiveExecution ] []; secondPlan ]
                        Repository = "repo"
                        ProtocolVersion = "1.0.0"
                        Actor = "test-agent"
                        StartedAt = None
                        BaselineDirtyPaths = []
                        UpdatedAt = "2026-09-09T00:00:00Z" }

                  Assert.equal
                      (ResolvedTelemetryContextOutcome.Rejected(
                          "TASK-SECOND",
                          TelemetryResolutionRejection.Ambiguous [ "EXE-x"; "EXE-y" ]
                      ))
                      (WorkOperations.resolveContextTelemetry repository contextPlan) }
          { Name = "file telemetry state repository reads only the requested work item's real execution records"
            Run =
              fun () ->
                  let root = Path.Combine(Path.GetTempPath(), $"ros-telemetry-state-{Guid.NewGuid():N}")
                  let executions = Path.Combine(root, ".ros", "telemetry", "executions")
                  Directory.CreateDirectory executions |> ignore

                  try
                      let write executionId workItemId status =
                          File.WriteAllText(
                              Path.Combine(executions, $"{executionId}.json"),
                              $"""{{"executionId":"{executionId}","workItemId":"{workItemId}","status":"{status}"}}"""
                          )

                      write "EXE-a" "TASK-TARGET" "active"
                      write "EXE-b" "TASK-TARGET" "finalized"
                      write "EXE-c" "TASK-OTHER" "active"

                      let candidates = FileTelemetryStateRepository.readCandidates root "TASK-TARGET"

                      Assert.equal
                          [ { ExecutionId = "EXE-a"
                              WorkItemId = "TASK-TARGET"
                              Status = ExecutionStatus.Active }
                            { ExecutionId = "EXE-b"
                              WorkItemId = "TASK-TARGET"
                              Status = ExecutionStatus.Finalized } ]
                          candidates
                  finally
                      Directory.Delete(root, true) }
          { Name = "file telemetry state repository reports no candidates when the executions directory is absent"
            Run =
              fun () ->
                  let root = Path.Combine(Path.GetTempPath(), $"ros-telemetry-state-{Guid.NewGuid():N}")
                  Assert.equal [] (FileTelemetryStateRepository.readCandidates root "TASK-TARGET") } ]
