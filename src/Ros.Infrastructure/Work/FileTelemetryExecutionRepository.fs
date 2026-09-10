namespace Ros.Infrastructure.Work

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Application.Git
open Ros.Domain.Telemetry
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Git
open Ros.Infrastructure.Json

/// The real "create a new telemetry execution record" effect
/// (`DF-ROS-2026-A028` Phase A): production's `startExecution`
/// (`tools/ros_telemetry.mjs`), the boundary the telemetry-resolution slice
/// (MIG-07-TELEMETRY-RESOLUTION) named `TelemetryEffect.CreateExecution` and
/// deliberately left unimplemented. Unlike `FileTelemetryStateRepository`
/// (read-only), this module writes a brand-new `.ros/telemetry/executions/
/// EXE-*.json` file -- it never reads, links, or finalizes an existing one.
[<RequireQualifiedAccess>]
module FileTelemetryExecutionRepository =
    /// `IdentitySource` and `CapabilitySource` are the same shape (both
    /// mirror production's plain `{type, name, mechanism}` source object)
    /// but stay distinct types so `Identity.discover`'s signature does not
    /// implicitly depend on the capability model.
    let private asCapabilitySource (source: IdentitySource) : CapabilitySource =
        { Type = source.Type; Name = source.Name; Mechanism = source.Mechanism }

    let nowIso () =
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

    let private newExecutionId (startedAt: string) =
        let stamp = startedAt.Replace("-", "").Replace(":", "").Replace(".", "")
        let suffix = RandomNumberGenerator.GetBytes 4 |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
        $"EXE-{stamp}-{suffix}"

    let private environmentIdentityInputs () : IdentityInputs =
        let variable name =
            match Environment.GetEnvironmentVariable(name: string) with
            | null
            | "" -> None
            | value -> Some value

        { IdentityInputs.empty with
            RosTelemetryProvider = variable "ROS_TELEMETRY_PROVIDER"
            RosTelemetryRuntime = variable "ROS_TELEMETRY_RUNTIME"
            RosTelemetryModel = variable "ROS_TELEMETRY_MODEL"
            RosTelemetryModelVersion = variable "ROS_TELEMETRY_MODEL_VERSION"
            RosTelemetryRuntimeVersion = variable "ROS_TELEMETRY_RUNTIME_VERSION"
            RosTelemetrySessionId = variable "ROS_TELEMETRY_SESSION_ID"
            RosTelemetryConversationId = variable "ROS_TELEMETRY_CONVERSATION_ID"
            RosTelemetryRunId = variable "ROS_TELEMETRY_RUN_ID"
            RosActor = variable "ROS_ACTOR"
            CodexSessionId = variable "CODEX_SESSION_ID"
            CodexThreadId = variable "CODEX_THREAD_ID"
            ClaudeCodeSessionId = variable "CLAUDE_CODE_SESSION_ID"
            GeminiSessionId = variable "GEMINI_SESSION_ID"
            CopilotSessionId = variable "COPILOT_SESSION_ID"
            GitHubActions = (variable "GITHUB_ACTIONS" = Some "true")
            GitHubRunId = variable "GITHUB_RUN_ID"
            OllamaHost = variable "OLLAMA_HOST" }

    /// Reused by `FileTelemetryFinalizationRepository` for an execution's
    /// ending snapshot -- the same shape production's own `gitSnapshot`
    /// produces for both the start and end of an execution's lifetime.
    type GitBaseline =
        { Available: bool
          Repository: string
          Branch: string option
          Commit: string option
          Dirty: bool option
          DirtyPaths: string list }

    let observeGitBaseline (root: string) (repositoryId: string) : GitBaseline =
        let ignored = FileWorkConfigRepository.readTelemetryIgnoredPaths root
        let isIgnored path = ignored |> List.exists (fun pattern -> Ros.Domain.Work.PathFilter.globMatch pattern path)

        match GitOperations.observe (ProcessGitRepository.create root) with
        | Ros.Domain.Git.GitStatusObservation.Unavailable _ ->
            { Available = false
              Repository = repositoryId
              Branch = None
              Commit = None
              Dirty = None
              DirtyPaths = [] }
        | observation ->
            let dirtyPaths =
                match observation with
                | Ros.Domain.Git.GitStatusObservation.Changed changes ->
                    changes
                    |> List.map _.Path
                    |> List.filter (isIgnored >> not)
                    |> List.distinct
                    |> List.sortWith (fun left right -> String.CompareOrdinal(left, right))
                | _ -> []

            let branch, commit = ProcessGitRepository.readBranchAndCommit root

            { Available = true
              Repository = repositoryId
              Branch = branch
              Commit = commit
              Dirty = Some(not dirtyPaths.IsEmpty)
              DirtyPaths = dirtyPaths }

    let sourceNode (source: CapabilitySource) : JsonObject =
        let node = JsonObject()
        node["type"] <- JsonValue.Create source.Type
        node["name"] <- JsonValue.Create source.Name
        node["mechanism"] <- JsonValue.Create source.Mechanism
        node

    let optionalString (value: string option) : JsonNode =
        match value with
        | Some text -> JsonValue.Create text
        | None -> null

    let stringArrayNode (values: string list) : JsonArray =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value: JsonNode))
        array

    let private capabilityHistoryEntryNode (entry: CapabilityHistoryEntry) : JsonObject =
        let node = JsonObject()
        node["status"] <- JsonValue.Create entry.Status
        node["reason"] <- optionalString entry.Reason
        node["source"] <- sourceNode entry.Source
        node["discoveredAt"] <- JsonValue.Create entry.DiscoveredAt
        node["lastAssessedAt"] <- JsonValue.Create entry.LastAssessedAt
        node["recordedAt"] <- JsonValue.Create entry.RecordedAt
        node

    /// Same conditional shape as production: a capability `initialCapabilities`
    /// seeded and no observation ever merged into (`Touched = false`) is
    /// exactly the 5-key object production's own map produces --
    /// `lastAssessedAt`/`recordedAt`/`history`/`historyOmitted` do not exist
    /// on it at all, not merely hold default values. A merged capability adds
    /// `lastAssessedAt`/`recordedAt` unconditionally, `history` only when at
    /// least one status change occurred, and `historyOmitted` only when
    /// history has actually overflowed the cap.
    let capabilityNode (capability: Capability) : JsonObject =
        let node = JsonObject()

        match capability.MetricId with
        | Some metricId -> node["metricId"] <- JsonValue.Create metricId
        | None -> ()

        match capability.ProviderField with
        | Some providerField -> node["providerField"] <- JsonValue.Create providerField
        | None -> ()

        node["status"] <- JsonValue.Create capability.Status
        node["reason"] <- optionalString capability.Reason
        node["discoveredAt"] <- JsonValue.Create capability.DiscoveredAt
        node["source"] <- sourceNode capability.Source

        if capability.Touched then
            node["lastAssessedAt"] <- JsonValue.Create capability.LastAssessedAt
            node["recordedAt"] <- JsonValue.Create capability.RecordedAt

            if not capability.History.IsEmpty then
                let history = JsonArray()
                capability.History |> List.iter (fun entry -> history.Add(capabilityHistoryEntryNode entry: JsonNode))
                node["history"] <- history

            if capability.HistoryOmitted > 0 then
                node["historyOmitted"] <- JsonValue.Create capability.HistoryOmitted

        node

    /// A `quality: "derived"` metric plus its capability-upsert inputs,
    /// including the content-addressed `measurementId` -- a SHA-256 digest
    /// of the metric's own fields with keys sorted, mirroring production
    /// `stable()` + `digest()`. Every derived metric this migration records
    /// (the execution-creation baseline, and every one `finalizeExecution`
    /// adds) shares this exact shape and quality, so one function builds
    /// them all.
    let derivedMetricNode (definition: MetricDefinition) (value: int64) (source: CapabilitySource) (collectedAt: string) : JsonObject * Capability =
        let sortedForDigest = JsonObject()
        sortedForDigest["aggregation"] <- JsonValue.Create definition.Aggregation
        sortedForDigest["collectedAt"] <- JsonValue.Create collectedAt
        sortedForDigest["confidence"] <- null
        sortedForDigest["currency"] <- null
        sortedForDigest["dimensions"] <- JsonObject()
        sortedForDigest["id"] <- JsonValue.Create definition.Id
        sortedForDigest["measurementId"] <- JsonValue.Create ""
        sortedForDigest["pricing"] <- null
        sortedForDigest["quality"] <- JsonValue.Create "derived"
        sortedForDigest["schemaVersion"] <- JsonValue.Create "1.0.0"
        sortedForDigest["scope"] <- JsonValue.Create "execution"

        let sortedSource = JsonObject()
        sortedSource["mechanism"] <- JsonValue.Create source.Mechanism
        sortedSource["name"] <- JsonValue.Create source.Name
        sortedSource["type"] <- JsonValue.Create source.Type
        sortedForDigest["source"] <- sortedSource
        sortedForDigest["unit"] <- JsonValue.Create definition.Unit
        sortedForDigest["value"] <- JsonValue.Create value

        let measurementId = "MEAS-" + CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact sortedForDigest)

        let node = JsonObject()
        node["measurementId"] <- JsonValue.Create measurementId
        node["id"] <- JsonValue.Create definition.Id
        node["value"] <- JsonValue.Create value
        node["unit"] <- JsonValue.Create definition.Unit
        node["currency"] <- null
        node["quality"] <- JsonValue.Create "derived"
        node["confidence"] <- null
        node["scope"] <- JsonValue.Create "execution"
        node["aggregation"] <- JsonValue.Create definition.Aggregation
        node["dimensions"] <- JsonObject()
        node["pricing"] <- null
        node["source"] <- sourceNode source
        node["collectedAt"] <- JsonValue.Create collectedAt
        node["schemaVersion"] <- JsonValue.Create "1.0.0"

        let capability =
            { MetricId = Some definition.Id
              ProviderField = None
              Status = "derived"
              Reason = Some "normalized measurement recorded"
              DiscoveredAt = collectedAt
              LastAssessedAt = collectedAt
              RecordedAt = collectedAt
              Source = source
              History = []
              HistoryOmitted = 0
              Touched = false }

        node, capability

    type CreateExecutionRequest =
        { WorkItemId: string
          WorkType: string
          Classifications: string list }

    let private serializerOptions =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    /// Mirrors production `startExecution`, excluding every explicit
    /// override option no current CLI command supplies (an execution ID,
    /// identity overrides, `startedAt`, requirement/experiment/PR links,
    /// and an initial `scope`) -- identity is discovered purely from the
    /// environment. Returns `None` when telemetry is disabled, matching
    /// production's own `if (!config.enabled) return null`.
    let createExecution (root: string) (request: CreateExecutionRequest) : Result<string option, string> =
        if not (FileWorkConfigRepository.readTelemetryEnabled root) then
            Ok None
        else
            try
                match RegistryLock.acquire root "telemetry-execution-index" RegistryLock.defaultSettings with
                | Error failure -> Error failure.Message
                | Ok lease ->
                    let result =
                        try
                            let startedAt = nowIso ()
                            let executionId = newExecutionId startedAt
                            let repositoryId = FileWorkConfigRepository.readRepositoryId root
                            let identity, discoverySource = Identity.discover (environmentIdentityInputs ())
                            let git = observeGitBaseline root repositoryId
                            let definitions = FileMetricRegistryRepository.read root

                            let classifications =
                                if request.Classifications.IsEmpty then
                                    [ WorkClassification.defaultFor request.WorkType ]
                                else
                                    request.Classifications |> List.distinct

                            let capabilities =
                                definitions
                                |> List.map (Capability.initial startedAt (asCapabilitySource discoverySource) identity.Runtime git.Available)

                            let baselineDefinition = definitions |> List.tryFind (fun metric -> metric.Id = "git.baseline_dirty_files")

                            let metricsNode = JsonArray()

                            let capabilities =
                                match baselineDefinition with
                                | None -> capabilities
                                | Some definition ->
                                    let baselineNode, upserted =
                                        derivedMetricNode
                                            definition
                                            (int64 git.DirtyPaths.Length)
                                            { Type = "ros-git"; Name = "git-status"; Mechanism = "porcelain-v1" }
                                            startedAt

                                    metricsNode.Add(baselineNode: JsonNode)

                                    let maxHistory = FileWorkConfigRepository.readTelemetryMaxCapabilityHistoryEntries root

                                    capabilities
                                    |> List.map (fun capability ->
                                        if capability.MetricId = Some definition.Id then
                                            Capability.upsert
                                                maxHistory
                                                startedAt
                                                startedAt
                                                startedAt
                                                upserted.Status
                                                upserted.Reason
                                                upserted.Source
                                                capability
                                        else
                                            capability)

                            let record = JsonObject()
                            record["schemaVersion"] <- JsonValue.Create "1.0.0"
                            record["executionId"] <- JsonValue.Create executionId
                            record["workItemId"] <- JsonValue.Create request.WorkItemId
                            record["status"] <- JsonValue.Create "active"
                            record["startedAt"] <- JsonValue.Create startedAt
                            record["finalizedAt"] <- null

                            let identityNode = JsonObject()
                            identityNode["provider"] <- JsonValue.Create identity.Provider
                            identityNode["model"] <- optionalString identity.Model
                            identityNode["modelVersion"] <- optionalString identity.ModelVersion
                            identityNode["runtime"] <- JsonValue.Create identity.Runtime
                            identityNode["runtimeVersion"] <- optionalString identity.RuntimeVersion
                            identityNode["sessionId"] <- optionalString identity.SessionId
                            identityNode["conversationId"] <- optionalString identity.ConversationId
                            identityNode["runId"] <- optionalString identity.RunId
                            identityNode["agentId"] <- optionalString identity.AgentId
                            identityNode["subagentId"] <- optionalString identity.SubagentId
                            identityNode["parentExecutionId"] <- optionalString identity.ParentExecutionId
                            identityNode["orchestration"] <- JsonObject()
                            record["identity"] <- identityNode

                            let provenanceNode = JsonObject()
                            provenanceNode["collector"] <- JsonValue.Create "ros"
                            provenanceNode["collectorVersion"] <- JsonValue.Create "1.0.0"
                            provenanceNode["discoveredAt"] <- JsonValue.Create startedAt
                            let sources = JsonArray()
                            sources.Add(sourceNode (asCapabilitySource discoverySource): JsonNode)
                            sources.Add(sourceNode { Type = "ros-git"; Name = "git"; Mechanism = "repository-baseline" }: JsonNode)
                            provenanceNode["sources"] <- sources
                            record["provenance"] <- provenanceNode

                            let classificationNode = JsonObject()
                            classificationNode["types"] <- stringArrayNode classifications
                            classificationNode["rationale"] <- null
                            classificationNode["evidence"] <- JsonArray()
                            classificationNode["rd"] <- null
                            record["classification"] <- classificationNode

                            let capabilitiesNode = JsonArray()
                            capabilities |> List.iter (fun capability -> capabilitiesNode.Add(capabilityNode capability: JsonNode))
                            record["capabilities"] <- capabilitiesNode

                            record["metrics"] <- metricsNode
                            record["rawTelemetry"] <- JsonArray()

                            let startedEvent = JsonObject()
                            startedEvent["type"] <- JsonValue.Create "execution.started"
                            startedEvent["occurredAt"] <- JsonValue.Create startedAt
                            startedEvent["source"] <- sourceNode { Type = "ros-clock"; Name = "ros"; Mechanism = "work-lifecycle" }
                            let eventsNode = JsonArray()
                            eventsNode.Add(startedEvent: JsonNode)
                            record["events"] <- eventsNode

                            let gitStartNode = JsonObject()
                            gitStartNode["available"] <- JsonValue.Create git.Available
                            gitStartNode["repository"] <- JsonValue.Create git.Repository
                            gitStartNode["branch"] <- optionalString git.Branch
                            gitStartNode["commit"] <- optionalString git.Commit

                            gitStartNode["dirty"] <-
                                match git.Dirty with
                                | Some value -> JsonValue.Create value
                                | None -> null

                            gitStartNode["dirtyPaths"] <- stringArrayNode git.DirtyPaths
                            let repositoryNode = JsonObject()
                            repositoryNode["start"] <- gitStartNode
                            repositoryNode["end"] <- null
                            repositoryNode["changeSummary"] <- null
                            record["repository"] <- repositoryNode

                            let scopeNode = JsonObject()
                            scopeNode["initial"] <- JsonObject()
                            scopeNode["actual"] <- JsonObject()
                            record["scope"] <- scopeNode
                            record["qualitySignals"] <- JsonArray()

                            let linksNode = JsonObject()
                            linksNode["workItemId"] <- JsonValue.Create request.WorkItemId
                            linksNode["parentWorkItemId"] <- null
                            linksNode["requirements"] <- JsonArray()
                            linksNode["acceptanceCriteria"] <- JsonArray()

                            linksNode["commits"] <-
                                match git.Commit with
                                | Some commit -> stringArrayNode [ commit ]
                                | None -> JsonArray()

                            linksNode["pullRequests"] <- JsonArray()
                            linksNode["experiments"] <- JsonArray()
                            linksNode["researchQuestions"] <- JsonArray()
                            linksNode["decisions"] <- JsonArray()
                            linksNode["defects"] <- JsonArray()
                            linksNode["dependencies"] <- JsonArray()
                            linksNode["evidence"] <- JsonArray()
                            record["links"] <- linksNode

                            let executionRoot = Path.Combine(root, FileWorkConfigRepository.readTelemetryExecutionRoot root)
                            Directory.CreateDirectory executionRoot |> ignore
                            let file = Path.Combine(executionRoot, $"{executionId}.json")

                            if File.Exists file then
                                Error $"duplicate execution ID '{executionId}'"
                            else
                                File.WriteAllText(file, record.ToJsonString serializerOptions + "\n")
                                Ok(Some executionId)
                        with error ->
                            Error error.Message

                    match lease.Release(), result with
                    | Error releaseFailure, Ok _ -> Error releaseFailure.Message
                    | _, outcome -> outcome
            with error ->
                Error error.Message
