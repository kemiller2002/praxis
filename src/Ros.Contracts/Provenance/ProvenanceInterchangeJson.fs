namespace Ros.Contracts.Provenance

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Ros.Domain.Provenance

/// What a consumer may do with a provenance block it received
/// (RQ-ROS-2026-A015):
///
/// - `Supported`: a `praxis.provenance/1` block this reader understands.
///   `Warnings` name tolerated forward-compatible content (an operation this
///   version does not know); the caller keeps the original JSON so nothing
///   it did not model is lost.
/// - `Unsupported`: a well-formed block of another major version. It must be
///   carried verbatim and never interpreted, merged into, or rewritten.
/// - `Malformed`: not a provenance block a consumer may accept; reject it at
///   the boundary rather than silently dropping or "repairing" it.
type InterchangeVerdict =
    | Supported of provenance: ArtifactProvenance * derivedFrom: string list * warnings: string list
    | Unsupported of schema: string
    | Malformed of problems: string list

/// The portable, versioned JSON form of Praxis provenance
/// (`schemas/provenance-interchange.schema.json`): the same contributions and
/// lineage an artifact carries in front matter, tagged with a schema version
/// so another Echelon system can recognise, preserve, and reject it
/// deterministically.
///
/// ```json
/// {"schema":"praxis.provenance/1",
///  "subject":{"id":"RQ-ROS-2026-A013","path":"research/requirements/..."},
///  "contributions":{"EXE-...":{"operations":["created"],"at":"...","actor":{...}}},
///  "derivedFrom":["EV-ROS-2026-A049"]}
/// ```
[<RequireQualifiedAccess>]
module ProvenanceInterchangeJson =
    [<Literal>]
    let SchemaTag = "praxis.provenance/1"

    [<Literal>]
    let SchemaFamily = "praxis.provenance/"

    let private schemaPattern =
        Regex("^praxis\\.provenance/([1-9][0-9]*)\\z", RegexOptions.CultureInvariant)

    /// Operation grammar every major-1 reader accepts. Vocabulary it does not
    /// know is tolerated (and preserved) so a newer minor release can add an
    /// operation without breaking older consumers.
    let private operationGrammar =
        Regex("^[a-z][a-z0-9-]*\\z", RegexOptions.CultureInvariant)

    /// Recognisable credential shapes (RQ-ROS-2026-A017). Provenance
    /// identifies an actor and a run; it never needs, and must never carry,
    /// authentication material. This is a tripwire for accidents, not a
    /// complete secret scanner.
    let private credentialPatterns =
        [ "gh[pousr]_[A-Za-z0-9]{20,}"
          "github_pat_[A-Za-z0-9_]{20,}"
          "sk-[A-Za-z0-9_-]{20,}"
          "AKIA[0-9A-Z]{16}"
          "xox[abprs]-[A-Za-z0-9-]{10,}"
          "-----BEGIN [A-Z ]*PRIVATE KEY-----"
          // ASCII-only semantics (contract 1.2): no \\b, \\s, or case folding,
          // whose meaning differs between .NET, JavaScript, and Python.
          "(?:^|[^A-Za-z0-9_])[Bb][Ee][Aa][Rr][Ee][Rr][\\t\\n\\v\\f\\r ]+[A-Za-z0-9._~+/=-]{16,}"
          "eyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\." ]
        |> List.map (fun pattern -> Regex(pattern, RegexOptions.CultureInvariant))

    let isCredentialLike (value: string) =
        credentialPatterns |> List.exists (fun pattern -> pattern.IsMatch value)

    /// Every string (keys included) anywhere in the block that looks like a
    /// credential, as dotted paths.
    let credentialFindings (node: JsonNode) : string list =
        let rec walk (path: string) (current: JsonNode) : string list =
            match current with
            | :? JsonObject as item ->
                item
                |> Seq.collect (fun pair ->
                    let child = if path.Length = 0 then pair.Key else $"{path}.{pair.Key}"
                    (if isCredentialLike pair.Key then [ child ] else []) @ walk child pair.Value)
                |> Seq.toList
            | :? JsonArray as items -> items |> Seq.mapi (fun index value -> walk $"{path}[{index}]" value) |> Seq.concat |> Seq.toList
            | :? JsonValue as value ->
                match value.TryGetValue<string>() with
                | true, text when isCredentialLike text -> [ path ]
                | _ -> []
            | _ -> []

        walk "" node

    let private contributionNode (item: Contribution) : JsonObject =
        let node = JsonObject()
        node["operations"] <- ProvenanceReportJson.strings (item.Operations |> List.map ContributionOperation.code)
        node["at"] <- JsonValue.Create item.At
        item.Last |> Option.iter (fun last -> node["last"] <- JsonValue.Create last)
        node["actor"] <- ActorJson.node item.Actor
        item.Reason |> Option.iter (fun reason -> node["reason"] <- JsonValue.Create reason)

        if not item.Evidence.IsEmpty then
            node["evidence"] <- ProvenanceReportJson.strings item.Evidence

        node

    /// Canonical interchange form. `subject` is included only for a
    /// standalone export; a block embedded in another record omits it.
    let toNode (subject: (string option * string option) option) (derivedFrom: string list) (provenance: ArtifactProvenance) =
        let node = JsonObject()
        node["schema"] <- JsonValue.Create SchemaTag

        subject
        |> Option.iter (fun (id, path) ->
            let item = JsonObject()
            id |> Option.iter (fun value -> item["id"] <- JsonValue.Create value)
            path |> Option.iter (fun value -> item["path"] <- JsonValue.Create value)
            node["subject"] <- item)

        let contributions = JsonObject()

        provenance.Contributions
        |> List.iter (fun item -> contributions[item.Key] <- contributionNode item)

        node["contributions"] <- contributions

        if not derivedFrom.IsEmpty then
            node["derivedFrom"] <- ProvenanceReportJson.strings derivedFrom

        node

    let private stringOf (node: JsonNode) =
        match node with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, text -> Some text
            | _ -> None
        | _ -> None

    /// A present property's value (which may be JSON `null`), or `None` when
    /// the property is absent: the two are never conflated (contract 1.1).
    let private property (container: JsonObject) (name: string) : JsonNode option =
        if container.ContainsKey name then Some container[name] else None

    /// An optional array of non-empty strings. Absent is empty; JSON `null`
    /// is not absent (contract 1.1) and is rejected like any other non-array.
    let private stringArray (field: string) (container: JsonObject) (name: string) : Result<string list, string> =
        match property container name with
        | None -> Ok []
        | Some(:? JsonArray as items) ->
            let values = items |> Seq.map stringOf |> Seq.toList

            if values |> List.forall (fun value -> value |> Option.exists (AsciiText.isBlank >> not)) then
                Ok(values |> List.choose id)
            else
                Error $"{field} must be an array of non-empty strings"
        | Some _ -> Error $"{field} must be an array of non-empty strings"

    let private parseContribution (key: string) (node: JsonNode) : Result<Contribution * string list, string list> =
        let prefix = $"contributions.{key}"

        match node with
        | :? JsonObject as entry ->
            let operations =
                if entry.ContainsKey "operations" then
                    stringArray $"{prefix}.operations" entry "operations"
                else
                    Error $"{prefix}.operations is required"

            let evidence = stringArray $"{prefix}.evidence" entry "evidence"
            let actor = ActorJson.tryParse entry["actor"]

            // A present actor field must be a string: JSON null never means
            // "not applicable" (that is expressed by omitting the field).
            let actorFieldProblems =
                match entry["actor"] with
                | :? JsonObject as actorNode ->
                    [ "kind"; "id"; "provider"; "model"; "runtime" ]
                    |> List.choose (fun name ->
                        match property actorNode name with
                        | Some value when (stringOf value).IsNone -> Some $"{prefix}.actor.{name} must be a string"
                        | _ -> None)
                | _ -> []

            let structural =
                [ match operations with
                  | Error message -> message
                  | Ok [] -> $"{prefix}.operations must record at least one operation"
                  | Ok values ->
                      for value in values do
                          if not (operationGrammar.IsMatch value) then
                              $"{prefix}.operations: '{value}' is not a valid operation code"

                      if (values |> List.distinct).Length <> values.Length then
                          $"{prefix}.operations must not repeat an operation"
                  match evidence with
                  | Error message -> message
                  | Ok _ -> ()
                  match actor with
                  | Error message -> $"{prefix}.actor: {message}"
                  | Ok None -> $"{prefix}.actor is required"
                  | Ok(Some _) -> ()
                  match entry["at"] with
                  | :? JsonValue as value when (stringOf value).IsSome -> ()
                  | _ -> $"{prefix}.at is required"
                  for optional in [ "last"; "reason" ] do
                      match property entry optional with
                      | None -> ()
                      | Some value when (stringOf value).IsSome -> ()
                      | Some _ -> $"{prefix}.{optional} must be a string"
                  yield! actorFieldProblems ]

            match structural, operations, evidence, actor with
            | [], Ok operationCodes, Ok evidenceItems, Ok(Some parsedActor) ->
                let parsed =
                    operationCodes
                    |> List.map (fun code ->
                        match ContributionOperation.tryParse code with
                        | Some operation -> operation, None
                        | None ->
                            // Forward-compatible vocabulary: preserved verbatim,
                            // reported, never coerced into a known operation.
                            ContributionOperation.Extension code,
                            Some $"{prefix}.operations: '{code}' is not an operation this Praxis version knows; preserved verbatim")

                let contribution =
                    { Key = key
                      Operations = parsed |> List.map fst
                      At = stringOf entry["at"] |> Option.defaultValue ""
                      Last = stringOf entry["last"]
                      Actor = parsedActor
                      Reason = stringOf entry["reason"] |> Option.filter (fun text -> text.Trim().Length > 0)
                      Evidence = evidenceItems }

                let domainProblems =
                    Contribution.problems contribution |> List.map (fun (field, message) -> $"{prefix}.{field}: {message}")

                if domainProblems.IsEmpty then
                    Ok(contribution, parsed |> List.choose snd)
                else
                    Error domainProblems
            | problems, _, _, _ -> Error problems
        | _ -> Error [ $"{prefix} must be an object" ]

    /// Structural classification of a parsed block; `classify` wraps it so
    /// that no input can escape as an exception.
    let private classifyNode (node: JsonNode) : InterchangeVerdict =
        match node with
        | :? JsonObject as block when not (credentialFindings block).IsEmpty ->
            Malformed(
                credentialFindings block
                |> List.map (fun path -> $"{path}: credential-like value; provenance must never carry authentication material")
            )
        | :? JsonObject as block ->
            let schema = stringOf block["schema"]

            match schema with
            | Some tag when tag <> SchemaTag ->
                if schemaPattern.IsMatch tag then
                    Unsupported tag
                elif tag.StartsWith(SchemaFamily, StringComparison.Ordinal) then
                    Malformed [ $"schema '{tag}' is not a valid version tag; expected praxis.provenance/<major>" ]
                else
                    Malformed [ $"schema '{tag}' is not a Praxis provenance schema" ]
            | _ when block.ContainsKey "schema" && schema.IsNone -> Malformed [ "schema must be a string" ]
            | _ ->
                match block["contributions"] with
                | :? JsonObject as contributions ->
                    let parsed =
                        contributions
                        |> Seq.map (fun pair -> parseContribution pair.Key pair.Value)
                        |> Seq.toList

                    let lineage = stringArray "derivedFrom" block "derivedFrom"

                    let problems =
                        (parsed |> List.collect (function Error problems -> problems | Ok _ -> []))
                        @ (match lineage with Error message -> [ message ] | Ok _ -> [])

                    if not problems.IsEmpty then
                        Malformed problems
                    else
                        let items = parsed |> List.choose (function Ok item -> Some item | Error _ -> None)
                        let provenance = items |> List.map fst |> ArtifactProvenance.ofContributions

                        match ArtifactProvenance.problems provenance with
                        | [] ->
                            Supported(
                                provenance,
                                (match lineage with Ok values -> values | Error _ -> []),
                                items |> List.collect snd
                            )
                        | invariantProblems ->
                            Malformed(invariantProblems |> List.map (fun problem -> $"{problem.Field}: {problem.Message}"))
                | null -> Malformed [ "contributions is required" ]
                | _ -> Malformed [ "contributions must be an object keyed by EXE-, EXT-, or CTB- keys" ]
        | _ -> Malformed [ "provenance must be a JSON object" ]

    let private strictUtf8 = UTF8Encoding(false, true)

    /// Contract 1.2: whether a string is well-formed Unicode (no unpaired
    /// UTF-16 surrogate). Such a string has no UTF-8 form, so a block holding
    /// one could not be carried verbatim.
    let private isWellFormed (value: string) =
        try
            strictUtf8.GetByteCount value |> ignore
            true
        with :? EncoderFallbackException ->
            false

    /// Every member name or string value that is not well-formed Unicode,
    /// as dotted paths. Reading such a value from a parsed node throws, which
    /// counts as the same finding.
    let private surrogateFindings (node: JsonNode) : string list =
        let rec walk (path: string) (current: JsonNode) : string list =
            match current with
            | :? JsonObject as item ->
                item
                |> Seq.collect (fun pair ->
                    let child = if path.Length = 0 then pair.Key else $"{path}.{pair.Key}"
                    (if isWellFormed pair.Key then [] else [ child ]) @ walk child pair.Value)
                |> Seq.toList
            | :? JsonArray as items -> items |> Seq.mapi (fun index value -> walk $"{path}[{index}]" value) |> Seq.concat |> Seq.toList
            | :? JsonValue as value ->
                try
                    match value.TryGetValue<string>() with
                    | true, text when not (isWellFormed text) -> [ path ]
                    | _ -> []
                with :? InvalidOperationException ->
                    [ path ]
            | _ -> []

        walk "" node

    /// Classifies a received block. A block with no `schema` tag is read as
    /// `praxis.provenance/1` only when it is the bare front-matter/registry
    /// projection. Never throws (contract 1.2): a block that is not
    /// well-formed Unicode, or a node whose repeated member names surface
    /// only on enumeration, is malformed rather than an exception.
    let classify (node: JsonNode) : InterchangeVerdict =
        try
            match surrogateFindings node with
            | [] -> classifyNode node
            | paths -> Malformed(paths |> List.map (fun path -> $"{path}: unpaired UTF-16 surrogate; provenance must be well-formed Unicode"))
        with
        | :? ArgumentException as error -> Malformed [ $"provenance repeats a member name within one object: {error.Message}" ]
        | :? InvalidOperationException as error -> Malformed [ $"provenance is not well-formed: {error.Message}" ]

    /// Member names repeated within any one object, read from the JSON text
    /// itself: a node-based reader keeps only one of them, so two readers
    /// could disagree about which contribution (or originator) is real.
    let private duplicateMembers (bytes: byte array) : string list =
        let mutable reader = Utf8JsonReader(ReadOnlySpan<byte>(bytes), JsonReaderOptions(CommentHandling = JsonCommentHandling.Disallow))
        let scopes = Stack<HashSet<string> option>()
        let found = List<string>()

        while reader.Read() do
            match reader.TokenType with
            | JsonTokenType.StartObject -> scopes.Push(Some(HashSet<string>(StringComparer.Ordinal)))
            | JsonTokenType.StartArray -> scopes.Push None
            | JsonTokenType.EndObject
            | JsonTokenType.EndArray -> scopes.Pop() |> ignore
            | JsonTokenType.PropertyName ->
                let name = reader.GetString()

                match scopes.Peek() with
                | Some seen when not (seen.Add name) -> found.Add name
                | _ -> ()
            | JsonTokenType.String -> reader.GetString() |> ignore
            | _ -> ()

        found |> Seq.toList

    /// Classifies a block received as JSON text (contract 1.2): text that is
    /// not JSON, that is not well-formed Unicode, or that repeats a member
    /// name within any object is malformed, whatever its major version.
    let classifyText (text: string) : InterchangeVerdict =
        match text with
        | null -> Malformed [ "provenance text is required" ]
        | _ ->
            try
                let bytes = strictUtf8.GetBytes text

                match duplicateMembers bytes with
                | [] ->
                    match JsonNode.Parse(text) with
                    | null -> Malformed [ "provenance must be a JSON object" ]
                    | node -> classify node
                | names -> Malformed(names |> List.map (fun name -> $"'{name}': member name repeated within one object"))
            with
            | :? EncoderFallbackException -> Malformed [ "provenance text is not well-formed Unicode" ]
            | :? JsonException as error -> Malformed [ $"provenance is not valid JSON: {error.Message}" ]
            | :? InvalidOperationException -> Malformed [ "provenance holds an unpaired UTF-16 surrogate escape" ]
