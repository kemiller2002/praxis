namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Domain.Telemetry
open Praxis.Infrastructure.Work

/// CONSTRUCT-010 Phase 6: cumulative usage and cost attribution from Claude
/// Code transcripts. Each accounting rule has its own test: one count per
/// provider request, cache buckets kept apart, no parent/child double count,
/// unknown never zero, cost provenance, malformed lines, and a golden test
/// against a real CONSTRUCT-008 run (A1), whose totals must equal the
/// research harness's `usage.py` for the same transcript.
[<RequireQualifiedAccess>]
module TelemetryUsageAttributionTests =
    let private t name run = { Name = $"usage attribution: {name}"; Run = run }

    let private fixture name =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name))

    // ---- transcript lines, built as JSON text so the reader is exercised ----

    let private assistant (uuid: string) (messageId: string) (usage: string) (tools: string list) =
        let content = tools |> List.map (fun id -> $"""{{"type":"tool_use","id":"{id}","name":"Bash","input":{{"command":"true"}}}}""") |> String.concat ","

        $"""{{"type":"assistant","uuid":"{uuid}","requestId":"req-{messageId}","sessionId":"s-1","version":"2.1.0","timestamp":"2026-10-13T10:00:0{uuid.Length % 10}.000Z","message":{{"id":"{messageId}","model":"model-x","usage":{usage},"content":[{content}]}}}}"""

    let private summarize (lines: string list) =
        lines |> String.concat "\n" |> ClaudeSessionTranscriptReader.parseLines |> SessionTranscript.summarize

    let private value (metricId: string) (summary: SessionSummary) =
        SessionTranscript.measurements summary |> List.find (fun measurement -> measurement.MetricId = metricId)

    let private fullUsage = """{"input_tokens":2,"output_tokens":6,"cache_read_input_tokens":100,"cache_creation_input_tokens":30,"cache_creation":{"ephemeral_5m_input_tokens":20,"ephemeral_1h_input_tokens":10},"output_tokens_details":{"thinking_tokens":4},"server_tool_use":{"web_search_requests":1},"service_tier":"standard"}"""

    // ---- usage-report measurements ----

    let private measurement execution lineage metric (value: float) quality basis origin : UsageMeasurement =
        { ExecutionId = execution
          WorkItemId = "WI-1"
          Provider = "anthropic"
          Model = Some "model-x"
          Step = None
          CollectedAt = "2026-10-13T10:00:00.000Z"
          MetricId = metric
          Value = value
          Unit = (if metric.StartsWith("cost.", StringComparison.Ordinal) then "currency" else "tokens")
          Currency = (if metric.StartsWith("cost.", StringComparison.Ordinal) then Some "USD" else None)
          Quality = quality
          Lineage = lineage
          CostBasis = basis
          Origin = origin
          LowerBound = false }

    let private request key (input: int64) : LedgerRequest =
        { Key = key
          Model = Some "model-x"
          AgentId = None
          Values = Map.ofList [ "tokens.input", input; "tokens.output", 1L ] }

    let private ledger execution keys : ExecutionLedger =
        { ExecutionId = execution
          SnapshotId = $"snap-{execution}"
          Step = None
          Requests = keys }

    let private table : PriceTable =
        { Reference =
            { Source = "fixture-prices"
              Version = "v1"
              EffectiveDate = Some "2026-10-01" }
          Currency = "USD"
          Models =
            Map.ofList
                [ "model-x",
                  { Input = Some 2m
                    Output = Some 10m
                    CacheRead = Some 0.2m
                    CacheWrite = None
                    CacheWrite5m = Some 2.5m
                    CacheWrite1h = Some 4m } ] }

    // ---- on-disk executions ----

    let private withRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"praxis-usage-attribution-{Guid.NewGuid():N}")
        Directory.CreateDirectory(Path.Combine(root, ".ros", "telemetry", "executions")) |> ignore

        let rec repositoryRoot (directory: DirectoryInfo) =
            if File.Exists(Path.Combine(directory.FullName, "telemetry", "metrics.json")) then directory.FullName
            elif isNull directory.Parent then failwith "repository metric registry not found"
            else repositoryRoot directory.Parent

        Directory.CreateDirectory(Path.Combine(root, "telemetry")) |> ignore
        File.Copy(Path.Combine(repositoryRoot (DirectoryInfo AppContext.BaseDirectory), "telemetry", "metrics.json"), Path.Combine(root, "telemetry", "metrics.json"))

        try
            run root
        finally
            Directory.Delete(root, true)

    let private writeExecution root (executionId: string) (parent: string option) =
        let parentJson = parent |> Option.map (fun id -> $"\"{id}\"") |> Option.defaultValue "null"

        File.WriteAllText(
            Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json"),
            $"""{{"schemaVersion":"1.0.0","executionId":"{executionId}","workItemId":"WI-A","status":"active","startedAt":"2026-10-13T09:59:00.000Z",
                "identity":{{"provider":"anthropic","runtime":"claude-code","parentExecutionId":{parentJson}}},"provenance":{{"collector":"ros","collectorVersion":"1.0.0","discoveredAt":"2026-10-13T09:59:00.000Z","sources":[]}},
                "classification":{{"types":["development"],"rationale":null,"evidence":[],"rd":null}},
                "capabilities":[],"metrics":[],"rawTelemetry":[],"events":[],"repository":{{"start":{{"available":false}}}},"scope":{{}},"qualitySignals":[],"links":{{}}}}"""
        )

    let private readExecution root (executionId: string) =
        JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json"))).AsObject()

    let private total (groups: UsageGroup list) key metric =
        groups
        |> List.filter (fun group -> group.Key = key && group.MetricId = metric && group.CostBasis.IsNone && group.Origin.IsNone)
        |> List.exactlyOne
        |> _.Total

    let private ok result =
        match result with
        | Ok value -> value
        | Error message -> failwith message

    let tests =
        [ // ---- deduplication ----
          t "a request repeated once per streamed content block is counted once" (fun () ->
              let summary =
                  summarize
                      [ assistant "a1" "msg-1" fullUsage [ "tool-1" ]
                        assistant "a2" "msg-1" fullUsage [ "tool-2" ]
                        assistant "a3" "msg-1" fullUsage []
                        assistant "a4" "msg-2" fullUsage [] ]

              Assert.equal 2 summary.ModelRequests
              Assert.equal (Some 4m) (value "tokens.input" summary).Value
              Assert.equal (Some 200m) (value "tokens.cache_read" summary).Value
              Assert.equal [ "msg-1"; "msg-2" ] (summary.Requests |> List.map _.Key))

          t "a tool call repeated across streamed entries is counted once by its tool_use ID" (fun () ->
              let summary =
                  summarize
                      [ assistant "a1" "msg-1" fullUsage [ "tool-1" ]
                        assistant "a2" "msg-1" fullUsage [ "tool-1"; "tool-2" ] ]

              Assert.equal [ "Bash", 2 ] summary.ToolCalls)

          // ---- cache buckets ----
          t "cache read and cache write are their own buckets, never added into input" (fun () ->
              let summary = summarize [ assistant "a1" "msg-1" fullUsage []; assistant "a2" "msg-2" fullUsage [] ]
              Assert.equal (Some 4m) (value "tokens.input" summary).Value
              Assert.equal (Some 200m) (value "tokens.cache_read" summary).Value
              Assert.equal (Some 60m) (value "tokens.cache_write" summary).Value)

          t "cache write is split by TTL where reported, and the split partitions the total" (fun () ->
              let summary = summarize [ assistant "a1" "msg-1" fullUsage []; assistant "a2" "msg-2" fullUsage [] ]
              let five = (value "tokens.cache_write_5m" summary).Value
              let hour = (value "tokens.cache_write_1h" summary).Value
              Assert.equal (Some 40m, Some 20m) (five, hour)
              Assert.equal (value "tokens.cache_write" summary).Value (Option.map2 (+) five hour))

          t "reasoning tokens come from output_tokens_details when exposed and stay a subset of output" (fun () ->
              let summary = summarize [ assistant "a1" "msg-1" fullUsage [] ]
              Assert.equal (Some 4m) (value "tokens.reasoning" summary).Value
              Assert.equal (Some 6m) (value "tokens.output" summary).Value)

          // ---- unknown is not zero ----
          t "a bucket the provider did not report is unknown with a reason, never zero" (fun () ->
              let summary =
                  summarize
                      [ assistant "a1" "msg-1" fullUsage []
                        assistant "a2" "msg-2" """{"input_tokens":1,"output_tokens":1,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}""" [] ]

              let split = value "tokens.cache_write_5m" summary
              Assert.equal None split.Value
              Assert.equal (Some "the provider reported this usage bucket on 1 of 2 requests") split.UnavailableReason
              // A reported zero is a real zero.
              Assert.equal (Some 100m) (value "tokens.cache_read" summary).Value
              Assert.equal (Some 30m) (value "tokens.cache_write" summary).Value)

          t "retries and request failures are unknown when the transcript records none, observed when it does" (fun () ->
              let none = summarize [ assistant "a1" "msg-1" fullUsage [] ]
              Assert.equal None (value "agent.retries" none).Value
              Assert.equal None (value "model.request_failures" none).Value

              let retried =
                  summarize
                      [ assistant "a1" "msg-1" fullUsage []
                        """{"type":"system","subtype":"api_error","timestamp":"2026-10-13T10:00:09.000Z","sessionId":"s-1","retryAttempt":1}""" ]

              Assert.equal (Some 1m) (value "agent.retries" retried).Value
              Assert.equal (Some 1m) (value "model.request_failures" retried).Value)

          t "an empty transcript reports every usage bucket unknown, never zero" (fun () ->
              let measurements = SessionTranscript.summarize [] |> SessionTranscript.measurements

              for metricId in RequestLedger.metricIds do
                  let found = measurements |> List.find (fun measurement -> measurement.MetricId = metricId)
                  Assert.equal None found.Value)

          t "a usage report shows a required metric nobody measured as unknown, not zero" (fun () ->
              let required =
                  [ { MetricId = "tokens.reasoning"
                      Unit = "tokens"
                      Currency = None
                      CostBasis = None } ]

              let reasons = Map.ofList [ ("EXE-a", "tokens.reasoning"), ("supported-unavailable", "not exposed") ]
              let groups = Usage.aggregateWith UsageDimension.Execution (Map.ofList [ "EXE-a", [ "EXE-a" ] ]) required reasons []
              let group = Assert.single groups
              Assert.equal None group.Total
              Assert.equal [ "EXE-a" ] group.UnavailableExecutions
              Assert.equal [ "EXE-a", "supported-unavailable", "not exposed" ] group.UnavailableReasons)

          // ---- malformed lines ----
          t "malformed transcript lines are counted and skipped, never fatal and never usage" (fun () ->
              let summary =
                  summarize
                      [ assistant "a1" "msg-1" fullUsage []
                        "{not json"
                        "[1,2,3]"
                        """{"type":"assistant","message":{"id":"msg-2","usage":{"input_tokens":5""" ]

              Assert.equal 3 summary.MalformedLines
              Assert.equal 1 summary.ModelRequests
              Assert.equal (Some 2m) (value "tokens.input" summary).Value)

          t "ingest accepts a transcript with a truncated final line and records the count in raw telemetry" (fun () ->
              withRoot (fun root ->
                  writeExecution root "EXE-1" None
                  let text = (fixture "construct-008-A1-usage-only.jsonl").TrimEnd() + "\n{\"type\":\"assistant\",\"mess"
                  FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") SessionTranscript.adapterName text |> ok |> ignore
                  let record = readExecution root "EXE-1"
                  let raw = record["rawTelemetry"].AsArray()
                  let payload = raw[0]["payload"]
                  Assert.equal 1 (payload["malformedLines"].GetValue<int>())))

          // ---- parent/child ----
          t "a request a parent and its child both hold belongs to the child and is counted once" (fun () ->
              let parents = Map.ofList [ "EXE-child", "EXE-parent" ]

              let attribution =
                  RequestAttribution.attribute
                      parents
                      [ ledger "EXE-parent" [ request "a" 1L; request "b" 10L; request "c" 100L ]
                        ledger "EXE-child" [ request "b" 10L; request "c" 100L; request "d" 1000L ] ]

              let owned = attribution.Owned |> List.map (fun (owner, row) -> row.Key, owner.ExecutionId) |> List.sort
              Assert.equal [ "a", "EXE-parent"; "b", "EXE-child"; "c", "EXE-child"; "d", "EXE-child" ] owned
              Assert.equal 2 attribution.DuplicateHoldings
              Assert.empty attribution.Conflicts)

          t "a parent rollup adds its child's usage once, never the two aggregates as independent consumption" (fun () ->
              let parents = Map.ofList [ "EXE-child", "EXE-parent" ]

              let attribution =
                  RequestAttribution.attribute
                      parents
                      [ ledger "EXE-parent" [ request "a" 1L; request "b" 10L ]
                        ledger "EXE-child" [ request "b" 10L; request "c" 100L ] ]

              let measurements =
                  RequestAttribution.cells attribution
                  |> List.collect (fun cell ->
                      cell.Totals
                      |> List.map (fun (metric, sum) ->
                          measurement cell.ExecutionId (RequestAttribution.ancestors parents cell.ExecutionId) metric (float sum) EvidenceQuality.ProviderReported None None))

              let byExecution = Usage.aggregate UsageDimension.Execution Map.empty measurements
              let rollup = Usage.aggregate UsageDimension.ExecutionTree Map.empty measurements
              Assert.equal (Some 1.0) (total byExecution "EXE-parent" "tokens.input")
              Assert.equal (Some 110.0) (total byExecution "EXE-child" "tokens.input")
              Assert.equal (Some 111.0) (total rollup "EXE-parent" "tokens.input")
              Assert.equal (Some 110.0) (total rollup "EXE-child" "tokens.input"))

          t "a request held by unrelated executions is counted once and reported as a conflict" (fun () ->
              let attribution = RequestAttribution.attribute Map.empty [ ledger "EXE-y" [ request "z" 5L ]; ledger "EXE-x" [ request "z" 5L ] ]
              Assert.equal 1 attribution.Owned.Length
              Assert.equal [ { Key = "z"; Executions = [ "EXE-x"; "EXE-y" ]; CountedIn = "EXE-x" } ] attribution.Conflicts)

          t "ancestors are cycle-safe" (fun () ->
              Assert.equal [ "B"; "C" ] (RequestAttribution.ancestors (Map.ofList [ "A", "B"; "B", "C"; "C", "A" ]) "A"))

          t "the usage report counts a transcript ingested into a parent and its child once" (fun () ->
              withRoot (fun root ->
                  writeExecution root "EXE-parent" None
                  writeExecution root "EXE-child" (Some "EXE-parent")
                  let text = fixture "construct-008-A1-usage-only.jsonl"

                  for execution in [ "EXE-parent"; "EXE-child" ] do
                      FileTelemetryFinalizationRepository.ingestTarget root (Some execution) SessionTranscript.adapterName text |> ok |> ignore

                  let report dimension =
                      let scope = FileTelemetryUsageRepository.read root None dimension
                      scope, Usage.aggregateWith dimension scope.ExecutionsByKey scope.Required scope.Reasons scope.Measurements

                  let scope, byExecution = report UsageDimension.Execution
                  let _, rollup = report UsageDimension.ExecutionTree
                  let _, byWorkItem = report UsageDimension.WorkItem
                  Assert.equal (Some 26.0) (total byExecution "EXE-child" "tokens.input")
                  Assert.equal None (total byExecution "EXE-parent" "tokens.input")
                  Assert.equal (Some 26.0) (total rollup "EXE-parent" "tokens.input")
                  Assert.equal (Some 792100.0) (total byWorkItem "WI-A" "tokens.cache_read")
                  Assert.equal (Some 13.0) (total byWorkItem "WI-A" "model.requests")
                  Assert.equal 13 scope.Attribution.DuplicateHoldings
                  // The transcript's other metrics are the child's too.
                  Assert.equal (Some 13.0) (total byWorkItem "WI-A" "tool.calls")
                  Assert.equal (Some 13.0) (total rollup "EXE-parent" "tool.calls")
                  Assert.equal None (total byExecution "EXE-parent" "tool.calls")
                  Assert.equal 1 scope.Attribution.DuplicateSnapshotHoldings
                  Assert.empty scope.Attribution.Conflicts))

          t "ingest --step attributes a transcript's usage to a started step, and refuses one never started" (fun () ->
              withRoot (fun root ->
                  writeExecution root "EXE-1" None
                  let text = fixture "construct-008-A1-usage-only.jsonl"

                  match FileTelemetryFinalizationRepository.ingestTargetAt root (Some "EXE-1") SessionTranscript.adapterName (Some "STEP-impl") text with
                  | Error message -> Assert.isTrue (message.Contains "has not been started") message
                  | Ok _ -> failwith "a step that was never started must be refused"

                  FileTelemetryFinalizationRepository.recordStep root (Some "EXE-1") StepTransition.Start "STEP-impl" (Some "implement") None "2026-10-13T10:00:00.000Z" |> ok |> ignore
                  FileTelemetryFinalizationRepository.ingestTargetAt root (Some "EXE-1") SessionTranscript.adapterName (Some "STEP-impl") text |> ok |> ignore
                  let scope = FileTelemetryUsageRepository.read root None UsageDimension.Step
                  let groups = Usage.aggregateWith UsageDimension.Step scope.ExecutionsByKey scope.Required scope.Reasons scope.Measurements
                  Assert.equal (Some 792100.0) (total groups "STEP-impl" "tokens.cache_read")
                  Assert.equal (Some 13.0) (total groups "STEP-impl" "tool.calls")))

          // ---- cost provenance ----
          t "provider-reported and estimated cost are different types and never one total" (fun () ->
              Assert.equal CostBasis.ProviderReported (CostBasis.ofMeasurement "observed" None)
              Assert.equal CostBasis.Unpriced (CostBasis.ofMeasurement "estimated" None)
              Assert.equal "estimated:fixture-prices@v1/2026-10-01" (CostBasis.code (CostBasis.ofMeasurement "estimated" (Some table.Reference)))

              let groups =
                  Usage.aggregate
                      UsageDimension.Execution
                      Map.empty
                      [ measurement "EXE-a" [] "cost.execution_total" 2.0 EvidenceQuality.ProviderReported (Some "provider-reported") None
                        measurement "EXE-a" [] "cost.execution_total" 3.0 EvidenceQuality.Estimated (Some(CostBasis.code (CostBasis.Estimated table.Reference))) None ]

              Assert.equal [ Some "estimated:fixture-prices@v1/2026-10-01", Some 3.0; Some "provider-reported", Some 2.0 ] (groups |> List.map (fun group -> group.CostBasis, group.Total)))

          t "an estimate prices each bucket at its own rate and names its price table" (fun () ->
              let row: LedgerRequest =
                  { Key = "msg-1"
                    Model = Some "model-x"
                    AgentId = None
                    Values =
                      Map.ofList
                          [ "tokens.input", 1_000_000L
                            "tokens.output", 100_000L
                            "tokens.cache_read", 1_000_000L
                            "tokens.cache_write", 300_000L
                            "tokens.cache_write_5m", 200_000L
                            "tokens.cache_write_1h", 100_000L ] }

              // 2 + 1 + 0.2 + 0.5 + 0.4
              Assert.equal (Ok 4.1m) (UsageCost.estimate table [ row ])
              Assert.equal (Error "the price table has no prices for model 'model-y'") (UsageCost.estimate table [ { row with Model = Some "model-y" } ])

              let unsplit = { row with Values = row.Values |> Map.remove "tokens.cache_write_5m" |> Map.remove "tokens.cache_write_1h" }
              Assert.equal (Error "the price table has no cache-write rate") (UsageCost.estimate table [ unsplit ])
              Assert.equal (Error "the request's input tokens are unknown") (UsageCost.estimate table [ { row with Values = row.Values.Remove "tokens.input" } ]))

          t "a price table must name its source, version and effective date" (fun () ->
              Assert.equal (Error "price table requires 'effectiveDate'") (PriceTableReader.parse """{"source":"s","version":"v","currency":"USD","models":{"m":{"input":1}}}""")

              let parsed = PriceTableReader.parse """{"source":"s","version":"v","effectiveDate":"2026-10-01","currency":"USD","models":{"m":{"input":1,"output":5}}}""" |> ok
              Assert.equal (Some "2026-10-01") parsed.Reference.EffectiveDate
              Assert.equal (Some 5m) parsed.Models["m"].Output)

          t "recording an estimated cost without a dated price table is refused; provider-reported cost needs none" (fun () ->
              withRoot (fun root ->
                  writeExecution root "EXE-1" None

                  let request quality pricing : FileTelemetryFinalizationRepository.RecordMetricRequest =
                      { MetricId = "cost.execution_total"
                        Value = 1.5
                        Unit = None
                        Currency = Some "USD"
                        Quality = quality
                        Confidence = FileTelemetryFinalizationRepository.TextConfidence "medium"
                        Scope = "execution"
                        Source = { Type = "calculated"; Name = "fixture"; Mechanism = "price-table" }
                        PricingSource = pricing
                        PricingVersion = pricing
                        CollectedAt = None
                        Step = None }

                  match FileTelemetryFinalizationRepository.recordMetricPriced root (Some "EXE-1") (request "estimated" (Some "table")) None with
                  | Error message -> Assert.isTrue (message.Contains "--pricing-effective-at") message
                  | Ok _ -> failwith "an undated estimate must be refused"

                  FileTelemetryFinalizationRepository.recordMetricPriced root (Some "EXE-1") (request "estimated" (Some "table")) (Some "2026-10-01") |> ok |> ignore

                  let observed = { request "observed" None with Source = { Type = "platform"; Name = "fixture"; Mechanism = "session-record" } }
                  FileTelemetryFinalizationRepository.recordMetricPriced root (Some "EXE-1") observed None |> ok |> ignore

                  let scope = FileTelemetryUsageRepository.read root None UsageDimension.Execution

                  let costs =
                      scope.Measurements
                      |> List.filter (fun measurement -> measurement.MetricId = "cost.execution_total")
                      |> List.map (fun measurement -> measurement.CostBasis, measurement.Value)
                      |> List.sort

                  Assert.equal [ Some "estimated:table@table/2026-10-01", 1.5; Some "provider-reported", 1.5 ] costs))

          // ---- host versus agent ----
          t "host measurements are their own totals, never added to the agent's" (fun () ->
              let groups =
                  Usage.aggregate
                      UsageDimension.Execution
                      Map.empty
                      [ measurement "EXE-a" [] "tool.calls" 13.0 EvidenceQuality.ProviderReported None None
                        measurement "EXE-a" [] "tool.calls" 2.0 EvidenceQuality.Measured None (Some "host") ]

              Assert.equal [ None, Some 13.0; Some "host", Some 2.0 ] (groups |> List.map (fun group -> group.Origin, group.Total)))

          // ---- acceptance ----
          t "first-pass and repaired acceptance are distinct outcomes and repairs are counted" (fun () ->
              Assert.equal (Ok AcceptanceOutcome.FirstPass) (Acceptance.outcome (Some 1L) (Some 0L) (Some 0L))
              Assert.equal (Ok(AcceptanceOutcome.Repaired(Some 2L))) (Acceptance.outcome (Some 0L) (Some 1L) (Some 2L))
              Assert.equal (Ok(AcceptanceOutcome.NotAccepted(Some 3L))) (Acceptance.outcome (Some 0L) (Some 0L) (Some 3L))
              Assert.equal (Ok AcceptanceOutcome.Unknown) (Acceptance.outcome None None None)

              for contradictory in [ Acceptance.outcome (Some 1L) (Some 1L) None; Acceptance.outcome (Some 1L) None (Some 1L); Acceptance.outcome None (Some 1L) (Some 0L) ] do
                  match contradictory with
                  | Error _ -> ()
                  | Ok outcome -> failwith $"expected a contradiction, got {outcome}")

          // ---- output is a lower bound ----
          t "transcript output is carried as a lower bound, never as the provider's final count" (fun () ->
              let summary = summarize [ assistant "a1" "msg-1" fullUsage [] ]
              Assert.isTrue (value "tokens.output" summary).LowerBound "output is recorded at streaming start"
              Assert.isTrue (not (value "tokens.input" summary).LowerBound) "input is final"

              withRoot (fun root ->
                  writeExecution root "EXE-1" None
                  FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") SessionTranscript.adapterName (fixture "construct-008-A1-usage-only.jsonl") |> ok |> ignore
                  let scope = FileTelemetryUsageRepository.read root None UsageDimension.Execution
                  let groups = Usage.aggregateWith UsageDimension.Execution scope.ExecutionsByKey scope.Required scope.Reasons scope.Measurements
                  let bound metric = groups |> List.find (fun group -> group.Key = "EXE-1" && group.MetricId = metric && group.Origin.IsNone) |> _.LowerBound
                  Assert.isTrue (bound "tokens.output") "the output total is a lower bound"
                  Assert.isTrue (not (bound "tokens.cache_read")) "cache read is exact"

                  let record = readExecution root "EXE-1"
                  let metrics = record["metrics"].AsArray()
                  let isOutput (metric: JsonNode) = metric["id"].GetValue<string>() = "tokens.output"
                  let stored = metrics |> Seq.find isOutput
                  let dimensions = stored["dimensions"]
                  Assert.equal "lower" (dimensions["bound"].GetValue<string>())))

          // ---- golden: CONSTRUCT-010 list_events normalized by events-to-transcript.py ----
          t "golden: a list_events page normalized by CONSTRUCT-010 events-to-transcript.py equals usage.py's totals" (fun () ->
              // Five real assistant events (two requests) of one remote session's
              // list_events page, content removed, normalized by research-documents
              // CONSTRUCT-010/harness/events-to-transcript.py. usage.py on the
              // result: requests 2, input 4, cache write 3835, cache read 676526,
              // output 5, cumulative 680370, tool calls 2, wall 4.701 s.
              let summary = fixture "construct-010-events-normalized.jsonl" |> ClaudeSessionTranscriptReader.parseLines |> SessionTranscript.summarize
              let known metricId = (value metricId summary).Value
              Assert.equal 2 summary.ModelRequests
              Assert.equal (Some 4m, Some 3835m, Some 676526m, Some 5m) (known "tokens.input", known "tokens.cache_write", known "tokens.cache_read", known "tokens.output")
              Assert.equal (Some 680370m) ([ "tokens.input"; "tokens.cache_write"; "tokens.cache_read"; "tokens.output" ] |> List.map known |> List.reduce (Option.map2 (+)))
              Assert.equal (Some 2m) (known "tool.calls")
              Assert.equal (Some 4701m) (known "time.agent_wall_ms")
              Assert.equal (Some 0m, Some 3835m) (known "tokens.cache_write_5m", known "tokens.cache_write_1h")
              // The normalized form carries no session ID: the snapshot is
              // identified by its content instead.
              Assert.equal None (SessionTranscript.snapshotId summary))

          // ---- golden: CONSTRUCT-008 run A1 against usage.py ----
          t "golden: CONSTRUCT-008 run A1 totals equal usage.py's for the same transcript" (fun () ->
              // research-documents research/construction-by-design/CONSTRUCT-008/results/A1/usage.json
              // (usage.py over transcript.jsonl, sha256 af15e33a...f4ae); the
              // fixture is that transcript reduced to ids, usage, timestamps and
              // tool names, on which usage.py reports the same totals.
              let summary = fixture "construct-008-A1-usage-only.jsonl" |> ClaudeSessionTranscriptReader.parseLines |> SessionTranscript.summarize
              let known metricId = (value metricId summary).Value
              Assert.equal 13 summary.ModelRequests
              Assert.equal (Some 26m) (known "tokens.input")
              Assert.equal (Some 41786m) (known "tokens.cache_write")
              Assert.equal (Some 792100m) (known "tokens.cache_read")
              Assert.equal (Some 213m) (known "tokens.output")
              // usage.py's cumulative_total_tokens: the four buckets' sum.
              Assert.equal (Some 834125m) ([ "tokens.input"; "tokens.cache_write"; "tokens.cache_read"; "tokens.output" ] |> List.map known |> List.reduce (Option.map2 (+)))
              Assert.equal (Some 13m) (known "tool.calls")
              Assert.equal [ "Bash", 12; "SubagentHandback", 1 ] summary.ToolCalls
              Assert.equal (Some 208178m) (known "time.agent_wall_ms")
              Assert.equal [ "claude-opus-5-5" ] summary.Models
              // Beyond usage.py: the TTL split, and reasoning, which this
              // transcript does not expose.
              Assert.equal (Some 41786m, Some 0m) (known "tokens.cache_write_5m", known "tokens.cache_write_1h")
              Assert.equal None (known "tokens.reasoning")
              Assert.equal 0 summary.MalformedLines
              Assert.isTrue summary.Sidechain "a subagent transcript"
              Assert.equal (Some "claude-session-5fb947e5-c0fd-51ef-842d-2e3b152799fb-agent-ab358fda66c8dec49") (SessionTranscript.snapshotId summary)) ]
