namespace Ros.Domain.Telemetry

open System

/// One recorded measurement, already extracted from an execution record's
/// `metrics` array. `DimensionsKey` is the measurement's own `dimensions`
/// object serialized verbatim (not canonically key-sorted, unlike
/// production's `stable()`-based `dimensionsKey` -- every metric this
/// migration itself writes always has empty `dimensions`, so this only
/// risks diverging from production's own grouping for a non-empty
/// `dimensions` object written by an external tool with non-canonical key
/// order, a narrow and documented gap).
type SummaryMetricMeasurement =
    { Id: string
      Unit: string
      Currency: string option
      DimensionsKey: string
      Value: float
      CollectedAt: string
      RegistryAggregation: string option
      SelfAggregation: string }

/// One execution record, reduced to exactly the fields `summarizeTelemetry`
/// reads.
type SummaryExecution =
    { ExecutionId: string
      Provider: string
      Runtime: string
      SessionId: string option
      StartedAt: string option
      FinalizedAt: string option
      Metrics: SummaryMetricMeasurement list }

type TimingSummary =
    { FullyFinalized: bool
      FinalizedExecutionCount: int
      ActiveExecutionCount: int
      EarliestStartedAt: string option
      LatestFinalizedAt: string option
      CalendarSpanMs: int64 option
      TotalExecutionWallMs: int64 option
      OverlappingExecutionMs: int64 option }

type MetricSummaryEntry =
    { Id: string
      Value: float option
      Unit: string
      Currency: string option
      DimensionsKey: string
      Aggregation: string
      Measurements: int
      Note: string option }

type TelemetrySummary =
    { SchemaVersion: string
      WorkItemId: string option
      ExecutionCount: int
      Providers: string list
      Runtimes: string list
      Timing: TimingSummary
      Metrics: MetricSummaryEntry list }

