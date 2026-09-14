namespace Ros.Persistence

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open EchelonFoundry.Ros.Integration
open Ros.ProjectAdministration

/// One inbound activity as Central has recorded it: the validated
/// contract payload, which producer sent it, its current lifecycle
/// state, and when Central first received it.
type StoredActivity =
    { Source: string
      Activity: ActivityObservation
      State: ExternalActivityState
      ReceivedAt: DateTimeOffset }

/// A provisional file-backed store for inbound activities, keyed by the
/// idempotency pair (`source`, `activityId`) the migration spec requires
/// -- see docs/migrations/central-integration/MIGRATION-PLAN.md Phase 4.
/// This is a deliberate simplification, not a final architectural
/// choice: the spec explicitly says not to introduce a database "just
/// because Central exists," and nothing about this V1 surface yet
/// demonstrates a need for one (see
/// docs/migrations/central-integration/COMPATIBILITY-MATRIX.md and the
/// "Complexity Requires Evidence" governance rule). A whole-file
/// read-modify-write is adequate for a single-instance host at today's
/// scale; it is not safe under real concurrent writers and should be
/// revisited (most likely with managed PostgreSQL, per the spec) once
/// real load or a multi-instance deployment gives evidence that is
/// actually needed.
[<RequireQualifiedAccess>]
module ActivityStore =
    let private activitiesPath (root: string) = Path.Combine(root, "activities.json")

    let private stateTag =
        function
        | Received -> "received"
        | Validated -> "validated"
        | Rejected _ -> "rejected"
        | Recorded -> "recorded"

    let private stateReason =
        function
        | Rejected reason -> Some reason
        | _ -> None

    let private parseState (tag: string) (reason: string option) : ExternalActivityState option =
        match tag, reason with
        | "received", _ -> Some Received
        | "validated", _ -> Some Validated
        | "recorded", _ -> Some Recorded
        | "rejected", Some reason -> Some(Rejected reason)
        | _ -> None

    let private toNode (stored: StoredActivity) : JsonObject =
        let node = JsonObject()
        node["source"] <- JsonValue.Create stored.Source
        node["state"] <- JsonValue.Create(stateTag stored.State)

        match stateReason stored.State with
        | Some reason -> node["stateReason"] <- JsonValue.Create reason
        | None -> ()

        node["receivedAt"] <- JsonValue.Create(stored.ReceivedAt.ToString("o"))
        node["activity"] <- JsonNode.Parse(ActivitySerialization.serializeActivity stored.Activity)
        node

    let private ofNode (node: JsonObject) : StoredActivity option =
        match node["source"], node["state"], node["receivedAt"], node["activity"] with
        | (:? JsonValue as source), (:? JsonValue as state), (:? JsonValue as receivedAt), (:? JsonObject as activityNode) ->
            let reason =
                match node["stateReason"] with
                | :? JsonValue as value -> Some(value.GetValue<string>())
                | _ -> None

            match parseState (state.GetValue<string>()) reason, DateTimeOffset.TryParse(receivedAt.GetValue<string>()) with
            | Some parsedState, (true, parsedReceivedAt) ->
                match ActivitySerialization.deserializeActivity (activityNode.ToJsonString()) with
                | Ok activity ->
                    Some
                        { Source = source.GetValue<string>()
                          Activity = activity
                          State = parsedState
                          ReceivedAt = parsedReceivedAt }
                | Error _ -> None
            | _ -> None
        | _ -> None

    let private readAll (root: string) : StoredActivity list =
        let path = activitiesPath root

        if not (File.Exists path) then
            []
        else
            match JsonNode.Parse(File.ReadAllText path) with
            | :? JsonArray as items ->
                items
                |> Seq.choose (fun item ->
                    match item with
                    | :? JsonObject as obj -> ofNode obj
                    | _ -> None)
                |> Seq.toList
            | _ -> []

    let private writeAll (root: string) (activities: StoredActivity list) =
        Directory.CreateDirectory root |> ignore
        let array = JsonArray()
        activities |> List.iter (fun stored -> array.Add(toNode stored: JsonNode))
        let options = JsonSerializerOptions(WriteIndented = true)
        File.WriteAllText(activitiesPath root, array.ToJsonString options + "\n")

    /// Looks up a previously stored activity by its idempotency key.
    let tryFind (root: string) (source: string) (activityId: string) : StoredActivity option =
        readAll root
        |> List.tryFind (fun stored -> stored.Source = source && ActivityId.value stored.Activity.ActivityId = activityId)

    /// Persists a new stored activity. Fails rather than overwriting an
    /// existing (source, activityId) pair -- callers are expected to
    /// have already checked `tryFind` for the idempotent-retry case, as
    /// `ActivityIngestion.handle` (Ros.Host) does.
    let save (root: string) (stored: StoredActivity) : Result<unit, string> =
        let existing = readAll root
        let activityId = ActivityId.value stored.Activity.ActivityId

        if existing |> List.exists (fun item -> item.Source = stored.Source && ActivityId.value item.Activity.ActivityId = activityId) then
            Error $"activity '{activityId}' from source '{stored.Source}' is already recorded"
        else
            writeAll root (stored :: existing)
            Ok()
