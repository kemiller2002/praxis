namespace Ros.Tests

open System
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Work

/// Pure planner tests (requirements/PLANNING-OPTIMIZATION.md, section 23)
/// and the regression scenarios observed in Praxis itself (section 25).
module PlanningFixtures =
    let minute = 60_000L

    let queued id status created =
        { Id = id
          Title = id
          Description = Some $"Deliver {id}."
          Tags = [ $"area-{id.ToLowerInvariant()}" ]
          Priority = None
          Status = status
          CreatedAt = Some created
          DependsOn = [] }

    let live id state =
        { Id = id
          State = state
          BlockReason = None
          UpdatedAt = None
          Checkpoint = None
          TelemetryExecutionIds = [] }

    let checkpoint (branch: string) (commit: string) (nextAction: string) =
        { CheckpointId = "cp-" + commit.Substring(0, 6)
          ExecutionId = "EXE-1"
          RecordedAt = "2026-09-20T00:00:00.000Z"
          Branch = branch
          Commit = commit
          Summary = "implementation and tests done"
          NextAction = nextAction
          Verified = true }

    /// A finalized execution whose productive time is `minutes`.
    let executed (index: int) (workItem: string) (taskClass: string) (minutes: int64) =
        let started = DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddHours(float index)

        { ExecutionId = $"EXE-{index:D4}"
          WorkItemId = workItem
          Status = ExecutionStatus.Finalized
          StartedAt = started.ToString("O")
          FinalizedAt = Some(started.AddMinutes(float minutes).ToString("O"))
          Classes = [ taskClass ]
          WallMs = Some(minutes * minute)
          BlockedMs = Some 0L
          Provider = "provider-a"
          Runtime = "runtime-a"
          Model = None
          Costs = []
          TokenMetrics = 0
          Session = SessionEvidence.none }

    /// Twenty-four finalized development executions of 10..33 minutes.
    let history = [ for index in 0..23 -> executed index $"HIST-{index}" "development" (10L + int64 index) ]

    let withCost (amount: decimal) (execution: HistoricalExecution) =
        { execution with Costs = [ { MetricId = "cost.execution_total"; Amount = amount; Currency = Some "USD"; Kind = CostEvidenceKind.Observed } ] }

    let input queue liveItems executions observations configuration =
        { Repository = "fixture"
          Commit = Some "1111111111111111111111111111111111111111"
          Branch = Some "main"
          PlannedAt = "2026-09-29T00:00:00.000Z"
          PlannerVersion = "test"
          Queue = queue
          Live = liveItems
          Executions = executions
          Observations = observations
          Configuration = configuration }

    let stateSafe = { PlannerConfiguration.defaults with PraxisStateMergeSafe = true }

    let analyze queue liveItems executions observations configuration =
        Planner.analyze (input queue liveItems executions observations configuration)

    let item (analysis: PlanningAnalysis) id = analysis.Items |> List.find (fun entry -> entry.Id = id)

    let simulate (analysis: PlanningAnalysis) configuration objective limit =
        Scheduling.simulate analysis configuration objective limit

    let executionWaves (plan: ExecutionPlan) =
        plan.Waves |> List.filter (fun wave -> wave.Kind = WaveKind.Execution) |> List.map (fun wave -> wave.Entries |> List.map (fun entry -> entry.WorkItem))

    let scheduled (plan: ExecutionPlan) = executionWaves plan |> List.concat

    let waveOf (plan: ExecutionPlan) id =
        plan.Waves |> List.tryFind (fun wave -> wave.Entries |> List.exists (fun entry -> entry.WorkItem = id)) |> Option.map (fun wave -> wave.Number)

    let hasFinding code id (analysis: PlanningAnalysis) =
        analysis.Findings |> List.exists (fun finding -> finding.Code = code && finding.WorkItems |> List.contains id)

    let reasonsOf (plan: ExecutionPlan) id =
        plan.Waves |> List.collect (fun wave -> wave.Entries) |> List.filter (fun entry -> entry.WorkItem = id) |> List.collect (fun entry -> entry.Reasons)

open PlanningFixtures

