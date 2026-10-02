namespace Ros.Contracts.Work

open System.Text.Json.Nodes
open Ros.Domain.Telemetry

/// The `praxis.telemetry-usage` version 1 document `telemetry usage` prints;
/// the control plane relays the same document rather than re-deriving it.
[<RequireQualifiedAccess>]
module TelemetryUsageJson =
    let private strings (values: string list) =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value: JsonNode))
        array

    let group (group: UsageGroup) : JsonObject =
        let node = JsonObject()
        node["key"] <- JsonValue.Create group.Key
        node["metric"] <- JsonValue.Create group.MetricId
        node["unit"] <- JsonValue.Create group.Unit
        node["currency"] <- (match group.Currency with Some value -> JsonValue.Create value | None -> null)
        node["total"] <- (match group.Total with Some total -> JsonValue.Create total | None -> null)
        node["complete"] <- JsonValue.Create group.UnavailableExecutions.IsEmpty
        node["measurements"] <- JsonValue.Create group.Measurements
        node["reportingExecutions"] <- strings group.ReportingExecutions
        node["unavailableExecutions"] <- strings group.UnavailableExecutions
        let qualities = JsonObject()
        group.Qualities |> List.iter (fun (quality, count) -> qualities[quality] <- JsonValue.Create count)
        node["evidenceQuality"] <- qualities
        node

    let document (workItemId: string option) (dimension: UsageDimension) (groups: UsageGroup list) : JsonObject =
        let output = JsonObject()
        output["schema"] <- JsonValue.Create "praxis.telemetry-usage"
        output["schemaVersion"] <- JsonValue.Create 1
        output["workItemId"] <- (match workItemId with Some id -> JsonValue.Create id | None -> null)
        output["by"] <- JsonValue.Create(UsageDimension.code dimension)
        let groupsNode = JsonArray()
        groups |> List.iter (fun item -> groupsNode.Add(group item: JsonNode))
        output["groups"] <- groupsNode
        output
