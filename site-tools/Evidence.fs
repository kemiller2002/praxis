namespace Praxis.Site

open System
open System.Globalization
open System.IO
open System.Text.RegularExpressions

/// The Praxis records a snapshot is checked against.
type Records =
    { WorkItems: Json list
      Lifecycle: Json list
      Executions: Json list }

/// Public GH-84 evidence snapshot for the site (docs/public-site.md).
///
/// The snapshot publishes only fields the public/private evidence boundary
/// allows: work item IDs, titles and states; execution IDs, status and times;
/// the recorded actor kind, provider, runtime and model; parent links; branch
/// and short commit; and whether token and cost metrics were recorded. Session,
/// conversation and run IDs, local paths, environment values and raw telemetry
/// are never copied.
///
/// Records keep moving after a snapshot is taken, so the check accepts a record
/// that has progressed (an active execution that is now finalized, a ready item
/// that is now complete) but never one whose identity, parentage or start differs.
[<RequireQualifiedAccess>]
module Evidence =
    let private scopePattern = Regex(@"^PRAXIS-SITE-\d+\z", RegexOptions.ECMAScript)

    let inScope (id: string option) =
        match id with
        | Some "GH-84" -> true
        | Some id -> scopePattern.IsMatch id
        | None -> false

    let private workItemOf (event: Json) = Json.get "workItem" event |> Json.text
    let private typeOf (event: Json) = Json.get "type" event |> Json.text

    let private shortCommit (commit: Json option) =
        match commit with
        | Some(JString text) -> JString(if text.Length > 7 then text.Substring(0, 7) else text)
        | _ -> JNull

    let private orUnknown (value: Json option) =
        match value with
        | None
        | Some JNull
        | Some(JString "") -> JString "unknown"
        | Some present -> present

    let private stateFromEvent =
        Map
            [ "work.started", "active"
              "work.resumed", "active"
              "work.blocked", "blocked"
              "work.completed", "complete" ]

    /// Adds or replaces a key while keeping first-insertion order, the way a
    /// JavaScript object literal spread does.
    let private upsert (key: string) (value: 'value) (entries: (string * 'value) list) =
        if entries |> List.exists (fun (existing, _) -> existing = key) then
            entries |> List.map (fun (existing, old) -> if existing = key then existing, value else existing, old)
        else
            entries @ [ key, value ]

    let private lookupEntry (key: string) (entries: (string * 'value) list) =
        entries |> List.tryFind (fun (existing, _) -> existing = key) |> Option.map snd

    let private telemetryExecutions (event: Json) =
        Json.get "telemetryExecutions" event |> Json.items

    let private order (id: string) =
        if id = "GH-84" then
            -1.0
        else
            match Double.TryParse(id.Split('-') |> Array.last, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value -> value
            | _ -> nan

    let workItems (queue: Json) (events: Json list) =
        let scoped = events |> List.filter (fun event -> inScope (workItemOf event))

        let lastState =
            scoped
            |> List.fold
                (fun states event ->
                    match typeOf event |> Option.bind stateFromEvent.TryFind, workItemOf event with
                    | Some state, Some id -> upsert id state states
                    | _ -> states)
                []

        let evidence =
            scoped
            |> List.filter (fun event -> typeOf event = Some "work.completed")
            |> List.fold
                (fun found event ->
                    let entries =
                        Json.get "evidence" event
                        |> Json.items
                        |> List.map (fun entry ->
                            Json.ofFields [ "type", Json.get "type" entry; "path", Json.get "path" entry ])

                    upsert (workItemOf event |> Option.get) (JArray entries) found)
                []

        let executions =
            scoped
            |> List.fold
                (fun found event ->
                    let id = workItemOf event |> Option.get
                    let existing = lookupEntry id found |> Option.defaultValue []
                    upsert id (existing @ telemetryExecutions event |> List.distinct) found)
                []

        let queued =
            Json.get "items" queue
            |> Json.items
            |> List.filter (fun item -> inScope (Json.get "id" item |> Json.text))

        let sources =
            queued
            |> List.map (fun item -> Json.get "id" item |> Json.text |> Option.get, Json.get "sourceReference" item |> Json.orElse JNull)

        let shape (id: string) (title: Json option) (state: Json option) =
            Json.ofFields
                [ "id", Some(JString id)
                  "title", title
                  "state", state
                  "sourceReference", Some(lookupEntry id sources |> Json.orElse JNull)
                  "executions", Some(JArray(lookupEntry id executions |> Option.defaultValue []))
                  "evidence", Some(lookupEntry id evidence |> Option.defaultValue (JArray [])) ]

        let titled =
            queued
            |> List.map (fun item ->
                let id = Json.get "id" item |> Json.text |> Option.get

                let state =
                    match lookupEntry id lastState with
                    | Some state -> Some(JString state)
                    | None -> Json.get "status" item

                id, shape id (Json.get "title" item) state)

        let unqueued =
            lastState
            |> List.filter (fun (id, _) -> not (titled |> List.exists (fun (queuedId, _) -> queuedId = id)))
            |> List.map (fun (id, state) ->
                let title = if id = "GH-84" then JString "Build the public Praxis website" else JNull
                id, shape id (Some title) (Some(JString state)))

        unqueued @ titled
        |> List.sortWith (fun (a, _) (b, _) -> compare (order a) (order b))
        |> List.map snd

    let lifecycle (events: Json list) =
        events
        |> List.filter (fun event -> workItemOf event = Some "GH-84")
        |> List.map (fun event ->
            let actor =
                match Json.get "actor" event with
                | actor when Json.truthy actor ->
                    Json.ofFields
                        [ "kind", Json.prop "kind" actor
                          "provider", Some(orUnknown (Json.prop "provider" actor))
                          "runtime", Some(orUnknown (Json.prop "runtime" actor))
                          "model", Some(orUnknown (Json.prop "model" actor)) ]
                | _ -> JNull

            let reason = Json.get "reason" event

            Json.ofFields
                [ "type", Json.get "type" event
                  "occurredAt", Json.get "occurredAt" event
                  "actor", Some actor
                  "reason", (if Json.truthy reason then reason else None)
                  "executions", Some(Json.get "telemetryExecutions" event |> Json.orElse (JArray [])) ])

    /// Status of one headline metric: recorded when any metric in its family was
    /// observed, otherwise the capability status of the total itself.
    let metricStatus (record: Json) (prefix: string) (total: string) =
        let observed =
            Json.get "metrics" record
            |> Json.items
            |> List.exists (fun metric ->
                Json.get "id" metric
                |> Json.text
                |> Option.exists (fun id -> id.StartsWith(prefix, StringComparison.Ordinal)))

        let capability =
            Json.get "capabilities" record
            |> Json.items
            |> List.tryFind (fun entry -> Json.get "metricId" entry = Some(JString total))

        if observed then "recorded"
        elif capability |> Option.bind (Json.get "status") = Some(JString "supported-unavailable") then "unavailable"
        else "unknown"

    let private metricValue (record: Json) (id: string) =
        Json.get "metrics" record
        |> Json.items
        |> List.tryFind (fun metric -> Json.get "id" metric = Some(JString id))
        |> Option.bind (Json.get "value")
        |> Json.orElse JNull

    let private position (record: Json) (which: string) =
        let point = Json.get "repository" record |> Json.prop which

        if Json.truthy (Json.prop "available" point) then
            Json.ofFields [ "branch", Json.prop "branch" point; "commit", Some(shortCommit (Json.prop "commit" point)) ]
        else
            JNull

    let execution (record: Json) =
        let identity = Json.get "identity" record

        Json.ofFields
            [ "executionId", Json.get "executionId" record
              "workItemId", Json.get "workItemId" record
              "status", Json.get "status" record
              "startedAt", Json.get "startedAt" record
              "finalizedAt", Some(Json.get "finalizedAt" record |> Json.orElse JNull)
              "identity",
              Some(
                  Json.ofFields
                      [ "actorKind", Some(orUnknown (Json.prop "actorKind" identity))
                        "provider", Some(orUnknown (Json.prop "provider" identity))
                        "runtime", Some(orUnknown (Json.prop "runtime" identity))
                        "model", Some(orUnknown (Json.prop "model" identity)) ]
              )
              "parentExecutionId", Some(Json.prop "parentExecutionId" identity |> Json.orElse JNull)
              "start", Some(position record "start")
              "end", Some(position record "end")
              "metrics",
              Some(
                  Json.ofFields
                      [ "wallMs", Some(metricValue record "time.wall_ms")
                        "commitsCreated", Some(metricValue record "git.commits_created")
                        "filesAdded", Some(metricValue record "git.files_added")
                        "filesModified", Some(metricValue record "git.files_modified")
                        "testsAdded", Some(metricValue record "tests.added")
                        "tokens", Some(JString(metricStatus record "tokens." "tokens.total"))
                        "cost", Some(JString(metricStatus record "cost." "cost.execution_total")) ]
              ) ]

    /// Builds the records view from raw events, the queue and the execution
    /// records named by in-scope events.
    let records (queue: Json) (events: Json list) (executionRecords: Json list) =
        { WorkItems = workItems queue events
          Lifecycle = lifecycle events
          Executions =
            executionRecords
            |> List.map execution
            |> List.sortWith (fun a b ->
                String.CompareOrdinal(Json.get "startedAt" a |> Json.toText, Json.get "startedAt" b |> Json.toText)) }

    [<Literal>]
    let Note =
        "Generated by praxis-site evidence (site-tools/SiteTools.fsproj) from the repository's Praxis records. Values reflect the records at asOf."

    let snapshot (records: Records) (asOf: string) =
        JObject
            [ "schemaVersion", JString "1.0.0"
              "kind", JString "praxis-public-evidence"
              "asOf", JString asOf
              "source", JString "https://github.com/kemiller2002/praxis"
              "note", JString Note
              "workItems", JArray records.WorkItems
              "lifecycle", JArray records.Lifecycle
              "executions", JArray records.Executions ]

    let recordsOfSnapshot (snapshot: Json) =
        { WorkItems = Json.get "workItems" snapshot |> Json.items
          Lifecycle = Json.get "lifecycle" snapshot |> Json.items
          Executions = Json.get "executions" snapshot |> Json.items }

    let private rank (ranks: Map<string, int>) (value: Json option) =
        Json.text value |> Option.bind ranks.TryFind

    let private stateRank = Map [ "captured", 0; "ready", 1; "active", 2; "blocked", 2; "complete", 3 ]
    let private statusRank = Map [ "active", 0; "finalized", 1 ]

    let snapshotProblems (snapshot: Json) (current: Records) =
        let executionProblems =
            Json.get "executions" snapshot
            |> Json.items
            |> List.collect (fun published ->
                let id = Json.get "executionId" published |> Json.toText
                let field key (value: Json) = Json.get key value

                match current.Executions |> List.tryFind (fun entry -> field "executionId" entry = field "executionId" published) with
                | None -> [ $"{id}: no such execution record" ]
                | Some record ->
                    let fixedKeys =
                        [ "workItemId"; "startedAt"; "parentExecutionId" ]
                        |> List.filter (fun key -> field key published <> field key record)
                        |> List.map (fun key ->
                            $"{id}: {key} {Json.toText (field key published)} differs from record {Json.toText (field key record)}")

                    let identity =
                        match field "identity" published with
                        | Some(JObject fields) ->
                            fields
                            |> List.filter (fun (key, value) -> Some value <> (field "identity" record |> Json.prop key))
                            |> List.map (fun (key, value) ->
                                let recorded = field "identity" record |> Json.prop key |> Json.toText
                                $"{id}: identity.{key} {Json.toText (Some value)} differs from record {recorded}")
                        | _ -> []

                    let start =
                        if Json.compact (field "start" published) = Json.compact (field "start" record) then
                            []
                        else
                            [ $"{id}: start differs from record" ]

                    let status =
                        match rank statusRank (field "status" record), rank statusRank (field "status" published) with
                        | Some recorded, Some shown when recorded >= shown -> []
                        | _ ->
                            let shown = Json.toText (field "status" published)
                            let recorded = Json.toText (field "status" record)
                            [ $"{id}: status {shown} is ahead of record {recorded}" ]

                    let settled =
                        if field "status" published = Some(JString "finalized") then
                            [ "finalizedAt"; "end"; "metrics" ]
                            |> List.filter (fun key -> Json.compact (field key published) <> Json.compact (field key record))
                            |> List.map (fun key -> $"{id}: finalized {key} differs from record")
                        else
                            []

                    fixedKeys @ identity @ start @ status @ settled)

        let itemProblems =
            Json.get "workItems" snapshot
            |> Json.items
            |> List.collect (fun published ->
                let id = Json.get "id" published |> Json.toText
                let field key (value: Json) = Json.get key value

                match current.WorkItems |> List.tryFind (fun entry -> field "id" entry = field "id" published) with
                | None -> [ $"{id}: no such work item" ]
                | Some record ->
                    let title = if field "title" published = field "title" record then [] else [ $"{id}: title differs from record" ]

                    let state =
                        let recorded = rank stateRank (field "state" record) |> Option.defaultValue 0
                        let shown = rank stateRank (field "state" published) |> Option.defaultValue 0

                        if recorded >= shown then
                            []
                        else
                            let shownState = Json.toText (field "state" published)
                            let recordedState = Json.toText (field "state" record)
                            [ $"{id}: state {shownState} is ahead of record {recordedState}" ]

                    let evidence =
                        if
                            field "state" published = Some(JString "complete")
                            && Json.compact (field "evidence" published) <> Json.compact (field "evidence" record)
                        then
                            [ $"{id}: completion evidence differs from record" ]
                        else
                            []

                    title @ state @ evidence)

        let lifecycleProblems =
            Json.get "lifecycle" snapshot
            |> Json.items
            |> List.indexed
            |> List.filter (fun (index, published) -> Json.compact (Some published) <> Json.compact (List.tryItem index current.Lifecycle))
            |> List.map (fun (_, published) ->
                let eventType = Json.get "type" published |> Json.toText
                let occurredAt = Json.get "occurredAt" published |> Json.toText
                $"lifecycle {eventType} at {occurredAt} differs from record")

        executionProblems @ itemProblems @ lifecycleProblems

    let private walkPath (parts: string list) (start: Json option) =
        parts |> List.fold (fun node part -> Json.step part node) start

    let private restPattern = Regex(@"\.(.*)", RegexOptions.Singleline ||| RegexOptions.CultureInvariant)

    /// The snapshot value a `data-evidence` key names, as the page shows it:
    /// "EXE-...:identity.runtime", "GH-84:state", "count:complete",
    /// "lifecycle:1.reason" or "asOf:date". `None` when the snapshot has no such value.
    let lookup (snapshot: Json) (key: string) =
        let parts = key.Split(':')
        let subject = parts[0]
        let field = if parts.Length > 1 then parts[1] else "undefined"
        let executions = Json.get "executions" snapshot |> Json.items
        let items = Json.get "workItems" snapshot |> Json.items
        let isNotGh84 item = Json.get "id" item <> Some(JString "GH-84")

        let count () =
            match field with
            | "total" -> Some(items |> List.filter isNotGh84 |> List.length)
            | "complete" ->
                Some(items |> List.filter (fun item -> isNotGh84 item && Json.get "state" item = Some(JString "complete")) |> List.length)
            | "executions" -> Some executions.Length
            | "tokensRecorded" ->
                Some(executions |> List.filter (fun e -> walkPath [ "metrics"; "tokens" ] (Some e) = Some(JString "recorded")) |> List.length)
            | "costRecorded" ->
                Some(executions |> List.filter (fun e -> walkPath [ "metrics"; "cost" ] (Some e) = Some(JString "recorded")) |> List.length)
            | "models" ->
                Some(executions |> List.filter (fun e -> walkPath [ "identity"; "model" ] (Some e) <> Some(JString "unknown")) |> List.length)
            | _ -> None

        let textOf (value: Json option) =
            value |> Option.map (fun found -> Json.toText (Some found))

        match subject with
        | "count" -> count () |> Option.map string
        | "asOf" ->
            let asOf = Json.get "asOf" snapshot |> Json.toText
            Some(if asOf.Length > 10 then asOf.Substring(0, 10) else asOf)
        | "lifecycle" ->
            let path = field.Split('.') |> List.ofArray

            let entry =
                match Int32.TryParse(path.Head, NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, index -> Json.get "lifecycle" snapshot |> Json.items |> List.tryItem index
                | _ -> None

            walkPath path.Tail entry |> textOf
        | _ ->
            let entity =
                executions
                |> List.tryFind (fun entry -> Json.get "executionId" entry = Some(JString subject))
                |> Option.orElse (items |> List.tryFind (fun entry -> Json.get "id" entry = Some(JString subject)))

            let split = restPattern.Match(field)
            let head = if split.Success then field.Substring(0, split.Index) else field
            let rest = if split.Success then Some split.Groups[1].Value else None

            match entity |> Option.bind (Json.get "evidence") with
            | Some evidence when head = "evidence" && Json.truthy (Some evidence) ->
                Json.items (Some evidence)
                |> List.tryFind (fun entry -> rest.IsSome && Json.get "type" entry = Some(JString rest.Value))
                |> Option.bind (Json.get "path")
                |> textOf
            | _ -> walkPath (field.Split('.') |> List.ofArray) entity |> textOf

    let private tags = Regex("<[^>]+>", RegexOptions.CultureInvariant)
    let private spaces = Regex(@"\s+", RegexOptions.CultureInvariant)

    let private decode (text: string) =
        let stripped =
            tags
                .Replace(text, "")
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'")

        spaces.Replace(stripped, " ").Trim()

    let private evidenceElement =
        Regex("""<([a-z0-9]+)\b[^>]*\bdata-evidence="([^"]+)"[^>]*>([\s\S]*?)<\/\1>""", RegexOptions.ECMAScript)

    /// Every `data-evidence` value in a page that does not match the snapshot.
    let htmlProblems (snapshot: Json) (name: string) (html: string) =
        evidenceElement.Matches(html)
        |> Seq.collect (fun found ->
            let key = found.Groups[2].Value
            let shown = decode found.Groups[3].Value

            match lookup snapshot key with
            | None -> [ $"{name}: data-evidence \"{key}\" is not in the snapshot" ]
            | Some expected when shown = expected -> []
            | Some expected -> [ $"{name}: data-evidence \"{key}\" shows \"{shown}\" but the snapshot records \"{expected}\"" ])
        |> List.ofSeq

    let private escape (text: string) =
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")

    let private stateClass =
        Map
            [ "complete", "status--verified"
              "active", "status--pending"
              "blocked", "status--unresolved"
              "ready", "status--ready"
              "captured", "status--ready" ]

    /// The case-study ledger (PRAXIS-SITE-23) is generated, never typed: every
    /// row cites the snapshot, so the evidence check covers it like any other value.
    let renderLedger (snapshot: Json) =
        Json.get "workItems" snapshot
        |> Json.items
        |> List.map (fun item ->
            let id = Json.get "id" item |> Json.toText
            let title = Json.get "title" item |> Json.toText |> escape
            let state = Json.get "state" item |> Json.toText
            let cssClass = stateClass.TryFind state |> Option.defaultValue "status--unknown"
            let executions = Json.get "executions" item |> Json.step "length" |> Json.toText

            String.concat
                "\n"
                [ "            <tr>"
                  $"              <th scope=\"row\"><span class=\"mono\" data-evidence=\"{id}:id\">{id}</span></th>"
                  $"              <td data-evidence=\"{id}:title\">{title}</td>"
                  $"              <td><span class=\"status {cssClass}\" data-evidence=\"{id}:state\">{state}</span></td>"
                  $"              <td class=\"mono\" data-evidence=\"{id}:executions.length\">{executions}</td>"
                  "            </tr>" ])
        |> String.concat "\n"

    let private ledgerPattern =
        Regex(@"(<!-- ledger:start -->)[\s\S]*?(\n\s*<!-- ledger:end -->)", RegexOptions.ECMAScript)

    let withLedger (html: string) (snapshot: Json) =
        ledgerPattern.Replace(
            html,
            MatchEvaluator(fun found -> $"{found.Groups[1].Value}\n{renderLedger snapshot}{found.Groups[2].Value}"),
            1
        )

    let private leafValue =
        Regex("""(<([a-z0-9]+)\b[^>]*\bdata-evidence="([^"]+)"[^>]*>)([^<]*)(<\/\2>)""", RegexOptions.ECMAScript)

    /// Refreshes every leaf data-evidence value (text only, no markup inside) from
    /// the snapshot. Values with markup inside are left alone and still checked.
    let withValues (html: string) (snapshot: Json) =
        leafValue.Replace(
            html,
            MatchEvaluator(fun found ->
                match lookup snapshot found.Groups[3].Value with
                | None -> found.Value
                | Some value -> $"{found.Groups[1].Value}{escape value}{found.Groups[5].Value}")
        )

    // ---- effects -------------------------------------------------------------

    let private readJson (file: string) = Json.parse (File.ReadAllText file)

    let private readLines (file: string) =
        File.ReadAllText(file).Split('\n')
        |> Array.filter (fun line -> line.Trim() <> "")
        |> Array.map Json.parse
        |> List.ofArray

    let snapshotPath (root: string) = Path.Combine(root, "site", "data", "gh-84.json")

    /// Reads the Praxis records under `root`.
    let readRecords (root: string) =
        let events = readLines (Path.Combine(root, ".ros", "events", "events.jsonl"))
        let queue = readJson (Path.Combine(root, ".ros", "work", "queue.json"))

        let executionRecords =
            events
            |> List.filter (fun event -> inScope (workItemOf event))
            |> List.collect telemetryExecutions
            |> List.distinct
            |> List.choose (fun id ->
                let file = Path.Combine(root, ".ros", "telemetry", "executions", $"{Json.toText (Some id)}.json")
                if File.Exists file then Some(readJson file) else None)

        records queue events executionRecords

    /// `new Date().toISOString()`
    let timestamp (moment: DateTimeOffset) =
        moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)

    let buildSnapshot (root: string) (asOf: string) = snapshot (readRecords root) asOf

    let readSnapshot (root: string) = readJson (snapshotPath root)

    let private pages (root: string) =
        let siteRoot = Path.Combine(root, "site")

        Directory.GetFiles(siteRoot)
        |> Array.filter (fun file -> file.EndsWith(".html", StringComparison.Ordinal))
        |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> List.ofArray

    /// The `--check` problems: the snapshot against the records, and every
    /// data-evidence value in site/*.html against the snapshot.
    let check (root: string) =
        if not (File.Exists(snapshotPath root)) then
            [ "site/data/gh-84.json is missing; run praxis-site evidence" ]
        else
            let published = readSnapshot root

            snapshotProblems published (readRecords root)
            @ (pages root
               |> List.collect (fun page ->
                   htmlProblems published $"site/{Path.GetFileName page}" (File.ReadAllText page)))

    /// Writes site/data/gh-84.json; returns its repository-relative path.
    let write (root: string) (asOf: string) =
        let target = snapshotPath root
        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
        File.WriteAllText(target, Json.pretty (buildSnapshot root asOf) + "\n")
        Repository.relative root target

    /// Regenerates the ledger and every data-evidence value in site/index.html.
    let render (root: string) =
        let page = Path.Combine(root, "site", "index.html")
        let published = readSnapshot root
        File.WriteAllText(page, withValues (withLedger (File.ReadAllText page) published) published)
