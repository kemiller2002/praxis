namespace Ros.Domain.Work

open System
open System.Globalization

/// A stored `work.checkpointed` event as validation sees it.
type CheckpointEventFacts =
    { EventId: string
      /// Whether the recorded `eventId` still matches the event's content.
      IdMatchesContent: bool
      SchemaVersion: string option
      WorkItemId: string
      OccurredAt: string
      ExecutionId: string option
      StepId: string option
      Checkpoint: Result<Checkpoint, string list> }

type ExecutionFacts =
    { ExecutionId: string
      WorkItemId: string
      StartedAt: string
      StartedSteps: string list }

type ProjectionFacts =
    { WorkItemId: string
      Projection: Result<RecordedCheckpoint option, string list> }

/// A stored `work.continued` event as validation sees it.
type ContinuationFacts =
    { EventId: string
      IdMatchesContent: bool
      WorkItemId: string
      SuccessorExecutionId: string option
      PredecessorExecutionId: string option
      CheckpointId: string option }

type CheckpointValidationInput =
    { Events: CheckpointEventFacts list
      Continuations: ContinuationFacts list
      Executions: ExecutionFacts list
      /// Each execution's recorded `parentExecutionId`.
      Parents: Map<string, string>
      WorkItemIds: string list
      Projections: ProjectionFacts list }

type CheckpointFinding =
    { Path: string
      Field: string
      Message: string }

