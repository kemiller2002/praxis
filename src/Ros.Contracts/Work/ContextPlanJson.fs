namespace Ros.Contracts.Work

open System
open System.Text.Json
open Ros.Contracts
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module WorkContextPlanContract =
    let private optionalString (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
        | _ -> None

    let private stringList (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.choose (fun entry -> if entry.ValueKind = JsonValueKind.String then Some(entry.GetString()) else None)
            |> Seq.toList
        | _ -> []

    let private parseState (value: string) =
        match value with
        | "ready" -> Ok LiveWorkState.Ready
        | "active" -> Ok LiveWorkState.Active
        | "blocked" -> Ok LiveWorkState.Blocked
        | "complete" -> Ok LiveWorkState.Complete
        | other -> Error $"unsupported semantic work state '{other}'"

    let private parseEvidence (element: JsonElement) =
        match element.TryGetProperty "type", element.TryGetProperty "path" with
        | (true, evidenceType), (true, path)
            when evidenceType.ValueKind = JsonValueKind.String && path.ValueKind = JsonValueKind.String ->
            Ok { Type = evidenceType.GetString(); Path = path.GetString() }
        | _ -> Error "work evidence requires string type and path"

    let private collect parser values =
        ((Ok []), values)
        ||> Seq.fold (fun state value ->
            match state, parser value with
            | Ok parsed, Ok next -> Ok(next :: parsed)
            | Error error, _ -> Error error
            | _, Error error -> Error error)
        |> Result.map List.rev

    let private parseItem (element: JsonElement) =
        match element.TryGetProperty "id", element.TryGetProperty "type", element.TryGetProperty "state", element.TryGetProperty "semanticState" with
        | (true, id), (true, workType), (true, state), (true, semanticState)
            when id.ValueKind = JsonValueKind.String
                 && workType.ValueKind = JsonValueKind.String
                 && state.ValueKind = JsonValueKind.String
                 && semanticState.ValueKind = JsonValueKind.String ->
            match parseState (semanticState.GetString()) with
            | Error error -> Error error
            | Ok parsedState ->
                let evidence =
                    match element.TryGetProperty "evidence" with
                    | true, value when value.ValueKind = JsonValueKind.Array -> collect parseEvidence (value.EnumerateArray())
                    | _ -> Ok []

                evidence
                |> Result.map (fun parsedEvidence ->
                    { Id = id.GetString()
                      WorkType = workType.GetString()
                      LocalState = state.GetString()
                      SemanticState = parsedState
                      Evidence = parsedEvidence
                      BlockReason = optionalString "blockReason" element
                      UpdatedAt = optionalString "updatedAt" element
                      CompletedAt = optionalString "completedAt" element
                      TelemetryExecutionIds = stringList "telemetryExecutionIds" element })
        | _ -> Error "work context item requires string id, type, state, and semanticState"

    let parseJson (content: string) =
        try
            use document = JsonDocument.Parse content
            let root = document.RootElement

            match root.TryGetProperty "workItems" with
            | true, workItems when workItems.ValueKind = JsonValueKind.Array ->
                collect parseItem (workItems.EnumerateArray())
                |> Result.map (fun items ->
                    { WorkItems = items
                      StartedAt = optionalString "startedAt" root
                      BaselineDirtyPaths = stringList "baselineDirtyPaths" root })
            | _ -> Error "work context requires a workItems array"
        with error ->
            Error $"invalid work context JSON: {error.Message}"

    let private writeContextRejection (writer: Utf8JsonWriter) rejection =
        match rejection with
        | WorkContextRejection.NoWorkItems -> writer.WriteString("reason", "no-work-items")
        | WorkContextRejection.InvalidWorkItemId workItemId ->
            writer.WriteString("reason", "invalid-work-item-id")
            writer.WriteString("workItem", workItemId)
        | WorkContextRejection.WorkItemNotInContext workItemId ->
            writer.WriteString("reason", "work-item-not-in-context")
            writer.WriteString("workItem", workItemId)
        | WorkContextRejection.ItemTransitionRejected(workItemId, transitionRejection) ->
            writer.WriteString("reason", "item-transition-rejected")
            writer.WriteString("workItem", workItemId)
            writer.WriteStartObject("transition")
            WorkDecisionContract.writeRejection writer transitionRejection
            writer.WriteEndObject()

    let renderJson outcome =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schemaVersion", "1.0.0")

            match outcome with
            | WorkContextPlanOutcome.Rejected rejection ->
                writer.WriteString("outcome", "rejected")
                writer.WriteNull("plan")
                writer.WriteStartObject("rejection")
                writeContextRejection writer rejection
                writer.WriteEndObject()
            | WorkContextPlanOutcome.Planned plan ->
                writer.WriteString("outcome", "planned")
                writer.WriteStartObject("plan")
                writer.WriteString("repository", plan.Repository)
                writer.WriteString("protocolVersion", plan.ProtocolVersion)
                writer.WriteString("actor", plan.Actor)
                WorkPlanContract.writeOptional writer "startedAt" plan.StartedAt
                writer.WriteStartArray("baselineDirtyPaths")
                plan.BaselineDirtyPaths |> List.iter writer.WriteStringValue
                writer.WriteEndArray()
                writer.WriteString("updatedAt", plan.UpdatedAt)
                writer.WriteStartArray("workItems")
                plan.WorkItems |> List.iter (WorkPlanContract.writeItem writer)
                writer.WriteEndArray()
                writer.WriteStartArray("events")
                plan.ItemPlans |> List.iter (fun itemPlan -> WorkPlanContract.writeEvent writer itemPlan.Event)
                writer.WriteEndArray()
                writer.WriteStartArray("telemetry")

                for itemPlan in plan.ItemPlans do
                    writer.WriteStartObject()
                    writer.WriteString("workItem", itemPlan.Item.Id)
                    writer.WriteStartArray("intents")
                    itemPlan.Telemetry |> List.iter (WorkPlanContract.writeIntent writer)
                    writer.WriteEndArray()
                    writer.WriteEndObject()

                writer.WriteEndArray()
                writer.WriteEndObject()
                writer.WriteNull("rejection")

            writer.WriteEndObject())
