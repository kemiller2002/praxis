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

/// What the backlog queue contributed to a native history merge.
/// `Regenerated` means `queue.json` merged cleanly and only `queue.md` was rebuilt.
type WorkBacklogMergeSummary =
    { Regenerated: bool
      BaseRows: int
      LeftRows: int
      RightRows: int
      MergedRows: int
      NextSeq: int }

type WorkHistoryMergeResult =
    { History: WorkHistorySnapshot
      Backlog: WorkBacklogMergeSummary option }

/// Offline Git merge repair. Git index stages, not model-supplied JSON, are authority.
[<RequireQualifiedAccess>]
module FileWorkHistoryMerge =
    let private paths = [ ".ros/context/current.json"; ".ros/events/events.jsonl" ]
    let private queuePaths = [ ".ros/work/queue.json"; ".ros/work/queue.md" ]
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

    let private backlogSnapshot (text: string) : WorkBacklogSnapshot =
        let node = JsonNode.Parse(text) :?> JsonObject
        if node["schemaVersion"].GetValue<string>() <> "1.0.0" then failwith "unsupported backlog queue version"
        let items = node["items"] :?> JsonArray |> Seq.map (fun item -> item :?> JsonObject) |> Seq.toList
        { Metadata = node |> Seq.filter (fun p -> p.Key <> "items" && p.Key <> "nextSeq") |> Seq.map (fun p -> p.Key, normalized p.Value) |> Map.ofSeq
          NextSeq = node["nextSeq"].GetValue<int>()
          Items = items |> List.map (fun item -> item["id"].GetValue<string>(), normalized item)
          Reidentified =
            items |> List.choose (fun item ->
                match item["reidentifiedFrom"] with
                | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
                | _ -> None) |> Set.ofList }

    /// Rebuilds `queue.json` from original row nodes exactly as the backlog writer serializes it.
    let private renderQueue (queues: string list) (merged: WorkBacklogSnapshot) =
        let originals = queues |> List.map (fun text -> JsonNode.Parse(text) :?> JsonObject)
        let node = originals.Head.DeepClone() :?> JsonObject
        node["nextSeq"] <- JsonValue.Create merged.NextSeq
        let originalItems = originals |> List.collect (fun source -> source["items"] :?> JsonArray |> Seq.toList)
        let items = JsonArray()
        for id, item in merged.Items do
            let original = originalItems |> List.find (fun source -> source["id"].GetValue<string>() = id && normalized source = item)
            items.Add(original.DeepClone())
        node["items"] <- items
        node.ToJsonString FileBacklogQueueRepository.serializerOptions + "\n"

    /// `queue.md` is never merged as text: it is the backlog writer's own projection
    /// of the merged queue over the merged live context.
    let private renderQueueMarkdown (queue: string) (context: string) =
        let rows =
            (JsonNode.Parse(queue)["items"] :?> JsonArray)
            |> Seq.choose (function :? JsonObject as item -> FileBacklogQueueRepository.rowOf item | _ -> None)
            |> Seq.toList
        match Praxis.Contracts.Work.WorkContextPlanContract.parseJson context with
        | Error error -> failwith error
        | Ok view -> QueuePresentation.mergedRows rows view.WorkItems |> QueuePresentation.renderMarkdown

    /// A pending journal is accepted only when it is this exact merge's write set.
    let private pendingIsExact (file: string) (writes: (string * string) list) label =
        if File.Exists file then
            let journal = JsonNode.Parse(File.ReadAllText file)
            let exact = journal["writes"] :?> JsonArray |> Seq.map (fun w -> w["path"].GetValue<string>(), w["content"].GetValue<string>()) |> Seq.toList
            if exact = writes then Ok true else Error $"pending {label} transaction is not this exact history merge"
        else Ok false

    let private safeFile (root: string) (path: string) =
        let mutable current = root
        for segment in path.Split('/') do
            current <- Path.Combine(current, segment)
            if (File.Exists current || Directory.Exists current) && File.GetAttributes(current).HasFlag FileAttributes.ReparsePoint then
                failwith "history path contains a link"
        current

    let merge root apply =
        try
            // Require exclusive, idle checkout: this lock serializes this command, not all native writers.
            match RegistryLock.acquire root "work-history-merge" RegistryLock.defaultSettings with
            | Error error -> Error error.Message
            | Ok lease ->
                try
                    let index () = git root (["ls-files"; "--unmerged"; "--"] @ paths)
                    let queueIndex () = git root (["ls-files"; "--stage"; "--"] @ queuePaths)
                    let observedIndex, observedQueueIndex = index (), queueIndex ()
                    let entries = observedIndex.Split('\n') |> Array.filter (String.IsNullOrWhiteSpace >> not)
                    if entries.Length <> 6 || entries |> Array.exists (fun line -> not (line.StartsWith "100644 ")) then
                        failwith "both native history paths must have regular-file Git stages 1, 2 and 3"
                    for path in paths do
                        for stage in [1; 2; 3] do
                            if entries |> Array.filter (fun line -> line.EndsWith($" {stage}\t{path}", StringComparison.Ordinal)) |> Array.length <> 1 then
                                failwith "incomplete Git conflict stages"
                    // The backlog queue participates only while it is unmerged; any other stage shape is refused.
                    let queueEntries = observedQueueIndex.Split('\n') |> Array.filter (String.IsNullOrWhiteSpace >> not)
                    let stagesOf path =
                        queueEntries |> Array.filter (fun line -> line.EndsWith($"\t{path}", StringComparison.Ordinal))
                        |> Array.map (fun line ->
                            let fields = line.Split([|' '; '\t'|])
                            if fields.Length <> 4 || fields[0] <> "100644" then failwith "backlog queue index entry is not a regular file"
                            fields[2])
                        |> Array.sort |> Array.toList
                    if queueEntries.Length <> (queuePaths |> List.sumBy (stagesOf >> List.length)) then failwith "unexpected backlog queue index entry"
                    let queueMode =
                        match stagesOf queuePaths[0], stagesOf queuePaths[1] with
                        | ["1"; "2"; "3"], (["1"; "2"; "3"] | ["0"]) -> Some false
                        | ["0"], ["1"; "2"; "3"] -> Some true
                        | ([] | ["0"]), ([] | ["0"]) -> None
                        | _ -> failwith "unsupported backlog queue conflict stages"
                    let conflictedQueuePaths = queuePaths |> List.filter (fun path -> stagesOf path = ["1"; "2"; "3"])
                    let mergeHead () = git root ["rev-parse"; "--verify"; "MERGE_HEAD"]
                    let head () = git root ["rev-parse"; "--verify"; "HEAD"]
                    let observedMerge, observedHead = mergeHead(), head()
                    let bases = (git root ["merge-base"; "--all"; observedHead.Trim(); observedMerge.Trim()]).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    if bases.Length <> 1 then failwith "history merge requires a single real merge base"
                    // The mutable index is not authority by itself: bind all stages to real commits.
                    for stage, commit in [1, bases[0]; 2, observedHead.Trim(); 3, observedMerge.Trim()] do
                        for path in paths @ conflictedQueuePaths do
                            let treeLine = (git root ["ls-tree"; commit; "--"; path]).TrimEnd()
                            let fields = treeLine.Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
                            if fields.Length <> 4 || fields[0] <> "100644" || fields[1] <> "blob" || fields[3] <> path then
                                failwith "committed history is not a regular file"
                            let expected = $"100644 {fields[2]} {stage}\t{path}"
                            if not (entries |> Array.contains expected || queueEntries |> Array.contains expected) then
                                failwith "history index stage does not match its commit"
                    let observedPaths = paths @ (if queueMode.IsSome then queuePaths else [])
                    let working () = observedPaths |> List.map (fun path -> safeFile root path |> File.ReadAllText)
                    let targets = working ()
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
                        let mergedContext = (writes |> List.find (fun w -> w.Path = paths[0])).Content
                        let backlog =
                            match queueMode with
                            | None -> Ok None
                            | Some true ->
                                // queue.json merged cleanly: keep its stage-0 bytes and only regenerate queue.md.
                                let queue = read 0 queuePaths[0]
                                if targets[2] <> queue then failwith "working backlog queue differs from its merged index entry"
                                let single = backlogSnapshot queue
                                WorkBacklogMerge.plan single single single
                                |> Result.map (fun checkedQueue ->
                                    let rows = checkedQueue.Items.Length
                                    Some({ Regenerated = true; BaseRows = rows; LeftRows = rows; RightRows = rows; MergedRows = rows; NextSeq = checkedQueue.NextSeq }, queue))
                            | Some false ->
                                let queues = [1; 2; 3] |> List.map (fun stage -> read stage queuePaths[0])
                                let sides = queues |> List.map backlogSnapshot
                                WorkBacklogMerge.plan sides[0] sides[1] sides[2]
                                |> Result.map (fun mergedQueue ->
                                    Some({ Regenerated = false; BaseRows = sides[0].Items.Length; LeftRows = sides[1].Items.Length; RightRows = sides[2].Items.Length
                                           MergedRows = mergedQueue.Items.Length; NextSeq = mergedQueue.NextSeq },
                                         renderQueue [queues[1]; queues[2]; queues[0]] mergedQueue))
                        match backlog with
                        | Error error -> Error error
                        | Ok backlog ->
                            let backlogWrites : BacklogStateWrite list =
                                match backlog with
                                | None -> []
                                | Some(_, queue) ->
                                    [ { Path = queuePaths[0]; Content = queue }
                                      { Path = queuePaths[1]; Content = renderQueueMarkdown queue mergedContext } ]
                            let result = { History = merged; Backlog = backlog |> Option.map fst }
                            if apply then
                                if index() <> observedIndex || queueIndex() <> observedQueueIndex || mergeHead() <> observedMerge || head() <> observedHead
                                   || working () <> targets then
                                    failwith "Git or working history changed during observation"
                                // Check both journals before writing anything; a crash between the two
                                // transactions re-runs to the identical write sets and accepts what was applied.
                                match pendingIsExact (WorkStateTransaction.transactionPath root) (writes |> List.map (fun w -> w.Path, w.Content)) "history",
                                      (if backlogWrites.IsEmpty then Ok false
                                       else pendingIsExact (BacklogStateTransaction.transactionPath root) (backlogWrites |> List.map (fun w -> w.Path, w.Content)) "backlog") with
                                | Error error, _ | _, Error error -> Error error
                                | Ok historyPending, Ok backlogPending ->
                                    let historyPrepared = if historyPending then Ok() else WorkStateTransaction.prepare root writes |> Result.mapError _.Message
                                    historyPrepared
                                    |> Result.bind (fun () -> WorkStateTransaction.recover root |> Result.mapError _.Message)
                                    |> Result.bind (fun () ->
                                        if backlogWrites.IsEmpty then Ok()
                                        else
                                            let backlogPrepared = if backlogPending then Ok() else BacklogStateTransaction.prepare root backlogWrites |> Result.mapError _.Message
                                            backlogPrepared |> Result.bind (fun () -> BacklogStateTransaction.recover root |> Result.mapError _.Message))
                                    |> Result.map (fun () -> result)
                            else Ok result
                finally lease.Release() |> ignore
        with _ -> Error "native history merge refused: malformed, unavailable or changed Git history"

    /// History-only view kept for existing callers.
    let run root apply = merge root apply |> Result.map _.History
