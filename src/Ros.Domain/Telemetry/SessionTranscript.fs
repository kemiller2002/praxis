namespace Ros.Domain.Telemetry

open System
open System.Text.RegularExpressions

/// One tool call a transcript records, reduced to the only arguments the
/// session metrics read. The command text is inspected in memory and never
/// stored (`docs/development-telemetry.md`, privacy).
type TranscriptToolUse =
    { Name: string
      FilePath: string option
      Command: string option }

type TranscriptUsage =
    { Input: int64
      Output: int64
      CacheRead: int64
      CacheCreation: int64 }

/// One Claude Code transcript line, reduced to content-free facts.
type TranscriptEntry =
    { Kind: string
      Subtype: string option
      Timestamp: DateTimeOffset option
      Cwd: string option
      SessionId: string option
      RuntimeVersion: string option
      /// `message.id`, else `requestId`, else `uuid`: usage repeated once per
      /// content block is counted once per request.
      RequestKey: string option
      Model: string option
      Usage: TranscriptUsage option
      ToolUses: TranscriptToolUse list
      ToolErrors: int
      IsCompactSummary: bool
      /// `totalCostUSD` of a `cost-state` line: the runtime's own estimate.
      CostUsd: decimal option }

type SessionSummary =
    { SessionId: string option
      RuntimeVersion: string option
      Models: string list
      StartedAt: DateTimeOffset option
      EndedAt: DateTimeOffset option
      SpanMs: int64 option
      ActiveMs: int64 option
      IdleGapsExcluded: int
      MsToFirstCodeChange: int64 option
      ModelRequests: int
      Tokens: TranscriptUsage option
      CostUsd: decimal option
      ToolCalls: (string * int) list
      ToolErrors: int
      ShellCommands: int
      FileReads: int
      DistinctFilesRead: int
      RepeatedReads: (string * int) list
      GovernanceReads: (string * int) list
      Searches: int
      FileWrites: int
      Builds: int
      TestRuns: int
      Compactions: int }

/// One normalized measurement the session adapter emits. `Value = None`
/// declares the metric recognized but unavailable in this transcript; it is
/// never recorded as zero.
type SessionMeasurement =
    { MetricId: string
      Value: decimal option
      Quality: string
      Currency: string option }

