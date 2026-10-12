namespace Praxis.Infrastructure.Work

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Work
open Praxis.Application.Work
open Praxis.Infrastructure.Json
open Praxis.Infrastructure.Artifacts

/// Offline Git merge repair. Git index stages, not model-supplied JSON, are authority.
[<RequireQualifiedAccess>]
module FileWorkHistoryMerge =
    let private paths = [ ".ros/context/current.json"; ".ros/events/events.jsonl" ]
    let private limit = 8 * 1024 * 1024
    let private git root args =
        let start = ProcessStartInfo("git", WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
        args |> List.iter start.ArgumentList.Add
        use child = new Process(StartInfo = start)
        if not (child.Start()) then failwith "cannot start Git"
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        if not (child.WaitForExit(10000)) then
            child.Kill(true)
            child.WaitForExit()
            failwith "Git history observation timed out"
        let text = output.GetAwaiter().GetResult()
        error.GetAwaiter().GetResult() |> ignore
        if child.ExitCode <> 0 then failwith "Git history observation failed"
        if text.Length > limit then failwith "Git history observation exceeds size bound"
        text

    let private normalized (node: JsonNode) = if isNull node then "null" else CanonicalJson.stabilize node |> CanonicalJson.serializeCompact

    let private snapshot (context: string) (events: string) =
        let node = JsonNode.Parse(context) :?> JsonObject
        if node["schemaVersion"].GetValue<string>() <> "1.0.0" || node["protocolVersion"].GetValue<string>() <> "1.0.0" then
            failwith "unsupported work context version"
        if String.IsNullOrWhiteSpace(node["repository"].GetValue<string>()) then failwith "missing repository identity"
        let time = DateTimeOffset.Parse(node["updatedAt"].GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        let items = node["workItems"] :?> JsonArray
        { Metadata = node |> Seq.filter (fun p -> p.Key <> "workItems" && p.Key <> "updatedAt") |> Seq.map (fun p -> p.Key, normalized p.Value) |> Map.ofSeq
          UpdatedAt = time
          Items = items |> Seq.map (fun item -> item["id"].GetValue<string>(), normalized item) |> Seq.toList
          Events = events.Split('\n') |> Array.filter (String.IsNullOrWhiteSpace >> not) |> Array.map (fun line ->
              let event = JsonNode.Parse line
              event["eventId"].GetValue<string>(), normalized event) |> Array.toList }

    let private render (contexts: string list) (merged: WorkHistorySnapshot) : WorkStateWrite list =
        let originals = contexts |> List.map (fun text -> JsonNode.Parse(text) :?> JsonObject)
        let node = originals.Head.DeepClone() :?> JsonObject
        for entry in node |> Seq.map _.Key |> Seq.toList do
            if entry <> "workItems" && entry <> "updatedAt" && not (merged.Metadata.ContainsKey entry) then node.Remove entry |> ignore
        for KeyValue(key, value) in merged.Metadata do
            node[key] <- originals |> List.tryPick (fun source ->
                if source.ContainsKey key && normalized source[key] = value then Some(if isNull source[key] then null else source[key].DeepClone()) else None)
                |> Option.defaultWith (fun () -> JsonNode.Parse value)
        node["updatedAt"] <- JsonValue.Create(merged.UpdatedAt.ToString("O"))
        let items = JsonArray()
        let originalItems = originals |> List.collect (fun source -> source["workItems"] :?> JsonArray |> Seq.toList)
        for id, item in merged.Items do
            let original = originalItems |> List.find (fun source -> source["id"].GetValue<string>() = id && normalized source = item)
            items.Add(original.DeepClone())
        node["workItems"] <- items
        // Reuse original event bytes below: normalized values are only comparison keys.
        [ { Path = paths[1]; Content = merged.Events |> List.map snd |> String.concat "\n" |> fun s -> s + "\n" }
          { Path = paths[0]; Content = node.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n" } ]

    let private safeFile (root: string) (path: string) =
        let mutable current = root
        for segment in path.Split('/') do
            current <- Path.Combine(current, segment)
            if (File.Exists current || Directory.Exists current) && File.GetAttributes(current).HasFlag FileAttributes.ReparsePoint then
                failwith "history path contains a link"
        current

    let run root apply =
        try
            // Require exclusive, idle checkout: this lock serializes this command, not all native writers.
            match RegistryLock.acquire root "work-history-merge" RegistryLock.defaultSettings with
            | Error error -> Error error.Message
            | Ok lease ->
                try
                    let index () = git root (["ls-files"; "--unmerged"; "--"] @ paths)
                    let observedIndex = index ()
                    let entries = observedIndex.Split('\n') |> Array.filter (String.IsNullOrWhiteSpace >> not)
                    if entries.Length <> 6 || entries |> Array.exists (fun line -> not (line.StartsWith "100644 ")) then
                        failwith "both native history paths must have regular-file Git stages 1, 2 and 3"
                    for path in paths do
                        for stage in [1; 2; 3] do
                            if entries |> Array.filter (fun line -> line.EndsWith($" {stage}\t{path}", StringComparison.Ordinal)) |> Array.length <> 1 then
                                failwith "incomplete Git conflict stages"
                    let mergeHead () = git root ["rev-parse"; "--verify"; "MERGE_HEAD"]
                    let head () = git root ["rev-parse"; "--verify"; "HEAD"]
                    let observedMerge, observedHead = mergeHead(), head()
                    let bases = (git root ["merge-base"; "--all"; observedHead.Trim(); observedMerge.Trim()]).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    if bases.Length <> 1 then failwith "history merge requires a single real merge base"
                    // The mutable index is not authority by itself: bind all stages to real commits.
                    for stage, commit in [1, bases[0]; 2, observedHead.Trim(); 3, observedMerge.Trim()] do
                        for path in paths do
                            let treeLine = (git root ["ls-tree"; commit; "--"; path]).TrimEnd()
                            let fields = treeLine.Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
                            if fields.Length <> 4 || fields[0] <> "100644" || fields[1] <> "blob" || fields[3] <> path then
                                failwith "committed history is not a regular file"
                            let expected = $"100644 {fields[2]} {stage}\t{path}"
                            if not (entries |> Array.contains expected) then failwith "history index stage does not match its commit"
                    let targets = paths |> List.map (fun path -> safeFile root path |> File.ReadAllText)
                    let read stage path =
                        let objectName = $":{stage}:{path}"
                        let size = Int64.Parse((git root ["cat-file"; "-s"; objectName]).Trim(), CultureInfo.InvariantCulture)
                        if size > int64 limit then failwith "history blob exceeds size bound"
                        git root ["show"; objectName]
                    let contexts = [1; 2; 3] |> List.map (fun stage -> read stage paths[0])
                    let snapshots = [1; 2; 3] |> List.map (fun stage -> snapshot contexts[stage - 1] (read stage paths[1]))
                    match WorkHistoryMerge.plan snapshots[0] snapshots[1] snapshots[2] with
                    | Error error -> Error error
                    | Ok merged ->
                        let writes = render [contexts[1]; contexts[2]] merged
                        // Preserve byte-for-byte event lines, including original property ordering used for IDs.
                        let rawEvents = [2; 3] |> List.collect (fun stage -> (read stage paths[1]).Split('\n') |> Array.filter (String.IsNullOrWhiteSpace >> not) |> Array.toList)
                        let eventById = rawEvents |> List.map (fun line ->
                            let event = JsonNode.Parse line
                            event["eventId"].GetValue<string>(), line) |> List.rev |> Map.ofList
                        let writes = { writes.Head with Content = merged.Events |> List.map (fst >> fun id -> eventById[id]) |> String.concat "\n" |> fun s -> s + "\n" } :: writes.Tail
                        if apply then
                            if index() <> observedIndex || mergeHead() <> observedMerge || head() <> observedHead
                               || (paths |> List.map (fun path -> safeFile root path |> File.ReadAllText)) <> targets then
                                failwith "Git or working history changed during observation"
                            let pending = WorkStateTransaction.transactionPath root
                            let prepared =
                                if File.Exists pending then
                                    let journal = JsonNode.Parse(File.ReadAllText pending)
                                    let pendingWrites = journal["writes"] :?> JsonArray
                                    let exact = pendingWrites |> Seq.map (fun w -> w["path"].GetValue<string>(), w["content"].GetValue<string>()) |> Seq.toList
                                    if exact = (writes |> List.map (fun w -> w.Path, w.Content)) then Ok()
                                    else Error "pending transaction is not this exact history merge"
                                else WorkStateTransaction.prepare root writes |> Result.mapError _.Message
                            match prepared with
                            | Error error -> Error error
                            | Ok() -> WorkStateTransaction.recover root |> Result.mapError _.Message |> Result.map (fun () -> merged)
                        else Ok merged
                finally lease.Release() |> ignore
        with _ -> Error "native history merge refused: malformed, unavailable or changed Git history"
