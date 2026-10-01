namespace Praxis.Tests

open Praxis.Contracts.Planning
open Praxis.Domain.Planning
open PlanningFixtures

/// PRAXIS-PLAN-05: the planner consumes observed cost, down-weights the
/// ROS-wall-time duration model for implementation work, and reports
/// measured context overhead (EV-ROS-2026-A064).
module PlanningSessionEvidenceTests =
    let private t name run = { Name = $"planning session evidence: {name}"; Run = run }

    let private cost metricId amount kind =
        { MetricId = metricId
          Amount = amount
          Currency = Some "USD"
          Kind = kind }

    let private withCosts costs (execution: HistoricalExecution) = { execution with Costs = costs }

    /// A session that ran `activeMinutes` and reached its first code change
    /// after `coldStartMinutes`.
    let private withSession activeMinutes coldStartMinutes (execution: HistoricalExecution) =
        { execution with
            Session =
                { ActiveMs = Some(activeMinutes * minute)
                  FirstCodeChangeMs = Some(coldStartMinutes * minute)
                  GovernanceReads = Some 4
                  RepeatedReads = Some 2 } }

    let private development (analysis: PlanningAnalysis) =
        analysis.History.Distributions |> List.find (fun distribution -> distribution.TaskClass = "development")

    let tests =
        [ t "cost.execution_total is summed and wins over components and the session gauge" (fun () ->
              let execution =
                  executed 0 "A" "development" 10L
                  |> withCosts
                      [ cost "cost.input" 0.5m CostEvidenceKind.Calculated
                        cost "cost.session_cumulative" 7m CostEvidenceKind.Estimated
                        cost "cost.execution_total" 1m CostEvidenceKind.Observed
                        cost "cost.execution_total" 2m CostEvidenceKind.Observed ]

              Assert.equal (Some(3m, Some "USD")) (History.executionCost execution)
              Assert.equal CostEvidenceKind.Observed (History.executionCostKind execution))

          t "a cumulative session cost is never added to cost components" (fun () ->
              let components =
                  executed 0 "A" "development" 10L
                  |> withCosts [ cost "cost.input" 0.5m CostEvidenceKind.Calculated; cost "cost.session_cumulative" 7m CostEvidenceKind.Estimated ]

              let sessionOnly = executed 1 "B" "development" 10L |> withCosts [ cost "cost.session_cumulative" 7m CostEvidenceKind.Estimated ]
              Assert.equal (Some(0.5m, Some "USD")) (History.executionCost components)
              Assert.equal (Some(7m, Some "USD")) (History.executionCost sessionOnly)
              Assert.equal CostEvidenceKind.Estimated (History.executionCostKind sessionOnly))

          t "an item with observed platform cost reports observed cost evidence" (fun () ->
              let observed = history |> List.map (withCosts [ cost "cost.execution_total" 9.48m CostEvidenceKind.Observed ])
              let own = executed 30 "A" "development" 20L |> withCosts [ cost "cost.execution_total" 9.48m CostEvidenceKind.Observed ]

              let analysis =
                  analyze [ queued "A" "ready" "2026-09-01T00:00:00Z"; queued "B" "ready" "2026-09-02T00:00:00Z" ] [] (own :: observed) [] PlannerConfiguration.defaults

              Assert.isTrue analysis.History.Cost.Sufficient "observed cost must make monetary optimization available"
              Assert.equal CostEvidenceKind.Observed (item analysis "A").CostEvidence
              Assert.equal CostEvidenceKind.Estimated (item analysis "B").CostEvidence
              Assert.isTrue (item analysis "B").RemainingCost.Expected.IsSome "cost must be estimated, not unknown"
              Assert.isTrue (not (analysis.Findings |> List.exists (fun finding -> finding.Code = FindingCode.MonetaryOptimizationUnavailable))) "no cost-unavailable finding")

          t "implementation estimates from ROS wall time alone lose one confidence level and say why" (fun () ->
              let analysis = analyze [ queued "A" "ready" "2026-09-01T00:00:00Z" ] [] history [] PlannerConfiguration.defaults
              let distribution = development analysis
              Assert.equal EvidenceConfidence.High distribution.Confidence
              Assert.equal 0 distribution.SessionMeasured
              let estimate, basis = History.estimateFor analysis.History.Distributions "development"
              Assert.equal EvidenceConfidence.Medium estimate.Confidence
              Assert.equal (Some distribution.Median) estimate.Expected
              Assert.isTrue (basis.Contains "EV-ROS-2026-A064") "the explanation must cite the evidence"
              Assert.isTrue (basis.Contains "0 of 24 samples carry runtime-measured session time") "the explanation must say what is missing")

          t "plan explain states the lowered implementation confidence" (fun () ->
              let analysis = analyze [ queued "A" "ready" "2026-09-01T00:00:00Z" ] [] history [] PlannerConfiguration.defaults

              match Planner.explain analysis PlannerConfiguration.defaults None "A" with
              | Error message -> failwith message
              | Ok explanation ->
                  Assert.isTrue (explanation.EstimateBasis |> List.exists (fun line -> line.Contains "confidence lowered one level for implementation work")) "explanation must carry the down-weight")

          t "non-implementation classes keep their confidence" (fun () ->
              let documentation = [ for index in 0..23 -> executed index $"DOC-{index}" "documentation" (10L + int64 index) ]
              let estimate, basis = History.estimateFor (History.distributions (History.samples documentation)) "documentation"
              Assert.equal EvidenceConfidence.High estimate.Confidence
              Assert.isTrue (not (basis.Contains "lowered")) "no down-weight for documentation")

          t "runtime-measured session time replaces shorter ROS wall time and restores confidence" (fun () ->
              let measured = history |> List.map (withSession 60L 6L)
              let distributions = History.distributions (History.samples measured)
              let distribution = distributions |> List.find (fun entry -> entry.TaskClass = "development")
              Assert.equal 24 distribution.SessionMeasured
              Assert.equal (60L * minute) distribution.Median
              let estimate, basis = History.estimateFor distributions "development"
              Assert.equal EvidenceConfidence.High estimate.Confidence
              Assert.isTrue (not (basis.Contains "lowered")) "no down-weight once sessions are measured")

          t "session time shorter than ROS wall time never shortens productive time" (fun () ->
              let execution = executed 0 "A" "development" 30L |> withSession 5L 1L
              Assert.equal (Some(30L * minute, false)) (History.productive execution))

          t "context reuse stays unmeasured below three sessions" (fun () ->
              let executions = history |> List.mapi (fun index execution -> if index < 2 then withSession 20L 5L execution else execution)
              let overhead = History.contextOverhead executions
              Assert.equal 2 overhead.SampledSessions
              Assert.isTrue (not overhead.Sufficient) "two sessions are not enough"
              Assert.equal Estimate.unknown overhead.ColdStart
              Assert.isTrue (overhead.Statement.Contains "unmeasured") "statement must say unmeasured")

          t "measured cold starts replace unknown context-reuse value" (fun () ->
              let executions = history |> List.mapi (fun index execution -> withSession 30L (4L + int64 (index % 4)) execution)
              let analysis = analyze [ queued "A" "ready" "2026-09-01T00:00:00Z" ] [] executions [] PlannerConfiguration.defaults
              let overhead = analysis.History.ContextOverhead
              Assert.isTrue overhead.Sufficient "24 sessions are enough"
              Assert.equal (Some(5L * minute)) overhead.ColdStart.Expected
              Assert.equal (Some 4) overhead.MedianGovernanceReads
              Assert.equal (Some 2) overhead.MedianRepeatedReads
              Assert.equal overhead.Statement (Grouping.contextNote analysis.History)
              let saving = Grouping.coldStartSaving analysis.History 4
              Assert.isTrue (saving.Contains "about 20 min") $"four avoided cold starts at a 5 min median: {saving}"
              Assert.isTrue (not (analysis.EvidenceRecommendations |> List.exists (fun line -> line.Contains "anthropic-claude-session"))) "no ingest recommendation once measured")

          t "analysis JSON reports session-measured samples and context overhead" (fun () ->
              let analysis = analyze [ queued "A" "ready" "2026-09-01T00:00:00Z" ] [] history [] PlannerConfiguration.defaults
              let json = PlanningJson.render (PlanningJson.analysis analysis)
              Assert.isTrue (json.Contains "\"sessionMeasured\"") "distributions carry sessionMeasured"
              Assert.isTrue (json.Contains "\"contextOverhead\"") "history carries contextOverhead"
              Assert.isTrue (analysis.EvidenceRecommendations |> List.exists (fun line -> line.Contains "anthropic-claude-session")) "unmeasured context recommends the adapter") ]
