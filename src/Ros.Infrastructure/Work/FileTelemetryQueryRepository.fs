namespace Ros.Infrastructure.Work

open System.IO
open System.Text.Json.Nodes

/// The read-only "show" query behind production's `telemetry show`
/// (`showTelemetry`, `tools/ros_telemetry.mjs`) -- reads
/// `.ros/telemetry/executions/*.json` verbatim, never mutating or locking
/// anything. Deliberately excludes `telemetry summary`'s aggregation
/// (`summarizeTelemetry`), a separately-scoped later slice.
[<RequireQualifiedAccess>]
module FileTelemetryQueryRepository =
    let private stringField (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = System.Text.Json.JsonValueKind.String -> Some(value.GetValue<string>())
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
