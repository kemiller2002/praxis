namespace Ros.Contracts.Work

open System.Text.Json.Nodes
open Ros.Domain.Telemetry

/// The `praxis.telemetry-usage` document of `telemetry usage --json` and the
/// usage/cost coverage the control-plane telemetry view reports. One renderer
/// for both, so a group reads the same wherever it is served.
[<RequireQualifiedAccess>]
module TelemetryUsageJson =
    let schema = "praxis.telemetry-usage"
    let schemaVersion = 1

    let private text (value: string) : JsonNode = JsonValue.Create value
    let private optionalText (value: string option) : JsonNode = value |> Option.map text |> Option.toObj

    let private texts (values: string list) : JsonNode =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(text value))
        array

    /// One aggregated group; `total` is null when nobody reported the metric.
    let group (group: UsageGroup) : JsonObject =
        let node = JsonObject()
        node["key"] <- text group.Key
        node["metric"] <- text group.MetricId
        node["unit"] <- text group.Unit
        node["currency"] <- optionalText group.Currency
        node["total"] <- (match group.Total with Some total -> JsonValue.Create total :> JsonNode | None -> null)
        node["complete"] <- JsonValue.Create group.UnavailableExecutions.IsEmpty
        node["measurements"] <- JsonValue.Create group.Measurements
        node["reportingExecutions"] <- texts group.ReportingExecutions
        node["unavailableExecutions"] <- texts group.UnavailableExecutions
        let qualities = JsonObject()
        group.Qualities |> List.iter (fun (quality, count) -> qualities[quality] <- JsonValue.Create count)
        node["evidenceQuality"] <- qualities
        node

    let groups (values: UsageGroup list) : JsonArray =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(group value: JsonNode))
        array

    let document (workItemId: string option) (dimension: UsageDimension) (values: UsageGroup list) : JsonObject =
        let output = JsonObject()
        output["schema"] <- text schema
        output["schemaVersion"] <- JsonValue.Create schemaVersion
        output["workItemId"] <- optionalText workItemId
        output["by"] <- text (UsageDimension.code dimension)
        output["groups"] <- groups values
        output

    /// A metric's coverage: `availability` "unknown" carries no totals.
    let coverage (value: MetricCoverage) : JsonObject =
        let totals = JsonArray()

        value.Totals
        |> List.iter (fun total ->
            let node = JsonObject()
            node["unit"] <- text total.Unit
            node["currency"] <- optionalText total.Currency
            node["total"] <- JsonValue.Create total.Total
            totals.Add(node: JsonNode))

        let node = JsonObject()
        node["metric"] <- text value.MetricId
        node["unit"] <- text value.Unit
        node["availability"] <- text (CoverageAvailability.code value.Availability)
        node["totals"] <- totals
        node["reportingExecutions"] <- texts value.ReportingExecutions
        node["unavailableExecutions"] <- texts value.UnavailableExecutions
        node
