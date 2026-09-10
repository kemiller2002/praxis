namespace Ros.Infrastructure.Work

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Domain.Telemetry
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Git
open Ros.Infrastructure.Json

/// The real "finalize an execution" effect (`DF-ROS-2026-A028` Phase A):
/// production's `finalizeExecution`/`finalizeWorkExecutions`
/// (`tools/ros_telemetry.mjs`), reduced to the path production's own `work
/// complete` CLI actually reaches (no `--input` adapter ingestion, which no
/// current CLI command threads through to a work-completion finalize).
/// Mutates an existing `.ros/telemetry/executions/EXE-*.json` record in
/// place, under its own per-execution lock -- it never creates or links an
/// execution (see `FileTelemetryExecutionRepository`).
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

        { MetricId = stringField node "metricId" |> Option.defaultValue ""
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
                                        if capability.MetricId = metricId then
                                            Capability.upsert maxHistory finalizedAt finalizedAt status reason source capability
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
