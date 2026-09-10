namespace Ros.Infrastructure.Work

open System.IO
open System.Text.Json.Nodes
open Ros.Contracts.Work
open Ros.Domain.Telemetry

/// The real read effect behind `telemetryFindings` (`tools/ros_telemetry.mjs`):
/// turns raw `.ros/telemetry/executions/*.json` records, `ros.json`'s
/// `telemetry.*` config, and `telemetry/metrics.json` into the plain
/// `Parsed*`/`Raw*` types `Ros.Domain.Telemetry.TelemetryValidation` (Tier
/// 2) decides over. This module makes zero legality decisions itself --
/// no set-membership checks, no "is this a valid timestamp", no message
/// text -- it only observes what the raw JSON structurally is and reports
/// it faithfully, per SDE-DOCTRINE-003 (Tier 4 owns "what actually
/// happened", not "what legal state can exist").
[<RequireQualifiedAccess>]
module FileTelemetryValidationRepository =
    let private stringField (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = System.Text.Json.JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private numberField (node: JsonObject) (name: string) : float option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = System.Text.Json.JsonValueKind.Number ->
            match value.TryGetValue<float>() with
            | true, parsed -> Some parsed
            | _ -> None
        | _ -> None

    let private objectField (node: JsonObject) (name: string) : JsonObject option =
        match node[name] with
        | :? JsonObject as nested -> Some nested
        | _ -> None

    let private arrayField (node: JsonObject) (name: string) : JsonArray option =
        match node[name] with
        | :? JsonArray as nested -> Some nested
        | _ -> None

    let private hasKeyValue (node: JsonObject) (name: string) : bool = node.ContainsKey name

    let private parseSource (node: JsonObject) (name: string) : ParsedSource option =
        objectField node name
        |> Option.map (fun s -> { Type = stringField s "type"; Name = stringField s "name"; Mechanism = stringField s "mechanism" })

    /// `FieldPresence` for a plain string field: `KeyAbsent` if the key is
    /// missing, else `KeyPresent (Some s)` for a string value or `KeyPresent
    /// None` for any other JSON shape (including explicit `null`).
    let private stringPresence (node: JsonObject) (name: string) : FieldPresence<string> =
        if not (hasKeyValue node name) then KeyAbsent else KeyPresent(stringField node name)

    let private numberPresence (node: JsonObject) (name: string) : FieldPresence<float> =
        if not (hasKeyValue node name) then KeyAbsent else KeyPresent(numberField node name)

    // ---- Redaction scan (a mechanical, non-decisional structural walk: which paths look unredacted) ----

    let private redactedMarker = "[REDACTED_BY_ROS]"

    let private rawRedactedKeyRegex =
        System.Text.RegularExpressions.Regex(
            @"^(?:authorization|cookie|set-cookie|password|passwd|secret|credential|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|prompt|prompts|messages?|content|tool[_-]?input|tool[_-]?response|request|response|stdout|stderr|command|full[_-]?command|transcript[_-]?path|cwd|current[_-]?dir|project[_-]?dir|workspace[_-]?path|file[_-]?path|email|user\.email)$",
            System.Text.RegularExpressions.RegexOptions.Compiled
            ||| System.Text.RegularExpressions.RegexOptions.IgnoreCase
        )

    let private rawSensitiveSegmentRegex =
        System.Text.RegularExpressions.Regex(
            @"(?:^|[._-])(?:authorization|password|passwd|secret|credential|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|email)(?:$|[._-])",
            System.Text.RegularExpressions.RegexOptions.Compiled
            ||| System.Text.RegularExpressions.RegexOptions.IgnoreCase
        )

    let rec private sensitivePaths (value: JsonNode) (currentPath: string) (result: ResizeArray<string>) : unit =
        match value with
        | :? JsonArray as array -> array |> Seq.iteri (fun index item -> sensitivePaths item $"{currentPath}[{index}]" result)
        | :? JsonObject as obj ->
            for pair in obj do
                let childPath = $"{currentPath}.{pair.Key}"
                let keyIsSensitive = rawRedactedKeyRegex.IsMatch pair.Key || rawSensitiveSegmentRegex.IsMatch pair.Key

                let isRedactedMarker =
                    match pair.Value with
                    | :? JsonValue as v when v.GetValueKind() = System.Text.Json.JsonValueKind.String -> v.GetValue<string>() = redactedMarker
                    | _ -> false

                if keyIsSensitive && not isRedactedMarker then
                    result.Add childPath
                elif pair.Value <> null then
                    sensitivePaths pair.Value childPath result
        | _ -> ()

    // ---- Per-collection parsers ----

    let private parseCapabilityHistoryEntry (node: JsonObject) : ParsedCapabilityHistoryEntry =
        { Status = stringField node "status"
          Source = parseSource node "source"
          DiscoveredAt = stringField node "discoveredAt"
          LastAssessedAt = stringPresence node "lastAssessedAt"
          RecordedAt = stringPresence node "recordedAt" }

    let private parseArrayOf (array: JsonArray option) (parseOne: JsonObject -> 'a) : ParsedItem<'a> list =
        match array with
        | None -> []
        | Some items ->
            items
            |> Seq.map (function
                | :? JsonObject as o -> ValidItem(parseOne o)
                | _ -> MalformedItem)
            |> Seq.toList

    let private parseCapability (node: JsonObject) : ParsedCapability =
        let history =
            if not (hasKeyValue node "history") then
                KeyAbsent
            else
                match arrayField node "history" with
                | Some items -> KeyPresent(Some(parseArrayOf (Some items) parseCapabilityHistoryEntry))
                | None -> KeyPresent None

        { Status = stringField node "status"
          MetricId = stringField node "metricId"
          ProviderField = stringField node "providerField"
          Source = parseSource node "source"
          DiscoveredAt = stringField node "discoveredAt"
          LastAssessedAt = stringPresence node "lastAssessedAt"
          RecordedAt = stringPresence node "recordedAt"
          History = history
          HistoryOmitted = numberPresence node "historyOmitted" }

    /// JS `item.confidence !== null && item.confidence !== undefined`
    /// treats an explicit JSON `null` the same as an absent key for this
    /// specific field -- unlike `stringPresence`/`numberPresence`, where a
    /// present `null` is a distinct, reportable shape.
    let private parseConfidencePresence (node: JsonObject) : FieldPresence<ParsedConfidence> =
        if not (hasKeyValue node "confidence") then
            KeyAbsent
        else
            match node["confidence"] with
            | null -> KeyAbsent
            | :? JsonValue as v when v.GetValueKind() = System.Text.Json.JsonValueKind.Number ->
                match v.TryGetValue<float>() with
                | true, parsed -> KeyPresent(Some(ConfidenceNumber parsed))
                | _ -> KeyPresent(Some ConfidenceOtherShape)
            | :? JsonValue as v when v.GetValueKind() = System.Text.Json.JsonValueKind.String -> KeyPresent(Some(ConfidenceLabel(v.GetValue<string>())))
            | _ -> KeyPresent(Some ConfidenceOtherShape)

    let private parseMetric (node: JsonObject) : ParsedMetric =
        let source = parseSource node "source"
        let pricing = objectField node "pricing"

        { Id = stringField node "id"
          MeasurementId = stringField node "measurementId"
          Value = numberField node "value"
          Quality = stringField node "quality"
          Confidence = parseConfidencePresence node
          Source = source
          CollectedAt = stringField node "collectedAt"
          SchemaVersion = stringField node "schemaVersion"
          Scope = stringField node "scope"
          Aggregation = stringField node "aggregation"
          Unit = stringField node "unit"
          Currency = stringField node "currency"
          SourceType = source |> Option.bind (fun s -> s.Type)
          PricingSource = pricing |> Option.bind (fun p -> stringField p "source")
          PricingVersion = pricing |> Option.bind (fun p -> stringField p "version") }

    let private parseQualitySignal (node: JsonObject) : ParsedQualitySignal =
        { Detector = stringField node "detector"; Source = parseSource node "source" }

    let private parseRawSnapshot (node: JsonObject) : ParsedRawSnapshot =
        let sensitive = ResizeArray<string>()
        let payloadPresent = hasKeyValue node "payload"

        let payloadSerialized =
            if not payloadPresent then
                None
            else
                let payload = node["payload"]

                if isNull payload then
                    sensitivePaths payload "$" sensitive |> ignore
                    Some "null"
                else
                    sensitivePaths payload "$" sensitive
                    Some(payload.ToJsonString())

        { SnapshotId = stringField node "snapshotId"
          Adapter = stringField node "adapter"
          Source = parseSource node "source"
          CollectedAt = stringField node "collectedAt"
          PayloadSerialized = payloadSerialized
          PayloadSensitiveFindingPaths = sensitive |> List.ofSeq
          PayloadBytes = numberPresence node "payloadBytes" }

    let private parseEvent (node: JsonObject) : ParsedEvent =
        { EventType = stringField node "type"
          OccurredAt = stringField node "occurredAt"
          Source = parseSource node "source"
          RawRetentionStatus =
            match objectField node "rawRetention" with
            | None -> KeyAbsent
            | Some retention -> KeyPresent(stringField retention "status") }

    let private identityField (node: JsonObject) (name: string) : IdentityField =
        match node[name] with
        | null when not (hasKeyValue node name) -> FieldAbsent
        | null -> FieldNull
        | :? JsonValue as value when value.GetValueKind() = System.Text.Json.JsonValueKind.String -> FieldString(value.GetValue<string>())
        | _ -> FieldOtherInvalid

    let private parseIdentity (node: JsonObject) : ParsedIdentity =
        { Provider = identityField node "provider"
          Model = identityField node "model"
          ModelVersion = identityField node "modelVersion"
          Runtime = identityField node "runtime"
          RuntimeVersion = identityField node "runtimeVersion"
          SessionId = identityField node "sessionId"
          ConversationId = identityField node "conversationId"
          RunId = identityField node "runId"
          AgentId = identityField node "agentId"
          SubagentId = identityField node "subagentId"
          ParentExecutionId = identityField node "parentExecutionId" }

    let private parseProvenance (node: JsonObject) : ParsedProvenance =
        { Collector = stringField node "collector"; CollectorVersion = stringField node "collectorVersion"; DiscoveredAt = stringField node "discoveredAt" }

    let private parseClassification (node: JsonObject) : ParsedClassification =
        let types =
            match arrayField node "types" with
            | None -> None
            | Some items ->
                items
                |> Seq.map (function
                    | :? JsonValue as v when v.GetValueKind() = System.Text.Json.JsonValueKind.String -> Some(v.GetValue<string>())
                    | _ -> None)
                |> Seq.toList
                |> Some

        let rd = objectField node "rd"
        let rdHasKey (name: string) = rd |> Option.map (fun r -> hasKeyValue r name && r[name] <> null) |> Option.defaultValue false

        { Types = types
          HasResearchQuestion = rdHasKey "researchQuestion"
          HasHypothesis = rdHasKey "hypothesis"
          HasTechnicalUncertainty = rdHasKey "technicalUncertainty"
          HasExperimentalObjective = rdHasKey "experimentalObjective"
          HasKnowledgeGap = rdHasKey "knowledgeGap"
          HasRd = rd.IsSome }

    let private parseExecutionRecord (relative: string) (node: JsonObject) : ParsedExecutionRecord =
        { Relative = relative
          SchemaVersion = stringField node "schemaVersion"
          ExecutionId = stringField node "executionId"
          WorkItemId = stringField node "workItemId"
          Identity = objectField node "identity" |> Option.map parseIdentity
          Provenance = objectField node "provenance" |> Option.map parseProvenance
          StartedAt = stringField node "startedAt"
          Status = stringField node "status"
          FinalizedAt = stringField node "finalizedAt"
          CapabilitiesIsArray = (arrayField node "capabilities").IsSome
          MetricsIsArray = (arrayField node "metrics").IsSome
          RawTelemetryIsArray = (arrayField node "rawTelemetry").IsSome
          EventsIsArray = (arrayField node "events").IsSome
          HasRepository = (objectField node "repository").IsSome
          HasLinks = (objectField node "links").IsSome
          Classification = objectField node "classification" |> Option.map parseClassification
          Capabilities = parseArrayOf (arrayField node "capabilities") parseCapability
          Metrics = parseArrayOf (arrayField node "metrics") parseMetric
          QualitySignals = parseArrayOf (arrayField node "qualitySignals") parseQualitySignal
          RawTelemetry = parseArrayOf (arrayField node "rawTelemetry") parseRawSnapshot
          Events = parseArrayOf (arrayField node "events") parseEvent }

    // ---- Config / registry / context / execution-file I/O ----

    let private relativePath (root: string) (file: string) : string = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')

    let private readConfig (root: string) : RawTelemetryConfig =
        { Enabled = FileWorkConfigRepository.readTelemetryEnabled root
          DisabledReason = FileWorkConfigRepository.readTelemetryDisabledReason root
          RequireFinalization = FileWorkConfigRepository.readTelemetryRequireFinalization root
          ExecutionRoot = FileWorkConfigRepository.readTelemetryExecutionRoot root
          MetricRegistryPath = FileWorkConfigRepository.readTelemetryMetricRegistryPath root
          MaxRawPayloadBytes = FileWorkConfigRepository.readTelemetryMaxRawPayloadBytes root
          MaxRawSnapshotsPerExecution = FileWorkConfigRepository.readTelemetryMaxRawSnapshotsPerExecution root
          MaxRawBytesPerExecution = FileWorkConfigRepository.readTelemetryMaxRawBytesPerExecution root
          MaxCapabilityHistoryEntries = FileWorkConfigRepository.readTelemetryMaxCapabilityHistoryEntries root }

    let private readRegistry (root: string) (metricRegistryPath: string) : RawMetricRegistry =
        let path = Path.Combine(root, metricRegistryPath)

        if not (File.Exists path) then
            RegistryFileMissing
        else
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as registry ->
                    match stringField registry "schemaVersion", arrayField registry "metrics" with
                    | Some "1.0.0", Some metrics ->
                        metrics
                        |> Seq.map (function
                            | :? JsonObject as metric ->
                                ({ Id = stringField metric "id"; Unit = stringField metric "unit"; Aggregation = stringField metric "aggregation"; Collection = stringField metric "collection" }: RawRegistryMetric)
                            | _ -> ({ Id = None; Unit = None; Aggregation = None; Collection = None }: RawRegistryMetric))
                        |> Seq.toList
                        |> RegistryFileParsed
                    | _ -> RegistryFileWrongShape
                | _ -> RegistryFileWrongShape
            with _ ->
                RegistryFileUnparseable

    let private readContextWorkItems (root: string) : Ros.Domain.Work.LiveWorkItem list =
        let path = Path.Combine(root, ".ros", "context", "current.json")

        if not (File.Exists path) then
            []
        else
            match WorkContextPlanContract.parseJson (File.ReadAllText path) with
            | Ok view -> view.WorkItems
            | Error _ -> []

    let private executionFiles (root: string) (executionRoot: string) : string list =
        let directory = Path.Combine(root, executionRoot)

        if not (Directory.Exists directory) then
            []
        else
            Directory.GetFiles(directory, "*.json") |> Array.sort |> Array.toList

    /// Mirrors production `telemetryFindings(root, context)`: reads
    /// config, registry, every execution record (or reports why one
    /// couldn't be read), and the live context's work items, then hands
    /// all of it to `Ros.Domain.Telemetry.TelemetryValidation.findings` to
    /// decide. This function itself makes no validity decisions.
    let findings (root: string) : TelemetryFinding list =
        let config = readConfig root
        let registry = readRegistry root config.MetricRegistryPath
        let workItems = readContextWorkItems root

        let unreadable = ResizeArray<UnreadableExecutionFile>()

        let records =
            executionFiles root config.ExecutionRoot
            |> List.choose (fun file ->
                let relative = relativePath root file

                try
                    match JsonNode.Parse(File.ReadAllText file) with
                    | :? JsonObject as record -> Some(parseExecutionRecord relative record)
                    | _ ->
                        unreadable.Add { Relative = relative; Message = "execution telemetry record must be an object" }
                        None
                with error ->
                    unreadable.Add { Relative = relative; Message = $"malformed telemetry JSON: {error.Message}" }
                    None)

        TelemetryValidation.findings config registry (unreadable |> List.ofSeq) records workItems
