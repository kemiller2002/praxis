namespace Ros.Contracts.Work

open System
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Domain.Artifacts

/// One item two branches changed in ways that cannot both hold. The merge
/// never picks a side for it.
type StateMergeConflict =
    { Path: string
      Item: string
      Reason: string }

[<RequireQualifiedAccess>]
type StateMergeOutcome =
    | Merged of content: string
    | Conflicted of StateMergeConflict list
    /// The input is not a well-formed instance of the file's format.
    | Unsupported of reason: string

/// How one top-level property of a JSON state document merges.
[<RequireQualifiedAccess>]
type PropertyRule =
    /// An array of objects identified by the named string field; each item
    /// merges three-way on its own.
    | KeyedArray of key: string
    /// Bookkeeping timestamp: the later ISO-8601 value.
    | Latest
    /// A "first happened at" timestamp: the earlier ISO-8601 value.
    | Earliest
    /// Bookkeeping counter: the larger number.
    | Maximum
    /// Bookkeeping that describes the latest write (for example the last
    /// actor): when both sides changed it, the side whose `timestamp`
    /// property is later.
    | FollowsLatest of timestamp: string

/// How a whole Praxis state file merges.
[<RequireQualifiedAccess>]
type StateFileRule =
    /// Append-only JSON Lines: every entry (identified by `key`) is kept
    /// exactly once; entries added on either side are interleaved by
    /// (`order`, `key`) after the common history, so the result does not
    /// depend on which side is "ours".
    | AppendOnlyLines of key: string * order: string
    /// A JSON object whose named properties follow a rule and whose other
    /// properties merge three-way as whole values.
    | Document of (string * PropertyRule) list
    /// A JSON array of objects identified by `key`, kept sorted by it.
    | SortedKeyedArray of key: string
    /// A JSON object whose properties are independent items.
    | KeyedObject
    /// A Markdown table whose rows are identified by their first cell and
    /// kept sorted by it (the backlog queue projection).
    | MarkdownTable

