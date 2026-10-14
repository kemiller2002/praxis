namespace Praxis.Domain.Planning

open System

/// Historical duration and cost evidence (PRX-PLAN-050..063, 170..173).
/// Only finalized executions are measured: elapsed time of an execution that
/// is still active is never read as productive time (PRX-PLAN-063).
[<RequireQualifiedAccess>]
module History =
    let pooledClass = "all"

    type DurationSample =
        { ExecutionId: string
          WorkItemId: string
          Classes: string list
          StartedAt: DateTimeOffset
          FinalizedAt: DateTimeOffset
          ProductiveMs: int64
          /// Productive time came from runtime-measured session time.
          SessionMeasured: bool
          Provider: string
          Runtime: string
          Model: string option }

    /// ROS wall time minus recorded blocked time.
    let private wallProductiveMs (execution: HistoricalExecution) : int64 option =
        match execution.Status with
        | ExecutionStatus.Active -> None
        | ExecutionStatus.Finalized ->
            let blocked = execution.BlockedMs |> Option.defaultValue 0L

            let wall =
                match execution.WallMs with
                | Some wall -> Some wall
                | None ->
                    match Text.tryTimestamp execution.StartedAt, execution.FinalizedAt |> Option.bind Text.tryTimestamp with
                    | Some started, Some finalized -> Some(int64 (finalized - started).TotalMilliseconds)
                    | _ -> None

            wall |> Option.map (fun wall -> max 0L (wall - blocked))

    /// Productive time: ROS wall time minus blocked time, or the
    /// runtime-measured active session time when that is longer. ROS wall
    /// time starts at `work begin`, which executors often run just before
    /// completion (EV-ROS-2026-A058), so it under-measures the work; a
    /// session transcript measures the session itself (PRAXIS-PLAN-05).
    /// Returns the time and whether session time supplied it.
    let productive (execution: HistoricalExecution) : (int64 * bool) option =
        match wallProductiveMs execution, execution.Session.ActiveMs with
        | Some wall, Some active when active > wall -> Some(active, true)
        | Some wall, _ -> Some(wall, false)
        | None, _ -> None

    let productiveMs (execution: HistoricalExecution) : int64 option =
        productive execution |> Option.map fst

    let samples (executions: HistoricalExecution list) : DurationSample list =
        executions
        |> List.choose (fun execution ->
            match productive execution, Text.tryTimestamp execution.StartedAt, execution.FinalizedAt |> Option.bind Text.tryTimestamp with
            | Some(productive, sessionMeasured), Some started, Some finalized ->
                Some
                    { ExecutionId = execution.ExecutionId
                      WorkItemId = execution.WorkItemId
                      Classes = Text.distinctOrdinal execution.Classes
                      StartedAt = started
                      FinalizedAt = finalized
                      ProductiveMs = productive
                      SessionMeasured = sessionMeasured
                      Provider = execution.Provider
                      Runtime = execution.Runtime
                      Model = execution.Model }
            | _ -> None)
        |> List.sortWith (fun left right ->
            match compare left.FinalizedAt right.FinalizedAt with
            | 0 -> Text.ordinal left.ExecutionId right.ExecutionId
            | order -> order)

    /// Nearest-rank percentile of an ascending list.
    let percentile (fraction: decimal) (sorted: int64 list) =
        match sorted with
        | [] -> None
        | _ ->
            let rank = int (Math.Ceiling(fraction * decimal sorted.Length)) |> max 1 |> min sorted.Length
            Some(List.item (rank - 1) sorted)

    /// PRX-PLAN-061: report ranges at a granularity the evidence supports:
    /// whole minutes below an hour, five minutes above.
    let roundDuration (milliseconds: int64) =
        let minute = 60_000L

        if milliseconds < 60L * minute then
            ((milliseconds + minute / 2L) / minute) * minute
        else
            let step = 5L * minute
            ((milliseconds + step / 2L) / step) * step

    let confidenceFor (count: int) (lower: int64) (upper: int64) =
        let bySize =
            if count >= 20 then EvidenceConfidence.High
            elif count >= 8 then EvidenceConfidence.Medium
            elif count >= 3 then EvidenceConfidence.Low
            else EvidenceConfidence.Unknown

        // A very wide interquartile spread is weaker evidence than its size suggests.
        if lower > 0L && upper / lower > 4L then EvidenceConfidence.downgrade bySize else bySize

    let distribution (taskClass: string) (samples: DurationSample list) : DurationDistribution option =
        let sorted = samples |> List.map (fun sample -> sample.ProductiveMs) |> List.sort

        match percentile 0.25m sorted, percentile 0.50m sorted, percentile 0.75m sorted with
        | Some lower, Some median, Some upper ->
            Some
                { TaskClass = taskClass
                  SampleCount = sorted.Length
                  SessionMeasured = samples |> List.filter (fun sample -> sample.SessionMeasured) |> List.length
                  Lower = roundDuration lower
                  Median = roundDuration median
                  Upper = roundDuration upper
                  Confidence = confidenceFor sorted.Length lower upper }
        | _ -> None

    let distributions (samples: DurationSample list) : DurationDistribution list =
        let pooled = samples |> distribution pooledClass |> Option.toList

        let byClass =
            samples
            |> List.collect (fun sample -> sample.Classes |> List.map (fun taskClass -> taskClass, sample))
            |> List.groupBy fst
            |> List.sortWith (fun (left, _) (right, _) -> Text.ordinal left right)
            |> List.choose (fun (taskClass, values) -> values |> List.map snd |> distribution taskClass)

        pooled @ byClass

    let toEstimate (distribution: DurationDistribution) : Estimate<int64> =
        if distribution.Confidence = EvidenceConfidence.Unknown then
            Estimate.unknown
        else
            { Lower = Some distribution.Lower
              Expected = Some distribution.Median
              Upper = Some distribution.Upper
              Confidence = distribution.Confidence }

    /// Task classes that are implementation work, plus the pooled class,
    /// which implementation executions dominate in recorded history.
    let implementationClasses =
        set [ pooledClass; "development"; "maintenance"; "refactoring"; "defect-bug-fix"; "infrastructure-devops"; "testing-verification"; "prototype-proof-of-concept" ]

    /// PRAXIS-PLAN-05: the history-based duration model missed implementation
    /// work badly in EX-ROS-2026-A021: 61 and 112 min observed against a
    /// 45 min upper bound (EV-ROS-2026-A064, HY-ROS-2026-A027), because ROS
    /// execution wall time starts at `work begin`. Until most samples carry
    /// runtime-measured session time, an implementation estimate loses one
    /// confidence level and its basis says why.
    let private downWeighted (taskClass: string) (distribution: DurationDistribution) (estimate: Estimate<int64>, basis: string) =
        if implementationClasses.Contains taskClass && distribution.SessionMeasured * 2 < distribution.SampleCount then
            { estimate with Confidence = EvidenceConfidence.downgrade estimate.Confidence },
            $"{basis}; confidence lowered one level for implementation work: {distribution.SessionMeasured} of {distribution.SampleCount} samples carry runtime-measured session time, and ROS wall time alone underestimated implementation work 1.4 to 2.5 times beyond the upper bound (EV-ROS-2026-A064)"
        else
            estimate, basis

    /// The full-effort duration estimate for a task class, with the basis it
    /// used. A class without usable history falls back to the pooled
    /// distribution one confidence level lower; no history at all is unknown.
    let estimateFor (distributions: DurationDistribution list) (taskClass: string) : Estimate<int64> * string =
        let find name =
            distributions
            |> List.tryFind (fun distribution -> distribution.TaskClass = name && distribution.Confidence <> EvidenceConfidence.Unknown)

        match find taskClass, find pooledClass with
        | Some own, _ when taskClass <> pooledClass ->
            (toEstimate own, $"{own.SampleCount} finalized '{taskClass}' executions (interquartile range)")
            |> downWeighted taskClass own
        | _, Some pooled ->
            let estimate = toEstimate pooled

            let estimate =
                if taskClass = pooledClass then estimate
                else { estimate with Confidence = EvidenceConfidence.downgrade estimate.Confidence }

            (estimate, $"{pooled.SampleCount} finalized executions of all classes (interquartile range)")
            |> downWeighted taskClass pooled
        | _ -> Estimate.unknown, "no finalized execution history"

    // ---- cost evidence (PRX-PLAN-050..053, 092) --------------------------

    let private usable (observation: CostObservation) =
        observation.Kind <> CostEvidenceKind.Unavailable

    let executionTotal = "cost.execution_total"
    let sessionCumulative = "cost.session_cumulative"

    let private total (costs: CostObservation list) =
        match costs with
        | [] -> None
        | _ ->
            let currency = costs |> List.choose (fun cost -> cost.Currency) |> List.distinct |> List.tryExactlyOne
            Some(costs |> List.sumBy (fun cost -> cost.Amount), currency)

    /// The cost observations an execution's monetary total is built from, in
    /// precedence order: every `cost.execution_total` (aggregation `sum`);
    /// else the cost components; else the latest cumulative session cost,
    /// which is a session gauge and never added to components.
    let private costBasis (execution: HistoricalExecution) : CostObservation list =
        let costs = execution.Costs |> List.filter usable
        let totals = costs |> List.filter (fun cost -> cost.MetricId = executionTotal)
        let components = costs |> List.filter (fun cost -> cost.MetricId <> executionTotal && cost.MetricId <> sessionCumulative)
        let session = costs |> List.filter (fun cost -> cost.MetricId = sessionCumulative) |> List.tryLast |> Option.toList

        [ totals; components; session ] |> List.tryFind (List.isEmpty >> not) |> Option.defaultValue []

    /// One execution's monetary total (see `costBasis`); unknown when none.
    let executionCost (execution: HistoricalExecution) : (decimal * string option) option =
        costBasis execution |> total

    /// Evidence strength: observed and provider-reported costs outrank
    /// calculated, which outranks estimated.
    let costKindRank (kind: CostEvidenceKind) =
        match kind with
        | CostEvidenceKind.Observed -> 4
        | CostEvidenceKind.ProviderReported -> 3
        | CostEvidenceKind.Calculated -> 2
        | CostEvidenceKind.Estimated -> 1
        | CostEvidenceKind.Unavailable -> 0

    /// The strongest kind of evidence behind an execution's monetary total.
    let executionCostKind (execution: HistoricalExecution) : CostEvidenceKind =
        costBasis execution
        |> List.map (fun cost -> cost.Kind)
        |> List.sortByDescending costKindRank
        |> List.tryHead
        |> Option.defaultValue CostEvidenceKind.Unavailable

    let costSummary (configuration: PlannerConfiguration) (executions: HistoricalExecution list) : CostEvidenceSummary =
        let sampled = executions |> List.filter (fun execution -> execution.Status = ExecutionStatus.Finalized)
        let withCost = sampled |> List.choose executionCost
        let currencies = withCost |> List.map snd |> List.distinct
        let currency = match currencies with | [ Some currency ] -> Some currency | _ -> None
        let withTokens = sampled |> List.filter (fun execution -> execution.TokenMetrics > 0) |> List.length
        let sufficient = withCost.Length >= configuration.MinimumCostSamples && currency.IsSome

        let statement =
            if sufficient then
                $"Monetary optimization available: {withCost.Length} of {sampled.Length} sampled executions contain usable cost evidence ({currency.Value})."
            elif withCost.Length > 0 && currency.IsNone then
                $"Monetary optimization unavailable: {withCost.Length} of {sampled.Length} sampled executions contain cost evidence, but not in one consistent currency."
            else
                $"Monetary optimization unavailable: {withCost.Length} of {sampled.Length} sampled executions contain usable cost evidence (at least {configuration.MinimumCostSamples} required)."

        { SampledExecutions = sampled.Length
          WithUsableCost = withCost.Length
          WithTokenUsage = withTokens
          Currency = currency
          Sufficient = sufficient
          Statement = statement }

    /// Pooled per-execution cost range; unknown unless evidence is sufficient.
    /// Never zero, never a default, never a provider stereotype.
    let costEstimate (summary: CostEvidenceSummary) (executions: HistoricalExecution list) : Estimate<Money> =
        match summary.Sufficient, summary.Currency with
        | true, Some currency ->
            let values =
                executions
                |> List.filter (fun execution -> execution.Status = ExecutionStatus.Finalized)
                |> List.choose executionCost
                |> List.map fst
                |> List.sort

            let at fraction =
                match values with
                | [] -> None
                | _ ->
                    let rank = int (Math.Ceiling(fraction * decimal values.Length)) |> max 1 |> min values.Length
                    Some { Amount = List.item (rank - 1) values; Currency = currency }

            { Lower = at 0.25m
              Expected = at 0.50m
              Upper = at 0.75m
              Confidence = confidenceFor values.Length 1L 1L }
        | _ -> Estimate.unknown

    // ---- context overhead (PRAXIS-PLAN-05, PRX-GRP-061) -------------------

    /// Sessions needed before a cold start is reported as measured.
    let minimumContextSessions = 3

    let private medianInt (values: int list) =
        values |> List.map int64 |> List.sort |> percentile 0.50m |> Option.map int

    /// Per-session context overhead from the session metrics finalized
    /// executions carry: time to first code change (the cold start),
    /// governance-document reads and repeated reads.
    let contextOverhead (executions: HistoricalExecution list) : ContextOverheadSummary =
        let sessions =
            executions
            |> List.filter (fun execution -> execution.Status = ExecutionStatus.Finalized)
            |> List.map (fun execution -> execution.Session)

        let coldStarts = sessions |> List.choose (fun session -> session.FirstCodeChangeMs) |> List.sort
        let governance = sessions |> List.choose (fun session -> session.GovernanceReads) |> medianInt
        let repeated = sessions |> List.choose (fun session -> session.RepeatedReads) |> medianInt
        let sufficient = coldStarts.Length >= minimumContextSessions
        let minutes (value: int64) = Math.Round(decimal value / 60_000m, 1).ToString(Globalization.CultureInfo.InvariantCulture)
        let count (value: int option) = value |> Option.map string |> Option.defaultValue "unknown"

        let coldStart =
            match sufficient, percentile 0.25m coldStarts, percentile 0.50m coldStarts, percentile 0.75m coldStarts with
            | true, Some lower, Some median, Some upper ->
                { Lower = Some lower
                  Expected = Some median
                  Upper = Some upper
                  Confidence = confidenceFor coldStarts.Length lower upper }
            | _ -> Estimate.unknown

        let statement =
            match coldStart.Expected with
            | Some median ->
                $"context overhead measured in {coldStarts.Length} sessions: a cold start takes a median {minutes median} min to the first code change (interquartile {minutes coldStart.Lower.Value}-{minutes coldStart.Upper.Value} min), with a median {count governance} governance-document reads and {count repeated} repeated reads per session"
            | None ->
                $"context reuse is unmeasured: {coldStarts.Length} finalized executions record session metrics (time.first_code_change_ms), at least {minimumContextSessions} are needed; ingest session transcripts with the anthropic-claude-session adapter (PRX-GRP-061)"

        { SampledSessions = coldStarts.Length
          ColdStart = coldStart
          MedianGovernanceReads = governance
          MedianRepeatedReads = repeated
          Sufficient = sufficient
          Statement = statement }

    // ---- segmentation and drift (PRX-PLAN-172, 173) ----------------------

    let segments (samples: DurationSample list) : HistorySegment list =
        let segment dimension (key: DurationSample -> string option) =
            samples
            |> List.choose (fun sample -> key sample |> Option.map (fun value -> value, sample.ProductiveMs))
            |> List.groupBy fst
            |> List.sortWith (fun (left, _) (right, _) -> Text.ordinal left right)
            |> List.map (fun (value, entries) ->
                let sorted = entries |> List.map snd |> List.sort

                { Dimension = dimension
                  Value = value
                  SampleCount = sorted.Length
                  MedianMs = percentile 0.50m sorted |> Option.map roundDuration })

        segment "provider" (fun sample -> Some sample.Provider)
        @ segment "runtime" (fun sample -> Some sample.Runtime)
        @ segment "model" (fun sample -> sample.Model)

    let recentWindow = 10

    /// Whether the most recent executions still resemble the history that
    /// produced the estimates. Needs at least twice the window of evidence.
    let drift (samples: DurationSample list) : DriftAssessment option =
        if samples.Length < 2 * recentWindow then
            None
        else
            let prior = samples |> List.take (samples.Length - recentWindow) |> List.map (fun sample -> sample.ProductiveMs) |> List.sort
            let recent = samples |> List.skip (samples.Length - recentWindow) |> List.map (fun sample -> sample.ProductiveMs) |> List.sort

            match percentile 0.25m prior, percentile 0.75m prior, percentile 0.50m recent with
            | Some lower, Some upper, Some median ->
                let drifted = median < lower || median > upper
                let minutes (value: int64) = value / 60_000L

                let statement =
                    if drifted then
                        $"The {recentWindow} most recent executions have a median of {minutes median} min, outside the earlier interquartile range {minutes lower}-{minutes upper} min; historical estimates may no longer predict current work."
                    else
                        $"The {recentWindow} most recent executions (median {minutes median} min) remain within the earlier interquartile range {minutes lower}-{minutes upper} min."

                Some
                    { RecentSampleCount = recent.Length
                      RecentMedianMs = roundDuration median
                      PriorLowerMs = roundDuration lower
                      PriorUpperMs = roundDuration upper
                      Drifted = drifted
                      Statement = statement }
            | _ -> None

    let summarize (configuration: PlannerConfiguration) (executions: HistoricalExecution list) : HistorySummary =
        let measured = samples executions

        { SampledExecutions = executions.Length
          FinalizedExecutions = executions |> List.filter (fun execution -> execution.Status = ExecutionStatus.Finalized) |> List.length
          ActiveExecutionsExcluded = executions |> List.filter (fun execution -> execution.Status = ExecutionStatus.Active) |> List.length
          DurationSamples = measured.Length
          Distributions = distributions measured
          Cost = costSummary configuration executions
          ContextOverhead = contextOverhead executions
          Segments = segments measured
          Drift = drift measured }