module PlanningTests =
    let private t name run = { Name = $"planning: {name}"; Run = run }

    let private mixed () =
        analyze
            [ queued "A" "ready" "2026-09-01T00:00:00Z"
              queued "B" "ready" "2026-09-02T00:00:00Z"
              queued "C" "captured" "2026-09-03T00:00:00Z"
              { queued "D" "ready" "2026-09-04T00:00:00Z" with DependsOn = [ "A" ] } ]
            [ live "E" LiveWorkState.Active ]
            history
            []
            PlannerConfiguration.defaults

    let tests =
        [ t "1 deterministic output" (fun () ->
              let first = mixed ()
              let second = mixed ()
              Assert.equal first second
              let render analysis = PlanningJson.render (PlanningJson.plan (Planner.plan analysis PlannerConfiguration.defaults OptimizationObjective.Balanced None))
              Assert.equal (render first) (render second))

          t "2 inventory is the union of queue and live context" (fun () ->
              let analysis = analyze [ queued "Q" "ready" "2026-09-01T00:00:00Z" ] [ live "L" LiveWorkState.Ready ] [] [] PlannerConfiguration.defaults
              Assert.equal [ "L"; "Q" ] (analysis.Items |> List.map (fun entry -> entry.Id)))

          t "3 a live-only item is planned, not lost" (fun () ->
              let analysis = analyze [] [ live "LIVE-ONLY" LiveWorkState.Active ] [] [] PlannerConfiguration.defaults
              let entry = item analysis "LIVE-ONLY"
              Assert.isTrue (not entry.InQueue && entry.InContext) "live-only item must be in the inventory"
              Assert.equal PlanningWorkState.Active entry.PlanningState
              Assert.isTrue (scheduled (simulate analysis PlannerConfiguration.defaults OptimizationObjective.Baseline None) |> List.contains "LIVE-ONLY") "live-only item must be schedulable")

          t "4 completed items are excluded" (fun () ->
              let analysis = analyze [ queued "DONE" "ready" "2026-09-01T00:00:00Z" ] [ live "DONE" LiveWorkState.Complete ] [] [] PlannerConfiguration.defaults
              Assert.equal PlanningWorkState.Complete (item analysis "DONE").PlanningState
              let plan = simulate analysis PlannerConfiguration.defaults OptimizationObjective.Balanced None
              Assert.empty (scheduled plan)
              Assert.empty plan.Blocked)

          t "5 abandoned items are excluded" (fun () ->
              let analysis = analyze [ queued "GONE" "abandoned" "2026-09-01T00:00:00Z" ] [] [] [] PlannerConfiguration.defaults
              Assert.equal PlanningWorkState.Abandoned (item analysis "GONE").PlanningState
              let plan = simulate analysis PlannerConfiguration.defaults OptimizationObjective.MinimumDuration None
              Assert.empty (scheduled plan)
              Assert.empty plan.NeedsTriage)

          t "6 captured items are not runnable and land in triage" (fun () ->
              let analysis = analyze [ { queued "CAP" "captured" "2026-09-01T00:00:00Z" with Description = None } ] [] history [] PlannerConfiguration.defaults
              let plan = simulate analysis PlannerConfiguration.defaults OptimizationObjective.MaximumSafeParallelism None
              Assert.empty (scheduled plan)
              let triage = Assert.single plan.NeedsTriage
              Assert.equal "CAP" triage.WorkItem
              Assert.isTrue ((item analysis "CAP").Triage |> List.contains TriageNeed.LacksAcceptanceCriteria) "missing description must be reported")

          t "7 blocked items are not scheduled" (fun () ->
              let blocked = { live "BLK" LiveWorkState.Blocked with BlockReason = Some "waiting on a design decision" }
              let analysis = analyze [ queued "BLK" "ready" "2026-09-01T00:00:00Z" ] [ blocked ] history [] PlannerConfiguration.defaults
              let plan = simulate analysis PlannerConfiguration.defaults OptimizationObjective.MinimumDuration None
              Assert.empty (scheduled plan)
              Assert.equal [ "BLK" ] (plan.Blocked |> List.map (fun entry -> entry.WorkItem)))

          t "8 a completed dependency no longer blocks" (fun () ->
              let analysis =
                  analyze
                      [ { queued "DOWN" "ready" "2026-09-02T00:00:00Z" with DependsOn = [ "UP" ] }; queued "UP" "ready" "2026-09-01T00:00:00Z" ]
                      [ live "UP" LiveWorkState.Complete ]
                      history
                      []
                      PlannerConfiguration.defaults

              let plan = simulate analysis PlannerConfiguration.defaults OptimizationObjective.Baseline None
              Assert.equal (Some 1) (waveOf plan "DOWN")
              Assert.isTrue (hasFinding FindingCode.DependencySatisfied "DOWN" analysis) "satisfied dependency must be reported")

          t "9 unresolved hard dependencies are enforced" (fun () ->
              let analysis =
                  analyze
                      [ { queued "DOWN" "ready" "2026-09-01T00:00:00Z" with DependsOn = [ "UP" ] }
                        queued "UP" "ready" "2026-09-02T00:00:00Z"
                        { queued "STUCK" "ready" "2026-09-03T00:00:00Z" with DependsOn = [ "CAP" ] }
                        queued "CAP" "captured" "2026-09-04T00:00:00Z" ]
                      []
                      history
                      []
                      stateSafe

              for objective in [ OptimizationObjective.Baseline; OptimizationObjective.MinimumDuration; OptimizationObjective.MaximumSafeParallelism ] do
                  let plan = simulate analysis stateSafe objective None
                  Assert.isTrue ((waveOf plan "UP").Value < (waveOf plan "DOWN").Value) "prerequisite must run in an earlier wave"
                  Assert.isTrue (not (scheduled plan |> List.contains "STUCK")) "item waiting on captured work must not be scheduled"
                  Assert.isTrue (plan.Blocked |> List.exists (fun entry -> entry.WorkItem = "STUCK")) "it must be reported as blocked")

          t "10 dependency cycles are findings, not arbitrary order" (fun () ->
              let analysis =
                  analyze
                      [ { queued "X" "ready" "2026-09-01T00:00:00Z" with DependsOn = [ "Y" ] }; { queued "Y" "ready" "2026-09-02T00:00:00Z" with DependsOn = [ "X" ] } ]
                      []
                      history
                      []
                      PlannerConfiguration.defaults

              Assert.equal [ [ "X"; "Y" ] ] (Graph.cycles analysis.Items)
              Assert.isTrue (hasFinding FindingCode.DependencyCycle "X" analysis) "cycle finding expected"
              let plan = simulate analysis PlannerConfiguration.defaults OptimizationObjective.Balanced None
              Assert.empty (scheduled plan)
              Assert.isTrue (plan.Blocked |> List.forall (fun entry -> entry.Reasons |> List.exists (fun reason -> reason.Code = ReasonCode.DependencyCycle))) "cycle reason expected")

          t "11 stale blockers need evidence; age alone is not enough" (fun () ->
              let blocked = { live "WAIT" LiveWorkState.Blocked with BlockReason = Some "awaiting merge of PR #12"; UpdatedAt = Some "2020-01-01T00:00:00Z" }
              let queue = [ queued "WAIT" "ready" "2026-09-01T00:00:00Z" ]
              let without = analyze queue [ blocked ] history [] PlannerConfiguration.defaults
              Assert.isTrue ((item without "WAIT").PlanningState <> PlanningWorkState.StaleStateCandidate) "an old blocker is not evidence of staleness"
              let merged = [ { Kind = ObservationKind.PullRequestMerged 12; Provenance = Provenance.create EvidenceSource.Git "git log main" } ]
              let observed = analyze queue [ blocked ] history merged PlannerConfiguration.defaults
              Assert.equal PlanningWorkState.StaleStateCandidate (item observed "WAIT").PlanningState
              Assert.isTrue (hasFinding FindingCode.StaleBlocker "WAIT" observed) "stale-blocker finding expected"
              Assert.equal LiveWorkState.Blocked blocked.State)

          t "12 a verified checkpoint reduces remaining work" (fun () ->
              let near = { live "NEAR" LiveWorkState.Active with Checkpoint = Some(checkpoint "feature/near" (String('a', 40)) "Record completion with implementation evidence") }
              let analysis = analyze [ queued "NEAR" "ready" "2026-09-01T00:00:00Z"; queued "FRESH" "ready" "2026-09-02T00:00:00Z" ] [ near ] history [] PlannerConfiguration.defaults
              let nearItem, fresh = item analysis "NEAR", item analysis "FRESH"
              Assert.equal RemainingBasis.FinalizationOnly nearItem.Basis
              Assert.equal PlanningWorkState.PartiallyComplete nearItem.PlanningState
              Assert.isTrue (nearItem.RemainingDuration.Upper.Value < fresh.RemainingDuration.Upper.Value) "continuation must be cheaper than a fresh start"
              Assert.isTrue (analysis |> hasFinding FindingCode.ResumableExecution "NEAR") "resumable execution must be recognised")

          t "13 missing cost stays unknown, never zero" (fun () ->
              let analysis = mixed ()
              Assert.isTrue (analysis.Items |> List.forall (fun entry -> entry.RemainingCost.Expected.IsNone && entry.RemainingCost.Lower.IsNone)) "item cost must be unknown"
              let plan = simulate analysis PlannerConfiguration.defaults OptimizationObjective.Balanced None
              Assert.equal Estimate.unknown plan.ExpectedCost
              Assert.isTrue (analysis.Items |> List.forall (fun entry -> entry.CostEvidence = CostEvidenceKind.Unavailable)) "cost evidence must be unavailable")

          t "14 missing duration stays unknown" (fun () ->
              let analysis = analyze [ queued "A" "ready" "2026-09-01T00:00:00Z" ] [] [] [] PlannerConfiguration.defaults
              Assert.equal Estimate.unknown (item analysis "A").RemainingDuration
              let plan = simulate analysis PlannerConfiguration.defaults OptimizationObjective.MinimumDuration None
              Assert.equal None plan.ExpectedDuration.Expected
              Assert.isTrue (match plan.Availability with StrategyAvailability.LowConfidence _ -> true | _ -> false) "speed without durations must be low confidence")

          t "15 historical durations inform estimates by class" (fun () ->
              let analysis = analyze [ queued "A" "ready" "2026-09-01T00:00:00Z" ] [] history [] PlannerConfiguration.defaults
              let development = analysis.History.Distributions |> List.find (fun distribution -> distribution.TaskClass = "development")
              Assert.equal 24 development.SampleCount
              Assert.equal EvidenceConfidence.High development.Confidence
              let estimate = (item analysis "A").FullDuration
              Assert.equal (Some development.Median) estimate.Expected
              Assert.isTrue (estimate.Lower.Value < estimate.Upper.Value) "estimates are ranges")

          t "16 critical path follows the longest hard-dependency chain" (fun () ->
              let analysis =
                  analyze
                      [ queued "A" "ready" "2026-09-01T00:00:00Z"
                        { queued "B" "ready" "2026-09-02T00:00:00Z" with DependsOn = [ "A" ] }
                        { queued "C" "ready" "2026-09-03T00:00:00Z" with DependsOn = [ "B" ] }
                        queued "D" "ready" "2026-09-04T00:00:00Z" ]
                      []
                      history
                      []
                      PlannerConfiguration.defaults

              Assert.equal [ "A"; "B"; "C" ] analysis.CriticalPath.WorkItems
              let unlock = analysis.Unlocks |> List.find (fun value -> value.WorkItem = "A")
              Assert.equal [ "B" ] unlock.DirectlyUnlocks
              Assert.equal [ "B"; "C" ] unlock.TransitiveDependents)

          t "17 independent scoped items form safe parallel waves" (fun () ->
              let analysis = analyze [ queued "A" "ready" "2026-09-01T00:00:00Z"; queued "B" "ready" "2026-09-02T00:00:00Z" ] [] history [] stateSafe
              let plan = simulate analysis stateSafe OptimizationObjective.MinimumDuration None
              Assert.equal [ [ "A"; "B" ] ] (executionWaves plan |> List.map List.sort)
              Assert.isTrue (reasonsOf plan "A" |> List.exists (fun reason -> reason.Code = ReasonCode.SafeParallelCandidate)) "safe parallel reason expected")

          t "18 known collisions prevent parallel execution" (fun () ->
              let sameBranch id = { live id LiveWorkState.Ready with Checkpoint = Some(checkpoint "shared" (String((if id = "A" then 'a' else 'b'), 40)) "implement the rest") }
              let analysis = analyze [ queued "A" "ready" "2026-09-01T00:00:00Z"; queued "B" "ready" "2026-09-02T00:00:00Z" ] [ sameBranch "A"; sameBranch "B" ] history [] stateSafe
              let collision = Assert.single analysis.Collisions
              Assert.equal CollisionRisk.Conflict collision.Risk

              for objective in [ OptimizationObjective.MinimumDuration; OptimizationObjective.MaximumSafeParallelism; OptimizationObjective.Balanced ] do
                  let plan = simulate analysis stateSafe objective None
                  Assert.isTrue (executionWaves plan |> List.forall (fun wave -> wave.Length = 1)) "conflicting items must never share a wave")

          t "19 unknown overlap is treated conservatively" (fun () ->
              let untagged = { queued "U" "ready" "2026-09-02T00:00:00Z" with Tags = [] }
              let analysis = analyze [ queued "A" "ready" "2026-09-01T00:00:00Z"; untagged ] [] history [] stateSafe
              Assert.equal CollisionRisk.Unknown (Assert.single analysis.Collisions).Risk
              Assert.isTrue (hasFinding FindingCode.UnknownCollision "U" analysis) "unknown-collision finding expected"

              for objective in [ OptimizationObjective.MinimumDuration; OptimizationObjective.MaximumSafeParallelism ] do
                  Assert.isTrue (executionWaves (simulate analysis stateSafe objective None) |> List.forall (fun wave -> wave.Length = 1)) "unknown overlap must not be co-scheduled")

          t "20 maximum concurrency is respected" (fun () ->
              let queue = [ for index in 1..5 -> queued $"P{index}" "ready" $"2026-09-0{index}T00:00:00Z" ]
              let analysis = analyze queue [] history [] stateSafe
              let plan = simulate analysis stateSafe OptimizationObjective.MaximumSafeParallelism (Some 2)
              Assert.isTrue (executionWaves plan |> List.forall (fun wave -> wave.Length <= 2)) "no wave may exceed the limit"
              Assert.equal 5 (scheduled plan).Length
              Assert.isTrue (plan.Waves |> List.collect (fun wave -> wave.Entries) |> List.exists (fun entry -> entry.Reasons |> List.exists (fun reason -> reason.Code = ReasonCode.CapacityLimited))) "capacity reason expected")

          t "21 FIFO baseline runs oldest first, one at a time" (fun () ->
              let analysis = analyze [ queued "NEW" "ready" "2026-09-03T00:00:00Z"; queued "OLD" "ready" "2026-09-01T00:00:00Z"; queued "MID" "ready" "2026-09-02T00:00:00Z" ] [] history [] stateSafe
              let plan = simulate analysis stateSafe OptimizationObjective.Baseline None
              Assert.equal [ [ "OLD" ]; [ "MID" ]; [ "NEW" ] ] (executionWaves plan))

          t "22 balanced strategy reports explicit weights and scores" (fun () ->
              let near = { live "NEAR" LiveWorkState.Active with Checkpoint = Some(checkpoint "feature/near" (String('c', 40)) "Record completion") }
              let analysis = analyze [ queued "FRESH" "ready" "2026-09-01T00:00:00Z"; queued "NEAR" "ready" "2026-09-02T00:00:00Z" ] [ near ] history [] PlannerConfiguration.defaults
              let plan = simulate analysis PlannerConfiguration.defaults OptimizationObjective.Balanced (Some 1)
              Assert.equal (Some PlannerConfiguration.defaultWeights) plan.Weights
              Assert.equal [ [ "NEAR" ]; [ "FRESH" ] ] (executionWaves plan)
              Assert.isTrue (reasonsOf plan "NEAR" |> List.exists (fun reason -> reason.Code = ReasonCode.BalancedScore && reason.Message.Contains "cost weight 0.15 not applied")) "score must show every weight")

          t "23 speed strategy prioritises the critical path" (fun () ->
              let analysis =
                  analyze
                      [ queued "SOLO" "ready" "2026-09-01T00:00:00Z"
                        queued "ROOT" "ready" "2026-09-02T00:00:00Z"
                        { queued "LEAF" "ready" "2026-09-03T00:00:00Z" with DependsOn = [ "ROOT" ] } ]
                      []
                      history
                      []
                      PlannerConfiguration.defaults

              let plan = simulate analysis PlannerConfiguration.defaults OptimizationObjective.MinimumDuration (Some 1)
              Assert.equal [ [ "ROOT" ]; [ "LEAF" ]; [ "SOLO" ] ] (executionWaves plan))

          t "24 cost strategy is unavailable without cost evidence" (fun () ->
              let analysis = mixed ()
              let plan = simulate analysis PlannerConfiguration.defaults OptimizationObjective.MinimumCost None

              match plan.Availability with
              | StrategyAvailability.Unavailable reason -> Assert.isTrue (reason.Contains "0 of 24 sampled executions") $"unexpected reason: {reason}"
              | other -> failwith $"expected unavailable, got {other}"

              Assert.empty plan.Waves
              let budget = simulate analysis PlannerConfiguration.defaults (OptimizationObjective.BudgetConstrained { Amount = 25m; Currency = "USD" }) None
              Assert.equal (Some ConstraintVerdict.CannotEvaluate) (budget.Constraint |> Option.map (fun assessment -> assessment.Verdict))
              let costed = history |> List.map (withCost 2m)
              let available = analyze [ queued "A" "ready" "2026-09-01T00:00:00Z" ] [] costed [] PlannerConfiguration.defaults
              Assert.equal StrategyAvailability.Available (simulate available PlannerConfiguration.defaults OptimizationObjective.MinimumCost None).Availability)

          t "25 compare reports a Pareto frontier, never one optimum" (fun () ->
              let queue = [ for index in 1..4 -> queued $"P{index}" "ready" $"2026-09-0{index}T00:00:00Z" ]
              let analysis = analyze queue [] history [] stateSafe
              let comparison = Planner.compare analysis stateSafe None
              Assert.isTrue (not comparison.Frontier.IsEmpty) "frontier expected"
              Assert.isTrue (not (comparison.Frontier |> List.contains "cost")) "an unavailable strategy is never on the frontier"
              Assert.isTrue (comparison.Frontier |> List.contains "baseline" && comparison.Frontier |> List.contains "speed") "slower-but-serial and faster-but-parallel both survive"
              Assert.equal Comparison.noUniversalOptimum comparison.Statement
              let durations = comparison.ConcurrencyCurve |> List.map (fun point -> point.ExpectedDuration.Expected.Value)
              Assert.equal (List.sortDescending durations) durations)

          t "26 identical input renders a byte-identical logical plan" (fun () ->
              let render (plannedAt: string) =
                  let analysis = Planner.analyze { (input [ queued "A" "ready" "2026-09-01T00:00:00Z" ] [ live "L" LiveWorkState.Active ] history [] PlannerConfiguration.defaults) with PlannedAt = plannedAt }
                  Planner.plan analysis PlannerConfiguration.defaults OptimizationObjective.MinimumDuration None |> PlanningJson.logicalPlan |> PlanningJson.render

              Assert.equal (render "2026-09-29T00:00:00.000Z") (render "2026-10-01T12:00:00.000Z"))

          t "27 stale plans are detected" (fun () ->
              let queue = [ queued "A" "ready" "2026-09-01T00:00:00Z"; queued "B" "ready" "2026-09-02T00:00:00Z" ]
              let before = (analyze queue [] history [] PlannerConfiguration.defaults).Snapshot
              Assert.isTrue (not (Snapshot.compare before (analyze queue [] history [] PlannerConfiguration.defaults).Snapshot).Stale) "unchanged state is fresh"
              let completed = Snapshot.compare before (analyze queue [ live "A" LiveWorkState.Complete ] history [] PlannerConfiguration.defaults).Snapshot
              Assert.isTrue (completed.Changes |> List.exists (fun change -> change.Kind = PlanChangeKind.ItemCompleted && change.WorkItem = Some "A")) "completion expected"
              let added = Snapshot.compare before (analyze (queue @ [ queued "C" "ready" "2026-09-03T00:00:00Z" ]) [] history [] PlannerConfiguration.defaults).Snapshot
              Assert.isTrue (added.Changes |> List.exists (fun change -> change.Kind = PlanChangeKind.WorkAdded)) "new work expected"
              let moved = Snapshot.compare before (Planner.analyze { (input queue [] history [] PlannerConfiguration.defaults) with Commit = Some "2222222222222222222222222222222222222222" }).Snapshot
              Assert.isTrue (moved.Changes |> List.exists (fun change -> change.Kind = PlanChangeKind.RepositoryChanged)) "repository change expected"
              let checkpointed = Snapshot.compare before (analyze queue [ { live "B" LiveWorkState.Active with Checkpoint = Some(checkpoint "b" (String('d', 40)) "implement") } ] history [] PlannerConfiguration.defaults).Snapshot
              Assert.isTrue (checkpointed.Changes |> List.exists (fun change -> change.Kind = PlanChangeKind.CheckpointRecorded)) "checkpoint expected")

          t "28 explain covers every strategy and the estimate evidence" (fun () ->
              let analysis = mixed ()

              match Planner.explain analysis PlannerConfiguration.defaults None "D" with
              | Error message -> failwith message
              | Ok explanation ->
                  Assert.equal [ "baseline"; "speed"; "balanced"; "cost"; "max-parallel" ] (explanation.Placements |> List.map (fun placement -> placement.Objective))
                  Assert.isTrue (explanation.Placements |> List.exists (fun placement -> placement.Reasons |> List.exists (fun reason -> reason.Code = ReasonCode.WaitingOnDependency))) "dependency reason expected"
                  Assert.isTrue (explanation.EstimateBasis |> List.exists (fun line -> line.Contains "never counted as productive")) "active elapsed-time rule must be stated"

              Assert.isTrue (Result.isError (Planner.explain analysis PlannerConfiguration.defaults None "MISSING")) "unknown id is an error")

          t "29 plan JSON round-trips" (fun () ->
              let near = { live "NEAR" LiveWorkState.Active with Checkpoint = Some(checkpoint "feature/near" (String('e', 40)) "Confirm CI then complete") }
              let analysis = analyze [ queued "A" "ready" "2026-09-01T00:00:00Z"; queued "C" "captured" "2026-09-02T00:00:00Z" ] [ near; { live "BLK" LiveWorkState.Blocked with BlockReason = Some "needs human sign-off" } ] (history |> List.map (withCost 1.25m)) [] PlannerConfiguration.defaults

              for objective in Planner.comparedObjectives @ [ OptimizationObjective.DeadlineConstrained(240L * minute); OptimizationObjective.BudgetConstrained { Amount = 10m; Currency = "USD" } ] do
                  let document = Planner.plan analysis PlannerConfiguration.defaults objective None
                  let json = PlanningJson.render (PlanningJson.plan document)

                  match PlanningJson.parsePlan json with
                  | Error message -> failwith message
                  | Ok parsed ->
                      Assert.equal document parsed
                      Assert.equal json (PlanningJson.render (PlanningJson.plan parsed)))

          t "A sparse cost: duration optimisation available, money unavailable" (fun () ->
              let analysis = mixed ()
              Assert.isTrue (not analysis.History.Cost.Sufficient) "cost must be insufficient"
              Assert.isTrue (analysis.Findings |> List.exists (fun finding -> finding.Code = FindingCode.MonetaryOptimizationUnavailable)) "finding expected"
              let speed = simulate analysis PlannerConfiguration.defaults OptimizationObjective.MinimumDuration None
              Assert.equal StrategyAvailability.Available speed.Availability
              Assert.isTrue speed.ExpectedDuration.Expected.IsSome "duration must be estimated")

          t "B completed prerequisite from free text is satisfied" (fun () ->
              let twelve = { queued "PRAXIS-REMOTE-12" "ready" "2026-09-02T00:00:00Z" with Description = Some "Conditor installs the remote surface. Depends on: PRAXIS-REMOTE-11." }
              let analysis = analyze [ queued "PRAXIS-REMOTE-11" "complete" "2026-09-01T00:00:00Z"; twelve ] [ live "PRAXIS-REMOTE-11" LiveWorkState.Complete ] history [] PlannerConfiguration.defaults
              let dependency = Assert.single (item analysis "PRAXIS-REMOTE-12").Dependencies
              Assert.equal DependencyOrigin.Inferred dependency.Dependency.Origin
              Assert.equal DependencyStatus.Satisfied dependency.Status
              Assert.equal (Some 1) (waveOf (simulate analysis PlannerConfiguration.defaults OptimizationObjective.Balanced None) "PRAXIS-REMOTE-12"))

          t "C checkpoint says little remains" (fun () ->
              let sixteen = { live "PRAXIS-REMOTE-16" LiveWorkState.Active with Checkpoint = Some(checkpoint "claude/gh90-followups" (String('f', 40)) "Confirm CI runs the new F# test green, then complete with implementation and tests evidence") }
              let analysis = analyze [ queued "PRAXIS-REMOTE-16" "ready" "2026-09-01T00:00:00Z" ] [ sixteen ] history [] PlannerConfiguration.defaults
              let entry = item analysis "PRAXIS-REMOTE-16"
              Assert.equal RemainingBasis.VerificationRemaining entry.Basis
              Assert.isTrue (entry.RemainingDuration.Upper.Value * 3L < entry.FullDuration.Upper.Value) "remaining work must be far below original work")

          t "D merged work still recorded active is a stale-state candidate" (fun () ->
              let commit = String('9', 40)
              let sixteen = { live "PRAXIS-REMOTE-16" LiveWorkState.Active with Checkpoint = Some(checkpoint "claude/gh90-followups" commit "Confirm CI runs green, then complete") }

              let observations =
                  [ { Kind = ObservationKind.CommitMerged(commit, "origin/main"); Provenance = Provenance.create EvidenceSource.Git "git merge-base" }
                    { Kind = ObservationKind.ContinuousIntegrationPassed commit; Provenance = Provenance.create EvidenceSource.ContinuousIntegration "run 1" } ]

              let analysis = analyze [ queued "PRAXIS-REMOTE-16" "ready" "2026-09-01T00:00:00Z" ] [ sixteen ] history observations PlannerConfiguration.defaults
              Assert.equal PlanningWorkState.StaleStateCandidate (item analysis "PRAXIS-REMOTE-16").PlanningState
              let plan = simulate analysis PlannerConfiguration.defaults OptimizationObjective.Balanced None
              Assert.empty (scheduled plan)
              let wave = plan.Waves |> List.head
              Assert.equal WaveKind.Reconciliation wave.Kind
              Assert.equal RecommendedAction.Reconcile (Assert.single wave.Entries).Action
              let finding = analysis.Findings |> List.find (fun finding -> finding.Code = FindingCode.StaleStateCandidate)
              Assert.isTrue (finding.RecommendedAction.Value.Contains "do not reimplement") "must recommend reconciliation, not reimplementation"
              Assert.isTrue (analysis.Findings |> List.exists (fun finding -> finding.Code = FindingCode.PlanningConfidenceLimited)) "confidence must be limited")

          t "E stale blocker naming a completed prerequisite" (fun () ->
              let blocked = { live "GH-90" LiveWorkState.Blocked with BlockReason = Some "Praxis-side work merged via PR #91; remaining PRAXIS-REMOTE-11 (live proof) needs an attested release" }
              let analysis = analyze [ queued "PRAXIS-REMOTE-11" "complete" "2026-09-01T00:00:00Z" ] [ blocked; live "PRAXIS-REMOTE-11" LiveWorkState.Complete ] history [] PlannerConfiguration.defaults
              Assert.equal PlanningWorkState.StaleStateCandidate (item analysis "GH-90").PlanningState
              let plan = simulate analysis PlannerConfiguration.defaults OptimizationObjective.Baseline None
              Assert.equal [ "GH-90" ] (plan.StaleStateCandidates |> List.map (fun entry -> entry.WorkItem)))

          t "F live item missing from the queue is in the inventory" (fun () ->
              let blocked = { live "GH-84" LiveWorkState.Blocked with BlockReason = Some "awaiting human review" }
              let analysis = analyze [ queued "OTHER" "ready" "2026-09-01T00:00:00Z" ] [ blocked ] history [] PlannerConfiguration.defaults
              let entry = item analysis "GH-84"
              Assert.isTrue (entry.InContext && not entry.InQueue) "context-only item expected"
              Assert.equal PlanningWorkState.AwaitingHuman entry.PlanningState)

          t "G parallel Praxis-state mutations carry elevated risk" (fun () ->
              let analysis = analyze [ queued "A" "ready" "2026-09-01T00:00:00Z"; queued "B" "ready" "2026-09-02T00:00:00Z" ] [] history [] PlannerConfiguration.defaults
              let collision = Assert.single analysis.Collisions
              Assert.equal CollisionRisk.Elevated collision.Risk
              Assert.equal [ CollisionSignal.PraxisStateFiles ] collision.Signals
              Assert.isTrue (analysis.Findings |> List.exists (fun finding -> finding.Code = FindingCode.PraxisStateCollision)) "state-collision finding expected"
              let plan = simulate analysis PlannerConfiguration.defaults OptimizationObjective.Balanced None
              Assert.equal 1 plan.Proxy.RiskAcceptedPairs
              Assert.isTrue (reasonsOf plan "A" |> List.exists (fun reason -> reason.Code = ReasonCode.ParallelRiskAccepted)) "accepted risk must be explained")

          t "narrative text is not a dependency" (fun () ->
              let narrative = { queued "CONT" "captured" "2026-09-02T00:00:00Z" with Description = Some "Found while driving PR #107: after PRAXIS-REMOTE-17 committed evidence, a fresh PRAXIS-REMOTE-16 checkpoint listed it." }
              let analysis = analyze [ queued "PRAXIS-REMOTE-16" "ready" "2026-09-01T00:00:00Z"; queued "PRAXIS-REMOTE-17" "ready" "2026-09-01T00:00:00Z"; narrative ] [] history [] PlannerConfiguration.defaults
              Assert.empty (item analysis "CONT").Dependencies)

          t "structured dependencies outrank inferred ones for the same target" (fun () ->
              let down = { queued "DOWN" "ready" "2026-09-02T00:00:00Z" with DependsOn = [ "UP" ]; Description = Some "Depends on UP." }
              let analysis = analyze [ queued "UP" "ready" "2026-09-01T00:00:00Z"; down ] [] history [] PlannerConfiguration.defaults
              let dependency = Assert.single (item analysis "DOWN").Dependencies
              Assert.equal DependencyOrigin.Structured dependency.Dependency.Origin)

          t "active execution elapsed time is never a duration sample" (fun () ->
              let running = { executed 99 "RUN" "development" 1L with Status = ExecutionStatus.Active; StartedAt = "2026-01-01T00:00:00Z"; FinalizedAt = None; WallMs = None }
              let summary = History.summarize PlannerConfiguration.defaults (running :: history)
              Assert.equal 24 summary.DurationSamples
              Assert.equal 1 summary.ActiveExecutionsExcluded)

          t "deadline is only satisfiable with evidence" (fun () ->
              let empty = analyze [ queued "A" "ready" "2026-09-01T00:00:00Z" ] [] [] [] PlannerConfiguration.defaults
              let unknown = simulate empty PlannerConfiguration.defaults (OptimizationObjective.DeadlineConstrained(240L * minute)) None
              Assert.equal (Some ConstraintVerdict.CannotEvaluate) (unknown.Constraint |> Option.map (fun assessment -> assessment.Verdict))
              let known = analyze [ queued "A" "ready" "2026-09-01T00:00:00Z" ] [] history [] PlannerConfiguration.defaults
              let within = simulate known PlannerConfiguration.defaults (OptimizationObjective.DeadlineConstrained(240L * minute)) None
              Assert.equal (Some ConstraintVerdict.Satisfiable) (within.Constraint |> Option.map (fun assessment -> assessment.Verdict))
              let tooShort = simulate known PlannerConfiguration.defaults (OptimizationObjective.DeadlineConstrained(1L * minute)) None
              Assert.equal (Some ConstraintVerdict.NotSatisfiable) (tooShort.Constraint |> Option.map (fun assessment -> assessment.Verdict)))

          t "replay predicts only from earlier executions" (fun () ->
              let report = Replay.replay PlannerConfiguration.defaults [] history
              let first = report.Predictions |> List.head
              Assert.equal 0 first.TrainingSamples
              Assert.equal None first.Predicted.Expected
              let last = report.Predictions |> List.last
              Assert.equal 23 last.TrainingSamples
              Assert.equal (report.Executions - report.Unpredictable) report.Predicted
              Assert.equal 1 report.Overlap.PeakConcurrency)

          t "drift is detected when recent work stops resembling history" (fun () ->
              let shifted = history @ [ for index in 24..35 -> executed index $"NEW-{index}" "development" 300L ]
              let summary = History.summarize PlannerConfiguration.defaults shifted
              Assert.isTrue (summary.Drift |> Option.exists (fun drift -> drift.Drifted)) "drift expected")

          t "configuration and observation files parse" (fun () ->
              let configuration =
                  PlanningJson.parseConfiguration """{"maxConcurrency":2,"praxisStateMergeSafe":true,"dependencies":[{"from":"B","to":"A"}],"areas":{"A":["src/a"]},"balancedWeights":{"duration":0.5}}"""

              match configuration with
              | Error message -> failwith message
              | Ok parsed ->
                  Assert.equal 2 parsed.MaxConcurrency
                  Assert.isTrue parsed.PraxisStateMergeSafe "flag expected"
                  Assert.equal [ { From = "B"; To = "A"; Kind = DependencyKind.Hard } ] parsed.Dependencies
                  Assert.equal 0.5m parsed.BalancedWeights.Duration

              match PlanningJson.parseObservations "obs.json" """{"observations":[{"kind":"ci-passed","subject":"X","source":"ci"},{"kind":"pull-request-merged","pullRequest":7,"source":"github"}]}""" with
              | Error message -> failwith message
              | Ok observations -> Assert.equal 2 observations.Length

              Assert.isTrue (Result.isError (PlanningJson.parseObservations "bad.json" """{"observations":[{"kind":"guess"}]}""")) "unknown kinds are refused")

          t "error history: a measurement uses only executions finalized by its as-of time" (fun () ->
              // history: execution i starts at hour i and runs 10 + i minutes.
              let asOf = DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)
              let measurement = ErrorHistory.measure PlannerConfiguration.defaults "test" "abc123" asOf history
              let window = history |> List.filter (fun execution -> (DateTimeOffset.Parse execution.FinalizedAt.Value) <= asOf)
              Assert.equal window.Length measurement.Executions
              Assert.equal "2026-09-01T12:00:00.000Z" measurement.AsOf
              Assert.equal (Replay.replay PlannerConfiguration.defaults [] window).Predicted measurement.Overall.Predictions
              let later = ErrorHistory.measure PlannerConfiguration.defaults "test" "abc123" (asOf.AddDays 2.0) history
              Assert.equal history.Length later.Executions
              Assert.isTrue (later.InputFingerprint <> measurement.InputFingerprint) "a different evidence window has a different fingerprint")

          t "error history: segments by task class and recorded provider, runtime and model only" (fun () ->
              let mixedIdentity =
                  history
                  |> List.mapi (fun index execution ->
                      if index % 2 = 0 then { execution with Provider = "provider-b"; Model = Some "model-x"; Classes = [ "maintenance" ] }
                      else { execution with Provider = "unknown"; Runtime = "unknown" })

              let measurement = ErrorHistory.measure PlannerConfiguration.defaults "test" "abc123" (DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)) mixedIdentity
              let values dimension = measurement.Segments |> List.filter (fun segment -> segment.Dimension = dimension) |> List.map (fun segment -> segment.Value)
              Assert.equal [ "development"; "maintenance" ] (values "task-class")
              Assert.equal [ "provider-b" ] (values "provider")
              Assert.equal [ "runtime-a" ] (values "runtime")
              Assert.equal [ "model-x" ] (values "model")

              let classTotal =
                  measurement.Segments |> List.filter (fun segment -> segment.Dimension = "task-class") |> List.sumBy (fun segment -> segment.Summary.Predictions)

              Assert.equal measurement.Overall.Predictions classTotal)

          t "error history: recording is idempotent and never overwrites a different measurement" (fun () ->
              let asOf = DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero)
              let first = ErrorHistory.measure PlannerConfiguration.defaults "test" "abc123" asOf history
              let earlier = ErrorHistory.measure PlannerConfiguration.defaults "test" "abc123" (asOf.AddHours -6.0) history

              match ErrorHistory.record [] first with
              | Error message -> failwith message
              | Ok(stored, outcome) ->
                  Assert.equal ErrorRecordOutcome.Recorded outcome

                  match ErrorHistory.record stored first with
                  | Ok(unchanged, ErrorRecordOutcome.Unchanged) -> Assert.equal stored unchanged
                  | other -> failwith $"expected unchanged, got {other}"

                  let conflicting = { first with Executions = first.Executions + 1 }
                  Assert.isTrue (Result.isError (ErrorHistory.record stored conflicting)) "same key, different content is refused"

                  match ErrorHistory.record stored earlier with
                  | Ok(both, ErrorRecordOutcome.Recorded) -> Assert.equal [ earlier.AsOf; first.AsOf ] (both |> List.map (fun measurement -> measurement.AsOf))
                  | other -> failwith $"expected recorded, got {other}")

          t "error history: measurements beyond the horizon are stale and never authoritative" (fun () ->
              let at (day: int) = DateTimeOffset(2026, 9, day, 0, 0, 0, TimeSpan.Zero)
              let old = ErrorHistory.measure PlannerConfiguration.defaults "test" "aaa" (at 2) history
              let recent = ErrorHistory.measure PlannerConfiguration.defaults "test" "bbb" (at 20) history
              let future = ErrorHistory.measure PlannerConfiguration.defaults "test" "ccc" (at 29) history
              let stored = ErrorHistory.canonical [ recent; future; old ]

              let view = ErrorHistory.view 10 (at 25) stored
              Assert.equal [ true; false ] (view.Entries |> List.map (fun entry -> entry.Stale))
              Assert.equal 1 view.Future
              Assert.equal (Some recent) view.Authoritative
              let overall = view.Series |> List.head
              Assert.equal "overall" overall.Dimension
              Assert.equal (Some recent.Overall) overall.Latest

              let allStale = ErrorHistory.view 1 (at 25) stored
              Assert.equal None allStale.Authoritative
              Assert.isTrue (allStale.Series |> List.forall (fun series -> series.Latest.IsNone)) "no stale point is authoritative"
              Assert.isTrue (allStale.Statement.Contains "none is authoritative") allStale.Statement

              Assert.equal None (ErrorHistory.view 10 (at 25) []).Authoritative)

          t "error history: rendering is deterministic and round-trips" (fun () ->
              let at (day: int) = DateTimeOffset(2026, 9, day, 0, 0, 0, TimeSpan.Zero)
              let measure () =
                  [ ErrorHistory.measure PlannerConfiguration.defaults "test" "aaa" (at 2) history
                    ErrorHistory.measure PlannerConfiguration.defaults "test" "bbb" (at 20) history ]

              let rendered = PlanningJson.renderErrorHistoryStore (measure ())
              Assert.equal rendered (PlanningJson.renderErrorHistoryStore (measure ()))
              Assert.equal (Ok(measure ())) (PlanningJson.readErrorHistoryStore rendered)
              let view () = ErrorHistory.view 10 (at 25) (measure ()) |> PlanningJson.errorHistory |> PlanningJson.render
              Assert.equal (view ()) (view ()))

          t "error history: findings catch inconsistent stored measurements" (fun () ->
              let measurement = ErrorHistory.measure PlannerConfiguration.defaults "test" "aaa" (DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero)) history
              Assert.empty (ErrorHistory.findings [ measurement ])
              let broken = { measurement with Overall = { measurement.Overall with WithinRange = measurement.Overall.Predictions + 1 }; AsOf = "2026-09-02" }
              let messages = ErrorHistory.findings [ broken; measurement ] |> List.map snd
              Assert.isTrue (messages |> List.exists (fun message -> message.Contains "withinRange")) "within-range count checked"
              Assert.isTrue (messages |> List.exists (fun message -> message.Contains "millisecond precision")) "as-of format checked"
              Assert.isTrue (ErrorHistory.findings [ measurement; measurement ] |> List.exists (fun (_, message) -> message.Contains "share the key")) "duplicate keys checked"
              let later = { measurement with AsOf = "2026-09-03T00:00:00.000Z" }
              Assert.isTrue (ErrorHistory.findings [ later; measurement ] |> List.exists (fun (_, message) -> message.Contains "ordered")) "order checked")

          t "error history: the horizon is configured" (fun () ->
              Assert.equal 90 PlannerConfiguration.defaults.ErrorHistoryHorizonDays

              match PlanningJson.parseConfiguration """{"errorHistory":{"horizonDays":30}}""" with
              | Error message -> failwith message
              | Ok parsed -> Assert.equal 30 parsed.ErrorHistoryHorizonDays

              Assert.isTrue (Result.isError (PlanningJson.parseConfiguration """{"errorHistory":{"horizonDays":0}}""")) "zero days is refused"
              Assert.isTrue (Result.isError (PlanningJson.parseConfiguration """{"errorHistory":{"horizonDays":1.5}}""")) "fractional days are refused") ]
