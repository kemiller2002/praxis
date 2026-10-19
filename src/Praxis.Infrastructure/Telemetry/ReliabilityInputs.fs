namespace Praxis.Infrastructure.Telemetry

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Telemetry

/// Reads the inputs of `telemetry reliability-report` (PRX-REL-009): the
/// work event log, execution records, and provider transcripts. Read-only.
///
/// Provider transcripts are read where the runtimes write them, which the
/// lifecycle never ingested on its own (the root cause of missing tokens):
/// - Claude Code: `~/.claude/projects/<cwd-slug>/<SESSION>.jsonl` and
///   subagents in `<SESSION>/subagents/agent-*.jsonl` (assistant lines carry
///   `message.id`, `requestId`, `message.model`, `message.usage`);
/// - Codex: `~/.codex/sessions/YYYY/MM/DD/rollout-*-<SESSION>.jsonl`
///   (`session_meta`, `turn_context.model`, cumulative `token_count`).
/// Only content-free usage facts are read; text, tool input and output are
/// never copied.
[<RequireQualifiedAccess>]
module ReliabilityInputs =
    let private child (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonObject as value -> Some value
        | _ -> None

    let private text (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String ->
            Some(value.GetValue<string>()) |> Option.filter (String.IsNullOrWhiteSpace >> not)
        | _ -> None

    let private integer (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number ->
            match value.TryGetValue<decimal>() with
            | true, parsed when parsed >= 0m -> Some(int64 parsed)
            | _ -> None
        | _ -> None

    let private decimalOf (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number ->
            match value.TryGetValue<decimal>() with
            | true, parsed -> Some parsed
            | _ -> None
        | _ -> None

    let private strings (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonArray as values ->
            values |> Seq.choose (function :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>()) | _ -> None) |> Seq.toList
        | _ -> []

    let private items (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonArray as values -> values |> Seq.choose (function :? JsonObject as value -> Some value | _ -> None) |> Seq.toList
        | _ -> []

    let private timestamp (value: string option) =
        value
        |> Option.bind (fun raw ->
            match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
            | true, parsed -> Some parsed
            | _ -> None)

    /// Non-empty lines parsed as JSON objects; `None` for a malformed line.
    let private jsonLines (content: string) : JsonObject option list =
        content.Split([| "\r\n"; "\n" |], StringSplitOptions.None)
        |> Array.filter (fun line -> line.Trim() <> "")
        |> Array.map (fun line ->
            try
                match JsonNode.Parse line with
                | :? JsonObject as value -> Some value
                | _ -> None
            with :? JsonException ->
                None)
        |> Array.toList

    let private values (pairs: (string * int64 option) list) =
        pairs |> List.choose (fun (metric, value) -> value |> Option.map (fun known -> metric, known)) |> Map.ofList

    // ---- events --------------------------------------------------------------

    let parseEvents (content: string) : ReliabilityEvent list * int * int =
        let lines = jsonLines content

        let events =
            lines
            |> List.mapi (fun order line -> order, line)
            |> List.choose (fun (order, line) ->
                line
                |> Option.bind (fun node ->
                    match text node "type", text node "workItem" with
                    | Some kind, Some workItem ->
                        Some
                            { EventId = text node "eventId" |> Option.defaultValue $"line-{order + 1}"
                              Type = kind
                              WorkItem = workItem
                              OccurredAt = timestamp (text node "occurredAt")
                              Order = order
                              Execution = text node "execution"
                              ActorId = child node "actor" |> Option.bind (fun actor -> text actor "id")
                              Claimant = text node "claimant"
                              Reason = text node "reason"
                              Codes = strings node "codes"
                              RequirementIds = strings node "requirementIds"
                              TelemetryExecutions = strings node "telemetryExecutions"
                              Commit = text node "commit" }
                    | _ -> None))

        events, lines.Length, lines |> List.filter Option.isNone |> List.length

    // ---- execution records -----------------------------------------------------

    let private metricSum (record: JsonObject) (metricId: string) =
        match items record "metrics" |> List.filter (fun metric -> text metric "id" = Some metricId) |> List.choose (fun metric -> integer metric "value") with
        | [] -> None
        | known -> Some(List.sum known)

    let private ledgerObservations (executionId: string) (record: JsonObject) =
        items record "rawTelemetry"
        |> List.collect (fun snapshot ->
            child snapshot "payload"
            |> Option.bind (fun payload -> child payload "requestLedger")
            |> Option.map (fun ledger ->
                items ledger "rows"
                |> List.choose (fun row ->
                    text row "key"
                    |> Option.map (fun key ->
                        let usage =
                            match row["usage"] with
                            | :? JsonArray as cells ->
                                cells
                                |> Seq.map (function
                                    | :? JsonValue as cell when cell.GetValueKind() = JsonValueKind.Number -> Some(cell.GetValue<int64>())
                                    | _ -> None)
                                |> Seq.toList
                            | _ -> []

                        { ObservationId = $"anthropic:{key}"
                          Provider = "anthropic"
                          Runtime = "claude-code"
                          SessionId = None
                          AgentId = text row "agentId"
                          Model = text row "model"
                          MessageId = Some key
                          RequestId = None
                          OccurredAt = None
                          Values = RequestLedger.values usage
                          NotApplicable = Set.empty
                          Origin = "execution-ledger"
                          ExplicitExecution = Some executionId })))
            |> Option.defaultValue [])

    let parseExecution (content: string) : ReliabilityExecution option =
        try
            match JsonNode.Parse content with
            | :? JsonObject as record ->
                text record "executionId"
                |> Option.map (fun executionId ->
                    let identity = child record "identity"
                    let identityText name = identity |> Option.bind (fun node -> text node name)

                    let tokenValues =
                        values ((ProviderUsage.buckets @ ProviderUsage.detailBuckets) |> List.map (fun metric -> metric, metricSum record metric))

                    let billed =
                        items record "metrics"
                        |> List.filter (fun metric -> text metric "id" = Some "cost.execution_total" && text metric "quality" = Some "observed")
                        |> List.choose (fun metric -> Option.map2 (fun amount currency -> amount, currency) (decimalOf metric "value") (text metric "currency"))

                    { ExecutionId = executionId
                      WorkItemId = text record "workItemId"
                      StartedAt = timestamp (text record "startedAt")
                      FinalizedAt = timestamp (text record "finalizedAt")
                      Provider = identityText "provider"
                      SessionId = identityText "sessionId"
                      AgentId = identityText "subagentId" |> Option.orElse (identityText "agentId")
                      WallMs = metricSum record "time.wall_ms"
                      ActiveMs = metricSum record "time.active_ms"
                      BlockedMs = metricSum record "time.blocked_ms"
                      AgentRetries = metricSum record "agent.retries"
                      BilledCost =
                        match billed |> List.map snd |> List.distinct with
                        | [ currency ] -> Some(billed |> List.sumBy fst, currency)
                        | _ -> None
                      Ledger = ledgerObservations executionId record
                      RecordUsage =
                        if tokenValues.IsEmpty then
                            None
                        else
                            Some
                                { ObservationId = $"record:{executionId}"
                                  Provider = identityText "provider" |> Option.defaultValue "unknown"
                                  Runtime = identityText "runtime" |> Option.defaultValue "unknown"
                                  SessionId = identityText "sessionId"
                                  AgentId = None
                                  Model = identityText "model"
                                  MessageId = None
                                  RequestId = None
                                  OccurredAt = None
                                  Values = tokenValues
                                  NotApplicable = Set.empty
                                  Origin = "execution-record"
                                  ExplicitExecution = Some executionId } })
            | _ -> None
        with :? JsonException ->
            None

    // ---- transcripts -------------------------------------------------------------

    let private syntheticModel = "<synthetic>"

    let private claudeObservation (node: JsonObject) : Result<UsageObservation, unit> option =
        match text node "type", child node "message" with
        | Some "assistant", Some message ->
            child message "usage"
            |> Option.map (fun usage ->
                let messageId = text message "id"
                let requestId = text node "requestId"

                match messageId |> Option.orElse requestId with
                | None -> Error()
                | Some key ->
                    let cacheCreation = child usage "cache_creation"
                    let details = child usage "output_tokens_details"

                    Ok
                        { ObservationId = $"anthropic:{key}"
                          Provider = "anthropic"
                          Runtime = "claude-code"
                          SessionId = text node "sessionId"
                          AgentId = text node "agentId"
                          Model = text message "model" |> Option.filter ((<>) syntheticModel)
                          MessageId = messageId
                          RequestId = requestId
                          OccurredAt = timestamp (text node "timestamp")
                          Values =
                            values
                                [ "tokens.input", integer usage "input_tokens"
                                  "tokens.output", integer usage "output_tokens"
                                  "tokens.cache_read", integer usage "cache_read_input_tokens"
                                  "tokens.cache_write", integer usage "cache_creation_input_tokens"
                                  "tokens.cache_write_5m", cacheCreation |> Option.bind (fun node -> integer node "ephemeral_5m_input_tokens")
                                  "tokens.cache_write_1h", cacheCreation |> Option.bind (fun node -> integer node "ephemeral_1h_input_tokens")
                                  "tokens.reasoning", details |> Option.bind (fun node -> integer node "thinking_tokens") ]
                          NotApplicable = Set.empty
                          Origin = "claude-transcript"
                          ExplicitExecution = None })
        | _ -> None

    let private digest (content: string) =
        let bytes = SHA256.HashData(Text.Encoding.UTF8.GetBytes content)
        Convert.ToHexString(bytes).ToLowerInvariant()

    let private isCodex (lines: JsonObject list) =
        lines |> List.exists (fun line -> text line "type" = Some "session_meta")

    let private codexObservations (lines: JsonObject list) =
        let sessionId =
            lines |> List.tryPick (fun line -> if text line "type" = Some "session_meta" then child line "payload" |> Option.bind (fun payload -> text payload "id") else None)

        let readings =
            lines
            |> List.fold
                (fun (model: string option, readings: ProviderUsage.CodexCounter list) line ->
                    let payload = child line "payload"

                    match text line "type", payload with
                    | Some "turn_context", Some context -> (text context "model" |> Option.orElse model), readings
                    | Some "event_msg", Some event when text event "type" = Some "token_count" ->
                        match child event "info" |> Option.bind (fun info -> child info "total_token_usage") with
                        | Some total ->
                            let reading: ProviderUsage.CodexCounter =
                                { At = timestamp (text line "timestamp")
                                  Model = model
                                  Input = integer total "input_tokens"
                                  CachedInput = integer total "cached_input_tokens"
                                  Output = integer total "output_tokens"
                                  Reasoning = integer total "reasoning_output_tokens"
                                  Total = integer total "total_tokens" }

                            model, reading :: readings
                        | None -> model, readings
                    | _ -> model, readings)
                (None, [])
            |> snd
            |> List.rev

        match sessionId with
        | Some session ->
            let observations, resets = ProviderUsage.codexIncrements session readings
            sessionId, observations, readings.Length, resets
        | None -> None, [], readings.Length, 0

    /// One transcript file's observations (not yet deduplicated) and its
    /// content-free source description.
    let parseTranscript (content: string) : UsageObservation list * TranscriptSource =
        let lines = jsonLines content
        let parsed = lines |> List.choose id
        let malformed = lines.Length - parsed.Length

        if isCodex parsed then
            let sessionId, observations, entries, resets = codexObservations parsed

            observations,
            { Kind = "codex-rollout"
              Digest = digest content
              SessionId = sessionId
              AgentId = None
              UsageEntries = entries
              MalformedLines = malformed
              UnidentifiedUsageEntries = (if sessionId.IsNone then entries else 0)
              CounterResets = resets }
        else
            let results = parsed |> List.choose claudeObservation
            let observations = results |> List.choose (function Ok value -> Some value | Error _ -> None)

            observations,
            { Kind = "claude-transcript"
              Digest = digest content
              SessionId = parsed |> List.tryPick (fun line -> text line "sessionId")
              AgentId = parsed |> List.tryPick (fun line -> text line "agentId")
              UsageEntries = results.Length
              MalformedLines = malformed
              UnidentifiedUsageEntries = results |> List.filter Result.isError |> List.length
              CounterResets = 0 }

    // ---- file system ---------------------------------------------------------------

    let private ordinal (values: string seq) = values |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> Seq.toList

    /// `.jsonl` files under each path (a file is itself), sorted ordinally.
    let transcriptFiles (paths: string list) : string list =
        paths
        |> List.collect (fun path ->
            if File.Exists path then [ Path.GetFullPath path ]
            elif Directory.Exists path then Directory.EnumerateFiles(path, "*.jsonl", SearchOption.AllDirectories) |> Seq.map Path.GetFullPath |> Seq.toList
            else [])
        |> List.distinct
        |> ordinal

    /// Where the runtimes write transcripts on this machine, when present:
    /// `$CLAUDE_CONFIG_DIR/projects` (else `~/.claude/projects`) and
    /// `$CODEX_HOME/sessions` (else `~/.codex/sessions`).
    let defaultTranscriptRoots () : string list =
        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        let orHome variable fallback = Environment.GetEnvironmentVariable variable |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.defaultValue (Path.Combine(home, fallback))

        [ Path.Combine(orHome "CLAUDE_CONFIG_DIR" ".claude", "projects"); Path.Combine(orHome "CODEX_HOME" ".codex", "sessions") ]
        |> List.filter Directory.Exists

    /// Transcript files that belong to one of the sessions: the main
    /// transcript `<SESSION>.jsonl`, its `<SESSION>/subagents/*.jsonl`, or a
    /// Codex rollout whose name ends with `-<SESSION>.jsonl`.
    let forSessions (sessions: string list) (files: string list) : string list =
        files
        |> List.filter (fun file ->
            let name = Path.GetFileName file
            let normalized = file.Replace('\\', '/')

            sessions
            |> List.exists (fun session ->
                name = $"{session}.jsonl"
                || name.EndsWith($"-{session}.jsonl", StringComparison.Ordinal)
                || normalized.Contains($"/{session}/subagents/", StringComparison.Ordinal)))

    let readTranscripts (files: string list) : UsageObservation list * TranscriptSource list =
        let parsed = files |> List.map (File.ReadAllText >> parseTranscript)
        parsed |> List.collect fst, parsed |> List.map snd

    let readExecutions (directory: string) : ReliabilityExecution list =
        if Directory.Exists directory then
            Directory.EnumerateFiles(directory, "*.json") |> ordinal |> List.choose (File.ReadAllText >> parseExecution)
        else
            []

    let readEvents (path: string) : ReliabilityEvent list * int * int =
        if File.Exists path then parseEvents (File.ReadAllText path) else [], 0, 0

    let readPriceTable (path: string) : Result<PriceTable, string> =
        if File.Exists path then Praxis.Infrastructure.Work.PriceTableReader.parse (File.ReadAllText path)
        else Error $"price table not found: {path}"

    /// Writes the report through a same-directory temporary file and an
    /// atomic replace, so a reader never sees a partial report.
    let writeReport (path: string) (content: string) =
        let full = Path.GetFullPath path
        let directory = Path.GetDirectoryName full
        Directory.CreateDirectory directory |> ignore
        let temporary = Path.Combine(directory, $".{Path.GetFileName full}.{Environment.ProcessId}.tmp")
        File.WriteAllText(temporary, content, Text.UTF8Encoding(false))
        File.Move(temporary, full, true)
