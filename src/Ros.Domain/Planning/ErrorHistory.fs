namespace Ros.Domain.Planning

open System
open System.Globalization

/// Estimate error of one population of replay predictions: the replay's own
/// aggregate shape (PRX-PLAN-152, 170).
type ErrorSummary =
    { Predictions: int
      WithinRange: int
      MedianAbsoluteErrorMs: int64 option
      MedianRelativeError: decimal option }

/// Error of the predictions sharing one recorded value of a dimension:
/// `task-class`, or the `provider`, `runtime` or `model` the telemetry
/// recorded (PRX-PLAN-171, 172). A measurement, never a claim.
type ErrorSegment =
    { Dimension: string
      Value: string
      Summary: ErrorSummary }

/// One durable estimate-error measurement, keyed by planner version,
/// repository commit and as-of time.
type ErrorMeasurement =
    { PlannerVersion: string
      Commit: string
      AsOf: string
      /// Finalized executions in the evidence window (finalized at or before
      /// `AsOf`).
      Executions: int
      /// SHA-256 over the canonical evidence window, so the same key over
      /// different evidence is detected rather than overwritten.
      InputFingerprint: string
      Overall: ErrorSummary
      Segments: ErrorSegment list }

[<RequireQualifiedAccess>]
type ErrorRecordOutcome =
    | Recorded
    | Unchanged

[<RequireQualifiedAccess>]
module ErrorRecordOutcome =
    let code outcome =
        match outcome with
        | ErrorRecordOutcome.Recorded -> "recorded"
        | ErrorRecordOutcome.Unchanged -> "unchanged"

type ErrorHistoryEntry =
    { Measurement: ErrorMeasurement
      AgeDays: decimal
      Stale: bool }

type ErrorSeriesPoint =
    { AsOf: string
      PlannerVersion: string
      Commit: string
      Summary: ErrorSummary
      Stale: bool }

/// One segment's error over time, oldest first. `Latest` is the newest
/// point inside the horizon; a stale point is never authoritative.
type ErrorSeries =
    { Dimension: string
      Value: string
      Points: ErrorSeriesPoint list
      Latest: ErrorSummary option }

type ErrorHistoryView =
    { AsOf: string
      HorizonDays: int
      Entries: ErrorHistoryEntry list
      /// Measurements dated after the view's as-of time; not read.
      Future: int
      Authoritative: ErrorMeasurement option
      Series: ErrorSeries list
      Statement: string }