/// PRAXIS-PLAN-05: the `anthropic-claude-session` telemetry adapter's pure
/// half. It promotes EX-ROS-2026-A021's `session_metrics.py` (EV-ROS-2026-A064)
/// into Praxis: repeated file reads, governance reads, time to first code
/// change, model requests, tool calls, compactions and tokens, derived
/// deterministically from a Claude Code session transcript. Nothing is
/// inferred by a model; a value the transcript does not carry is unavailable.
[<RequireQualifiedAccess>]
module SessionTranscript =
    let adapterName = "anthropic-claude-session"

    /// A whole-session transcript is far larger than a status-line snapshot.
    /// The adapter keeps only a content-free summary as its raw snapshot, so
    /// the input may exceed the raw-payload budget up to this bound.
    let maxInputBytes = 64 * 1024 * 1024

    /// A gap between consecutive transcript entries longer than this is idle
    /// (waiting on a person, a permission prompt, an orchestrator) and is not
    /// counted as active session time.
    let idleGapMs = 15L * 60_000L

    /// The model name the runtime writes on messages it synthesizes itself.
    let syntheticModel = "<synthetic>"

    /// The input budget for an adapter: this adapter's own bound, every
    /// other adapter the configured raw-payload budget.
    let inputLimit (adapter: string) (configured: int) =
        if adapter = adapterName then max configured maxInputBytes else configured

    /// Startup and governance documents whose reads are the per-session
    /// context overhead that grouping can save (EV-ROS-2026-A064).
    let governancePrefixes =
        [ "AGENTS.md"
          "CLAUDE.md"
          "docs/00-governance/"
          "docs/work-protocol.md"
          "docs/planning.md"
          "docs/cli.md"
          "requirements/PLANNING-WORK-GROUPS.md"
          "docs/development-telemetry.md"
          "docs/agent-provenance.md" ]

    // A read or search starts a command (after ^ ; & && or "("), never a
    // pipe: "| head" and "| grep" filter output rather than read files.
    let private readCommand =
        Regex(@"(?:^|[;&(]\s*)(?:cat|head|tail|less|sed\s+-n\s+'?[0-9,$p]+'?)((?:\s+[^\s;|&<>]+)+)", RegexOptions.CultureInvariant)

    let private searchCommand = Regex(@"(?:^|[;&(]\s*)(?:grep|rg|find|git\s+grep)\b", RegexOptions.CultureInvariant)
    let private pathToken = Regex(@"^[\w./-]+\.[\w]+$|^[\w.-]+/[\w./-]+$", RegexOptions.CultureInvariant)
    let private buildCommand = Regex(@"dotnet\s+build|build:fsharp", RegexOptions.CultureInvariant)
    let private testCommand = Regex(@"Ros\.Tests\.dll|node\s+--test|dotnet\s+test|npm\s+(?:run\s+)?test", RegexOptions.CultureInvariant)

    let private writeCommand =
        Regex(@"(?:cat|tee)\s+>{1,2}\s*\S+|sed\s+-i|open\([^)]*['""]w['""]|>\s*(?:src|tests)/", RegexOptions.CultureInvariant)

    let private codeDirectoryMention = Regex(@"\b(?:src|tests)/", RegexOptions.CultureInvariant)

    let private editTools = set [ "Edit"; "Write"; "NotebookEdit" ]
    let private searchTools = set [ "Grep"; "Glob" ]

    let relative (cwd: string option) (path: string) =
        match cwd |> Option.map (fun value -> value.TrimEnd '/' + "/") with
        | Some prefix when path.StartsWith(prefix, StringComparison.Ordinal) -> path.Substring prefix.Length
        | _ -> path

    let isGovernance (path: string) =
        governancePrefixes |> List.exists (fun prefix -> path.StartsWith(prefix, StringComparison.Ordinal))

    let private isCodePath (path: string) =
        path.StartsWith("src/", StringComparison.Ordinal) || path.StartsWith("tests/", StringComparison.Ordinal)

    /// Files a shell command reads: path-like arguments of cat/head/tail/
    /// less/`sed -n` at the start of a command.
    let shellReads (command: string) : string list =
        readCommand.Matches command
        |> Seq.collect (fun m -> m.Groups[1].Value.Split([| ' '; '\t'; '\n' |], StringSplitOptions.RemoveEmptyEntries))
        |> Seq.filter (fun token -> not (token.StartsWith("-", StringComparison.Ordinal)) && pathToken.IsMatch token)
        |> Seq.toList

    /// What one tool call contributes, classified once.
    type private ToolFacts =
        { Reads: string list
          Searches: int
          Writes: string list
          ShellWrite: bool
          Build: bool
          Test: bool
          Shell: bool
          CodeChange: bool }

    let private toolFacts (cwd: string option) (tool: TranscriptToolUse) : ToolFacts =
        let none =
            { Reads = []
              Searches = 0
              Writes = []
              ShellWrite = false
              Build = false
              Test = false
              Shell = false
              CodeChange = false }

        let path = tool.FilePath |> Option.map (relative cwd)

        match tool.Name with
        | "Read" -> { none with Reads = path |> Option.toList }
        | name when searchTools.Contains name -> { none with Searches = 1 }
        | name when editTools.Contains name ->
            let written = path |> Option.defaultValue ""
            { none with Writes = [ written ]; CodeChange = isCodePath written }
        | "Bash" ->
            let command = tool.Command |> Option.defaultValue ""
            let shellWrite = writeCommand.IsMatch command

            { none with
                Reads = shellReads command |> List.map (relative cwd)
                Searches = searchCommand.Matches(command).Count
                ShellWrite = shellWrite
                Build = buildCommand.IsMatch command
                Test = testCommand.IsMatch command
                Shell = true
                CodeChange = shellWrite && codeDirectoryMention.IsMatch command }
        | _ -> none

    /// Active time: the sum of gaps between consecutive entries, leaving out
    /// every gap longer than `idleGapMs`. Returns (active ms, idle gaps).
    let activeTime (stamps: DateTimeOffset list) : int64 * int =
        stamps
        |> List.sort
        |> List.pairwise
        |> List.map (fun (earlier, later) -> int64 (later - earlier).TotalMilliseconds)
        |> List.fold (fun (active, idle) gap -> if gap > idleGapMs then active, idle + 1 else active + gap, idle) (0L, 0)

    let private counts (values: string list) =
        values |> List.countBy id |> List.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right))

    let summarize (entries: TranscriptEntry list) : SessionSummary =
        let firstSome selector = entries |> List.tryPick selector
        let cwd = firstSome (fun entry -> entry.Cwd)
        let stamps = entries |> List.choose (fun entry -> entry.Timestamp)
        let startedAt = if stamps.IsEmpty then None else Some(List.min stamps)
        let endedAt = if stamps.IsEmpty then None else Some(List.max stamps)
        let assistant = entries |> List.filter (fun entry -> entry.Kind = "assistant")

        // Last usage per request wins, matching the harness script.
        let usage =
            assistant
            |> List.fold
                (fun (seen: Map<string, TranscriptUsage>) entry ->
                    match entry.RequestKey, entry.Usage with
                    | Some key, Some value -> seen.Add(key, value)
                    | _ -> seen)
                Map.empty

        let calls =
            assistant
            |> List.collect (fun entry -> entry.ToolUses |> List.map (fun tool -> entry.Timestamp, tool, toolFacts cwd tool))

        let facts = calls |> List.map (fun (_, _, fact) -> fact)
        let reads = facts |> List.collect (fun fact -> fact.Reads) |> List.filter (String.IsNullOrEmpty >> not) |> counts
        let span = Option.map2 (fun (started: DateTimeOffset) ended -> int64 (ended - started).TotalMilliseconds) startedAt endedAt
        let active, idle = activeTime stamps

        let firstCodeChange =
            calls
            |> List.tryPick (fun (at, _, fact) -> if fact.CodeChange then at else None)

        let sumTokens (values: TranscriptUsage list) =
            values
            |> List.fold
                (fun total value ->
                    { Input = total.Input + value.Input
                      Output = total.Output + value.Output
                      CacheRead = total.CacheRead + value.CacheRead
                      CacheCreation = total.CacheCreation + value.CacheCreation })
                { Input = 0L; Output = 0L; CacheRead = 0L; CacheCreation = 0L }

        { SessionId = firstSome (fun entry -> entry.SessionId)
          RuntimeVersion = firstSome (fun entry -> entry.RuntimeVersion)
          Models = assistant |> List.choose (fun entry -> entry.Model) |> List.filter ((<>) syntheticModel) |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
          StartedAt = startedAt
          EndedAt = endedAt
          SpanMs = span
          ActiveMs = span |> Option.map (fun _ -> active)
          IdleGapsExcluded = idle
          MsToFirstCodeChange = Option.map2 (fun (started: DateTimeOffset) (changed: DateTimeOffset) -> int64 (changed - started).TotalMilliseconds) startedAt firstCodeChange
          ModelRequests = usage.Count
          Tokens = if usage.IsEmpty then None else Some(usage |> Map.toList |> List.map snd |> sumTokens)
          CostUsd = entries |> List.choose (fun entry -> entry.CostUsd) |> List.tryLast
          ToolCalls = calls |> List.map (fun (_, tool, _) -> tool.Name) |> counts
          ToolErrors = entries |> List.filter (fun entry -> entry.Kind = "user") |> List.sumBy (fun entry -> entry.ToolErrors)
          ShellCommands = facts |> List.filter (fun fact -> fact.Shell) |> List.length
          FileReads = reads |> List.sumBy snd
          DistinctFilesRead = reads.Length
          RepeatedReads = reads |> List.filter (fun (_, count) -> count > 1)
          GovernanceReads = reads |> List.filter (fst >> isGovernance)
          Searches = facts |> List.sumBy (fun fact -> fact.Searches)
          FileWrites = (facts |> List.sumBy (fun fact -> fact.Writes.Length)) + (facts |> List.filter (fun fact -> fact.ShellWrite) |> List.length)
          Builds = facts |> List.filter (fun fact -> fact.Build) |> List.length
          TestRuns = facts |> List.filter (fun fact -> fact.Test) |> List.length
          Compactions =
            entries
            |> List.filter (fun entry ->
                entry.IsCompactSummary
                || (entry.Kind = "system" && entry.Subtype |> Option.exists (fun subtype -> subtype.Contains("compact", StringComparison.Ordinal))))
            |> List.length }

    /// Reads beyond the first of each file: the repeated context a later
    /// read re-acquires.
    let repeatedReadCount (summary: SessionSummary) =
        summary.RepeatedReads |> List.sumBy (fun (_, count) -> count - 1)

    /// The normalized measurements, in a stable order. Counts that the
    /// transcript records directly are `observed`; counts pattern-matched
    /// from shell commands, and time derived from entry timestamps, are
    /// `derived`; the runtime's own cost is an `estimated` cumulative
    /// session cost, never `cost.execution_total`.
    let measurements (summary: SessionSummary) : SessionMeasurement list =
        let measured metricId quality (value: decimal option) =
            { MetricId = metricId
              Value = value
              Quality = quality
              Currency = None }

        let count metricId quality (value: int) = measured metricId quality (Some(decimal value))
        let tokens selector = summary.Tokens |> Option.map (selector >> decimal)
        let transcriptSeen = summary.StartedAt.IsSome

        [ count "model.requests" "observed" summary.ModelRequests
          measured "tokens.input" "observed" (tokens (fun usage -> usage.Input))
          measured "tokens.output" "observed" (tokens (fun usage -> usage.Output))
          measured "tokens.cache_read" "observed" (tokens (fun usage -> usage.CacheRead))
          measured "tokens.cache_write" "observed" (tokens (fun usage -> usage.CacheCreation))
          count "tool.calls" "observed" (summary.ToolCalls |> List.sumBy snd)
          count "tool.failures" "observed" summary.ToolErrors
          count "tool.shell_commands" "observed" summary.ShellCommands
          count "tool.file_reads" "derived" summary.FileReads
          count "tool.file_writes" "derived" summary.FileWrites
          count "tool.searches" "derived" summary.Searches
          count "tool.build_executions" "derived" summary.Builds
          count "tool.test_executions" "derived" summary.TestRuns
          count "context.compactions" "observed" summary.Compactions
          count "context.repeated_file_reads" "derived" (repeatedReadCount summary)
          count "context.governance_reads" "derived" (summary.GovernanceReads |> List.sumBy snd)
          measured "time.first_code_change_ms" "derived" (summary.MsToFirstCodeChange |> Option.map decimal)
          measured "time.active_ms" "derived" (summary.ActiveMs |> Option.map decimal)
          { MetricId = "cost.session_cumulative"
            Value = summary.CostUsd
            Quality = "estimated"
            Currency = Some "USD" } ]
        |> List.map (fun measurement -> if transcriptSeen then measurement else { measurement with Value = None })
