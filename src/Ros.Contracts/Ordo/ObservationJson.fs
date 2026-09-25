namespace Ros.Contracts.Ordo

open System
open System.Globalization
open System.Text.Json
open Ros.Contracts
open Ros.Domain.Ordo

[<RequireQualifiedAccess>]
module ObservationJson =
    let private parseDocument (text: string) =
        try Ok(JsonDocument.Parse text)
        with error -> Error $"Malformed JSON: {error.Message}"

    let private property (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value -> Ok value
        | _ -> Error $"Missing required field '$.{name}'."

    let private stringValue (path: string) (element: JsonElement) =
        if element.ValueKind = JsonValueKind.String then
            match element.GetString() with
            | null -> Error $"{path} must not be null."
            | value -> Ok value
        else
            Error $"{path} must be a string."

    let private intValue (path: string) (element: JsonElement) =
        match element.TryGetInt32() with
        | true, value -> Ok value
        | _ -> Error $"{path} must be an integer."

    let private boolValue (path: string) (element: JsonElement) =
        if element.ValueKind = JsonValueKind.True || element.ValueKind = JsonValueKind.False then Ok(element.GetBoolean())
        else Error $"{path} must be a boolean."

    let private dateValue (path: string) (element: JsonElement) =
        stringValue path element
        |> Result.bind (fun value ->
            match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, parsed -> Ok parsed
            | _ -> Error $"{path} must be an ISO-8601 timestamp.")

    let private requiredString (name: string) (element: JsonElement) = property name element |> Result.bind (stringValue $"$.{name}")
    let private requiredInt (name: string) (element: JsonElement) = property name element |> Result.bind (intValue $"$.{name}")
    let private requiredBool (name: string) (element: JsonElement) = property name element |> Result.bind (boolValue $"$.{name}")
    let private requiredDate (name: string) (element: JsonElement) = property name element |> Result.bind (dateValue $"$.{name}")

    let private optionalString (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok None
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok None
        | true, value -> stringValue $"$.{name}" value |> Result.map Some

    let private optionalInt (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok None
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok None
        | true, value -> intValue $"$.{name}" value |> Result.map Some

    let private optionalBool (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok None
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok None
        | true, value -> boolValue $"$.{name}" value |> Result.map Some

    let private optionalDate (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok None
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok None
        | true, value -> dateValue $"$.{name}" value |> Result.map Some

    let private stringArray (name: string) (element: JsonElement) =
        property name element
        |> Result.bind (fun value ->
            if value.ValueKind <> JsonValueKind.Array then
                Error $"$.{name} must be an array."
            else
                value.EnumerateArray()
                |> Seq.map (stringValue $"$.{name}[]")
                |> Seq.fold
                    (fun state item ->
                        match state, item with
                        | Error error, _ -> Error error
                        | _, Error error -> Error error
                        | Ok values, Ok value -> Ok(value :: values))
                    (Ok [])
                |> Result.map List.rev)

    let private optionalStringArray (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok []
        | true, value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.map (stringValue $"$.{name}[]")
            |> Seq.fold
                (fun state item ->
                    match state, item with
                    | Error error, _ -> Error error
                    | _, Error error -> Error error
                    | Ok values, Ok value -> Ok(value :: values))
                (Ok [])
            |> Result.map List.rev
        | _ -> Error $"$.{name} must be an array."

    let private bind2 left right map =
        match left, right with
        | Ok a, Ok b -> Ok(map a b)
        | Error error, _ -> Error error
        | _, Error error -> Error error

    let private bind3 a b c map =
        match a, b, c with
        | Ok av, Ok bv, Ok cv -> Ok(map av bv cv)
        | Error error, _, _ -> Error error
        | _, Error error, _ -> Error error
        | _, _, Error error -> Error error

    let private parseCoverage (root: JsonElement) =
        property "coverage" root
        |> Result.bind (fun coverage ->
            if coverage.ValueKind <> JsonValueKind.Array then
                Error "$.coverage must be an array."
            else
                coverage.EnumerateArray()
                |> Seq.map (fun (item: JsonElement) ->
                    let scope = requiredString "scope" item
                    let status =
                        requiredString "status" item
                        |> Result.bind (fun token ->
                            CoverageStatus.tryOfWire token
                            |> Option.map Ok
                            |> Option.defaultValue (Error $"Unknown coverage status '{token}'."))
                    let provenance = stringArray "provenanceEvidenceIds" item
                    match scope, status, provenance with
                    | Ok s, Ok st, Ok p when p.IsEmpty -> Error "$.coverage[].provenanceEvidenceIds must not be empty."
                    | Ok s, Ok st, Ok p ->
                        Ok ({ Scope = s; Status = st; ProvenanceEvidenceIds = p } : CoverageClaim)
                    | Error error, _, _ -> Error error
                    | _, Error error, _ -> Error error
                    | _, _, Error error -> Error error)
                |> Seq.fold
                    (fun state item ->
                        match state, item with
                        | Error error, _ -> Error error
                        | _, Error error -> Error error
                        | Ok values, Ok value -> Ok(value :: values))
                    (Ok [])
                |> Result.map List.rev)

    let private parseProvider (root: JsonElement) =
        match root.TryGetProperty "provider" with
        | false, _ -> Ok None
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok None
        | true, value ->
            match requiredString "id" value, optionalString "model" value, optionalString "modelVersion" value, requiredString "adapterVersion" value with
            | Ok id, Ok model, Ok modelVersion, Ok adapterVersion ->
                Ok(Some { Id = id; Model = model; ModelVersion = modelVersion; AdapterVersion = adapterVersion })
            | Error error, _, _, _
            | _, Error error, _, _
            | _, _, Error error, _
            | _, _, _, Error error -> Error error

    let private parseConfidence (root: JsonElement) =
        match root.TryGetProperty "confidence" with
        | false, _ -> Ok None
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok None
        | true, value ->
            let magnitude =
                match value.TryGetProperty "magnitude" with
                | true, number when number.ValueKind = JsonValueKind.Number ->
                    match number.TryGetDouble() with
                    | true, parsed when parsed >= 0.0 && parsed <= 1.0 -> Ok parsed
                    | _ -> Error "$.confidence.magnitude must be between 0 and 1."
                | _ -> Error "$.confidence.magnitude must be a number."
            bind2 magnitude (requiredString "provenance" value) (fun m p -> Some { Magnitude = m; Provenance = p })

    let private parseUsage (root: JsonElement) =
        property "usage" root
        |> Result.bind (fun value ->
            match optionalInt "inputTokens" value, optionalInt "outputTokens" value, optionalInt "cachedInputTokens" value, optionalString "providerReportedCost" value with
            | Ok input, Ok output, Ok cached, Ok cost ->
                Ok { InputTokens = input; OutputTokens = output; CachedInputTokens = cached; ProviderReportedCost = cost }
            | Error error, _, _, _
            | _, Error error, _, _
            | _, _, Error error, _
            | _, _, _, Error error -> Error error)

    let private parseStateViewSchema (root: JsonElement) =
        property "stateViewSchema" root
        |> Result.bind (fun value ->
            bind2 (requiredString "id" value) (requiredInt "version" value) (fun id version -> { Id = id; Version = version }))

    let private parsePolicy (root: JsonElement) =
        match root.TryGetProperty "policy" with
        | false, _ -> Ok(None, None, None)
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok(None, None, None)
        | true, value ->
            match requiredString "id" value, requiredInt "version" value, requiredBool "experimental" value with
            | Ok id, Ok version, Ok experimental -> Ok(Some id, Some version, Some experimental)
            | Error error, _, _
            | _, Error error, _
            | _, _, Error error -> Error error

    let private parseEscalation (root: JsonElement) =
        property "escalation" root
        |> Result.bind (fun value ->
            if value.ValueKind <> JsonValueKind.Array then Error "$.escalation must be an array."
            else Ok(value.EnumerateArray() |> Seq.map (fun item -> item.GetRawText()) |> Seq.toList))

    let private errorOf = function
        | Ok _ -> None
        | Error error -> Some error

    let parseResolutionObservation (text: string) : Result<ResolutionObservation, string> =
        parseDocument text
        |> Result.bind (fun parsed ->
            use document = parsed
            let root = document.RootElement
            match requiredString "schema" root, requiredInt "schemaVersion" root with
            | Ok "ordo.resolution-observation", Ok 2 ->
                let resolutionId = requiredString "resolutionId" root
                let correlationId = optionalString "correlationId" root
                let causedBy = optionalString "causedBy" root
                let mode = requiredString "mode" root
                let contractId = requiredString "contractId" root
                let contractVersion = requiredInt "contractVersion" root
                let requestId = requiredString "requestId" root
                let stateFingerprint = requiredString "stateFingerprint" root
                let stateViewSchema = parseStateViewSchema root
                let coverage = parseCoverage root
                let provider = parseProvider root
                let startedAt = requiredDate "startedAt" root
                let completedAt = requiredDate "completedAt" root
                let outcome = requiredString "outcome" root
                let selectedChoice = optionalString "selectedChoice" root
                let confidence = parseConfidence root
                let evidenceUsed = stringArray "evidenceUsed" root
                let escalation = parseEscalation root
                let transition = optionalString "transition" root
                let policy = parsePolicy root
                let usage = parseUsage root
                let transportRetries = requiredInt "transportRetries" root
                let experimentReference = optionalString "experimentReference" root

                match
                    resolutionId, correlationId, causedBy, mode, contractId, contractVersion,
                    requestId, stateFingerprint, stateViewSchema, coverage, provider,
                    startedAt, completedAt, outcome, selectedChoice, confidence,
                    evidenceUsed, escalation, transition, policy, usage, transportRetries, experimentReference
                with
                | Ok resolutionId, Ok correlationId, Ok causedBy, Ok mode, Ok contractId, Ok contractVersion,
                  Ok requestId, Ok stateFingerprint, Ok stateViewSchema, Ok coverage, Ok provider,
                  Ok startedAt, Ok completedAt, Ok outcome, Ok selectedChoice, Ok confidence,
                  Ok evidenceUsed, Ok escalation, Ok transition, Ok (policyId, policyVersion, policyExperimental),
                  Ok usage, Ok transportRetries, Ok experimentReference ->
                    if completedAt < startedAt then Error "$.completedAt must not precede $.startedAt."
                    else
                        Ok
                            { ResolutionId = resolutionId
                              CorrelationId = correlationId
                              CausedBy = causedBy
                              Mode = mode
                              ContractId = contractId
                              ContractVersion = contractVersion
                              RequestId = requestId
                              StateFingerprint = stateFingerprint
                              StateViewSchema = stateViewSchema
                              Coverage = coverage
                              Provider = provider
                              StartedAt = startedAt
                              CompletedAt = completedAt
                              Outcome = outcome
                              SelectedChoice = selectedChoice
                              Confidence = confidence
                              EvidenceUsed = evidenceUsed
                              Escalation = escalation
                              Transition = transition
                              PolicyId = policyId
                              PolicyVersion = policyVersion
                              PolicyExperimental = policyExperimental
                              Usage = usage
                              TransportRetries = transportRetries
                              ExperimentReference = experimentReference }
                | _ ->
                    [ errorOf resolutionId
                      errorOf correlationId
                      errorOf causedBy
                      errorOf mode
                      errorOf contractId
                      errorOf contractVersion
                      errorOf requestId
                      errorOf stateFingerprint
                      errorOf stateViewSchema
                      errorOf coverage
                      errorOf provider
                      errorOf startedAt
                      errorOf completedAt
                      errorOf outcome
                      errorOf selectedChoice
                      errorOf confidence
                      errorOf evidenceUsed
                      errorOf escalation
                      errorOf transition
                      errorOf policy
                      errorOf usage
                      errorOf transportRetries
                      errorOf experimentReference ]
                    |> List.tryPick id
                    |> Option.defaultValue "Invalid resolution observation."
                    |> Error
            | Ok schema, Ok _ when schema <> "ordo.resolution-observation" ->
                Error $"Unexpected schema '{schema}'. Expected 'ordo.resolution-observation'."
            | Ok _, Ok version -> Error $"Unsupported ordo.resolution-observation schemaVersion {version}; supported: 2."
            | Error error, _ -> Error error
            | _, Error error -> Error error)

    let private writeOptionalString (writer: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some value -> writer.WriteString(name, value)
        | None -> writer.WriteNull(name)

    let private writeOptionalInt (writer: Utf8JsonWriter) (name: string) (value: int option) =
        match value with
        | Some value -> writer.WriteNumber(name, value)
        | None -> writer.WriteNull(name)

    let renderResolutionObservation (value: ResolutionObservation) =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", "ordo.resolution-observation")
            writer.WriteNumber("schemaVersion", 2)
            writer.WriteString("resolutionId", value.ResolutionId)
            writeOptionalString writer "correlationId" value.CorrelationId
            writeOptionalString writer "causedBy" value.CausedBy
            writer.WriteString("mode", value.Mode)
            writer.WriteString("contractId", value.ContractId)
            writer.WriteNumber("contractVersion", value.ContractVersion)
            writer.WriteString("requestId", value.RequestId)
            writer.WriteString("stateFingerprint", value.StateFingerprint)
            writer.WriteStartObject("stateViewSchema")
            writer.WriteString("id", value.StateViewSchema.Id)
            writer.WriteNumber("version", value.StateViewSchema.Version)
            writer.WriteEndObject()
            writer.WriteStartArray("coverage")
            for claim in value.Coverage do
                writer.WriteStartObject()
                writer.WriteString("scope", claim.Scope)
                writer.WriteString("status", CoverageStatus.toWire claim.Status)
                writer.WriteStartArray("provenanceEvidenceIds")
                claim.ProvenanceEvidenceIds |> List.iter writer.WriteStringValue
                writer.WriteEndArray()
                writer.WriteEndObject()
            writer.WriteEndArray()
            match value.Provider with
            | None -> writer.WriteNull("provider")
            | Some provider ->
                writer.WriteStartObject("provider")
                writer.WriteString("id", provider.Id)
                writeOptionalString writer "model" provider.Model
                writeOptionalString writer "modelVersion" provider.ModelVersion
                writer.WriteString("adapterVersion", provider.AdapterVersion)
                writer.WriteEndObject()
            writer.WriteString("startedAt", value.StartedAt.ToString("O", CultureInfo.InvariantCulture))
            writer.WriteString("completedAt", value.CompletedAt.ToString("O", CultureInfo.InvariantCulture))
            writer.WriteString("outcome", value.Outcome)
            writeOptionalString writer "selectedChoice" value.SelectedChoice
            match value.Confidence with
            | None -> writer.WriteNull("confidence")
            | Some confidence ->
                writer.WriteStartObject("confidence")
                writer.WriteNumber("magnitude", confidence.Magnitude)
                writer.WriteString("provenance", confidence.Provenance)
                writer.WriteEndObject()
            writer.WriteStartArray("evidenceUsed")
            value.EvidenceUsed |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            writer.WriteStartArray("escalation")
            value.Escalation |> List.iter (fun raw -> writer.WriteRawValue(raw, false))
            writer.WriteEndArray()
            writeOptionalString writer "transition" value.Transition
            match value.PolicyId, value.PolicyVersion, value.PolicyExperimental with
            | Some id, Some version, Some experimental ->
                writer.WriteStartObject("policy")
                writer.WriteString("id", id)
                writer.WriteNumber("version", version)
                writer.WriteBoolean("experimental", experimental)
                writer.WriteEndObject()
            | _ -> writer.WriteNull("policy")
            writer.WriteStartObject("usage")
            writeOptionalInt writer "inputTokens" value.Usage.InputTokens
            writeOptionalInt writer "outputTokens" value.Usage.OutputTokens
            writeOptionalInt writer "cachedInputTokens" value.Usage.CachedInputTokens
            writeOptionalString writer "providerReportedCost" value.Usage.ProviderReportedCost
            writer.WriteEndObject()
            writer.WriteNumber("transportRetries", value.TransportRetries)
            writeOptionalString writer "experimentReference" value.ExperimentReference
            writer.WriteEndObject())

    let parseAssessment (text: string) =
        parseDocument text
        |> Result.bind (fun document ->
            use document = document
            let root = document.RootElement
            match requiredString "schema" root, requiredInt "schemaVersion" root with
            | Ok "ros.resolution-assessment", Ok 1 ->
                let semantic = requiredString "semantic" root |> Result.bind (fun x -> SemanticAssessment.tryOfWire x |> Option.map Ok |> Option.defaultValue (Error $"Unknown semantic assessment '{x}'."))
                let operational = requiredString "operational" root |> Result.bind (fun x -> OperationalAssessment.tryOfWire x |> Option.map Ok |> Option.defaultValue (Error $"Unknown operational assessment '{x}'."))
                match
                    requiredString "assessmentId" root,
                    requiredString "resolutionId" root,
                    semantic,
                    operational,
                    requiredString "assessor" root,
                    requiredDate "assessedAt" root,
                    requiredDate "recordedAt" root,
                    stringArray "evidenceReferences" root,
                    requiredString "method" root,
                    optionalStringArray "limitations" root
                with
                | Ok assessmentId, Ok resolutionId, Ok semantic, Ok operational, Ok assessor, Ok assessedAt, Ok recordedAt, Ok evidenceReferences, Ok method, Ok limitations
                    when evidenceReferences.IsEmpty ->
                    Error "$.evidenceReferences must not be empty."
                | Ok assessmentId, Ok resolutionId, Ok semantic, Ok operational, Ok assessor, Ok assessedAt, Ok recordedAt, Ok evidenceReferences, Ok method, Ok limitations
                    when recordedAt < assessedAt ->
                    Error "$.recordedAt must not precede $.assessedAt."
                | Ok assessmentId, Ok resolutionId, Ok semantic, Ok operational, Ok assessor, Ok assessedAt, Ok recordedAt, Ok evidenceReferences, Ok method, Ok limitations ->
                    Ok
                        { AssessmentId = assessmentId
                          ResolutionId = resolutionId
                          Semantic = semantic
                          Operational = operational
                          Assessor = assessor
                          AssessedAt = assessedAt
                          RecordedAt = recordedAt
                          EvidenceReferences = evidenceReferences
                          Method = method
                          Limitations = limitations }
                | _ -> Error "Invalid ros.resolution-assessment record."
            | Ok schema, _ when schema <> "ros.resolution-assessment" -> Error $"Unexpected schema '{schema}'."
            | Ok _, Ok version -> Error $"Unsupported ros.resolution-assessment schemaVersion {version}; supported: 1."
            | Error error, _ -> Error error
            | _, Error error -> Error error)

    let renderAssessment (value: ResolutionAssessment) =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", "ros.resolution-assessment")
            writer.WriteNumber("schemaVersion", 1)
            writer.WriteString("assessmentId", value.AssessmentId)
            writer.WriteString("resolutionId", value.ResolutionId)
            writer.WriteString("semantic", SemanticAssessment.toWire value.Semantic)
            writer.WriteString("operational", OperationalAssessment.toWire value.Operational)
            writer.WriteString("assessor", value.Assessor)
            writer.WriteString("assessedAt", value.AssessedAt.ToString("O", CultureInfo.InvariantCulture))
            writer.WriteString("recordedAt", value.RecordedAt.ToString("O", CultureInfo.InvariantCulture))
            writer.WriteStartArray("evidenceReferences")
            value.EvidenceReferences |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            writer.WriteString("method", value.Method)
            writer.WriteStartArray("limitations")
            value.Limitations |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            writer.WriteEndObject())

    let parseSearchObservation (text: string) =
        parseDocument text
        |> Result.bind (fun document ->
            use document = document
            let root = document.RootElement
            let coverage = requiredString "coverage" root |> Result.bind (fun x -> CoverageStatus.tryOfWire x |> Option.map Ok |> Option.defaultValue (Error $"Unknown coverage status '{x}'."))
            let outcome = requiredString "outcome" root |> Result.bind (fun x -> SearchOutcome.tryOfWire x |> Option.map Ok |> Option.defaultValue (Error $"Unknown search outcome '{x}'."))
            match requiredString "schema" root, requiredInt "schemaVersion" root, requiredString "observationId" root, requiredString "target" root, requiredString "scope" root, requiredString "method" root, optionalString "query" root, requiredString "stateReference" root, coverage, stringArray "coverageEvidenceIds" root, optionalStringArray "exclusions" root, optionalStringArray "errors" root, outcome, requiredDate "observedAt" root with
            | Ok "ros.search-observation", Ok 1, Ok id, Ok target, Ok scope, Ok method, Ok query, Ok state, Ok coverage, Ok evidence, Ok exclusions, Ok errors, Ok outcome, Ok observedAt when evidence.IsEmpty ->
                Error "$.coverageEvidenceIds must not be empty."
            | Ok "ros.search-observation", Ok 1, Ok id, Ok target, Ok scope, Ok method, Ok query, Ok state, Ok coverage, Ok evidence, Ok exclusions, Ok errors, Ok outcome, Ok observedAt ->
                Ok { ObservationId = id; Target = target; Scope = scope; Method = method; Query = query; StateReference = state; Coverage = coverage; CoverageEvidenceIds = evidence; Exclusions = exclusions; Errors = errors; Outcome = outcome; ObservedAt = observedAt }
            | Ok schema, _, _, _, _, _, _, _, _, _, _, _, _, _ when schema <> "ros.search-observation" -> Error $"Unexpected schema '{schema}'."
            | Ok _, Ok version, _, _, _, _, _, _, _, _, _, _, _, _ when version <> 1 -> Error $"Unsupported ros.search-observation schemaVersion {version}; supported: 1."
            | _ -> Error "Invalid ros.search-observation record.")

    let renderSearchObservation (value: SearchObservation) =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", "ros.search-observation")
            writer.WriteNumber("schemaVersion", 1)
            writer.WriteString("observationId", value.ObservationId)
            writer.WriteString("target", value.Target)
            writer.WriteString("scope", value.Scope)
            writer.WriteString("method", value.Method)
            writeOptionalString writer "query" value.Query
            writer.WriteString("stateReference", value.StateReference)
            writer.WriteString("coverage", CoverageStatus.toWire value.Coverage)
            writer.WriteStartArray("coverageEvidenceIds")
            value.CoverageEvidenceIds |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            writer.WriteStartArray("exclusions")
            value.Exclusions |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            writer.WriteStartArray("errors")
            value.Errors |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            writer.WriteString("outcome", SearchOutcome.toWire value.Outcome)
            writer.WriteString("observedAt", value.ObservedAt.ToString("O", CultureInfo.InvariantCulture))
            writer.WriteEndObject())

    let parseEffectObservation (text: string) =
        parseDocument text
        |> Result.bind (fun document ->
            use document = document
            let root = document.RootElement
            let outcome = requiredString "outcome" root |> Result.bind (fun x -> EffectOutcome.tryOfWire x |> Option.map Ok |> Option.defaultValue (Error $"Unknown effect outcome '{x}'."))
            match
                requiredString "schema" root,
                requiredInt "schemaVersion" root,
                requiredString "observationId" root,
                optionalString "resolutionId" root,
                requiredString "effectId" root,
                outcome,
                requiredDate "attemptedAt" root,
                requiredDate "observedAt" root,
                requiredBool "reconciliationRequested" root,
                optionalString "reconciliationResult" root,
                optionalDate "reconciledAt" root,
                requiredBool "retryBlocked" root,
                requiredBool "compensationBlocked" root
            with
            | Ok "ros.effect-observation", Ok 1, Ok id, Ok resolution, Ok effectId, Ok outcome, Ok attemptedAt, Ok observedAt, Ok reconciliationRequested, Ok reconciliationResult, Ok reconciledAt, Ok retryBlocked, Ok compensationBlocked
                when observedAt < attemptedAt ->
                Error "$.observedAt must not precede $.attemptedAt."
            | Ok "ros.effect-observation", Ok 1, Ok id, Ok resolution, Ok effectId, Ok outcome, Ok attemptedAt, Ok observedAt, Ok reconciliationRequested, Ok reconciliationResult, Ok reconciledAt, Ok retryBlocked, Ok compensationBlocked
                when reconciledAt |> Option.exists (fun at -> at < attemptedAt) ->
                Error "$.reconciledAt must not precede $.attemptedAt."
            | Ok "ros.effect-observation", Ok 1, Ok id, Ok resolution, Ok effectId, Ok outcome, Ok attemptedAt, Ok observedAt, Ok reconciliationRequested, Ok reconciliationResult, Ok reconciledAt, Ok retryBlocked, Ok compensationBlocked
                when Option.isSome reconciliationResult <> Option.isSome reconciledAt ->
                Error "$.reconciliationResult and $.reconciledAt must either both be present or both be null."
            | Ok "ros.effect-observation", Ok 1, Ok id, Ok resolution, Ok effectId, Ok outcome, Ok attemptedAt, Ok observedAt, Ok reconciliationRequested, Ok reconciliationResult, Ok reconciledAt, Ok retryBlocked, Ok compensationBlocked ->
                Ok
                    { ObservationId = id
                      ResolutionId = resolution
                      EffectId = effectId
                      Outcome = outcome
                      AttemptedAt = attemptedAt
                      ObservedAt = observedAt
                      ReconciliationRequested = reconciliationRequested
                      ReconciliationResult = reconciliationResult
                      ReconciledAt = reconciledAt
                      RetryBlocked = retryBlocked
                      CompensationBlocked = compensationBlocked }
            | Ok schema, _, _, _, _, _, _, _, _, _, _, _, _ when schema <> "ros.effect-observation" -> Error $"Unexpected schema '{schema}'."
            | Ok _, Ok version, _, _, _, _, _, _, _, _, _, _, _ when version <> 1 -> Error $"Unsupported ros.effect-observation schemaVersion {version}; supported: 1."
            | _ -> Error "Invalid ros.effect-observation record.")

    let renderEffectObservation (value: EffectObservation) =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", "ros.effect-observation")
            writer.WriteNumber("schemaVersion", 1)
            writer.WriteString("observationId", value.ObservationId)
            writeOptionalString writer "resolutionId" value.ResolutionId
            writer.WriteString("effectId", value.EffectId)
            writer.WriteString("outcome", EffectOutcome.toWire value.Outcome)
            writer.WriteString("attemptedAt", value.AttemptedAt.ToString("O", CultureInfo.InvariantCulture))
            writer.WriteString("observedAt", value.ObservedAt.ToString("O", CultureInfo.InvariantCulture))
            writer.WriteBoolean("reconciliationRequested", value.ReconciliationRequested)
            writeOptionalString writer "reconciliationResult" value.ReconciliationResult
            match value.ReconciledAt with
            | Some reconciledAt -> writer.WriteString("reconciledAt", reconciledAt.ToString("O", CultureInfo.InvariantCulture))
            | None -> writer.WriteNull("reconciledAt")
            writer.WriteBoolean("retryBlocked", value.RetryBlocked)
            writer.WriteBoolean("compensationBlocked", value.CompensationBlocked)
            writer.WriteEndObject())

    let renderEffectiveCurrent (value: EffectiveCurrentProjection) =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", "ros.effective-current")
            writer.WriteNumber("schemaVersion", 1)
            writer.WriteNumber("basisCount", value.BasisCount)
            writer.WriteStartArray("supersededResolutionIds")
            value.SupersededResolutionIds |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            match value.Resolution with
            | None -> writer.WriteNull("resolution")
            | Some resolution ->
                writer.WritePropertyName("resolution")
                use document = JsonDocument.Parse(renderResolutionObservation resolution)
                document.RootElement.WriteTo writer
            match value.Assessment with
            | None -> writer.WriteNull("assessment")
            | Some assessment ->
                writer.WritePropertyName("assessment")
                use document = JsonDocument.Parse(renderAssessment assessment)
                document.RootElement.WriteTo writer
            writer.WriteEndObject())

    /// `producedBy` (`praxis.actor/1`) names the actor and execution that
    /// packaged the handoff, so a receiving agent or system keeps the
    /// originating provenance across the boundary.
    let renderHandoff (producedBy: Ros.Domain.Provenance.Actor option) (value: HandoffAuthority) =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", "ros.handoff-authority")
            writer.WriteNumber("schemaVersion", 1)
            writer.WriteStartObject("authority")
            writer.WriteString("repositoryRevision", value.Authority.RepositoryRevision)
            writer.WriteString("source", value.Authority.Source)
            writeOptionalString writer "stateFingerprint" value.Authority.StateFingerprint
            writer.WriteEndObject()
            writeOptionalString writer "resolutionId" value.ResolutionId
            let writeArray (name: string) (values: string list) =
                writer.WriteStartArray(name)
                values |> List.iter (fun value -> writer.WriteStringValue(value))
                writer.WriteEndArray()
            writeArray "authoritativeArtifacts" value.AuthoritativeArtifacts
            writeArray "historicalDecisionReferences" value.HistoricalDecisionReferences
            writeArray "facts" value.Facts
            writeArray "assumptions" value.Assumptions
            writeArray "unknowns" value.Unknowns
            writeArray "obligations" value.Obligations
            writeArray "completedVerification" value.CompletedVerification
            writeArray "legalNextActions" value.LegalNextActions
            writeArray "supersededResolutionIds" value.SupersededResolutionIds

            producedBy
            |> Option.iter (fun actor ->
                writer.WritePropertyName("producedBy")
                (Ros.Contracts.Provenance.ProvenanceJson.actorNode actor).WriteTo(writer))

            writer.WriteEndObject())