/// PRX-PLAN-170..173: estimate error persisted over time. `measure` turns a
/// replay into a measurement, `record` adds it to the stored history
/// idempotently, `view` reports error over time with stale measurements
/// marked and never authoritative, and `findings` checks a stored history.
[<RequireQualifiedAccess>]
module ErrorHistory =
    let overallDimension = "overall"
    let taskClassDimension = "task-class"
    let dimensions = [ taskClassDimension; "provider"; "runtime"; "model" ]

    /// The canonical as-of text: UTC, millisecond precision, so equal
    /// instants written differently are the same key.
    let normalizeTimestamp (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)

    let private medianOf (values: 'a list) =
        match List.sort values with
        | [] -> None
        | sorted -> Some(List.item ((sorted.Length - 1) / 2) sorted)

    let summarize (predictions: ReplayPrediction list) : ErrorSummary =
        { Predictions = predictions.Length
          WithinRange = predictions |> List.filter (fun prediction -> prediction.WithinRange = Some true) |> List.length
          MedianAbsoluteErrorMs = predictions |> List.choose (fun prediction -> prediction.AbsoluteErrorMs) |> medianOf
          MedianRelativeError = predictions |> List.choose (fun prediction -> prediction.RelativeError) |> medianOf }

    /// A recorded identity value; `unknown` and blank are not recorded.
    let private recorded (value: string) =
        if String.IsNullOrWhiteSpace value || value = "unknown" then None else Some value

    let private segmentsOf (samples: Map<string, History.DurationSample>) (predicted: ReplayPrediction list) =
        let segment dimension (key: ReplayPrediction -> string option) =
            predicted
            |> List.choose (fun prediction -> key prediction |> Option.map (fun value -> value, prediction))
            |> List.groupBy fst
            |> List.sortWith (fun (left, _) (right, _) -> Text.ordinal left right)
            |> List.map (fun (value, entries) -> { Dimension = dimension; Value = value; Summary = entries |> List.map snd |> summarize })

        let identity (read: History.DurationSample -> string option) (prediction: ReplayPrediction) =
            samples.TryFind prediction.ExecutionId |> Option.bind read

        segment taskClassDimension (fun prediction -> Some prediction.TaskClass)
        @ segment "provider" (identity (fun sample -> recorded sample.Provider))
        @ segment "runtime" (identity (fun sample -> recorded sample.Runtime))
        @ segment "model" (identity (fun sample -> sample.Model |> Option.bind recorded))

    let private fingerprint (samples: History.DurationSample list) =
        samples
        |> List.map (fun sample ->
            String.Join(
                "|",
                [ sample.ExecutionId
                  sample.WorkItemId
                  String.Join(",", sample.Classes)
                  sample.StartedAt.ToString("O", CultureInfo.InvariantCulture)
                  sample.FinalizedAt.ToString("O", CultureInfo.InvariantCulture)
                  string sample.ProductiveMs
                  sample.Provider
                  sample.Runtime
                  sample.Model |> Option.defaultValue "-" ]
            ))
        |> String.concat "\n"
        |> Snapshot.hash

    /// Replays the executions finalized at or before `asOf` (no hindsight,
    /// PRX-PLAN-151) and summarizes the error of every prediction made,
    /// overall and per segment.
    let measure
        (configuration: PlannerConfiguration)
        (plannerVersion: string)
        (commit: string)
        (asOf: DateTimeOffset)
        (executions: HistoricalExecution list)
        : ErrorMeasurement =
        let window =
            executions
            |> List.filter (fun execution ->
                execution.Status = ExecutionStatus.Finalized
                && execution.FinalizedAt |> Option.bind Text.tryTimestamp |> Option.exists (fun finalized -> finalized <= asOf))

        let report = Replay.replay configuration [] window
        let samples = History.samples window
        let predicted = report.Predictions |> List.filter (fun prediction -> prediction.Predicted.Expected.IsSome)

        { PlannerVersion = plannerVersion
          Commit = commit
          AsOf = normalizeTimestamp asOf
          Executions = samples.Length
          InputFingerprint = fingerprint samples
          Overall = summarize predicted
          Segments = segmentsOf (samples |> List.map (fun sample -> sample.ExecutionId, sample) |> Map.ofList) predicted }

    let key (measurement: ErrorMeasurement) = measurement.PlannerVersion, measurement.Commit, measurement.AsOf

    let describeKey (measurement: ErrorMeasurement) =
        $"planner {measurement.PlannerVersion}, commit {measurement.Commit}, as of {measurement.AsOf}"

    let private compareMeasurements (left: ErrorMeasurement) (right: ErrorMeasurement) =
        [ Text.ordinal left.AsOf right.AsOf
          Text.ordinal left.PlannerVersion right.PlannerVersion
          Text.ordinal left.Commit right.Commit ]
        |> List.tryFind ((<>) 0)
        |> Option.defaultValue 0

    /// Canonical store order: as-of, then planner version, then commit.
    let canonical (measurements: ErrorMeasurement list) = measurements |> List.sortWith compareMeasurements

    /// Adds a measurement. The same key with the same content is unchanged
    /// (idempotent); the same key with different content is refused, never
    /// overwritten.
    let record (existing: ErrorMeasurement list) (measurement: ErrorMeasurement) : Result<ErrorMeasurement list * ErrorRecordOutcome, string> =
        match existing |> List.tryFind (fun stored -> key stored = key measurement) with
        | Some stored when stored = measurement -> Ok(existing, ErrorRecordOutcome.Unchanged)
        | Some _ ->
            Error
                $"a different measurement is already recorded for {describeKey measurement}; the stored measurement is kept (its evidence differs, for example uncommitted telemetry)"
        | None -> Ok(canonical (existing @ [ measurement ]), ErrorRecordOutcome.Recorded)

    let private percent (summary: ErrorSummary) =
        if summary.Predictions = 0 then "no predictions"
        else
            let share = Math.Round(decimal summary.WithinRange * 100m / decimal summary.Predictions, 1)
            $"{summary.WithinRange} of {summary.Predictions} predictions within range ({share.ToString(CultureInfo.InvariantCulture)}%%)"

    let private absolute (summary: ErrorSummary) =
        match summary.MedianAbsoluteErrorMs with
        | Some ms -> $"median absolute error {ms / 60_000L} min"
        | None -> "median absolute error unknown"

    /// Error over time as of `asOf`. A measurement older than `horizonDays`
    /// is stale: it is reported but never authoritative (PRX-PLAN-173).
    let view (horizonDays: int) (asOf: DateTimeOffset) (measurements: ErrorMeasurement list) : ErrorHistoryView =
        let dated =
            canonical measurements
            |> List.choose (fun measurement -> Text.tryTimestamp measurement.AsOf |> Option.map (fun at -> measurement, at))

        let known = dated |> List.filter (fun (_, at) -> at <= asOf)
        let horizon = TimeSpan.FromDays(float horizonDays)

        let entries =
            known
            |> List.map (fun (measurement, at) ->
                let age = asOf - at

                { Measurement = measurement
                  AgeDays = Math.Round(decimal age.TotalDays, 2)
                  Stale = age > horizon })

        let authoritative = entries |> List.filter (fun entry -> not entry.Stale) |> List.tryLast |> Option.map (fun entry -> entry.Measurement)

        let point (entry: ErrorHistoryEntry) (summary: ErrorSummary) =
            { AsOf = entry.Measurement.AsOf
              PlannerVersion = entry.Measurement.PlannerVersion
              Commit = entry.Measurement.Commit
              Summary = summary
              Stale = entry.Stale }

        let seriesOf dimension value (points: ErrorSeriesPoint list) =
            { Dimension = dimension
              Value = value
              Points = points
              Latest = points |> List.filter (fun current -> not current.Stale) |> List.tryLast |> Option.map (fun current -> current.Summary) }

        let overall = seriesOf overallDimension "all" (entries |> List.map (fun entry -> point entry entry.Measurement.Overall))

        let segmented =
            entries
            |> List.collect (fun entry -> entry.Measurement.Segments |> List.map (fun segment -> (segment.Dimension, segment.Value), point entry segment.Summary))
            |> List.groupBy fst
            |> List.sortWith (fun ((leftDimension, leftValue), _) ((rightDimension, rightValue), _) ->
                let byDimension = compare (List.findIndex ((=) leftDimension) dimensions) (List.findIndex ((=) rightDimension) dimensions)
                if byDimension <> 0 then byDimension else Text.ordinal leftValue rightValue)
            |> List.map (fun ((dimension, value), points) -> seriesOf dimension value (points |> List.map snd))

        let stale = entries |> List.filter (fun entry -> entry.Stale) |> List.length

        let statement =
            match entries, authoritative with
            | [], _ -> "No estimate-error measurement is recorded as of this time; record one with 'plan record-error'."
            | _, None ->
                $"All {entries.Length} measurement(s) are older than the {horizonDays}-day horizon; none is authoritative. Record a current one with 'plan record-error'."
            | _, Some latest ->
                $"Authoritative measurement: {describeKey latest}: {percent latest.Overall}, {absolute latest.Overall}. {entries.Length} measurement(s) shown, {stale} stale (older than {horizonDays} days). Segments by provider, runtime and model are measurements only."

        { AsOf = normalizeTimestamp asOf
          HorizonDays = horizonDays
          Entries = entries
          Future = dated.Length - known.Length
          Authoritative = authoritative
          Series = overall :: segmented
          Statement = statement }

    let private summaryFindings (name: string) (summary: ErrorSummary) =
        [ if summary.Predictions < 0 then $"{name}.predictions must not be negative"
          if summary.WithinRange < 0 || summary.WithinRange > summary.Predictions then $"{name}.withinRange must be between 0 and predictions"
          if summary.MedianAbsoluteErrorMs |> Option.exists (fun value -> value < 0L) then $"{name}.medianAbsoluteErrorMs must not be negative"
          if summary.MedianRelativeError |> Option.exists (fun value -> value < 0m) then $"{name}.medianRelativeError must not be negative"
          if summary.Predictions = 0 && (summary.MedianAbsoluteErrorMs.IsSome || summary.MedianRelativeError.IsSome) then $"{name} has error medians without predictions" ]

    /// Clock-free consistency findings over a stored history, as
    /// (measurement name, message). Staleness is not a finding: it depends
    /// on when the history is read.
    let findings (measurements: ErrorMeasurement list) : (string * string) list =
        let allowed = String.concat ", " dimensions
        let name (measurement: ErrorMeasurement) = $"measurements[{describeKey measurement}]"

        let perMeasurement =
            measurements
            |> List.collect (fun measurement ->
                let label = name measurement

                [ if String.IsNullOrWhiteSpace measurement.PlannerVersion then "plannerVersion must not be empty"
                  if String.IsNullOrWhiteSpace measurement.Commit then "commit must not be empty"
                  yield!
                      match Text.tryTimestamp measurement.AsOf with
                      | Some at when normalizeTimestamp at = measurement.AsOf -> []
                      | Some _ -> [ "asOf must be a UTC timestamp with millisecond precision (yyyy-MM-ddTHH:mm:ss.fffZ)" ]
                      | None -> [ "asOf must be a timestamp" ]
                  if measurement.Executions < measurement.Overall.Predictions then "executions must be at least the overall prediction count"
                  if measurement.InputFingerprint.Length <> 64 || not (measurement.InputFingerprint |> Seq.forall Uri.IsHexDigit) then
                      "inputFingerprint must be a SHA-256 hex digest"
                  yield! summaryFindings "overall" measurement.Overall
                  for segment in measurement.Segments do
                      let segmentName = $"segments[{segment.Dimension}={segment.Value}]"
                      if not (List.contains segment.Dimension dimensions) then $"{segmentName}.dimension must be one of {allowed}"
                      if segment.Summary.Predictions > measurement.Overall.Predictions then $"{segmentName}.predictions exceed the overall predictions"
                      yield! summaryFindings segmentName segment.Summary
                  yield!
                      measurement.Segments
                      |> List.countBy (fun segment -> segment.Dimension, segment.Value)
                      |> List.filter (fun (_, count) -> count > 1)
                      |> List.map (fun ((dimension, value), _) -> $"segment {dimension}={value} is recorded more than once") ]
                |> List.map (fun message -> label, message))

        let duplicates =
            measurements
            |> List.countBy key
            |> List.filter (fun (_, count) -> count > 1)
            |> List.map (fun ((version, commit, asOf), count) ->
                "measurements", $"{count} measurements share the key planner {version}, commit {commit}, as of {asOf}")

        let order =
            if canonical measurements = measurements then []
            else [ "measurements", "measurements must be ordered by asOf, then plannerVersion, then commit" ]

        perMeasurement @ duplicates @ order
