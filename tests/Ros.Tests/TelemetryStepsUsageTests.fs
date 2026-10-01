namespace Ros.Tests

open Ros.Domain.Remote
open Ros.Domain.Telemetry

/// Steps inside executions, the evidence-quality projection, and usage
/// aggregation (PRAXIS-REMOTE-04, `PRX-REMOTE-008..010`).
[<RequireQualifiedAccess>]
module TelemetryStepsUsageTests =
    let private event transition stepId at : StepEvent =
        { Transition = transition
          StepId = stepId
          Name = None
          Reason = None
          OccurredAt = at }

    let private measurement execution provider model step metric value quality : UsageMeasurement =
        { ExecutionId = execution
          WorkItemId = "WI-1"
          Provider = provider
          Model = model
          Step = step
          CollectedAt = "2026-09-28T10:00:00.000Z"
          MetricId = metric
          Value = value
          Unit = "tokens"
          Currency = None
          Quality = quality }

    let tests =
        [ { Name = "steps: a step runs until it completes or fails, and its lifecycle is projected from events"
            Run =
              fun () ->
                  let steps =
                      Steps.project
                          [ event StepTransition.Start "plan" "t1"
                            event StepTransition.Start "build" "t2"
                            event StepTransition.Complete "plan" "t3"
                            event StepTransition.Fail "build" "t4" ]

                  Assert.equal [ "plan", EventStepStatus.Completed, Some "t3"; "build", EventStepStatus.Failed, Some "t4" ] (steps |> List.map (fun step -> step.StepId, step.Status, step.EndedAt)) }

          { Name = "steps: repeating a transition is an idempotent no-op; an illegal one is refused, never coerced"
            Run =
              fun () ->
                  let started = [ event StepTransition.Start "plan" "t1" ]
                  Assert.equal StepDecision.AlreadyRecorded (Steps.decide started (event StepTransition.Start "plan" "t9"))
                  Assert.equal (StepDecision.Record(event StepTransition.Complete "plan" "t2")) (Steps.decide started (event StepTransition.Complete "plan" "t2"))

                  let completed = started @ [ event StepTransition.Complete "plan" "t2" ]
                  Assert.equal StepDecision.AlreadyRecorded (Steps.decide completed (event StepTransition.Complete "plan" "t3"))

                  match Steps.decide completed (event StepTransition.Fail "plan" "t3"), Steps.decide [] (event StepTransition.Complete "ghost" "t1"), Steps.decide [] (event StepTransition.Start "--x" "t1") with
                  | StepDecision.Rejected _, StepDecision.Rejected _, StepDecision.Rejected _ -> ()
                  | other -> failwith $"expected three refusals, got {other}" }

          { Name = "evidence quality: the #90 vocabulary is projected from quality and source, never upgraded"
            Run =
              fun () ->
                  [ ("observed", "ros-git"), EvidenceQuality.Measured
                    ("observed", "ros-clock"), EvidenceQuality.Measured
                    ("observed", "runtime-api"), EvidenceQuality.ProviderReported
                    ("observed", "runtime-output"), EvidenceQuality.ProviderReported
                    ("observed", "agent-report"), EvidenceQuality.AgentReported
                    ("observed", "human-report"), EvidenceQuality.HumanReported
                    ("derived", "calculated"), EvidenceQuality.Calculated
                    ("observed", "calculated"), EvidenceQuality.Calculated
                    ("estimated", "runtime-api"), EvidenceQuality.Estimated
                    ("estimated", "ros-git"), EvidenceQuality.Estimated
                    ("observed", "something-new"), EvidenceQuality.Unclassified ]
                  |> List.iter (fun ((quality, source), expected) -> Assert.equal expected (EvidenceQuality.classify quality source)) }

          { Name = "usage: totals keep their evidence, and executions that reported nothing stay unavailable, never zero"
            Run =
              fun () ->
                  let measurements =
                      [ measurement "EXE-a" "p1" (Some "m1") (Some "plan") "tokens.input" 100.0 EvidenceQuality.ProviderReported
                        measurement "EXE-a" "p1" (Some "m1") None "tokens.input" 50.0 EvidenceQuality.AgentReported ]

                  let byProvider =
                      Usage.aggregate UsageDimension.Provider (Map.ofList [ "p1", [ "EXE-a" ]; "p2", [ "EXE-b" ] ]) measurements

                  match byProvider with
                  | [ p1; p2 ] ->
                      Assert.equal (Some 150.0) p1.Total
                      Assert.equal [ "agent-reported", 1; "provider-reported", 1 ] p1.Qualities
                      Assert.empty p1.UnavailableExecutions
                      Assert.equal None p2.Total
                      Assert.equal [ "EXE-b" ] p2.UnavailableExecutions
                  | other -> failwith $"unexpected {other}"

                  let byStep = Usage.aggregate UsageDimension.Step (Map.ofList [ "plan", [ "EXE-a" ] ]) measurements
                  Assert.equal [ "(outside any step)", Some 50.0; "plan", Some 100.0 ] (byStep |> List.map (fun group -> group.Key, group.Total))

                  let byModel = Usage.aggregate UsageDimension.Model Map.empty [ measurement "EXE-c" "p3" None None "tokens.input" 7.0 EvidenceQuality.Estimated ]
                  Assert.equal [ "unknown" ] (byModel |> List.map _.Key) }

          { Name = "remote 1.1: step operations need the requester's execution and are unknown to a 1.0 request"
            Run =
              fun () ->
                  let envelope version (extra: string) =
                      $$"""{"protocol":"praxis.remote","protocolVersion":"{{version}}","requestId":"req-step-0001","operation":"step.start","repository":{"ref":"refs/heads/main","expectedSha":"59b4e032818a4c765886e48c117595dc58019d43"},"arguments":{"stepId":"plan"}{{extra}}}"""

                  match Ros.Contracts.Remote.RemoteJson.parseRequest ProtocolVersion.current (envelope "1.0" "") with
                  | Error failure -> Assert.equal FailureCode.UnsupportedOperation failure.Failure.Code
                  | Ok _ -> failwith "a 1.0 request cannot use a 1.1 operation"

                  match Ros.Contracts.Remote.RemoteJson.parseRequest ProtocolVersion.current (envelope "1.1" "") with
                  | Ok request -> Assert.isTrue (RequestValidation.problems request |> List.exists (fun problem -> problem.Field = "execution.id")) "execution.id required"
                  | Error failure -> failwith $"{failure.Failure.Problems}"

                  match Ros.Contracts.Remote.RemoteJson.parseRequest ProtocolVersion.current (envelope "1.1" ""","execution":{"id":"EXE-1"}""") with
                  | Ok request ->
                      Assert.empty (RequestValidation.problems request)

                      match ExecutionPlan.forRequest "2026-09-28T10:00:00.000Z" request with
                      | ExecutionPlan.Command arguments ->
                          Assert.equal [ "telemetry"; "step"; "start"; "EXE-1"; "--step"; "plan"; "--occurred-at"; "2026-09-28T10:00:00.000Z" ] arguments
                      | other -> failwith $"unexpected {other}"
                  | Error failure -> failwith $"{failure.Failure.Problems}" } ]
