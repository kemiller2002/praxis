namespace Ros.Domain.Planning

open System

type ReplayPrediction =
    { ExecutionId: string
      WorkItemId: string
      TaskClass: string
      TrainingSamples: int
      Predicted: Estimate<int64>
      ActualMs: int64
      WithinRange: bool option
      AbsoluteErrorMs: int64 option
      RelativeError: decimal option }

type ReplayClassAccuracy =
    { TaskClass: string
      Predictions: int
      WithinRange: int
      MedianAbsoluteErrorMs: int64 option
      MedianRelativeError: decimal option }

type OverlapObservation =
    { PeakConcurrency: int
      OverlappingMs: int64
      CalendarSpanMs: int64 }

type OrderingAgreement =
    { ComparablePairs: int
      ConcordantPairs: int
      Agreement: decimal option
      Statement: string }

type ReplayReport =
    { Executions: int
      Predicted: int
      Unpredictable: int
      WithinRange: int
      Coverage: decimal option
      MedianAbsoluteErrorMs: int64 option
      MedianRelativeError: decimal option
      ByClass: ReplayClassAccuracy list
      Overlap: OverlapObservation
      Ordering: OrderingAgreement
      Cost: CostEvidenceSummary
      Predictions: ReplayPrediction list
      Statement: string }

