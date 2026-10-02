namespace Ros.Domain.Planning

open System

/// Estimate error of one group of replayed predictions.
type ErrorSegment =
    { /// `all`, `task-class`, `provider`, `model` or `runtime`.
      Dimension: string
      Value: string
      Predictions: int
      WithinRange: int
      Coverage: decimal option
      MedianAbsoluteErrorMs: int64 option
      MedianRelativeError: decimal option }

/// One durable estimate-error measurement (PRX-PLAN-170..173): a replay's
/// summary, keyed by planner version, repository commit and as-of time.
type ErrorMeasurement =
    { Id: string
      PlannerVersion: string
      Commit: string
      AsOf: string
      Executions: int
      Segments: ErrorSegment list }

/// A measurement as the history view reports it.
type ErrorHistoryEntry =
    { Measurement: ErrorMeasurement
      Stale: bool }

/// The values of one segment over time.
type ErrorSeries =
    { Dimension: string
      Value: string
      Points: (string * ErrorSegment) list }

type ErrorHistoryView =
    { AsOf: string
      HorizonDays: int
      Entries: ErrorHistoryEntry list
      /// The latest measurement inside the horizon, if any.
      Authoritative: ErrorMeasurement option
      Series: ErrorSeries list
      Statement: string }

[<RequireQualifiedAccess>]
type ErrorRecordOutcome =
    | Recorded of ErrorMeasurement
    | AlreadyRecorded of ErrorMeasurement
    /// The key is taken by a measurement with different content.
    | KeyConflict of existing: ErrorMeasurement * computed: ErrorMeasurement

