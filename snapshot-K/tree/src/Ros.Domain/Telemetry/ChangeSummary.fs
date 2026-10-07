namespace Ros.Domain.Telemetry

open System.Text.RegularExpressions

/// One entry from `git diff --name-status` (or a synthesized untracked-file
/// entry), after ignored-path filtering. Mirrors production's own `entries`
/// array in `cleanBaselineChanges` (`tools/ros_telemetry.mjs`).
type ChangeEntry =
    { Status: char
      Path: string
      Untracked: bool }

/// One raw `git diff --numstat` line, pre-aggregation. `Added`/`Deleted`
/// stay as strings because production's own binary-file sentinel is the
/// literal `"-"`, checked before any numeric parsing.
type NumstatLine =
    { Added: string
      Deleted: string
      File: string }

type ChangeCounts =
    { Added: int
      Modified: int
      Deleted: int
      Renamed: int }

type TestChangeCounts =
    { Added: int
      Modified: int
      Removed: int }

type ChangeSummary =
    { Mechanism: string
      StartCommit: string
      EndCommit: string
      Commits: int
      Counts: ChangeCounts
      LinesAdded: int
      LinesDeleted: int
      BinaryFiles: int
      Tests: TestChangeCounts
      DocumentationFilesChanged: int }

/// Whether an untracked file (read directly off disk, since it has no
/// numstat entry of its own) is binary, and if not, its line count.
/// Mirrors production `lineCount`.
type UntrackedLineCount =
    { Binary: bool
      Lines: int }

/// Mirrors production `isTestFile`/`isDocumentation` (`tools/
/// ros_telemetry.mjs`) exactly -- both classifications are used only to
/// bucket already-collected change entries, never to decide whether a
/// change is collected at all.
[<RequireQualifiedAccess>]
module ChangeClassification =
    let private testDirectory = Regex(@"(^|/)(?:test|tests|__tests__)(/|$)", RegexOptions.IgnoreCase)
    let private testSuffix = Regex(@"(?:^|[._-])(?:test|spec)\.[^/]+$", RegexOptions.IgnoreCase)
    let isTestFile (path: string) = testDirectory.IsMatch path || testSuffix.IsMatch path

    let private docsDirectory = Regex(@"(^|/)docs?/", RegexOptions.IgnoreCase)
    let private readme = Regex(@"(^|/)readme(?:\.[^/]*)?$", RegexOptions.IgnoreCase)
    let private docExtension = Regex(@"\.(?:md|mdx|rst|adoc)$", RegexOptions.IgnoreCase)
    let isDocumentation (path: string) = docsDirectory.IsMatch path || readme.IsMatch path || docExtension.IsMatch path