/// Offline, deterministic structural validation of checkpoint history
/// (CONT-070). It never needs the network: whether a checkpoint is still
/// recoverable is a runtime observation, not a property of history.
[<RequireQualifiedAccess>]
module CheckpointValidation =
    let supportedSchemaVersions = set [ "1.0.0" ]

    let private instant (value: string) =
        match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
        | true, parsed -> Some parsed
        | _ -> None

    let private eventPath (eventId: string) = $".ros/events/events.jsonl#{eventId}"
    let private contextPath (workItemId: string) = $".ros/context/current.json#{workItemId}"

    let private eventFindings (input: CheckpointValidationInput) (event: CheckpointEventFacts) =
        let finding field message =
            { Path = eventPath event.EventId
              Field = field
              Message = message }

        let execution =
            event.ExecutionId
            |> Option.bind (fun id -> input.Executions |> List.tryFind (fun candidate -> candidate.ExecutionId = id))

        [ if not event.IdMatchesContent then
              finding "eventId" "checkpoint event content does not match its eventId; checkpoint events must never be edited"
          match event.SchemaVersion with
          | Some version when supportedSchemaVersions.Contains version -> ()
          | Some version -> finding "schemaVersion" $"checkpoint event schema '{version}' is not supported"
          | None -> finding "schemaVersion" "checkpoint event has no schemaVersion"
          match event.Checkpoint with
          | Error problems ->
              for problem in problems do
                  finding "checkpoint" problem
          | Ok _ -> ()
          if not (List.contains event.WorkItemId input.WorkItemIds) then
              finding "workItem" $"checkpoint names work item '{event.WorkItemId}', which is not in repository context"
          match event.ExecutionId, execution with
          | None, _ -> ()
          | Some id, None -> finding "execution" $"checkpoint names execution '{id}', which has no record under .ros/telemetry/executions"
          | Some id, Some record when record.WorkItemId <> event.WorkItemId ->
              finding "execution" $"execution '{id}' belongs to '{record.WorkItemId}', not '{event.WorkItemId}'"
          | Some _, Some record ->
              match event.StepId with
              | Some step when not (List.contains step record.StartedSteps) ->
                  finding "step" $"step '{step}' was never started in execution '{record.ExecutionId}'"
              | _ -> ()

              match instant event.OccurredAt, instant record.StartedAt with
              | Some occurred, Some started when occurred < started ->
                  finding "occurredAt" $"checkpoint is dated {event.OccurredAt}, before its execution started ({record.StartedAt})"
              | _ -> ()
          if (instant event.OccurredAt).IsNone then
              finding "occurredAt" $"'{event.OccurredAt}' is not a timestamp" ]

    let private chronologyFindings (events: CheckpointEventFacts list) =
        events
        |> List.groupBy _.WorkItemId
        |> List.collect (fun (_, history) ->
            history
            |> List.pairwise
            |> List.choose (fun (earlier, later) ->
                match instant earlier.OccurredAt, instant later.OccurredAt with
                | Some before, Some after when after < before ->
                    Some
                        { Path = eventPath later.EventId
                          Field = "occurredAt"
                          Message = $"checkpoint is dated {later.OccurredAt}, before the earlier checkpoint {earlier.EventId} ({earlier.OccurredAt}); history must be chronological" }
                | _ -> None))

    let private duplicateFindings (events: CheckpointEventFacts list) =
        events
        |> List.countBy _.EventId
        |> List.filter (fun (_, count) -> count > 1)
        |> List.map (fun (eventId, count) ->
            { Path = eventPath eventId
              Field = "eventId"
              Message = $"checkpoint identity '{eventId}' is recorded {count} times; each checkpoint is recorded once" })

    let private projectionFindings (events: CheckpointEventFacts list) (projections: ProjectionFacts list) =
        projections
        |> List.collect (fun projection ->
            let history = events |> List.filter (fun event -> event.WorkItemId = projection.WorkItemId)
            let latest = List.tryLast history

            let finding message =
                { Path = contextPath projection.WorkItemId
                  Field = "latestCheckpoint"
                  Message = message }

            match projection.Projection, latest with
            | Error problems, _ -> problems |> List.map finding
            | Ok None, None -> []
            | Ok None, Some event ->
                [ finding $"work item has checkpoint history (latest {event.EventId}) but no latestCheckpoint projection" ]
            | Ok(Some recorded), None ->
                [ finding $"latestCheckpoint '{recorded.CheckpointId}' has no work.checkpointed event in history" ]
            | Ok(Some recorded), Some event when recorded.CheckpointId <> event.EventId ->
                [ finding $"latestCheckpoint '{recorded.CheckpointId}' is not the latest checkpoint event '{event.EventId}'" ]
            | Ok(Some recorded), Some event ->
                match event.Checkpoint with
                | Ok fromHistory when fromHistory = recorded.Recorded -> []
                | Ok _ -> [ finding $"latestCheckpoint '{recorded.CheckpointId}' differs from the event that recorded it" ]
                | Error _ -> [])

    let private continuationFindings (input: CheckpointValidationInput) (event: ContinuationFacts) =
        let finding field message =
            { Path = eventPath event.EventId
              Field = field
              Message = message }

        let execution id = input.Executions |> List.tryFind (fun candidate -> candidate.ExecutionId = id)

        [ if not event.IdMatchesContent then
              finding "eventId" "continuation event content does not match its eventId; continuation events must never be edited"
          if not (List.contains event.WorkItemId input.WorkItemIds) then
              finding "workItem" $"continuation names work item '{event.WorkItemId}', which is not in repository context"
          match event.SuccessorExecutionId with
          | Some id ->
              match execution id with
              | None -> finding "execution" $"successor execution '{id}' has no record under .ros/telemetry/executions"
              | Some record when record.WorkItemId <> event.WorkItemId ->
                  finding "execution" $"successor execution '{id}' belongs to '{record.WorkItemId}', not '{event.WorkItemId}'"
              | Some _ ->
                  match event.PredecessorExecutionId, input.Parents |> Map.tryFind id with
                  | Some predecessor, Some parent when parent <> predecessor ->
                      finding "predecessor" $"successor '{id}' records parent '{parent}', not the predecessor '{predecessor}'"
                  | Some predecessor, None -> finding "predecessor" $"successor '{id}' records no parent, but the event names '{predecessor}'"
                  | _ -> ()
          | None -> ()
          match event.PredecessorExecutionId with
          | Some id when (execution id).IsNone -> finding "predecessor" $"predecessor execution '{id}' has no record under .ros/telemetry/executions"
          | Some id when event.SuccessorExecutionId = Some id -> finding "predecessor" "a successor cannot be its own predecessor"
          | _ -> ()
          match event.CheckpointId with
          | Some id when not (input.Events |> List.exists (fun checkpoint -> checkpoint.EventId = id && checkpoint.WorkItemId = event.WorkItemId)) ->
              finding "checkpoint" $"continuation names checkpoint '{id}', which is not a checkpoint of '{event.WorkItemId}'"
          | _ -> () ]

    let findings (input: CheckpointValidationInput) : CheckpointFinding list =
        (input.Events |> List.collect (eventFindings input))
        @ chronologyFindings input.Events
        @ duplicateFindings input.Events
        @ projectionFindings input.Events input.Projections
        @ (input.Continuations |> List.collect (continuationFindings input))