/// Three-way merge of Praxis state files (PRAXIS-STATE-MERGE-01). Pure: the
/// caller supplies the common ancestor (`None` when the file was added on
/// both sides), "ours" and "theirs" as text and receives the merged text or
/// the conflicting items.
[<RequireQualifiedAccess>]
module StateMergeJson =
    let eventsPath = ".ros/events/events.jsonl"
    let contextPath = ".ros/context/current.json"
    let queuePath = ".ros/work/queue.json"
    let queueMarkdownPath = ".ros/work/queue.md"
    let groupsPath = ".ros/work/groups.json"
    let publicationsPath = ".ros/publications.json"
    let estimateErrorPath = ".ros/planning/estimate-error.jsonl"

    /// Every state file the merge covers, with its rule.
    let rules: (string * StateFileRule) list =
        [ eventsPath, StateFileRule.AppendOnlyLines("eventId", "occurredAt")
          estimateErrorPath, StateFileRule.AppendOnlyLines("id", "asOf")
          contextPath,
          StateFileRule.Document
              [ "workItems", PropertyRule.KeyedArray "id"
                "updatedAt", PropertyRule.Latest
                "startedAt", PropertyRule.Earliest
                "actor", PropertyRule.FollowsLatest "updatedAt" ]
          queuePath, StateFileRule.Document [ "items", PropertyRule.KeyedArray "id"; "nextSeq", PropertyRule.Maximum ]
          queueMarkdownPath, StateFileRule.MarkdownTable
          groupsPath, StateFileRule.Document [ "groups", PropertyRule.KeyedArray "id" ]
          publicationsPath, StateFileRule.KeyedObject ]
        @ (ArtifactKinds.configurations
           |> List.map (fun configuration -> configuration.RegistryPath, StateFileRule.SortedKeyedArray "id"))

    let ruleFor (path: string) =
        let normalized = path.Replace('\\', '/')
        let relative = if normalized.StartsWith "./" then normalized.Substring 2 else normalized
        rules |> List.tryFind (fst >> (=) relative) |> Option.map snd

    // ---- shared values -------------------------------------------------------

    let private indented =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private conflict path item reason = { Path = path; Item = item; Reason = reason }

    /// A present value (`Some`, possibly JSON null) or an absent one.
    let private same (left: JsonNode option) (right: JsonNode option) =
        match left, right with
        | None, None -> true
        | Some left, Some right -> JsonNode.DeepEquals(left, right)
        | _ -> false

    let private clone (node: JsonNode) = if isNull node then null else node.DeepClone()

    /// Classic three-way choice: whatever changed, unless both changed it
    /// differently.
    let private threeWay (ancestor: JsonNode option) (ours: JsonNode option) (theirs: JsonNode option) =
        if same ours theirs then Ok ours
        elif same ancestor ours then Ok theirs
        elif same ancestor theirs then Ok ours
        else
            match ours, theirs with
            | None, _
            | _, None -> Error "changed on one side and removed on the other"
            | _ -> Error "changed differently on both sides"

    let private stringField (name: string) (node: JsonNode) =
        match node with
        | :? JsonObject as record ->
            match record[name] with
            | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
            | _ -> None
        | _ -> None

    let private collect (results: Result<'value, StateMergeConflict list> list) =
        let conflicts = results |> List.collect (function Error found -> found | Ok _ -> [])
        if conflicts.IsEmpty then Ok(results |> List.choose (function Ok value -> Some value | Error _ -> None)) else Error conflicts

    // ---- keyed items ---------------------------------------------------------

    /// `(key, item)` pairs in order, or why the array cannot be keyed.
    let private index (key: string) (array: JsonArray) : Result<(string * JsonNode) list, string> =
        let entries = array |> Seq.map (fun node -> stringField key node, node) |> Seq.toList

        match entries |> List.tryFind (fst >> Option.isNone) with
        | Some _ -> Error $"an entry has no string '{key}'"
        | None ->
            let keyed = entries |> List.map (fun (id, node) -> id.Value, node)
            let duplicates = keyed |> List.countBy fst |> List.filter (snd >> (<) 1) |> List.map fst

            match duplicates with
            | [] -> Ok keyed
            | _ ->
                let listed = String.Join(", ", duplicates)
                Error $"duplicate '{key}' values: {listed}"

    /// Merges independent keyed items. The common items keep the ancestor's
    /// order; items new on either side follow in ordinal key order, so the
    /// order does not depend on which side is "ours".
    let private mergeItems
        path
        (label: string)
        (ancestor: (string * JsonNode) list)
        (ours: (string * JsonNode) list)
        (theirs: (string * JsonNode) list)
        (sorted: bool)
        : Result<(string * JsonNode) list, StateMergeConflict list> =
        let lookup (entries: (string * JsonNode) list) id = entries |> List.tryFind (fst >> (=) id) |> Option.map snd
        let ancestorIds = ancestor |> List.map fst

        let additions =
            (ours @ theirs)
            |> List.map fst
            |> List.distinct
            |> List.filter (fun id -> not (List.contains id ancestorIds))
            |> List.sortWith (fun left right -> String.CompareOrdinal(left, right))

        let ids =
            ancestorIds @ additions
            |> fun all -> if sorted then all |> List.sortWith (fun left right -> String.CompareOrdinal(left, right)) else all

        ids
        |> List.map (fun id ->
            match threeWay (lookup ancestor id) (lookup ours id) (lookup theirs id) with
            | Ok merged -> Ok(merged |> Option.map (fun node -> id, node))
            | Error reason -> Error [ conflict path $"{label} '{id}'" reason ])
        |> collect
        |> Result.map (List.choose id)

    let private toArray (entries: (string * JsonNode) list) =
        JsonArray(entries |> List.map (snd >> clone) |> List.toArray)

    let private asArray (node: JsonNode option) : Result<JsonArray option, string> =
        match node with
        | None -> Ok None
        | Some(:? JsonArray as array) -> Ok(Some array)
        | Some _ -> Error "expected a JSON array"

    let private mergeKeyedArrays path label key sorted ancestor ours theirs =
        match asArray ancestor, asArray ours, asArray theirs with
        | Ok ancestor, Ok(Some ours), Ok(Some theirs) ->
            let indexed array = array |> Option.map (index key) |> Option.defaultValue (Ok [])

            match indexed ancestor, index key ours, index key theirs with
            | Ok ancestor, Ok ours, Ok theirs -> mergeItems path label ancestor ours theirs sorted |> Result.map (toArray >> fun array -> array :> JsonNode)
            | Error reason, _, _
            | _, Error reason, _
            | _, _, Error reason -> Error [ conflict path label reason ]
        | _ -> Error [ conflict path label "expected an array on every side" ]

    // ---- documents -----------------------------------------------------------

    let private propertyValue (record: JsonObject option) (name: string) =
        match record with
        | Some record when record.ContainsKey name -> Some record[name]
        | _ -> None

    let private text (node: JsonNode option) =
        match node with
        | Some(:? JsonValue as value) when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private number (node: JsonNode option) =
        match node with
        | Some(:? JsonValue as value) ->
            match value.TryGetValue<decimal>() with
            | true, parsed -> Some parsed
            | _ -> None
        | _ -> None

    let private mergeProperty path (ancestor: JsonObject option) (ours: JsonObject) (theirs: JsonObject) (rule: PropertyRule option) name =
        let ancestorValue = propertyValue ancestor name
        let oursValue = propertyValue (Some ours) name
        let theirsValue = propertyValue (Some theirs) name
        let whole () = threeWay ancestorValue oursValue theirsValue |> Result.mapError (fun reason -> [ conflict path name reason ])

        match rule with
        | Some(PropertyRule.KeyedArray key) when oursValue.IsSome && theirsValue.IsSome ->
            mergeKeyedArrays path name key false ancestorValue oursValue theirsValue |> Result.map Some
        | Some PropertyRule.Latest ->
            match text oursValue, text theirsValue with
            | Some left, Some right -> Ok(if String.CompareOrdinal(left, right) >= 0 then oursValue else theirsValue)
            | _ -> whole ()
        | Some PropertyRule.Earliest ->
            match text oursValue, text theirsValue with
            | Some left, Some right -> Ok(if String.CompareOrdinal(left, right) <= 0 then oursValue else theirsValue)
            | _ -> whole ()
        | Some PropertyRule.Maximum ->
            match number oursValue, number theirsValue with
            | Some left, Some right -> Ok(if left >= right then oursValue else theirsValue)
            | _ -> whole ()
        | Some(PropertyRule.FollowsLatest timestamp) ->
            match whole () with
            | Ok merged -> Ok merged
            | Error found ->
                match text (propertyValue (Some ours) timestamp), text (propertyValue (Some theirs) timestamp) with
                | Some left, Some right when left <> right -> Ok(if String.CompareOrdinal(left, right) > 0 then oursValue else theirsValue)
                | _ -> Error found
        | _ -> whole ()

    let private mergeDocument path (properties: (string * PropertyRule) list) (ancestor: JsonNode option) (ours: JsonNode) (theirs: JsonNode) =
        match ancestor, ours, theirs with
        | (None | Some(:? JsonObject)), (:? JsonObject as oursRecord), (:? JsonObject as theirsRecord) ->
            let ancestorRecord = ancestor |> Option.map (fun node -> node :?> JsonObject)

            let names =
                [ yield! oursRecord |> Seq.map _.Key
                  yield! theirsRecord |> Seq.map _.Key
                  yield! ancestorRecord |> Option.map (Seq.map _.Key >> Seq.toList) |> Option.defaultValue [] ]
                |> List.distinct

            names
            |> List.map (fun name ->
                mergeProperty path ancestorRecord oursRecord theirsRecord (properties |> List.tryFind (fst >> (=) name) |> Option.map snd) name
                |> Result.map (fun merged -> merged |> Option.map (fun value -> name, value)))
            |> collect
            |> Result.map (fun entries ->
                JsonObject(entries |> List.choose id |> List.map (fun (name, value) -> Collections.Generic.KeyValuePair(name, clone value)))
                :> JsonNode)
        | _ -> Error [ conflict path "document" "expected a JSON object on every side" ]

    let private mergeKeyedObject path (ancestor: JsonNode option) (ours: JsonNode) (theirs: JsonNode) =
        let entries (node: JsonNode) =
            match node with
            | :? JsonObject as record -> Some(record |> Seq.map (fun pair -> pair.Key, pair.Value) |> Seq.toList)
            | _ -> None

        match ancestor |> Option.map entries, entries ours, entries theirs with
        | (None | Some(Some _)) as ancestorEntries, Some oursEntries, Some theirsEntries ->
            let ancestorList = ancestorEntries |> Option.flatten |> Option.defaultValue []

            mergeItems path "entry" ancestorList oursEntries theirsEntries false
            |> Result.map (fun merged ->
                JsonObject(merged |> List.map (fun (name, value) -> Collections.Generic.KeyValuePair(name, clone value))) :> JsonNode)
        | _ -> Error [ conflict path "document" "expected a JSON object on every side" ]

    let private parseJson (text: string) : Result<JsonNode, string> =
        try
            match JsonNode.Parse text with
            | null -> Error "the document is JSON null"
            | node -> Ok node
        with error ->
            Error $"malformed JSON: {error.Message}"

    let private mergeJson path (merge: JsonNode option -> JsonNode -> JsonNode -> Result<JsonNode, StateMergeConflict list>) ancestor ours theirs =
        let ancestorNode =
            match ancestor with
            | None -> Ok None
            | Some text -> parseJson text |> Result.map Some

        match ancestorNode, parseJson ours, parseJson theirs with
        | Ok ancestorNode, Ok oursNode, Ok theirsNode ->
            match merge ancestorNode oursNode theirsNode with
            | Ok merged -> StateMergeOutcome.Merged(merged.ToJsonString indented + "\n")
            | Error conflicts -> StateMergeOutcome.Conflicted conflicts
        | Error reason, _, _ -> StateMergeOutcome.Unsupported $"common ancestor: {reason}"
        | _, Error reason, _ -> StateMergeOutcome.Unsupported $"ours: {reason}"
        | _, _, Error reason -> StateMergeOutcome.Unsupported $"theirs: {reason}"

    // ---- append-only lines ---------------------------------------------------

    type private Line =
        { Key: string
          Order: string
          Text: string
          Node: JsonNode }

    let private parseLines (key: string) (order: string) (text: string) : Result<Line list, string> =
        text.Split('\n')
        |> Array.map (fun line -> line.TrimEnd '\r')
        |> Array.filter (fun line -> line.Trim().Length > 0)
        |> Array.toList
        |> List.mapi (fun number line ->
            match parseJson line with
            | Error reason -> Error $"line {number + 1}: {reason}"
            | Ok node ->
                match stringField key node with
                | None -> Error $"line {number + 1}: no string '{key}'"
                | Some id ->
                    Ok
                        { Key = id
                          Order = stringField order node |> Option.defaultValue ""
                          Text = line
                          Node = node })
        |> List.fold
            (fun state entry ->
                match state, entry with
                | Error reason, _ -> Error reason
                | Ok _, Error reason -> Error reason
                | Ok lines, Ok line when lines |> List.exists (fun existing -> existing.Key = line.Key) -> Error $"duplicate '{key}' {line.Key}"
                | Ok lines, Ok line -> Ok(line :: lines))
            (Ok [])
        |> Result.map List.rev

    let private before (left: Line) (right: Line) =
        match String.CompareOrdinal(left.Order, right.Order) with
        | 0 -> String.CompareOrdinal(left.Key, right.Key) < 0
        | order -> order < 0

    /// Interleaves two sequences of new entries by (order, key), keeping each
    /// side's own sequence and emitting an entry both sides added once. The
    /// result depends only on the two sequences, never on which is "ours".
    let rec private interleave (emitted: Set<string>) (left: Line list) (right: Line list) (accumulator: Line list) =
        match left, right with
        | head :: rest, _ when emitted.Contains head.Key -> interleave emitted rest right accumulator
        | _, head :: rest when emitted.Contains head.Key -> interleave emitted left rest accumulator
        | [], [] -> List.rev accumulator
        | head :: rest, [] -> interleave (emitted.Add head.Key) rest [] (head :: accumulator)
        | [], head :: rest -> interleave (emitted.Add head.Key) [] rest (head :: accumulator)
        | leftHead :: leftRest, rightHead :: rightRest when leftHead.Key = rightHead.Key ->
            interleave (emitted.Add leftHead.Key) leftRest rightRest (leftHead :: accumulator)
        | leftHead :: leftRest, rightHead :: _ when before leftHead rightHead ->
            interleave (emitted.Add leftHead.Key) leftRest right (leftHead :: accumulator)
        | _, rightHead :: rightRest -> interleave (emitted.Add rightHead.Key) left rightRest (rightHead :: accumulator)

    let private mergeLines path (key: string) (order: string) (ancestor: string option) (ours: string) (theirs: string) =
        let parsed text = parseLines key order text

        match ancestor |> Option.map parsed |> Option.defaultValue (Ok []), parsed ours, parsed theirs with
        | Error reason, _, _ -> StateMergeOutcome.Unsupported $"common ancestor: {reason}"
        | _, Error reason, _ -> StateMergeOutcome.Unsupported $"ours: {reason}"
        | _, _, Error reason -> StateMergeOutcome.Unsupported $"theirs: {reason}"
        | Ok ancestorLines, Ok oursLines, Ok theirsLines ->
            let find (lines: Line list) id = lines |> List.tryFind (fun line -> line.Key = id)
            let ancestorKeys = ancestorLines |> List.map _.Key |> Set.ofList

            // History is append-only: an entry may never vanish or change.
            let historyConflicts =
                ancestorLines
                |> List.collect (fun line ->
                    [ ("ours", find oursLines line.Key); ("theirs", find theirsLines line.Key) ]
                    |> List.choose (fun (side, current) ->
                        match current with
                        | None -> Some(conflict path $"{key} {line.Key}" $"removed from the append-only history on {side}")
                        | Some current when not (JsonNode.DeepEquals(current.Node, line.Node)) ->
                            Some(conflict path $"{key} {line.Key}" $"rewritten in the append-only history on {side}")
                        | Some _ -> None))

            let added (lines: Line list) = lines |> List.filter (fun line -> not (ancestorKeys.Contains line.Key))
            let oursAdded = added oursLines
            let theirsAdded = added theirsLines

            let additionConflicts =
                oursAdded
                |> List.choose (fun line ->
                    match find theirsAdded line.Key with
                    | Some other when not (JsonNode.DeepEquals(other.Node, line.Node)) ->
                        Some(conflict path $"{key} {line.Key}" "added on both sides with different content")
                    | _ -> None)

            match historyConflicts @ additionConflicts with
            | [] ->
                let merged = ancestorLines @ interleave Set.empty oursAdded theirsAdded []
                StateMergeOutcome.Merged(merged |> List.map (fun line -> line.Text + "\n") |> String.concat "")
            | conflicts -> StateMergeOutcome.Conflicted conflicts

    // ---- Markdown table ------------------------------------------------------

    type private Table =
        { Prefix: string list
          Rows: (string * string) list
          Suffix: string list }

    let private parseTable (text: string) : Result<Table, string> =
        let lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n') |> Array.toList
        let isSeparator (line: string) = line.StartsWith "|" && line.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Length = 0 && line.Contains "-"

        match lines |> List.tryFindIndex isSeparator with
        | None -> Error "no Markdown table"
        | Some separator ->
            let prefix = lines |> List.take (separator + 1)
            let rest = lines |> List.skip (separator + 1)
            let rows = rest |> List.takeWhile (fun line -> line.StartsWith "|")
            let suffix = rest |> List.skip rows.Length

            let keyed =
                rows
                |> List.map (fun row ->
                    let cells = row.Split('|')
                    (if cells.Length > 1 then cells[1].Trim() else ""), row)

            if keyed |> List.exists (fst >> String.IsNullOrEmpty) then Error "a row has no first cell"
            elif (keyed |> List.map fst |> List.distinct).Length <> keyed.Length then Error "duplicate rows"
            else Ok { Prefix = prefix; Rows = keyed; Suffix = suffix }

    let private mergeTable path (ancestor: string option) (ours: string) (theirs: string) =
        let wrap (lines: string list) = Some(JsonValue.Create(String.Join("\n", lines)) :> JsonNode)
        let rowNode (row: string) = JsonValue.Create row :> JsonNode

        let ancestorTable =
            match ancestor with
            | None -> Ok None
            | Some text -> parseTable text |> Result.map Some

        match ancestorTable, parseTable ours, parseTable theirs with
        | Error reason, _, _ -> StateMergeOutcome.Unsupported $"common ancestor: {reason}"
        | _, Error reason, _ -> StateMergeOutcome.Unsupported $"ours: {reason}"
        | _, _, Error reason -> StateMergeOutcome.Unsupported $"theirs: {reason}"
        | Ok ancestorTable, Ok oursTable, Ok theirsTable ->
            let rows (table: Table) = table.Rows |> List.map (fun (id, row) -> id, rowNode row)
            let ancestorRows = ancestorTable |> Option.map rows |> Option.defaultValue []
            let section select = threeWay (ancestorTable |> Option.bind (select >> wrap)) (wrap (select oursTable)) (wrap (select theirsTable))

            match section _.Prefix, section _.Suffix, mergeItems path "row" ancestorRows (rows oursTable) (rows theirsTable) true with
            | Ok prefix, Ok suffix, Ok merged ->
                let lines (node: JsonNode option) =
                    node |> Option.map (fun value -> value.GetValue<string>()) |> Option.filter (fun value -> value.Length > 0) |> Option.toList

                let body = merged |> List.map (fun (_, row) -> row.GetValue<string>())
                StateMergeOutcome.Merged(String.Join("\n", lines prefix @ body @ lines suffix) + "\n")
            | prefix, suffix, rowsResult ->
                let sectionConflict name result =
                    match result with
                    | Error reason -> [ conflict path name reason ]
                    | Ok _ -> []

                StateMergeOutcome.Conflicted(
                    sectionConflict "table header" prefix
                    @ sectionConflict "text after the table" suffix
                    @ (match rowsResult with
                       | Error found -> found
                       | Ok _ -> [])
                )

    // ---- entry point ---------------------------------------------------------

    /// Merges one state file. `ancestor` is `None` when the file did not
    /// exist in the common ancestor.
    let merge (path: string) (ancestor: string option) (ours: string) (theirs: string) : StateMergeOutcome =
        let ancestor = ancestor |> Option.filter (fun text -> text.Trim().Length > 0)

        match ruleFor path with
        | None -> StateMergeOutcome.Unsupported $"{path} is not a Praxis state file this merge covers"
        | Some(StateFileRule.AppendOnlyLines(key, order)) -> mergeLines path key order ancestor ours theirs
        | Some(StateFileRule.Document properties) -> mergeJson path (mergeDocument path properties) ancestor ours theirs
        | Some(StateFileRule.SortedKeyedArray key) ->
            mergeJson path (fun ancestor ours theirs -> mergeKeyedArrays path "entry" key true ancestor (Some ours) (Some theirs)) ancestor ours theirs
        | Some StateFileRule.KeyedObject -> mergeJson path (mergeKeyedObject path) ancestor ours theirs
        | Some StateFileRule.MarkdownTable -> mergeTable path ancestor ours theirs
