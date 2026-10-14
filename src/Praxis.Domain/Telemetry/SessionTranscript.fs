namespace Praxis.Domain.Telemetry

open System
open System.Text.RegularExpressions

/// One tool call a transcript records, reduced to the only arguments the
/// session metrics read. The command text is inspected in memory and never
/// stored (`docs/development-telemetry.md`, privacy).
type TranscriptToolUse =
    { /// The provider's `tool_use` block ID: a call split over several
      /// transcript entries is counted once.
      Id: string option
      Name: string
      FilePath: string option
      Command: string option }

/// One request's provider-reported usage. Every bucket is optional: a field
/// the provider did not report is unknown, never zero. Cache read and cache
/// write are their own buckets and are never folded into input; the TTL
/// split partitions cache write, and reasoning is a subset of output.
type TranscriptUsage =
    { Input: int64 option
      Output: int64 option
      CacheRead: int64 option
      CacheCreation: int64 option
      CacheCreation5m: int64 option
      CacheCreation1h: int64 option
      /// `output_tokens_details.thinking_tokens`, when the provider exposes it.
      Reasoning: int64 option
      /// Numeric usage fields this adapter does not normalize, by dotted
      /// path (e.g. `server_tool_use.web_search_requests`). Preserved through
      /// the sanitized raw layer, never promoted silently.
      Unmapped: (string * int64) list
      /// Textual usage fields (`service_tier`, `speed`, `inference_geo`).
      Labels: (string * string) list }

[<RequireQualifiedAccess>]
module TranscriptUsage =
    let empty =
        { Input = None
          Output = None
          CacheRead = None
          CacheCreation = None
          CacheCreation5m = None
          CacheCreation1h = None
          Reasoning = None
          Unmapped = []
          Labels = [] }

/// One Claude Code transcript line, reduced to content-free facts.
type TranscriptEntry =
    { Kind: string
      Subtype: string option
      Timestamp: DateTimeOffset option
      Cwd: string option
      SessionId: string option
      RuntimeVersion: string option
      /// `message.id`, else `requestId`, else `uuid`: usage repeated once per
      /// content block (streaming) is counted once per request.
      RequestKey: string option
      /// `agentId` of a subagent (sidechain) transcript entry.
      AgentId: string option
      IsSidechain: bool
      Model: string option
      Usage: TranscriptUsage option
      ToolUses: TranscriptToolUse list
      ToolErrors: int
      IsCompactSummary: bool
      /// `totalCostUSD` of a `cost-state` line: the runtime's own estimate.
      CostUsd: decimal option
      /// `retryAttempt` of an `api_error` system entry, when present.
      RetryAttempt: int option }

[<RequireQualifiedAccess>]
module TranscriptEntry =
    /// The kind the reader gives a line that is not a JSON object: counted,
    /// never parsed, never fatal.
    let malformedKind = "praxis.malformed-line"

    let malformed =
        { Kind = malformedKind
          Subtype = None
          Timestamp = None
          Cwd = None
          SessionId = None
          RuntimeVersion = None
          RequestKey = None
          AgentId = None
          IsSidechain = false
          Model = None
          Usage = None
          ToolUses = []
          ToolErrors = 0
          IsCompactSummary = false
          CostUsd = None
          RetryAttempt = None }

/// One provider request after deduplication: the unit every usage total
/// counts exactly once. Its key is the provider's own message ID, so a
/// parent and child that both hold it can be reconciled (`RequestAttribution`).
type TranscriptRequest =
    { Key: string
      Model: string option
      AgentId: string option
      Usage: TranscriptUsage }

/// One usage bucket summed over a session's requests. The sum is a usable
/// total only when every request reported the bucket (`TokenTotal.complete`).
type TokenTotal =
    { Sum: int64
      Reporting: int
      Requests: int }

type TokenTotals =
    { Input: TokenTotal
      Output: TokenTotal
      CacheRead: TokenTotal
      CacheWrite: TokenTotal
      CacheWrite5m: TokenTotal
      CacheWrite1h: TokenTotal
      Reasoning: TokenTotal }

