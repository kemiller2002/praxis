namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Text.Json
open Praxis.Domain.Work

[<RequireQualifiedAccess>]
module ReconciliationEnvelopeJson =
    let private requiredString (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String ->
            let text = value.GetString()
            if String.IsNullOrWhiteSpace text then Error ("missing-" + name) else Ok text
        | _ -> Error ("missing-" + name)

    let private optionalString (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> Some (value.GetString())
        | _ -> None

    let private parseActorElement (agent: JsonElement) =
        match requiredString "kind" agent, requiredString "id" agent with
        | Ok kind, Ok id ->
            Ok { ActorKind = kind
                 ActorId = id
                 Provider = optionalString "provider" agent
                 Model = optionalString "model" agent
                 Runtime = optionalString "runtime" agent }
        | Error code, _ | _, Error code -> Error code

    let private parseAgent (root: JsonElement) =
        match root.TryGetProperty "agent" with
        | true, agent when agent.ValueKind = JsonValueKind.Object -> parseActorElement agent
        | _ -> Error "missing-agent"

    let private optionalTimestamp (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok None
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok None
        | true, value when value.ValueKind = JsonValueKind.String ->
            match DateTimeOffset.TryParse(value.GetString()) with
            | true, timestamp -> Ok(Some timestamp)
            | _ -> Error $"invalid-{name}"
        | _ -> Error $"invalid-{name}"

    let private requiredTimestamp (name: string) (element: JsonElement) =
        match requiredString name element with
        | Error code -> Error code
        | Ok value ->
            match DateTimeOffset.TryParse value with
            | true, timestamp -> Ok timestamp
            | _ -> Error $"invalid-{name}"

    let private stringArray (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok []
        | true, values when values.ValueKind = JsonValueKind.Array ->
            let parsed =
                values.EnumerateArray()
                |> Seq.map (fun value -> if value.ValueKind = JsonValueKind.String then Ok(value.GetString()) else Error $"invalid-{name}")
                |> Seq.toList
            match parsed |> List.tryPick (function Error code -> Some code | _ -> None) with
            | Some code -> Error code
            | None -> Ok(parsed |> List.choose (function Ok value -> Some value | _ -> None))
        | _ -> Error $"invalid-{name}"

    let private parseStepTransitions (step: JsonElement) =
        match step.TryGetProperty "transitions" with
        | false, _ -> Ok []
        | true, transitions when transitions.ValueKind = JsonValueKind.Array ->
            let parsed =
                transitions.EnumerateArray()
                |> Seq.map (fun item ->
                    match item.TryGetProperty "sequence", requiredString "to" item, requiredTimestamp "timestamp" item with
                    | (true, sequence), Ok target, Ok timestamp when sequence.ValueKind = JsonValueKind.Number ->
                        match sequence.TryGetInt32() with
                        | true, number -> Ok { Sequence = number; FromStatus = optionalString "from" item; ToStatus = target; Timestamp = timestamp }
                        | _ -> Error "invalid-step-transition"
                    | _ -> Error "invalid-step-transition")
                |> Seq.toList
            match parsed |> List.tryPick (function Error code -> Some code | _ -> None) with
            | Some code -> Error code
            | None -> Ok(parsed |> List.choose (function Ok value -> Some value | _ -> None))
        | _ -> Error "invalid-step-transitions"

    let private parseStepMeasurements (step: JsonElement) =
        match step.TryGetProperty "telemetry" with
        | false, _ -> Ok([], [])
        | true, telemetry when telemetry.ValueKind = JsonValueKind.Object ->
            let raw =
                match telemetry.TryGetProperty "rawTelemetry" with
                | true, values when values.ValueKind = JsonValueKind.Array -> values.EnumerateArray() |> Seq.map _.GetRawText() |> Seq.toList
                | _ -> []
            match telemetry.TryGetProperty "measurements" with
            | false, _ -> Ok([], raw)
            | true, measurements when measurements.ValueKind = JsonValueKind.Array ->
                let parsed =
                    measurements.EnumerateArray()
                    |> Seq.map (fun item ->
                        match requiredString "measurementId" item, requiredString "metricId" item, requiredString "availability" item with
                        | Ok measurementId, Ok metricId, Ok availability ->
                            let value =
                                match item.TryGetProperty "value" with
                                | true, number when number.ValueKind = JsonValueKind.Number ->
                                    match number.TryGetDouble() with true, parsed -> Some parsed | _ -> None
                                | _ -> None
                            Ok
                                { MeasurementId = measurementId
                                  MetricId = metricId
                                  Value = value
                                  Unit = optionalString "unit" item
                                  Currency = optionalString "currency" item
                                  Quality = optionalString "quality" item
                                  Availability = availability
                                  RawJson = item.GetRawText() }
                        | _ -> Error "invalid-step-measurement")
                    |> Seq.toList
                match parsed |> List.tryPick (function Error code -> Some code | _ -> None) with
                | Some code -> Error code
                | None -> Ok(parsed |> List.choose (function Ok value -> Some value | _ -> None), raw)
            | _ -> Error "invalid-step-measurements"
        | _ -> Error "invalid-step-telemetry"

    let private parseStep (executionId: string) (workItem: string) (item: JsonElement) =
        match requiredString "stepId" item,
              item.TryGetProperty "sequence",
              requiredString "name" item,
              requiredString "status" item,
              requiredTimestamp "plannedAt" item,
              optionalTimestamp "startedAt" item,
              optionalTimestamp "completedAt" item,
              optionalTimestamp "endedAt" item,
              item.TryGetProperty "actor",
              parseStepTransitions item,
              parseStepMeasurements item,
              stringArray "classification" item,
              stringArray "evidence" item with
        | Ok stepId, (true, sequence), Ok name, Ok status, Ok plannedAt, Ok startedAt, Ok completedAt, Ok endedAt,
          (true, actor), Ok transitions, Ok(measurements, raw), Ok classifications, Ok evidence
            when sequence.ValueKind = JsonValueKind.Number && actor.ValueKind = JsonValueKind.Object ->
            match sequence.TryGetInt32(), parseActorElement actor with
            | (true, number), Ok parsedActor ->
                Ok
                    { StepId = stepId
                      ExecutionId = optionalString "executionId" item |> Option.defaultValue executionId
                      WorkItemId = optionalString "workItemId" item |> Option.defaultValue workItem
                      Sequence = number
                      ParentStepId = optionalString "parentStepId" item
                      Name = name
                      Description = optionalString "description" item
                      Classifications = classifications
                      Status = status
                      PlannedAt = plannedAt
                      StartedAt = startedAt
                      CompletedAt = completedAt
                      EndedAt = endedAt
                      Actor = parsedActor
                      Transitions = transitions
                      Measurements = measurements
                      RawTelemetry = raw
                      Evidence = evidence }
            | _ -> Error "invalid-step"
        | _ -> Error "invalid-step"

    let private parseExecution (root: JsonElement) (workItem: string) =
        match root.TryGetProperty "execution" with
        | false, _ -> Ok None
        | true, execution when execution.ValueKind = JsonValueKind.Object ->
            match requiredString "executionId" execution, requiredTimestamp "startedAt" execution with
            | Ok executionId, Ok startedAt ->
                match execution.TryGetProperty "steps" with
                | false, _ -> Error "missing-steps"
                | true, steps when steps.ValueKind = JsonValueKind.Array ->
                    let parsed = steps.EnumerateArray() |> Seq.map (parseStep executionId workItem) |> Seq.toList
                    match parsed |> List.tryPick (function Error code -> Some code | _ -> None) with
                    | Some code -> Error code
                    | None -> Ok(Some { ExecutionId = executionId; StartedAt = startedAt; Steps = parsed |> List.choose (function Ok value -> Some value | _ -> None) })
                | _ -> Error "invalid-steps"
            | _ -> Error "invalid-execution"
        | _ -> Error "invalid-execution"

    let private parseTimeline (root: JsonElement) =
        match root.TryGetProperty "timeline" with
        | true, timeline when timeline.ValueKind = JsonValueKind.Array ->
            timeline.EnumerateArray()
            |> Seq.map (fun item ->
                match item.TryGetProperty "sequence", item.TryGetProperty "timestamp", item.TryGetProperty "action" with
                | (true, sequence), (true, timestamp), (true, action)
                    when sequence.ValueKind = JsonValueKind.Number
                         && timestamp.ValueKind = JsonValueKind.String
                         && action.ValueKind = JsonValueKind.String ->
                    match sequence.TryGetInt32(), DateTimeOffset.TryParse(timestamp.GetString()) with
                    | (true, number), (true, instant) when not (String.IsNullOrWhiteSpace(action.GetString())) ->
                        Ok { Sequence = number; Timestamp = instant; Action = action.GetString() }
                    | _ -> Error "invalid-timeline-entry"
                | _ -> Error "invalid-timeline-entry")
            |> Seq.toList
            |> fun values ->
                match values |> List.tryPick (function Error code -> Some code | _ -> None) with
                | Some code -> Error code
                | None -> Ok (values |> List.choose (function Ok value -> Some value | _ -> None))
        | _ -> Error "missing-timeline"

    let private parseEvidence (element: JsonElement) =
        match requiredString "type" element, requiredString "path" element with
        | Ok evidenceType, Ok path -> Ok { WorkEvidence.Type = evidenceType; Path = path }
        | _ -> Error "invalid-request-evidence"

    let private requestEvidence (item: JsonElement) =
        match item.TryGetProperty "evidence" with
        | false, _ -> Ok []
        | true, values when values.ValueKind = JsonValueKind.Array ->
            let parsed = values.EnumerateArray() |> Seq.map parseEvidence |> Seq.toList
            match parsed |> List.tryPick (function Error code -> Some code | _ -> None) with
            | Some code -> Error code
            | None -> Ok(parsed |> List.choose (function Ok value -> Some value | _ -> None))
        | _ -> Error "invalid-request-evidence"

    let private parseRequests (root: JsonElement) =
        match root.TryGetProperty "requests" with
        | true, requests when requests.ValueKind = JsonValueKind.Array ->
            requests.EnumerateArray()
            |> Seq.map (fun item ->
                match requiredString "type" item, optionalTimestamp "occurredAt" item, requestEvidence item with
                | Ok requestType, Ok occurredAt, Ok evidence ->
                    Ok
                        { RequestType = requestType
                          OccurredAt = occurredAt
                          WorkType = optionalString "workType" item
                          Reason = optionalString "reason" item
                          Conclusion = optionalString "conclusion" item
                          Evidence = evidence }
                | _ -> Error "invalid-request")
            |> Seq.toList
            |> fun values ->
                match values |> List.tryPick (function Error code -> Some code | _ -> None) with
                | Some code -> Error code
                | None -> Ok (values |> List.choose (function Ok value -> Some value | _ -> None))
        | _ -> Error "missing-requests"

    let private parse (root: JsonElement) =
        match requiredString "schemaVersion" root,
              requiredString "transactionId" root,
              requiredString "workItem" root,
              requiredString "branch" root,
              requiredString "baseCommit" root,
              parseAgent root,
              parseExecution root (match requiredString "workItem" root with Ok value -> value | _ -> ""),
              parseTimeline root,
              parseRequests root with
        | Ok schemaVersion, Ok transactionId, Ok workItem, Ok branch, Ok baseCommit, Ok agent, Ok execution, Ok timeline, Ok requests ->
            Ok { SchemaVersion = schemaVersion
                 TransactionId = transactionId
                 WorkItem = workItem
                 Branch = branch
                 BaseCommit = baseCommit
                 PraxisInstanceId = optionalString "praxisInstanceId" root
                 Agent = agent
                 Execution = execution
                 Timeline = timeline
                 Requests = requests }
        | values ->
            let code =
                match values with
                | Error c, _, _, _, _, _, _, _, _
                | _, Error c, _, _, _, _, _, _, _
                | _, _, Error c, _, _, _, _, _, _
                | _, _, _, Error c, _, _, _, _, _
                | _, _, _, _, Error c, _, _, _, _
                | _, _, _, _, _, Error c, _, _, _
                | _, _, _, _, _, _, Error c, _, _
                | _, _, _, _, _, _, _, Error c, _
                | _, _, _, _, _, _, _, _, Error c -> c
                | _ -> "invalid-envelope-json"
            Error [ code ]

    let read path : Result<EnvelopeReconciliationInput, string list> =
        try
            if not (File.Exists path) then Error [ "envelope-not-found" ]
            else
                use document = JsonDocument.Parse(File.ReadAllText path)
                if document.RootElement.ValueKind <> JsonValueKind.Object then Error [ "invalid-envelope-json" ]
                else parse document.RootElement
        with
        | :? JsonException -> Error [ "invalid-envelope-json" ]
        | _ -> Error [ "envelope-read-failed" ]

[<RequireQualifiedAccess>]
module InputDocuments =
    let canonicalRelative = Path.Combine(".praxis", "inbox", "documents")
    let legacyRelative = "input-documents"

    let inventory root =
        [ canonicalRelative; legacyRelative ]
        |> List.collect (fun relative ->
            let path = Path.Combine(Path.GetFullPath root, relative)
            if Directory.Exists path then
                Directory.EnumerateFiles(path)
                |> Seq.filter (fun file -> not (String.Equals(Path.GetFileName file, "README.md", StringComparison.OrdinalIgnoreCase)))
                |> Seq.map (fun file -> relative, file)
                |> Seq.toList
            else [])
        |> List.sortBy snd
