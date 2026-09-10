namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Domain.Telemetry
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module TelemetryQueryTests =
    let private stringField (record: JsonObject) (name: string) : string =
        match record[name] with
        | :? JsonValue as value -> value.GetValue<string>()
        | _ -> failwith $"expected '{name}' to be a string field"

    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-telemetry-query-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    let private writeExecution root executionId workItemId startedAt =
        let directory = Path.Combine(root, ".ros", "telemetry", "executions")
        Directory.CreateDirectory directory |> ignore

        let json =
            $"{{\"schemaVersion\":\"1.0.0\",\"executionId\":\"{executionId}\",\"workItemId\":\"{workItemId}\",\"status\":\"active\",\"startedAt\":\"{startedAt}\"}}"

        File.WriteAllText(Path.Combine(directory, $"{executionId}.json"), json)

    let tests =
        [ { Name = "readAll returns an empty list when the executions directory does not exist yet"
            Run = fun () -> withTemporaryRoot (fun root -> Assert.empty (FileTelemetryQueryRepository.readAll root)) }

          { Name = "readAll returns every record in ascending filename order"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeExecution root "EXE-20260101T000000000Z-bbbbbbbb" "WI-0001" "2026-01-01T00:00:00.000Z"
                    writeExecution root "EXE-20260101T000000000Z-aaaaaaaa" "WI-0002" "2026-01-01T00:00:00.000Z"
                    let records = FileTelemetryQueryRepository.readAll root
                    Assert.equal 2 records.Length
                    Assert.equal "EXE-20260101T000000000Z-aaaaaaaa" (stringField records[0] "executionId")
                    Assert.equal "EXE-20260101T000000000Z-bbbbbbbb" (stringField records[1] "executionId")) }

          { Name = "readByWorkItemId filters to matching records only, never rejecting an id matching nothing"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeExecution root "EXE-1" "WI-MATCH" "2026-01-01T00:00:00.000Z"
                    writeExecution root "EXE-2" "WI-OTHER" "2026-01-01T00:00:00.000Z"
                    let matching = FileTelemetryQueryRepository.readByWorkItemId root "WI-MATCH"
                    Assert.equal 1 matching.Length
                    Assert.equal "EXE-1" (stringField matching[0] "executionId")
                    Assert.empty (FileTelemetryQueryRepository.readByWorkItemId root "WI-GHOST")) }

          { Name = "readByExecutionId rejects an unknown id with production's exact message"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    match FileTelemetryQueryRepository.readByExecutionId root "EXE-GHOST" with
                    | Error message -> Assert.equal "telemetry execution 'EXE-GHOST' was not found" message
                    | Ok _ -> failwith "expected a rejection for an unknown execution id") }

          { Name = "readByExecutionId resolves a known id"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeExecution root "EXE-KNOWN" "WI-0001" "2026-01-01T00:00:00.000Z"
                    match FileTelemetryQueryRepository.readByExecutionId root "EXE-KNOWN" with
                    | Error message -> failwith message
                    | Ok record -> Assert.equal "WI-0001" (stringField record "workItemId")) }

          { Name = "telemetry adapters is production's exact static catalog, in production's exact order"
            Run = fun () ->
                Assert.equal
                    [ "generic"; "openai-codex"; "anthropic-claude-statusline"; "anthropic-claude-hook"
                      "anthropic-claude-otel"; "google-gemini-hook"; "google-gemini-otel"; "github-copilot-hook"
                      "github-copilot-otel"; "otel-json" ]
                    TelemetryAdapters.all } ]
