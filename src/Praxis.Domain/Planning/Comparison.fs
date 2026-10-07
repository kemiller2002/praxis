namespace Praxis.Domain.Planning

open System

type StrategySummary =
    { Objective: string
      Availability: StrategyAvailability
      ExpectedDuration: Estimate<int64>
      ExpectedCost: Estimate<Money>
      WaveCount: int
      PeakConcurrency: int
      RiskAcceptedPairs: int
      Executions: int
      ContextAcquisitions: int
      Confidence: EvidenceConfidence
      OnFrontier: bool }

type ConcurrencyPoint =
    { MaxConcurrency: int
      ExpectedDuration: Estimate<int64>
      PeakConcurrency: int
      RiskAcceptedPairs: int
      MarginalSavingMs: int64 option
      DiminishingReturns: bool }

type StrategyComparison =
    { Strategies: StrategySummary list
      Frontier: string list
      ConcurrencyCurve: ConcurrencyPoint list
      DiminishingReturns: string option
      Statement: string }

type OutcomeEntry =
    { WorkItem: string
      RecommendedWave: int
      RecommendedAction: RecommendedAction
      LifecycleThen: string
      LifecycleNow: string
      Outcome: string }

/// A completed recommended item: its predicted remaining duration at plan
/// time against the productive time its executions started after the plan
/// recorded.
type DurationFollowUp =
    { WorkItem: string
      Predicted: Estimate<int64>
      ObservedMs: int64 option
      WithinRange: bool option }

/// PRX-PLAN-162: how what happened compares with what was recommended.
type RecommendationFollowUp =
    { Recommended: int
      Started: int
      Completed: int
      /// Pairs of started items in different waves, and how many started in
      /// the recommended order.
      OrderedPairs: int
      PairsInRecommendedOrder: int
      Durations: DurationFollowUp list
      Statement: string }

type PlanReview =
    { Freshness: PlanFreshness
      Outcomes: OutcomeEntry list
      FollowUp: RecommendationFollowUp
      Statement: string }

