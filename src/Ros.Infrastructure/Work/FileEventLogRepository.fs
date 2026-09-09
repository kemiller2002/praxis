namespace Ros.Infrastructure.Work

open System.IO
open System.Text.Json

/// Reads the same `.ros/events/events.jsonl` file production's `workFindings`
/// reads for attribution: one JSON object per non-empty line, unioning each
/// event's `paths` array. Read-only: never writes the event log.
[<RequireQualifiedAccess>]
module FileEventLogRepository =
    let private eventPaths (line: string) =
        use document = JsonDocument.Parse line

        match document.RootElement.TryGetProperty "paths" with
        | true, value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.choose (fun entry -> if entry.ValueKind = JsonValueKind.String then Some(entry.GetString()) else None)
            |> Seq.toList
        | _ -> []

    /// Every path named in any event's `paths` array, across the whole log.
    let readAttributedPaths (root: string) : Set<string> =
        let path = Path.Combine(root, ".ros", "events", "events.jsonl")

        if not (File.Exists path) then
            Set.empty
        else
            File.ReadAllLines path
            |> Array.filter (fun line -> line.Trim().Length > 0)
            |> Array.collect (eventPaths >> List.toArray)
            |> Set.ofArray
