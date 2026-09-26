namespace Ros.Contracts.Provenance

open System
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Domain.Provenance

/// What a consumer found when it read a provenance interchange record
/// (`schemas/provenance-record.schema.json`, RQ-ROS-2026-A013). `Raw` is the
/// document exactly as received: every write goes through it, so fields this
/// version does not model survive a round trip.
[<RequireQualifiedAccess>]
type ProvenanceRecordReading =
    /// `contract`/`version` present, supported major.
    | Current of record: ProvenanceRecord * raw: JsonObject
    /// A bare `{"contributions": ...}` block (an artifact's provenance as
    /// projected into a registry) without contract or version: read as
    /// version 1.0.0, never rejected for the missing envelope.
    | Unversioned of record: ProvenanceRecord * raw: JsonObject
    /// A well-labelled record in a major version this consumer does not
    /// support: preserved verbatim, never interpreted, modified, or extended.
    | Unsupported of version: string * raw: JsonObject

/// The JSON codec for the provenance interchange record. Contributions and
/// actors are exactly the artifact-provenance and actor shapes; this module
/// adds only the versioned envelope, lineage, and lossless preservation.
[<RequireQualifiedAccess>]
module ProvenanceRecordJson =
    let private problem field message : ProvenanceProblem = { Field = field; Message = message }

    let private stringOf (node: JsonNode) =
        match node with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, text -> Some text
            | _ -> None
        | _ -> None

    let private stringField (item: JsonObject) (name: string) = stringOf item[name]

    let private stringArray (prefix: string) (node: JsonNode) : Result<string list, ProvenanceProblem list> =
        match node with
        | null -> Ok []
        | :? JsonArray as items ->
            let values = items |> Seq.map stringOf |> Seq.toList

            if values |> List.forall Option.isSome then
                Ok(values |> List.choose id)
            else
                Error [ problem prefix "must be an array of strings" ]
        | _ -> Error [ problem prefix "must be an array of strings" ]

    let private parseContribution (key: string) (node: JsonNode) : Result<Contribution, ProvenanceProblem list> =
        let prefix = $"contributions.{key}"

        match node with
        | :? JsonObject as entry ->
            let operations = stringArray $"{prefix}.operations" entry["operations"]
            let evidence = stringArray $"{prefix}.evidence" entry["evidence"]

            let actor =
                match entry["actor"] with
                | null -> Error [ problem $"{prefix}.actor" "actor is required" ]
                | value ->
                    match ActorJson.tryParse value with
                    | Ok(Some actor) -> Ok actor
                    | Ok None -> Error [ problem $"{prefix}.actor" "actor is required" ]
                    | Error message -> Error [ problem $"{prefix}.actor" message ]

            match operations, evidence, actor with
            | Ok operationTexts, Ok evidenceItems, Ok actor ->
                let unknown =
                    operationTexts
                    |> List.filter (ContributionOperation.tryParse >> Option.isNone)
                    |> List.map (fun operation -> problem $"{prefix}.operations" $"unknown operation '{operation}'")

                let duplicates =
                    if List.length (List.distinct operationTexts) <> List.length operationTexts then
                        [ problem $"{prefix}.operations" "operations must be unique" ]
                    else
                        []

                match unknown @ duplicates with
                | [] ->
                    Ok
                        { Key = key
                          Operations = operationTexts |> List.choose ContributionOperation.tryParse
                          At = stringField entry "at" |> Option.defaultValue ""
                          Last = stringField entry "last"
                          Actor = actor
                          Reason = stringField entry "reason"
                          Evidence = evidenceItems }
                | problems -> Error problems
            | _ ->
                Error(
                    [ operations |> Result.map ignore; evidence |> Result.map ignore; actor |> Result.map ignore ]
                    |> List.collect (function Error problems -> problems | Ok () -> [])
                )
        | _ -> Error [ problem prefix "contribution must be an object" ]

    let private collect (results: Result<'value, ProvenanceProblem list> list) : Result<'value list, ProvenanceProblem list> =
        match results |> List.collect (function Error problems -> problems | Ok _ -> []) with
        | [] -> results |> List.choose (function Ok value -> Some value | Error _ -> None) |> Ok
        | problems -> Error problems

    let private parseContributions (node: JsonNode) : Result<ArtifactProvenance, ProvenanceProblem list> =
        match node with
        | :? JsonObject as entries ->
            entries
            |> Seq.map (fun pair -> parseContribution pair.Key pair.Value)
            |> Seq.toList
            |> collect
            |> Result.map ArtifactProvenance.ofContributions
        | null -> Error [ problem "contributions" "contributions is required" ]
        | _ -> Error [ problem "contributions" "contributions must be an object keyed by execution (EXE-...) or contribution (CTB-...) ID" ]

    let rec private readAt (depth: int) (node: JsonNode) : Result<ProvenanceRecordReading, ProvenanceProblem list> =
        match node with
        | :? JsonObject as raw ->
            let contract = raw["contract"]
            let version = raw["version"]

            let body (version: ContractVersion) : Result<ProvenanceRecord, ProvenanceProblem list> =
                let contributions = parseContributions raw["contributions"]
                let derivedFrom = stringArray "derivedFrom" raw["derivedFrom"]

                let subject =
                    match raw["subject"] with
                    | null -> Ok None
                    | value ->
                        match stringOf value with
                        | Some text -> Ok(Some text)
                        | None -> Error [ problem "subject" "subject must be a string" ]

                let sources =
                    match raw["sources"] with
                    | null -> Ok []
                    | :? JsonObject as snapshots when depth >= ProvenanceRecord.MaxSourceDepth && snapshots.Count > 0 ->
                        Error [ problem "sources" $"lineage snapshots nest deeper than {ProvenanceRecord.MaxSourceDepth} levels" ]
                    | :? JsonObject as snapshots ->
                        snapshots
                        |> Seq.map (fun pair ->
                            match readAt (depth + 1) pair.Value with
                            | Ok(ProvenanceRecordReading.Current(source, _))
                            | Ok(ProvenanceRecordReading.Unversioned(source, _)) -> Ok(pair.Key, SourceSnapshot.Known source)
                            | Ok(ProvenanceRecordReading.Unsupported(sourceVersion, _)) ->
                                Ok(pair.Key, SourceSnapshot.Opaque sourceVersion)
                            | Error problems ->
                                Error(problems |> List.map (fun item -> { item with Field = $"sources.{pair.Key}.{item.Field}" })))
                        |> Seq.toList
                        |> collect
                    | _ -> Error [ problem "sources" "sources must be an object keyed by lineage reference" ]

                match contributions, derivedFrom, subject, sources with
                | Ok provenance, Ok lineage, Ok subject, Ok snapshots ->
                    Ok
                        { Version = version
                          Subject = subject
                          Provenance = provenance
                          DerivedFrom = lineage
                          Sources = snapshots }
                | _ ->
                    Error(
                        [ contributions |> Result.map ignore
                          derivedFrom |> Result.map ignore
                          subject |> Result.map ignore
                          sources |> Result.map ignore ]
                        |> List.collect (function Error problems -> problems | Ok () -> [])
                    )

            match contract, version with
            | null, null ->
                body ContractVersion.current
                |> Result.map (fun record -> ProvenanceRecordReading.Unversioned(record, raw))
            | contract, _ when stringOf contract <> Some ProvenanceRecord.ContractName ->
                Error [ problem "contract" $"contract must be '{ProvenanceRecord.ContractName}'" ]
            | _, version ->
                match version |> stringOf |> Option.bind ContractVersion.tryParse with
                | None -> Error [ problem "version" "version must be a semantic version (MAJOR.MINOR.PATCH)" ]
                | Some parsed when not (ContractVersion.isSupported parsed) ->
                    Ok(ProvenanceRecordReading.Unsupported(ContractVersion.code parsed, raw))
                | Some parsed -> body parsed |> Result.map (fun record -> ProvenanceRecordReading.Current(record, raw))
        | _ -> Error [ problem "" "a provenance record must be a JSON object" ]

    /// Reads a record (structure only; see `validate`).
    let read (node: JsonNode) = readAt 0 node

    let parse (text: string) : Result<ProvenanceRecordReading, ProvenanceProblem list> =
        try
            JsonNode.Parse text |> read
        with :? JsonException as error ->
            Error [ problem "" $"not valid JSON: {error.Message}" ]

    /// Reads and applies every structural rule. An unsupported major version
    /// is not an error: it is reported by the reading and must be carried
    /// verbatim.
    let validate (node: JsonNode) : Result<ProvenanceRecordReading, ProvenanceProblem list> =
        match read node with
        | Ok(ProvenanceRecordReading.Current(record, _) as reading)
        | Ok(ProvenanceRecordReading.Unversioned(record, _) as reading) ->
            match ProvenanceRecord.problems record with
            | [] -> Ok reading
            | problems -> Error problems
        | other -> other

    let private strings (values: string list) : JsonArray =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value: JsonNode))
        array

    /// The canonical form of one contribution, identical to the artifact
    /// front-matter entry: operations, at, last?, actor, reason?, evidence?.
    let contributionNode (contribution: Contribution) : JsonObject =
        let node = JsonObject()
        node["operations"] <- strings (contribution.Operations |> List.map ContributionOperation.code)
        node["at"] <- JsonValue.Create contribution.At
        contribution.Last |> Option.iter (fun last -> node["last"] <- JsonValue.Create last)
        node["actor"] <- ActorJson.node contribution.Actor
        contribution.Reason |> Option.iter (fun reason -> node["reason"] <- JsonValue.Create reason)

        if not contribution.Evidence.IsEmpty then
            node["evidence"] <- strings contribution.Evidence

        node

    let private envelope (subject: string option) =
        let node = JsonObject()
        node["contract"] <- JsonValue.Create ProvenanceRecord.ContractName
        node["version"] <- JsonValue.Create(ContractVersion.code ContractVersion.current)
        subject |> Option.iter (fun value -> node["subject"] <- JsonValue.Create value)
        node

    /// A fresh record for a subject whose provenance starts here.
    let create (subject: string option) (contribution: Contribution) : Result<JsonObject, ProvenanceProblem list> =
        let record =
            { ProvenanceRecord.empty with
                Subject = subject
                Provenance = ArtifactProvenance.ofContributions [ contribution ] }

        match ProvenanceRecord.problems record with
        | [] ->
            let node = envelope subject
            let contributions = JsonObject()
            contributions[contribution.Key] <- contributionNode contribution
            node["contributions"] <- contributions
            Ok node
        | problems -> Error problems

    let private clone (node: JsonObject) = node.DeepClone().AsObject()

    let private sameNode (left: JsonNode) (right: JsonNode) = JsonNode.DeepEquals(left, right)

    /// JSON-level preservation for one hop: everything `before` carried --
    /// including fields this version does not model, on the record, on each
    /// contribution, and on each actor -- is still present and unchanged in
    /// `after`, except the fields a contribution may legitimately extend
    /// (`operations`, `evidence`, `last`) and `reason` when it was absent.
    let private preservationProblems (before: JsonObject) (after: JsonObject) : ProvenanceProblem list =
        let topLevel =
            before
            |> Seq.filter (fun pair -> pair.Key <> "contributions" && pair.Key <> "derivedFrom" && pair.Key <> "sources" && pair.Key <> "version" && pair.Key <> "contract")
            |> Seq.filter (fun pair -> not (after.ContainsKey pair.Key) || not (sameNode pair.Value after[pair.Key]))
            |> Seq.map (fun pair -> problem pair.Key "field was removed or changed; fields a consumer does not model must be preserved")
            |> Seq.toList

        let extendable = set [ "operations"; "evidence"; "last"; "reason" ]

        let contributions =
            match before["contributions"], after["contributions"] with
            | (:? JsonObject as previous), (:? JsonObject as current) ->
                previous
                |> Seq.collect (fun pair ->
                    match pair.Value, current[pair.Key] with
                    | (:? JsonObject as entry), (:? JsonObject as successor) ->
                        entry
                        |> Seq.filter (fun field -> not (extendable.Contains field.Key))
                        |> Seq.filter (fun field -> not (successor.ContainsKey field.Key) || not (sameNode field.Value successor[field.Key]))
                        |> Seq.map (fun field ->
                            problem $"contributions.{pair.Key}.{field.Key}" "field was removed or changed; another contributor's entry must be preserved verbatim")
                        |> Seq.toList
                    | _ -> [])
                |> Seq.toList
            | _ -> []

        let snapshots =
            match before["sources"], after["sources"] with
            | (:? JsonObject as previous), (:? JsonObject as current) ->
                previous
                |> Seq.filter (fun pair -> current.ContainsKey pair.Key && not (sameNode pair.Value current[pair.Key]))
                |> Seq.map (fun pair -> problem $"sources.{pair.Key}" "lineage snapshot must be carried verbatim")
                |> Seq.toList
            | _ -> []

        topLevel @ contributions @ snapshots

    /// Whether `after` is a non-destructive successor of `before`
    /// (RQ-ROS-2026-A015): nothing removed, no original actor overwritten, no
    /// history replaced, no execution identity lost, unknown fields kept. An
    /// unsupported `before` must be carried byte-for-byte equivalent.
    let private interpreted (reading: ProvenanceRecordReading) =
        match reading with
        | ProvenanceRecordReading.Current(record, _)
        | ProvenanceRecordReading.Unversioned(record, _) -> Some record
        | ProvenanceRecordReading.Unsupported _ -> None

    let successorProblems (before: JsonObject) (after: JsonObject) : ProvenanceProblem list =
        match read before, read after with
        | Ok(ProvenanceRecordReading.Unsupported _), _ ->
            if sameNode before after then
                []
            else
                [ problem "" "a record in an unsupported major version must be carried verbatim" ]
        | Error _, _ -> [ problem "" "the previous record is malformed; refusing to judge a successor of it" ]
        | _, Error problems -> problems
        | Ok previous, Ok current ->
            match interpreted previous, interpreted current with
            | Some previous, Some current -> ProvenanceRecord.successorProblems previous current @ preservationProblems before after
            | _, _ ->
                [ problem "version" "a supported record was replaced by an unsupported major version; a record's major version never changes in place" ]

    /// Appends (or merges into the contributing execution's own entry) one
    /// contribution. The raw record is cloned and only the touched entry's
    /// `operations`/`last`/`evidence`/`reason` change; every other byte of
    /// meaning -- other contributors, unknown fields, lineage snapshots --
    /// is carried over and then proven unchanged before the result is
    /// returned. A legacy unversioned block gains the envelope.
    let append (contribution: Contribution) (raw: JsonObject) : Result<JsonObject, ProvenanceProblem list> =
        match read raw with
        | Error problems -> Error problems
        | Ok(ProvenanceRecordReading.Unsupported(version, _)) ->
            Error [ problem "version" $"version {version} is not supported by this consumer; the record must be carried verbatim, not extended" ]
        | Ok(ProvenanceRecordReading.Current(record, _))
        | Ok(ProvenanceRecordReading.Unversioned(record, _)) ->
            match ProvenanceRecord.problems record with
            | problems when not problems.IsEmpty ->
                Error(problem "" "refusing to extend malformed provenance" :: problems)
            | _ ->
                match ProvenanceRecord.record contribution record with
                | Error message -> Error [ problem $"contributions.{contribution.Key}" message ]
                | Ok updated ->
                    let merged = updated.Provenance.Contributions |> List.find (fun item -> item.Key = contribution.Key)
                    let result = clone raw

                    if isNull result["contract"] then
                        result["contract"] <- JsonValue.Create ProvenanceRecord.ContractName
                        result["version"] <- JsonValue.Create(ContractVersion.code ContractVersion.current)

                    let contributions = result["contributions"].AsObject()
                    let canonical = contributionNode merged

                    match contributions[contribution.Key] with
                    | :? JsonObject as existing ->
                        for name in [ "operations"; "last"; "evidence"; "reason" ] do
                            match canonical[name] with
                            | null -> ()
                            | value -> existing[name] <- value.DeepClone()
                    | _ -> contributions[contribution.Key] <- canonical

                    match validate result with
                    | Error problems -> Error problems
                    | Ok _ ->
                        match successorProblems raw result with
                        | [] -> Ok result
                        | problems -> Error(problem "" "the appended record failed its own preservation check" :: problems)

    /// Starts the provenance of a new subject derived from other subjects:
    /// `creator` authors it; each source's record is carried verbatim as a
    /// lineage snapshot and named in `derivedFrom`, never merged into the
    /// new subject's contributions.
    let derive (subject: string) (creator: Contribution) (sources: (string * JsonObject) list) (lineageOnly: string list) : Result<JsonObject, ProvenanceProblem list> =
        let readings =
            sources
            |> List.map (fun (reference, node) ->
                match read node with
                | Ok(ProvenanceRecordReading.Current(record, _))
                | Ok(ProvenanceRecordReading.Unversioned(record, _)) -> Ok(reference, SourceSnapshot.Known record)
                | Ok(ProvenanceRecordReading.Unsupported(version, _)) -> Ok(reference, SourceSnapshot.Opaque version)
                | Error problems -> Error(problems |> List.map (fun item -> { item with Field = $"sources.{reference}.{item.Field}" })))
            |> collect

        match readings with
        | Error problems -> Error problems
        | Ok snapshots ->
            match ProvenanceRecord.derive subject creator snapshots with
            | Error message -> Error [ problem "contributions" message ]
            | Ok record ->
                let record = { record with DerivedFrom = (record.DerivedFrom @ lineageOnly) |> List.distinct }

                match ProvenanceRecord.problems record with
                | problems when not problems.IsEmpty -> Error problems
                | _ ->
                    let node = envelope (Some subject)
                    let contributions = JsonObject()
                    contributions[creator.Key] <- contributionNode creator
                    node["contributions"] <- contributions
                    node["derivedFrom"] <- strings record.DerivedFrom

                    if not sources.IsEmpty then
                        let snapshotNode = JsonObject()

                        sources
                        |> List.distinctBy fst
                        |> List.iter (fun (reference, source) -> snapshotNode[reference] <- source.DeepClone())

                        node["sources"] <- snapshotNode

                    Ok node

    let render (node: JsonNode) = ProvenanceReportJson.render node
