namespace Praxis.Contracts.Requirements

open System
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Requirements

/// Strict decoding of `praxis.requirement-catalog/1` and
/// `praxis.requirement-implementation-map/1`, and deterministic rendering of
/// `praxis.verification-manifest/1` (REQ-RVM-001). The manifest carries no
/// timestamp and no host detail: identical catalog bytes and source commit
/// render byte-identical manifests. `contentHash` is the SHA-256 of the
/// manifest's compact JSON with `contentHash` itself removed, so any edit to
/// a compiled manifest is detectable without the catalog.
[<RequireQualifiedAccess>]
module RequirementManifestJson =
    let catalogSchema = "praxis.requirement-catalog/1"
    let mappingSchema = "praxis.requirement-implementation-map/1"
    let manifestSchema = "praxis.verification-manifest/1"
    let findingsSchema = "praxis.requirement-catalog-findings/1"
    let compilerName = "praxis requirements manifest"
    /// The manifest format version this compiler writes. Bump on any change to
    /// what is written, so two manifests with equal inputs but different
    /// compilers are never mistaken for each other.
    let compilerVersion = "1.0.0"

    type private ResultBuilder() =
        member _.Bind(value, next) = Result.bind next value
        member _.Return value = Ok value
        member _.ReturnFrom(value: Result<'T, string>) = value

    let private result = ResultBuilder()

    let private property (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value -> Ok value
        | _ -> Error $"missing required member '{name}'"

    let private str name element =
        property name element
        |> Result.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then Ok(value.GetString())
            else Error $"member '{name}' must be a string")

    let private optionalStr (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok None
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok None
        | true, value when value.ValueKind = JsonValueKind.String -> Ok(Some(value.GetString()))
        | _ -> Error $"member '{name}' must be a string or null"

    let private boolean (name: string) (fallback: bool) (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok fallback
        | true, value when value.ValueKind = JsonValueKind.True -> Ok true
        | true, value when value.ValueKind = JsonValueKind.False -> Ok false
        | _ -> Error $"member '{name}' must be true or false"

    let private optionalInt (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok None
        | true, value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt32() with
            | true, number -> Ok(Some number)
            | _ -> Error $"member '{name}' must be an integer"
        | _ -> Error $"member '{name}' must be an integer"

    let private sequence (decode: JsonElement -> Result<'T, string>) (items: JsonElement seq) =
        items
        |> Seq.mapi (fun index item -> index, item)
        |> Seq.fold
            (fun acc (index, item) ->
                acc
                |> Result.bind (fun decoded ->
                    decode item
                    |> Result.map (fun value -> value :: decoded)
                    |> Result.mapError (fun reason -> $"[{index}]: {reason}")))
            (Ok [])
        |> Result.map List.rev

    let private optionalArray (name: string) decode (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok []
        | true, value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray() |> sequence decode |> Result.mapError (fun reason -> $"member '{name}'{reason}")
        | _ -> Error $"member '{name}' must be an array"

    let private array name decode (element: JsonElement) =
        property name element |> Result.bind (fun _ -> optionalArray name decode element)

    let private stringItem (element: JsonElement) =
        if element.ValueKind = JsonValueKind.String then Ok(element.GetString())
        else Error "expected a string"

    let private schemaIs (expected: string) (root: JsonElement) =
        str "schema" root
        |> Result.bind (fun schema ->
            if schema = expected then Ok()
            else Error $"document schema is '{schema}', expected '{expected}'")

    let private parse (text: string) (decode: JsonElement -> Result<'T, string>) =
        try
            use document = JsonDocument.Parse(text, JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow))

            if document.RootElement.ValueKind <> JsonValueKind.Object then Error "document root must be an object"
            else decode document.RootElement
        with :? JsonException as error ->
            Error $"invalid JSON: {error.Message}"

    let private rules (element: JsonElement) : Result<FieldRules, string> =
        match element.TryGetProperty "validation" with
        | false, _ -> Ok { MinLength = None; MaxLength = None; Pattern = None; Options = [] }
        | true, value when value.ValueKind = JsonValueKind.Object ->
            result {
                let! minimum = optionalInt "minLength" value
                let! maximum = optionalInt "maxLength" value
                let! pattern = optionalStr "pattern" value
                let! options = optionalArray "options" stringItem value

                return
                    { MinLength = minimum
                      MaxLength = maximum
                      Pattern = pattern
                      Options = options }
            }
        | _ -> Error "member 'validation' must be an object"

    let private source (element: JsonElement) : Result<SourceReference, string> =
        result {
            let! document = str "document" element
            let! requirement = str "requirement" element
            return { Document = document; Requirement = requirement }
        }

    let private fact (element: JsonElement) =
        result {
            let! name = str "fact" element
            let! value = boolean "equals" true element
            return name, value
        }

    let private requirement (element: JsonElement) : Result<CatalogRequirement, string> =
        result {
            let! id = str "id" element
            let! kind = str "type" element
            let! screen = str "screen" element
            let! name = str "name" element
            let! comp = optionalStr "component" element
            let! required = boolean "required" false element
            let! visible = boolean "visible" true element
            let! editable = boolean "editable" false element
            let! validation = rules element
            let! verification = optionalArray "verification" stringItem element
            let! sources = optionalArray "sources" source element
            let! dependsOn = optionalArray "dependsOn" stringItem element
            let! appliesWhen = optionalArray "appliesWhen" fact element
            let! instances = optionalStr "instances" element

            return
                { Id = id
                  Kind = kind
                  Screen = screen
                  Name = name
                  Component = comp
                  Required = required
                  Visible = visible
                  Editable = editable
                  Rules = validation
                  Verification = verification
                  Sources = sources
                  DependsOn = dependsOn
                  AppliesWhen = appliesWhen
                  Instances = instances }
        }
        |> Result.mapError (fun reason ->
            match element.TryGetProperty "id" with
            | true, id when id.ValueKind = JsonValueKind.String -> $" ({id.GetString()}): {reason}"
            | _ -> $": {reason}")

    let private retirement (element: JsonElement) : Result<Retirement, string> =
        result {
            let! id = str "id" element
            let! reason = str "reason" element
            let! authority = str "authority" element
            let! replacedBy = optionalStr "replacedBy" element

            return
                { Id = id
                  Reason = reason
                  Authority = authority
                  ReplacedBy = replacedBy }
        }

    let private screen (element: JsonElement) : Result<Screen, string> =
        result {
            let! id = str "id" element
            let! route = optionalStr "route" element
            return { Id = id; Route = route }
        }

    let decodeCatalog (text: string) : Result<Catalog, string> =
        parse text (fun root ->
            result {
                do! schemaIs catalogSchema root
                let! catalogId = str "catalogId" root
                let! idPattern = str "idPattern" root
                let! status = str "status" root
                let! screens = array "screens" screen root
                let! facts = optionalArray "facts" stringItem root
                let! requirements = array "requirements" requirement root
                let! retired = optionalArray "retired" retirement root

                return
                    { CatalogId = catalogId
                      IdPattern = idPattern
                      Status = status
                      Screens = screens
                      Facts = facts
                      Requirements = requirements
                      Retired = retired }
            })

    let decodeMappings (text: string) : Result<ImplementationMapping list, string> =
        parse text (fun root ->
            result {
                do! schemaIs mappingSchema root

                return!
                    array
                        "mappings"
                        (fun element ->
                            result {
                                let! id = str "requirement" element
                                let! location = str "location" element
                                return { RequirementId = id; Location = location }
                            })
                        root
            })

    /// What a check needs from a compiled manifest it did not write.
    type ManifestSummary =
        { CatalogId: string
          SourceCommit: string
          CatalogSha256: string
          ContentHash: string
          /// The hash recomputed from the document's own content.
          RecomputedHash: string
          RequirementIds: string list }

    let sha256Hex (bytes: byte array) =
        bytes |> SHA256.HashData |> Convert.ToHexString |> fun hex -> hex.ToLowerInvariant()

    let private compactOptions = JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private hashOf (node: JsonObject) =
        let copy = node.DeepClone().AsObject()
        copy.Remove "contentHash" |> ignore
        copy.ToJsonString compactOptions |> Encoding.UTF8.GetBytes |> sha256Hex

    let decodeManifestSummary (text: string) : Result<ManifestSummary, string> =
        let summary =
            parse text (fun root ->
                result {
                    do! schemaIs manifestSchema root
                    let! catalogId = str "catalogId" root
                    let! source = property "source" root
                    let! commit = str "commit" source
                    let! catalogSha = str "catalogSha256" source
                    let! contentHash = str "contentHash" root
                    let! ids = array "requirements" (str "id") root

                    return
                        { CatalogId = catalogId
                          SourceCommit = commit
                          CatalogSha256 = catalogSha
                          ContentHash = contentHash
                          RecomputedHash = ""
                          RequirementIds = ids }
                })

        summary
        |> Result.bind (fun summary ->
            try
                match JsonNode.Parse text with
                | :? JsonObject as node -> Ok { summary with RecomputedHash = hashOf node }
                | _ -> Error "document root must be an object"
            with :? JsonException as error ->
                Error $"invalid JSON: {error.Message}")

    let private strings (values: string list) =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value))
        array

    let private optional (value: string option) : JsonNode =
        match value with
        | Some text -> JsonValue.Create text
        | None -> null

    let private optionalNumber (value: int option) : JsonNode =
        match value with
        | Some number -> JsonValue.Create number
        | None -> null

    let private objectOf (members: (string * JsonNode) list) =
        let node = JsonObject()
        members |> List.iter (fun (name, value) -> node.Add(name, value))
        node

    let private arrayOf (nodes: JsonNode list) =
        let array = JsonArray()
        nodes |> List.iter array.Add
        array

    let private applicabilityNode (applicability: Applicability) : JsonNode =
        match applicability with
        | Always -> objectOf [ "rule", JsonValue.Create "always" ]
        | When facts ->
            objectOf
                [ "rule", JsonValue.Create "when"
                  "facts", arrayOf (facts |> List.map (fun (name, value) -> objectOf [ "fact", JsonValue.Create name; "equals", JsonValue.Create value ] :> JsonNode)) ]

    let private instancingNode (instancing: Instancing) : JsonNode =
        match instancing with
        | Single -> objectOf [ "scope", JsonValue.Create "single" ]
        | Repeated key -> objectOf [ "scope", JsonValue.Create "repeated"; "key", JsonValue.Create key ]

    let private requirementNode (requirement: ManifestRequirement) : JsonNode =
        objectOf
            [ "id", JsonValue.Create requirement.Id
              "type", JsonValue.Create(RequirementKind.text requirement.Kind)
              "screen", JsonValue.Create requirement.Screen
              "name", JsonValue.Create requirement.Name
              "component", optional requirement.Component
              "required", JsonValue.Create requirement.Required
              "visible", JsonValue.Create requirement.Visible
              "editable", JsonValue.Create requirement.Editable
              "validation",
              objectOf
                  [ "minLength", optionalNumber requirement.Rules.MinLength
                    "maxLength", optionalNumber requirement.Rules.MaxLength
                    "pattern", optional requirement.Rules.Pattern
                    "options", strings requirement.Rules.Options ]
              "sources",
              arrayOf (requirement.Sources |> List.map (fun s -> objectOf [ "document", JsonValue.Create s.Document; "requirement", JsonValue.Create s.Requirement ] :> JsonNode))
              "dependsOn", strings requirement.DependsOn
              "applicability", applicabilityNode requirement.Applicability
              "instancing", instancingNode requirement.Instancing
              "levels", strings (requirement.Levels |> List.map VerificationLevel.text)
              "obligations",
              arrayOf (
                  requirement.Obligations
                  |> List.map (fun o ->
                      objectOf
                          [ "id", JsonValue.Create o.Id
                            "level", JsonValue.Create(VerificationLevel.text o.Level)
                            "check", JsonValue.Create(VerificationCheck.text o.Check)
                            "mandatory", JsonValue.Create o.Mandatory ]
                      :> JsonNode)
              )
              "mappings", strings requirement.Mappings ]

    let private countsNode (manifest: VerificationManifest) =
        let obligations = manifest.Requirements |> List.collect _.Obligations

        objectOf
            [ "requirements", JsonValue.Create manifest.Requirements.Length
              "requiredRequirements", JsonValue.Create(manifest.Requirements |> List.filter _.Required |> List.length)
              "obligations", JsonValue.Create obligations.Length
              "mandatoryObligations", JsonValue.Create(obligations |> List.filter _.Mandatory |> List.length)
              "byType",
              objectOf (
                  RequirementKind.all
                  |> List.map (fun kind -> RequirementKind.text kind, JsonValue.Create(manifest.Requirements |> List.filter (fun r -> r.Kind = kind) |> List.length) :> JsonNode)
              )
              "byLevel",
              objectOf (
                  VerificationLevel.all
                  |> List.map (fun level -> VerificationLevel.text level, JsonValue.Create(obligations |> List.filter (fun o -> o.Level = level) |> List.length) :> JsonNode)
              )
              "retired", JsonValue.Create manifest.Retired.Length ]

    let private indented =
        JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    /// The manifest as canonical, indented JSON with a trailing newline.
    let renderManifest (manifest: VerificationManifest) : string =
        let body =
            objectOf
                [ "schema", JsonValue.Create manifestSchema
                  "compiler", objectOf [ "name", JsonValue.Create compilerName; "version", JsonValue.Create compilerVersion ]
                  "catalogId", JsonValue.Create manifest.CatalogId
                  "catalogStatus", JsonValue.Create manifest.CatalogStatus
                  "source", objectOf [ "commit", JsonValue.Create manifest.SourceCommit; "catalogSha256", JsonValue.Create manifest.CatalogSha256 ]
                  "screens",
                  arrayOf (manifest.Screens |> List.map (fun s -> objectOf [ "id", JsonValue.Create s.Id; "route", optional s.Route ] :> JsonNode))
                  "facts", strings manifest.Facts
                  "counts", countsNode manifest
                  "requirements", arrayOf (manifest.Requirements |> List.map requirementNode)
                  "retired",
                  arrayOf (
                      manifest.Retired
                      |> List.map (fun r ->
                          objectOf
                              [ "id", JsonValue.Create r.Id
                                "reason", JsonValue.Create r.Reason
                                "replacedBy", optional r.ReplacedBy
                                "authority", JsonValue.Create r.Authority ]
                          :> JsonNode)
                  ) ]

        body.Add("contentHash", JsonValue.Create(hashOf body))
        body.ToJsonString(indented).Replace("\r\n", "\n") + "\n"

    let severityText severity =
        match severity with
        | CatalogError -> "error"
        | CatalogWarning -> "warning"

    let renderFindings (catalogId: string) (findings: CatalogFinding list) : string =
        let body =
            objectOf
                [ "schema", JsonValue.Create findingsSchema
                  "catalogId", JsonValue.Create catalogId
                  "verdict", JsonValue.Create(if RequirementCatalog.hasErrors findings then "invalid" else "valid")
                  "errors", JsonValue.Create(findings |> List.filter (fun f -> f.Severity = CatalogError) |> List.length)
                  "warnings", JsonValue.Create(findings |> List.filter (fun f -> f.Severity = CatalogWarning) |> List.length)
                  "findings",
                  arrayOf (
                      findings
                      |> List.map (fun f ->
                          objectOf
                              [ "code", JsonValue.Create f.Code
                                "severity", JsonValue.Create(severityText f.Severity)
                                "requirement", optional f.Requirement
                                "message", JsonValue.Create f.Message ]
                          :> JsonNode)
                  ) ]

        body.ToJsonString(indented).Replace("\r\n", "\n") + "\n"

    let renderFindingsText (findings: CatalogFinding list) =
        findings
        |> List.map (fun f ->
            let subject = f.Requirement |> Option.map (fun id -> $" {id}") |> Option.defaultValue ""
            $"{(severityText f.Severity).ToUpperInvariant()} {f.Code}{subject}: {f.Message}")
        |> String.concat "\n"
