namespace Ros.Domain.Telemetry

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open Ros.Domain.Work

/// Mirrors production `telemetryFindings` (`tools/ros_telemetry.mjs`), the
/// telemetry contributor to `validate`'s combined findings array. Unlike
/// the earlier draft of this port, this operates entirely over the plain
/// `Parsed*`/`Raw*` types in `Validation.fs` -- no `JsonNode` crosses into
/// this module at all. `Ros.Infrastructure.Work.
/// FileTelemetryValidationRepository` owns turning raw JSON into those
/// types (and nothing else); every actual legality decision -- what a
/// valid capability status is, what "present but invalid" means, how
/// history entries must order -- lives here, per SDE-DOCTRINE-003/004
/// (Tier 2 owns "what legal state can exist"; Tier 4 only reports what
/// the raw data structurally was).
[<RequireQualifiedAccess>]
module TelemetryValidation =
    let private finding path field message : TelemetryFinding = { Path = path; Field = field; Message = message }

    let private nonEmpty (value: string option) : string option = value |> Option.filter (fun s -> s <> "")

    let private isTimestamp (value: string) : bool =
        match DateTimeOffset.TryParse(value, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None) with
        | true, _ -> true
        | false, _ -> false

    let private validTimestamp (value: string option) : bool = value |> Option.map isTimestamp |> Option.defaultValue false

    let private presenceValue (presence: FieldPresence<'a>) : 'a option =
        match presence with
        | KeyPresent(Some value) -> Some value
        | _ -> None

    let private identityTruthy (field: IdentityField) : bool =
        match field with
        | FieldString s when s <> "" -> true
        | _ -> false

    let private identityInvalid (field: IdentityField) : bool =
        match field with
        | FieldOtherInvalid -> true
        | _ -> false

    // ---- Closed vocabularies (raw external strings; see QueueValidation for the established precedent of validating a raw string rather than a closed DU, since an unrecognized value must be reportable, not merely rejected at parse) ----

    let private workClassifications =
        set
            [ "research"
              "development"
              "research-development"
              "maintenance"
              "defect-bug-fix"
              "investigation-diagnostic"
              "architecture-design"
              "documentation"
              "testing-verification"
              "infrastructure-devops"
              "security"
              "operational-support"
              "refactoring"
              "experiment"
              "prototype-proof-of-concept"
              "administrative-process" ]

    let private classificationExtensionRegex = Regex(@"^x-[a-z0-9][a-z0-9._-]*(?:/[a-z0-9][a-z0-9._-]*)?$", RegexOptions.Compiled)

    let private validClassification (value: string) =
        workClassifications.Contains value || classificationExtensionRegex.IsMatch value

    let private capabilityStatuses = set [ "supported-observed"; "supported-unavailable"; "unsupported"; "unknown"; "derived"; "estimated" ]
    let private qualities = set [ "observed"; "derived"; "estimated" ]
    let private confidenceLabels = set [ "low"; "medium"; "high" ]
    let private metricScopes = set [ "operation"; "turn"; "tool"; "execution"; "session"; "work-item"; "repository" ]
    let private aggregations = set [ "sum"; "latest"; "latest-per-session"; "maximum"; "none" ]

    let private sourceTypes =
        set [ "runtime-api"; "runtime-hook"; "runtime-output"; "environment"; "ros-git"; "ros-clock"; "agent-report"; "human-report"; "external-tool"; "calculated" ]

    let private qualityDetectors =
        set [ "compiler"; "type-system"; "test"; "static-analysis"; "architecture-check"; "runtime"; "agent-self"; "human"; "escaped-defect"; "mutation-test"; "ros-state-system" ]

    let private sourceFindings (relative: string) (field: string) (source: ParsedSource option) (missingMessage: string) : TelemetryFinding list =
        match source with
        | Some s ->
            match nonEmpty s.Type, nonEmpty s.Name, nonEmpty s.Mechanism with
            | Some sourceType, Some _, Some _ ->
                if sourceTypes.Contains sourceType then [] else [ finding relative $"{field}.type" $"unknown source type '{sourceType}'" ]
            | _ -> [ finding relative field missingMessage ]
        | None -> [ finding relative field missingMessage ]

    // ---- Config / registry state machine ----

    let private configLimits (config: RawTelemetryConfig) =
        [ "maxRawPayloadBytes", config.MaxRawPayloadBytes, 1024
          "maxRawSnapshotsPerExecution", config.MaxRawSnapshotsPerExecution, 1
          "maxRawBytesPerExecution", config.MaxRawBytesPerExecution, 1024
          "maxCapabilityHistoryEntries", config.MaxCapabilityHistoryEntries, 2 ]

    let private validateConfig (config: RawTelemetryConfig) : Result<RawTelemetryConfig, TelemetryFinding> =
        match configLimits config |> List.tryFind (fun (_, value, minimum) -> value < minimum) with
        | Some(field, _, minimum) -> Error(finding "ros.json" "telemetry" $"telemetry.{field} must be an integer greater than or equal to {minimum}")
        | None -> Ok config

    let private validateRegistry (path: string) (raw: RawMetricRegistry) : Result<Map<string, MetricDefinition>, TelemetryFinding> =
        let unsupported () = Error(finding path "schemaVersion" $"telemetry metric registry is missing or unsupported: {path}")

        match raw with
        | RegistryFileMissing
        | RegistryFileUnparseable
        | RegistryFileWrongShape -> unsupported ()
        | RegistryFileParsed metrics ->
            let mutable error = None
            let mutable map: Map<string, MetricDefinition> = Map.empty

            for metric in metrics do
                if error.IsNone then
                    match nonEmpty metric.Id with
                    | None -> error <- Some "telemetry metric registry has invalid or duplicate id ''"
                    | Some id when map.ContainsKey id -> error <- Some $"telemetry metric registry has invalid or duplicate id '{id}'"
                    | Some id ->
                        match metric.Unit, metric.Aggregation with
                        | Some unit, Some aggregation when aggregations.Contains aggregation ->
                            let definition: MetricDefinition =
                                { Id = id; Unit = unit; Aggregation = aggregation; Collection = metric.Collection |> Option.defaultValue "" }

                            map <- map.Add(id, definition)
                        | _ -> error <- Some $"telemetry metric registry has an invalid definition for '{id}'"

            match error with
            | Some message -> Error(finding path "schemaVersion" message)
            | None -> Ok map

    /// The pipeline's own early-exit state machine: config validity, then
    /// disabled/enabled, then registry validity -- production's own three
    /// sequential early `return`s, each closing off everything after it.
    let private evaluateConfig (config: RawTelemetryConfig) (registry: RawMetricRegistry) : TelemetryValidationOutcome =
        match validateConfig config with
        | Error f -> ConfigInvalid f
        | Ok cfg ->
            if not cfg.Enabled then
                match nonEmpty cfg.DisabledReason with
                | None -> TelemetryDisabled(Some(finding "ros.json" "telemetry.disabledReason" "disabled telemetry requires an explicit reason"))
                | Some _ -> TelemetryDisabled None
            else
                match validateRegistry cfg.MetricRegistryPath registry with
                | Error f -> RegistryInvalid f
                | Ok map ->
                    ReadyToValidate(map, cfg.RequireFinalization, cfg.MaxCapabilityHistoryEntries, cfg.MaxRawPayloadBytes, cfg.MaxRawSnapshotsPerExecution, cfg.MaxRawBytesPerExecution)

    // ---- Capabilities ----

    let private capabilityFindings
        (relative: string)
        (maxHistory: int)
        (registry: Map<string, MetricDefinition>)
        (capabilities: ParsedItem<ParsedCapability> list)
        : TelemetryFinding list * Map<string, ParsedCapability> =
        let findings = ResizeArray<TelemetryFinding>()
        let byMetric = Dictionary<string, ParsedCapability>()
        let keys = HashSet<string>()

        capabilities
        |> List.iteri (fun index parsedItem ->
            let field = $"capabilities[{index}]"

            match parsedItem with
            | MalformedItem -> findings.Add(finding relative field "capability must be an object")
            | ValidItem capability ->
                let status = capability.Status |> Option.defaultValue ""

                if not (capabilityStatuses.Contains status) then
                    findings.Add(finding relative $"{field}.status" $"invalid capability status '{status}'")

                let truthyMetricId = nonEmpty capability.MetricId
                let truthyProviderField = nonEmpty capability.ProviderField

                if truthyMetricId.IsNone && truthyProviderField.IsNone then
                    findings.Add(finding relative field "capability requires metricId or providerField")

                let key = (capability.MetricId |> Option.defaultValue "") + " " + (capability.ProviderField |> Option.defaultValue "")

                if keys.Contains key then
                    findings.Add(finding relative field "duplicate capability identity")

                keys.Add key |> ignore

                findings.AddRange(sourceFindings relative $"{field}.source" capability.Source "capability source provenance is required")

                if not (validTimestamp capability.DiscoveredAt) then
                    findings.Add(finding relative $"{field}.discoveredAt" "capability discovery timestamp is required")

                match capability.LastAssessedAt with
                | KeyPresent value when not (value |> Option.map isTimestamp |> Option.defaultValue false) ->
                    findings.Add(finding relative $"{field}.lastAssessedAt" "capability assessment timestamp must be valid")
                | _ -> ()

                match capability.RecordedAt with
                | KeyPresent value when not (value |> Option.map isTimestamp |> Option.defaultValue false) ->
                    findings.Add(finding relative $"{field}.recordedAt" "capability recording timestamp must be valid")
                | _ -> ()

                let historyEntries =
                    match capability.History with
                    | KeyAbsent -> []
                    | KeyPresent None ->
                        findings.Add(finding relative $"{field}.history" "capability history must be an array")
                        []
                    | KeyPresent(Some entries) -> entries

                if historyEntries.Length > maxHistory then
                    findings.Add(finding relative $"{field}.history" "capability history exceeds the configured retention limit")

                match capability.HistoryOmitted with
                | KeyPresent value ->
                    match value with
                    | Some v when v >= 1.0 && Math.Floor v = v -> ()
                    | _ -> findings.Add(finding relative $"{field}.historyOmitted" "omitted capability-history count must be a positive integer")
                | KeyAbsent -> ()

                historyEntries
                |> List.iteri (fun historyIndex item ->
                    let historyField = $"{field}.history[{historyIndex}]"

                    match item with
                    | MalformedItem -> findings.Add(finding relative historyField "capability history entry must be an object")
                    | ValidItem observation ->
                        let observationStatus = observation.Status |> Option.defaultValue ""

                        if not (capabilityStatuses.Contains observationStatus) then
                            findings.Add(finding relative $"{historyField}.status" $"invalid capability status '{observationStatus}'")

                        findings.AddRange(sourceFindings relative $"{historyField}.source" observation.Source "capability history requires source provenance")

                        if not (validTimestamp observation.DiscoveredAt) then
                            findings.Add(finding relative $"{historyField}.discoveredAt" "capability history transition timestamp is required")

                        match observation.LastAssessedAt with
                        | KeyPresent value when not (value |> Option.map isTimestamp |> Option.defaultValue false) ->
                            findings.Add(finding relative $"{historyField}.lastAssessedAt" "capability history assessment timestamp must be valid")
                        | _ -> ()

                        match observation.RecordedAt with
                        | KeyPresent value when not (value |> Option.map isTimestamp |> Option.defaultValue false) ->
                            findings.Add(finding relative historyField "capability history recording timestamp must be valid")
                        | _ -> ()

                        let observedAt = presenceValue observation.RecordedAt |> Option.filter isTimestamp

                        let nextAt =
                            match historyEntries |> List.tryItem (historyIndex + 1) with
                            | Some(ValidItem nextEntry) -> presenceValue nextEntry.RecordedAt |> Option.filter isTimestamp
                            | Some MalformedItem -> None
                            | None -> presenceValue capability.RecordedAt |> Option.filter isTimestamp

                        match observedAt, nextAt with
                        | Some observedValid, Some nextValid when DateTimeOffset.Parse nextValid < DateTimeOffset.Parse observedValid ->
                            findings.Add(finding relative historyField "capability state recording order must be chronological")
                        | _ -> ())

                match truthyMetricId with
                | Some id when not (registry.ContainsKey id) ->
                    findings.Add(finding relative $"{field}.metricId" $"unknown normalized metric '{id}'; use providerField for unmapped capabilities")
                | Some id -> byMetric[id] <- capability
                | None -> ())

        findings |> List.ofSeq, byMetric |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

    // ---- Metrics ----

    let private metricFindings
        (relative: string)
        (registry: Map<string, MetricDefinition>)
        (capabilityByMetric: Map<string, ParsedCapability>)
        (sessionId: string option)
        (metrics: ParsedItem<ParsedMetric> list)
        : TelemetryFinding list =
        let findings = ResizeArray<TelemetryFinding>()
        let measurementIds = HashSet<string>()
        let unitByMetric = Dictionary<string, string>()

        metrics
        |> List.iteri (fun index parsedItem ->
            let field = $"metrics[{index}]"

            match parsedItem with
            | MalformedItem -> findings.Add(finding relative field "metric must be an object")
            | ValidItem item ->
                let id = item.Id |> Option.defaultValue ""
                let definition = registry.TryFind id

                match nonEmpty item.MeasurementId with
                | None -> findings.Add(finding relative $"{field}.measurementId" "measurement identity is required")
                | Some measurementId ->
                    if measurementIds.Contains measurementId then
                        findings.Add(finding relative $"{field}.measurementId" $"duplicate measurement ID '{measurementId}'")

                    measurementIds.Add measurementId |> ignore

                if definition.IsNone then
                    findings.Add(finding relative $"{field}.id" $"unknown normalized metric '{id}'; retain it as raw telemetry")

                if not (item.Value |> Option.map (fun v -> Double.IsFinite v && v >= 0.0) |> Option.defaultValue false) then
                    findings.Add(finding relative $"{field}.value" "metric value must be a finite non-negative number")

                let quality = item.Quality |> Option.defaultValue ""

                if not (qualities.Contains quality) then
                    findings.Add(finding relative $"{field}.quality" $"invalid metric quality '{quality}'")

                let validConfidence =
                    match presenceValue item.Confidence with
                    | Some(ConfidenceNumber c) -> c >= 0.0 && c <= 1.0
                    | Some(ConfidenceLabel label) -> confidenceLabels.Contains label
                    | _ -> false

                if quality = "estimated" && not validConfidence then
                    findings.Add(
                        finding relative $"{field}.confidence" "estimated metric requires numeric confidence from zero through one or low/medium/high confidence"
                    )

                let confidencePresent = match item.Confidence with KeyPresent _ -> true | KeyAbsent -> false

                if quality <> "estimated" && confidencePresent then
                    findings.Add(finding relative $"{field}.confidence" "confidence is only valid for an estimated metric")

                findings.AddRange(sourceFindings relative $"{field}.source" item.Source "metric source provenance is required")

                if not (validTimestamp item.CollectedAt) then
                    findings.Add(finding relative $"{field}.collectedAt" "metric collection timestamp is required")

                if item.SchemaVersion <> Some "1.0.0" then
                    findings.Add(finding relative $"{field}.schemaVersion" "metric schema version must be '1.0.0'")

                let scope = item.Scope |> Option.defaultValue ""

                if not (metricScopes.Contains scope) then
                    findings.Add(finding relative $"{field}.scope" $"invalid metric scope '{scope}'")

                match definition with
                | Some d when item.Aggregation <> Some d.Aggregation -> findings.Add(finding relative $"{field}.aggregation" $"metric aggregation must be '{d.Aggregation}'")
                | _ -> ()

                match definition with
                | Some d when d.Unit = "currency" ->
                    let currencyValid = item.Currency |> Option.map (fun c -> Regex.IsMatch(c, "^[A-Z]{3}$")) |> Option.defaultValue false

                    if item.Unit <> Some "currency" || not currencyValid then
                        findings.Add(finding relative $"{field}.unit" "cost metric requires unit 'currency' and an ISO-style three-letter currency")

                    if item.SourceType = Some "calculated" then
                        if (nonEmpty item.PricingSource).IsNone || (nonEmpty item.PricingVersion).IsNone then
                            findings.Add(finding relative $"{field}.pricing" "calculated cost requires pricing source and version provenance")
                | Some d when item.Unit <> Some d.Unit -> findings.Add(finding relative $"{field}.unit" $"metric unit must be '{d.Unit}'")
                | _ -> ()

                let unitKey = (item.Unit |> Option.defaultValue "") + ":" + (item.Currency |> Option.defaultValue "")

                if unitByMetric.ContainsKey id && unitByMetric[id] <> unitKey then
                    findings.Add(finding relative $"{field}.unit" "conflicting units for the same normalized metric")

                unitByMetric[id] <- unitKey

                match definition with
                | Some d when d.Collection = "ros-derived" && quality <> "derived" ->
                    findings.Add(finding relative $"{field}.quality" "ROS-derived metric cannot be represented as observed or estimated")
                | _ -> ()

                if (id = "context.utilization" || id = "runtime.cpu_utilization") && (item.Value |> Option.defaultValue 0.0) > 1.0 then
                    findings.Add(finding relative $"{field}.value" "ratio cannot exceed one")

                if definition |> Option.map (fun d -> d.Aggregation = "latest-per-session") |> Option.defaultValue false
                   && scope = "session"
                   && (nonEmpty sessionId).IsNone then
                    findings.Add(finding relative $"{field}.scope" "session-cumulative or session-gauge metric requires a session ID")

                match capabilityByMetric.TryFind id with
                | Some capability when capability.Status = Some "unsupported" ->
                    findings.Add(finding relative field "metric is present while its capability is marked unsupported")
                | Some capability when capability.Status = Some "supported-unavailable" && capability.DiscoveredAt = item.CollectedAt ->
                    findings.Add(finding relative field "metric is present in the same snapshot that marks the capability unavailable")
                | _ -> ())

        findings |> List.ofSeq

    // ---- Quality signals ----

    let private qualitySignalFindings (relative: string) (signals: ParsedItem<ParsedQualitySignal> list) : TelemetryFinding list =
        let findings = ResizeArray<TelemetryFinding>()

        signals
        |> List.iteri (fun index parsedItem ->
            let field = $"qualitySignals[{index}]"

            match parsedItem with
            | MalformedItem -> findings.Add(finding relative field "quality signal must be an object")
            | ValidItem signal ->
                let detector = signal.Detector |> Option.defaultValue ""

                if not (qualityDetectors.Contains detector) && not (detector.StartsWith "x-") then
                    findings.Add(finding relative $"{field}.detector" $"invalid quality-signal detector '{detector}'")

                findings.AddRange(sourceFindings relative $"{field}.source" signal.Source "quality signal requires source type, name, and mechanism"))

        findings |> List.ofSeq

    // ---- Raw telemetry ----

    let private rawTelemetryFindings (relative: string) (maxPayload: int) (maxSnapshots: int) (maxBytes: int) (snapshots: ParsedItem<ParsedRawSnapshot> list) : TelemetryFinding list =
        let findings = ResizeArray<TelemetryFinding>()
        let snapshotIds = HashSet<string>()
        let mutable retainedRawBytes = 0

        snapshots
        |> List.iteri (fun index parsedItem ->
            let field = $"rawTelemetry[{index}]"

            match parsedItem with
            | MalformedItem -> findings.Add(finding relative field "raw snapshot must be an object")
            | ValidItem snapshot ->
                let snapshotId = nonEmpty snapshot.SnapshotId
                let adapter = nonEmpty snapshot.Adapter

                let sourceOk =
                    snapshot.Source
                    |> Option.map (fun s -> (nonEmpty s.Type).IsSome && (nonEmpty s.Name).IsSome && (nonEmpty s.Mechanism).IsSome)
                    |> Option.defaultValue false

                let collectedAtOk = validTimestamp snapshot.CollectedAt

                if snapshotId.IsNone || adapter.IsNone || not sourceOk || not collectedAtOk then
                    findings.Add(finding relative field "raw snapshot requires identity, adapter, source type/name/mechanism, and timestamp")
                else
                    let sourceType = snapshot.Source |> Option.bind (fun s -> s.Type) |> Option.defaultValue ""

                    if not (sourceTypes.Contains sourceType) then
                        findings.Add(finding relative $"{field}.source.type" $"unknown source type '{sourceType}'")

                match snapshotId with
                | Some id ->
                    if snapshotIds.Contains id then
                        findings.Add(finding relative $"{field}.snapshotId" $"duplicate snapshot ID '{id}'")

                    snapshotIds.Add id |> ignore
                | None -> ()

                match snapshot.PayloadSerialized with
                | None -> findings.Add(finding relative $"{field}.payload" "raw snapshot payload is required")
                | Some serialized ->
                    let payloadBytes = Text.Encoding.UTF8.GetByteCount serialized
                    retainedRawBytes <- retainedRawBytes + payloadBytes

                    if payloadBytes > maxPayload then
                        findings.Add(finding relative $"{field}.payload" "raw snapshot exceeds configured storage limit")

                    match snapshot.PayloadBytes with
                    | KeyPresent value ->
                        match value with
                        | Some v when int v = payloadBytes -> ()
                        | _ -> findings.Add(finding relative $"{field}.payloadBytes" "recorded raw payload byte count does not match the retained payload")
                    | KeyAbsent -> ()

                    for path in snapshot.PayloadSensitiveFindingPaths do
                        findings.Add(finding relative $"{field}.payload" $"sensitive raw field is not redacted: {path}"))

        if snapshots.Length > maxSnapshots then
            findings.Add(finding relative "rawTelemetry" "raw snapshot count exceeds the configured per-execution limit")

        if retainedRawBytes > maxBytes then
            findings.Add(finding relative "rawTelemetry" "retained raw payload bytes exceed the configured per-execution limit")

        findings |> List.ofSeq

    // ---- Events ----

    let private eventFindings (relative: string) (events: ParsedItem<ParsedEvent> list) : TelemetryFinding list =
        let findings = ResizeArray<TelemetryFinding>()

        events
        |> List.iteri (fun index parsedItem ->
            let field = $"events[{index}]"

            match parsedItem with
            | MalformedItem -> findings.Add(finding relative field "event must be an object")
            | ValidItem event ->
                let hasType = (nonEmpty event.EventType).IsSome
                let hasTimestamp = validTimestamp event.OccurredAt

                let sourceOk =
                    event.Source
                    |> Option.map (fun s -> (nonEmpty s.Type).IsSome && (nonEmpty s.Name).IsSome && (nonEmpty s.Mechanism).IsSome)
                    |> Option.defaultValue false

                if not hasType || not hasTimestamp || not sourceOk then
                    findings.Add(finding relative field "event requires type, timestamp, and source type/name/mechanism")
                else
                    let sourceType = event.Source |> Option.bind (fun s -> s.Type) |> Option.defaultValue ""

                    if not (sourceTypes.Contains sourceType) then
                        findings.Add(finding relative $"{field}.source.type" $"unknown source type '{sourceType}'")

                match event.RawRetentionStatus with
                | KeyPresent(Some("retained" | "omitted")) -> ()
                | KeyPresent _ -> findings.Add(finding relative $"{field}.rawRetention.status" "raw retention status must be retained or omitted")
                | KeyAbsent -> ())

        findings |> List.ofSeq

    // ---- Per-record ----

    let private recordFindings (maxHistory: int) (registry: Map<string, MetricDefinition>) (maxPayload: int) (maxSnapshots: int) (maxBytes: int) (record: ParsedExecutionRecord) : TelemetryFinding list =
        let relative = record.Relative
        let findings = ResizeArray<TelemetryFinding>()

        if record.SchemaVersion <> Some "1.0.0" then
            let shown = record.SchemaVersion |> Option.defaultValue "missing"
            findings.Add(finding relative "schemaVersion" $"unsupported telemetry schema version '{shown}'")

        let executionId = nonEmpty record.ExecutionId

        match executionId with
        | None -> findings.Add(finding relative "executionId" "execution identity is required")
        | Some id ->
            if not (Regex.IsMatch(id, "^EXE-[A-Za-z0-9._-]+$")) then
                findings.Add(finding relative "executionId" "execution identity must be portable and match ^EXE-[A-Za-z0-9._-]+$")

            if IO.Path.GetFileNameWithoutExtension relative <> id then
                findings.Add(finding relative "executionId" "execution filename must match executionId")

        if (nonEmpty record.WorkItemId).IsNone then
            findings.Add(finding relative "workItemId" "work-item linkage is required")

        match record.Identity with
        | Some identity when identityTruthy identity.Provider && identityTruthy identity.Runtime ->
            for name, value in
                [ "provider", identity.Provider
                  "model", identity.Model
                  "modelVersion", identity.ModelVersion
                  "runtime", identity.Runtime
                  "runtimeVersion", identity.RuntimeVersion
                  "sessionId", identity.SessionId
                  "conversationId", identity.ConversationId
                  "runId", identity.RunId
                  "agentId", identity.AgentId
                  "subagentId", identity.SubagentId
                  "parentExecutionId", identity.ParentExecutionId ] do
                if identityInvalid value then
                    findings.Add(finding relative $"identity.{name}" "identity value must be a string or null")
        | _ ->
            findings.Add(finding relative "identity" "provider and runtime identity are required; use 'unknown' only when discovery cannot resolve them")

        let provenanceOk =
            record.Provenance
            |> Option.map (fun p -> p.Collector = Some "ros" && (nonEmpty p.CollectorVersion).IsSome && validTimestamp p.DiscoveredAt)
            |> Option.defaultValue false

        if not provenanceOk then
            findings.Add(finding relative "provenance" "collector, collectorVersion, and discovery timestamp are required")

        if not (validTimestamp record.StartedAt) then
            findings.Add(finding relative "startedAt" "valid execution start timestamp is required")

        match record.Status with
        | Some("active" | "finalized") -> ()
        | _ ->
            let shown = record.Status |> Option.defaultValue "undefined"
            findings.Add(finding relative "status" $"invalid execution status '{shown}'")

        if record.Status = Some "finalized" && not (validTimestamp record.FinalizedAt) then
            findings.Add(finding relative "finalizedAt" "finalized execution requires a completion timestamp")

        match record.FinalizedAt |> Option.filter isTimestamp, record.StartedAt |> Option.filter isTimestamp with
        | Some f, Some s when DateTimeOffset.Parse f < DateTimeOffset.Parse s -> findings.Add(finding relative "finalizedAt" "execution completion precedes its start")
        | _ -> ()

        for fieldName, isArray in
            [ "capabilities", record.CapabilitiesIsArray
              "metrics", record.MetricsIsArray
              "rawTelemetry", record.RawTelemetryIsArray
              "events", record.EventsIsArray ] do
            if not isArray then
                findings.Add(finding relative fieldName "required telemetry collection must be an array")

        if not record.HasRepository then
            findings.Add(finding relative "repository" "repository linkage and snapshots are required")

        if not record.HasLinks then
            findings.Add(finding relative "links" "execution links object is required")

        match record.Classification with
        | None -> findings.Add(finding relative "classification.types" "at least one work classification is required")
        | Some classification ->
            match classification.Types with
            | None -> findings.Add(finding relative "classification.types" "at least one work classification is required")
            | Some types when types.IsEmpty -> findings.Add(finding relative "classification.types" "at least one work classification is required")
            | Some types ->
                let typeValues = types |> List.map (Option.defaultValue "")

                if (typeValues |> Set.ofList |> Set.count) <> typeValues.Length then
                    findings.Add(finding relative "classification.types" "work classifications must be unique")

                for t in typeValues do
                    if not (validClassification t) then
                        findings.Add(finding relative "classification.types" $"invalid work classification '{t}'")

                if typeValues |> List.contains "research-development" then
                    let rdOk =
                        classification.HasRd
                        && (classification.HasResearchQuestion
                            || classification.HasHypothesis
                            || classification.HasTechnicalUncertainty
                            || classification.HasExperimentalObjective
                            || classification.HasKnowledgeGap)

                    if not rdOk then
                        findings.Add(finding relative "classification.rd" "research-development classification requires factual uncertainty or experimental context")

        let capabilityFindingsResult, capabilityByMetric = capabilityFindings relative maxHistory registry record.Capabilities
        findings.AddRange capabilityFindingsResult

        let sessionId =
            match record.Identity with
            | Some identity ->
                match identity.SessionId with
                | FieldString s -> Some s
                | _ -> None
            | None -> None

        findings.AddRange(metricFindings relative registry capabilityByMetric sessionId record.Metrics)
        findings.AddRange(qualitySignalFindings relative record.QualitySignals)
        findings.AddRange(rawTelemetryFindings relative maxPayload maxSnapshots maxBytes record.RawTelemetry)
        findings.AddRange(eventFindings relative record.Events)

        findings |> List.ofSeq

    // ---- Cross-record and work-item linkage ----

    let private crossRecordFindings (requireFinalization: bool) (records: ParsedExecutionRecord list) (workItems: LiveWorkItem list) : TelemetryFinding list =
        let findings = ResizeArray<TelemetryFinding>()
        let byId = records |> List.choose (fun r -> nonEmpty r.ExecutionId |> Option.map (fun id -> id, r)) |> Map.ofList
        let workById = workItems |> List.map (fun item -> item.Id, item) |> Map.ofList

        for record in records do
            match nonEmpty record.WorkItemId with
            | Some workItemId ->
                match workById.TryFind workItemId with
                | None -> findings.Add(finding record.Relative "workItemId" $"linked work item '{workItemId}' is not present in repository context")
                | Some workItem ->
                    let executionId = nonEmpty record.ExecutionId |> Option.defaultValue ""

                    if not (workItem.TelemetryExecutionIds |> List.contains executionId) then
                        findings.Add(finding record.Relative "workItemId" $"execution '{executionId}' is not linked back from work item '{workItemId}'")
            | None -> ()

            let parentExecutionId =
                match record.Identity with
                | Some identity ->
                    match identity.ParentExecutionId with
                    | FieldString s -> nonEmpty (Some s)
                    | _ -> None
                | None -> None

            match parentExecutionId with
            | Some parentId when not (byId.ContainsKey parentId) -> findings.Add(finding record.Relative "identity.parentExecutionId" $"parent execution '{parentId}' was not found")
            | _ -> ()

        for item in workItems do
            for id in item.TelemetryExecutionIds do
                if not (byId.ContainsKey id) then
                    findings.Add(finding ".ros/context/current.json" "telemetryExecutionIds" $"work item '{item.Id}' links missing execution '{id}'")

            if requireFinalization && item.SemanticState = LiveWorkState.Complete && not item.TelemetryExecutionIds.IsEmpty then
                for id in item.TelemetryExecutionIds do
                    let status = byId.TryFind id |> Option.bind (fun r -> r.Status)

                    if status <> Some "finalized" then
                        let path = byId.TryFind id |> Option.map (fun r -> r.Relative) |> Option.defaultValue ".ros/context/current.json"
                        findings.Add(finding path "status" $"completed work item '{item.Id}' has unfinalized telemetry")

        findings |> List.ofSeq

    /// Mirrors production `telemetryFindings(root, context)` end to end.
    /// Findings are returned in the same category order production's own
    /// imperative `findings.push` sequence produces (config/registry
    /// early-exit, per-file read failures, duplicate execution ids,
    /// per-record checks in file order, then cross-record checks) -- not
    /// sorted, matching this module's own internal order rather than
    /// `validate`'s own final `.sort()`, which the eventual unified
    /// `validate` command applies once over every contributor's combined
    /// output.
    let findings
        (config: RawTelemetryConfig)
        (registry: RawMetricRegistry)
        (unreadable: UnreadableExecutionFile list)
        (records: ParsedExecutionRecord list)
        (workItems: LiveWorkItem list)
        : TelemetryFinding list =
        match evaluateConfig config registry with
        | ConfigInvalid f -> [ f ]
        | TelemetryDisabled(Some f) -> [ f ]
        | TelemetryDisabled None -> []
        | RegistryInvalid f -> [ f ]
        | ReadyToValidate(registryMap, requireFinalization, maxHistory, maxPayload, maxSnapshots, maxBytes) ->
            let unreadableFindings = unreadable |> List.map (fun u -> finding u.Relative "" u.Message)

            let seen = Dictionary<string, string>()
            let duplicateFindings = ResizeArray<TelemetryFinding>()

            for record in records do
                match nonEmpty record.ExecutionId with
                | Some id ->
                    match seen.TryGetValue id with
                    | true, firstSeen -> duplicateFindings.Add(finding record.Relative "executionId" $"duplicate execution ID also in {firstSeen}")
                    | false, _ -> seen[id] <- record.Relative
                | None -> ()

            let perRecord = records |> List.collect (recordFindings maxHistory registryMap maxPayload maxSnapshots maxBytes)
            let cross = crossRecordFindings requireFinalization records workItems

            unreadableFindings @ (duplicateFindings |> List.ofSeq) @ perRecord @ cross