/// Persisted planner estimate error (PRX-PLAN-170..173). Measuring and
/// viewing are pure; only `plan record-error` stores anything.
[<RequireQualifiedAccess>]
module ErrorHistory =
    /// Days after which a measurement is reported as stale and is no longer
    /// authoritative, unless planner configuration says otherwise.
    let defaultHorizonDays = PlannerConfiguration.defaults.EstimateErrorHorizonDays

    let private medianOf (values: 'a list) =
        match List.sort values with
        | [] -> None
        | sorted -> Some(List.item ((sorted.Length - 1) / 2) sorted)

    /// A value is "recorded" when telemetry names it; `unknown` is not a value.
    let private recorded (value: string) =
        if String.IsNullOrWhiteSpace value || value = "unknown" then None else Some value

    let private segment dimension value (predictions: ReplayPrediction list) =
        let within = predictions |> List.filter (fun prediction -> prediction.WithinRange = Some true) |> List.length

        { Dimension = dimension
          Value = value
          Predictions = predictions.Length
          WithinRange = within
          Coverage = if predictions.IsEmpty then None else Some(Math.Round(decimal within / decimal predictions.Length, 4))
          MedianAbsoluteErrorMs = predictions |> List.choose _.AbsoluteErrorMs |> medianOf
          MedianRelativeError = predictions |> List.choose _.RelativeError |> medianOf }

    /// The measurement key's identity: the same planner version, commit and
    /// as-of time always name the same measurement.
    let idFor (plannerVersion: string) (commit: string) (asOf: string) =
        "ERR-" + (Snapshot.hash $"{plannerVersion}\n{commit}\n{asOf}").Substring(0, 24)

    /// Replays the history finalized at or before `asOf` (no hindsight, as in
    /// `Replay.predict`) and summarises its error overall, by task class and,
    /// where telemetry recorded them, by provider, model and runtime.
    let measure (plannerVersion: string) (commit: string) (asOf: DateTimeOffset) (asOfText: string) (executions: HistoricalExecution list) : ErrorMeasurement =
        let samples = History.samples executions |> List.filter (fun sample -> sample.FinalizedAt <= asOf)
        let byExecution = samples |> List.map (fun sample -> sample.ExecutionId, sample) |> Map.ofList
        let predicted = samples |> List.map (Replay.predict samples) |> List.filter (fun prediction -> prediction.Predicted.Expected.IsSome)

        let grouped dimension (key: ReplayPrediction -> string option) =
            predicted
            |> List.choose (fun prediction -> key prediction |> Option.map (fun value -> value, prediction))
            |> List.groupBy fst
            |> List.sortWith (fun (left, _) (right, _) -> Text.ordinal left right)
            |> List.map (fun (value, entries) -> segment dimension value (entries |> List.map snd))

        let sampleField (field: History.DurationSample -> string option) (prediction: ReplayPrediction) =
            byExecution |> Map.tryFind prediction.ExecutionId |> Option.bind field

        { Id = idFor plannerVersion commit asOfText
          PlannerVersion = plannerVersion
          Commit = commit
          AsOf = asOfText
          Executions = samples.Length
          Segments =
            [ yield segment "all" "all" predicted
              yield! grouped "task-class" (fun prediction -> Some prediction.TaskClass)
              yield! grouped "provider" (sampleField (fun sample -> recorded sample.Provider))
              yield! grouped "model" (sampleField (fun sample -> sample.Model |> Option.bind recorded))
              yield! grouped "runtime" (sampleField (fun sample -> recorded sample.Runtime)) ] }

    /// Recording is idempotent: an identical measurement under the same key
    /// is already recorded; a different one under the same key is refused.
    let record (existing: ErrorMeasurement list) (computed: ErrorMeasurement) =
        match existing |> List.tryFind (fun measurement -> measurement.Id = computed.Id) with
        | Some stored when stored = computed -> ErrorRecordOutcome.AlreadyRecorded stored
        | Some stored -> ErrorRecordOutcome.KeyConflict(stored, computed)
        | None -> ErrorRecordOutcome.Recorded computed

    let private chronological (left: ErrorMeasurement) (right: ErrorMeasurement) =
        match Text.ordinal left.AsOf right.AsOf with
        | 0 -> Text.ordinal left.Id right.Id
        | order -> order

    /// Error over time. A measurement older than `horizonDays` before
    /// `asOf` is stale: it is still shown, but never authoritative.
    let view (asOf: DateTimeOffset) (asOfText: string) (horizonDays: int) (measurements: ErrorMeasurement list) : ErrorHistoryView =
        let cutoff = asOf.AddDays(float -horizonDays)

        let entries =
            measurements
            |> List.filter (fun measurement -> Text.tryTimestamp measurement.AsOf |> Option.exists (fun at -> at <= asOf))
            |> List.sortWith chronological
            |> List.map (fun measurement ->
                { Measurement = measurement
                  Stale = Text.tryTimestamp measurement.AsOf |> Option.forall (fun at -> at < cutoff) })

        let authoritative = entries |> List.filter (fun entry -> not entry.Stale) |> List.tryLast |> Option.map _.Measurement

        let series =
            entries
            |> List.collect (fun entry -> entry.Measurement.Segments |> List.map (fun segment -> (segment.Dimension, segment.Value), (entry.Measurement.AsOf, segment)))
            |> List.groupBy fst
            |> List.sortWith (fun ((leftDimension, leftValue), _) ((rightDimension, rightValue), _) ->
                match Text.ordinal leftDimension rightDimension with
                | 0 -> Text.ordinal leftValue rightValue
                | order -> order)
            |> List.map (fun ((dimension, value), points) -> { Dimension = dimension; Value = value; Points = points |> List.map snd })

        let stale = entries |> List.filter _.Stale |> List.length

        { AsOf = asOfText
          HorizonDays = horizonDays
          Entries = entries
          Authoritative = authoritative
          Series = series
          Statement =
            match authoritative with
            | Some current ->
                $"{entries.Length} measurement(s); the latest inside the {horizonDays}-day horizon ({current.AsOf}, planner {current.PlannerVersion}) is authoritative; {stale} older one(s) are stale and shown for history only. Segments are observations of recorded telemetry, not claims about any provider."
            | None when entries.IsEmpty -> "No estimate-error measurement is recorded; run 'praxis plan record-error' to record one."
            | None ->
                $"{entries.Length} measurement(s), all older than the {horizonDays}-day horizon: none is authoritative. Record a current one with 'praxis plan record-error'." }

    // ---- validation of the stored history ------------------------------------

    let private isHex (value: string) =
        (value.Length = 40 || value.Length = 64) && value |> Seq.forall (fun character -> Char.IsDigit character || (character >= 'a' && character <= 'f'))

    /// Problems with stored measurements, as (measurement id, field, message).
    let findings (measurements: ErrorMeasurement list) : (string * string * string) list =
        let perMeasurement (measurement: ErrorMeasurement) =
            [ if measurement.Id <> idFor measurement.PlannerVersion measurement.Commit measurement.AsOf then
                  yield measurement.Id, "id", "does not match its planner version, commit and as-of time"
              if String.IsNullOrWhiteSpace measurement.PlannerVersion then
                  yield measurement.Id, "plannerVersion", "is required"
              if not (isHex measurement.Commit) then
                  yield measurement.Id, "commit", "must be a full lowercase hexadecimal commit SHA"
              if (Text.tryTimestamp measurement.AsOf).IsNone then
                  yield measurement.Id, "asOf", "must be an ISO-8601 timestamp"
              if measurement.Executions < 0 then
                  yield measurement.Id, "executions", "must not be negative"
              if not (measurement.Segments |> List.exists (fun segment -> segment.Dimension = "all")) then
                  yield measurement.Id, "segments", "must include the overall ('all') segment"
              for segment in measurement.Segments do
                  let field = $"segments[{segment.Dimension}={segment.Value}]"

                  if segment.Predictions < 0 || segment.WithinRange < 0 || segment.WithinRange > segment.Predictions then
                      yield measurement.Id, field, "within-range count must lie between 0 and the number of predictions"
                  if segment.Coverage |> Option.exists (fun coverage -> coverage < 0m || coverage > 1m) then
                      yield measurement.Id, field, "coverage must lie between 0 and 1"
                  if segment.MedianAbsoluteErrorMs |> Option.exists (fun value -> value < 0L) || segment.MedianRelativeError |> Option.exists (fun value -> value < 0m) then
                      yield measurement.Id, field, "errors must not be negative" ]

        let duplicates =
            measurements
            |> List.countBy _.Id
            |> List.filter (snd >> (<) 1)
            |> List.map (fun (id, _) -> id, "id", "is recorded more than once")

        (measurements |> List.collect perMeasurement) @ duplicates