[<RequireQualifiedAccess>]
module TokenTotal =
    let ofRequests (selector: TranscriptUsage -> int64 option) (requests: TranscriptRequest list) : TokenTotal =
        let known = requests |> List.choose (fun request -> selector request.Usage)

        { Sum = List.sum known
          Reporting = known.Length
          Requests = requests.Length }

    /// `Some` only when every request reported the bucket: a partially
    /// reported bucket is unknown, never a silently low total.
    let complete (total: TokenTotal) =
        if total.Requests > 0 && total.Reporting = total.Requests then Some total.Sum else None

    let unavailableReason (total: TokenTotal) =
        if total.Requests = 0 then "the transcript carries no provider request with usage"
        elif total.Reporting = 0 then "the provider did not report this usage bucket on any request"
        else $"the provider reported this usage bucket on {total.Reporting} of {total.Requests} requests"

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
      /// To the first change under the repository's configured meaningful
      /// paths (PRX-GRP-151), instead of the fixed `src/` and `tests/`.
      MsToFirstProductiveChange: int64 option
      /// The largest context one request carried: its input plus cache
      /// read and cache creation tokens (PRX-GRP-151).
      PeakContextTokens: int64 option
      ModelRequests: int
      /// Every deduplicated provider request, in first-seen order.
      Requests: TranscriptRequest list
      Tokens: TokenTotals option
      /// Unnormalized numeric usage fields summed over requests, by path.
      UsageExtensions: (string * int64) list
      /// Distinct values of textual usage fields, by name.
      UsageLabels: (string * string list) list
      /// Distinct subagent IDs on the transcript's entries.
      AgentIds: string list
      /// Every entry is a sidechain (subagent) entry: a child's transcript.
      Sidechain: bool
      /// Lines that were not JSON objects: skipped, counted, never fatal.
      MalformedLines: int
      /// `api_error` system entries, and those that name a retry attempt.
      ApiErrors: int
      Retries: int
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
      Currency: string option
      /// Why `Value` is `None`, when the adapter knows more than "absent".
      UnavailableReason: string option
      /// The value is a lower bound, not the provider's final count: a
      /// transcript records output at streaming start.
      LowerBound: bool }

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
          CodeChange: bool
          /// Paths the call wrote, when known (edit tools).
          Written: string option }

    let private toolFacts (cwd: string option) (tool: TranscriptToolUse) : ToolFacts =
        let none =
            { Reads = []
              Searches = 0
              Writes = []
              ShellWrite = false
              Build = false
              Test = false
              Shell = false
              CodeChange = false
              Written = None }

        let path = tool.FilePath |> Option.map (relative cwd)

        match tool.Name with
        | "Read" -> { none with Reads = path |> Option.toList }
        | name when searchTools.Contains name -> { none with Searches = 1 }
        | name when editTools.Contains name ->
            let written = path |> Option.defaultValue ""
            { none with Writes = [ written ]; CodeChange = isCodePath written; Written = Some written }
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

    /// Deduplicates provider requests by request key. A transcript repeats
    /// an assistant message once per content block while streaming, so each
    /// request is kept once, in first-seen order, carrying its last-written
    /// usage (the CONSTRUCT-008 `usage.py` script keeps the first; the two
    /// agree whenever repeated blocks carry identical usage, as recorded
    /// transcripts do). Entries without usage are not requests.
    let requestsOf (assistant: TranscriptEntry list) : TranscriptRequest list =
        let order, latest =
            assistant
            |> List.fold
                (fun (order: string list, latest: Map<string, TranscriptRequest>) entry ->
                    match entry.RequestKey, entry.Usage with
                    | Some key, Some usage ->
                        let request =
                            { Key = key
                              Model = entry.Model |> Option.filter ((<>) syntheticModel)
                              AgentId = entry.AgentId
                              Usage = usage }

                        (if latest.ContainsKey key then order else key :: order), latest.Add(key, request)
                    | _ -> order, latest)
                ([], Map.empty)

        order |> List.rev |> List.map (fun key -> latest[key])

    let tokenTotals (requests: TranscriptRequest list) : TokenTotals =
        let total selector = TokenTotal.ofRequests selector requests

        { Input = total (fun usage -> usage.Input)
          Output = total (fun usage -> usage.Output)
          CacheRead = total (fun usage -> usage.CacheRead)
          CacheWrite = total (fun usage -> usage.CacheCreation)
          CacheWrite5m = total (fun usage -> usage.CacheCreation5m)
          CacheWrite1h = total (fun usage -> usage.CacheCreation1h)
          Reasoning = total (fun usage -> usage.Reasoning) }

    /// Tool calls, each counted once: a `tool_use` block ID seen again
    /// (the same call repeated over streamed entries) is not a new call.
    let distinctToolUses (assistant: TranscriptEntry list) : (DateTimeOffset option * TranscriptToolUse) list =
        assistant
        |> List.collect (fun entry -> entry.ToolUses |> List.map (fun tool -> entry.Timestamp, tool))
        |> List.fold
            (fun (seen: Set<string>, kept) (at, tool) ->
                match tool.Id with
                | Some id when seen.Contains id -> seen, kept
                | Some id -> seen.Add id, (at, tool) :: kept
                | None -> seen, (at, tool) :: kept)
            (Set.empty, [])
        |> snd
        |> List.rev

    /// `isProductive` judges a written path against the repository's
    /// meaningful-path configuration; a shell write counts when it names a
    /// code directory, as for the first code change.
    let summarizeWith (isProductive: string -> bool) (entries: TranscriptEntry list) : SessionSummary =
        let firstSome selector = entries |> List.tryPick selector
        let cwd = firstSome (fun entry -> entry.Cwd)
        let stamps = entries |> List.choose (fun entry -> entry.Timestamp)
        let startedAt = if stamps.IsEmpty then None else Some(List.min stamps)
        let endedAt = if stamps.IsEmpty then None else Some(List.max stamps)
        let assistant = entries |> List.filter (fun entry -> entry.Kind = "assistant")

        let requests = requestsOf assistant
        let calls =
            distinctToolUses assistant
            |> List.map (fun (at, tool) -> at, tool, toolFacts cwd tool)

        let facts = calls |> List.map (fun (_, _, fact) -> fact)
        let reads = facts |> List.collect (fun fact -> fact.Reads) |> List.filter (String.IsNullOrEmpty >> not) |> counts
        let span = Option.map2 (fun (started: DateTimeOffset) ended -> int64 (ended - started).TotalMilliseconds) startedAt endedAt
        let active, idle = activeTime stamps

        let firstCodeChange =
            calls
            |> List.tryPick (fun (at, _, fact) -> if fact.CodeChange then at else None)

        let firstProductiveChange =
            calls
            |> List.tryPick (fun (at, _, fact) ->
                match fact.Written with
                | Some path when not (String.IsNullOrEmpty path) && isProductive path -> at
                | Some _ -> None
                | None -> if fact.CodeChange then at else None)

        let elapsedTo (moment: DateTimeOffset option) =
            Option.map2 (fun (started: DateTimeOffset) (changed: DateTimeOffset) -> int64 (changed - started).TotalMilliseconds) startedAt moment

        let apiErrors =
            entries |> List.filter (fun entry -> entry.Kind = "system" && entry.Subtype = Some "api_error")

        { SessionId = firstSome (fun entry -> entry.SessionId)
          RuntimeVersion = firstSome (fun entry -> entry.RuntimeVersion)
          Models = assistant |> List.choose (fun entry -> entry.Model) |> List.filter ((<>) syntheticModel) |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
          StartedAt = startedAt
          EndedAt = endedAt
          SpanMs = span
          ActiveMs = span |> Option.map (fun _ -> active)
          IdleGapsExcluded = idle
          MsToFirstCodeChange = elapsedTo firstCodeChange
          MsToFirstProductiveChange = elapsedTo firstProductiveChange
          PeakContextTokens =
            requests
            |> List.choose (fun request ->
                let usage = request.Usage
                [ usage.Input; usage.CacheRead; usage.CacheCreation ] |> List.choose id |> function
                    | [] -> None
                    | known -> Some(List.sum known))
            |> function
                | [] -> None
                | sizes -> Some(List.max sizes)
          ModelRequests = requests.Length
          Requests = requests
          Tokens = if requests.IsEmpty then None else Some(tokenTotals requests)
          UsageExtensions =
            requests
            |> List.collect (fun request -> request.Usage.Unmapped)
            |> List.groupBy fst
            |> List.map (fun (path, values) -> path, values |> List.sumBy snd)
            |> List.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right))
          UsageLabels =
            requests
            |> List.collect (fun request -> request.Usage.Labels)
            |> List.groupBy fst
            |> List.map (fun (name, values) -> name, values |> List.map snd |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)))
            |> List.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right))
          AgentIds = entries |> List.choose (fun entry -> entry.AgentId) |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
          Sidechain =
            let parsed = entries |> List.filter (fun entry -> entry.Kind <> TranscriptEntry.malformedKind)
            not parsed.IsEmpty && parsed |> List.forall (fun entry -> entry.IsSidechain)
          MalformedLines = entries |> List.filter (fun entry -> entry.Kind = TranscriptEntry.malformedKind) |> List.length
          ApiErrors = apiErrors.Length
          Retries = apiErrors |> List.filter (fun entry -> entry.RetryAttempt.IsSome) |> List.length
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

    /// The default judgement: code under `src/` or `tests/`.
    let summarize (entries: TranscriptEntry list) : SessionSummary = summarizeWith isCodePath entries

    /// Reads beyond the first of each file: the repeated context a later
    /// read re-acquires.
    let repeatedReadCount (summary: SessionSummary) =
        summary.RepeatedReads |> List.sumBy (fun (_, count) -> count - 1)

    /// The normalized measurements, in a stable order. Counts that the
    /// transcript records directly are `observed`; counts pattern-matched
    /// from shell commands, and time derived from entry timestamps, are
    /// `derived`; the runtime's own cost is an `estimated` cumulative
    /// session cost, never `cost.execution_total`. Every value the
    /// transcript does not carry is `None` with a reason, never zero.
    let measurements (summary: SessionSummary) : SessionMeasurement list =
        let measured metricId quality (value: decimal option) =
            { MetricId = metricId
              Value = value
              Quality = quality
              Currency = None
              UnavailableReason = None
              LowerBound = false }

        let count metricId quality (value: int) = measured metricId quality (Some(decimal value))

        let tokens metricId (selector: TokenTotals -> TokenTotal) =
            match summary.Tokens |> Option.map selector with
            | None -> { measured metricId "observed" None with UnavailableReason = Some "the transcript carries no provider request with usage" }
            | Some total ->
                match TokenTotal.complete total with
                | Some sum -> measured metricId "observed" (Some(decimal sum))
                | None -> { measured metricId "observed" None with UnavailableReason = Some(TokenTotal.unavailableReason total) }

        // A transcript records an API error only when one happens; with none
        // recorded, absence is not proof of zero.
        let onlyWhenRecorded metricId (value: int) =
            if value > 0 then
                count metricId "observed" value
            else
                { measured metricId "observed" None with
                    UnavailableReason = Some "the transcript records API errors and retries only when they occur; none were recorded, which does not prove zero" }

        let transcriptSeen = summary.StartedAt.IsSome

        [ count "model.requests" "observed" summary.ModelRequests
          tokens "tokens.input" _.Input
          { tokens "tokens.output" _.Output with LowerBound = true }
          tokens "tokens.cache_read" _.CacheRead
          tokens "tokens.cache_write" _.CacheWrite
          tokens "tokens.cache_write_5m" _.CacheWrite5m
          tokens "tokens.cache_write_1h" _.CacheWrite1h
          { tokens "tokens.reasoning" _.Reasoning with LowerBound = true }
          onlyWhenRecorded "model.request_failures" summary.ApiErrors
          onlyWhenRecorded "agent.retries" summary.Retries
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
          count "context.repeated_file_reads_distinct" "derived" summary.RepeatedReads.Length
          measured "context.peak_tokens" "observed" (summary.PeakContextTokens |> Option.map decimal)
          count "context.governance_reads" "derived" (summary.GovernanceReads |> List.sumBy snd)
          measured "time.first_code_change_ms" "derived" (summary.MsToFirstCodeChange |> Option.map decimal)
          measured "time.first_productive_change_ms" "derived" (summary.MsToFirstProductiveChange |> Option.map decimal)
          measured "time.active_ms" "derived" (summary.ActiveMs |> Option.map decimal)
          measured "time.agent_wall_ms" "derived" (summary.SpanMs |> Option.map decimal)
          { MetricId = "cost.session_cumulative"
            Value = summary.CostUsd
            Quality = "estimated"
            Currency = Some "USD"
            UnavailableReason = None
            LowerBound = false } ]
        |> List.map (fun measurement ->
            if transcriptSeen then measurement
            else { measurement with Value = None; UnavailableReason = Some "the transcript has no entries" })

    /// Metrics the transcript can never observe: host compute is measured by
    /// the host (`telemetry record`), never inferred from the agent's log;
    /// acceptance outcomes come from verification, not from the transcript.
    let unobservable =
        [ "host.compile_ms"
          "host.verify_ms"
          "host.prepare_apply_ms"
          "acceptance.first_pass"
          "acceptance.repaired"
          "acceptance.repair_cycles" ]

    /// The snapshot identity: a subagent (sidechain) transcript is its own
    /// snapshot, so a child's transcript never collides with its parent
    /// session's under one session ID.
    let snapshotId (summary: SessionSummary) : string option =
        summary.SessionId
        |> Option.map (fun session ->
            match summary.Sidechain, summary.AgentIds with
            | true, [ agent ] -> $"claude-session-{session}-agent-{agent}"
            | _ -> $"claude-session-{session}")
