namespace Ros.Infrastructure.Work

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Ros.Domain.Telemetry
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Git
open Ros.Infrastructure.Json

/// Real mutation effects against an existing execution record
/// (`DF-ROS-2026-A028` Phase A): production's `finalizeExecution`/
/// `finalizeWorkExecutions` and `recordTelemetryLifecycle`
/// (`tools/ros_telemetry.mjs`). `finalizeWorkExecutions` is reduced to the
/// path production's own `work complete` CLI actually reaches (no
/// `--input` adapter ingestion, which no current CLI command threads
/// through to a work-completion finalize). Each mutates an existing
/// `.ros/telemetry/executions/EXE-*.json` record in place, under its own
/// per-execution lock -- neither creates or links an execution (see
/// `FileTelemetryExecutionRepository`).
[<RequireQualifiedAccess>]
module FileTelemetryFinalizationRepository =
    let private serializerOptions =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private stringField (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private boolField (node: JsonObject) (name: string) : bool option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.True || value.GetValueKind() = JsonValueKind.False ->
            Some(value.GetValue<bool>())
        | _ -> None

    /// A JS-truthiness approximation for a field on an arbitrary caller-
    /// supplied JSON object, shared by every real-adapter mapping: absent,
    /// JSON `false`, and an empty string are falsy; everything else
    /// (objects, arrays, non-zero numbers, non-empty strings, `true`) is
    /// truthy. Realistic adapter input never relies on JS's zero-is-falsy
    /// rule for these particular fields (always an object, a non-empty
    /// identifier string, or absent), so that one divergence from full JS
    /// truthiness is left unreproduced.
    let private hasTruthyField (node: JsonObject) (name: string) : bool =
        match node[name] with
        | null -> false
        | :? JsonValue as v when v.GetValueKind() = JsonValueKind.False -> false
        | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> v.GetValue<string>() <> ""
        | _ -> true

    let private intField (node: JsonObject) (name: string) : int option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number ->
            match value.TryGetValue<int>() with
            | true, parsed -> Some parsed
            | _ -> None
        | _ -> None

    let private parseSource (node: JsonObject) : CapabilitySource =
        { Type = stringField node "type" |> Option.defaultValue ""
          Name = stringField node "name" |> Option.defaultValue ""
          Mechanism = stringField node "mechanism" |> Option.defaultValue "" }

    let private parseHistoryEntry (node: JsonObject) : CapabilityHistoryEntry =
        { Status = stringField node "status" |> Option.defaultValue ""
          Reason = stringField node "reason"
          Source = (match node["source"] with :? JsonObject as source -> parseSource source | _ -> { Type = ""; Name = ""; Mechanism = "" })
          DiscoveredAt = stringField node "discoveredAt" |> Option.defaultValue ""
          LastAssessedAt = stringField node "lastAssessedAt" |> Option.defaultValue ""
          RecordedAt = stringField node "recordedAt" |> Option.defaultValue "" }

    /// The reverse of `FileTelemetryExecutionRepository.capabilityNode`: a
    /// capability read back has `Touched = true` exactly when it carries the
    /// `lastAssessedAt` key production's own `initialCapabilities` never
    /// writes -- the same distinction the writer uses, applied in reverse.
    let private parseCapability (node: JsonObject) : Capability =
        let discoveredAt = stringField node "discoveredAt" |> Option.defaultValue ""
        let touched = node.ContainsKey "lastAssessedAt"

        { MetricId = stringField node "metricId"
          ProviderField = stringField node "providerField"
          Status = stringField node "status" |> Option.defaultValue ""
          Reason = stringField node "reason"
          DiscoveredAt = discoveredAt
          LastAssessedAt = stringField node "lastAssessedAt" |> Option.defaultValue discoveredAt
          RecordedAt = stringField node "recordedAt" |> Option.defaultValue discoveredAt
          Source = (match node["source"] with :? JsonObject as source -> parseSource source | _ -> { Type = ""; Name = ""; Mechanism = "" })
          History =
            match node["history"] with
            | :? JsonArray as history -> history |> Seq.choose (function :? JsonObject as entry -> Some(parseHistoryEntry entry) | _ -> None) |> Seq.toList
            | _ -> []
          HistoryOmitted = intField node "historyOmitted" |> Option.defaultValue 0
          Touched = touched }

    let private executionFile (root: string) (executionId: string) =
        Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json")

    /// Every execution file whose `workItemId` and `status` match, in the
    /// same sorted-by-filename order `FileTelemetryStateRepository` already
    /// establishes as this migration's ordering convention.
    let private activeExecutionIds (root: string) (workItemId: string) : string list =
        let directory = Path.Combine(root, ".ros", "telemetry", "executions")

        if not (Directory.Exists directory) then
            []
        else
            Directory.GetFiles(directory, "*.json")
            |> Array.sort
            |> Array.choose (fun file ->
                match JsonNode.Parse(File.ReadAllText file) with
                | :? JsonObject as record ->
                    match stringField record "workItemId", stringField record "status", stringField record "executionId" with
                    | Some recordWorkItemId, Some "active", Some executionId when recordWorkItemId = workItemId -> Some executionId
                    | _ -> None
                | _ -> None)
            |> Array.toList

    /// A change summary's identity plus every field production's own
    /// `finalizeExecution` reads from `cleanBaselineChanges`'s result --
    /// unavailable with a reason string that matches one of production's
    /// own literal reasons or a real `GitUnavailableReason` code.
    let private computeChangeSummary
        (root: string)
        (startAvailable: bool)
        (startCommit: string option)
        (startDirty: bool option)
        (endSnapshot: FileTelemetryExecutionRepository.GitBaseline)
        : Result<ChangeSummary, string> =
        if not startAvailable then
            Error "git-unavailable"
        else
            match startCommit with
            | None -> Error "starting-commit-unavailable"
            | Some _ when startDirty = Some true -> Error "preexisting-dirty-worktree"
            | Some startCommitValue ->
                if not endSnapshot.Available then
                    Error "git-unavailable"
                else
                    let endCommitValue = endSnapshot.Commit |> Option.defaultValue startCommitValue

                    match ProcessGitRepository.readNameStatusDiff root startCommitValue with
                    | Error failure -> Error(Ros.Domain.Git.GitUnavailableReason.code failure.Reason)
                    | Ok nameStatusText ->
                        match ProcessGitRepository.readNumstatDiff root startCommitValue with
                        | Error failure -> Error(Ros.Domain.Git.GitUnavailableReason.code failure.Reason)
                        | Ok numstatText ->
                            match ProcessGitRepository.readUntrackedFiles root with
                            | Error failure -> Error(Ros.Domain.Git.GitUnavailableReason.code failure.Reason)
                            | Ok untrackedText ->
                                let ignoredPatterns = FileWorkConfigRepository.readTelemetryIgnoredPaths root
                                let nameStatusEntries = ChangeSummaryParser.parseNameStatus ignoredPatterns nameStatusText
                                let numstatLines = ChangeSummaryParser.parseNumstat numstatText
                                let untrackedPaths = ChangeSummaryParser.parseUntracked untrackedText

                                let untrackedLineCounts =
                                    untrackedPaths
                                    |> List.map (fun path ->
                                        let fullPath = Path.Combine(root, path)

                                        let counted =
                                            try
                                                let bytes = File.ReadAllBytes fullPath

                                                if Array.contains 0uy bytes then
                                                    { Binary = true; Lines = 0 }
                                                elif bytes.Length = 0 then
                                                    { Binary = false; Lines = 0 }
                                                else
                                                    let text = Text.Encoding.UTF8.GetString bytes
                                                    let lines = text.Split '\n'
                                                    let count = if text.EndsWith "\n" then lines.Length - 1 else lines.Length
                                                    { Binary = false; Lines = count }
                                            with _ ->
                                                { Binary = false; Lines = 0 }

                                        path, counted)
                                    |> Map.ofList

                                let commitsResult =
                                    if endCommitValue <> startCommitValue then
                                        ProcessGitRepository.readCommitCount root startCommitValue endCommitValue
                                    else
                                        Ok 0

                                match commitsResult with
                                | Error failure -> Error(Ros.Domain.Git.GitUnavailableReason.code failure.Reason)
                                | Ok commits ->
                                    Ok(
                                        ChangeSummaryParser.compute
                                            ignoredPatterns
                                            nameStatusEntries
                                            numstatLines
                                            untrackedPaths
                                            untrackedLineCounts
                                            startCommitValue
                                            endCommitValue
                                            commits
                                    )

    let private finalizeOne (root: string) (repositoryId: string) (executionId: string) : Result<unit, string> =
        match RegistryLock.acquire root $"telemetry-execution:{executionId}" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                try
                    let file = executionFile root executionId

                    if not (File.Exists file) then
                        Ok()
                    else
                        match JsonNode.Parse(File.ReadAllText file) with
                        | :? JsonObject as record ->
                            match stringField record "status" with
                            | Some "finalized" -> Ok()
                            | _ ->
                                let finalizedAt = FileTelemetryExecutionRepository.nowIso ()
                                let startedAt = stringField record "startedAt" |> Option.defaultValue finalizedAt

                                let startSnapshot =
                                    match record["repository"] with
                                    | :? JsonObject as repository ->
                                        match repository["start"] with
                                        | :? JsonObject as start -> Some start
                                        | _ -> None
                                    | _ -> None

                                let startAvailable = startSnapshot |> Option.bind (fun s -> boolField s "available") |> Option.defaultValue false
                                let startCommit = startSnapshot |> Option.bind (fun s -> stringField s "commit")
                                let startDirty = startSnapshot |> Option.bind (fun s -> boolField s "dirty")

                                let endSnapshot = FileTelemetryExecutionRepository.observeGitBaseline root repositoryId

                                let endNode = JsonObject()
                                endNode["available"] <- JsonValue.Create endSnapshot.Available
                                endNode["repository"] <- JsonValue.Create endSnapshot.Repository

                                endNode["branch"] <-
                                    match endSnapshot.Branch with
                                    | Some branch -> JsonValue.Create branch
                                    | None -> null

                                endNode["commit"] <-
                                    match endSnapshot.Commit with
                                    | Some commit -> JsonValue.Create commit
                                    | None -> null

                                endNode["dirty"] <-
                                    match endSnapshot.Dirty with
                                    | Some dirty -> JsonValue.Create dirty
                                    | None -> null

                                let dirtyPathsNode = JsonArray()
                                endSnapshot.DirtyPaths |> List.iter (fun path -> dirtyPathsNode.Add(JsonValue.Create path: JsonNode))
                                endNode["dirtyPaths"] <- dirtyPathsNode

                                let definitions = FileMetricRegistryRepository.read root
                                let definitionFor id = definitions |> List.tryFind (fun d -> d.Id = id)

                                let capabilitiesArray =
                                    match record["capabilities"] with
                                    | :? JsonArray as array -> array
                                    | _ -> JsonArray()

                                let existingCapabilities =
                                    capabilitiesArray
                                    |> Seq.choose (function :? JsonObject as node -> Some(parseCapability node) | _ -> None)
                                    |> Seq.toList

                                let metricsArray =
                                    match record["metrics"] with
                                    | :? JsonArray as array -> array
                                    | _ -> JsonArray()

                                let calculatedSource name mechanism : CapabilitySource =
                                    { Type = "calculated"; Name = name; Mechanism = mechanism }

                                let rosGitSource mechanism : CapabilitySource =
                                    { Type = "ros-git"; Name = "git-status"; Mechanism = mechanism }

                                let maxHistory = FileWorkConfigRepository.readTelemetryMaxCapabilityHistoryEntries root

                                let upsertInto (capabilities: Capability list) metricId status reason source =
                                    capabilities
                                    |> List.map (fun capability ->
                                        if capability.MetricId = Some metricId then
                                            Capability.upsert maxHistory finalizedAt finalizedAt finalizedAt status reason source capability
                                        else
                                            capability)

                                let appendMetric metricId (value: int64) source (capabilities: Capability list) =
                                    match definitionFor metricId with
                                    | None -> capabilities
                                    | Some definition ->
                                        let node, upserted = FileTelemetryExecutionRepository.derivedMetricNode definition value source finalizedAt
                                        metricsArray.Add(node: JsonNode)
                                        upsertInto capabilities metricId upserted.Status upserted.Reason upserted.Source

                                // time.wall_ms and time.blocked_ms are unconditional, matching production.
                                let wallMs =
                                    max
                                        0L
                                        (DateTimeOffset.Parse(finalizedAt).ToUnixTimeMilliseconds()
                                         - DateTimeOffset.Parse(startedAt).ToUnixTimeMilliseconds())

                                let lifecycleEvents =
                                    match record["events"] with
                                    | :? JsonArray as events ->
                                        events
                                        |> Seq.choose (function
                                            | :? JsonObject as node ->
                                                match stringField node "type", stringField node "occurredAt" with
                                                | Some eventType, Some occurredAt -> Some { Type = eventType; OccurredAt = occurredAt }
                                                | _ -> None
                                            | _ -> None)
                                        |> Seq.toList
                                    | _ -> []

                                let blockedMs = BlockedDuration.compute lifecycleEvents finalizedAt

                                let capabilitiesAfterTime =
                                    existingCapabilities
                                    |> appendMetric "time.wall_ms" wallMs (calculatedSource "ros" "timestamp-difference")
                                    |> appendMetric "time.blocked_ms" blockedMs (calculatedSource "ros-work-lifecycle" "block-resume-intervals")

                                let capabilitiesAfterEnding =
                                    if endSnapshot.Available then
                                        capabilitiesAfterTime
                                        |> appendMetric "git.ending_dirty_files" (int64 endSnapshot.DirtyPaths.Length) (rosGitSource "porcelain-v1")
                                    else
                                        upsertInto
                                            capabilitiesAfterTime
                                            "git.ending_dirty_files"
                                            "supported-unavailable"
                                            (Some "ending Git status unavailable")
                                            (rosGitSource "porcelain-v1")

                                let changeSummaryOutcome = computeChangeSummary root startAvailable startCommit startDirty endSnapshot

                                let gitChangeMetricIds =
                                    [ "git.commits_created"; "git.files_added"; "git.files_modified"; "git.files_deleted"; "git.files_renamed"
                                      "git.binary_files_changed"; "git.lines_added"; "git.lines_deleted"; "tests.added"; "tests.modified"
                                      "tests.removed"; "documentation.files_changed" ]

                                let capabilitiesAfterChangeSummary, changeSummaryNode, commitsToLink =
                                    match changeSummaryOutcome with
                                    | Ok summary ->
                                        let metricSource : CapabilitySource = { Type = "ros-git"; Name = "git-diff"; Mechanism = summary.Mechanism }

                                        let capabilities =
                                            capabilitiesAfterEnding
                                            |> appendMetric "git.commits_created" (int64 summary.Commits) metricSource
                                            |> appendMetric "git.files_added" (int64 summary.Counts.Added) metricSource
                                            |> appendMetric "git.files_modified" (int64 summary.Counts.Modified) metricSource
                                            |> appendMetric "git.files_deleted" (int64 summary.Counts.Deleted) metricSource
                                            |> appendMetric "git.files_renamed" (int64 summary.Counts.Renamed) metricSource
                                            |> appendMetric "git.binary_files_changed" (int64 summary.BinaryFiles) metricSource
                                            |> appendMetric "git.lines_added" (int64 summary.LinesAdded) metricSource
                                            |> appendMetric "git.lines_deleted" (int64 summary.LinesDeleted) metricSource
                                            |> appendMetric "tests.added" (int64 summary.Tests.Added) metricSource
                                            |> appendMetric "tests.modified" (int64 summary.Tests.Modified) metricSource
                                            |> appendMetric "tests.removed" (int64 summary.Tests.Removed) metricSource
                                            |> appendMetric "documentation.files_changed" (int64 summary.DocumentationFilesChanged) metricSource

                                        let countsNode = JsonObject()
                                        countsNode["added"] <- JsonValue.Create summary.Counts.Added
                                        countsNode["modified"] <- JsonValue.Create summary.Counts.Modified
                                        countsNode["deleted"] <- JsonValue.Create summary.Counts.Deleted
                                        countsNode["renamed"] <- JsonValue.Create summary.Counts.Renamed

                                        let testsNode = JsonObject()
                                        testsNode["added"] <- JsonValue.Create summary.Tests.Added
                                        testsNode["modified"] <- JsonValue.Create summary.Tests.Modified
                                        testsNode["removed"] <- JsonValue.Create summary.Tests.Removed

                                        let node = JsonObject()
                                        node["available"] <- JsonValue.Create true
                                        node["mechanism"] <- JsonValue.Create summary.Mechanism
                                        node["startCommit"] <- JsonValue.Create summary.StartCommit
                                        node["endCommit"] <- JsonValue.Create summary.EndCommit

                                        node["branch"] <-
                                            match endSnapshot.Branch with
                                            | Some branch -> JsonValue.Create branch
                                            | None -> null

                                        node["commits"] <- JsonValue.Create summary.Commits
                                        node["counts"] <- countsNode
                                        node["linesAdded"] <- JsonValue.Create summary.LinesAdded
                                        node["linesDeleted"] <- JsonValue.Create summary.LinesDeleted
                                        node["binaryFiles"] <- JsonValue.Create summary.BinaryFiles
                                        node["tests"] <- testsNode
                                        node["documentationFilesChanged"] <- JsonValue.Create summary.DocumentationFilesChanged
                                        capabilities, (node: JsonNode), [ summary.StartCommit; summary.EndCommit ]
                                    | Error reason ->
                                        let reasonText = $"execution attribution unavailable: {reason}"
                                        let unavailableSource : CapabilitySource = { Type = "ros-git"; Name = "git-diff"; Mechanism = "precondition-check" }

                                        let capabilities =
                                            gitChangeMetricIds
                                            |> List.fold
                                                (fun acc metricId -> upsertInto acc metricId "supported-unavailable" (Some reasonText) unavailableSource)
                                                capabilitiesAfterEnding

                                        let node = JsonObject()
                                        node["available"] <- JsonValue.Create false
                                        node["reason"] <- JsonValue.Create reason
                                        capabilities, (node: JsonNode), []

                                let capabilitiesNode = JsonArray()
                                capabilitiesAfterChangeSummary |> List.iter (fun capability -> capabilitiesNode.Add(FileTelemetryExecutionRepository.capabilityNode capability: JsonNode))
                                record["capabilities"] <- capabilitiesNode
                                record["metrics"] <- metricsArray

                                match record["repository"] with
                                | :? JsonObject as repository ->
                                    repository["end"] <- endNode
                                    repository["changeSummary"] <- changeSummaryNode
                                | _ -> ()

                                if not commitsToLink.IsEmpty then
                                    match record["links"] with
                                    | :? JsonObject as links ->
                                        let existingCommits =
                                            match links["commits"] with
                                            | :? JsonArray as array ->
                                                array
                                                |> Seq.choose (function :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>()) | _ -> None)
                                                |> Seq.toList
                                            | _ -> []

                                        let merged = (existingCommits @ commitsToLink) |> List.distinct
                                        let commitsNode = JsonArray()
                                        merged |> List.iter (fun commit -> commitsNode.Add(JsonValue.Create commit: JsonNode))
                                        links["commits"] <- commitsNode
                                    | _ -> ()

                                record["status"] <- JsonValue.Create "finalized"
                                record["finalizedAt"] <- JsonValue.Create finalizedAt

                                let finalizedEventSortedForDigest = JsonObject()
                                finalizedEventSortedForDigest["finalizedAt"] <- JsonValue.Create finalizedAt
                                finalizedEventSortedForDigest["type"] <- JsonValue.Create "execution.finalized"
                                let eventId = "TEVT-" + CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact finalizedEventSortedForDigest)

                                let finalizedEvent = JsonObject()
                                finalizedEvent["type"] <- JsonValue.Create "execution.finalized"
                                finalizedEvent["occurredAt"] <- JsonValue.Create finalizedAt
                                finalizedEvent["source"] <- FileTelemetryExecutionRepository.sourceNode { Type = "ros-clock"; Name = "ros"; Mechanism = "work-lifecycle" }
                                finalizedEvent["eventId"] <- JsonValue.Create eventId

                                match record["events"] with
                                | :? JsonArray as events -> events.Add(finalizedEvent: JsonNode)
                                | _ -> record["events"] <- JsonArray(finalizedEvent :> JsonNode)

                                File.WriteAllText(file, record.ToJsonString serializerOptions + "\n")
                                Ok()
                        | _ -> Error "execution record must be a JSON object"
                with error ->
                    Error error.Message

            match lease.Release(), result with
            | Error releaseFailure, Ok() -> Error releaseFailure.Message
            | _, outcome -> outcome

    /// Mirrors production `finalizeWorkExecutions`: finalize every currently
    /// `active` execution linked to a work item. A work item this migration
    /// never observed telemetry for (or one whose executions are already
    /// finalized) is a legitimate no-op, not an error.
    let finalizeWorkExecutions (root: string) (workItemId: string) : Result<unit, string> =
        let repositoryId = FileWorkConfigRepository.readRepositoryId root

        let rec loop remaining =
            match remaining with
            | [] -> Ok()
            | executionId :: rest ->
                match finalizeOne root repositoryId executionId with
                | Error message -> Error message
                | Ok() -> loop rest

        loop (activeExecutionIds root workItemId)

    /// Mirrors production `recordTelemetryLifecycle`: appends a
    /// `work.blocked`/`work.resumed` event (deduped by its sorted-key
    /// digest `eventId`, matching production's own `digest()` exactly, not
    /// the unsorted `eventId` convention `.ros/events/events.jsonl` uses)
    /// plus its paired `agent.interruptions`/`agent.resumes` metric, to
    /// every currently-active execution linked to a work item. `work
    /// block`/`work resume` call this BEFORE resolving or creating any new
    /// telemetry execution for the same transition, matching production's
    /// own ordering exactly -- a freshly created execution never receives
    /// a lifecycle event for the transition that created it. A work item
    /// with no currently-active execution is a legitimate no-op, not an
    /// error (matching production's own empty-array iteration). Excludes
    /// `ensureRecordDefaults`' legacy-record backfill: every record this
    /// migration's own writers produce already carries the full shape it
    /// would otherwise backfill.
    let recordLifecycle (root: string) (workItemId: string) (lifecycleType: string) (occurredAt: string) (reason: string option) : Result<unit, string> =
        let recordOne (executionId: string) : Result<unit, string> =
            match RegistryLock.acquire root $"telemetry-execution:{executionId}" RegistryLock.defaultSettings with
            | Error failure -> Error failure.Message
            | Ok lease ->
                let result =
                    try
                        let file = executionFile root executionId

                        if not (File.Exists file) then
                            Ok()
                        else
                            match JsonNode.Parse(File.ReadAllText file) with
                            | :? JsonObject as record ->
                                match stringField record "status" with
                                | Some "active" ->
                                    let sortedForDigest = JsonObject()
                                    sortedForDigest["occurredAt"] <- JsonValue.Create occurredAt

                                    sortedForDigest["reason"] <-
                                        match reason with
                                        | Some value -> JsonValue.Create value
                                        | None -> null

                                    let sortedSource = JsonObject()
                                    sortedSource["mechanism"] <- JsonValue.Create "work-lifecycle"
                                    sortedSource["name"] <- JsonValue.Create "ros"
                                    sortedSource["type"] <- JsonValue.Create "ros-clock"
                                    sortedForDigest["source"] <- sortedSource
                                    sortedForDigest["type"] <- JsonValue.Create $"work.{lifecycleType}"

                                    let eventId = "TEVT-" + CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact sortedForDigest)

                                    let events =
                                        match record["events"] with
                                        | :? JsonArray as existing -> existing
                                        | _ ->
                                            let created = JsonArray()
                                            record["events"] <- created
                                            created

                                    let alreadyRecorded =
                                        events
                                        |> Seq.exists (function
                                            | :? JsonObject as node -> stringField node "eventId" = Some eventId
                                            | _ -> false)

                                    if not alreadyRecorded then
                                        let event = JsonObject()
                                        event["type"] <- JsonValue.Create $"work.{lifecycleType}"
                                        event["occurredAt"] <- JsonValue.Create occurredAt

                                        event["reason"] <-
                                            match reason with
                                            | Some value -> JsonValue.Create value
                                            | None -> null

                                        event["source"] <- FileTelemetryExecutionRepository.sourceNode { Type = "ros-clock"; Name = "ros"; Mechanism = "work-lifecycle" }
                                        event["eventId"] <- JsonValue.Create eventId
                                        events.Add(event: JsonNode)

                                        let metricId = if lifecycleType = "blocked" then "agent.interruptions" else "agent.resumes"
                                        let mechanism = if lifecycleType = "blocked" then "blocked-transition" else "resume-transition"
                                        let metricSource: CapabilitySource = { Type = "calculated"; Name = "ros-work-protocol"; Mechanism = mechanism }

                                        match FileMetricRegistryRepository.read root |> List.tryFind (fun definition -> definition.Id = metricId) with
                                        | None -> ()
                                        | Some definition ->
                                            let metricNode, upserted = FileTelemetryExecutionRepository.derivedMetricNode definition 1L metricSource occurredAt

                                            match record["metrics"] with
                                            | :? JsonArray as metrics -> metrics.Add(metricNode: JsonNode)
                                            | _ -> record["metrics"] <- JsonArray(metricNode :> JsonNode)

                                            let existingCapabilities =
                                                match record["capabilities"] with
                                                | :? JsonArray as array ->
                                                    array
                                                    |> Seq.choose (function :? JsonObject as node -> Some(parseCapability node) | _ -> None)
                                                    |> Seq.toList
                                                | _ -> []

                                            let maxHistory = FileWorkConfigRepository.readTelemetryMaxCapabilityHistoryEntries root

                                            let mergedCapabilities =
                                                if existingCapabilities |> List.exists (fun capability -> capability.MetricId = Some metricId) then
                                                    existingCapabilities
                                                    |> List.map (fun capability ->
                                                        if capability.MetricId = Some metricId then
                                                            Capability.upsert maxHistory occurredAt occurredAt occurredAt upserted.Status upserted.Reason upserted.Source capability
                                                        else
                                                            capability)
                                                else
                                                    existingCapabilities @ [ upserted ]

                                            let capabilitiesNode = JsonArray()

                                            mergedCapabilities
                                            |> List.iter (fun capability -> capabilitiesNode.Add(FileTelemetryExecutionRepository.capabilityNode capability: JsonNode))

                                            record["capabilities"] <- capabilitiesNode

                                        File.WriteAllText(file, record.ToJsonString serializerOptions + "\n")

                                    Ok()
                                | _ -> Ok()
                            | _ -> Error "execution record must be a JSON object"
                    with error ->
                        lease.Release() |> ignore
                        reraise ()

                match lease.Release(), result with
                | Error releaseFailure, Ok _ -> Error releaseFailure.Message
                | _, outcome -> outcome

        let rec loop remaining =
            match remaining with
            | [] -> Ok()
            | executionId :: rest ->
                match recordOne executionId with
                | Error message -> Error message
                | Ok() -> loop rest

        loop (activeExecutionIds root workItemId)

    /// Every execution record on disk, in ascending filename order (a local
    /// duplicate of `FileTelemetryQueryRepository.readAll`'s scan -- this
    /// migration's established convention keeps small per-module read
    /// helpers independent rather than cross-referenced, the same way each
    /// telemetry-execution module already carries its own `stringField`).
    let private readAllExecutionRecords (root: string) : JsonObject list =
        let directory = Path.Combine(root, ".ros", "telemetry", "executions")

        if not (Directory.Exists directory) then
            []
        else
            Directory.GetFiles(directory, "*.json")
            |> Array.sort
            |> Array.choose (fun file ->
                match JsonNode.Parse(File.ReadAllText file) with
                | :? JsonObject as record -> Some record
                | _ -> None)
            |> Array.toList

    /// Every work-item id currently `active` or `blocked` in
    /// `.ros/context/current.json`, mirroring production's own
    /// `loadContext(root).workItems.filter(item => item.semanticState ===
    /// "active" || item.semanticState === "blocked")` -- used only to
    /// resolve `telemetry finalize`'s target when none is given.
    let private activeOrBlockedWorkItemIds (root: string) : string list =
        let path = Path.Combine(root, ".ros", "context", "current.json")

        if not (File.Exists path) then
            []
        else
            match JsonNode.Parse(File.ReadAllText path) with
            | :? JsonObject as context ->
                match context["workItems"] with
                | :? JsonArray as items ->
                    items
                    |> Seq.choose (function
                        | :? JsonObject as item ->
                            match stringField item "id", stringField item "semanticState" with
                            | Some id, Some("active" | "blocked") -> Some id
                            | _ -> None
                        | _ -> None)
                    |> Seq.toList
                | _ -> []
            | _ -> []

    /// Mirrors production `resolveExecution`'s target resolution: an
    /// `EXE-`-prefixed target matches by exact `executionId`; any other
    /// target matches by `workItemId` regardless of status (unlike
    /// `telemetry show`'s work-item branch, an already-finalized execution
    /// is a legal match here too); no target at all requires exactly one
    /// currently active-or-blocked work item, rejecting production's exact
    /// message otherwise. `activeOnly` (production's own option of the same
    /// name, used by `telemetry record`/`ingest`/`classify` but not
    /// `finalize`) additionally drops every non-`"active"` candidate before
    /// the not-found check, and appends production's own `" or is already
    /// finalized"` suffix to that check's message. Every branch's surviving
    /// matches sort by `startedAt` and take the last (most recently
    /// started) one.
    let private resolveExecutionTarget (root: string) (target: string option) (activeOnly: bool) : Result<string, string> =
        let records = readAllExecutionRecords root

        let candidatesResult =
            match target with
            | Some value when value.StartsWith("EXE-", StringComparison.Ordinal) ->
                Ok(records |> List.filter (fun record -> stringField record "executionId" = Some value))
            | Some workItemId -> Ok(records |> List.filter (fun record -> stringField record "workItemId" = Some workItemId))
            | None ->
                match activeOrBlockedWorkItemIds root with
                | [ workItemId ] -> Ok(records |> List.filter (fun record -> stringField record "workItemId" = Some workItemId))
                | _ -> Error "telemetry target is ambiguous; provide a work-item or execution ID"

        match candidatesResult with
        | Error message -> Error message
        | Ok candidates ->
            let filtered =
                if activeOnly then
                    candidates |> List.filter (fun record -> stringField record "status" = Some "active")
                else
                    candidates

            match filtered with
            | [] ->
                let suffix = if activeOnly then " or is already finalized" else ""
                Error $"""telemetry execution '{target |> Option.defaultValue "current"}' was not found{suffix}"""
            | nonEmpty ->
                match nonEmpty |> List.sortBy (fun record -> stringField record "startedAt" |> Option.defaultValue "") |> List.last |> fun record -> stringField record "executionId" with
                | Some executionId -> Ok executionId
                | None -> Error "execution record must carry an executionId"

    let private resolveFinalizeTarget (root: string) (target: string option) : Result<string, string> =
        resolveExecutionTarget root target false

    /// Mirrors production `finalizeExecution(root, target, options)` with no
    /// `--input` (adapter-ingestion is a separately-scoped later slice, the
    /// one place it is reachable at all): resolves the target the same way
    /// production does, returns an already-finalized record untouched and
    /// unlocked (matching production's own pre-lock fast return), and
    /// otherwise finalizes it via the same `finalizeOne` mutation
    /// `finalizeWorkExecutions` uses, then returns the freshly written
    /// record for the CLI to print.
    let finalizeTarget (root: string) (target: string option) : Result<JsonObject, string> =
        match resolveFinalizeTarget root target with
        | Error message -> Error message
        | Ok executionId ->
            let file = executionFile root executionId

            let alreadyFinalized =
                match JsonNode.Parse(File.ReadAllText file) with
                | :? JsonObject as record -> stringField record "status" = Some "finalized"
                | _ -> false

            let result =
                if alreadyFinalized then
                    Ok()
                else
                    let repositoryId = FileWorkConfigRepository.readRepositoryId root
                    finalizeOne root repositoryId executionId

            match result with
            | Error message -> Error message
            | Ok() ->
                match JsonNode.Parse(File.ReadAllText file) with
                | :? JsonObject as record -> Ok record
                | _ -> Error "execution record must be a JSON object"

    /// Production's `--confidence` accepts either a number (`0`-`1`) or a
    /// free-form label (`"low"`/`"medium"`/`"high"`, or in fact any
    /// non-numeric text `Number.isFinite` rejects) and stores whichever one
    /// the CLI actually parsed -- no confidence at all is `null`, never a
    /// third "unset" marker distinct from it.
    type MetricConfidence =
        | NoConfidence
        | NumericConfidence of float
        | TextConfidence of string

    /// The CLI-reachable shape of production's `recordTelemetryMetric`
    /// input: every field `./ros telemetry record` can actually populate.
    /// `Unit`/`Currency` are explicit overrides of the registry's own
    /// `unit` (`None` means "use the registry's"); `Value` is passed through
    /// unvalidated (matching production's own `Number(rawValue)`, which
    /// silently becomes `NaN` for non-numeric input) so the finite-value
    /// check below runs at the same point production's does: after target
    /// resolution and lock acquisition, not before.
    type RecordMetricRequest =
        { MetricId: string
          Value: float
          Unit: string option
          Currency: string option
          Quality: string
          Confidence: MetricConfidence
          Scope: string
          Source: CapabilitySource
          PricingSource: string option
          PricingVersion: string option
          CollectedAt: string option }

    let private confidenceNode (confidence: MetricConfidence) : JsonNode =
        match confidence with
        | NoConfidence -> null
        | NumericConfidence value -> JsonValue.Create value
        | TextConfidence text -> JsonValue.Create text

    let private optionalStringNode (value: string option) : JsonNode =
        match value with
        | Some text -> JsonValue.Create text
        | None -> null

    let private pricingNode (source: string option) (version: string option) : JsonNode =
        match source, version with
        | None, None -> null
        | _ ->
            let node = JsonObject()
            node["source"] <- optionalStringNode source
            node["version"] <- optionalStringNode version
            node

    /// Mirrors production's `quality`-to-capability-`status` mapping inside
    /// `addMetric`: `"derived"`/`"estimated"` pass straight through, and
    /// every other quality string -- including the CLI's own `"observed"`
    /// default -- becomes `"supported-observed"`.
    let private capabilityStatusForQuality (quality: string) =
        match quality with
        | "derived" -> "derived"
        | "estimated" -> "estimated"
        | _ -> "supported-observed"

    /// Mirrors production `recordTelemetryMetric`/`normalizeMetric`/
    /// `addMetric` for the one CLI command that reaches them directly,
    /// `telemetry record`: resolves the target with `activeOnly` (an
    /// execution that finalized between the pre-lock resolve and the lock
    /// acquiring is rejected by the same re-resolve `withExecutionLock`
    /// performs, reproduced here), then -- inside the lock, after that
    /// re-resolve, exactly where production's own validation runs --
    /// rejects an unregistered metric id or a non-finite value. The
    /// `measurementId` is a SHA-256 digest of the metric's own fields with
    /// keys sorted (mirroring production's `stable()` + `digest()`, and
    /// this migration's own `derivedMetricNode`), so an identical repeated
    /// call is deduplicated by content rather than appended twice; the
    /// capability upsert (and the record's rewrite) still happens
    /// unconditionally either way, matching production's own `addMetric`
    /// exactly. `dimensions` is always `{}` and `aggregation` is always the
    /// registry's own value, since no current CLI flag can override either
    /// (production's `normalizeMetric` allows both, but nothing reaches
    /// that path with a non-default value).
    let recordMetric (root: string) (target: string option) (request: RecordMetricRequest) : Result<JsonObject, string> =
        match resolveExecutionTarget root target true with
        | Error message -> Error message
        | Ok executionId ->
            match RegistryLock.acquire root $"telemetry-execution:{executionId}" RegistryLock.defaultSettings with
            | Error failure -> Error failure.Message
            | Ok lease ->
                let result =
                    try
                        match resolveExecutionTarget root (Some executionId) true with
                        | Error message -> Error message
                        | Ok _ ->
                            match FileMetricRegistryRepository.read root |> List.tryFind (fun definition -> definition.Id = request.MetricId) with
                            | None -> Error $"unknown normalized metric '{request.MetricId}'; preserve it in raw telemetry until it is registered"
                            | Some definition when not (Double.IsFinite request.Value) ->
                                Error $"metric '{request.MetricId}' requires a finite numeric value"
                            | Some definition ->
                                let file = executionFile root executionId

                                match JsonNode.Parse(File.ReadAllText file) with
                                | :? JsonObject as record ->
                                    let unit = request.Unit |> Option.defaultValue definition.Unit
                                    let collectedAt = request.CollectedAt |> Option.defaultValue (FileTelemetryExecutionRepository.nowIso ())

                                    let sortedForDigest = JsonObject()
                                    sortedForDigest["aggregation"] <- JsonValue.Create definition.Aggregation
                                    sortedForDigest["collectedAt"] <- JsonValue.Create collectedAt
                                    sortedForDigest["confidence"] <- confidenceNode request.Confidence
                                    sortedForDigest["currency"] <- optionalStringNode request.Currency
                                    sortedForDigest["dimensions"] <- JsonObject()
                                    sortedForDigest["id"] <- JsonValue.Create request.MetricId
                                    sortedForDigest["measurementId"] <- JsonValue.Create ""
                                    sortedForDigest["pricing"] <- pricingNode request.PricingSource request.PricingVersion
                                    sortedForDigest["quality"] <- JsonValue.Create request.Quality
                                    sortedForDigest["schemaVersion"] <- JsonValue.Create "1.0.0"
                                    sortedForDigest["scope"] <- JsonValue.Create request.Scope

                                    let sortedSource = JsonObject()
                                    sortedSource["mechanism"] <- JsonValue.Create request.Source.Mechanism
                                    sortedSource["name"] <- JsonValue.Create request.Source.Name
                                    sortedSource["type"] <- JsonValue.Create request.Source.Type
                                    sortedForDigest["source"] <- sortedSource
                                    sortedForDigest["unit"] <- JsonValue.Create unit
                                    sortedForDigest["value"] <- JsonValue.Create request.Value

                                    let measurementId = "MEAS-" + CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact sortedForDigest)

                                    let metricsArray =
                                        match record["metrics"] with
                                        | :? JsonArray as array -> array
                                        | _ ->
                                            let created = JsonArray()
                                            record["metrics"] <- created
                                            created

                                    let alreadyPresent =
                                        metricsArray
                                        |> Seq.exists (function
                                            | :? JsonObject as node -> stringField node "measurementId" = Some measurementId
                                            | _ -> false)

                                    if not alreadyPresent then
                                        let metricNode = JsonObject()
                                        metricNode["measurementId"] <- JsonValue.Create measurementId
                                        metricNode["id"] <- JsonValue.Create request.MetricId
                                        metricNode["value"] <- JsonValue.Create request.Value
                                        metricNode["unit"] <- JsonValue.Create unit
                                        metricNode["currency"] <- optionalStringNode request.Currency
                                        metricNode["quality"] <- JsonValue.Create request.Quality
                                        metricNode["confidence"] <- confidenceNode request.Confidence
                                        metricNode["scope"] <- JsonValue.Create request.Scope
                                        metricNode["aggregation"] <- JsonValue.Create definition.Aggregation
                                        metricNode["dimensions"] <- JsonObject()
                                        metricNode["pricing"] <- pricingNode request.PricingSource request.PricingVersion
                                        metricNode["source"] <- FileTelemetryExecutionRepository.sourceNode request.Source
                                        metricNode["collectedAt"] <- JsonValue.Create collectedAt
                                        metricNode["schemaVersion"] <- JsonValue.Create "1.0.0"
                                        metricsArray.Add(metricNode: JsonNode)

                                    let status = capabilityStatusForQuality request.Quality
                                    let recordedAtNow = FileTelemetryExecutionRepository.nowIso ()

                                    let existingCapabilities =
                                        match record["capabilities"] with
                                        | :? JsonArray as array -> array |> Seq.choose (function :? JsonObject as node -> Some(parseCapability node) | _ -> None) |> Seq.toList
                                        | _ -> []

                                    let maxHistory = FileWorkConfigRepository.readTelemetryMaxCapabilityHistoryEntries root

                                    let mergedCapabilities =
                                        if existingCapabilities |> List.exists (fun capability -> capability.MetricId = Some request.MetricId) then
                                            existingCapabilities
                                            |> List.map (fun capability ->
                                                if capability.MetricId = Some request.MetricId then
                                                    Capability.upsert maxHistory collectedAt collectedAt recordedAtNow status (Some "normalized measurement recorded") request.Source capability
                                                else
                                                    capability)
                                        else
                                            existingCapabilities
                                            @ [ { MetricId = Some request.MetricId
                                                  ProviderField = None
                                                  Status = status
                                                  Reason = Some "normalized measurement recorded"
                                                  DiscoveredAt = collectedAt
                                                  LastAssessedAt = collectedAt
                                                  RecordedAt = recordedAtNow
                                                  Source = request.Source
                                                  History = []
                                                  HistoryOmitted = 0
                                                  Touched = false } ]

                                    let capabilitiesNode = JsonArray()
                                    mergedCapabilities |> List.iter (fun capability -> capabilitiesNode.Add(FileTelemetryExecutionRepository.capabilityNode capability: JsonNode))
                                    record["capabilities"] <- capabilitiesNode

                                    File.WriteAllText(file, record.ToJsonString serializerOptions + "\n")
                                    Ok record
                                | _ -> Error "execution record must be a JSON object"
                    with error ->
                        Error error.Message

                match lease.Release(), result with
                | Error releaseFailure, Ok _ -> Error releaseFailure.Message
                | _, outcome -> outcome

    // ---- telemetry ingest (generic adapter only; every other adapter is
    // its own future MIG-08 slice, rejected outright at the CLI layer) ----

    /// The one CLI-reachable field set of production's `adaptGeneric`'s
    /// return shape (`tools/ros_telemetry.mjs`): everything the generic
    /// adapter passes through verbatim from the input JSON, plus the
    /// handful of fields it defaults itself.
    type private AdaptedSnapshot =
        { Identity: JsonObject
          Capabilities: JsonObject list
          Metrics: JsonObject list
          Events: JsonObject list
          Classification: JsonObject option
          Scope: JsonObject option
          QualitySignals: JsonObject list
          Links: JsonObject option
          Raw: JsonNode
          MappedFields: string list
          SchemaVersion: JsonNode
          SnapshotId: string option
          CollectedAt: string
          Source: JsonObject }

    let private defaultGenericSource () : JsonObject =
        let node = JsonObject()
        node["type"] <- JsonValue.Create "runtime-output"
        node["name"] <- JsonValue.Create "generic-telemetry-envelope"
        node["mechanism"] <- JsonValue.Create "json"
        node

    /// Mirrors production `adaptGeneric`: the "generic" adapter maps
    /// nothing -- every field the input JSON itself carries passes through
    /// verbatim, defaulting only the same handful of fields production's
    /// own `??` calls default. A non-object input (e.g. the JSON Lines
    /// fallback's array, or a bare scalar) behaves exactly as production's
    /// own property access on it would: every field reads as absent.
    let private adaptGeneric (input: JsonNode) (collectedAt: string) : AdaptedSnapshot =
        let inputObject = match input with :? JsonObject as o -> Some o | _ -> None

        let field (name: string) : JsonNode option =
            inputObject |> Option.bind (fun o -> match o[name] with null -> None | v -> Some v)

        let objectField (name: string) : JsonObject option =
            match field name with
            | Some(:? JsonObject as o) -> Some o
            | _ -> None

        let arrayField (name: string) : JsonObject list =
            match field name with
            | Some(:? JsonArray as a) -> a |> Seq.choose (function :? JsonObject as o -> Some o | _ -> None) |> Seq.toList
            | _ -> []

        { Identity = objectField "identity" |> Option.defaultValue (JsonObject())
          Capabilities = arrayField "capabilities"
          Metrics = arrayField "metrics"
          Events = arrayField "events"
          Classification = objectField "classification"
          Scope = objectField "scope"
          QualitySignals = arrayField "qualitySignals"
          Links = objectField "links"
          Raw = (field "raw" |> Option.orElse (field "providerTelemetry") |> Option.defaultValue (JsonObject() :> JsonNode))
          MappedFields = []
          SchemaVersion = (field "schemaVersion" |> Option.defaultValue null)
          SnapshotId = inputObject |> Option.bind (fun o -> stringField o "snapshotId")
          CollectedAt = (inputObject |> Option.bind (fun o -> stringField o "collectedAt")) |> Option.defaultValue collectedAt
          Source = objectField "source" |> Option.defaultValue (defaultGenericSource ()) }

    /// The six usage fields production's own `adaptOpenAICodex` maps, in
    /// its own object-literal (and therefore iteration) order.
    let private codexUsageFields =
        [ "input_tokens", "tokens.input"
          "output_tokens", "tokens.output"
          "cached_input_tokens", "tokens.cached_input"
          "cache_write_input_tokens", "tokens.cache_write"
          "reasoning_output_tokens", "tokens.reasoning"
          "total_tokens", "tokens.total" ]

    /// Mirrors production `adaptOpenAICodex`: maps a Codex `exec --json`
    /// event stream (or a single event object) to normalized token/context
    /// metrics. `completed` -- entries with `type === "turn.completed"` or
    /// any `usage` object at all -- each contribute a fixed capability
    /// declaration for all six usage fields (present or not) plus a metric
    /// only for the fields actually present, matching production's own
    /// per-entry, per-field iteration order exactly; `context.window_size`
    /// is the one field with a metric but deliberately no paired
    /// capability declaration, matching production's own omission.
    /// `identity.model` resolves from the *last* record (searched in
    /// reverse) that carries a truthy `server_model` or `model`, regardless
    /// of whether that record was itself "completed".
    let private adaptOpenAICodex (input: JsonNode) (collectedAt: string) : AdaptedSnapshot =
        let records: JsonObject list =
            match input with
            | :? JsonArray as array -> array |> Seq.choose (function :? JsonObject as o -> Some o | _ -> None) |> Seq.toList
            | :? JsonObject as single ->
                match single["events"] with
                | :? JsonArray as array -> array |> Seq.choose (function :? JsonObject as o -> Some o | _ -> None) |> Seq.toList
                | _ -> [ single ]
            | _ -> []

        let completed = records |> List.filter (fun entry -> stringField entry "type" = Some "turn.completed" || hasTruthyField entry "usage")

        let runtimeSource =
            let node = JsonObject()
            node["type"] <- JsonValue.Create "runtime-output"
            node["name"] <- JsonValue.Create "codex-json"
            node["mechanism"] <- JsonValue.Create "codex-exec-jsonl"
            node["provider"] <- JsonValue.Create "openai"
            node["runtime"] <- JsonValue.Create "codex"
            node

        let usageOf (entry: JsonObject) : JsonObject option =
            match entry["usage"] with
            | :? JsonObject as usage -> Some usage
            | _ -> None

        let numberOf (usage: JsonObject option) (field: string) : float option =
            usage
            |> Option.bind (fun u ->
                match u[field] with
                | :? JsonValue as v when v.GetValueKind() = JsonValueKind.Number ->
                    match v.TryGetValue<float>() with
                    | true, parsed -> Some parsed
                    | _ -> None
                | _ -> None)

        let capabilityNode metricId present : JsonObject =
            let node = JsonObject()
            node["metricId"] <- JsonValue.Create(metricId: string)
            node["status"] <- JsonValue.Create(if present then "supported-observed" else "supported-unavailable")

            node["reason"] <-
                JsonValue.Create(
                    if present then
                        "provider field observed"
                    else
                        "adapter recognizes the field but it was unavailable in this snapshot"
                )

            node["source"] <- runtimeSource.DeepClone()
            node["discoveredAt"] <- JsonValue.Create(collectedAt: string)
            node

        let metricNode metricId (value: float) at scope (dimensions: JsonObject option) : JsonObject =
            let node = JsonObject()
            node["id"] <- JsonValue.Create(metricId: string)
            node["value"] <- JsonValue.Create value
            node["collectedAt"] <- JsonValue.Create(at: string)
            node["source"] <- runtimeSource.DeepClone()
            node["scope"] <- JsonValue.Create(scope: string)

            match dimensions with
            | Some d -> node["dimensions"] <- d
            | None -> ()

            node

        let capabilities = ResizeArray<JsonObject>()
        let metrics = ResizeArray<JsonObject>()

        completed
        |> List.iteri (fun index entry ->
            let at = stringField entry "timestamp" |> Option.defaultValue collectedAt
            let usage = usageOf entry

            for field, metricId in codexUsageFields do
                let value = numberOf usage field
                capabilities.Add(capabilityNode metricId value.IsSome)

                match value with
                | Some v ->
                    let dimensions = JsonObject()
                    dimensions["turnIndex"] <- JsonValue.Create(index: int)
                    metrics.Add(metricNode metricId v at "turn" (Some dimensions))
                | None -> ()

            match numberOf usage "model_context_window" with
            | Some windowSize -> metrics.Add(metricNode "context.window_size" windowSize at "session" None)
            | None -> ())

        let identityEvent =
            records
            |> List.rev
            |> List.tryFind (fun entry -> hasTruthyField entry "server_model" || hasTruthyField entry "model")

        let model =
            identityEvent
            |> Option.bind (fun entry -> stringField entry "server_model" |> Option.orElse (stringField entry "model"))

        let identity = JsonObject()
        identity["provider"] <- JsonValue.Create "openai"
        identity["runtime"] <- JsonValue.Create "codex"

        identity["model"] <-
            match model with
            | Some m -> JsonValue.Create m
            | None -> null

        let events =
            completed
            |> List.map (fun entry ->
                let node = JsonObject()
                node["type"] <- JsonValue.Create "agent.turn.completed"
                node["occurredAt"] <- JsonValue.Create(stringField entry "timestamp" |> Option.defaultValue collectedAt: string)
                node["source"] <- runtimeSource.DeepClone()
                node)

        { Identity = identity
          Capabilities = capabilities |> List.ofSeq
          Metrics = metrics |> List.ofSeq
          Events = events
          Classification = None
          Scope = None
          QualitySignals = []
          Links = None
          Raw = input
          MappedFields =
            [ "type"; "timestamp"; "server_model"; "model"; "usage.model_context_window" ]
            @ (codexUsageFields |> List.map (fun (field, _) -> $"usage.{field}"))
          SchemaVersion = (match input with :? JsonObject as o -> (match o["schemaVersion"] with null -> null | v -> v) | _ -> null)
          SnapshotId = None
          CollectedAt = collectedAt
          Source = runtimeSource }

    /// Precompiled once, mirroring `rawRedactedKeyPattern`'s own convention,
    /// rather than reconstructed on every `toolCategoryMetric`/`adaptHook`
    /// call.
    let private toolCategoryPatterns =
        [ Regex(@"bash|shell|command|terminal|powershell"), "tool.shell_commands"
          Regex(@"read|view|open_file"), "tool.file_reads"
          Regex(@"write|edit|patch|replace|create_file"), "tool.file_writes"
          Regex(@"search|grep|find|glob"), "tool.searches"
          Regex(@"web|browser|fetch|chrome"), "tool.web_activity"
          Regex(@"git|repository"), "tool.repository_operations"
          Regex(@"test"), "tool.test_executions"
          Regex(@"build|compile"), "tool.build_executions"
          Regex(@"deploy"), "tool.deployments"
          Regex(@"database|sql|query"), "tool.database_operations"
          Regex(@"api|http|mcp"), "tool.api_operations" ]

    let private hookNamePattern (pattern: string) = Regex(pattern, RegexOptions.IgnoreCase)
    let private postToolUsePattern = hookNamePattern "posttooluse|aftertool|toolresult"
    let private subagentStartPattern = hookNamePattern "subagentstart"
    let private postCompactPattern = hookNamePattern "postcompact"
    let private permissionRequestPattern = hookNamePattern "permissionrequest"
    let private permissionDeniedPattern = hookNamePattern "permissiondenied"

    /// Mirrors production `toolCategoryMetric`: buckets a tool name into
    /// one of eleven categories by substring match (first pattern wins, in
    /// this exact order), or a general fallback when nothing matches.
    let private toolCategoryMetric (toolName: string) : string =
        let value = toolName.ToLowerInvariant()

        toolCategoryPatterns
        |> List.tryPick (fun (pattern, category) -> if pattern.IsMatch value then Some category else None)
        |> Option.defaultValue "tool.external_service_calls"

    /// Mirrors production `adaptHook`, shared by all three lifecycle-hook
    /// adapters (`anthropic-claude-hook`, `google-gemini-hook`,
    /// `github-copilot-hook`), distinguished only by `identityDefaults`
    /// (`provider`/`runtime`). Always emits exactly one lifecycle event
    /// (`runtime.<hook-name-kebab-lowercased>`); metrics are conditional on
    /// independent (not mutually exclusive) regex matches against the hook
    /// event name, matching production's own independent `if` checks
    /// exactly. Unlike `adaptOpenAICodex`'s fixed six-field capability
    /// declaration, every capability here is derived 1:1 from whichever
    /// metrics actually fired -- there is no "field recognized but absent"
    /// declaration for a hook adapter at all.
    let private adaptHook (input: JsonNode) (collectedAt: string) (identityProvider: string) (identityRuntime: string) : AdaptedSnapshot =
        let inputObject = match input with :? JsonObject as o -> Some o | _ -> None

        let field (name: string) : JsonObject -> JsonNode option =
            fun o -> match o[name] with null -> None | v -> Some v

        let stringOf (name: string) : string option =
            inputObject |> Option.bind (fun o -> stringField o name)

        let hookName =
            stringOf "hook_event_name" |> Option.orElse (stringOf "hookEventName") |> Option.orElse (stringOf "event") |> Option.defaultValue "unknown"

        let toolName =
            stringOf "tool_name"
            |> Option.orElse (stringOf "toolName")
            |> Option.orElse (
                inputObject
                |> Option.bind (fun o -> field "tool" o)
                |> Option.bind (function :? JsonObject as t -> stringField t "name" | _ -> None)
            )
            |> Option.defaultValue "unknown"

        let at = (inputObject |> Option.bind (fun o -> stringField o "timestamp")) |> Option.defaultValue collectedAt

        let runtimeSource =
            let node = JsonObject()
            node["type"] <- JsonValue.Create "runtime-hook"
            node["name"] <- JsonValue.Create $"{identityRuntime}-hook"
            node["mechanism"] <- JsonValue.Create "lifecycle-hook-json"
            node["provider"] <- JsonValue.Create identityProvider
            node["runtime"] <- JsonValue.Create identityRuntime
            node

        let plainMetric metricId (value: float) : JsonObject =
            let node = JsonObject()
            node["id"] <- JsonValue.Create(metricId: string)
            node["value"] <- JsonValue.Create value
            node["collectedAt"] <- JsonValue.Create(at: string)
            node["source"] <- runtimeSource.DeepClone()
            node

        let toolScopedMetric metricId : JsonObject =
            let node = plainMetric metricId 1.0
            node["scope"] <- JsonValue.Create "tool"
            let dimensions = JsonObject()
            dimensions["toolType"] <- JsonValue.Create(toolName: string)
            node["dimensions"] <- dimensions
            node

        let metrics = ResizeArray<JsonObject>()
        let hookNameLower = hookName.ToLowerInvariant()

        if postToolUsePattern.IsMatch hookNameLower then
            metrics.Add(toolScopedMetric "tool.calls")
            metrics.Add(toolScopedMetric (toolCategoryMetric toolName))

            let toolResponseError =
                inputObject
                |> Option.bind (fun o -> field "tool_response" o)
                |> Option.map (function
                    | :? JsonObject as r -> hasTruthyField r "error"
                    | _ -> false)
                |> Option.defaultValue false

            let successIsFalse =
                inputObject |> Option.bind (fun o -> boolField o "success") = Some false

            let hasError = (inputObject |> Option.map (fun o -> hasTruthyField o "error") |> Option.defaultValue false) || toolResponseError || successIsFalse

            if hasError then
                metrics.Add(toolScopedMetric "tool.failures")

        if subagentStartPattern.IsMatch hookNameLower then
            metrics.Add(plainMetric "agent.subagents_spawned" 1.0)

        if postCompactPattern.IsMatch hookNameLower then
            metrics.Add(plainMetric "context.compactions" 1.0)

        if permissionRequestPattern.IsMatch hookNameLower then
            metrics.Add(plainMetric "agent.approvals_requested" 1.0)

        if permissionDeniedPattern.IsMatch hookNameLower then
            metrics.Add(plainMetric "agent.approvals_denied" 1.0)

        let capabilities =
            metrics
            |> Seq.map (fun m ->
                let node = JsonObject()
                node["metricId"] <- (stringField m "id" |> Option.get |> JsonValue.Create :> JsonNode)
                node["status"] <- JsonValue.Create "supported-observed"
                node["reason"] <- JsonValue.Create "provider field observed"
                node["source"] <- runtimeSource.DeepClone()
                node["discoveredAt"] <- JsonValue.Create(stringField m "collectedAt" |> Option.get: string)
                node)
            |> List.ofSeq

        let eventTypeSuffix = Regex.Replace(hookName, @"[^A-Za-z0-9]+", "-").ToLowerInvariant()

        let event =
            let node = JsonObject()
            node["type"] <- JsonValue.Create $"runtime.{eventTypeSuffix}"
            node["occurredAt"] <- JsonValue.Create(at: string)
            node["source"] <- runtimeSource.DeepClone()
            node

        let identity = JsonObject()
        identity["provider"] <- JsonValue.Create identityProvider
        identity["runtime"] <- JsonValue.Create identityRuntime

        identity["sessionId"] <-
            match stringOf "session_id" with
            | Some s -> JsonValue.Create s
            | None -> null

        let model =
            match inputObject |> Option.bind (fun o -> field "model" o) with
            | Some(:? JsonObject as m) -> stringField m "id"
            | Some(:? JsonValue as v) when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
            | _ -> None

        identity["model"] <-
            match model with
            | Some m -> JsonValue.Create m
            | None -> null

        identity["agentId"] <-
            match stringOf "agent_type" with
            | Some s -> JsonValue.Create s
            | None -> null

        { Identity = identity
          Capabilities = capabilities
          Metrics = metrics |> List.ofSeq
          Events = [ event ]
          Classification = None
          Scope = None
          QualitySignals = []
          Links = None
          Raw = input
          MappedFields =
            [ "hook_event_name"
              "hookEventName"
              "event"
              "tool_name"
              "toolName"
              "tool.name"
              "timestamp"
              "session_id"
              "model"
              "model.id"
              "agent_type"
              "error"
              "tool_response.error"
              "success" ]
          SchemaVersion = (inputObject |> Option.bind (fun o -> match o["schemaVersion"] with null -> None | v -> Some v) |> Option.defaultValue null)
          SnapshotId = None
          CollectedAt = at
          Source = runtimeSource }

    let private rawRedactedKeyPattern =
        Regex(
            @"^(?:authorization|cookie|set-cookie|password|passwd|secret|credential|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|prompt|prompts|messages?|content|tool[_-]?input|tool[_-]?response|request|response|stdout|stderr|command|full[_-]?command|transcript[_-]?path|cwd|current[_-]?dir|project[_-]?dir|workspace[_-]?path|file[_-]?path|email|user\.email)$",
            RegexOptions.IgnoreCase
        )

    let private rawSensitiveSegmentPattern =
        Regex(
            @"(?:^|[._-])(?:authorization|password|passwd|secret|credential|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|email)(?:$|[._-])",
            RegexOptions.IgnoreCase
        )

    let private maxRawStringLength = 2048

    /// Mirrors production `sanitizeRaw`: redacts any key matching either
    /// sensitive-key pattern (replacing its value, never recursing into
    /// it), truncates an over-long string leaf, and otherwise rebuilds the
    /// tree unchanged -- always via fresh nodes (`DeepClone` for scalars),
    /// since a `JsonNode` already attached elsewhere cannot be reattached.
    let rec private sanitizeRawNode (value: JsonNode) (currentPath: string) (redactions: ResizeArray<string>) : JsonNode =
        match value with
        | null -> null
        | :? JsonArray as array ->
            let result = JsonArray()
            array |> Seq.iteri (fun index item -> result.Add(sanitizeRawNode item $"{currentPath}[{index}]" redactions))
            result
        | :? JsonObject as obj ->
            let result = JsonObject()

            for entry in obj |> Seq.toList do
                let childPath = $"{currentPath}.{entry.Key}"

                if rawRedactedKeyPattern.IsMatch(entry.Key) || rawSensitiveSegmentPattern.IsMatch(entry.Key) then
                    result[entry.Key] <- JsonValue.Create "[REDACTED_BY_ROS]"
                    redactions.Add childPath
                else
                    result[entry.Key] <- sanitizeRawNode entry.Value childPath redactions

            result
        | :? JsonValue as leaf when leaf.GetValueKind() = JsonValueKind.String ->
            let text = leaf.GetValue<string>()

            if text.Length > maxRawStringLength then
                JsonValue.Create $"[TRUNCATED_BY_ROS length={text.Length}]"
            else
                JsonValue.Create text
        | _ -> value.DeepClone()

    /// Mirrors production `leafPaths`: every leaf value's path (array
    /// indices normalized to `[]`), deduplicated and sorted -- called on
    /// the already-sanitized payload, so a redacted leaf's path is still
    /// recorded (its value is now a plain string, still a leaf).
    let rec private leafPathsInto (value: JsonNode) (prefix: string) (result: ResizeArray<string>) =
        match value with
        | :? JsonArray as array -> array |> Seq.iteri (fun index item -> leafPathsInto item $"{prefix}[{index}]" result)
        | :? JsonObject as obj ->
            for entry in obj do
                leafPathsInto entry.Value $"{prefix}.{entry.Key}" result
        | _ -> result.Add(Regex.Replace(prefix, @"\[\d+\]", "[]"))

    let private leafPaths (value: JsonNode) : string list =
        let result = ResizeArray<string>()
        leafPathsInto value "$" result
        result |> Seq.distinct |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> Seq.toList

    let private alreadyIngested (record: JsonObject) (snapshotId: string) : bool =
        let inRawTelemetry =
            match record["rawTelemetry"] with
            | :? JsonArray as array -> array |> Seq.exists (function :? JsonObject as node -> stringField node "snapshotId" = Some snapshotId | _ -> false)
            | _ -> false

        let inEvents =
            match record["events"] with
            | :? JsonArray as array ->
                array
                |> Seq.exists (function
                    | :? JsonObject as node -> stringField node "type" = Some "telemetry.snapshot.ingested" && stringField node "snapshotId" = Some snapshotId
                    | _ -> false)
            | _ -> false

        inRawTelemetry || inEvents

    /// Mirrors production's consistency guard inside `ingestAdapted`: a
    /// snapshot that both reports a value for a metric AND declares every
    /// capability for that same metric id unavailable/unsupported
    /// contradicts itself, rejected before any mutation.
    let private declaredUnavailableConflict (metrics: JsonObject list) (capabilities: JsonObject list) : string option =
        let metricIds = metrics |> List.choose (fun m -> stringField m "id") |> List.distinct

        metricIds
        |> List.tryPick (fun metricId ->
            let declared = capabilities |> List.filter (fun c -> stringField c "metricId" = Some metricId)

            if
                not declared.IsEmpty
                && declared |> List.forall (fun c -> stringField c "status" = Some "supported-unavailable" || stringField c "status" = Some "unsupported")
            then
                Some metricId
            else
                None)

    let private capabilitySourceFromNode (node: JsonNode) : CapabilitySource =
        match node with
        | :? JsonObject as o -> parseSource o
        | _ -> { Type = ""; Name = ""; Mechanism = "" }

    /// Mirrors production `upsertCapability`'s general (metricId,
    /// providerField)-keyed merge, shared by every capability write this
    /// increment produces: a directly-declared ingest capability, a
    /// discovered-but-unmapped raw field, and a metric's own upsert.
    let private mergeCapabilityByKey
        (root: string)
        (record: JsonObject)
        (metricId: string option)
        (providerField: string option)
        (status: string)
        (reason: string option)
        (discoveredAt: string)
        (lastAssessedAt: string)
        (source: CapabilitySource)
        : unit =
        let recordedAtNow = FileTelemetryExecutionRepository.nowIso ()
        let maxHistory = FileWorkConfigRepository.readTelemetryMaxCapabilityHistoryEntries root

        let existing =
            match record["capabilities"] with
            | :? JsonArray as array -> array |> Seq.choose (function :? JsonObject as node -> Some(parseCapability node) | _ -> None) |> Seq.toList
            | _ -> []

        let matchesKey (capability: Capability) = capability.MetricId = metricId && capability.ProviderField = providerField

        let merged =
            if existing |> List.exists matchesKey then
                existing
                |> List.map (fun capability ->
                    if matchesKey capability then
                        Capability.upsert maxHistory discoveredAt lastAssessedAt recordedAtNow status reason source capability
                    else
                        capability)
            else
                existing
                @ [ { MetricId = metricId
                      ProviderField = providerField
                      Status = status
                      Reason = reason
                      DiscoveredAt = discoveredAt
                      LastAssessedAt = lastAssessedAt
                      RecordedAt = recordedAtNow
                      Source = source
                      History = []
                      HistoryOmitted = 0
                      Touched = false } ]

        let node = JsonArray()
        merged |> List.iter (fun capability -> node.Add(FileTelemetryExecutionRepository.capabilityNode capability: JsonNode))
        record["capabilities"] <- node

    /// A directly-declared ingest capability (`adapted.capabilities`, an
    /// arbitrary JSON object): production's `capability.discoveredAt ??
    /// nowIso()`/`capability.lastAssessedAt ?? capability.discoveredAt ??
    /// nowIso()`/`capability.source ?? source("agent-report",
    /// "telemetry-capability", "explicit")` fallbacks, none of which use
    /// `adapted.collectedAt` at all (unlike every other timestamp this
    /// increment writes).
    let private mergeDeclaredCapability (root: string) (record: JsonObject) (capabilityNode: JsonObject) : unit =
        let metricId = stringField capabilityNode "metricId"
        let providerField = stringField capabilityNode "providerField"
        let status = stringField capabilityNode "status" |> Option.defaultValue ""
        let reason = stringField capabilityNode "reason"
        let discoveredAt = stringField capabilityNode "discoveredAt" |> Option.defaultValue (FileTelemetryExecutionRepository.nowIso ())
        let lastAssessedAt = stringField capabilityNode "lastAssessedAt" |> Option.defaultValue discoveredAt

        let source =
            match capabilityNode["source"] with
            | :? JsonObject as s -> parseSource s
            | _ -> { Type = "agent-report"; Name = "telemetry-capability"; Mechanism = "explicit" }

        mergeCapabilityByKey root record metricId providerField status reason discoveredAt lastAssessedAt source

    /// Mirrors production `normalizeMetric`/`addMetric` for a metric item
    /// that arrives as an arbitrary JSON object -- as `telemetry ingest`'s
    /// generic adapter passes every `metrics[]` entry through verbatim,
    /// unlike `telemetry record`'s narrow CLI-typed shape, a metric here
    /// may carry any of its own `unit`/`currency`/`quality`/`confidence`/
    /// `scope`/`aggregation`/`dimensions`/`pricing`/`measurementId`/
    /// `source`/`collectedAt` fields, each honored verbatim over the
    /// registry/defaults fallback chain exactly as production's
    /// field-by-field `??` does. Appends the normalized metric (deduped by
    /// content-addressed `measurementId`) and always upserts its
    /// capability, whether or not the metric itself was a duplicate --
    /// matching production's own `addMetric` exactly.
    let private normalizeAndAppendIngestedMetric
        (root: string)
        (record: JsonObject)
        (metricNode: JsonObject)
        (defaultSource: JsonNode)
        (defaultCollectedAt: string)
        : Result<unit, string> =
        match stringField metricNode "id" with
        | None -> Error "metric requires an 'id'"
        | Some metricId ->
            match FileMetricRegistryRepository.read root |> List.tryFind (fun definition -> definition.Id = metricId) with
            | None -> Error $"unknown normalized metric '{metricId}'; preserve it in raw telemetry until it is registered"
            | Some definition ->
                let numericValue =
                    match metricNode["value"] with
                    | :? JsonValue as v when v.GetValueKind() = JsonValueKind.Number ->
                        match v.TryGetValue<float>() with
                        | true, parsed -> Some parsed
                        | _ -> None
                    | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String ->
                        let text = v.GetValue<string>()

                        if text.Trim() = "" then
                            None
                        else
                            match Double.TryParse(text, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                            | true, parsed -> Some parsed
                            | _ -> None
                    | _ -> None

                match numericValue with
                | None -> Error $"metric '{metricId}' requires a finite numeric value"
                | Some value when not (Double.IsFinite value) -> Error $"metric '{metricId}' requires a finite numeric value"
                | Some value ->
                    let unit = stringField metricNode "unit" |> Option.defaultValue definition.Unit
                    let currency = stringField metricNode "currency"
                    let quality = stringField metricNode "quality" |> Option.defaultValue "observed"
                    let scope = stringField metricNode "scope" |> Option.defaultValue "execution"
                    let aggregation = stringField metricNode "aggregation" |> Option.defaultValue definition.Aggregation
                    let collectedAt = stringField metricNode "collectedAt" |> Option.defaultValue defaultCollectedAt
                    let dimensionsValue = match metricNode["dimensions"] with :? JsonObject as o -> (o: JsonNode) | _ -> JsonObject()
                    let sourceValue = match metricNode["source"] with null -> defaultSource | v -> v

                    let normalized = JsonObject()
                    normalized["measurementId"] <- JsonValue.Create ""
                    normalized["id"] <- JsonValue.Create metricId
                    normalized["value"] <- JsonValue.Create value
                    normalized["unit"] <- JsonValue.Create unit
                    normalized["currency"] <- (match currency with Some c -> JsonValue.Create c | None -> null)
                    normalized["quality"] <- JsonValue.Create quality

                    normalized["confidence"] <-
                        (match metricNode["confidence"] with
                         | null -> null
                         | v -> v.DeepClone())

                    normalized["scope"] <- JsonValue.Create scope
                    normalized["aggregation"] <- JsonValue.Create aggregation
                    normalized["dimensions"] <- dimensionsValue.DeepClone()

                    normalized["pricing"] <-
                        (match metricNode["pricing"] with
                         | null -> null
                         | v -> v.DeepClone())

                    normalized["source"] <- sourceValue.DeepClone()
                    normalized["collectedAt"] <- JsonValue.Create collectedAt
                    normalized["schemaVersion"] <- JsonValue.Create "1.0.0"

                    let measurementId =
                        match stringField metricNode "measurementId" with
                        | Some existing when existing <> "" -> existing
                        | _ -> "MEAS-" + CanonicalJson.contentDigest 24 normalized

                    normalized["measurementId"] <- JsonValue.Create measurementId

                    let metricsArray =
                        match record["metrics"] with
                        | :? JsonArray as array -> array
                        | _ ->
                            let created = JsonArray()
                            record["metrics"] <- created
                            created

                    let alreadyPresent =
                        metricsArray
                        |> Seq.exists (function
                            | :? JsonObject as node -> stringField node "measurementId" = Some measurementId
                            | _ -> false)

                    if not alreadyPresent then
                        metricsArray.Add(normalized: JsonNode)

                    let status =
                        match quality with
                        | "derived" -> "derived"
                        | "estimated" -> "estimated"
                        | _ -> "supported-observed"

                    mergeCapabilityByKey
                        root
                        record
                        (Some metricId)
                        None
                        status
                        (Some "normalized measurement recorded")
                        collectedAt
                        collectedAt
                        (capabilitySourceFromNode sourceValue)

                    Ok()

    let private syntheticMetricNode (metricId: string) (value: int) (source: CapabilitySource) (collectedAt: string) : JsonObject =
        let node = JsonObject()
        node["id"] <- JsonValue.Create metricId
        node["value"] <- JsonValue.Create(float value)
        node["quality"] <- JsonValue.Create "derived"
        node["collectedAt"] <- JsonValue.Create collectedAt
        node["source"] <- FileTelemetryExecutionRepository.sourceNode source
        node

    let private mergeIdentity (record: JsonObject) (incoming: JsonObject) : unit =
        match record["identity"] with
        | :? JsonObject as identity ->
            for entry in incoming |> Seq.toList do
                match entry.Value with
                | null -> ()
                | value -> identity[entry.Key] <- value.DeepClone()
        | _ -> ()

    /// A plain JS object-spread merge (`{...record.X, ...adapted.X}`),
    /// unlike identity's `mergeObject`: an incoming key's value is always
    /// set verbatim, including an explicit JSON `null` (spread never skips
    /// `null`, only `mergeObject`'s own explicit check does).
    let private shallowMerge (record: JsonObject) (key: string) (incoming: JsonObject option) : unit =
        match incoming with
        | None -> ()
        | Some incomingObject ->
            let target =
                match record[key] with
                | :? JsonObject as o -> o
                | _ ->
                    let created = JsonObject()
                    record[key] <- created
                    created

            for entry in incomingObject |> Seq.toList do
                target[entry.Key] <-
                    match entry.Value with
                    | null -> null
                    | v -> v.DeepClone()

    let private mergeLinks (record: JsonObject) (incoming: JsonObject option) : unit =
        match incoming with
        | None -> ()
        | Some incomingLinks ->
            let target =
                match record["links"] with
                | :? JsonObject as o -> o
                | _ ->
                    let created = JsonObject()
                    record["links"] <- created
                    created

            for entry in incomingLinks |> Seq.toList do
                match entry.Value with
                | :? JsonArray as incomingArray ->
                    let existingArray =
                        match target[entry.Key] with
                        | :? JsonArray as array -> array |> Seq.toList
                        | _ -> []

                    let seen = Collections.Generic.HashSet<string>()
                    let merged = JsonArray()

                    for item in existingArray @ (incomingArray |> Seq.toList) do
                        let key = match item with null -> "null" | v -> v.ToJsonString()
                        if seen.Add key then merged.Add(item.DeepClone(): JsonNode)

                    target[entry.Key] <- merged
                | value ->
                    target[entry.Key] <-
                        match value with
                        | null -> null
                        | v -> v.DeepClone()

    let private mergeIngestedEvent (record: JsonObject) (eventNode: JsonObject) : unit =
        let eventId =
            match stringField eventNode "eventId" with
            | Some existing -> existing
            | None -> "TEVT-" + CanonicalJson.contentDigest 24 eventNode

        let eventsArray =
            match record["events"] with
            | :? JsonArray as array -> array
            | _ ->
                let created = JsonArray()
                record["events"] <- created
                created

        let alreadyPresent =
            eventsArray
            |> Seq.exists (function
                | :? JsonObject as node -> stringField node "eventId" = Some eventId
                | _ -> false)

        if not alreadyPresent then
            let normalized = eventNode.DeepClone() :?> JsonObject
            normalized["eventId"] <- JsonValue.Create eventId
            eventsArray.Add(normalized: JsonNode)

    let private mergeQualitySignal (record: JsonObject) (signalNode: JsonObject) : unit =
        let signalId =
            match stringField signalNode "signalId" with
            | Some existing -> existing
            | None -> "QS-" + CanonicalJson.contentDigest 24 signalNode

        let signalsArray =
            match record["qualitySignals"] with
            | :? JsonArray as array -> array
            | _ ->
                let created = JsonArray()
                record["qualitySignals"] <- created
                created

        let alreadyPresent =
            signalsArray
            |> Seq.exists (function
                | :? JsonObject as node -> stringField node "signalId" = Some signalId
                | _ -> false)

        if not alreadyPresent then
            let normalized = signalNode.DeepClone() :?> JsonObject
            normalized["signalId"] <- JsonValue.Create signalId
            signalsArray.Add(normalized: JsonNode)

    /// Mirrors production `ingestAdapted`, restricted to what the generic
    /// adapter ever produces: snapshot dedup (an already-ingested
    /// `snapshotId`, tracked in either `rawTelemetry` or the ingestion
    /// event log, returns the record untouched), the declared-unavailable
    /// consistency guard, identity/capability/metric/event/classification/
    /// scope/links/quality-signal merging, raw payload redaction plus the
    /// byte-budget retention policy (four outcomes, exactly matching
    /// production's own branch order), unknown-field capability discovery,
    /// the three derived quality metrics, the ingestion event, and
    /// provenance-source tracking (deduped by content digest).
    let private ingestAdaptedGeneric (root: string) (executionId: string) (adapted: AdaptedSnapshot) : Result<JsonObject, string> =
        let file = executionFile root executionId

        match JsonNode.Parse(File.ReadAllText file) with
        | :? JsonObject as record ->
            // `ingestTarget` always populates `SnapshotId` before calling here
            // (either the adapter's own `input.snapshotId`, or production's
            // `ingestTelemetry`-level `{adapter, input}` digest) -- production's
            // OWN `ingestAdapted`-level fallback (`{adapter, raw: adapted.raw}`)
            // is unreachable through this call path and is not reproduced here.
            let snapshotId = adapted.SnapshotId |> Option.defaultWith (fun () -> failwith "snapshotId must be populated before ingestAdaptedGeneric runs")

            if alreadyIngested record snapshotId then
                Ok record
            else
                match declaredUnavailableConflict adapted.Metrics adapted.Capabilities with
                | Some metricId -> Error $"telemetry snapshot '{snapshotId}' records metric '{metricId}' while declaring it unavailable or unsupported"
                | None ->
                    mergeIdentity record adapted.Identity
                    adapted.Capabilities |> List.iter (mergeDeclaredCapability root record)

                    let metricResult =
                        adapted.Metrics
                        |> List.fold
                            (fun outcome metricNode ->
                                match outcome with
                                | Error _ -> outcome
                                | Ok() -> normalizeAndAppendIngestedMetric root record metricNode (adapted.Source: JsonNode) adapted.CollectedAt)
                            (Ok())

                    match metricResult with
                    | Error message -> Error message
                    | Ok() ->
                        adapted.Events |> List.iter (mergeIngestedEvent record)
                        shallowMerge record "classification" adapted.Classification
                        shallowMerge record "scope" adapted.Scope
                        mergeLinks record adapted.Links
                        adapted.QualitySignals |> List.iter (mergeQualitySignal record)

                        let allowRaw = FileWorkConfigRepository.readTelemetryAllowRawTelemetry root
                        let maxPayloadBytes = FileWorkConfigRepository.readTelemetryMaxRawPayloadBytes root
                        let maxSnapshotsPerExecution = FileWorkConfigRepository.readTelemetryMaxRawSnapshotsPerExecution root
                        let maxBytesPerExecution = FileWorkConfigRepository.readTelemetryMaxRawBytesPerExecution root

                        let redactions = ResizeArray<string>()
                        let payload = sanitizeRawNode adapted.Raw "$" redactions
                        let fields = leafPaths payload
                        let mappedSet = Set.ofList adapted.MappedFields
                        let canonicalField (field: string) = Regex.Replace(field, @"^\$(?:\[\])?\.?", "")

                        let discoveredFields =
                            fields
                            |> List.filter (fun field ->
                                let canonical = canonicalField field
                                not (mappedSet |> Set.exists (fun known -> canonical = known || canonical.EndsWith($".{known}"))))

                        let serializedBytes = Text.Encoding.UTF8.GetByteCount(CanonicalJson.serializeCompact payload)

                        let existingRawTelemetry =
                            match record["rawTelemetry"] with
                            | :? JsonArray as array -> array |> Seq.choose (function :? JsonObject as node -> Some node | _ -> None) |> Seq.toList
                            | _ -> []

                        let retainedRawBytes =
                            existingRawTelemetry
                            |> List.sumBy (fun snapshot ->
                                match snapshot["payload"] with
                                | null -> 0
                                | payloadNode -> Text.Encoding.UTF8.GetByteCount(CanonicalJson.serializeCompact payloadNode))

                        let rawRetentionStatus, rawRetentionReason =
                            if not allowRaw then "omitted", "repository-policy-disabled"
                            elif serializedBytes > maxPayloadBytes then "omitted", "snapshot-byte-limit"
                            elif existingRawTelemetry.Length >= maxSnapshotsPerExecution then "omitted", "execution-snapshot-limit"
                            elif retainedRawBytes + serializedBytes > maxBytesPerExecution then "omitted", "execution-byte-limit"
                            else "retained", ""

                        if rawRetentionStatus = "retained" then
                            let rawTelemetryArray =
                                match record["rawTelemetry"] with
                                | :? JsonArray as array -> array
                                | _ ->
                                    let created = JsonArray()
                                    record["rawTelemetry"] <- created
                                    created

                            let snapshotNode = JsonObject()
                            snapshotNode["snapshotId"] <- JsonValue.Create snapshotId
                            snapshotNode["adapter"] <- JsonValue.Create "generic"
                            snapshotNode["schemaVersion"] <-
                                match adapted.SchemaVersion with
                                | null -> null
                                | v -> v.DeepClone()
                            snapshotNode["collectedAt"] <- JsonValue.Create adapted.CollectedAt
                            snapshotNode["source"] <- adapted.Source.DeepClone()
                            snapshotNode["payload"] <- payload
                            snapshotNode["payloadBytes"] <- JsonValue.Create serializedBytes

                            let discoveredFieldsNode = JsonArray()
                            discoveredFields |> List.iter (fun field -> discoveredFieldsNode.Add(JsonValue.Create field: JsonNode))
                            snapshotNode["discoveredFields"] <- discoveredFieldsNode

                            let redactionsNode = JsonArray()
                            redactions |> Seq.iter (fun path -> redactionsNode.Add(JsonValue.Create path: JsonNode))
                            snapshotNode["redactions"] <- redactionsNode

                            rawTelemetryArray.Add(snapshotNode: JsonNode)

                        let discoveredFieldReason =
                            if rawRetentionStatus = "retained" then
                                "provider field preserved but not normalized by this adapter version"
                            else
                                $"provider field discovered but raw payload was omitted: {rawRetentionReason}"

                        for fieldPath in discoveredFields do
                            mergeCapabilityByKey
                                root
                                record
                                None
                                (Some fieldPath)
                                "unknown"
                                (Some discoveredFieldReason)
                                adapted.CollectedAt
                                adapted.CollectedAt
                                (capabilitySourceFromNode adapted.Source)

                        if redactions.Count > 0 then
                            normalizeAndAppendIngestedMetric
                                root
                                record
                                (syntheticMetricNode "telemetry.redactions" redactions.Count { Type = "calculated"; Name = "ros-raw-filter"; Mechanism = "sensitive-key-redaction" } adapted.CollectedAt)
                                (adapted.Source: JsonNode)
                                adapted.CollectedAt
                            |> ignore

                        if not discoveredFields.IsEmpty then
                            normalizeAndAppendIngestedMetric
                                root
                                record
                                (syntheticMetricNode "telemetry.unknown_fields" discoveredFields.Length { Type = "calculated"; Name = "ros-field-discovery"; Mechanism = "unmapped-leaf-count" } adapted.CollectedAt)
                                (adapted.Source: JsonNode)
                                adapted.CollectedAt
                            |> ignore

                        if rawRetentionStatus = "omitted" then
                            normalizeAndAppendIngestedMetric
                                root
                                record
                                (syntheticMetricNode "telemetry.raw_snapshots_omitted" 1 { Type = "calculated"; Name = "ros-retention-policy"; Mechanism = rawRetentionReason } adapted.CollectedAt)
                                (adapted.Source: JsonNode)
                                adapted.CollectedAt
                            |> ignore

                        let rawRetentionNode = JsonObject()
                        rawRetentionNode["status"] <- JsonValue.Create rawRetentionStatus
                        rawRetentionNode["reason"] <- (if rawRetentionStatus = "omitted" then JsonValue.Create rawRetentionReason else null)
                        rawRetentionNode["payloadBytes"] <- JsonValue.Create serializedBytes

                        let ingestionEventSeed = JsonObject()
                        ingestionEventSeed["type"] <- JsonValue.Create "telemetry.snapshot.ingested"
                        ingestionEventSeed["snapshotId"] <- JsonValue.Create snapshotId
                        ingestionEventSeed["adapter"] <- JsonValue.Create "generic"
                        let ingestionEventId = "TEVT-" + CanonicalJson.contentDigest 24 ingestionEventSeed

                        let eventsArray =
                            match record["events"] with
                            | :? JsonArray as array -> array
                            | _ ->
                                let created = JsonArray()
                                record["events"] <- created
                                created

                        let ingestionEventAlreadyPresent =
                            eventsArray
                            |> Seq.exists (function
                                | :? JsonObject as node -> stringField node "eventId" = Some ingestionEventId
                                | _ -> false)

                        if not ingestionEventAlreadyPresent then
                            let ingestionEvent = JsonObject()
                            ingestionEvent["type"] <- JsonValue.Create "telemetry.snapshot.ingested"
                            ingestionEvent["snapshotId"] <- JsonValue.Create snapshotId
                            ingestionEvent["adapter"] <- JsonValue.Create "generic"
                            ingestionEvent["rawRetention"] <- rawRetentionNode
                            ingestionEvent["occurredAt"] <- JsonValue.Create adapted.CollectedAt
                            ingestionEvent["source"] <- adapted.Source.DeepClone()
                            ingestionEvent["eventId"] <- JsonValue.Create ingestionEventId
                            eventsArray.Add(ingestionEvent: JsonNode)

                        let existingSources =
                            match record["provenance"] with
                            | :? JsonObject as provenance ->
                                match provenance["sources"] with
                                | :? JsonArray as array -> array |> Seq.choose (function :? JsonObject as node -> Some node | _ -> None) |> Seq.toList
                                | _ -> []
                            | _ -> []

                        let dedupedSources =
                            let seen = Collections.Generic.HashSet<string>()
                            let result = ResizeArray<JsonObject>()

                            for candidate in existingSources @ [ adapted.Source ] do
                                let key = CanonicalJson.contentDigest 24 candidate
                                if seen.Add key then result.Add candidate

                            result |> Seq.toList

                        let sourcesNode = JsonArray()
                        dedupedSources |> List.iter (fun source -> sourcesNode.Add(source.DeepClone(): JsonNode))

                        match record["provenance"] with
                        | :? JsonObject as provenance -> provenance["sources"] <- sourcesNode
                        | _ ->
                            let provenance = JsonObject()
                            provenance["sources"] <- sourcesNode
                            record["provenance"] <- provenance

                        File.WriteAllText(file, record.ToJsonString serializerOptions + "\n")
                        Ok record
        | _ -> Error "execution record must be a JSON object"

    /// Mirrors production `readTelemetryInput`'s parsing half (the
    /// disk/stdin read and byte-limit check are the CLI's own concern,
    /// matching how this migration already splits file I/O into
    /// `Ros.Cli.Program`): a single JSON value first, falling back to
    /// JSON Lines (one value per non-blank line) only when that fails.
    let parseIngestInput (text: string) : Result<JsonNode, string> =
        let trimmed = text.Trim()

        if trimmed = "" then
            Error "telemetry input is empty"
        else
            try
                Ok(JsonNode.Parse trimmed)
            with _ ->
                try
                    let lines = trimmed.Split([| "\r\n"; "\n" |], StringSplitOptions.None) |> Array.filter (fun line -> line.Trim() <> "")
                    let array = JsonArray()
                    lines |> Array.iter (fun line -> array.Add(JsonNode.Parse line))
                    Ok(array: JsonNode)
                with error ->
                    Error $"telemetry input must be JSON or JSON Lines: {error.Message}"

    /// Mirrors production `ingestTelemetry(root, target, input, {adapter:
    /// "generic"})`: adapter validation and adaptation happen before any
    /// target resolution or lock, exactly matching production's own
    /// `adaptInput` call ahead of `withExecutionLock` -- an unknown
    /// adapter name (one production itself would not recognize) or a bad
    /// target both reject before anything is touched. Every other real
    /// adapter name is its own future MIG-08 slice.
    let ingestTarget (root: string) (target: string option) (adapter: string) (inputText: string) : Result<JsonObject, string> =
        let supportedAdapters =
            [ "generic"; "openai-codex"; "anthropic-claude-hook"; "google-gemini-hook"; "github-copilot-hook" ]

        if not (Ros.Domain.Telemetry.TelemetryAdapters.all |> List.contains adapter) then
            Error $"unknown telemetry adapter '{adapter}'"
        elif not (supportedAdapters |> List.contains adapter) then
            Error $"telemetry ingest --adapter '{adapter}' is not yet supported by this CLI"
        else
            match parseIngestInput inputText with
            | Error message -> Error message
            | Ok inputNode ->
                let collectedAt = FileTelemetryExecutionRepository.nowIso ()

                let adapted =
                    match adapter with
                    | "openai-codex" -> adaptOpenAICodex inputNode collectedAt
                    | "anthropic-claude-hook" -> adaptHook inputNode collectedAt "anthropic" "claude-code"
                    | "google-gemini-hook" -> adaptHook inputNode collectedAt "google" "gemini-cli"
                    | "github-copilot-hook" -> adaptHook inputNode collectedAt "github" "copilot"
                    | _ -> adaptGeneric inputNode collectedAt

                // Mirrors production `ingestTelemetry`'s own `adapted.snapshotId
                // ??= \`SNAP-${digest({ adapter, input })}\``, computed here
                // (using the ORIGINAL, un-adapted input) before `ingestAdapted`
                // ever runs -- the adapter's own fallback formula (`{adapter,
                // raw: adapted.raw}`) is unreachable through this call path,
                // since `snapshotId` is always already set by the time
                // `ingestAdapted` would consult it.
                let adapted =
                    match adapted.SnapshotId with
                    | Some _ -> adapted
                    | None ->
                        let seed = JsonObject()
                        seed["adapter"] <- JsonValue.Create adapter
                        seed["input"] <- inputNode.DeepClone()
                        { adapted with SnapshotId = Some("SNAP-" + CanonicalJson.contentDigest 24 seed) }

                match resolveExecutionTarget root target true with
                | Error message -> Error message
                | Ok executionId ->
                    match RegistryLock.acquire root $"telemetry-execution:{executionId}" RegistryLock.defaultSettings with
                    | Error failure -> Error failure.Message
                    | Ok lease ->
                        let result =
                            try
                                match resolveExecutionTarget root (Some executionId) true with
                                | Error message -> Error message
                                | Ok _ -> ingestAdaptedGeneric root executionId adapted
                            with error ->
                                Error error.Message

                        match lease.Release(), result with
                        | Error releaseFailure, Ok _ -> Error releaseFailure.Message
                        | _, outcome -> outcome

    // ---- telemetry classify (a thin wrapper over generic ingest) ----

    /// Mirrors production `telemetry classify --classification NAME [...]
    /// [--rationale TEXT] [--evidence-link LINK]* [--rd-context FILE]`: a
    /// synthetic ingest whose `snapshotId` is `classification-{now-ms}`, a
    /// real-clock timestamp production computes with its own `Date.now()`
    /// (not a content digest), so a repeated call is *not* deduplicated
    /// the way a normal `telemetry ingest` snapshot would be -- each
    /// classify call is its own event, matching production exactly. Reuses
    /// `ingestTarget` unchanged (always the `generic` adapter, matching
    /// production's own un-overridden `ingestTelemetry` call) rather than
    /// a second mutation path, since a classification is just an ingest
    /// whose only real content is its `classification` object and an
    /// explicitly empty `raw: {}`.
    let classifyTarget
        (root: string)
        (target: string option)
        (classifications: string list)
        (rationale: string option)
        (evidenceLinks: string list)
        (rd: JsonNode option)
        : Result<JsonObject, string> =
        if classifications.IsEmpty then
            Error "telemetry classify requires at least one --classification"
        else
            let snapshotId = $"classification-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"

            let typesNode = JsonArray()
            classifications |> List.iter (fun value -> typesNode.Add(JsonValue.Create value: JsonNode))

            let evidenceNode = JsonArray()
            evidenceLinks |> List.iter (fun value -> evidenceNode.Add(JsonValue.Create value: JsonNode))

            let classificationNode = JsonObject()
            classificationNode["types"] <- typesNode
            classificationNode["rationale"] <- (match rationale with Some value -> JsonValue.Create value | None -> null)
            classificationNode["evidence"] <- evidenceNode
            classificationNode["rd"] <- (match rd with Some node -> node.DeepClone() | None -> null)

            let inputNode = JsonObject()
            inputNode["snapshotId"] <- JsonValue.Create snapshotId
            inputNode["classification"] <- classificationNode
            inputNode["raw"] <- JsonObject()

            match ingestTarget root target "generic" (inputNode.ToJsonString()) with
            | Error message -> Error message
            | Ok record ->
                match record["classification"] with
                | :? JsonObject as classification -> Ok classification
                | _ -> Error "execution record must carry a classification object"

    // ---- telemetry start (manual recover-or-create, no state transition) ----

    /// Mirrors production's `recoverOrStartExecution`/`showTelemetry`
    /// filtering exactly, via the shared pure `ExecutionLinkRecovery.decide`
    /// (`Ros.Domain.Telemetry.ExecutionLink`) `work start`/`work resume`
    /// already use through the heavier `TelemetryResolution`/
    /// `WorkContextPlan` layer -- `telemetry start` needs none of that
    /// machinery (there is no state transition at all here, just a manual
    /// telemetry attachment to an item already `active`/`blocked`), so it
    /// calls the same low-level decision directly. Bounded to one creation
    /// attempt, matching `Ros.Cli.Program.resolveContextTelemetryWithCreation`'s
    /// own retry cap.
    let private resolveOrCreateExecution
        (root: string)
        (workItemId: string)
        (workType: string)
        (classifications: string list)
        (classificationRationale: string option)
        (linkedIds: string list)
        : Result<string option, string> =
        let rec attempt attemptsLeft =
            let candidates = FileTelemetryStateRepository.readCandidates root workItemId

            let request: ExecutionLinkRequest =
                { WorkItemId = workItemId
                  LinkedExecutionIds = Set.ofList linkedIds
                  RecoverableStatuses = Set.singleton ExecutionStatus.Active
                  RequestedExecutionId = None
                  Candidates = candidates }

            match ExecutionLinkRecovery.decide request with
            | ExecutionLinkDecision.Recover executionId -> Ok(Some executionId)
            | ExecutionLinkDecision.RejectAmbiguous ids ->
                Error
                    $"""multiple detached telemetry executions require explicit selection for '{workItemId}'; rerun with --execution-id one of: {String.Join(", ", ids)}"""
            | ExecutionLinkDecision.RejectDetachedConflict _ ->
                Error $"a detached telemetry execution must be linked before creating a new one for '{workItemId}', which this command does not yet support selecting"
            | ExecutionLinkDecision.StartNew ->
                if attemptsLeft <= 0 then
                    Error $"unable to resolve a telemetry execution for '{workItemId}'"
                else
                    let createRequest: FileTelemetryExecutionRepository.CreateExecutionRequest =
                        { WorkItemId = workItemId
                          WorkType = workType
                          Classifications = classifications
                          ClassificationRationale = classificationRationale
                          ParentExecutionId = None }

                    match FileTelemetryExecutionRepository.createExecution root createRequest with
                    | Error message -> Error message
                    | Ok None -> Ok None
                    | Ok(Some _) -> attempt (attemptsLeft - 1)

        attempt 1

    /// Mirrors production `telemetry start WORKITEMID` (`tools/ros_cli.mjs`)
    /// -- MIG-08's eighth increment: a manual telemetry-execution attachment
    /// to a work item already `active`/`blocked`, with no semantic-state
    /// transition of its own (unlike `work start`/`resume`, which combine a
    /// transition with telemetry attachment in one committed operation).
    /// Runs under the same `work-protocol` lock and recovery preflight
    /// every live-work transition uses, then does its own narrow
    /// `context/current.json` read-modify-write (no event log entry at
    /// all, matching production's own `renderedEventLog(root, [])` with an
    /// empty planned-events list) rather than going through the
    /// transition-shaped `WorkContextPlanning`/`FileWorkContextRepository.
    /// applyContextPlan`, since there is no transition to plan. Returns
    /// `Ok None` when telemetry is disabled and no candidate execution
    /// already exists, matching production's own pre-lock-irrelevant
    /// `startExecution` short-circuit -- no context or file mutation at
    /// all in that case. Deliberately excludes `--execution-id` and every
    /// `--provider`/`--model`/`--runtime`/... identity-override flag
    /// production's own CLI exposes here (the one command that does);
    /// supporting them would mean extending `createExecution` itself to
    /// accept an explicit execution id and identity overrides, a
    /// separately scoped future slice.
    let startTarget
        (root: string)
        (workItemId: string)
        (classifications: string list)
        (classificationRationale: string option)
        : Result<JsonObject option, string> =
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match BacklogStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            let contextPath = Path.Combine(root, ".ros", "context", "current.json")

                            if not (File.Exists contextPath) then
                                Error $"work item '{workItemId}' must be active or blocked before starting telemetry"
                            else
                                match JsonNode.Parse(File.ReadAllText contextPath) with
                                | :? JsonObject as context ->
                                    match context["workItems"] with
                                    | :? JsonArray as items ->
                                        let matchedItem =
                                            items
                                            |> Seq.tryPick (function
                                                | :? JsonObject as item when stringField item "id" = Some workItemId -> Some item
                                                | _ -> None)

                                        match matchedItem with
                                        | None -> Error $"work item '{workItemId}' must be active or blocked before starting telemetry"
                                        | Some item ->
                                            let semanticState = stringField item "semanticState" |> Option.defaultValue ""

                                            if semanticState <> "active" && semanticState <> "blocked" then
                                                Error $"work item '{workItemId}' must be active or blocked before starting telemetry"
                                            else
                                                let workType = stringField item "type" |> Option.defaultValue "task"

                                                let linkedIds =
                                                    match item["telemetryExecutionIds"] with
                                                    | :? JsonArray as array ->
                                                        array
                                                        |> Seq.choose (function
                                                            | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
                                                            | _ -> None)
                                                        |> Seq.toList
                                                    | _ -> []

                                                match resolveOrCreateExecution root workItemId workType classifications classificationRationale linkedIds with
                                                | Error message -> Error message
                                                | Ok None -> Ok None
                                                | Ok(Some executionId) ->
                                                    if not (List.contains executionId linkedIds) then
                                                        let idsNode = JsonArray()
                                                        (linkedIds @ [ executionId ]) |> List.iter (fun id -> idsNode.Add(JsonValue.Create id: JsonNode))
                                                        item["telemetryExecutionIds"] <- idsNode

                                                    context["updatedAt"] <- JsonValue.Create(FileTelemetryExecutionRepository.nowIso ())
                                                    File.WriteAllText(contextPath, context.ToJsonString(serializerOptions) + "\n")

                                                    match JsonNode.Parse(File.ReadAllText(executionFile root executionId)) with
                                                    | :? JsonObject as record -> Ok(Some record)
                                                    | _ -> Error "execution record must be a JSON object"
                                    | _ -> Error $"work item '{workItemId}' must be active or blocked before starting telemetry"
                                | _ -> Error "work context must be a JSON object"
                with error ->
                    lease.Release() |> ignore
                    reraise ()

            match lease.Release(), result with
            | Error releaseFailure, Ok _ -> Error releaseFailure.Message
            | _, outcome -> outcome
