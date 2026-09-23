namespace Ros.Infrastructure.Work

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Ros.Domain.Telemetry
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Git

type ChangeHealthCapture =
    { Enabled: bool
      Metrics: ChangeHealthMetrics
      Findings: ChangeHealthFinding list
      Node: JsonObject }

[<RequireQualifiedAccess>]
module FileChangeHealthRepository =
    type private RawFileChange =
        { Status: char
          Path: string
          From: string option
          Untracked: bool }

    let private serializerOptions =
        JsonSerializerOptions(
            WriteIndented = true,
            IndentSize = 2,
            Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        )

    let private intProperty (element: JsonElement) (name: string) (fallback: int) : int =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt32() with
            | true, parsed -> parsed
            | _ -> fallback
        | _ -> fallback

    let private boolProperty (element: JsonElement) (name: string) (fallback: bool) : bool =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.True -> true
        | true, value when value.ValueKind = JsonValueKind.False -> false
        | _ -> fallback

    let private stringProperty (element: JsonElement) (name: string) (fallback: string) : string =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> fallback

    let private optionalStringProperty (element: JsonElement) (name: string) : string option =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
        | _ -> None

    let private optionalIntProperty (element: JsonElement) (name: string) : int option =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt32() with
            | true, parsed -> Some parsed
            | _ -> None
        | true, value when value.ValueKind = JsonValueKind.Null -> None
        | _ -> None

    let private threshold (element: JsonElement) (name: string) (fallback: ChangeThreshold) : ChangeThreshold =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Object ->
            { Warning = optionalIntProperty value "warning"
              Error = optionalIntProperty value "error" }
        | _ -> fallback

    let private validateThreshold (name: string) (value: ChangeThreshold) : Result<unit, string> =
        let values = [ value.Warning; value.Error ] |> List.choose id

        if values |> List.exists (fun item -> item < 0) then
            Error $"change-health threshold '{name}' cannot be negative"
        else
            match value.Warning, value.Error with
            | Some warning, Some error when warning > error ->
                Error $"change-health threshold '{name}' warning cannot exceed error"
            | _ -> Ok()

    let loadPolicy (root: string) : Result<ChangeHealthPolicy * string, string> =
        let relative = FileWorkConfigRepository.readTelemetryChangeHealthPolicyPath root
        let file = Path.GetFullPath(Path.Combine(root, relative))

        if not (File.Exists file) then
            Ok(ChangeHealth.defaultPolicy, relative)
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText file)
                let policy = document.RootElement

                if policy.ValueKind <> JsonValueKind.Object then
                    Error $"change-health policy must be a JSON object: {relative}"
                else
                    let defaults = ChangeHealth.defaultPolicy

                    let thresholds =
                        match policy.TryGetProperty "thresholds" with
                        | true, value when value.ValueKind = JsonValueKind.Object ->
                            { FilesChanged = threshold value "filesChanged" defaults.Thresholds.FilesChanged
                              LinesChanged = threshold value "linesChanged" defaults.Thresholds.LinesChanged
                              LargestFileChurn = threshold value "largestFileChurn" defaults.Thresholds.LargestFileChurn
                              LargestChangedFileLines = threshold value "largestChangedFileLines" defaults.Thresholds.LargestChangedFileLines
                              HunksChanged = threshold value "hunksChanged" defaults.Thresholds.HunksChanged
                              MaxHunksPerFile = threshold value "maxHunksPerFile" defaults.Thresholds.MaxHunksPerFile
                              RepeatFileTouches = threshold value "repeatFileTouches" defaults.Thresholds.RepeatFileTouches
                              RepeatRegionTouches = threshold value "repeatRegionTouches" defaults.Thresholds.RepeatRegionTouches }
                        | _ -> defaults.Thresholds

                    let parsed =
                        { Enabled = boolProperty policy "enabled" defaults.Enabled
                          HistoryPath = stringProperty policy "historyPath" defaults.HistoryPath
                          HistoryWindow = intProperty policy "historyWindow" defaults.HistoryWindow
                          MaxHistoryUpdates = intProperty policy "maxHistoryUpdates" defaults.MaxHistoryUpdates
                          LineBucketSize = intProperty policy "lineBucketSize" defaults.LineBucketSize
                          Thresholds = thresholds }

                    if parsed.HistoryWindow < 1 then
                        Error "change-health historyWindow must be at least 1"
                    elif parsed.MaxHistoryUpdates < 1 then
                        Error "change-health maxHistoryUpdates must be at least 1"
                    elif parsed.LineBucketSize < 1 then
                        Error "change-health lineBucketSize must be at least 1"
                    else
                        [ "filesChanged", thresholds.FilesChanged
                          "linesChanged", thresholds.LinesChanged
                          "largestFileChurn", thresholds.LargestFileChurn
                          "largestChangedFileLines", thresholds.LargestChangedFileLines
                          "hunksChanged", thresholds.HunksChanged
                          "maxHunksPerFile", thresholds.MaxHunksPerFile
                          "repeatFileTouches", thresholds.RepeatFileTouches
                          "repeatRegionTouches", thresholds.RepeatRegionTouches ]
                        |> List.fold
                            (fun state (name, value) ->
                                state
                                |> Result.bind (fun () -> validateThreshold name value))
                            (Ok())
                        |> Result.map (fun () -> parsed, relative)
            with error ->
                Error $"change-health policy is unreadable: {relative}: {error.Message}"

    let private ignored (ignoredPatterns: string list) (path: string) : bool =
        ignoredPatterns |> List.exists (fun pattern -> Ros.Domain.Work.PathFilter.globMatch pattern path)

    let private parseNameStatus (ignoredPatterns: string list) (text: string) : RawFileChange list =
        if String.IsNullOrEmpty text then
            []
        else
            text.Split '\n'
            |> Array.choose (fun line ->
                if String.IsNullOrWhiteSpace line then
                    None
                else
                    let parts = line.TrimEnd('\r').Split '\t'
                    if parts.Length < 2 || parts[0].Length = 0 then
                        None
                    else
                        let status = parts[0][0]
                        let renamed = status = 'R' || status = 'C'
                        let target = if renamed && parts.Length > 2 then parts[2] else parts[1]
                        let from = if renamed && parts.Length > 2 then Some parts[1] else None

                        if ignored ignoredPatterns target then
                            None
                        else
                            Some
                                { Status = status
                                  Path = target
                                  From = from
                                  Untracked = false })
            |> Array.toList

    let private parseUntracked (ignoredPatterns: string list) (known: Set<string>) (text: string) : RawFileChange list =
        if String.IsNullOrEmpty text then
            []
        else
            text.Split '\000'
            |> Array.filter (fun value -> value <> "" && not (ignored ignoredPatterns value) && not (Set.contains value known))
            |> Array.map (fun path ->
                { Status = 'A'
                  Path = path
                  From = None
                  Untracked = true })
            |> Array.toList

    let private parseNumstat (ignoredPatterns: string list) (text: string) : Map<string, int option * int option> =
        if String.IsNullOrEmpty text then
            Map.empty
        else
            text.Split '\n'
            |> Array.choose (fun line ->
                let parts = line.TrimEnd('\r').Split '\t'
                if parts.Length < 3 then
                    None
                else
                    let file = parts[parts.Length - 1]
                    if ignored ignoredPatterns file then
                        None
                    elif parts[0] = "-" || parts[1] = "-" then
                        Some(file, (None, None))
                    else
                        match Int32.TryParse parts[0], Int32.TryParse parts[1] with
                        | (true, added), (true, deleted) -> Some(file, (Some added, Some deleted))
                        | _ -> None)
            |> Map.ofArray

    let private decodeDiffPath (value: string) =
        let trimmed = value.Trim()

        if trimmed = "/dev/null" then
            None
        else
            let unprefixed =
                if trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal) then
                    trimmed.Substring 2
                else
                    trimmed

            if unprefixed.StartsWith(""", StringComparison.Ordinal) && unprefixed.EndsWith(""", StringComparison.Ordinal) then
                try
                    Some(JsonSerializer.Deserialize<string>(unprefixed))
                with _ ->
                    Some(unprefixed.Trim('"'))
            else
                Some unprefixed

    let private hunkPattern =
        Regex(
            "^@@ -(?<oldStart>[0-9]+)(?:,(?<oldLines>[0-9]+))? \+(?<newStart>[0-9]+)(?:,(?<newLines>[0-9]+))? @@",
            RegexOptions.Compiled
        )

    let parseHunks (bucketSize: int) (ignoredPatterns: string list) (text: string) : Map<string, ChangeHunk list> =
        let mutable oldPath: string option = None
        let mutable newPath: string option = None
        let mutable map: Map<string, ChangeHunk list> = Map.empty

        let addHunk (path: string) (hunk: ChangeHunk) =
            let current = map |> Map.tryFind path |> Option.defaultValue []
            map <- map |> Map.add path (current @ [ hunk ])

        for rawLine in text.Split '\n' do
            let line = rawLine.TrimEnd '\r'

            if line.StartsWith("--- ", StringComparison.Ordinal) then
                oldPath <- decodeDiffPath (line.Substring 4)
            elif line.StartsWith("+++ ", StringComparison.Ordinal) then
                newPath <- decodeDiffPath (line.Substring 4)
            elif line.StartsWith("@@ ", StringComparison.Ordinal) then
                let matched = hunkPattern.Match line

                if matched.Success then
                    let parse (name: string) (fallback: int) : int =
                        let value = matched.Groups[name]
                        if value.Success && value.Value <> "" then int value.Value else fallback

                    let oldStart = parse "oldStart" 0
                    let oldLines = parse "oldLines" 1
                    let newStart = parse "newStart" 0
                    let newLines = parse "newLines" 1

                    match newPath |> Option.orElse oldPath with
                    | Some path when not (ignored ignoredPatterns path) ->
                        addHunk
                            path
                            { OldStart = oldStart
                              OldLines = oldLines
                              NewStart = newStart
                              NewLines = newLines
                              Buckets = ChangeHealth.buckets bucketSize oldStart oldLines newStart newLines }
                    | _ -> ()

        map

    let private countTextLines (root: string) (relative: string) =
        let file = Path.Combine(root, relative)

        try
            if not (File.Exists file) then
                None
            else
                let bytes = File.ReadAllBytes file
                if Array.contains 0uy bytes then
                    None
                elif bytes.Length = 0 then
                    Some 0
                else
                    let text = Encoding.UTF8.GetString bytes
                    let split = text.Split '\n'
                    Some(if text.EndsWith "\n" then split.Length - 1 else split.Length)
        with _ ->
            None

    let private historyFile (root: string) (policy: ChangeHealthPolicy) : string =
        Path.GetFullPath(Path.Combine(root, policy.HistoryPath))

    let private parseHunkNode (node: JsonObject) =
        let intField (name: string) : int =
            match node[name] with
            | :? JsonValue as value ->
                match value.TryGetValue<int>() with
                | true, parsed -> parsed
                | _ -> 0
            | _ -> 0

        let buckets =
            match node["buckets"] with
            | :? JsonArray as array ->
                array
                |> Seq.choose (function
                    | :? JsonValue as value ->
                        match value.TryGetValue<int>() with
                        | true, parsed -> Some parsed
                        | _ -> None
                    | _ -> None)
                |> Seq.toList
            | _ -> []

        { OldStart = intField "oldStart"
          OldLines = intField "oldLines"
          NewStart = intField "newStart"
          NewLines = intField "newLines"
          Buckets = buckets }

    let private stringNodeField (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private parseHistory (file: string) =
        if not (File.Exists file) then
            ChangeHealth.emptyHistory
        else
            match JsonNode.Parse(File.ReadAllText file) with
            | :? JsonObject as rootNode ->
                let schemaVersion = stringNodeField rootNode "schemaVersion"

                if schemaVersion <> Some "1.0.0" then
                    raise (InvalidDataException($"change-health history is missing or unsupported: {file}"))

                let omitted =
                    match rootNode["historyOmitted"] with
                    | :? JsonValue as value ->
                        match value.TryGetValue<int>() with
                        | true, parsed -> parsed
                        | _ -> 0
                    | _ -> 0

                let updates =
                    match rootNode["updates"] with
                    | :? JsonArray as array ->
                        array
                        |> Seq.choose (function
                            | :? JsonObject as update ->
                                match
                                    stringNodeField update "executionId",
                                    stringNodeField update "workItemId",
                                    stringNodeField update "finalizedAt",
                                    stringNodeField update "startCommit",
                                    stringNodeField update "endCommit"
                                with
                                | Some executionId, Some workItemId, Some finalizedAt, Some startCommit, Some endCommit ->
                                    let files =
                                        match update["files"] with
                                        | :? JsonArray as fileArray ->
                                            fileArray
                                            |> Seq.choose (function
                                                | :? JsonObject as fileNode ->
                                                    match stringNodeField fileNode "path", stringNodeField fileNode "status" with
                                                    | Some path, Some status when status.Length > 0 ->
                                                        let from = stringNodeField fileNode "from"
                                                        let hunks =
                                                            match fileNode["hunks"] with
                                                            | :? JsonArray as hunks ->
                                                                hunks
                                                                |> Seq.choose (function :? JsonObject as hunk -> Some(parseHunkNode hunk) | _ -> None)
                                                                |> Seq.toList
                                                            | _ -> []

                                                        Some
                                                            { Path = path
                                                              From = from
                                                              Status = status[0]
                                                              Hunks = hunks }
                                                    | _ -> None
                                                | _ -> None)
                                            |> Seq.toList
                                        | _ -> []

                                    Some
                                        { ExecutionId = executionId
                                          WorkItemId = workItemId
                                          FinalizedAt = finalizedAt
                                          StartCommit = startCommit
                                          EndCommit = endCommit
                                          Files = files }
                                | _ -> None
                            | _ -> None)
                        |> Seq.toList
                    | _ -> []

                match rootNode["updates"] with
                | :? JsonArray -> ()
                | _ -> raise (InvalidDataException($"change-health history is missing or unsupported: {file}"))

                { SchemaVersion = "1.0.0"
                  HistoryOmitted = omitted
                  Updates = updates }
            | _ -> raise (InvalidDataException($"change-health history is missing or unsupported: {file}"))

    let private hunkNode (hunk: HistoricalHunk) =
        let node = JsonObject()
        node["oldStart"] <- JsonValue.Create hunk.OldStart
        node["oldLines"] <- JsonValue.Create hunk.OldLines
        node["newStart"] <- JsonValue.Create hunk.NewStart
        node["newLines"] <- JsonValue.Create hunk.NewLines
        let buckets = JsonArray()
        hunk.Buckets |> List.iter (fun bucket -> buckets.Add(JsonValue.Create bucket: JsonNode))
        node["buckets"] <- buckets
        node

    let private historyNode (history: ChangeHistory) =
        let root = JsonObject()
        root["schemaVersion"] <- JsonValue.Create history.SchemaVersion
        root["historyOmitted"] <- JsonValue.Create history.HistoryOmitted
        let updates = JsonArray()

        for update in history.Updates do
            let updateNode = JsonObject()
            updateNode["executionId"] <- JsonValue.Create update.ExecutionId
            updateNode["workItemId"] <- JsonValue.Create update.WorkItemId
            updateNode["finalizedAt"] <- JsonValue.Create update.FinalizedAt
            updateNode["startCommit"] <- JsonValue.Create update.StartCommit
            updateNode["endCommit"] <- JsonValue.Create update.EndCommit
            let files = JsonArray()

            for file in update.Files do
                let fileNode = JsonObject()
                fileNode["path"] <- JsonValue.Create file.Path
                fileNode["status"] <- JsonValue.Create(string file.Status)
                fileNode["from"] <- match file.From with Some value -> JsonValue.Create value | None -> null
                let hunks = JsonArray()
                file.Hunks |> List.iter (fun hunk -> hunks.Add(hunkNode hunk: JsonNode))
                fileNode["hunks"] <- hunks
                files.Add(fileNode: JsonNode)

            updateNode["files"] <- files
            updates.Add(updateNode: JsonNode)

        root["updates"] <- updates
        root

    let private writeHistoryAtomic (file: string) (history: ChangeHistory) =
        let directory = Path.GetDirectoryName file
        Directory.CreateDirectory directory |> ignore
        let temporary = Path.Combine(directory, $".{Path.GetFileName file}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp")

        try
            File.WriteAllText(temporary, historyNode(history).ToJsonString(serializerOptions) + "\n")
            File.Move(temporary, file, true)
        finally
            if File.Exists temporary then File.Delete temporary

    let private findingNode (finding: ChangeHealthFinding) =
        let node = JsonObject()
        node["code"] <- JsonValue.Create finding.Code
        node["metric"] <- JsonValue.Create finding.Metric
        node["severity"] <- JsonValue.Create finding.Severity
        node["actual"] <- JsonValue.Create finding.Actual
        node["threshold"] <- JsonValue.Create finding.Threshold
        node["message"] <- JsonValue.Create finding.Message
        node["remediation"] <- JsonValue.Create finding.Remediation
        node

    let private fileNode (file: ChangeFileDetail) =
        let node = JsonObject()
        node["path"] <- JsonValue.Create file.Path
        node["from"] <- match file.From with Some value -> JsonValue.Create value | None -> null
        node["status"] <- JsonValue.Create(string file.Status)
        node["linesAdded"] <- match file.LinesAdded with Some value -> JsonValue.Create value | None -> null
        node["linesDeleted"] <- match file.LinesDeleted with Some value -> JsonValue.Create value | None -> null
        node["churn"] <- match file.Churn with Some value -> JsonValue.Create value | None -> null
        node["currentLines"] <- match file.CurrentLines with Some value -> JsonValue.Create value | None -> null
        node["recentTouches"] <- JsonValue.Create file.RecentTouches
        node["maxRegionTouches"] <- JsonValue.Create file.MaxRegionTouches
        let hunks = JsonArray()

        for hunk in file.Hunks do
            let hunkNode = JsonObject()
            hunkNode["oldStart"] <- JsonValue.Create hunk.OldStart
            hunkNode["oldLines"] <- JsonValue.Create hunk.OldLines
            hunkNode["newStart"] <- JsonValue.Create hunk.NewStart
            hunkNode["newLines"] <- JsonValue.Create hunk.NewLines
            let buckets = JsonArray()
            hunk.Buckets |> List.iter (fun bucket -> buckets.Add(JsonValue.Create bucket: JsonNode))
            hunkNode["buckets"] <- buckets
            hunks.Add(hunkNode: JsonNode)

        node["hunks"] <- hunks
        node

    let private metricsNode (metrics: ChangeHealthMetrics) =
        let node = JsonObject()
        node["filesChanged"] <- JsonValue.Create metrics.FilesChanged
        node["sourceFilesChanged"] <- JsonValue.Create metrics.SourceFilesChanged
        node["testFilesChanged"] <- JsonValue.Create metrics.TestFilesChanged
        node["documentationFilesChanged"] <- JsonValue.Create metrics.DocumentationFilesChanged
        node["linesAdded"] <- JsonValue.Create metrics.LinesAdded
        node["linesDeleted"] <- JsonValue.Create metrics.LinesDeleted
        node["linesChanged"] <- JsonValue.Create metrics.LinesChanged
        node["netLines"] <- JsonValue.Create metrics.NetLines
        node["hunksChanged"] <- JsonValue.Create metrics.HunksChanged
        node["maxHunksPerFile"] <- JsonValue.Create metrics.MaxHunksPerFile
        node["largestFileChurn"] <- JsonValue.Create metrics.LargestFileChurn
        node["largestChangedFileLines"] <- JsonValue.Create metrics.LargestChangedFileLines
        node["repeatFileTouches"] <- JsonValue.Create metrics.RepeatFileTouches
        node["repeatRegionTouches"] <- JsonValue.Create metrics.RepeatRegionTouches
        node

    let capture
        (root: string)
        (executionId: string)
        (workItemId: string)
        (finalizedAt: string)
        (summary: ChangeSummary)
        : Result<ChangeHealthCapture, string> =
        loadPolicy root
        |> Result.bind (fun (policy, policyPath) ->
            if not policy.Enabled then
                let node = JsonObject()
                node["schemaVersion"] <- JsonValue.Create "1.0.0"
                node["available"] <- JsonValue.Create true
                node["enabled"] <- JsonValue.Create false

                Ok
                    { Enabled = false
                      Metrics = ChangeHealth.metrics 0 0 0 0 0 0 0 0 0 0 0 0
                      Findings = []
                      Node = node }
            else
                let ignoredPatterns = FileWorkConfigRepository.readTelemetryIgnoredPaths root

                match
                    ProcessGitRepository.readNameStatusDiff root summary.StartCommit,
                    ProcessGitRepository.readNumstatDiff root summary.StartCommit,
                    ProcessGitRepository.readUntrackedFiles root,
                    ProcessGitRepository.readZeroContextDiff root summary.StartCommit
                with
                | Ok nameStatusText, Ok numstatText, Ok untrackedText, Ok unifiedText ->
                    let tracked = parseNameStatus ignoredPatterns nameStatusText
                    let known = tracked |> List.map _.Path |> Set.ofList
                    let entries = tracked @ parseUntracked ignoredPatterns known untrackedText
                    let numstat = parseNumstat ignoredPatterns numstatText
                    let hunkMap = parseHunks policy.LineBucketSize ignoredPatterns unifiedText
                    let historyPath = historyFile root policy

                    match RegistryLock.acquire root "telemetry-change-history" RegistryLock.defaultSettings with
                    | Error failure -> Error failure.Message
                    | Ok lease ->
                        let result =
                            try
                                let history = parseHistory historyPath
                                let comparisonHistory =
                                    { history with
                                        Updates = history.Updates |> List.filter (fun update -> update.ExecutionId <> executionId) }

                                let details =
                                    entries
                                    |> List.map (fun entry ->
                                        let observedCurrentLines =
                                            if entry.Status = 'D' then None else countTextLines root entry.Path

                                        let added, deleted =
                                            if entry.Untracked then
                                                observedCurrentLines, Some 0
                                            else
                                                numstat |> Map.tryFind entry.Path |> Option.defaultValue (None, None)

                                        let currentLines =
                                            if not entry.Untracked && added.IsNone && deleted.IsNone then None
                                            else observedCurrentLines

                                        let churn =
                                            match added, deleted with
                                            | Some a, Some d -> Some(a + d)
                                            | _ -> None

                                        let hunks =
                                            match hunkMap |> Map.tryFind entry.Path with
                                            | Some values -> values
                                            | None when entry.Untracked ->
                                                let lines = currentLines |> Option.defaultValue 0
                                                if lines > 0 then
                                                    [ { OldStart = 0
                                                        OldLines = 0
                                                        NewStart = 1
                                                        NewLines = lines
                                                        Buckets = ChangeHealth.buckets policy.LineBucketSize 0 0 1 lines } ]
                                                else
                                                    []
                                            | None -> []

                                        let buckets = hunks |> List.collect _.Buckets |> List.distinct
                                        let recentTouches, maxRegionTouches =
                                            ChangeHealth.recentTouches policy comparisonHistory entry.Path entry.From buckets

                                        { Path = entry.Path
                                          From = entry.From
                                          Status = entry.Status
                                          LinesAdded = added
                                          LinesDeleted = deleted
                                          Churn = churn
                                          CurrentLines = currentLines
                                          Hunks = hunks
                                          RecentTouches = recentTouches
                                          MaxRegionTouches = maxRegionTouches })

                                let filesChanged = details.Length
                                let sourceFilesChanged = details |> List.filter (fun file -> ChangeHealth.isSourceFile file.Path) |> List.length
                                let testFilesChanged = details |> List.filter (fun file -> ChangeClassification.isTestFile file.Path) |> List.length
                                let hunksChanged = details |> List.sumBy (fun file -> file.Hunks.Length)
                                let maxHunksPerFile = details |> List.map (fun file -> file.Hunks.Length) |> List.fold max 0
                                let largestFileChurn = details |> List.choose _.Churn |> List.fold max 0
                                let largestChangedFileLines = details |> List.choose _.CurrentLines |> List.fold max 0
                                let repeatFileTouches = details |> List.map _.RecentTouches |> List.fold max 0
                                let repeatRegionTouches = details |> List.map _.MaxRegionTouches |> List.fold max 0

                                let metrics =
                                    ChangeHealth.metrics
                                        filesChanged
                                        sourceFilesChanged
                                        testFilesChanged
                                        summary.DocumentationFilesChanged
                                        summary.LinesAdded
                                        summary.LinesDeleted
                                        hunksChanged
                                        maxHunksPerFile
                                        largestFileChurn
                                        largestChangedFileLines
                                        repeatFileTouches
                                        repeatRegionTouches

                                let findings = ChangeHealth.evaluate policy metrics

                                let historyUpdate =
                                    { ExecutionId = executionId
                                      WorkItemId = workItemId
                                      FinalizedAt = finalizedAt
                                      StartCommit = summary.StartCommit
                                      EndCommit = summary.EndCommit
                                      Files =
                                        details
                                        |> List.map (fun file ->
                                            { Path = file.Path
                                              From = file.From
                                              Status = file.Status
                                              Hunks =
                                                file.Hunks
                                                |> List.map (fun hunk ->
                                                    { OldStart = hunk.OldStart
                                                      OldLines = hunk.OldLines
                                                      NewStart = hunk.NewStart
                                                      NewLines = hunk.NewLines
                                                      Buckets = hunk.Buckets }) }) }

                                let nextHistory = ChangeHealth.appendHistory policy historyUpdate history
                                writeHistoryAtomic historyPath nextHistory

                                let node = JsonObject()
                                node["schemaVersion"] <- JsonValue.Create "1.0.0"
                                node["available"] <- JsonValue.Create true
                                node["enabled"] <- JsonValue.Create true
                                node["status"] <-
                                    JsonValue.Create(
                                        if findings |> List.exists (fun finding -> finding.Severity = "error") then "error"
                                        elif findings |> List.exists (fun finding -> finding.Severity = "warning") then "warning"
                                        else "healthy"
                                    )
                                node["policy"] <- JsonValue.Create policyPath
                                node["historyWindow"] <- JsonValue.Create policy.HistoryWindow
                                node["lineBucketSize"] <- JsonValue.Create policy.LineBucketSize
                                node["metrics"] <- metricsNode metrics

                                let findingsNode = JsonArray()
                                findings |> List.iter (fun finding -> findingsNode.Add(findingNode finding: JsonNode))
                                node["findings"] <- findingsNode

                                let filesNode = JsonArray()
                                details |> List.iter (fun file -> filesNode.Add(fileNode file: JsonNode))
                                node["files"] <- filesNode

                                let historyNode = JsonObject()
                                historyNode["path"] <- JsonValue.Create policy.HistoryPath
                                historyNode["retainedUpdates"] <- JsonValue.Create nextHistory.Updates.Length
                                historyNode["historyOmitted"] <- JsonValue.Create nextHistory.HistoryOmitted
                                node["history"] <- historyNode

                                Ok
                                    { Enabled = true
                                      Metrics = metrics
                                      Findings = findings
                                      Node = node }
                            with error ->
                                Error error.Message

                        match lease.Release(), result with
                        | Error releaseFailure, Ok _ -> Error releaseFailure.Message
                        | _, outcome -> outcome
                | Error failure, _, _, _
                | _, Error failure, _, _
                | _, _, Error failure, _
                | _, _, _, Error failure ->
                    Error failure.Message)

    let hotspots (root: string) : Result<JsonObject, string> =
        loadPolicy root
        |> Result.map (fun (policy, policyPath) ->
            let history = parseHistory (historyFile root policy)
            let recent = history.Updates |> List.rev |> List.truncate policy.HistoryWindow |> List.rev

            let fileCounts =
                recent
                |> List.collect (fun update -> update.Files |> List.map (fun file -> file.Path, update.FinalizedAt))
                |> List.groupBy fst
                |> List.map (fun (path, values) ->
                    let timestamps = values |> List.map snd
                    path, values.Length, (timestamps |> List.max))
                |> List.sortByDescending (fun (path, count, last) -> count, last, path)

            let regionCounts =
                recent
                |> List.collect (fun update ->
                    update.Files
                    |> List.collect (fun file ->
                        file.Hunks
                        |> List.collect (fun hunk ->
                            hunk.Buckets |> List.map (fun bucket -> file.Path, bucket, update.FinalizedAt))))
                |> List.distinct
                |> List.groupBy (fun (path, bucket, _) -> path, bucket)
                |> List.map (fun ((path, bucket), values) ->
                    let timestamps = values |> List.map (fun (_, _, at) -> at)
                    path, bucket, values.Length, (timestamps |> List.max))
                |> List.sortByDescending (fun (path, bucket, count, last) -> count, last, path, bucket)

            let rootNode = JsonObject()
            rootNode["schemaVersion"] <- JsonValue.Create "1.0.0"
            rootNode["policy"] <- JsonValue.Create policyPath
            rootNode["historyWindow"] <- JsonValue.Create policy.HistoryWindow
            rootNode["retainedUpdates"] <- JsonValue.Create history.Updates.Length
            rootNode["historyOmitted"] <- JsonValue.Create history.HistoryOmitted

            let files = JsonArray()
            for path, touches, lastTouchedAt in fileCounts |> List.truncate 50 do
                let node = JsonObject()
                node["path"] <- JsonValue.Create path
                node["touches"] <- JsonValue.Create touches
                node["lastTouchedAt"] <- JsonValue.Create lastTouchedAt
                files.Add(node: JsonNode)
            rootNode["files"] <- files

            let regions = JsonArray()
            for path, bucket, touches, lastTouchedAt in regionCounts |> List.truncate 100 do
                let node = JsonObject()
                node["path"] <- JsonValue.Create path
                node["bucket"] <- JsonValue.Create bucket
                node["startLine"] <- JsonValue.Create(bucket * policy.LineBucketSize + 1)
                node["endLine"] <- JsonValue.Create((bucket + 1) * policy.LineBucketSize)
                node["touches"] <- JsonValue.Create touches
                node["lastTouchedAt"] <- JsonValue.Create lastTouchedAt
                regions.Add(node: JsonNode)
            rootNode["regions"] <- regions
            rootNode)
