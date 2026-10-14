namespace Praxis.Infrastructure.Work

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Telemetry

/// PRAXIS-PLAN-05: reads a Claude Code session transcript (JSON Lines, one
/// entry per line, as `~/.claude/projects/*/SESSION.jsonl` stores it) into
/// the content-free `TranscriptEntry` facts `SessionTranscript.summarize`
/// needs. Message text, tool input and tool output are never copied: only a
/// tool's name, its `file_path` and (in memory, for classification) its
/// shell command are read.
[<RequireQualifiedAccess>]
module ClaudeSessionTranscriptReader =
    let private child (node: JsonObject) (name: string) : JsonObject option =
        match node[name] with
        | :? JsonObject as value -> Some value
        | _ -> None

    let private items (node: JsonObject) (name: string) : JsonObject list =
        match node[name] with
        | :? JsonArray as array -> array |> Seq.choose (function :? JsonObject as value -> Some value | _ -> None) |> Seq.toList
        | _ -> []

    let private text (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private number (node: JsonObject) (name: string) : decimal option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number ->
            match value.TryGetValue<decimal>() with
            | true, parsed -> Some parsed
            | _ -> None
        | _ -> None

    let private flag (node: JsonObject) (name: string) : bool =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.True -> true
        | _ -> false

    let private timestamp (node: JsonObject) : DateTimeOffset option =
        text node "timestamp"
        |> Option.bind (fun value ->
            match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
            | true, parsed -> Some parsed
            | _ -> None)

    let private integer (node: JsonObject) (name: string) : int64 option =
        number node name |> Option.map int64

    /// Usage fields this adapter normalizes; every other numeric leaf is an
    /// extension preserved by path, every top-level string a label.
    let private mappedUsagePaths =
        set
            [ "input_tokens"
              "output_tokens"
              "cache_read_input_tokens"
              "cache_creation_input_tokens"
              "cache_creation.ephemeral_5m_input_tokens"
              "cache_creation.ephemeral_1h_input_tokens"
              "output_tokens_details.thinking_tokens" ]

    let rec private numericLeaves (prefix: string) (node: JsonNode) : (string * int64) list =
        match node with
        | :? JsonObject as value ->
            value
            |> Seq.toList
            |> List.collect (fun entry ->
                let path = if prefix = "" then entry.Key else $"{prefix}.{entry.Key}"
                match entry.Value with
                | null -> []
                | child -> numericLeaves path child)
        | :? JsonArray as value -> value |> Seq.toList |> List.collect (function null -> [] | child -> numericLeaves $"{prefix}[]" child)
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number ->
            match value.TryGetValue<decimal>() with
            | true, parsed -> [ prefix, int64 parsed ]
            | _ -> []
        | _ -> []

    let private usage (message: JsonObject) : TranscriptUsage option =
        child message "usage"
        |> Option.map (fun value ->
            let cacheCreation = child value "cache_creation"
            let outputDetails = child value "output_tokens_details"

            { Input = integer value "input_tokens"
              Output = integer value "output_tokens"
              CacheRead = integer value "cache_read_input_tokens"
              CacheCreation = integer value "cache_creation_input_tokens"
              CacheCreation5m = cacheCreation |> Option.bind (fun node -> integer node "ephemeral_5m_input_tokens")
              CacheCreation1h = cacheCreation |> Option.bind (fun node -> integer node "ephemeral_1h_input_tokens")
              Reasoning = outputDetails |> Option.bind (fun node -> integer node "thinking_tokens")
              Unmapped = numericLeaves "" value |> List.filter (fun (path, _) -> not (mappedUsagePaths.Contains path))
              Labels =
                value
                |> Seq.toList
                |> List.choose (fun entry ->
                    match entry.Value with
                    | :? JsonValue as leaf when leaf.GetValueKind() = JsonValueKind.String -> Some(entry.Key, leaf.GetValue<string>())
                    | _ -> None) })

    let private toolUse (block: JsonObject) : TranscriptToolUse option =
        match text block "type", text block "name" with
        | Some "tool_use", Some name ->
            let input = child block "input"

            Some
                { Id = text block "id"
                  Name = name
                  FilePath = input |> Option.bind (fun value -> text value "file_path")
                  Command = input |> Option.bind (fun value -> text value "command") }
        | _ -> None

    let private isToolError (block: JsonObject) =
        text block "type" = Some "tool_result" && flag block "is_error"

    let entry (node: JsonObject) : TranscriptEntry =
        let kind = text node "type" |> Option.defaultValue ""
        let message = child node "message"
        let blocks = message |> Option.map (fun value -> items value "content") |> Option.defaultValue []
        let assistant = kind = "assistant"

        { Kind = kind
          Subtype = text node "subtype"
          Timestamp = timestamp node
          Cwd = text node "cwd"
          SessionId = text node "sessionId"
          RuntimeVersion = text node "version"
          RequestKey =
            [ message |> Option.bind (fun value -> text value "id"); text node "requestId"; text node "uuid" ]
            |> List.tryPick id
          AgentId = text node "agentId"
          IsSidechain = flag node "isSidechain"
          Model = if assistant then message |> Option.bind (fun value -> text value "model") else None
          Usage = if assistant then message |> Option.bind usage else None
          ToolUses = if assistant then blocks |> List.choose toolUse else []
          ToolErrors = if kind = "user" then blocks |> List.filter isToolError |> List.length else 0
          IsCompactSummary = flag node "isCompactSummary"
          CostUsd = if kind = "cost-state" then number node "totalCostUSD" else None
          RetryAttempt = if kind = "system" then number node "retryAttempt" |> Option.map int else None }

    /// The entries of a parsed ingest input: a JSON Lines array, or one
    /// entry object. A non-object item is a malformed entry, counted.
    let entries (input: JsonNode) : TranscriptEntry list =
        match input with
        | :? JsonArray as array ->
            array |> Seq.map (function :? JsonObject as value -> entry value | _ -> TranscriptEntry.malformed) |> Seq.toList
        | :? JsonObject as single -> [ entry single ]
        | _ -> []

    /// Reads transcript text line by line. A line that is not a JSON object
    /// (a truncated final write, a corrupted line) becomes a malformed entry:
    /// counted and reported, never fatal, never parsed into usage.
    let parseLines (text: string) : TranscriptEntry list =
        text.Split([| "\r\n"; "\n" |], StringSplitOptions.None)
        |> Array.filter (fun line -> line.Trim() <> "")
        |> Array.map (fun line ->
            try
                match JsonNode.Parse line with
                | :? JsonObject as value -> entry value
                | _ -> TranscriptEntry.malformed
            with :? JsonException ->
                TranscriptEntry.malformed)
        |> Array.toList
