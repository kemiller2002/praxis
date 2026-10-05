namespace Praxis.Contracts.Work

open System
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Work

/// Strict decoders for the external quality-evidence contracts Praxis
/// consumes (PRX-QUAL-023), the `workProtocol.qualityEvidence` policy, and
/// the persisted `praxis.completion-readiness/1` record. Decoding is pure:
/// text in, typed value out. A document naming another contract or version
/// is `Unsupported`; anything unreadable or contract-violating is
/// `Malformed`. Neither is ever a pass.
[<RequireQualifiedAccess>]
module QualityEvidenceJson =
    let dokimosContract = "dokimos.ratchet"
    let dokimosSchemaVersion = "1.0.0"
    let ordoSchema = "ordo.boundary-amplification/1"
    let readinessSchema = "praxis.completion-readiness/1"

    type private Decoder<'T> = JsonElement -> Result<'T, string>

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

    let private int name element =
        property name element
        |> Result.bind (fun value ->
            match value.ValueKind with
            | JsonValueKind.Number ->
                match value.TryGetInt32() with
                | true, number -> Ok number
                | _ -> Error $"member '{name}' must be an integer"
            | _ -> Error $"member '{name}' must be an integer")

    let private obj name element =
        property name element
        |> Result.bind (fun value ->
            if value.ValueKind = JsonValueKind.Object then Ok value
            else Error $"member '{name}' must be an object")

    let private array name (item: Decoder<'T>) element =
        property name element
        |> Result.bind (fun value ->
            if value.ValueKind <> JsonValueKind.Array then
                Error $"member '{name}' must be an array"
            else
                value.EnumerateArray()
                |> Seq.fold
                    (fun acc entry -> acc |> Result.bind (fun items -> item entry |> Result.map (fun decoded -> decoded :: items)))
                    (Ok [])
                |> Result.map List.rev
                |> Result.mapError (fun reason -> $"member '{name}': {reason}"))

    let private stringItem: Decoder<string> =
        fun element ->
            if element.ValueKind = JsonValueKind.String then Ok(element.GetString())
            else Error "expected a string"

    let private idItem: Decoder<string> = str "Id"

    let private enumOf name (cases: (string * 'T) list) element =
        str name element
        |> Result.bind (fun code ->
            match cases |> List.tryFind (fun (candidate, _) -> candidate = code) with
            | Some(_, value) -> Ok value
            | None -> Error $"member '{name}' has unknown value '{code}'")

    /// Small result builder so decoders read as data, not nested matches.
    type private ResultBuilder() =
        member _.Bind(value, next) = Result.bind next value
        member _.Return value = Ok value

    let private result = ResultBuilder()

    let private parse (text: string) (decode: JsonElement -> EvidenceReading<'T>) =
        try
            use document = JsonDocument.Parse text

            if document.RootElement.ValueKind <> JsonValueKind.Object then
                EvidenceReading.Malformed "the document must be a JSON object"
            else
                decode document.RootElement
        with :? JsonException as error ->
            EvidenceReading.Malformed $"not valid JSON: {error.Message}"

    let private verdicts =
        [ "pass", DokimosVerdict.Pass
          "regression", DokimosVerdict.Regression
          "invalid-exceptions", DokimosVerdict.InvalidExceptions
          "unavailable", DokimosVerdict.Unavailable ]

    let private decodeDokimosBody (root: JsonElement) =
        result {
            let! version = str "DokimosVersion" root
            let! checkedAt = str "CheckedAt" root
            let! repository = optionalStr "Repository" root
            let! verdict = enumOf "Verdict" verdicts root
            let! exitCode = int "ExitCode" root
            let! reasons = array "Reasons" stringItem root
            let! baseline = obj "Baseline" root
            let! baselinePath = str "Path" baseline
            let! baselineDigest = optionalStr "Digest" baseline
            let! exceptions = obj "Exceptions" root
            let! active = array "Active" idItem exceptions
            let! expired = array "Expired" idItem exceptions
            let! invalid = array "Invalid" idItem exceptions
            let! summary = obj "Summary" root
            let! regressions = int "Regressions" summary
            let! excepted = int "Excepted" summary
            let! improvements = int "Improvements" summary
            let! measured = int "RulesMeasured" summary
            let! unavailable = int "RulesUnavailable" summary
            let! _ = array "Rules" Ok root
            let! _ = array "Findings" Ok root

            return
                { DokimosVersion = version
                  CheckedAt = checkedAt
                  Repository = repository
                  Verdict = verdict
                  ExitCode = exitCode
                  Reasons = reasons
                  BaselinePath = baselinePath
                  BaselineDigest = baselineDigest
                  Regressions = regressions
                  Excepted = excepted
                  Improvements = improvements
                  RulesMeasured = measured
                  RulesUnavailable = unavailable
                  ActiveExceptions = active
                  ExpiredExceptions = expired
                  InvalidExceptions = invalid }
        }

    /// Decodes a `dokimos.ratchet` 1.0.0 report (`dokimos ratchet check --json`).
    let decodeDokimos (text: string) : EvidenceReading<DokimosRatchetEvidence> =
        parse text (fun root ->
            match str "Contract" root, str "SchemaVersion" root with
            | Ok contract, _ when contract <> dokimosContract ->
                EvidenceReading.Unsupported $"contract '{contract}' is not '{dokimosContract}'"
            | Ok _, Ok version when version <> dokimosSchemaVersion ->
                EvidenceReading.Unsupported $"{dokimosContract} schema version '{version}' is not supported (expected '{dokimosSchemaVersion}')"
            | Error reason, _
            | _, Error reason -> EvidenceReading.Malformed $"not a {dokimosContract} report: {reason}"
            | Ok _, Ok _ ->
                match decodeDokimosBody root with
                | Ok report -> EvidenceReading.Parsed report
                | Error reason -> EvidenceReading.Malformed reason)

    let private risks =
        [ "low", OrdoRiskLevel.Low; "elevated", OrdoRiskLevel.Elevated; "high", OrdoRiskLevel.High ]

    let private recommendations =
        [ "no-action", OrdoRecommendation.NoAction
          "consider-split-along", OrdoRecommendation.ConsiderSplitAlong
          "require-design-review", OrdoRecommendation.RequireDesignReview ]

    let private coverages =
        [ "not-applicable", OrdoExceptionCoverage.NotApplicable
          "none", OrdoExceptionCoverage.NoCoverage
          "partial", OrdoExceptionCoverage.Partial
          "full", OrdoExceptionCoverage.Full ]

    let private decodeOrdoBody (root: JsonElement) =
        result {
            let! workItem = str "workItem" root
            let! risk = obj "risk" root
            let! level = enumOf "level" risks risk
            let! score = int "score" risk
            let! riskEvidence = array "evidence" stringItem risk
            let! recommendation = obj "recommendation" root
            let! kind = enumOf "kind" recommendations recommendation
            let! splitAlong = array "splitAlong" stringItem recommendation
            let! exceptionNode = obj "exception" root
            let! coverage = enumOf "coverage" coverages exceptionNode
            let! downgradedNode = property "downgradedFrom" exceptionNode

            let! downgradedFrom =
                if downgradedNode.ValueKind = JsonValueKind.Null then Ok None
                elif downgradedNode.ValueKind = JsonValueKind.Object then enumOf "kind" recommendations downgradedNode |> Result.map Some
                else Error "member 'downgradedFrom' must be an object or null"

            let! crossings = array "unexpectedCrossings" stringItem root
            let! unclassified = array "unclassified" stringItem root
            let! _ = array "signals" Ok root

            let generatorVersion =
                match root.TryGetProperty "generator" with
                | true, generator when generator.ValueKind = JsonValueKind.Object ->
                    match optionalStr "version" generator with
                    | Ok version -> version
                    | Error _ -> None
                | _ -> None

            return
                { WorkItem = workItem
                  Risk = level
                  Score = score
                  RiskEvidence = riskEvidence
                  Recommendation = kind
                  SplitAlong = splitAlong
                  Coverage = coverage
                  DowngradedFrom = downgradedFrom
                  UnexpectedCrossings = crossings
                  Unclassified = unclassified
                  GeneratorVersion = generatorVersion }
        }

    /// Decodes an `ordo.boundary-amplification/1` assessment (`ordo boundary assess --json`).
    let decodeOrdo (text: string) : EvidenceReading<OrdoBoundaryEvidence> =
        parse text (fun root ->
            match str "schema" root with
            | Ok schema when schema <> ordoSchema -> EvidenceReading.Unsupported $"schema '{schema}' is not '{ordoSchema}'"
            | Error reason -> EvidenceReading.Malformed $"not an {ordoSchema} assessment: {reason}"
            | Ok _ ->
                match decodeOrdoBody root with
                | Ok assessment -> EvidenceReading.Parsed assessment
                | Error reason -> EvidenceReading.Malformed reason)

    let private requirementMember (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok EvidenceRequirement.Off
        | true, value when value.ValueKind = JsonValueKind.String ->
            match CompletionReadiness.parseRequirement (value.GetString()) with
            | Some requirement -> Ok requirement
            | None -> Error $"'{name}' must be one of required, optional, off (got '{value.GetString()}')"
        | _ -> Error $"'{name}' must be one of required, optional, off"

    let private stringSet (name: string) (decodeItem: string -> Result<'T, string>) (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok None
        | true, value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.fold
                (fun acc entry ->
                    acc
                    |> Result.bind (fun items ->
                        if entry.ValueKind = JsonValueKind.String then decodeItem (entry.GetString()) |> Result.map (fun item -> item :: items)
                        else Error $"'{name}' entries must be strings"))
                (Ok [])
            |> Result.map (Set.ofList >> Some)
        | _ -> Error $"'{name}' must be an array of strings"

    let private knownPolicyMembers =
        set [ "version"; "dokimos"; "dokimosBaseline"; "ordoBoundary"; "requiredFacets"; "workTypes"; "description" ]

    /// Decodes `workProtocol.qualityEvidence`. `None` is "absent" (the caller
    /// applies the legacy default); a present but invalid policy is an error,
    /// so a typo can never silently disable a gate.
    let decodePolicy (element: JsonElement) : Result<QualityEvidencePolicy, string> =
        if element.ValueKind <> JsonValueKind.Object then
            Error "workProtocol.qualityEvidence must be an object"
        else
            let unknown =
                element.EnumerateObject() |> Seq.map _.Name |> Seq.filter (knownPolicyMembers.Contains >> not) |> Seq.toList

            result {
                let! () =
                    match unknown with
                    | [] -> Ok()
                    | names ->
                        let joined = String.concat ", " names
                        Error $"unknown member(s): {joined}"

                let! () =
                    match element.TryGetProperty "version" with
                    | false, _ -> Ok()
                    | true, value when value.ValueKind = JsonValueKind.String && value.GetString() = "1.0.0" -> Ok()
                    | _ -> Error "'version' must be \"1.0.0\""

                let! dokimos = requirementMember "dokimos" element
                let! ordo = requirementMember "ordoBoundary" element
                let! baseline = optionalStr "dokimosBaseline" element

                let! facets =
                    stringSet
                        "requiredFacets"
                        (fun code ->
                            match CompletionReadiness.parseFacet code with
                            | Some facet -> Ok facet
                            | None -> Error $"unknown facet '{code}'")
                        element

                let! workTypes = stringSet "workTypes" Ok element

                return
                    { Dokimos = dokimos
                      DokimosBaseline = baseline
                      OrdoBoundary = ordo
                      RequiredFacets = facets |> Option.defaultValue Set.empty
                      WorkTypes = workTypes }
            }

    // ------------------------------------------------------------ rendering

    let private strings (values: string seq) =
        let node = JsonArray()
        values |> Seq.iter (fun value -> node.Add(JsonValue.Create value: JsonNode))
        node

    let policyNode (policy: QualityEvidencePolicy) =
        let node = JsonObject()
        node["dokimos"] <- JsonValue.Create(CompletionReadiness.requirementCode policy.Dokimos)
        policy.DokimosBaseline |> Option.iter (fun baseline -> node["dokimosBaseline"] <- JsonValue.Create baseline)
        node["ordoBoundary"] <- JsonValue.Create(CompletionReadiness.requirementCode policy.OrdoBoundary)

        node["requiredFacets"] <-
            strings (CompletionReadiness.allFacets |> List.filter policy.RequiredFacets.Contains |> List.map CompletionReadiness.facetCode)

        policy.WorkTypes |> Option.iter (fun types -> node["workTypes"] <- strings (Set.toList types))
        node

    let private judgementNode (node: JsonObject) (judgement: SourceJudgement) =
        match judgement with
        | SourceJudgement.Ignored -> node["judgement"] <- JsonValue.Create "ignored"
        | SourceJudgement.Passed evidence ->
            node["judgement"] <- JsonValue.Create "passed"
            node["evidence"] <- strings [ evidence ]
        | SourceJudgement.Failed reasons ->
            node["judgement"] <- JsonValue.Create "failed"
            node["reasons"] <- strings reasons
        | SourceJudgement.Unavailable reasons ->
            node["judgement"] <- JsonValue.Create "unavailable"
            node["reasons"] <- strings reasons

    let private sourceNode
        (evidenceType: string)
        (requirement: EvidenceRequirement)
        (observation: SourceObservation<'T>)
        (judgement: SourceJudgement)
        (parsed: JsonObject -> 'T -> unit)
        =
        let node = JsonObject()
        node["evidenceType"] <- JsonValue.Create evidenceType
        node["requirement"] <- JsonValue.Create(CompletionReadiness.requirementCode requirement)

        if requirement <> EvidenceRequirement.Off then
            match observation with
            | SourceObservation.NotSupplied -> node["observation"] <- JsonValue.Create "not-supplied"
            | SourceObservation.Ambiguous paths ->
                node["observation"] <- JsonValue.Create "ambiguous"
                node["paths"] <- strings paths
            | SourceObservation.Supplied(path, reading) ->
                node["path"] <- JsonValue.Create path

                match reading with
                | EvidenceReading.Parsed value ->
                    node["observation"] <- JsonValue.Create "parsed"
                    parsed node value
                | EvidenceReading.Unsupported reason ->
                    node["observation"] <- JsonValue.Create "unsupported"
                    node["reason"] <- JsonValue.Create reason
                | EvidenceReading.Malformed reason ->
                    node["observation"] <- JsonValue.Create "malformed"
                    node["reason"] <- JsonValue.Create reason

        judgementNode node judgement
        node

    let private dokimosDetails (node: JsonObject) (report: DokimosRatchetEvidence) =
        node["contract"] <- JsonValue.Create dokimosContract
        node["schemaVersion"] <- JsonValue.Create dokimosSchemaVersion
        node["dokimosVersion"] <- JsonValue.Create report.DokimosVersion
        node["checkedAt"] <- JsonValue.Create report.CheckedAt
        node["verdict"] <- JsonValue.Create(CompletionReadiness.verdictCode report.Verdict)
        node["exitCode"] <- JsonValue.Create report.ExitCode
        node["baselinePath"] <- JsonValue.Create report.BaselinePath
        report.BaselineDigest |> Option.iter (fun digest -> node["baselineDigest"] <- JsonValue.Create digest)
        node["regressions"] <- JsonValue.Create report.Regressions
        node["excepted"] <- JsonValue.Create report.Excepted
        node["improvements"] <- JsonValue.Create report.Improvements
        node["rulesUnavailable"] <- JsonValue.Create report.RulesUnavailable
        node["activeExceptions"] <- strings report.ActiveExceptions

    let private ordoDetails (node: JsonObject) (assessment: OrdoBoundaryEvidence) =
        node["schema"] <- JsonValue.Create ordoSchema
        node["workItem"] <- JsonValue.Create assessment.WorkItem
        node["riskLevel"] <- JsonValue.Create(CompletionReadiness.riskCode assessment.Risk)
        node["riskScore"] <- JsonValue.Create assessment.Score
        node["recommendation"] <- JsonValue.Create(CompletionReadiness.recommendationCode assessment.Recommendation)

        assessment.DowngradedFrom
        |> Option.iter (fun original -> node["downgradedFrom"] <- JsonValue.Create(CompletionReadiness.recommendationCode original))

        node["exceptionCoverage"] <- JsonValue.Create(CompletionReadiness.coverageCode assessment.Coverage)
        node["unexpectedCrossings"] <- strings assessment.UnexpectedCrossings
        assessment.GeneratorVersion |> Option.iter (fun version -> node["ordoVersion"] <- JsonValue.Create version)

    let private facetNode (facet: FacetAssessment) =
        let node = JsonObject()
        node["required"] <- JsonValue.Create facet.Required

        match facet.Status with
        | FacetStatus.Satisfied evidence ->
            node["status"] <- JsonValue.Create "satisfied"
            node["evidence"] <- strings evidence
        | FacetStatus.NotSatisfied reasons ->
            node["status"] <- JsonValue.Create "not-satisfied"
            node["reasons"] <- strings reasons
        | FacetStatus.Unavailable reasons ->
            node["status"] <- JsonValue.Create "unavailable"
            node["reasons"] <- strings reasons
        | FacetStatus.NotRequired -> node["status"] <- JsonValue.Create "not-required"

        node["blocking"] <- JsonValue.Create facet.Blocking
        node

    let private camel (code: string) =
        let parts = code.Split '-'
        parts[0] + (parts[1..] |> Array.map (fun part -> string (Char.ToUpperInvariant part[0]) + part.Substring 1) |> String.concat "")

    /// The `praxis.completion-readiness/1` record for one work item.
    let readinessNode (readiness: ItemReadiness) =
        let node = JsonObject()
        node["schema"] <- JsonValue.Create readinessSchema
        node["workItem"] <- JsonValue.Create readiness.WorkItemId
        node["decision"] <- JsonValue.Create(if CompletionReadiness.isReady readiness then "ready" else "refused")
        node["policy"] <- policyNode readiness.Policy

        let facets = JsonObject()

        for facet in readiness.Facets do
            facets[camel (CompletionReadiness.facetCode facet.Facet)] <- facetNode facet

        node["facets"] <- facets
        let sources = JsonObject()

        sources["dokimos"] <-
            sourceNode QualityEvidenceTypes.dokimosRatchet readiness.Policy.Dokimos readiness.Dokimos readiness.DokimosJudgement dokimosDetails

        sources["ordoBoundary"] <-
            sourceNode QualityEvidenceTypes.ordoBoundary readiness.Policy.OrdoBoundary readiness.Ordo readiness.OrdoJudgement ordoDetails

        node["sources"] <- sources
        node["blockingReasons"] <- strings (CompletionReadiness.blockingReasons readiness)
        node

    /// The additive member written onto a completion event and work item.
    let extensionMember = "completionReadiness"

    let extension (readiness: ItemReadiness) =
        let node = JsonObject()
        node[extensionMember] <- readinessNode readiness
        node
