namespace Praxis.Cli

open System
open Praxis.Contracts.Planning
open Praxis.Domain.Planning

/// Text rendering of the planner's evidence-about-itself reports: replay
/// accuracy with cost and the recorded calibration history (PRX-PLAN-150..152,
/// 170), and the follow-up of a saved plan (PRX-PLAN-162).
[<RequireQualifiedAccess>]
module PlanEvidenceText =
    let private duration = PlanningJson.describeDuration
    let private minutes (value: int64 option) = value |> Option.map (fun ms -> $"{ms / 60_000L} min") |> Option.defaultValue "unknown"
    let private percent (value: decimal option) = value |> Option.map (fun ratio -> $"{Math.Round(ratio * 100m, 1)}%%") |> Option.defaultValue "unknown"
    let private amount (value: decimal option) = value |> Option.map string |> Option.defaultValue "unknown"

    let replay (details: bool) (report: ReplayReport) (trend: CalibrationTrend) (recorded: bool option) =
        [ yield report.Statement
          yield ""
          yield $"Executions replayed: {report.Executions}; predicted {report.Predicted}; unpredictable (no prior evidence) {report.Unpredictable}"
          yield $"Actual within predicted range: {report.WithinRange} of {report.Predicted} ({percent report.Coverage})"
          yield $"Median absolute error {minutes report.MedianAbsoluteErrorMs}; median relative error {percent report.MedianRelativeError}"
          yield!
              report.ByClass
              |> List.map (fun accuracy ->
                  $"  {accuracy.TaskClass}: {accuracy.WithinRange}/{accuracy.Predictions} within range, median abs error {minutes accuracy.MedianAbsoluteErrorMs}, median relative {percent accuracy.MedianRelativeError}")
          yield $"Observed overlap: peak {report.Overlap.PeakConcurrency} concurrent execution(s), {report.Overlap.OverlappingMs / 60_000L} min overlapping over {report.Overlap.CalendarSpanMs / 3_600_000L} h (baseline models 1)"
          yield report.Ordering.Statement
          yield report.Cost.Statement
          yield report.CostAccuracy.Statement
          if report.CostAccuracy.Predicted > 0 then
              let currency = report.CostAccuracy.Currency |> Option.defaultValue ""
              yield $"  median absolute cost error {amount report.CostAccuracy.MedianAbsoluteError} {currency}"
          match recorded with
          | Some true -> yield "Recorded this replay in .ros/planning/calibration.jsonl."
          | Some false -> yield "This work state and planner version were already recorded; nothing was added."
          | None -> ()
          yield $"Calibration history: {trend.Statement}"
          if details then
              yield ""

              yield!
                  report.Predictions
                  |> List.map (fun prediction ->
                      $"  {prediction.ExecutionId} {prediction.WorkItemId} [{prediction.TaskClass}] predicted {duration prediction.Predicted}, actual {prediction.ActualMs / 60_000L} min")

              yield!
                  report.CostAccuracy.Predictions
                  |> List.map (fun prediction ->
                      let predicted =
                          match prediction.Predicted.Lower, prediction.Predicted.Upper with
                          | Some lower, Some upper -> $"{lower.Amount}-{upper.Amount} {upper.Currency}"
                          | _ -> "unknown"

                      $"  {prediction.ExecutionId} {prediction.WorkItemId} cost predicted {predicted}, actual {prediction.Actual.Amount} {prediction.Actual.Currency}") ]

    let review (review: PlanReview) =
        [ (if review.Freshness.Stale then "The plan is STALE: material state changed since it was computed." else "The plan is current: no material state change since it was computed.")
          yield! review.Freshness.Changes |> List.map (fun change -> $"  {PlanChangeKind.code change.Kind}: {change.Message}")
          ""
          review.Statement
          yield!
              review.Outcomes
              |> List.map (fun outcome ->
                  $"  wave {outcome.RecommendedWave} {RecommendedAction.code outcome.RecommendedAction} {outcome.WorkItem}: {outcome.Outcome} ({outcome.LifecycleThen} -> {outcome.LifecycleNow})")
          ""
          review.FollowUp.Statement
          yield!
              review.FollowUp.Durations
              |> List.map (fun entry -> $"  {entry.WorkItem}: predicted {duration entry.Predicted}, observed {minutes entry.ObservedMs}") ]