/// PRX-PLAN-150..152, 170: replay completed history, predicting each
/// execution only from executions that had finished before it started.
[<RequireQualifiedAccess>]
module Replay =
    let private medianOf (values: 'a list) =
        match List.sort values with
        | [] -> None
        | sorted -> Some(List.item ((sorted.Length - 1) / 2) sorted)

    let predict (samples: History.DurationSample list) (sample: History.DurationSample) : ReplayPrediction =
        // No hindsight: only executions finalized strictly before this one started.
        let training = samples |> List.filter (fun candidate -> candidate.FinalizedAt < sample.StartedAt)
        let taskClass = sample.Classes |> List.tryHead |> Option.defaultValue History.pooledClass
        let predicted, _ = History.estimateFor (History.distributions training) taskClass

        let within =
            match predicted.Lower, predicted.Upper with
            | Some lower, Some upper -> Some(sample.ProductiveMs >= lower && sample.ProductiveMs <= upper)
            | _ -> None

        let absolute = predicted.Expected |> Option.map (fun expected -> abs (expected - sample.ProductiveMs))

        { ExecutionId = sample.ExecutionId
          WorkItemId = sample.WorkItemId
          TaskClass = taskClass
          TrainingSamples = training.Length
          Predicted = predicted
          ActualMs = sample.ProductiveMs
          WithinRange = within
          AbsoluteErrorMs = absolute
          RelativeError = absolute |> Option.map (fun error -> Math.Round(decimal error / decimal (max 60_000L sample.ProductiveMs), 4)) }

    /// Peak concurrency and overlapping wall time of recorded executions.
    let overlap (samples: History.DurationSample list) : OverlapObservation =
        match samples with
        | [] -> { PeakConcurrency = 0; OverlappingMs = 0L; CalendarSpanMs = 0L }
        | _ ->
            let events =
                samples
                |> List.collect (fun sample -> [ sample.StartedAt, 1; sample.FinalizedAt, -1 ])
                |> List.sortWith (fun (leftTime, leftDelta) (rightTime, rightDelta) ->
                    match compare leftTime rightTime with
                    | 0 -> compare leftDelta rightDelta
                    | order -> order)

            let _, _, peak, overlapping, _ =
                events
                |> List.fold
                    (fun (active, last: DateTimeOffset option, peak, overlapping, _) (time, delta) ->
                        let overlapping =
                            match last with
                            | Some previous when active >= 2 -> overlapping + int64 (time - previous).TotalMilliseconds
                            | _ -> overlapping

                        let active = active + delta
                        active, Some time, max peak active, overlapping, ())
                    (0, None, 0, 0L, ())

            let start = samples |> List.map (fun sample -> sample.StartedAt) |> List.min
            let finish = samples |> List.map (fun sample -> sample.FinalizedAt) |> List.max

            { PeakConcurrency = peak
              OverlappingMs = overlapping
              CalendarSpanMs = int64 (finish - start).TotalMilliseconds }

    /// How often baseline order (backlog creation) matched the order work
    /// actually started.
    let ordering (queue: PlanningQueueItem list) (samples: History.DurationSample list) : OrderingAgreement =
        let started =
            samples
            |> List.groupBy (fun sample -> sample.WorkItemId)
            |> List.map (fun (id, entries) -> id, entries |> List.map (fun sample -> sample.StartedAt) |> List.min)
            |> Map.ofList

        let comparable =
            queue
            |> List.choose (fun item ->
                match item.CreatedAt |> Option.bind Text.tryTimestamp, started.TryFind item.Id with
                | Some created, Some start -> Some(item.Id, created, start)
                | _ -> None)
            |> List.sortWith (fun (left, _, _) (right, _, _) -> Text.ordinal left right)

        let pairs =
            comparable
            |> List.mapi (fun index left -> comparable |> List.skip (index + 1) |> List.map (fun right -> left, right))
            |> List.concat
            |> List.filter (fun ((_, createdA, startA), (_, createdB, startB)) -> createdA <> createdB && startA <> startB)

        let concordant =
            pairs |> List.filter (fun ((_, createdA, startA), (_, createdB, startB)) -> (createdA < createdB) = (startA < startB)) |> List.length

        let agreement = if pairs.IsEmpty then None else Some(Math.Round(decimal concordant / decimal pairs.Length, 4))

        { ComparablePairs = pairs.Length
          ConcordantPairs = concordant
          Agreement = agreement
          Statement =
            match agreement with
            | Some value ->
                let percent = Math.Round(value * 100m, 1)
                $"Baseline creation order matched the actual start order for {concordant} of {pairs.Length} comparable pairs ({percent}%%)."
            | None -> "No pair of backlog items has both a creation time and a recorded start; ordering agreement is unknown." }

    let replay (configuration: PlannerConfiguration) (queue: PlanningQueueItem list) (executions: HistoricalExecution list) : ReplayReport =
        let samples = History.samples executions
        let predictions = samples |> List.map (predict samples)
        let predicted = predictions |> List.filter (fun prediction -> prediction.Predicted.Expected.IsSome)
        let within = predicted |> List.filter (fun prediction -> prediction.WithinRange = Some true) |> List.length

        let accuracy (taskClass: string) (entries: ReplayPrediction list) =
            { TaskClass = taskClass
              Predictions = entries.Length
              WithinRange = entries |> List.filter (fun prediction -> prediction.WithinRange = Some true) |> List.length
              MedianAbsoluteErrorMs = entries |> List.choose (fun prediction -> prediction.AbsoluteErrorMs) |> medianOf
              MedianRelativeError = entries |> List.choose (fun prediction -> prediction.RelativeError) |> medianOf }

        let byClass =
            predicted
            |> List.groupBy (fun prediction -> prediction.TaskClass)
            |> List.sortWith (fun (left, _) (right, _) -> Text.ordinal left right)
            |> List.map (fun (taskClass, entries) -> accuracy taskClass entries)

        let coverage = if predicted.IsEmpty then None else Some(Math.Round(decimal within / decimal predicted.Length, 4))

        { Executions = samples.Length
          Predicted = predicted.Length
          Unpredictable = samples.Length - predicted.Length
          WithinRange = within
          Coverage = coverage
          MedianAbsoluteErrorMs = predicted |> List.choose (fun prediction -> prediction.AbsoluteErrorMs) |> medianOf
          MedianRelativeError = predicted |> List.choose (fun prediction -> prediction.RelativeError) |> medianOf
          ByClass = byClass
          Overlap = overlap samples
          Ordering = ordering queue samples
          Cost = History.costSummary configuration executions
          Predictions = predictions
          Statement =
            "Each execution is predicted only from executions finalized before it started (no hindsight). Its task class comes from its own telemetry record, which is normally set when the execution starts. Predicted cost is unavailable wherever cost evidence is insufficient." }
