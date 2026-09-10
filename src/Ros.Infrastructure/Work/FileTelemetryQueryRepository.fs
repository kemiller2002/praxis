namespace Ros.Infrastructure.Work

open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Domain.Telemetry

/// The read-only queries behind production's `telemetry show`
/// (`showTelemetry`) and `telemetry summary` (`summarizeTelemetry`,
/// `tools/ros_telemetry.mjs`) -- reads `.ros/telemetry/executions/*.json`
/// verbatim, never mutating or locking anything.
[<RequireQualifiedAccess>]
module FileTelemetryQueryRepository =
    let private stringField (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private doubleField (node: JsonObject) (name: string) : float option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number ->
            match value.TryGetValue<float>() with
            | true, parsed -> Some parsed
            | _ -> None
        | _ -> None

    let private objectField (node: JsonObject) (name: string) : JsonObject option =
        match node[name] with
        | :? JsonObject as nested -> Some nested
        | _ -> None

    let private executionDirectory (root: string) = Path.Combine(root, ".ros", "telemetry", "executions")

    /// Mirrors production `executionFiles`/`loadExecutions`: every execution
    /// record, in ascending filename order (execution IDs embed a timestamp,
    /// so this is also chronological), or an empty list when the directory
    /// does not exist yet.
    let readAll (root: string) : JsonObject list =
        let directory = executionDirectory root

        if not (Directory.Exists directory) then
            []
        else
            Directory.GetFiles(directory, "*.json")
            |> Array.sort
            |> Array.choose (fun file ->
                match JsonNode.Parse(File.ReadAllText file) with
                | :? JsonObject as record -> Some record
                | _ -> None)
            |> Array.toList

    /// Every record whose `workItemId` matches, in the same file-sorted
    /// order `readAll` returns -- matching production's own unsorted
    /// (already chronological) `.filter`. An id matching nothing returns an
    /// empty list, never a rejection (production's own `showTelemetry` never
    /// throws for this branch).
    let readByWorkItemId (root: string) (workItemId: string) : JsonObject list =
        readAll root |> List.filter (fun record -> stringField record "workItemId" = Some workItemId)

    /// Mirrors production `resolveExecution`'s exact-executionId branch: an
    /// empty match is production's exact rejection message; a match sorts by
    /// `startedAt` and takes the last one (a real behavior for a
    /// hypothetical duplicate-id collision, reproduced rather than assumed
    /// impossible, even though one execution ID names exactly one file in
    /// practice).
    let readByExecutionId (root: string) (executionId: string) : Result<JsonObject, string> =
        let matches =
            readAll root
            |> List.filter (fun record -> stringField record "executionId" = Some executionId)
            |> List.sortBy (fun record -> stringField record "startedAt" |> Option.defaultValue "")

        match matches with
        | [] -> Error $"telemetry execution '{executionId}' was not found"
        | _ -> Ok(List.last matches)

    let private parseMeasurement (registryAggregations: Map<string, string>) (node: JsonObject) : SummaryMetricMeasurement option =
        match stringField node "id", stringField node "unit", doubleField node "value", stringField node "collectedAt" with
        | Some id, Some unit, Some value, Some collectedAt ->
            let dimensionsKey =
                match node["dimensions"] with
                | :? JsonObject as dimensions -> dimensions.ToJsonString()
                | _ -> "{}"

            Some
                { Id = id
                  Unit = unit
                  Currency = stringField node "currency"
                  DimensionsKey = dimensionsKey
                  Value = value
                  CollectedAt = collectedAt
                  RegistryAggregation = registryAggregations |> Map.tryFind id
                  SelfAggregation = stringField node "aggregation" |> Option.defaultValue "latest" }
        | _ -> None

    let private parseSummaryExecution (registryAggregations: Map<string, string>) (node: JsonObject) : SummaryExecution option =
        match stringField node "executionId" with
        | None -> None
        | Some executionId ->
            let identity = objectField node "identity"

            let metrics =
                match node["metrics"] with
                | :? JsonArray as array ->
                    array
                    |> Seq.choose (function
                        | :? JsonObject as measurement -> parseMeasurement registryAggregations measurement
                        | _ -> None)
                    |> Seq.toList
                | _ -> []

            Some
                { ExecutionId = executionId
                  Provider = identity |> Option.bind (fun i -> stringField i "provider") |> Option.defaultValue "unknown"
                  Runtime = identity |> Option.bind (fun i -> stringField i "runtime") |> Option.defaultValue "unknown"
                  SessionId = identity |> Option.bind (fun i -> stringField i "sessionId")
                  StartedAt = stringField node "startedAt"
                  FinalizedAt = stringField node "finalizedAt"
                  Metrics = metrics }

    /// Reads every execution record matching `workItemId` (or every record
    /// when `None`, mirroring production's own falsy-target check), parsed
    /// into the pure aggregation input `TelemetrySummary.summarize` expects.
    /// A record with no `executionId` at all is dropped rather than parsed
    /// with a fabricated one -- unreachable for any record this migration's
    /// own writers produce, since `executionId` is always their primary key.
    let readSummaryExecutions (root: string) (workItemId: string option) : SummaryExecution list =
        let records = match workItemId with
                      | None -> readAll root
                      | Some id -> readByWorkItemId root id

        let registryAggregations =
            FileMetricRegistryRepository.read root
            |> List.map (fun definition -> definition.Id, definition.Aggregation)
            |> Map.ofList

        records |> List.choose (parseSummaryExecution registryAggregations)
