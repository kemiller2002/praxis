namespace Ros.Domain.Telemetry

open System
open System.IO

type ChangeThreshold =
    { Warning: int option
      Error: int option }

type ChangeHealthThresholds =
    { FilesChanged: ChangeThreshold
      LinesChanged: ChangeThreshold
      LargestFileChurn: ChangeThreshold
      LargestChangedFileLines: ChangeThreshold
      HunksChanged: ChangeThreshold
      MaxHunksPerFile: ChangeThreshold
      RepeatFileTouches: ChangeThreshold
      RepeatRegionTouches: ChangeThreshold }

type ChangeHealthPolicy =
    { Enabled: bool
      HistoryPath: string
      HistoryWindow: int
      MaxHistoryUpdates: int
      LineBucketSize: int
      FailOnSeverity: string option
      Thresholds: ChangeHealthThresholds }

type ChangeHunk =
    { OldStart: int
      OldLines: int
      NewStart: int
      NewLines: int
      Buckets: int list }

type ChangeFileDetail =
    { Path: string
      From: string option
      Status: char
      LinesAdded: int option
      LinesDeleted: int option
      Churn: int option
      CurrentLines: int option
      Hunks: ChangeHunk list
      RecentTouches: int
      MaxRegionTouches: int }

type ChangeHealthMetrics =
    { FilesChanged: int
      SourceFilesChanged: int
      TestFilesChanged: int
      DocumentationFilesChanged: int
      LinesAdded: int
      LinesDeleted: int
      LinesChanged: int
      NetLines: int
      HunksChanged: int
      MaxHunksPerFile: int
      LargestFileChurn: int
      LargestChangedFileLines: int
      RepeatFileTouches: int
      RepeatRegionTouches: int }

type ChangeHealthFinding =
    { Code: string
      Metric: string
      Severity: string
      Actual: int
      Threshold: int
      Message: string
      Remediation: string }

type HistoricalHunk =
    { OldStart: int
      OldLines: int
      NewStart: int
      NewLines: int
      Buckets: int list }

type HistoricalFileTouch =
    { Path: string
      From: string option
      Status: char
      Hunks: HistoricalHunk list }

type HistoricalUpdate =
    { ExecutionId: string
      WorkItemId: string
      FinalizedAt: string
      StartCommit: string
      EndCommit: string
      Files: HistoricalFileTouch list }

type ChangeHistory =
    { SchemaVersion: string
      HistoryOmitted: int
      Updates: HistoricalUpdate list }

