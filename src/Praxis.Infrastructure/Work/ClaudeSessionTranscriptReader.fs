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

    let private usage (message: JsonObject) : TranscriptUsage option =
        child message "usage"
        |> Option.map (fun value ->
            let tokens name = number value name |> Option.map int64 |> Option.defaultValue 0L

            { Input = tokens "input_tokens"
              Output = tokens "output_tokens"
              CacheRead = tokens "cache_read_input_tokens"
              CacheCreation = tokens "cache_creation_input_tokens" })

    let private toolUse (block: JsonObject) : TranscriptToolUse option =
        match text block "type", text block "name" with
        | Some "tool_use", Some name ->
            let input = child block "input"

            Some
                { Name = name
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
          Model = if assistant then message |> Option.bind (fun value -> text value "model") else None
          Usage = if assistant then message |> Option.bind usage else None
          ToolUses = if assistant then blocks |> List.choose toolUse else []
          ToolErrors = if kind = "user" then blocks |> List.filter isToolError |> List.length else 0
          IsCompactSummary = flag node "isCompactSummary"
          CostUsd = if kind = "cost-state" then number node "totalCostUSD" else None }

    /// The entries of a parsed ingest input: a JSON Lines array, or one
    /// entry object. Anything else carries no entries.
    let entries (input: JsonNode) : TranscriptEntry list =
        match input with
        | :? JsonArray as array -> array |> Seq.choose (function :? JsonObject as value -> Some(entry value) | _ -> None) |> Seq.toList
        | :? JsonObject as single -> [ entry single ]
        | _ -> []
