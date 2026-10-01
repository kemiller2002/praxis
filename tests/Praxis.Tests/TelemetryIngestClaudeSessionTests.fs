namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Domain.Planning
open Praxis.Domain.Telemetry
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// PRAXIS-PLAN-05: the `anthropic-claude-session` adapter (session metrics
/// from a Claude Code transcript) and platform-reported
/// `cost.execution_total`, against the fixture transcript
/// `Fixtures/claude-session-transcript.jsonl`.
[<RequireQualifiedAccess>]
module TelemetryIngestClaudeSessionTests =
    let private fixtureText () =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-session-transcript.jsonl"))

    let private summary () =
        match FileTelemetryFinalizationRepository.parseIngestInput (fixtureText ()) with
        | Ok node -> ClaudeSessionTranscriptReader.entries node |> SessionTranscript.summarize
        | Error message -> failwith message

    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-telemetry-session-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    /// The repository's own registry, so the adapter is tested against the
    /// metric IDs it will really meet.
    let private copyMetricRegistry root =
        let rec repositoryRoot (directory: DirectoryInfo) =
            if File.Exists(Path.Combine(directory.FullName, "telemetry", "metrics.json")) then directory.FullName
            elif isNull directory.Parent then failwith "repository metric registry not found"
            else repositoryRoot directory.Parent

        let source = Path.Combine(repositoryRoot (DirectoryInfo AppContext.BaseDirectory), "telemetry", "metrics.json")
        Directory.CreateDirectory(Path.Combine(root, "telemetry")) |> ignore
        File.Copy(source, Path.Combine(root, "telemetry", "metrics.json"))

    let private executionFile root executionId =
        Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json")

    let private writeExecution root executionId workItemId =
        Directory.CreateDirectory(Path.Combine(root, ".ros", "telemetry", "executions")) |> ignore

        File.WriteAllText(
            executionFile root executionId,
            $"""{{"schemaVersion":"1.0.0","executionId":"{executionId}","workItemId":"{workItemId}","status":"active","startedAt":"2026-09-30T09:59:00.000Z",
                "identity":{{"provider":"unknown","runtime":"unknown"}},"provenance":{{"collector":"ros","collectorVersion":"1.0.0","discoveredAt":"2026-09-30T09:59:00.000Z","sources":[]}},
                "classification":{{"types":["development"],"rationale":null,"evidence":[],"rd":null}},
                "capabilities":[],"metrics":[],"rawTelemetry":[],"events":[],"repository":{{"start":{{"available":false}}}},"scope":{{}},"qualitySignals":[],"links":{{}}}}"""
        )

    let private readExecution root executionId : JsonObject =
        match JsonNode.Parse(File.ReadAllText(executionFile root executionId)) with
        | :? JsonObject as record -> record
        | _ -> failwith "expected a JSON object"

    let private objects (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonArray as array -> array |> Seq.choose (function :? JsonObject as item -> Some item | _ -> None) |> Seq.toList
        | _ -> []

    let private text (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = Text.Json.JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private metricValues (record: JsonObject) (id: string) =
        objects record "metrics"
        |> List.filter (fun metric -> text metric "id" = Some id)
        |> List.map (fun metric -> metric["value"].GetValue<float>())

    let private capabilityStatus (record: JsonObject) (id: string) =
        objects record "capabilities" |> List.tryFind (fun capability -> text capability "metricId" = Some id) |> Option.bind (fun capability -> text capability "status")

    let private ingest root =
        FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") SessionTranscript.adapterName (fixtureText ())

    let private t name run = { Name = $"claude session adapter: {name}"; Run = run }

    let tests =
        [ t "summarizes the fixture transcript like the A021 harness script" (fun () ->
              let summary = summary ()
              Assert.equal (Some "sess-fixture") summary.SessionId
              Assert.equal (Some "2.1.0") summary.RuntimeVersion
              Assert.equal [ "claude-test" ] summary.Models
              Assert.equal (Some 2_401_000L) summary.SpanMs
              Assert.equal 5 summary.ModelRequests
              Assert.equal (Some { Input = 1000L; Output = 100L; CacheRead = 3000L; CacheCreation = 50L }) summary.Tokens
              Assert.equal [ "Bash", 3; "Edit", 1; "Read", 2 ] summary.ToolCalls
              Assert.equal 1 summary.ToolErrors
              Assert.equal 3 summary.ShellCommands
              Assert.equal 5 summary.FileReads
              Assert.equal 3 summary.DistinctFilesRead
              Assert.equal [ "/tmp/FIXTURE-SCRATCH/build.log", 2; "AGENTS.md", 2 ] summary.RepeatedReads
              Assert.equal [ "AGENTS.md", 2; "docs/planning.md", 1 ] summary.GovernanceReads
              Assert.equal 1 summary.Searches
              Assert.equal 1 summary.FileWrites
              Assert.equal 1 summary.Builds
              Assert.equal 1 summary.TestRuns
              Assert.equal 1 summary.Compactions
              Assert.equal (Some 1.25m) summary.CostUsd)

          t "time to first code change is the first src/ or tests/ mutation" (fun () ->
              Assert.equal (Some 300_000L) (summary ()).MsToFirstCodeChange)

          t "active time leaves out idle gaps longer than 15 minutes" (fun () ->
              let summary = summary ()
              Assert.equal (Some 421_000L) summary.ActiveMs
              Assert.equal 1 summary.IdleGapsExcluded)

          t "an empty transcript reports every metric unavailable, never zero" (fun () ->
              let measurements = SessionTranscript.summarize [] |> SessionTranscript.measurements
              Assert.isTrue (not measurements.IsEmpty) "every recognized metric is declared"
              Assert.isTrue (measurements |> List.forall (fun measurement -> measurement.Value.IsNone)) "nothing is recorded as zero")

          t "only this adapter's input may exceed the raw-payload budget" (fun () ->
              Assert.equal SessionTranscript.maxInputBytes (SessionTranscript.inputLimit SessionTranscript.adapterName 262_144)
              Assert.equal 262_144 (SessionTranscript.inputLimit "anthropic-claude-statusline" 262_144))

          t "ingest records the session metrics under registered metric IDs" (fun () ->
              withTemporaryRoot (fun root ->
                  copyMetricRegistry root
                  writeExecution root "EXE-1" "WI-A"

                  match ingest root with
                  | Error message -> failwith message
                  | Ok _ ->
                      let record = readExecution root "EXE-1"
                      Assert.equal [ 5.0 ] (metricValues record "model.requests")
                      Assert.equal [ 1000.0 ] (metricValues record "tokens.input")
                      Assert.equal [ 3000.0 ] (metricValues record "tokens.cache_read")
                      Assert.equal [ 6.0 ] (metricValues record "tool.calls")
                      Assert.equal [ 5.0 ] (metricValues record "tool.file_reads")
                      Assert.equal [ 2.0 ] (metricValues record "context.repeated_file_reads")
                      Assert.equal [ 3.0 ] (metricValues record "context.governance_reads")
                      Assert.equal [ 300000.0 ] (metricValues record "time.first_code_change_ms")
                      Assert.equal [ 421000.0 ] (metricValues record "time.active_ms")
                      Assert.equal [ 1.0 ] (metricValues record "context.compactions")
                      Assert.equal (Some "derived") (capabilityStatus record "context.repeated_file_reads")
                      Assert.equal (Some "supported-observed") (capabilityStatus record "model.requests")

                      let identity = record["identity"].AsObject()
                      Assert.equal (Some "claude-code") (text identity "runtime")
                      Assert.equal (Some "claude-test") (text identity "model")
                      Assert.equal (Some "sess-fixture") (text identity "sessionId")))

          t "the runtime's cost is an estimated session cost, never cost.execution_total" (fun () ->
              withTemporaryRoot (fun root ->
                  copyMetricRegistry root
                  writeExecution root "EXE-1" "WI-A"
                  ingest root |> Result.mapError failwith |> ignore
                  let record = readExecution root "EXE-1"
                  let cost = objects record "metrics" |> List.find (fun metric -> text metric "id" = Some "cost.session_cumulative")
                  Assert.equal (Some "estimated") (text cost "quality")
                  Assert.equal (Some "USD") (text cost "currency")
                  Assert.empty (metricValues record "cost.execution_total")))

          t "no transcript content reaches the execution record" (fun () ->
              withTemporaryRoot (fun root ->
                  copyMetricRegistry root
                  writeExecution root "EXE-1" "WI-A"
                  ingest root |> Result.mapError failwith |> ignore
                  let stored = File.ReadAllText(executionFile root "EXE-1")

                  for needle in [ "FIXTURE-PROMPT-TEXT"; "FIXTURE-COMMAND-NEEDLE"; "FIXTURE-TOOL-OUTPUT"; "FIXTURE-ASSISTANT-TEXT"; "FIXTURE-SCRATCH"; "/work/repo" ] do
                      Assert.isTrue (not (stored.Contains needle)) $"record must not contain {needle}"

                  let raw = objects (readExecution root "EXE-1") "rawTelemetry" |> Assert.single
                  Assert.equal (Some "claude-session-sess-fixture") (text raw "snapshotId")

                  let repeated =
                      objects (raw["payload"].AsObject()) "repeatedReads"
                      |> List.map (fun entry -> text entry "path", entry["count"].GetValue<int>())

                  Assert.equal [ Some "(outside the repository)", 2; Some "AGENTS.md", 2 ] repeated))

          t "a session is ingested into an execution once, so totals are never summed twice" (fun () ->
              withTemporaryRoot (fun root ->
                  copyMetricRegistry root
                  writeExecution root "EXE-1" "WI-A"
                  ingest root |> Result.mapError failwith |> ignore
                  ingest root |> Result.mapError failwith |> ignore
                  Assert.equal [ 1000.0 ] (metricValues (readExecution root "EXE-1") "tokens.input")))

          t "platform-reported cost.execution_total and session metrics reach the planner" (fun () ->
              withTemporaryRoot (fun root ->
                  copyMetricRegistry root
                  writeExecution root "EXE-1" "WI-A"
                  ingest root |> Result.mapError failwith |> ignore

                  let request: FileTelemetryFinalizationRepository.RecordMetricRequest =
                      { MetricId = "cost.execution_total"
                        Value = 9.48
                        Unit = None
                        Currency = Some "USD"
                        Quality = "observed"
                        Confidence = FileTelemetryFinalizationRepository.NoConfidence
                        Scope = "execution"
                        Source = { Type = "platform"; Name = "claude-code-remote"; Mechanism = "session-record" }
                        PricingSource = None
                        PricingVersion = None
                        CollectedAt = None
                        Step = None }

                  FileTelemetryFinalizationRepository.recordMetric root (Some "EXE-1") request |> Result.mapError failwith |> ignore

                  let execution = FilePlanningRepository.readExecutions root |> Assert.single
                  Assert.equal (Some(9.48m, Some "USD")) (History.executionCost execution)
                  Assert.equal CostEvidenceKind.Observed (History.executionCostKind execution)
                  Assert.equal (Some 421_000L) execution.Session.ActiveMs
                  Assert.equal (Some 300_000L) execution.Session.FirstCodeChangeMs
                  Assert.equal (Some 3) execution.Session.GovernanceReads
                  Assert.equal (Some 2) execution.Session.RepeatedReads)) ]