/// PRX-PLAN-100..102 and 162: comparing alternatives without claiming a
/// universal optimum, and comparing past recommendations with what happened.
[<RequireQualifiedAccess>]
module Comparison =
    let noUniversalOptimum =
        "No single schedule is optimal across cost, duration and collision risk. The frontier lists the non-dominated alternatives; choose the tradeoff that matters."

    let summarize (plan: ExecutionPlan) : StrategySummary =
        let execution = plan.Waves |> List.filter (fun wave -> wave.Kind = WaveKind.Execution)

        { Objective = OptimizationObjective.code plan.Objective
          Availability = plan.Availability
          ExpectedDuration = plan.ExpectedDuration
          ExpectedCost = plan.ExpectedCost
          WaveCount = execution.Length
          PeakConcurrency = plan.Proxy.PeakConcurrency
          RiskAcceptedPairs = plan.Proxy.RiskAcceptedPairs
          Executions = plan.Proxy.Executions
          ContextAcquisitions = plan.Proxy.ContextAcquisitions
          Confidence = plan.Confidence
          OnFrontier = false }

    /// Minimized dimensions: expected duration, peak concurrency (executors
    /// paid for at once), accepted risk, context acquisitions and, only when
    /// every compared plan has it, expected cost.
    let private dimensions (includeCost: bool) (summary: StrategySummary) =
        [ yield decimal summary.ExpectedDuration.Expected.Value
          yield decimal summary.PeakConcurrency
          yield decimal summary.RiskAcceptedPairs
          yield decimal summary.ContextAcquisitions
          if includeCost then yield summary.ExpectedCost.Expected.Value.Amount ]

    let private dominates (includeCost: bool) (left: StrategySummary) (right: StrategySummary) =
        let a, b = dimensions includeCost left, dimensions includeCost right
        List.forall2 (<=) a b && List.exists2 (<) a b

    let frontier (summaries: StrategySummary list) : StrategySummary list =
        let eligible =
            summaries
            |> List.filter (fun summary ->
                (match summary.Availability with StrategyAvailability.Unavailable _ -> false | _ -> true)
                && summary.ExpectedDuration.Expected.IsSome)

        let includeCost = not eligible.IsEmpty && eligible |> List.forall (fun summary -> summary.ExpectedCost.Expected.IsSome)

        summaries
        |> List.map (fun summary ->
            let onFrontier =
                eligible |> List.contains summary
                && not (eligible |> List.exists (fun other -> other <> summary && dominates includeCost other summary))

            { summary with OnFrontier = onFrontier })

    /// PRX-PLAN-102: how much each additional concurrent executor buys.
    let concurrencyCurve (points: (int * ExecutionPlan) list) : ConcurrencyPoint list * string option =
        let threshold = 0.10m

        let curve =
            points
            |> List.fold
                (fun (acc: ConcurrencyPoint list) (limit, plan) ->
                    let previous = acc |> List.tryLast |> Option.bind (fun point -> point.ExpectedDuration.Expected)
                    let current = plan.ExpectedDuration.Expected
                    let saving = Option.map2 (fun (before: int64) (now: int64) -> before - now) previous current

                    let diminishing =
                        match previous, saving with
                        | Some before, Some saved when before > 0L -> decimal saved / decimal before < threshold
                        | _ -> false

                    acc
                    @ [ { MaxConcurrency = limit
                          ExpectedDuration = plan.ExpectedDuration
                          PeakConcurrency = plan.Proxy.PeakConcurrency
                          RiskAcceptedPairs = plan.Proxy.RiskAcceptedPairs
                          MarginalSavingMs = saving
                          DiminishingReturns = diminishing } ])
                []

        let statement =
            curve
            |> List.tryFind (fun point -> point.DiminishingReturns)
            |> Option.map (fun point ->
                let saved = point.MarginalSavingMs |> Option.map (fun ms -> ms / 60_000L) |> Option.defaultValue 0L
                $"Beyond {point.MaxConcurrency - 1} concurrent executor(s), adding another saves less than 10%% of the expected duration (at {point.MaxConcurrency}: {saved} min saved).")

        curve, statement

    /// PRX-PLAN-162: what happened to each recommended item since the plan,
    /// stated as observation, never as the planner's effect.
    /// PRX-PLAN-162: start order and durations after the plan, against its
    /// waves and estimates. Only executions that started after the plan was
    /// computed count; nothing here is attributed to the plan.
    let followUp (document: PlanDocument) (outcomes: OutcomeEntry list) (executions: HistoricalExecution list) : RecommendationFollowUp =
        let plannedAt = Text.tryTimestamp document.Snapshot.PlannedAt

        let after =
            executions
            |> List.filter (fun execution ->
                match plannedAt, Text.tryTimestamp execution.StartedAt with
                | Some planned, Some started -> started >= planned
                | _ -> false)

        let entries = document.Plan.Waves |> List.collect (fun wave -> wave.Entries |> List.map (fun entry -> wave.Number, entry))

        let firstStart id =
            after |> List.filter (fun execution -> execution.WorkItemId = id) |> List.choose (fun execution -> Text.tryTimestamp execution.StartedAt) |> List.sort |> List.tryHead

        let started = entries |> List.choose (fun (wave, entry) -> firstStart entry.WorkItem |> Option.map (fun start -> entry.WorkItem, wave, start))

        let pairs =
            started
            |> List.mapi (fun index left -> started |> List.skip (index + 1) |> List.map (fun right -> left, right))
            |> List.concat
            |> List.filter (fun ((_, leftWave, _), (_, rightWave, _)) -> leftWave <> rightWave)

        let inOrder =
            pairs
            |> List.filter (fun ((_, leftWave, leftStart), (_, rightWave, rightStart)) -> (leftWave < rightWave) = (leftStart <= rightStart))
            |> List.length

        let completed = outcomes |> List.filter (fun outcome -> outcome.Outcome = "completed") |> List.map (fun outcome -> outcome.WorkItem) |> Set.ofList

        let durations =
            entries
            |> List.filter (fun (_, entry) -> completed.Contains entry.WorkItem)
            |> List.map (fun (_, entry) ->
                let observed =
                    after
                    |> List.filter (fun execution -> execution.WorkItemId = entry.WorkItem && execution.Status = ExecutionStatus.Finalized)
                    |> List.choose History.productiveMs
                    |> function
                        | [] -> None
                        | values -> Some(List.sum values)

                { WorkItem = entry.WorkItem
                  Predicted = entry.Remaining
                  ObservedMs = observed
                  WithinRange =
                    match entry.Remaining.Lower, entry.Remaining.Upper, observed with
                    | Some lower, Some upper, Some value -> Some(value >= lower && value <= upper)
                    | _ -> None })

        let within = durations |> List.filter (fun entry -> entry.WithinRange = Some true) |> List.length
        let measured = durations |> List.filter (fun entry -> entry.WithinRange.IsSome) |> List.length

        let order =
            if pairs.IsEmpty then "no two items from different waves have started since the plan, so its order cannot be compared"
            else $"{inOrder} of {pairs.Length} started pair(s) from different waves started in the recommended order"

        { Recommended = entries.Length
          Started = started.Length
          Completed = completed.Count
          OrderedPairs = pairs.Length
          PairsInRecommendedOrder = inOrder
          Durations = durations
          Statement =
            $"Since the plan: {started.Length} of {entries.Length} recommended item(s) started and {completed.Count} completed; {order}; {within} of {measured} completed item(s) with measured time finished within the predicted range. These are observations, not effects of the plan." }

    let review (document: PlanDocument) (current: PlanSnapshot) (executions: HistoricalExecution list) : PlanReview =
        let now = current.Items |> List.map (fun item -> item.WorkItem, item) |> Map.ofList
        let before = document.Snapshot.Items |> List.map (fun item -> item.WorkItem, item) |> Map.ofList

        let outcomes =
            document.Plan.Waves
            |> List.collect (fun wave ->
                wave.Entries
                |> List.map (fun entry ->
                    let thenState = before.TryFind entry.WorkItem |> Option.map (fun item -> item.LifecycleState) |> Option.defaultValue "unknown"
                    let nowState = now.TryFind entry.WorkItem |> Option.map (fun item -> item.LifecycleState) |> Option.defaultValue "removed"

                    let outcome =
                        match thenState, nowState with
                        | a, b when a = b -> "unchanged"
                        | _, "complete" -> "completed"
                        | _, "active" -> "started"
                        | _, "blocked" -> "blocked"
                        | _, "abandoned" -> "abandoned"
                        | _, other -> $"now {other}"

                    { WorkItem = entry.WorkItem
                      RecommendedWave = wave.Number
                      RecommendedAction = entry.Action
                      LifecycleThen = thenState
                      LifecycleNow = nowState
                      Outcome = outcome }))

        { Freshness = Snapshot.compare document.Snapshot current
          Outcomes = outcomes
          FollowUp = followUp document outcomes executions
          Statement = "Outcomes are observed changes since the plan was computed; the planner does not claim to have caused them." }
