namespace Praxis.Infrastructure.Work

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Work
open Praxis.Application.Work
open Praxis.Contracts.Provenance
open Praxis.Domain.Provenance
open Praxis.Infrastructure.Json
open Praxis.Infrastructure.Artifacts

type WorkHistoryOrderAmendment =
    { ExpectedHead: string
      ExpectedIncoming: string
      ApprovalPath: string
      ApprovalDigest: string
      Reason: string
      OccurredAt: DateTimeOffset
      Actor: Actor }

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

    let private approvalDigest root (request: WorkHistoryOrderAmendment) =
        if not (LocalAgentHandoff.isResourcePath request.ApprovalPath)
           || not (request.ApprovalPath.StartsWith("research/decisions/", StringComparison.Ordinal))
           || not (LocalAgentHandoff.isDigest request.ApprovalDigest) then
            failwith "amendment requires a pinned repository decision"
        use input = new FileStream(safeFile root request.ApprovalPath, FileMode.Open, FileAccess.Read, FileShare.Read)
        if input.Length > 131072L then failwith "approval evidence exceeds bound"
        let bytes = Array.zeroCreate<byte> (int input.Length)
        input.ReadExactly bytes
        if input.ReadByte() <> -1 then failwith "approval evidence changed during capture"
        "sha256:" + (SHA256.HashData bytes |> Convert.ToHexString).ToLowerInvariant()

    // Ordering approval cannot silently rearrange a member/execution's own
    // history. Unknown scopes refuse rather than being declared independent.
    let private independentReordering (left: WorkHistorySnapshot) (right: WorkHistorySnapshot) =
        let scopes (text: string) =
            let node = JsonNode.Parse text
            let scalar (name: string) (prefix: string) =
                match node[name] with
                | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String ->
                    let value = value.GetValue<string>()
                    if String.IsNullOrWhiteSpace value then [] else [prefix + value]
                | _ -> []
            let executions =
                match node["telemetryExecutions"] with
                | :? JsonArray as values -> values |> Seq.map (fun value -> "execution:" + value.GetValue<string>()) |> Seq.toList
                | _ -> []
            scalar "workItem" "work:" @ scalar "execution" "execution:" @ scalar "contribution" "execution:" @ executions |> Set.ofList
        let ranks = right.Events |> List.mapi (fun i (id, _) -> id, i) |> Map.ofList
        let shared = left.Events |> List.filter (fun (id, _) -> ranks.ContainsKey id) |> List.toArray
        let mutable maximumRank = -1
        let mutable unscopedRank = -1
        let mutable scopeRanks = Map.empty<string, int>
        for id, text in shared do
            let rank, groups = ranks[id], scopes text
            if unscopedRank > rank || (groups.IsEmpty && maximumRank > rank)
               || (groups |> Seq.exists (fun group -> Map.tryFind group scopeRanks |> Option.exists (fun prior -> prior > rank))) then
                failwith "canonical amendment would reorder related or unscoped events"
            maximumRank <- max maximumRank rank
            if groups.IsEmpty then unscopedRank <- max unscopedRank rank
            for group in groups do scopeRanks <- scopeRanks.Add(group, max rank (scopeRanks |> Map.tryFind group |> Option.defaultValue -1))

    let private amendmentEvent (request: WorkHistoryOrderAmendment) (head: string) (incoming: string) (sourceBlob: string) (incomingBlob: string)
                               (left: WorkHistorySnapshot) (right: WorkHistorySnapshot) (merged: WorkHistorySnapshot) =
        let orderDigest (snapshot: WorkHistorySnapshot) =
            let text = JsonSerializer.Serialize(snapshot.Events |> List.map fst)
            "sha256:" + CanonicalJson.sha256HexPrefix 64 text
        let node = JsonObject()
        node["schemaVersion"] <- JsonValue.Create "1.0.0"
        node["type"] <- JsonValue.Create "work.history.order-reconciled"
        node["repository"] <- JsonNode.Parse merged.Metadata["repository"]
        node["protocolVersion"] <- JsonNode.Parse merged.Metadata["protocolVersion"]
        node["occurredAt"] <- JsonValue.Create(request.OccurredAt.ToString("O"))
        node["reason"] <- JsonValue.Create request.Reason
        node["actor"] <- ActorJson.node request.Actor
        let record = JsonObject()
        record["preference"] <- JsonValue.Create "incoming-shared-event-order"
        record["head"] <- JsonValue.Create head
        record["incoming"] <- JsonValue.Create incoming
        record["headEventBlob"] <- JsonValue.Create sourceBlob
        record["incomingEventBlob"] <- JsonValue.Create incomingBlob
        record["headOrderDigest"] <- JsonValue.Create(orderDigest left)
        record["incomingOrderDigest"] <- JsonValue.Create(orderDigest right)
        record["mergedOriginalOrderDigest"] <- JsonValue.Create(orderDigest merged)
        record["preservedOriginalEvents"] <- JsonValue.Create merged.Events.Length
        record["approvalPath"] <- JsonValue.Create request.ApprovalPath
        record["approvalDigest"] <- JsonValue.Create request.ApprovalDigest
        record["approvalKind"] <- JsonValue.Create "operator-recorded-owner-approval"
        node["reconciliation"] <- record
        let publication = JsonObject()
        publication["status"] <- JsonValue.Create "pending"
        node["publication"] <- publication
        let id = CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact node)
        node["eventId"] <- JsonValue.Create id
        id, CanonicalJson.serializeCompact node

    let private runCore root apply (amendment: WorkHistoryOrderAmendment option) =
        try
            // Require exclusive, idle checkout: this lock serializes this command, not all native writers.
            match RegistryLock.acquire root "work-history-merge" RegistryLock.defaultSettings with
            | Error error -> Error error.Message
            | Ok lease ->
                try
                    let index () = git root (["ls-files"; "--stage"; "--"] @ paths)
                    let observedIndex = index ()
                    let entries = observedIndex.Split('\n') |> Array.filter (String.IsNullOrWhiteSpace >> not)
                    if entries |> Array.exists (fun line -> not (line.StartsWith "100644 ")) then
                        failwith "native history index must contain regular files"
                    let clean = paths |> List.map (fun path ->
                        let matches = entries |> Array.filter (fun line -> line.EndsWith("\t" + path, StringComparison.Ordinal))
                        if matches.Length = 1 && matches[0].EndsWith($" 0\t{path}", StringComparison.Ordinal) then true
                        elif matches.Length = 3 && [1; 2; 3] |> List.forall (fun stage ->
                            matches |> Array.filter (fun line -> line.EndsWith($" {stage}\t{path}", StringComparison.Ordinal)) |> Array.length = 1) then false
                        else failwith "incomplete native history index")
                    if clean |> List.forall id then failwith "native history merge requires a conflict"
                    let mergeHead () = git root ["rev-parse"; "--verify"; "MERGE_HEAD"]
                    let head () = git root ["rev-parse"; "--verify"; "HEAD"]
                    let observedMerge, observedHead = mergeHead(), head()
                    amendment |> Option.iter (fun request ->
                        if not (LocalAgentHandoff.isCommit request.ExpectedHead && LocalAgentHandoff.isCommit request.ExpectedIncoming)
                           || request.ExpectedHead <> observedHead.Trim() || request.ExpectedIncoming <> observedMerge.Trim()
                           || String.IsNullOrWhiteSpace request.Reason || request.Reason.Length > 4096
                           || request.Reason |> Seq.exists Char.IsControl
                           || request.OccurredAt.Offset <> TimeSpan.Zero || request.OccurredAt > DateTimeOffset.UtcNow
                           || approvalDigest root request <> request.ApprovalDigest then
                            failwith "invalid or stale canonical-order approval")
                    let bases = (git root ["merge-base"; "--all"; observedHead.Trim(); observedMerge.Trim()]).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    if bases.Length = 0 || bases.Length > 32 then failwith "unsupported real merge-base count"
                    let committedObject commit path =
                            let treeLine = (git root ["ls-tree"; commit; "--"; path]).TrimEnd()
                            let fields = treeLine.Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
                            if fields.Length <> 4 || fields[0] <> "100644" || fields[1] <> "blob" || fields[3] <> path then
                                failwith "committed history is not a regular file"
                            fields[2]
                    // Derive history only from real HEAD/incoming/merge-base commits.
                    // Git's virtual stage 1 in a criss-cross merge is never parsed.
                    for path, isClean in List.zip paths clean do
                        if not isClean then
                            let bindings = [2, observedHead.Trim(); 3, observedMerge.Trim()]
                                           @ (if bases.Length = 1 then [1, bases[0]] else [])
                            for stage, commit in bindings do
                                let expected = $"100644 {committedObject commit path} {stage}\t{path}"
                                if not (entries |> Array.contains expected) then failwith "history index stage does not match its commit"
                    let targets = paths |> List.map (fun path -> safeFile root path |> File.ReadAllText)
                    let read objectName =
                        let size = Int64.Parse((git root ["cat-file"; "-s"; objectName]).Trim(), CultureInfo.InvariantCulture)
                        if size > int64 limit then failwith "history blob exceeds size bound"
                        git root ["show"; objectName]
                    let commits = (bases |> Array.toList) @ [observedHead.Trim(); observedMerge.Trim()]
                    let contents = commits |> List.map (fun commit -> paths |> List.map (fun path -> read (committedObject commit path)))
                    let snapshots = contents |> List.map (fun values -> snapshot values[0] values[1])
                    let left, right = contents[bases.Length], contents[bases.Length + 1]
                    let leftSnapshot, rightSnapshot = snapshots[bases.Length], snapshots[bases.Length + 1]
                    let planning =
                        match amendment with
                        | None -> WorkHistoryMerge.planMany (snapshots |> List.take bases.Length) leftSnapshot rightSnapshot
                        | Some request ->
                            if request.OccurredAt < max leftSnapshot.UpdatedAt rightSnapshot.UpdatedAt then failwith "amendment predates observed history"
                            independentReordering leftSnapshot rightSnapshot
                            WorkHistoryMerge.planCanonicalIncoming (snapshots |> List.take bases.Length) leftSnapshot rightSnapshot
                    match planning with
                    | Error error -> Error error
                    | Ok merged ->
                        amendment |> Option.iter (fun _ ->
                            independentReordering leftSnapshot merged
                            independentReordering rightSnapshot merged)
                        let writes = render [left[0]; right[0]] merged
                        // Preserve original event lines from authoritative branch commits.
                        let rawEvents = [left[1]; right[1]] |> List.collect (fun text -> text.Split('\n') |> Array.filter (String.IsNullOrWhiteSpace >> not) |> Array.toList)
                        let eventById = rawEvents |> List.map (fun line ->
                            let event = JsonNode.Parse line
                            event["eventId"].GetValue<string>(), line) |> List.rev |> Map.ofList
                        let originalLines = merged.Events |> List.map (fst >> fun id -> eventById[id])
                        let finalSnapshot, eventLines =
                            match amendment with
                            | None -> merged, originalLines
                            | Some request ->
                                let leftLines = left[1].Split('\n') |> Array.filter (String.IsNullOrWhiteSpace >> not) |> Array.map (fun line ->
                                    let node = JsonNode.Parse line
                                    node["eventId"].GetValue<string>(), line) |> Map.ofArray
                                for line in right[1].Split('\n') |> Array.filter (String.IsNullOrWhiteSpace >> not) do
                                    let node = JsonNode.Parse line
                                    let id = node["eventId"].GetValue<string>()
                                    if leftLines.ContainsKey id && leftLines[id] <> line then failwith "shared event bytes differ"
                                let id, audit = amendmentEvent request (observedHead.Trim()) (observedMerge.Trim())
                                                    (committedObject (observedHead.Trim()) paths[1]) (committedObject (observedMerge.Trim()) paths[1])
                                                    leftSnapshot rightSnapshot merged
                                if eventById.ContainsKey id then failwith "amendment identity already exists in input history"
                                { merged with Events = merged.Events @ [id, normalized (JsonNode.Parse audit)] }, originalLines @ [audit]
                        let writes = { writes.Head with Content = String.concat "\n" eventLines + "\n" } :: writes.Tail
                        // Auto-merged paths are inputs only for a preservation check:
                        // staged/working edits must exactly match the authoritative plan.
                        for i in [0; 1] do
                            if clean[i] then
                                let rendered = writes |> List.find (fun write -> write.Path = paths[i])
                                let lineEndings (value: string) = value.Replace("\r\n", "\n")
                                if lineEndings targets[i] <> lineEndings (read ($":0:{paths[i]}")) && targets[i] <> rendered.Content then
                                    failwith "auto-merged history has working changes"
                                if i = 0 then
                                    let current = snapshot targets[i] ""
                                    if current.Metadata <> merged.Metadata || current.UpdatedAt <> merged.UpdatedAt
                                       || current.Items.Length <> merged.Items.Length || Map.ofList current.Items <> Map.ofList merged.Items then
                                        failwith "auto-merged context differs from authoritative histories"
                                elif (snapshot left[0] targets[i]).Events <> merged.Events && (snapshot left[0] targets[i]).Events <> finalSnapshot.Events then
                                    failwith "auto-merged events differ from authoritative histories"
                        if apply then
                            if index() <> observedIndex || mergeHead() <> observedMerge || head() <> observedHead
                               || (paths |> List.map (fun path -> safeFile root path |> File.ReadAllText)) <> targets then
                                failwith "Git or working history changed during observation"
                            amendment |> Option.iter (fun request -> if approvalDigest root request <> request.ApprovalDigest then failwith "approval evidence changed before apply")
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
                            | Ok() -> WorkStateTransaction.recover root |> Result.mapError _.Message |> Result.map (fun () -> finalSnapshot)
                        else Ok finalSnapshot
                finally lease.Release() |> ignore
        with _ -> Error "native history merge refused: malformed, unavailable or changed Git history"

    let run root apply = runCore root apply None

    let runAmended root apply request = runCore root apply (Some request)
