namespace Ros.Tests

open Ros.Application.Telemetry
open Ros.Domain.Telemetry

[<RequireQualifiedAccess>]
module TelemetryTests =
    let private candidate executionId workItemId status =
        { ExecutionId = executionId
          WorkItemId = workItemId
          Status = status }

    let private request linked statuses requested candidates =
        { WorkItemId = "WI-TARGET"
          LinkedExecutionIds = Set.ofList linked
          RecoverableStatuses = Set.ofList statuses
          RequestedExecutionId = requested
          Candidates = candidates }

    let tests =
        [ { Name = "execution-link recovery starts new when no detached candidate exists"
            Run = fun () ->
                let decision =
                    request
                        [ "EXE-linked" ]
                        [ ExecutionStatus.Active ]
                        None
                        [ candidate "EXE-linked" "WI-TARGET" ExecutionStatus.Active
                          candidate "EXE-other" "WI-OTHER" ExecutionStatus.Active ]
                    |> TelemetryOperations.decideExecutionLink

                Assert.equal ExecutionLinkDecision.StartNew decision }
          { Name = "execution-link recovery adopts one detached active record"
            Run = fun () ->
                let decision =
                    request [] [ ExecutionStatus.Active ] None [ candidate "EXE-detached" "WI-TARGET" ExecutionStatus.Active ]
                    |> TelemetryOperations.decideExecutionLink

                Assert.equal (ExecutionLinkDecision.Recover "EXE-detached") decision }
          { Name = "execution-link recovery can include finalized evidence for transition repair"
            Run = fun () ->
                let decision =
                    request
                        []
                        [ ExecutionStatus.Active; ExecutionStatus.Finalized ]
                        None
                        [ candidate "EXE-final" "WI-TARGET" ExecutionStatus.Finalized ]
                    |> TelemetryOperations.decideExecutionLink

                Assert.equal (ExecutionLinkDecision.Recover "EXE-final") decision }
          { Name = "execution-link recovery rejects ambiguous detached evidence"
            Run = fun () ->
                let decision =
                    request
                        []
                        [ ExecutionStatus.Active ]
                        None
                        [ candidate "EXE-z" "WI-TARGET" ExecutionStatus.Active
                          candidate "EXE-a" "WI-TARGET" ExecutionStatus.Active ]
                    |> TelemetryOperations.decideExecutionLink

                Assert.equal (ExecutionLinkDecision.RejectAmbiguous [ "EXE-a"; "EXE-z" ]) decision }
          { Name = "execution-link recovery selects an explicitly requested detached record"
            Run = fun () ->
                let decision =
                    request
                        []
                        [ ExecutionStatus.Active ]
                        (Some "EXE-z")
                        [ candidate "EXE-z" "WI-TARGET" ExecutionStatus.Active
                          candidate "EXE-a" "WI-TARGET" ExecutionStatus.Active ]
                    |> TelemetryOperations.decideExecutionLink

                Assert.equal (ExecutionLinkDecision.Recover "EXE-z") decision }
          { Name = "execution-link recovery rejects a new requested ID while detached evidence exists"
            Run = fun () ->
                let decision =
                    request
                        []
                        [ ExecutionStatus.Active ]
                        (Some "EXE-new")
                        [ candidate "EXE-old" "WI-TARGET" ExecutionStatus.Active ]
                    |> TelemetryOperations.decideExecutionLink

                Assert.equal (ExecutionLinkDecision.RejectDetachedConflict [ "EXE-old" ]) decision } ]
