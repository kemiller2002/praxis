namespace Praxis.Domain.Planning

open System

/// Replay accuracy for one task class, as recorded.
type CalibrationClass =
    { TaskClass: string
      Predictions: int
      WithinRange: int
      MedianAbsoluteErrorMs: int64 option }

/// `praxis.plan-calibration/1`: one recorded replay (PRX-PLAN-170). The
/// history of these entries is how estimate error is tracked over time.
type CalibrationEntry =
    { RecordedAt: string
      PlannerVersion: string
      WorkStateFingerprint: string
      Executions: int
      Predicted: int
      WithinRange: int
      Coverage: decimal option
      MedianAbsoluteErrorMs: int64 option
      MedianRelativeError: decimal option
      ByClass: CalibrationClass list
      CostPredicted: int
      CostWithinRange: int
      CostMedianAbsoluteError: decimal option
      CostCurrency: string option }

type CalibrationTrend =
    { Entries: int
      /// Coverage (share of actuals within the predicted range) of the
      /// first and latest recorded entries that have one.
      FirstCoverage: decimal option
      LatestCoverage: decimal option
      FirstMedianAbsoluteErrorMs: int64 option
      LatestMedianAbsoluteErrorMs: int64 option
      Statement: string }

/// PRX-PLAN-170: estimate error tracked over time as an append-only history
/// of replay results. Recording is explicit (`plan replay --record`); every
/// other planning command stays read-only (PRX-PLAN-001).
[<RequireQualifiedAccess>]
module Calibration =
    let entry (recordedAt: string) (snapshot: PlanSnapshot) (report: ReplayReport) : CalibrationEntry =
        { RecordedAt = recordedAt
          PlannerVersion = snapshot.PlannerVersion
          WorkStateFingerprint = snapshot.WorkStateFingerprint
          Executions = report.Executions
          Predicted = report.Predicted
          WithinRange = report.WithinRange
          Coverage = report.Coverage
          MedianAbsoluteErrorMs = report.MedianAbsoluteErrorMs
          MedianRelativeError = report.MedianRelativeError
          ByClass =
            report.ByClass
            |> List.map (fun accuracy ->
                { TaskClass = accuracy.TaskClass
                  Predictions = accuracy.Predictions
                  WithinRange = accuracy.WithinRange
                  MedianAbsoluteErrorMs = accuracy.MedianAbsoluteErrorMs })
          CostPredicted = report.CostAccuracy.Predicted
          CostWithinRange = report.CostAccuracy.WithinRange
          CostMedianAbsoluteError = report.CostAccuracy.MedianAbsoluteError
          CostCurrency = report.CostAccuracy.Currency }

    /// The same work state replayed by the same planner version is one
    /// measurement; recording it again changes nothing.
    let sameMeasurement (left: CalibrationEntry) (right: CalibrationEntry) =
        left.WorkStateFingerprint = right.WorkStateFingerprint && left.PlannerVersion = right.PlannerVersion

    let append (history: CalibrationEntry list) (candidate: CalibrationEntry) : CalibrationEntry list * bool =
        if history |> List.exists (sameMeasurement candidate) then history, false
        else history @ [ candidate ], true

    let trend (history: CalibrationEntry list) : CalibrationTrend =
        let coverage = history |> List.choose (fun entry -> entry.Coverage)
        let errors = history |> List.choose (fun entry -> entry.MedianAbsoluteErrorMs)
        let percent (value: decimal) = Math.Round(value * 100m, 1)

        let statement =
            match coverage with
            | [] when history.IsEmpty -> "No replay has been recorded yet; run 'plan replay --record' to start the estimate-error history."
            | [] -> $"{history.Length} replay(s) recorded; none had a predictable execution."
            | [ only ] -> $"1 recorded replay with predictions: {percent only}%% of actuals within range. A trend needs at least two."
            | first :: _ ->
                let latest = List.last coverage
                let direction = if latest > first then "improved" elif latest < first then "worsened" else "unchanged"
                $"{coverage.Length} recorded replay(s) with predictions: coverage {direction} from {percent first}%% to {percent latest}%%. Observation only; the planner does not claim a cause."

        { Entries = history.Length
          FirstCoverage = coverage |> List.tryHead
          LatestCoverage = coverage |> List.tryLast
          FirstMedianAbsoluteErrorMs = errors |> List.tryHead
          LatestMedianAbsoluteErrorMs = errors |> List.tryLast
          Statement = statement }
