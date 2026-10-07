namespace Ros.Tests

open System.IO
open System.Text.Json.Nodes
open Ros.Domain.Telemetry

/// Effective-current telemetry (DF-ROS-2026-A042, PRAXIS-CONT-11): step
/// tracking may be adopted partway through an execution. Everything recorded
/// before adoption stays execution-scoped, its step attribution is
/// unavailable (never zero, never redistributed), and neither the execution
/// nor its work item has to restart.
[<RequireQualifiedAccess>]
module TelemetrySegmentationTests =
    open PraxisCli

    let private step stepId startedAt : Step =
        { StepId = stepId
          Name = None
          Status = StepStatus.Running
          StartedAt = startedAt
          EndedAt = None
          Reason = None }

    let private measurement execution stepId value : UsageMeasurement =
        { ExecutionId = execution
          WorkItemId = "WI-1"
          Provider = "p"
          Model = None
          Step = stepId
          CollectedAt = "2026-09-29T10:00:00.000Z"
          MetricId = "tokens.input"
          Value = value
          Unit = "tokens"
          Currency = None
          Quality = EvidenceQuality.ProviderReported }

    let private agentA = agent "anthropic/claude-code" "anthropic" "claude-code" "claude-session-A"
    let private agentB = agent "google/gemini-cli" "google" "gemini-cli" "gemini-session-B"

    let private record clone who (arguments: string list) =
        run clone (Some who) ([ "telemetry"; "record"; "FEAT-7"; "--metric"; "tokens.input"; "--unit"; "tokens"; "--source-type"; "runtime-api"; "--quiet" ] @ arguments)
        |> ok
        |> ignore

    let private stepTransition clone who transition stepId =
        run clone (Some who) [ "telemetry"; "step"; transition; "FEAT-7"; "--step"; stepId; "--occurred-at"; now () ] |> ok |> ignore

    let private usage clone dimension =
        let report = run clone None [ "telemetry"; "usage"; "FEAT-7"; "--by"; dimension ] |> ok
        report.Json["groups"] :?> JsonArray |> Seq.map (fun node -> node :?> JsonObject) |> Seq.filter (fun node -> text node["metric"] = "tokens.input") |> Seq.toList

    let private group (key: string) (groups: JsonObject list) =
        groups |> List.find (fun node -> text node["key"] = key)

    let private strings (node: JsonNode) =
        node :?> JsonArray |> Seq.map text |> Seq.toList

    let private segmentationOf (continuity: JsonNode) (executionId: string) =
        continuity["telemetry"].["executions"] :?> JsonArray
        |> Seq.map (fun node -> node :?> JsonObject)
        |> Seq.find (fun node -> text node["executionId"] = executionId)

    let tests =
        [ { Name = "segmentation: an execution that never recorded a step is execution-level, with step attribution unavailable"
            Run =
              fun () ->
                  let segmentation = TelemetrySegmentation.derive (Some "2026-09-29T09:00:00.000Z") []
                  Assert.equal TelemetrySegmentation.ExecutionLevel segmentation
                  Assert.equal "execution-level" (TelemetrySegmentation.code segmentation)
                  Assert.equal None (TelemetrySegmentation.stepTrackingStartedAt segmentation)
                  Assert.isTrue (TelemetrySegmentation.hasExecutionScopedPeriod segmentation) "unsegmented usage must stay unavailable per step"
                  // Legal history: a measurement with no step never needs one.
                  Assert.empty (TelemetrySegmentation.danglingStepReferences [] [ None; None ]) }
          { Name = "segmentation: adoption mid-execution leaves earlier activity execution-scoped and starts step-level at the first step"
            Run =
              fun () ->
                  let steps = [ step "later" "2026-09-29T11:00:00.000Z"; step "first" "2026-09-29T10:00:00.000Z" ]
                  let adopted = TelemetrySegmentation.derive (Some "2026-09-29T09:00:00.000Z") steps

                  Assert.equal
                      (TelemetrySegmentation.StepLevel("2026-09-29T10:00:00.000Z", PreStepPeriod.ExecutionScopedFrom "2026-09-29T09:00:00.000Z"))
                      adopted

                  Assert.equal "step-level-adopted" (TelemetrySegmentation.code adopted)
                  Assert.isTrue (TelemetrySegmentation.hasExecutionScopedPeriod adopted) "the pre-adoption period lost its execution scope"

                  let fromStart = TelemetrySegmentation.derive (Some "2026-09-29T10:00:00.000Z") steps
                  Assert.equal (TelemetrySegmentation.StepLevel("2026-09-29T10:00:00.000Z", PreStepPeriod.Absent)) fromStart
                  Assert.isTrue (not (TelemetrySegmentation.hasExecutionScopedPeriod fromStart)) "no pre-step period exists"

                  // An unknown start is reported as unknown, not guessed either way.
                  Assert.equal
                      (TelemetrySegmentation.StepLevel("2026-09-29T10:00:00.000Z", PreStepPeriod.Unknown))
                      (TelemetrySegmentation.derive None steps) }
          { Name = "segmentation: a measurement is never moved into a step it was not recorded against"
            Run =
              fun () ->
                  let adopted = TelemetrySegmentation.derive (Some "2026-09-29T09:00:00.000Z") [ step "impl" "2026-09-29T10:00:00.000Z" ]
                  Assert.equal MeasurementScope.ExecutionBeforeSteps (TelemetrySegmentation.scopeOf adopted None "2026-09-29T09:30:00.000Z")
                  Assert.equal MeasurementScope.ExecutionOutsideSteps (TelemetrySegmentation.scopeOf adopted None "2026-09-29T10:30:00.000Z")
                  Assert.equal (MeasurementScope.Step "impl") (TelemetrySegmentation.scopeOf adopted (Some "impl") "2026-09-29T10:30:00.000Z")
                  // An unparseable time cannot place a measurement after adoption.
                  Assert.equal MeasurementScope.ExecutionBeforeSteps (TelemetrySegmentation.scopeOf adopted None "")
                  Assert.equal MeasurementScope.ExecutionBeforeSteps (TelemetrySegmentation.scopeOf TelemetrySegmentation.ExecutionLevel None "2026-09-29T10:30:00.000Z") }
          { Name = "segmentation: unavailable pre-adoption step usage is unknown, never zero, and never redistributed"
            Run =
              fun () ->
                  // EXE-a reported 500 before adopting steps and 200 in 'impl';
                  // EXE-b has an execution-scoped period but reported nothing there.
                  let measurements = [ measurement "EXE-a" None 500.0; measurement "EXE-a" (Some "impl") 200.0 ]

                  let byStep =
                      Usage.aggregate
                          UsageDimension.Step
                          (Map.ofList [ "impl", [ "EXE-a" ]; Usage.outsideAnyStep, [ "EXE-a"; "EXE-b" ] ])
                          measurements

                  let outside = byStep |> List.find (fun group -> group.Key = Usage.outsideAnyStep)
                  let impl = byStep |> List.find (fun group -> group.Key = "impl")
                  Assert.equal (Some 200.0) impl.Total
                  Assert.equal (Some 500.0) outside.Total
                  Assert.equal [ "EXE-a" ] outside.ReportingExecutions
                  Assert.equal [ "EXE-b" ] outside.UnavailableExecutions

                  let silent = Usage.aggregate UsageDimension.Step (Map.ofList [ Usage.outsideAnyStep, [ "EXE-c" ] ]) [ measurement "EXE-z" (Some "x") 1.0 ]
                  let unknown = silent |> List.find (fun group -> group.Key = Usage.outsideAnyStep)
                  Assert.equal None unknown.Total
                  Assert.equal [ "EXE-c" ] unknown.UnavailableExecutions }
          { Name = "segmentation: a step-scoped metric must name a step its own execution started"
            Run =
              fun () ->
                  let steps = [ step "impl" "2026-09-29T10:00:00.000Z" ]
                  Assert.empty (TelemetrySegmentation.danglingStepReferences steps [ None; Some "impl" ])
                  Assert.equal [ 1, "ghost" ] (TelemetrySegmentation.danglingStepReferences steps [ None; Some "ghost"; Some "impl" ]) }
          { Name = "cli segmentation: A adopts steps mid-execution without restart; B continues with its own steps; usage stays separately attributable"
            Run =
              fun () ->
                  let parent = GitFixture.temporaryDirectory "segmentation"

                  try
                      let _, clone = installedRepository parent "clone"
                      run clone (Some agentA) [ "work"; "start"; "--id"; "FEAT-7"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore

                      // Execution A begins unsegmented: usage is execution-scoped.
                      record clone agentA [ "--value"; "500" ]
                      let before = run clone None [ "work"; "context"; "FEAT-7" ] |> ok
                      let executionA = text (before.Json["continuity"].[0].["telemetry"].["executions"].[0].["executionId"])
                      Assert.equal "execution-level" (text (segmentationOf before.Json["continuity"].[0] executionA).["segmentation"])
                      run clone None [ "validate" ] |> ok |> ignore

                      // Step tracking is adopted partway through the same execution.
                      stepTransition clone agentA "start" "impl"
                      record clone agentA [ "--value"; "200"; "--step"; "impl" ]
                      stepTransition clone agentA "complete" "impl"
                      let refused = run clone (Some agentA) [ "telemetry"; "record"; "FEAT-7"; "--metric"; "tokens.input"; "--value"; "1"; "--step"; "never-started" ]
                      Assert.isTrue (refused.ExitCode <> 0) "a metric was attributed to a step that never started"

                      let adopted = run clone None [ "work"; "context"; "FEAT-7" ] |> ok
                      let a = segmentationOf adopted.Json["continuity"].[0] executionA
                      Assert.equal "step-level-adopted" (text a["segmentation"])
                      Assert.equal (text a["startedAt"]) (text a["executionScopedBefore"].["from"])
                      Assert.equal (text a["stepTrackingStartedAt"]) (text a["executionScopedBefore"].["until"])
                      Assert.equal "unavailable" (text a["historicalStepAttribution"])
                      Assert.equal 1 (a["measurements"].["stepScoped"].GetValue<int>())
                      Assert.isTrue (a["measurements"].["executionScopedBeforeSteps"].GetValue<int>() >= 1) "pre-adoption usage lost its execution scope"
                      // The same execution, never restarted, and history still validates.
                      Assert.equal "active" (text a["status"])
                      run clone None [ "validate" ] |> ok |> ignore

                      let human = run clone None [ "work"; "context"; "FEAT-7"; "--text"; "--offline" ] |> ok
                      Assert.isTrue (human.Output.Contains "TELEMETRY SEGMENTATION") human.Output
                      let startedAt = text a["startedAt"]
                      let adoptedAt = text a["stepTrackingStartedAt"]
                      Assert.isTrue (human.Output.Contains $"execution-level before: {startedAt}") human.Output
                      Assert.isTrue (human.Output.Contains $"step-level from: {adoptedAt}") human.Output

                      // Pre-adoption usage is not redistributed into the step.
                      let stepUsage = usage clone "step"
                      let total (node: JsonObject) = node["total"].GetValue<float>()
                      Assert.equal 200.0 (total (group "impl" stepUsage))
                      Assert.equal 500.0 (total (group Usage.outsideAnyStep stepUsage))

                      // A commits, pushes and checkpoints against its step, then is lost.
                      GitFixture.write clone "src/slice-a.txt" "slice a\n"
                      pushAll clone "Implement slice A" |> ignore

                      run clone (Some agentA) [ "work"; "checkpoint"; "--id"; "FEAT-7"; "--occurred-at"; now (); "--summary"; "Slice A"; "--next-action"; "Slice B"; "--step"; "impl"; "--json" ]
                      |> ok
                      |> ignore

                      pushAll clone "praxis: checkpoint A" |> ignore

                      // B (another provider) continues: a new execution with its own steps.
                      let continued = run clone (Some agentB) [ "work"; "continue"; "--id"; "FEAT-7"; "--occurred-at"; now (); "--json" ] |> ok
                      let executionB = text continued.Json["executionId"]
                      Assert.isTrue (executionB <> executionA) "B reused A's execution"
                      Assert.equal [ "impl" ] (continued.Json["predecessor"].["steps"] :?> JsonArray |> Seq.map (fun node -> text node["stepId"]) |> Seq.toList)
                      // The continuation view keeps A's segmentation as recorded.
                      Assert.equal "step-level-adopted" (text (segmentationOf continued.Json["continuity"] executionA).["segmentation"])
                      Assert.equal "execution-level" (text (segmentationOf continued.Json["continuity"] executionB).["segmentation"])

                      stepTransition clone agentB "start" "slice-b"
                      record clone agentB [ "--value"; "300"; "--step"; "slice-b" ]
                      stepTransition clone agentB "complete" "slice-b"

                      // B's steps belong to B only; A's record gained nothing.
                      let stepsOf (executionId: string) =
                          let json = JsonNode.Parse(File.ReadAllText(Path.Combine(clone, ".ros", "telemetry", "executions", $"{executionId}.json")))

                          json["events"] :?> JsonArray
                          |> Seq.filter (fun node -> text node["type"] = "step.started")
                          |> Seq.map (fun node -> text node["stepId"])
                          |> Seq.toList

                      Assert.equal [ "impl" ] (stepsOf executionA)
                      Assert.equal [ "slice-b" ] (stepsOf executionB)

                      let byExecution = usage clone "execution"
                      Assert.equal 700.0 (total (group executionA byExecution))
                      Assert.equal 300.0 (total (group executionB byExecution))

                      let byStep = usage clone "step"
                      let reporting key = strings (group key byStep).["reportingExecutions"]
                      Assert.equal [ executionB ] (reporting "slice-b")
                      Assert.equal [ executionA ] (reporting "impl")
                      // B's own pre-step period reported nothing: unknown, not zero.
                      let outside = group Usage.outsideAnyStep byStep
                      Assert.equal 500.0 (outside["total"].GetValue<float>())
                      Assert.equal [ executionA ] (strings outside["reportingExecutions"])
                      Assert.equal [ executionB ] (strings outside["unavailableExecutions"])
                      Assert.isTrue (not (outside["complete"].GetValue<bool>())) "an unavailable execution was counted as zero"

                      let final = run clone None [ "work"; "context"; "FEAT-7" ] |> ok
                      Assert.equal "step-level-adopted" (text (segmentationOf final.Json["continuity"].[0] executionB).["segmentation"])
                      run clone None [ "validate" ] |> ok |> ignore
                  finally
                      GitFixture.cleanup parent } ]