[<RequireQualifiedAccess>]
module ChangeHealth =
    let defaultPolicy =
        { Enabled = true
          HistoryPath = ".ros/telemetry/change-history.json"
          HistoryWindow = 20
          MaxHistoryUpdates = 200
          LineBucketSize = 25
          FailOnSeverity = None
          Thresholds =
            { FilesChanged = { Warning = Some 25; Error = Some 60 }
              LinesChanged = { Warning = Some 800; Error = Some 2000 }
              LargestFileChurn = { Warning = Some 300; Error = Some 800 }
              LargestChangedFileLines = { Warning = Some 800; Error = Some 1500 }
              HunksChanged = { Warning = Some 30; Error = Some 80 }
              MaxHunksPerFile = { Warning = Some 10; Error = Some 25 }
              RepeatFileTouches = { Warning = Some 5; Error = Some 10 }
              RepeatRegionTouches = { Warning = Some 3; Error = Some 6 } } }

    let emptyHistory =
        { SchemaVersion = "1.0.0"
          HistoryOmitted = 0
          Updates = [] }

    let private sourceExtensions =
        set
            [ ".fs"; ".fsx"; ".cs"; ".vb"; ".ts"; ".tsx"; ".js"; ".jsx"; ".mjs"; ".cjs"
              ".py"; ".java"; ".kt"; ".kts"; ".go"; ".rs"; ".c"; ".cc"; ".cpp"; ".cxx"
              ".h"; ".hh"; ".hpp"; ".swift"; ".scala"; ".rb"; ".php"; ".sql"; ".html"
              ".htm"; ".css"; ".scss"; ".sass"; ".less"; ".razor"; ".vue"; ".svelte" ]

    let isSourceFile (path: string) =
        if ChangeClassification.isTestFile path || ChangeClassification.isDocumentation path then
            false
        else
            sourceExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())

    let buckets (bucketSize: int) (oldStart: int) (oldLines: int) (newStart: int) (newLines: int) =
        let size = max 1 bucketSize
        let start = if newLines > 0 then newStart else oldStart
        let length = max 1 (if newLines > 0 then newLines else oldLines)
        let first = max 0 ((max 1 start - 1) / size)
        let last = max first ((max 1 start + length - 2) / size)
        [ first .. last ]

    let private aliases (file: HistoricalFileTouch) =
        match file.From with
        | Some from -> Set.ofList [ file.Path; from ]
        | None -> Set.singleton file.Path

    let private currentAliases (path: string) (from: string option) =
        match from with
        | Some original -> Set.ofList [ path; original ]
        | None -> Set.singleton path

    let private intersects left right = not (Set.intersect left right).IsEmpty

    let recentTouches (policy: ChangeHealthPolicy) (history: ChangeHistory) (path: string) (from: string option) (currentBuckets: int list) =
        let recent = history.Updates |> List.rev |> List.truncate policy.HistoryWindow
        let names = currentAliases path from

        let fileTouches =
            recent
            |> List.filter (fun update -> update.Files |> List.exists (fun file -> intersects names (aliases file)))
            |> List.length
            |> (+) 1

        let regionTouches =
            currentBuckets
            |> List.map (fun bucket ->
                recent
                |> List.filter (fun update ->
                    update.Files
                    |> List.exists (fun file ->
                        intersects names (aliases file)
                        && file.Hunks |> List.exists (fun hunk -> List.contains bucket hunk.Buckets)))
                |> List.length
                |> (+) 1)
            |> function
                | [] -> 1
                | values -> List.max values

        fileTouches, regionTouches

    let private finding code metric severity actual threshold remediation =
        { Code = code
          Metric = metric
          Severity = severity
          Actual = actual
          Threshold = threshold
          Message = $"{metric} is {actual}, exceeding the {severity} threshold of {threshold}."
          Remediation = remediation }

    let private evaluateOne code metric threshold actual remediation =
        match threshold.Error, threshold.Warning with
        | Some limit, _ when actual > limit -> Some(finding code metric "error" actual limit remediation)
        | _, Some limit when actual > limit -> Some(finding code metric "warning" actual limit remediation)
        | _ -> None

    let evaluate (policy: ChangeHealthPolicy) (metrics: ChangeHealthMetrics) =
        if not policy.Enabled then
            []
        else
            [ evaluateOne "PRAXIS-CHG-001" "filesChanged" policy.Thresholds.FilesChanged metrics.FilesChanged "Review whether the change should be decomposed into independently verifiable updates."
              evaluateOne "PRAXIS-CHG-002" "linesChanged" policy.Thresholds.LinesChanged metrics.LinesChanged "Review the change boundary, evidence, and test scope; split unrelated work."
              evaluateOne "PRAXIS-CHG-003" "largestFileChurn" policy.Thresholds.LargestFileChurn metrics.LargestFileChurn "Inspect the highest-churn file for mixed responsibilities or unstable boundaries."
              evaluateOne "PRAXIS-CHG-004" "largestChangedFileLines" policy.Thresholds.LargestChangedFileLines metrics.LargestChangedFileLines "Review the largest changed file for decomposition opportunities."
              evaluateOne "PRAXIS-CHG-005" "hunksChanged" policy.Thresholds.HunksChanged metrics.HunksChanged "Review whether widely scattered edits indicate excessive scope."
              evaluateOne "PRAXIS-CHG-006" "maxHunksPerFile" policy.Thresholds.MaxHunksPerFile metrics.MaxHunksPerFile "Inspect the file with the most separated edits for cohesion and testability."
              evaluateOne "PRAXIS-CHG-007" "repeatFileTouches" policy.Thresholds.RepeatFileTouches metrics.RepeatFileTouches "Review repeatedly changed files for architectural hotspots, ownership friction, or missing abstractions."
              evaluateOne "PRAXIS-CHG-008" "repeatRegionTouches" policy.Thresholds.RepeatRegionTouches metrics.RepeatRegionTouches "Review repeatedly changed line regions for unstable logic or concentrated maintenance risk." ]
            |> List.choose id

    let metrics
        filesChanged
        sourceFilesChanged
        testFilesChanged
        documentationFilesChanged
        linesAdded
        linesDeleted
        hunksChanged
        maxHunksPerFile
        largestFileChurn
        largestChangedFileLines
        repeatFileTouches
        repeatRegionTouches
        =
        { FilesChanged = filesChanged
          SourceFilesChanged = sourceFilesChanged
          TestFilesChanged = testFilesChanged
          DocumentationFilesChanged = documentationFilesChanged
          LinesAdded = linesAdded
          LinesDeleted = linesDeleted
          LinesChanged = linesAdded + linesDeleted
          NetLines = linesAdded - linesDeleted
          HunksChanged = hunksChanged
          MaxHunksPerFile = maxHunksPerFile
          LargestFileChurn = largestFileChurn
          LargestChangedFileLines = largestChangedFileLines
          RepeatFileTouches = repeatFileTouches
          RepeatRegionTouches = repeatRegionTouches }

    let appendHistory (policy: ChangeHealthPolicy) (update: HistoricalUpdate) (history: ChangeHistory) =
        // Retrying finalization for the same execution replaces its prior
        // history observation instead of double-counting one logical update.
        let withoutSameExecution =
            history.Updates |> List.filter (fun existing -> existing.ExecutionId <> update.ExecutionId)

        let updates = withoutSameExecution @ [ update ]
        let overflow = max 0 (updates.Length - policy.MaxHistoryUpdates)

        { SchemaVersion = "1.0.0"
          HistoryOmitted = history.HistoryOmitted + overflow
          Updates = if overflow > 0 then updates |> List.skip overflow else updates }

    let severityRank severity =
        match severity with
        | "error" -> 2
        | "warning" -> 1
        | _ -> 0

    let shouldFail (policy: ChangeHealthPolicy) findings =
        match policy.FailOnSeverity with
        | None -> false
        | Some severity ->
            let threshold = severityRank severity
            findings |> List.exists (fun finding -> severityRank finding.Severity >= threshold)
