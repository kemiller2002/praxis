namespace Praxis.Domain.Ordo

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json

/// One stable, machine-readable refusal. Codes are Ordo's own
/// `ORDO.CONTRACT.*` vocabulary so a Praxis refusal and an Ordo refusal of the
/// same bytes are comparable; `Message` is informative only.
type OrdoDiagnostic =
    { Code: string
      Subject: string option
      Message: string }

/// The identity of an Ordo constructibility contract Praxis admitted before
/// any agent work starts (shared interface I3). Every list is ordinal-sorted
/// and duplicate-free. `Digest` is Ordo's canonical blueprint digest,
/// `sha256:<64 lowercase hex>`. Admission is not execution authority.
type AdmittedOrdoContract =
    { Version: string
      Digest: string
      RequirementIds: string list
      DecisionIds: string list
      ConflictResolutionIds: string list }

/// Independent consumer of Ordo's published `ecir/1` wire format
/// (ordo examples/ecir/consumer, `ConstructibilityContract.admitContract`).
/// It deliberately does not call the Ordo binary: it reproduces the strict
/// decoding, manifest-independent semantic rules and canonical digest from the
/// documented wire contract, and the golden corpus proves byte-for-byte parity.
[<RequireQualifiedAccess>]
module OrdoContractAdmission =

    /// The wire versions this consumer can interpret. A caller may narrow this
    /// set through `admit`'s first argument but can never widen it.
    let implementedVersions = [ "ecir/1" ]

    [<Literal>]
    let UnsupportedSchemaCode = "ORDO.CONTRACT.UNSUPPORTED_SCHEMA"

    [<Literal>]
    let InvalidJsonCode = "ORDO.CONTRACT.INVALID_BLUEPRINT_JSON"

    [<Literal>]
    let StaleReferenceCode = "ORDO.CONTRACT.STALE_REFERENCE"

    [<Literal>]
    let InvalidReferenceDigestCode = "ORDO.CONTRACT.INVALID_REFERENCE_DIGEST"

    // -- Decoded wire model ---------------------------------------------------

    type private Source =
        { Key: string
          OriginalId: string
          Document: string
          Location: string
          Revision: string
          ContentDigest: string }

    type private Disposition =
        | Modeled
        | Deferred of reason: string
        | Unresolved of reason: string
        | Rejected of reason: string
        | Superseded of target: string * reason: string

    type private Requirement =
        { Source: Source
          Disposition: Disposition
          NodeIds: string list }

    /// Node kinds keep Ordo's wire spelling and its F# case name; the latter
    /// is part of Ordo's MISSING_DISPOSITION_NODE subject.
    type private NodeKind =
        | Decision
        | Contract
        | Invariant
        | Interface
        | Cohort
        | VerificationObligation
        | Conflict
        | Deferral
        | EngineeringRationale

    type private Node =
        { Id: string
          Kind: NodeKind
          RequirementKeys: string list
          DependsOn: string list
          Justification: string option }

    type private Blueprint =
        { SchemaVersion: string
          SourceManifestDigest: string
          Requirements: Requirement list
          Nodes: Node list }

    let private kinds =
        [ "decision", Decision; "contract", Contract; "invariant", Invariant; "interface", Interface
          "cohort", Cohort; "verificationObligation", VerificationObligation; "conflict", Conflict
          "deferral", Deferral; "engineeringRationale", EngineeringRationale ]

    let private kindWire kind = kinds |> List.find (snd >> (=) kind) |> fst

    let private kindCase kind =
        match kind with
        | Decision -> "Decision"
        | Contract -> "Contract"
        | Invariant -> "Invariant"
        | Interface -> "Interface"
        | Cohort -> "Cohort"
        | VerificationObligation -> "VerificationObligation"
        | Conflict -> "Conflict"
        | Deferral -> "Deferral"
        | EngineeringRationale -> "EngineeringRationale"

    // -- Strict decoding (mirrors Ordo StrictJson) ----------------------------

    let private fail path message : Result<'a, string> = Error(sprintf "%s: %s" path message)

    let private traverse (decode: int -> 'a -> Result<'b, string>) (values: 'a list) =
        let folder state (index, value) =
            state |> Result.bind (fun acc -> decode index value |> Result.map (fun item -> item :: acc))
        values |> List.indexed |> List.fold folder (Ok []) |> Result.map List.rev

    let private fields path (required: string list) (optional: string list) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then fail path "expected object"
        else
            let properties = [ for property in element.EnumerateObject() -> property.Name, property.Value ]
            let names = properties |> List.map fst
            let duplicate = names |> List.countBy id |> List.tryFind (fun (_, count) -> count <> 1)
            let unknown = names |> List.tryFind (fun name -> not (List.contains name (required @ optional)))
            let missing = required |> List.tryFind (fun name -> not (List.contains name names))
            match duplicate, unknown, missing with
            | Some(name, _), _, _ -> fail path ("duplicate JSON member " + name)
            | _, Some name, _ -> fail path ("unknown JSON member " + name)
            | _, _, Some name -> fail path ("missing JSON member " + name)
            | _ -> Ok(Map.ofList properties)

    let private asText path (element: JsonElement) =
        if element.ValueKind = JsonValueKind.String then
            match element.GetString() with
            | null -> fail path "expected nonblank string"
            | text when String.IsNullOrWhiteSpace text -> fail path "expected nonblank string"
            | text -> Ok text
        else fail path "expected nonblank string"

    let private textField path name (properties: Map<string, JsonElement>) =
        match Map.tryFind name properties with
        | Some value -> asText (path + "." + name) value
        | None -> fail path ("missing " + name)

    let private elements path (properties: Map<string, JsonElement>) name =
        match Map.tryFind name properties with
        | Some value when value.ValueKind = JsonValueKind.Array -> Ok [ for item in value.EnumerateArray() -> item ]
        | Some _ -> fail (path + "." + name) "expected array"
        | None -> fail path ("missing " + name)

    let private strings path properties name =
        elements path properties name
        |> Result.bind (traverse (fun index item -> asText (sprintf "%s.%s[%d]" path name index) item))

    let private decodeSource path element =
        fields path [ "key"; "originalId"; "document"; "location"; "revision"; "contentDigest" ] [] element
        |> Result.bind (fun p ->
            let text name = textField path name p
            match text "key", text "originalId", text "document", text "location", text "revision", text "contentDigest" with
            | Ok key, Ok originalId, Ok document, Ok location, Ok revision, Ok contentDigest ->
                Ok { Key = key; OriginalId = originalId; Document = document; Location = location
                     Revision = revision; ContentDigest = contentDigest }
            | Error e, _, _, _, _, _ | _, Error e, _, _, _, _ | _, _, Error e, _, _, _
            | _, _, _, Error e, _, _ | _, _, _, _, Error e, _ | _, _, _, _, _, Error e -> Error e)

    let private decodeDisposition path element =
        fields path [ "kind" ] [ "reason"; "targetKey" ] element
        |> Result.bind (fun p ->
            let exactly expected =
                if Set.ofSeq (Map.keys p) = Set.ofList expected then Ok() else Error()
            textField path "kind" p
            |> Result.bind (fun kind ->
                let reason () = textField path "reason" p
                let incorrect () = fail path ("incorrect fields for disposition " + kind)
                match kind with
                | "modeled" ->
                    match exactly [ "kind" ] with
                    | Ok() -> Ok Modeled
                    | Error() -> incorrect ()
                | "deferred" | "unresolved" | "rejected" ->
                    match exactly [ "kind"; "reason" ] with
                    | Error() -> incorrect ()
                    | Ok() ->
                        reason ()
                        |> Result.map (fun why ->
                            match kind with
                            | "deferred" -> Deferred why
                            | "unresolved" -> Unresolved why
                            | _ -> Rejected why)
                | "superseded" ->
                    match exactly [ "kind"; "reason"; "targetKey" ] with
                    | Error() -> incorrect ()
                    | Ok() ->
                        reason ()
                        |> Result.bind (fun why -> textField path "targetKey" p |> Result.map (fun target -> Superseded(target, why)))
                | _ -> fail path ("unknown disposition: " + kind)))

    let private decodeRequirement path element =
        fields path [ "source"; "disposition"; "nodeIds" ] [] element
        |> Result.bind (fun p ->
            decodeSource (path + ".source") p["source"]
            |> Result.bind (fun source ->
                decodeDisposition (path + ".disposition") p["disposition"]
                |> Result.bind (fun disposition ->
                    strings path p "nodeIds"
                    |> Result.map (fun ids -> { Source = source; Disposition = disposition; NodeIds = ids }))))

    let private decodeNode path element =
        fields path [ "id"; "kind"; "requirementKeys"; "dependsOn" ] [ "justification" ] element
        |> Result.bind (fun p ->
            textField path "id" p
            |> Result.bind (fun id ->
                textField path "kind" p
                |> Result.bind (fun kindText ->
                    match kinds |> List.tryFind (fst >> (=) kindText) with
                    | None -> fail (path + ".kind") ("unknown node kind: " + kindText)
                    | Some(_, kind) ->
                        strings path p "requirementKeys"
                        |> Result.bind (fun keys ->
                            strings path p "dependsOn"
                            |> Result.bind (fun dependencies ->
                                let justification =
                                    match Map.tryFind "justification" p with
                                    | None -> Ok None
                                    | Some raw -> asText (path + ".justification") raw |> Result.map Some
                                justification
                                |> Result.map (fun why ->
                                    { Id = id; Kind = kind; RequirementKeys = keys
                                      DependsOn = dependencies; Justification = why }))))))

    let private decodeBlueprint (root: JsonElement) =
        fields "$" [ "schemaVersion"; "sourceManifestDigest"; "requirements"; "nodes" ] [] root
        |> Result.bind (fun p ->
            textField "$" "schemaVersion" p
            |> Result.bind (fun version ->
                if version <> "ecir/1" then fail "$.schemaVersion" ("unsupported " + version)
                else
                    textField "$" "sourceManifestDigest" p
                    |> Result.bind (fun digest ->
                        elements "$" p "requirements"
                        |> Result.bind (traverse (fun index item -> decodeRequirement (sprintf "$.requirements[%d]" index) item))
                        |> Result.bind (fun requirements ->
                            elements "$" p "nodes"
                            |> Result.bind (traverse (fun index item -> decodeNode (sprintf "$.nodes[%d]" index) item))
                            |> Result.map (fun nodes ->
                                { SchemaVersion = version; SourceManifestDigest = digest
                                  Requirements = requirements; Nodes = nodes })))))

    // -- Manifest-independent semantic rules (mirrors Ordo ConstructionIr.validate)

    let private duplicates (values: string list) =
        values |> List.countBy id |> List.filter (fun (_, count) -> count > 1) |> List.map fst

    let private firstBy (key: 'a -> string) (items: 'a list) =
        items |> List.fold (fun index item -> if Map.containsKey (key item) index then index else Map.add (key item) item index) Map.empty

    let private violations (blueprint: Blueprint) : (string * string option) list =
        let requirementIndex = firstBy (fun (r: Requirement) -> r.Source.Key) blueprint.Requirements
        let nodeIndex = firstBy (fun (n: Node) -> n.Id) blueprint.Nodes
        let issue code subject = code, subject

        let intake =
            [ if List.isEmpty blueprint.Requirements then issue "ORDO.CONTRACT.EMPTY_INTAKE" None
              for key in duplicates (blueprint.Requirements |> List.map _.Source.Key) do
                  issue "ORDO.CONTRACT.DUPLICATE_BLUEPRINT_REQUIREMENT" (Some key)
              for id in duplicates (blueprint.Nodes |> List.map _.Id) do
                  issue "ORDO.CONTRACT.DUPLICATE_NODE" (Some id) ]

        let supersessionLoops start =
            let rec follow seen key =
                match Map.tryFind key requirementIndex |> Option.map _.Disposition with
                | Some(Superseded(target, _)) when Set.contains target seen -> true
                | Some(Superseded(target, _)) -> follow (Set.add target seen) target
                | _ -> false
            follow (Set.singleton start) start

        let requirementRules =
            [ for requirement in blueprint.Requirements do
                let key = requirement.Source.Key
                if List.isEmpty requirement.NodeIds then issue "ORDO.CONTRACT.MISSING_REQUIREMENT_MAPPING" (Some key)
                for id in duplicates requirement.NodeIds do
                    issue "ORDO.CONTRACT.DUPLICATE_LINK" (Some(key + "->" + id))
                for nodeId in requirement.NodeIds do
                    match Map.tryFind nodeId nodeIndex with
                    | None -> issue "ORDO.CONTRACT.MISSING_NODE" (Some(key + "->" + nodeId))
                    | Some node when not (List.contains key node.RequirementKeys) ->
                        issue "ORDO.CONTRACT.NON_RECIPROCAL_LINK" (Some(key + "<->" + nodeId))
                    | Some _ -> ()
                let hasKind kind =
                    requirement.NodeIds
                    |> List.exists (fun id ->
                        match Map.tryFind id nodeIndex with
                        | Some node -> node.Kind = kind && List.contains key node.RequirementKeys
                        | None -> false)
                let missingNode kind =
                    [ if not (hasKind kind) then
                          issue "ORDO.CONTRACT.MISSING_DISPOSITION_NODE" (Some(key + "->" + kindCase kind)) ]
                match requirement.Disposition with
                | Modeled ->
                    if not (hasKind VerificationObligation) then issue "ORDO.CONTRACT.MISSING_VERIFICATION" (Some key)
                    if not (hasKind Cohort) then issue "ORDO.CONTRACT.MISSING_OWNER_COHORT" (Some key)
                | Deferred _ -> yield! missingNode Deferral
                | Unresolved _ -> yield! missingNode Conflict
                | Rejected _ -> yield! missingNode EngineeringRationale
                | Superseded(target, _) ->
                    if key = target || not (Map.containsKey target requirementIndex) || supersessionLoops key then
                        issue "ORDO.CONTRACT.INVALID_SUPERSESSION" (Some(key + "->" + target)) ]

        let nodeRules =
            [ for node in blueprint.Nodes do
                for key in duplicates node.RequirementKeys do
                    issue "ORDO.CONTRACT.DUPLICATE_LINK" (Some(node.Id + "->" + key))
                for id in duplicates node.DependsOn do
                    issue "ORDO.CONTRACT.DUPLICATE_LINK" (Some(node.Id + "->" + id))
                if List.isEmpty node.RequirementKeys
                   && (node.Kind <> EngineeringRationale || Option.isNone node.Justification) then
                    issue "ORDO.CONTRACT.UNJUSTIFIED_NODE" (Some node.Id)
                for key in node.RequirementKeys do
                    match Map.tryFind key requirementIndex with
                    | None -> issue "ORDO.CONTRACT.MISSING_REQUIREMENT_REFERENCE" (Some(node.Id + "->" + key))
                    | Some requirement when not (List.contains node.Id requirement.NodeIds) ->
                        issue "ORDO.CONTRACT.NON_RECIPROCAL_LINK" (Some(key + "<->" + node.Id))
                    | Some _ -> ()
                for dependency in node.DependsOn do
                    if dependency = node.Id then issue "ORDO.CONTRACT.SELF_DEPENDENCY" (Some node.Id)
                    elif not (Map.containsKey dependency nodeIndex) then
                        issue "ORDO.CONTRACT.MISSING_DEPENDENCY" (Some(node.Id + "->" + dependency)) ]

        // Peel nodes whose known dependencies are all peeled; what remains is
        // every node from which a cycle is reachable (Ordo's exact rule).
        let cycles =
            let known (node: Node) = node.DependsOn |> List.filter (fun id -> Map.containsKey id nodeIndex)
            let rec unpeelable remaining =
                let peelable =
                    remaining |> Set.filter (fun id -> known nodeIndex[id] |> List.forall (fun dep -> not (Set.contains dep remaining)))
                if Set.isEmpty peelable then remaining else unpeelable (Set.difference remaining peelable)
            nodeIndex |> Map.keys |> Set.ofSeq |> unpeelable |> Set.toList
            |> List.map (fun id -> issue "ORDO.CONTRACT.DEPENDENCY_CYCLE" (Some id))

        intake @ requirementRules @ nodeRules @ cycles |> List.distinct |> List.sort

    // -- Canonical form and digest (mirrors Ordo Json.renderCanonical) --------

    type private Canonical =
        | Text of string
        | Items of Canonical list
        | Record of (string * Canonical) list

    let private escapeInto (builder: StringBuilder) (value: string) =
        builder.Append '"' |> ignore
        for ch in value do
            match ch with
            | '"' -> builder.Append "\\\"" |> ignore
            | '\\' -> builder.Append "\\\\" |> ignore
            | '\b' -> builder.Append "\\b" |> ignore
            | '\f' -> builder.Append "\\f" |> ignore
            | '\n' -> builder.Append "\\n" |> ignore
            | '\r' -> builder.Append "\\r" |> ignore
            | '\t' -> builder.Append "\\t" |> ignore
            | c when c < ' ' -> builder.Append("\\u").Append((int c).ToString("x4")) |> ignore
            | c -> builder.Append c |> ignore
        builder.Append '"' |> ignore

    let private render (value: Canonical) =
        let builder = StringBuilder()
        let pad depth = String(' ', depth * 2)
        let rec go value depth =
            match value with
            | Text text -> escapeInto builder text
            | Items [] -> builder.Append "[]" |> ignore
            | Record [] -> builder.Append "{}" |> ignore
            | Items items ->
                builder.Append "[\n" |> ignore
                items |> List.iteri (fun index item ->
                    if index > 0 then builder.Append ",\n" |> ignore
                    builder.Append(pad (depth + 1)) |> ignore
                    go item (depth + 1))
                builder.Append('\n').Append(pad depth).Append ']' |> ignore
            | Record members ->
                builder.Append "{\n" |> ignore
                members
                |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
                |> List.iteri (fun index (name, item) ->
                    if index > 0 then builder.Append ",\n" |> ignore
                    builder.Append(pad (depth + 1)) |> ignore
                    escapeInto builder name
                    builder.Append ": " |> ignore
                    go item (depth + 1))
                builder.Append('\n').Append(pad depth).Append '}' |> ignore
        go value 0
        builder.ToString()

    let private texts values = Items(values |> List.map Text)

    let private optionalMember name value =
        value |> Option.map (fun text -> [ name, Text text ]) |> Option.defaultValue []

    /// Ordo's canonical rendering: requirements by source key, nodes by id,
    /// identity reference arrays sorted, object members sorted by ordinal key.
    let private canonicalText (blueprint: Blueprint) =
        let source (s: Source) =
            Record [ "key", Text s.Key; "originalId", Text s.OriginalId; "document", Text s.Document
                     "location", Text s.Location; "revision", Text s.Revision; "contentDigest", Text s.ContentDigest ]
        let disposition state =
            match state with
            | Modeled -> Record [ "kind", Text "modeled" ]
            | Deferred why -> Record [ "kind", Text "deferred"; "reason", Text why ]
            | Unresolved why -> Record [ "kind", Text "unresolved"; "reason", Text why ]
            | Rejected why -> Record [ "kind", Text "rejected"; "reason", Text why ]
            | Superseded(target, why) -> Record [ "kind", Text "superseded"; "reason", Text why; "targetKey", Text target ]
        Record [
            "schemaVersion", Text blueprint.SchemaVersion
            "sourceManifestDigest", Text blueprint.SourceManifestDigest
            "requirements",
            Items [ for r in blueprint.Requirements |> List.sortBy _.Source.Key ->
                        Record [ "source", source r.Source
                                 "disposition", disposition r.Disposition
                                 "nodeIds", texts (List.sort r.NodeIds) ] ]
            "nodes",
            Items [ for n in blueprint.Nodes |> List.sortBy _.Id ->
                        Record([ "id", Text n.Id
                                 "kind", Text(kindWire n.Kind)
                                 "requirementKeys", texts (List.sort n.RequirementKeys)
                                 "dependsOn", texts (List.sort n.DependsOn) ]
                               @ optionalMember "justification" n.Justification) ] ]
        |> render

    let private sha256 (text: string) =
        "sha256:" + (text |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString).ToLowerInvariant()

    // -- Public surface -------------------------------------------------------

    let private diagnostic code subject message =
        { Code = code; Subject = subject; Message = message }

    let private sorted (diagnostics: OrdoDiagnostic list) =
        diagnostics |> List.sortBy (fun item -> item.Code, item.Subject)

    /// Ordo decodes every string and member name before reading any field, so
    /// undecodable text anywhere refuses the whole document; do the same.
    let rec private decodeAllText (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.String -> element.GetString() |> ignore
        | JsonValueKind.Array -> for item in element.EnumerateArray() do decodeAllText item
        | JsonValueKind.Object ->
            for property in element.EnumerateObject() do
                property.Name |> ignore
                decodeAllText property.Value
        | _ -> ()

    let private parse (json: string) =
        if String.IsNullOrEmpty json then Error "invalid JSON: input was null or empty"
        else
            try
                use document = JsonDocument.Parse(json, JsonDocumentOptions(AllowTrailingCommas = false))
                decodeAllText document.RootElement
                Ok(document.RootElement.Clone())
            with
            | :? JsonException as ex -> Error("invalid JSON: " + ex.Message)
            | :? InvalidOperationException as ex -> Error("invalid JSON: " + ex.Message)

    let private requestedVersion (root: JsonElement) =
        if root.ValueKind <> JsonValueKind.Object then None
        else
            match [ for property in root.EnumerateObject() do if property.Name = "schemaVersion" then property.Value ] with
            | [ value ] when value.ValueKind = JsonValueKind.String -> Option.ofObj (value.GetString())
            | _ -> None

    /// The canonical `ecir/1` text whose UTF-8 SHA-256 is the contract digest.
    /// Exposed so operators can diff what was actually hashed.
    let canonicalForm (contractJson: string) : Result<string, OrdoDiagnostic list> =
        match parse contractJson |> Result.bind (fun root -> decodeBlueprint root) with
        | Ok blueprint -> Ok(canonicalText blueprint)
        | Error message -> Error [ diagnostic InvalidJsonCode None message ]

    /// I3: strict, deterministic, side-effect-free admission of one published
    /// Ordo contract. `supportedVersions` narrows `implementedVersions`.
    let admit (supportedVersions: string list) (contractJson: string) : Result<AdmittedOrdoContract, OrdoDiagnostic list> =
        let accepted = implementedVersions |> List.filter (fun version -> List.contains version supportedVersions)
        let unsupported version =
            Error [ diagnostic UnsupportedSchemaCode (Some version) ("supported schema versions: " + String.concat ", " accepted) ]
        try
            match parse contractJson with
            | Error message -> Error [ diagnostic InvalidJsonCode None message ]
            | Ok root ->
                match requestedVersion root with
                | Some version when not (List.contains version accepted) -> unsupported version
                | _ ->
                    match decodeBlueprint root with
                    | Error message -> Error [ diagnostic InvalidJsonCode None message ]
                    | Ok blueprint when not (List.contains blueprint.SchemaVersion accepted) -> unsupported blueprint.SchemaVersion
                    | Ok blueprint ->
                        match violations blueprint with
                        | [] ->
                            let idsOf kind =
                                blueprint.Nodes |> List.filter (fun node -> node.Kind = kind) |> List.map _.Id |> List.distinct |> List.sort
                            Ok { Version = blueprint.SchemaVersion
                                 Digest = sha256 (canonicalText blueprint)
                                 RequirementIds = blueprint.Requirements |> List.map _.Source.Key |> List.distinct |> List.sort
                                 DecisionIds = idsOf Decision
                                 ConflictResolutionIds = idsOf Conflict }
                        | found ->
                            found
                            |> List.map (fun (code, subject) ->
                                diagnostic code subject (sprintf "%s%s" code (subject |> Option.map ((+) ": ") |> Option.defaultValue "")))
                            |> sorted
                            |> Error
        with :? InvalidOperationException as ex ->
            // e.g. an escaped lone surrogate that cannot be decoded to text.
            Error [ diagnostic InvalidJsonCode None ("invalid JSON: " + ex.Message) ]

    let private isSha256 (value: string) =
        not (isNull value)
        && value.Length = 71
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.Substring 7 |> Seq.forall (fun ch -> (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))

    /// Stale-reference detection: a holder of `expectedDigest` learns that the
    /// contract it now reads has a different canonical digest.
    let checkReference (expectedDigest: string) (admitted: AdmittedOrdoContract) : Result<AdmittedOrdoContract, OrdoDiagnostic list> =
        if not (isSha256 expectedDigest) then
            Error [ diagnostic InvalidReferenceDigestCode (Some expectedDigest)
                        "the expected contract digest must be a lowercase 64-hex SHA-256 value" ]
        elif not (String.Equals(expectedDigest, admitted.Digest, StringComparison.Ordinal)) then
            Error [ diagnostic StaleReferenceCode (Some(expectedDigest + "->" + admitted.Digest))
                        "the Ordo contract changed since this reference was pinned" ]
        else Ok admitted

    /// Admission followed, when a pin is held, by stale-reference detection.
    let admitPinned (supportedVersions: string list) (expectedDigest: string option) (contractJson: string) =
        match admit supportedVersions contractJson, expectedDigest with
        | Ok admitted, Some expected -> checkReference expected admitted
        | outcome, _ -> outcome