/// Ports production's own text parsing for each of the three git reads
/// `cleanBaselineChanges` performs, and the aggregation that follows.
/// Reduced to only the fields production's own `finalizeExecution` caller
/// actually reads (`commits`, `counts`, `linesAdded`/`linesDeleted`,
/// `binaryFiles`, `tests`, `documentationFilesChanged`) -- `filesByType`
/// and per-file `paths` exist on production's return value but are never
/// consumed by the one caller in this migration's current scope, so they
/// are not modeled here.
[<RequireQualifiedAccess>]
module ChangeSummaryParser =
    let private isIgnored (ignoredPatterns: string list) (path: string) =
        ignoredPatterns |> List.exists (fun pattern -> Ros.Domain.Work.PathFilter.globMatch pattern path)

    let parseNameStatus (ignoredPatterns: string list) (text: string) : ChangeEntry list =
        if text = "" then
            []
        else
            text.Split '\n'
            |> Array.choose (fun line ->
                if line = "" then
                    None
                else
                    let parts = line.Split '\t'
                    let code = parts.[0].[0]
                    let first = if parts.Length > 1 then parts.[1] else ""
                    let second = if parts.Length > 2 then parts.[2] else ""
                    let file = if code = 'R' || code = 'C' then second else first

                    if isIgnored ignoredPatterns file then
                        None
                    else
                        Some { Status = code; Path = file; Untracked = false })
            |> Array.toList

    let parseNumstat (text: string) : NumstatLine list =
        if text = "" then
            []
        else
            text.Split '\n'
            |> Array.choose (fun line ->
                if line = "" then
                    None
                else
                    let parts = line.Split '\t'
                    if parts.Length < 3 then
                        None
                    else
                        Some
                            { Added = parts.[0]
                              Deleted = parts.[1]
                              File = parts.[parts.Length - 1] })
            |> Array.toList

    let parseUntracked (text: string) : string list =
        if text = "" then [] else text.Split '\000' |> Array.filter (fun value -> value <> "") |> Array.toList

    let compute
        (ignoredPatterns: string list)
        (nameStatusEntries: ChangeEntry list)
        (numstatLines: NumstatLine list)
        (untrackedPaths: string list)
        (untrackedLineCounts: Map<string, UntrackedLineCount>)
        (startCommit: string)
        (endCommit: string)
        (commits: int)
        : ChangeSummary =
        let known = nameStatusEntries |> List.map (fun entry -> entry.Path) |> Set.ofList

        let untrackedEntries =
            untrackedPaths
            |> List.filter (fun path -> not (isIgnored ignoredPatterns path) && not (known.Contains path))
            |> List.map (fun path -> { Status = 'A'; Path = path; Untracked = true })

        let entries = nameStatusEntries @ untrackedEntries

        let numstatLinesAdded, numstatLinesDeleted, numstatBinaryFiles =
            numstatLines
            |> List.filter (fun line -> not (isIgnored ignoredPatterns line.File))
            |> List.fold
                (fun (linesAdded, linesDeleted, binaryFiles) line ->
                    if line.Added = "-" || line.Deleted = "-" then
                        (linesAdded, linesDeleted, binaryFiles + 1)
                    else
                        (linesAdded + int line.Added, linesDeleted + int line.Deleted, binaryFiles))
                (0, 0, 0)

        let untrackedLinesAdded, untrackedBinaryFiles =
            entries
            |> List.filter (fun entry -> entry.Untracked)
            |> List.fold
                (fun (linesAdded, binaryFiles) entry ->
                    match untrackedLineCounts |> Map.tryFind entry.Path with
                    | Some counted when counted.Binary -> (linesAdded, binaryFiles + 1)
                    | Some counted -> (linesAdded + counted.Lines, binaryFiles)
                    | None -> (linesAdded, binaryFiles))
                (0, 0)

        let counts =
            entries
            |> List.fold
                (fun (acc: ChangeCounts) entry ->
                    match entry.Status with
                    | 'A' -> { acc with Added = acc.Added + 1 }
                    | 'D' -> { acc with Deleted = acc.Deleted + 1 }
                    | 'R' -> { acc with Renamed = acc.Renamed + 1 }
                    | _ -> { acc with Modified = acc.Modified + 1 })
                { Added = 0; Modified = 0; Deleted = 0; Renamed = 0 }

        let tests =
            entries
            |> List.filter (fun entry -> ChangeClassification.isTestFile entry.Path)
            |> List.fold
                (fun (acc: TestChangeCounts) entry ->
                    match entry.Status with
                    | 'A' -> { acc with Added = acc.Added + 1 }
                    | 'D' -> { acc with Removed = acc.Removed + 1 }
                    | _ -> { acc with Modified = acc.Modified + 1 })
                { Added = 0; Modified = 0; Removed = 0 }

        let documentationFilesChanged =
            entries |> List.filter (fun entry -> ChangeClassification.isDocumentation entry.Path) |> List.length

        { Mechanism = "git-diff-from-clean-execution-baseline"
          StartCommit = startCommit
          EndCommit = endCommit
          Commits = commits
          Counts = counts
          LinesAdded = numstatLinesAdded + untrackedLinesAdded
          LinesDeleted = numstatLinesDeleted
          BinaryFiles = numstatBinaryFiles + untrackedBinaryFiles
          Tests = tests
          DocumentationFilesChanged = documentationFilesChanged }

/// Mirrors production `blockedDuration` (`tools/ros_telemetry.mjs`): total
/// milliseconds spent in a `work.blocked` interval, closed by the next
/// `work.resumed` event or -- if still open -- by `finalizedAt`. With no
/// `work.blocked`/`work.resumed` events in an execution's own history
/// (this migration has not yet ported `recordTelemetryLifecycle`, the
/// effect that would write them), this always computes to zero -- a
/// correct answer, not a stub, for the data this migration currently
/// produces.
type LifecycleEvent =
    { Type: string
      OccurredAt: string }

[<RequireQualifiedAccess>]
module BlockedDuration =
    let private toEpochMilliseconds (timestamp: string) =
        System.DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds()

    let compute (events: LifecycleEvent list) (finalizedAt: string) : int64 =
        let sorted = events |> List.sortBy (fun event -> event.OccurredAt)

        let rec loop blockedAt total remaining =
            match remaining with
            | [] ->
                match blockedAt with
                | Some at -> total + max 0L (toEpochMilliseconds finalizedAt - at)
                | None -> total
            | event :: rest ->
                match event.Type, blockedAt with
                | "work.blocked", None -> loop (Some(toEpochMilliseconds event.OccurredAt)) total rest
                | "work.resumed", Some at -> loop None (total + max 0L (toEpochMilliseconds event.OccurredAt - at)) rest
                | _ -> loop blockedAt total rest

        loop None 0L sorted
