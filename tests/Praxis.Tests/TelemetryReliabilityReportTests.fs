namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Domain.Telemetry
open Praxis.Infrastructure.Telemetry

/// PRX-REL-009 / RES-REL-003: provider usage ingestion (dedupe, unavailable
/// versus zero) and the deterministic cost-per-verified-requirement report.
[<RequireQualifiedAccess>]
module TelemetryReliabilityReportTests =
    let private fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "reliability")
    let private fixture (relative: string) = Path.Combine(fixtures, relative)
    let private session = "11111111-1111-4111-8111-111111111111"

    let private input (priced: bool) =
        let events, lines, malformed = ReliabilityInputs.readEvents (fixture "events.jsonl")
        let observations, sources = ReliabilityInputs.readTranscripts (ReliabilityInputs.transcriptFiles [ fixture "transcripts" ])

        { Events = events
          EventLines = lines
          MalformedEventLines = malformed
          Executions = ReliabilityInputs.readExecutions (fixture "executions")
          Transcripts = observations
          Sources = sources
          PriceTable =
            if priced then
                match ReliabilityInputs.readPriceTable (fixture "price-table.json") with
                | Ok table -> Some table
                | Error message -> failwith message
            else
                None
          SessionWindow = true
          WorkItemFilter = [] }

    let private render (value: ReliabilityInput) = ReliabilityReport.build value |> ReliabilityReport.toJson

    let private at (path: string) (node: JsonNode) =
        path.Split('.') |> Array.fold (fun (current: JsonNode) key -> if isNull current then null else current[key]) node

    let private report value = JsonNode.Parse(render value)
    let private textAt path node = (at path node).GetValue<string>()
    let private intAt path node = (at path node).GetValue<int64>()
    let private decimalAt path node = (at path node).GetValue<decimal>()

    let private workItem (id: string) (node: JsonNode) =
        (node["workItems"] :?> JsonArray) |> Seq.find (fun item -> item["id"].GetValue<string>() = id)

    let private requirement (id: string) (node: JsonNode) =
        (node["requirements"] :?> JsonArray) |> Seq.find (fun item -> item["id"].GetValue<string>() = id)

    let tests =
        [ { Name = "reliability: streamed and cross-file duplicate usage counts once; unidentified and malformed lines are counted, never summed"
            Run =
              fun () ->
                  let observations, sources = ReliabilityInputs.readTranscripts (ReliabilityInputs.transcriptFiles [ fixture "transcripts" ])
                  let claude = observations |> List.filter (fun o -> o.Provider = "anthropic")
                  let deduped = ProviderUsage.dedupe claude
                  Assert.equal [ "anthropic:msg_A"; "anthropic:msg_B"; "anthropic:msg_C"; "anthropic:msg_D" ] (deduped.Observations |> List.map _.ObservationId)
                  Assert.equal 2 deduped.DuplicateEntries
                  Assert.equal 1 (sources |> List.sumBy _.UnidentifiedUsageEntries)
                  Assert.equal 1 (sources |> List.sumBy _.MalformedLines)
                  let a = deduped.Observations |> List.find (fun o -> o.ObservationId = "anthropic:msg_A")
                  Assert.equal (Some "req_A") a.RequestId
                  Assert.equal (Some "claude-test-1") a.Model
                  Assert.equal (Map.ofList [ "tokens.cache_read", 100L; "tokens.cache_write", 20L; "tokens.input", 10L; "tokens.output", 5L ]) a.Values
                  let d = deduped.Observations |> List.find (fun o -> o.ObservationId = "anthropic:msg_D")
                  Assert.equal (Some "ax") d.AgentId
                  Assert.equal (Some session) d.SessionId }

          { Name = "reliability: codex cumulative counters become increments; a repeated reading or null info adds nothing; cache write is not applicable"
            Run =
              fun () ->
                  let observations, _ = ReliabilityInputs.readTranscripts (ReliabilityInputs.transcriptFiles [ fixture "transcripts" ])
                  let codex = observations |> List.filter (fun o -> o.Provider = "openai")
                  Assert.equal [ "openai:44444444-4444-4444-8444-444444444444:total@110"; "openai:44444444-4444-4444-8444-444444444444:total@280" ] (codex |> List.map _.ObservationId)
                  Assert.equal (Map.ofList [ "tokens.cache_read", 40L; "tokens.input", 60L; "tokens.output", 10L; "tokens.reasoning", 5L ]) codex[0].Values
                  Assert.equal (Map.ofList [ "tokens.cache_read", 100L; "tokens.input", 50L; "tokens.output", 20L; "tokens.reasoning", 0L ]) codex[1].Values
                  Assert.equal (Some 280L) (codex |> List.map ProviderUsage.total |> List.choose id |> List.sum |> Some)
                  Assert.isTrue (codex |> List.forall (fun o -> o.NotApplicable.Contains "tokens.cache_write" && not (o.Values.ContainsKey "tokens.cache_write"))) "cache write must be not-applicable, not zero"
                  Assert.equal (Some "gpt-test") codex[0].Model }

          { Name = "reliability: unavailable usage is null with a reason, an observed zero is zero"
            Run =
              fun () ->
                  let full = input false
                  let zeroOnly = { full with Transcripts = []; Sources = []; Executions = full.Executions |> List.filter (fun e -> e.ExecutionId = "EXE-3") }
                  let zero = report zeroOnly
                  Assert.equal "available" (textAt "usage.tokens.total.availability" zero)
                  Assert.equal 0L (intAt "usage.tokens.total.value" zero)
                  let none = report { zeroOnly with Executions = full.Executions |> List.filter (fun e -> e.ExecutionId = "EXE-1") }
                  Assert.equal "unavailable" (textAt "usage.tokens.total.availability" none)
                  Assert.isTrue (isNull (at "usage.tokens.total.value" none)) "unavailable tokens must be null, never 0"
                  Assert.isTrue (isNull (at "usage.cost.estimated.value" none)) "unavailable cost must be null, never 0"
                  Assert.equal "unavailable" (textAt "usage.cost.billed.availability" none) }

          { Name = "reliability: shared usage splits evenly, remainder to the ordinally first target, and allocations sum exactly"
            Run =
              fun () ->
                  Assert.equal [ "a", Map.ofList [ "x", 4L; "y", 1L ]; "b", Map.ofList [ "x", 3L; "y", 0L ] ] (ReliabilityReport.splitEven [ "b"; "a" ] (Map.ofList [ "x", 7L; "y", 1L ]))
                  let node = report (input false)
                  // msg_A (135) whole + half of msg_B (4+100) + msg_D share (2).
                  Assert.equal 241L (intAt "tokensTotal" (workItem "WI-1" node))
                  Assert.equal 241L (intAt "tokensTotal" (requirement "REQ-1" node) + intAt "tokensTotal" (requirement "REQ-2" node))
                  Assert.equal 122L (intAt "tokensTotal" (requirement "REQ-1" node))
                  Assert.equal 2L (intAt "usage.attributionMethods.session-window-shared" node)
                  // The codex session has no execution: reported, not attributable.
                  Assert.equal 2L (intAt "usage.unattributed.byReason.no-execution-for-session" node)
                  Assert.equal 344L (intAt "usage.tokens.total.value" node)
                  Assert.equal "partial" (textAt "usage.tokens.total.availability" node) }

          { Name = "reliability: an ingested ledger row and its transcript line are one observation, attributed explicitly"
            Run =
              fun () ->
                  let full = input false
                  let ledgerRow = { (full.Transcripts |> List.find (fun o -> o.ObservationId = "anthropic:msg_C")) with Origin = "execution-ledger"; ExplicitExecution = Some "EXE-1"; OccurredAt = None }
                  let executions = full.Executions |> List.map (fun e -> if e.ExecutionId = "EXE-1" then { e with Ledger = [ ledgerRow ] } else e)
                  let node = report { full with Executions = executions }
                  Assert.equal 7L (intAt "usage.observations" node)
                  Assert.equal 1L (intAt "usage.attributionMethods.explicit" node)
                  Assert.equal 3L (intAt "usage.duplicateEntriesExcluded" node) }

          { Name = "reliability: event metrics follow interface I1 (first pass, rework, duplicate ownership, stale writes, elapsed)"
            Run =
              fun () ->
                  let node = report (input true)
                  Assert.equal 3L (intAt "verification.verifiedRequirements.value" node)
                  Assert.equal 0.5m (decimalAt "verification.firstPassVerificationRate.value" node)
                  Assert.equal 2L (intAt "verification.firstPassVerificationRate.denominator" node)
                  Assert.equal 0.5m (decimalAt "verification.reworkFrequency.value" node)
                  Assert.equal 1L (intAt "verification.duplicateOwnershipAttempts.value" node)
                  Assert.equal 2L (intAt "verification.staleWriteRejections.value" node)
                  Assert.equal 1L (intAt "verification.retries.completionReplays.value" node)
                  Assert.equal 2100000L (intAt "time.elapsedMs.value" node)
                  Assert.equal 1L (intAt "inputs.events.duplicateEventIds" node)
                  Assert.equal 1L (intAt "inputs.events.malformedLines" node)
                  // 55 + 34 per million at the fixture's (non-real) test rates.
                  Assert.equal 0.000089m (decimalAt "usage.cost.estimated.value" node)
                  Assert.equal "partial" (textAt "usage.cost.estimated.availability" node)
                  Assert.equal "partial" (textAt "perVerifiedRequirement.estimatedCost.availability" node)
                  Assert.equal 3L (intAt "perVerifiedRequirement.estimatedCost.denominator" node) }

          { Name = "reliability: zero verified requirements leaves every per-requirement ratio unavailable, never zero or infinite"
            Run =
              fun () ->
                  let full = input true
                  let node = report { full with Events = full.Events |> List.filter (fun e -> e.Type <> "work.completion.verified") }
                  Assert.equal 0L (intAt "verification.verifiedRequirements.value" node)

                  for metric in [ "estimatedCost"; "billedCost"; "tokensTotal" ] do
                      Assert.equal "unavailable" (textAt $"perVerifiedRequirement.{metric}.availability" node)
                      Assert.isTrue (isNull (at $"perVerifiedRequirement.{metric}.value" node)) $"{metric} must be null"
                      Assert.equal 0L (intAt $"perVerifiedRequirement.{metric}.denominator" node) }

          { Name = "reliability: without a price table cost is unavailable, and explicit-only attribution leaves session usage unattributed"
            Run =
              fun () ->
                  let node = report (input false)
                  Assert.equal "unavailable" (textAt "usage.cost.estimated.availability" node)
                  Assert.isTrue ((textAt "usage.cost.estimated.reason" node).Contains "no price table") "reason must name the missing price table"
                  let strict = report { input false with SessionWindow = false }
                  Assert.equal 6L (intAt "usage.unattributed.byReason.session-window-attribution-disabled" strict)
                  Assert.equal 1L (intAt "usage.attributionMethods.execution-record" strict) }

          { Name = "reliability: identical inputs give byte-identical output, whatever the input order, and the CLI writes the same bytes"
            Run =
              fun () ->
                  let value = input true
                  let expected = render value
                  let reversed = { value with Events = List.rev value.Events; Executions = List.rev value.Executions; Transcripts = List.rev value.Transcripts; Sources = List.rev value.Sources }
                  Assert.equal expected (render reversed)
                  Assert.equal expected (render value)
                  let root = Path.Combine(Path.GetTempPath(), $"praxis-reliability-{Guid.NewGuid():N}")
                  Directory.CreateDirectory root |> ignore
                  let arguments out = [ "telemetry"; "reliability-report"; "--events"; fixture "events.jsonl"; "--executions"; fixture "executions"; "--transcripts"; fixture "transcripts"; "--price-table"; fixture "price-table.json"; "--out"; out ]
                  PraxisCli.run root None (arguments "first.json") |> PraxisCli.ok |> ignore
                  PraxisCli.run root None (arguments "second.json") |> PraxisCli.ok |> ignore
                  let first = File.ReadAllBytes(Path.Combine(root, "first.json"))
                  Assert.isTrue (first = File.ReadAllBytes(Path.Combine(root, "second.json"))) "two CLI runs differ"
                  Assert.equal expected (Text.Encoding.UTF8.GetString first) } ]