/// Mirrors production `timingSummary` (`tools/ros_telemetry.mjs`): merges
/// each finalized execution's `[startedAt, finalizedAt]` span via an
/// interval-sweep union to report calendar span versus total (possibly
/// overlapping) execution wall time, explicitly nulling every finalized-only
/// figure whenever any execution in the set is still active (its true end
/// time is unknown).
[<RequireQualifiedAccess>]
module TimingSummary =
    let private parseEpochMs (timestamp: string) =
        DateTimeOffset.Parse(timestamp, Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds()

    let compute (executions: SummaryExecution list) : TimingSummary =
        let starts = executions |> List.choose (fun e -> e.StartedAt) |> List.map parseEpochMs
        let spans =
            executions
            |> List.choose (fun e ->
                match e.StartedAt, e.FinalizedAt with
                | Some startedAt, Some finalizedAt ->
                    let start = parseEpochMs startedAt
                    let finish = parseEpochMs finalizedAt
                    if finish >= start then Some(start, finish) else None
                | _ -> None)

        let fullyFinalized = executions.Length > 0 && spans.Length = executions.Length
        let earliestStartedAt = if starts.IsEmpty then None else Some(DateTimeOffset.FromUnixTimeMilliseconds(List.min starts).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))

        if spans.IsEmpty then
            { FullyFinalized = fullyFinalized
              FinalizedExecutionCount = 0
              ActiveExecutionCount = executions.Length
              EarliestStartedAt = earliestStartedAt
              LatestFinalizedAt = None
              CalendarSpanMs = None
              TotalExecutionWallMs = None
              OverlappingExecutionMs = None }
        else
            let ordered = spans |> List.sortWith (fun (startA, endA) (startB, endB) -> if startA <> startB then compare startA startB else compare endA endB)

            let unionMs =
                let firstStart, firstEnd = List.head ordered

                let finalUnion, finalStart, finalEnd =
                    ordered
                    |> List.tail
                    |> List.fold
                        (fun (union, currentStart, currentEnd) (spanStart, spanEnd) ->
                            if spanStart <= currentEnd then
                                (union, currentStart, max currentEnd spanEnd)
                            else
                                (union + (currentEnd - currentStart), spanStart, spanEnd))
                        (0L, firstStart, firstEnd)

                finalUnion + (finalEnd - finalStart)

            let totalExecutionWallMs = spans |> List.sumBy (fun (start, finish) -> max 0L (finish - start))
            let latestFinalizedAtMs = spans |> List.map snd |> List.max
            let earliestStartMs = spans |> List.map fst |> List.min

            { FullyFinalized = fullyFinalized
              FinalizedExecutionCount = spans.Length
              ActiveExecutionCount = executions.Length - spans.Length
              EarliestStartedAt = earliestStartedAt
              LatestFinalizedAt = Some(DateTimeOffset.FromUnixTimeMilliseconds(latestFinalizedAtMs).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
              CalendarSpanMs = if fullyFinalized then Some(latestFinalizedAtMs - earliestStartMs) else None
              TotalExecutionWallMs = if fullyFinalized then Some totalExecutionWallMs else None
              OverlappingExecutionMs = if fullyFinalized then Some(max 0L (totalExecutionWallMs - unionMs)) else None }

/// Mirrors production's four aggregation strategies inside
/// `summarizeTelemetry`, applied to one already-grouped set of measurements
/// (same `id`/`unit`/`currency`/`dimensions`).
[<RequireQualifiedAccess>]
module MetricAggregation =
    type Sample =
        { CollectedAt: string
          Value: float
          Provider: string
          Runtime: string
          SessionId: string option
          ExecutionId: string }

    let compute (metricId: string) (aggregation: string) (samples: Sample list) : float option * string option =
        match aggregation with
        | "sum" ->
            let value = samples |> List.sumBy (fun s -> s.Value)
            let note = if metricId = "time.wall_ms" then Some "sum of execution spans; may exceed calendarSpanMs when executions overlap" else None
            Some value, note
        | "maximum" -> Some(samples |> List.map (fun s -> s.Value) |> List.max), None
        | "latest-per-session" ->
            let sessionKey (sample: Sample) =
                match sample.SessionId with
                | Some sessionId -> $"{sample.Provider}\000{sample.Runtime}\000{sessionId}"
                | None -> $"execution:{sample.ExecutionId}"

            let bySession =
                samples
                |> List.fold
                    (fun (chosen: Map<string, Sample>) sample ->
                        let key = sessionKey sample

                        match chosen.TryFind key with
                        | Some previous when String.CompareOrdinal(previous.CollectedAt, sample.CollectedAt) < 0 -> chosen.Add(key, sample)
                        | Some _ -> chosen
                        | None -> chosen.Add(key, sample))
                    Map.empty

            let value = bySession |> Map.toList |> List.sumBy (fun (_, sample) -> sample.Value)
            Some value, Some "latest value per unique provider session; cumulative snapshots are not summed"
        | "none" -> None, Some "not aggregated; inspect per-execution measurements"
        | _ ->
            let latest = samples |> List.sortWith (fun a b -> String.CompareOrdinal(a.CollectedAt, b.CollectedAt)) |> List.last
            Some latest.Value, None

/// Mirrors production `summarizeTelemetry` (`tools/ros_telemetry.mjs`): the
/// pure aggregation over already-parsed execution records (parsing and
/// registry lookup are the caller's job -- this module only groups and
/// aggregates).
[<RequireQualifiedAccess>]
module TelemetrySummary =
    let summarize (workItemId: string option) (executions: SummaryExecution list) : TelemetrySummary =
        let providers = executions |> List.map (fun e -> e.Provider) |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        let runtimes = executions |> List.map (fun e -> e.Runtime) |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

        let groupKey (metric: SummaryMetricMeasurement) =
            let currency = metric.Currency |> Option.defaultValue ""
            $"{metric.Id}\000{metric.Unit}\000{currency}\000{metric.DimensionsKey}"

        let initialState: string list * Map<string, (SummaryExecution * SummaryMetricMeasurement) list> = [], Map.empty

        let orderedKeys, grouped =
            executions
            |> List.collect (fun execution -> execution.Metrics |> List.map (fun metric -> execution, metric))
            |> List.fold
                (fun (order, byKey) (execution, metric) ->
                    let key = groupKey metric

                    match byKey |> Map.tryFind key with
                    | Some existing -> order, byKey |> Map.add key (existing @ [ execution, metric ])
                    | None -> order @ [ key ], byKey |> Map.add key [ execution, metric ])
                initialState

        let metrics =
            orderedKeys
            |> List.map (fun key ->
                let entries = grouped |> Map.find key
                let _, firstMetric = List.head entries
                let aggregation = firstMetric.RegistryAggregation |> Option.defaultValue firstMetric.SelfAggregation

                let samples =
                    entries
                    |> List.map (fun (execution, metric) ->
                        { MetricAggregation.CollectedAt = metric.CollectedAt
                          MetricAggregation.Value = metric.Value
                          MetricAggregation.Provider = execution.Provider
                          MetricAggregation.Runtime = execution.Runtime
                          MetricAggregation.SessionId = execution.SessionId
                          MetricAggregation.ExecutionId = execution.ExecutionId }: MetricAggregation.Sample)

                let value, note = MetricAggregation.compute firstMetric.Id aggregation samples

                { Id = firstMetric.Id
                  Value = value
                  Unit = firstMetric.Unit
                  Currency = firstMetric.Currency
                  DimensionsKey = firstMetric.DimensionsKey
                  Aggregation = aggregation
                  Measurements = entries.Length
                  Note = note })
            |> List.sortWith (fun a b -> String.CompareOrdinal(a.Id, b.Id))

        { SchemaVersion = "1.0.0"
          WorkItemId = workItemId
          ExecutionCount = executions.Length
          Providers = providers
          Runtimes = runtimes
          Timing = TimingSummary.compute executions
          Metrics = metrics }
