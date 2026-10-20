namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Domain.Execution
open Praxis.Domain.Telemetry
open Praxis.Infrastructure.Telemetry
open Praxis.Infrastructure.Work

/// PRAXIS-REL-010 (F2, interface I7): per-agent usage attribution within one
/// orchestrating session, and PRAXIS-REL-013 (F5): the versioned price table.
[<RequireQualifiedAccess>]
module TelemetryAgentAttributionTests =
    let private fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "reliability-agents")
    let private fixture (relative: string) = Path.Combine(fixtures, relative)
    let private session = "22222222-2222-4222-8222-222222222222"

    let private ok (result: Result<'a, string>) =
        match result with
        | Ok value -> value
        | Error message -> failwith message

    let private error (result: Result<'a, string>) =
        match result with
        | Ok _ -> failwith "expected an error"
        | Error message -> message

    let private input (priced: bool) =
        let events, lines, malformed = ReliabilityInputs.readEvents (fixture "events.jsonl")
        let observations, sources = ReliabilityInputs.readTranscripts (ReliabilityInputs.transcriptFiles [ fixture "transcripts" ])

        { Events = events
          EventLines = lines
          MalformedEventLines = malformed
          Executions = []
          Transcripts = observations
          Sources = sources
          PriceTable = (if priced then Some(ReliabilityInputs.readPriceTable (fixture "price-table-v1.json") |> ok) else None)
          AgentBinding = Some(ReliabilityInputs.readAgentBinding (fixture "agent-binding.json") |> ok)
          SessionWindow = true
          WorkItemFilter = [] }

    let private render (value: ReliabilityInput) = ReliabilityReport.build value |> ReliabilityReport.toJson
    let private report value = JsonNode.Parse(render value)

    let private at (path: string) (node: JsonNode) =
        path.Split('.') |> Array.fold (fun (current: JsonNode) key -> if isNull current then null else current[key]) node

    let private textAt path node = (at path node).GetValue<string>()
    let private intAt path node = (at path node).GetValue<int64>()
    let private decimalAt path node = (at path node).GetValue<decimal>()

    let private find (path: string) (key: string) (id: string) (node: JsonNode) =
        (at path node :?> JsonArray) |> Seq.find (fun item -> item[key].GetValue<string>() = id)

    let private agent id node = find "agentAttribution.agents" "agentId" id node
    let private kind name (entry: JsonNode) = intAt $"tokens.{name}.value" entry
    let private availability name (entry: JsonNode) = textAt $"tokens.{name}.availability" entry

    let private observation id agentId sidechain (at: string) (values: (string * int64) list) : UsageObservation =
        { ObservationId = $"anthropic:{id}"
          Provider = "anthropic"
          Runtime = "claude-code"
          SessionId = Some session
          AgentId = agentId
          Model = Some "claude-test-1"
          MessageId = Some id
          RequestId = None
          OccurredAt = Some(DateTimeOffset.Parse at)
          Values = Map.ofList values
          NotApplicable = Set.empty
          Origin = "claude-transcript"
          ExplicitExecution = None
          Sidechain = sidechain
          Source = Some "test" }

    let private noTimestamp (value: string) = DateTimeOffset.Parse(value, Globalization.CultureInfo.InvariantCulture)

    let tests =
        [ { Name = "agent attribution: three sub-agents and an orchestrator sharing one session each get exactly their own deduplicated tokens, and the parts sum to the global total"
            Run =
              fun () ->
                  let node = report (input false)
                  // 12 usage entries: 2 streamed repeats, 2 parent echoes, 1 overlapping copy.
                  Assert.equal 7L (intAt "agentAttribution.observations" node)
                  Assert.equal 5L (intAt "agentAttribution.duplicateEntriesExcluded" node)
                  Assert.equal 3L (intAt "agentAttribution.crossFileDuplicatesExcluded" node)
                  Assert.equal 3L (intAt "usage.crossFileDuplicatesExcluded" node)

                  let a1 = agent "a1" node
                  Assert.equal [ 10L; 52L; 60L; 10L; 10L; 0L; 132L ] ([ "input"; "output"; "cacheRead"; "cacheWrite"; "cacheWrite5m"; "cacheWrite1h"; "total" ] |> List.map (fun name -> kind name a1))
                  Assert.isTrue ([ "input"; "cacheRead"; "cacheWrite"; "cacheWrite5m"; "cacheWrite1h" ] |> List.forall (fun name -> availability name a1 = "available")) "a1 input and cache kinds available"
                  // Transcript output is written before streaming completes: an explicit lower bound.
                  Assert.equal "partial" (availability "output" a1)
                  Assert.equal "partial" (availability "total" a1)
                  Assert.isTrue ((textAt "tokens.output.reason" a1).Contains "lower bound") (textAt "tokens.output.reason" a1)
                  Assert.equal 2L (intAt "methods.agent-id" a1)

                  let a2 = agent "a2" node
                  Assert.equal [ 8L; 10L; 10L; 300L; 328L ] ([ "input"; "output"; "cacheRead"; "cacheWrite"; "total" ] |> List.map (fun name -> kind name a2))
                  // msg_B2 reports no cache_creation: the TTL split is a labelled lower bound.
                  Assert.equal "partial" (availability "cacheWrite5m" a2)
                  Assert.equal 100L (kind "cacheWrite5m" a2)
                  Assert.equal 200L (kind "cacheWrite1h" a2)

                  let a3 = agent "a3" node
                  Assert.equal [ 2L; 8L; 10L ] ([ "input"; "output"; "total" ] |> List.map (fun name -> kind name a3))
                  Assert.equal 1L (intAt "methods.agent-id" a3)

                  let orchestrator = find "agentAttribution.orchestrators" "sessionId" session node
                  // msg_O1 streamed with output 2 then 30: the maximum, not the first entry.
                  Assert.equal [ 15L; 50L; 300L; 50L; 415L ] ([ "input"; "output"; "cacheRead"; "cacheWrite"; "total" ] |> List.map (fun name -> kind name orchestrator))

                  // The parent's Agent tool result (usage 999s) is a summary, never an observation.
                  Assert.equal 35L (intAt "agentAttribution.tokens.input.value" node)
                  Assert.equal 885L (intAt "agentAttribution.tokens.total.value" node)
                  Assert.equal (132L + 328L + 10L + 415L) (intAt "agentAttribution.tokens.total.value" node)
                  Assert.equal 0L (intAt "agentAttribution.unattributed.observations" node)
                  Assert.isTrue ((at "agentAttribution.conservation.balanced" node).GetValue<bool>()) "agents + orchestrator must equal the global total"
                  Assert.equal 885L ([ "input"; "output"; "cacheRead"; "cacheWrite" ] |> List.sumBy (fun name -> intAt $"agentAttribution.conservation.parts.{name}" node))

                  let main = (at "inputs.transcripts" node :?> JsonArray) |> Seq.find (fun source -> source["echoedUsageEntries"].GetValue<int64>() > 0L)
                  Assert.equal 2L (main["echoedUsageEntries"].GetValue<int64>()) }

          { Name = "agent attribution: streamed entries of one message keep each bucket's maximum, whichever entry holds it"
            Run =
              fun () ->
                  let early = observation "msg_S" (Some "a1") (Some true) "2026-10-02T10:00:00Z" [ "tokens.input", 4L; "tokens.output", 3L; "tokens.cache_write", 0L ]
                  let late = { early with Values = Map.ofList [ "tokens.input", 4L; "tokens.output", 40L; "tokens.cache_write", 0L ] }
                  let other = { early with Values = Map.ofList [ "tokens.input", 4L; "tokens.output", 1L; "tokens.cache_write", 9L ]; AgentId = None; Source = Some "other" }
                  let deduped = ProviderUsage.dedupe [ early; late; other ]
                  let single = Assert.single deduped.Observations
                  Assert.equal (Map.ofList [ "tokens.cache_write", 9L; "tokens.input", 4L; "tokens.output", 40L ]) single.Values
                  Assert.equal (Some "a1") single.AgentId
                  Assert.equal 2 deduped.DuplicateEntries
                  Assert.equal 1 deduped.CrossFileDuplicates
                  Assert.equal [ "anthropic:msg_S" ] deduped.ConflictingIds }

          { Name = "agent attribution: an observation with several candidate agents or an unbound agent ID is reported unattributed, never split"
            Run =
              fun () ->
                  let full = input false
                  let ambiguous = observation "msg_X" None None "2026-10-02T10:07:00Z" [ "tokens.input", 1L; "tokens.output", 1L; "tokens.cache_read", 0L; "tokens.cache_write", 0L ]
                  let unbound = observation "msg_Y" (Some "zz") (Some true) "2026-10-02T10:07:30Z" [ "tokens.input", 3L; "tokens.output", 4L; "tokens.cache_read", 0L; "tokens.cache_write", 0L ]
                  let node = report { full with Transcripts = full.Transcripts @ [ ambiguous; unbound ] }
                  Assert.equal 2L (intAt "agentAttribution.unattributed.observations" node)
                  Assert.equal 1L (intAt "agentAttribution.unattributed.byReason.ambiguous-agents" node)
                  Assert.equal 1L (intAt "agentAttribution.unattributed.byReason.agent-not-bound" node)
                  Assert.equal "zz" ((at "agentAttribution.unattributed.agentIds" node).[0].GetValue<string>())
                  Assert.equal 9L (intAt "agentAttribution.unattributed.tokens.total.value" node)
                  // Agents keep exactly their own usage; nothing was split onto them.
                  Assert.equal 132L (kind "total" (agent "a1" node))
                  Assert.equal 328L (kind "total" (agent "a2" node))
                  Assert.isTrue ((at "agentAttribution.conservation.balanced" node).GetValue<bool>()) "unattributed usage stays in the conserved total"
                  Assert.equal 894L (intAt "agentAttribution.tokens.total.value" node) }

          { Name = "agent attribution: an execution's provider.agentInstanceId attributes, then a single bound agent in the session does (I7 order)"
            Run =
              fun () ->
                  let full = input false
                  let record =
                      """{"executionId":"EXE-Z","workItemId":"WI-B","startedAt":"2026-10-02T10:20:00Z","finalizedAt":"2026-10-02T10:30:00Z",
                          "identity":{"provider":"anthropic","runtime":"claude-code","sessionId":null},
                          "provider":{"sessionId":"22222222-2222-4222-8222-222222222222","agentInstanceId":"a2"},"metrics":[],"rawTelemetry":[]}"""

                  let execution = ReliabilityInputs.parseExecution record |> Option.get
                  Assert.equal (Some "a2") execution.AgentInstanceId
                  Assert.equal (Some session) execution.SessionId
                  let viaExecution = observation "msg_E" None None "2026-10-02T10:25:00Z" [ "tokens.input", 2L; "tokens.output", 2L; "tokens.cache_read", 0L; "tokens.cache_write", 0L ]
                  let node = report { full with Executions = [ execution ]; Transcripts = full.Transcripts @ [ viaExecution ] }
                  Assert.equal 1L (intAt "methods.execution-agent-instance" (agent "a2" node))
                  Assert.equal 332L (kind "total" (agent "a2" node))

                  let single = { full.AgentBinding.Value with Bindings = full.AgentBinding.Value.Bindings |> List.filter (fun entry -> entry.AgentId = "a3") }
                  let lone = observation "msg_L" None None "2026-10-02T10:40:00Z" [ "tokens.input", 1L; "tokens.output", 0L; "tokens.cache_read", 0L; "tokens.cache_write", 0L ]

                  Assert.equal
                      (AgentResolution.Attributed(AgentTarget.Agent "a3", "session-single-agent"))
                      (AgentAttribution.resolve single (fun _ -> None) lone)

                  Assert.equal (AgentResolution.Unattributed "no-agent-for-session") (AgentAttribution.resolve { single with Bindings = [] } (fun _ -> None) lone)
                  Assert.equal (AgentResolution.Unattributed "no-session-id") (AgentAttribution.resolve single (fun _ -> None) { lone with SessionId = None })
                  // A main-thread line is the orchestrator even when an execution names an agent.
                  Assert.equal
                      (AgentResolution.Attributed(AgentTarget.Orchestrator session, "main-thread"))
                      (AgentAttribution.resolve single (fun _ -> Some [ Some "a3" ]) { lone with Sidechain = Some false }) }

          { Name = "agent attribution: one compact summary per agent and per verified requirement, from the bound work items"
            Run =
              fun () ->
                  let node = report (input false)
                  let requirement id = find "agentAttribution.summaries.perVerifiedRequirement" "requirementId" id node
                  Assert.equal 3 ((at "agentAttribution.summaries.perVerifiedRequirement" node :?> JsonArray).Count)
                  Assert.equal 3 ((at "agentAttribution.summaries.perAgent" node :?> JsonArray).Count)
                  // a1 -> WI-A -> REQ-A1, REQ-A2: an even integer split of 132.
                  Assert.equal 66L (intAt "total" (requirement "REQ-A1"))
                  Assert.equal 66L (intAt "total" (requirement "REQ-A2"))
                  Assert.equal 26L (intAt "output" (requirement "REQ-A1"))
                  Assert.equal 328L (intAt "total" (requirement "REQ-B"))
                  // WI-C (agent a3) has no verified requirement: no requirement carries its usage.
                  Assert.equal [ "a1" ] ((at "agentAttribution.requirements" node :?> JsonArray) |> Seq.find (fun item -> item["requirementId"].GetValue<string>() = "REQ-A1") |> fun item -> (item["agents"] :?> JsonArray) |> Seq.map _.GetValue<string>() |> Seq.toList)
                  let a2 = find "agentAttribution.summaries.perAgent" "agentId" "a2" node
                  Assert.equal 328L (intAt "total" a2)
                  Assert.isTrue (isNull (at "cacheWrite5m" a2)) "an incomplete kind is null in a compact summary, never a partial number"
                  Assert.isTrue (isNull (at "estimatedCost" a2)) "no price table: cost unavailable"
                  Assert.equal 2L (intAt "observations" a2)
                  Assert.equal [ "estimatedCost"; "output"; "total" ] ((at "lowerBounds" a2 :?> JsonArray) |> Seq.map _.GetValue<string>() |> Seq.toList) }

          { Name = "price table: praxis.price-table/1 selects the entry effective at the observation's time and never applies a price outside its range"
            Run =
              fun () ->
                  let schedule = ReliabilityInputs.readPriceTable (fixture "price-table-v1.json") |> ok
                  Assert.equal PriceSchedule.VersionedFormat schedule.Format
                  let select at = PriceSchedule.select schedule "anthropic" (Some "claude-test-1") (Some(noTimestamp at))
                  Assert.equal (Some 100m) ((select "2026-09-15T00:00:00Z" |> ok).Prices.Input)
                  Assert.equal (Some 1m) ((select "2026-10-01T00:00:00Z" |> ok).Prices.Input)
                  Assert.isTrue ((select "2026-08-31T23:59:59Z" |> error).Contains "no anthropic/claude-test-1 price is effective") "before every range: unpriced"
                  Assert.isTrue ((PriceSchedule.select schedule "openai" (Some "claude-test-1") (Some(noTimestamp "2026-10-02T00:00:00Z")) |> error).Contains "no prices for openai/claude-test-1") "provider must match"
                  Assert.isTrue ((PriceSchedule.select schedule "anthropic" (Some "claude-test-1") None |> error).Contains "no timestamp") "no timestamp: unpriced"
                  // 5m/1h split priced at their own rates: 7 + 9*2 + 100*1.25 + 200*2 = 550 per million.
                  let values = Map.ofList [ "tokens.input", 7L; "tokens.output", 9L; "tokens.cache_read", 0L; "tokens.cache_write", 300L; "tokens.cache_write_5m", 100L; "tokens.cache_write_1h", 200L ]
                  Assert.equal 0.00055m (PriceSchedule.estimate schedule "anthropic" (Some "claude-test-1") (Some(noTimestamp "2026-10-02T10:04:00Z")) "k" values |> ok |> fst)
                  // The same usage a month earlier is priced at that month's rates, not today's.
                  Assert.equal 0.0316m (PriceSchedule.estimate schedule "anthropic" (Some "claude-test-1") (Some(noTimestamp "2026-09-15T00:00:00Z")) "k" values |> ok |> fst)

                  let node = report (input true)
                  Assert.equal 0.000132m (decimalAt "estimatedCost.value" (agent "a1" node)) // 132.5 per million, half-to-even at six places
                  Assert.equal "partial" (textAt "estimatedCost.availability" (agent "a2" node))
                  Assert.isTrue ((textAt "estimatedCost.reason" (agent "a2" node)).Contains "output_tokens") "the cost is a lower bound because output is"
                  Assert.equal 0.000554m (decimalAt "estimatedCost.value" (agent "a2" node))
                  // claude-test-2 is priced only from 2026-11-01: cost unavailable, tokens still reported.
                  let a3 = agent "a3" node
                  Assert.equal "unavailable" (textAt "estimatedCost.availability" a3)
                  Assert.isTrue ((textAt "estimatedCost.reason" a3).Contains "no anthropic/claude-test-2 price is effective") (textAt "estimatedCost.reason" a3)
                  Assert.equal 10L (kind "total" a3)
                  Assert.equal "available" (availability "input" a3)
                  Assert.isTrue (availability "total" a3 <> "unavailable") "tokens are reported when the price is missing"
                  Assert.equal "partial" (textAt "agentAttribution.estimatedCost.availability" node)
                  Assert.equal 0.000894m (decimalAt "agentAttribution.estimatedCost.value" node)
                  Assert.equal 3 ((at "inputs.priceTable.entries" node :?> JsonArray).Count)
                  Assert.equal "praxis.price-table/1" (textAt "inputs.priceTable.format" node) }

          { Name = "price table: the legacy format is still read compatibly; praxis.price-table/1 tables are validated"
            Run =
              fun () ->
                  let legacy = """{"source":"s","version":"v","effectiveDate":"2026-10-01","currency":"USD","unit":"per-million-tokens","models":{"m":{"input":1,"output":5}}}"""
                  let schedule = PriceTableReader.parseSchedule legacy |> ok
                  Assert.equal PriceSchedule.LegacyFormat schedule.Format
                  let entry = Assert.single schedule.Entries
                  Assert.equal None entry.Provider
                  Assert.equal None entry.EffectiveTo
                  Assert.isTrue (PriceSchedule.select schedule "anthropic" (Some "m") (Some(noTimestamp "2026-09-30T00:00:00Z")) |> Result.isError) "a legacy table never prices usage before its effective date"
                  Assert.isTrue (PriceSchedule.select schedule "anthropic" (Some "m") (Some(noTimestamp "2026-10-01T00:00:00Z")) |> Result.isOk) "and prices it from that date"
                  Assert.equal (Some 5m) ((PriceTableReader.parse legacy |> ok).Models["m"].Output)

                  let versioned (entries: string) = $"""{{"schema":"praxis.price-table/1","tableVersion":"t1","entries":[{entries}]}}"""
                  let entryJson (model: string) (from: string) (until: string) (currency: string) (source: string) =
                      $"""{{"provider":"anthropic","model":"{model}","effectiveFrom":"{from}","effectiveTo":{until},"currency":"{currency}","unit":"per-million-tokens","input":1,"output":1{source}}}"""
                  let source = ""","source":{"url":"https://example.invalid/p","retrievedAt":"2026-10-20"}"""
                  Assert.isTrue ((PriceTableReader.parse (versioned (entryJson "m" "2026-01-01" "null" "USD" source)) |> error).Contains "praxis.price-table/1") "telemetry usage refuses a dated table it cannot select from"
                  Assert.isTrue ((PriceTableReader.parseSchedule (versioned (entryJson "m" "2026-01-01" "null" "USD" "")) |> error).Contains "source") "source url and retrieval date are required"
                  Assert.isTrue ((PriceTableReader.parseSchedule (versioned (entryJson "m" "2026-01-01" "null" "USD" source + "," + entryJson "m" "2026-06-01" "\"2026-12-01\"" "USD" source)) |> error).Contains "overlap") "overlapping ranges are ambiguous"
                  Assert.isTrue ((PriceTableReader.parseSchedule (versioned (entryJson "m" "2026-01-01" "\"2026-06-01\"" "USD" source + "," + entryJson "n" "2026-01-01" "null" "EUR" source)) |> error).Contains "currencies") "one currency per table"
                  Assert.isTrue ((PriceTableReader.parseSchedule (versioned (entryJson "m" "2026-06-01" "\"2026-06-01\"" "USD" source)) |> error).Contains "effectiveTo at or before") "an empty range is refused"
                  let adjacent = PriceTableReader.parseSchedule (versioned (entryJson "m" "2026-01-01" "\"2026-06-01\"" "USD" source + "," + entryJson "m" "2026-06-01" "null" "USD" source)) |> ok
                  Assert.equal 2 adjacent.Entries.Length }

          { Name = "agent binding: praxis.agent-binding/1 is validated"
            Run =
              fun () ->
                  let binding = ReliabilityInputs.parseAgentBinding """{"bindings":[{"agentId":"a","sessionId":"s","scope":"t","workItems":["W"]}]}""" |> ok
                  Assert.equal [ "W" ] (Assert.single binding.Bindings).WorkItems
                  Assert.isTrue binding.Digest.IsSome "the binding digest is reported"
                  Assert.isTrue ((ReliabilityInputs.parseAgentBinding """{"schema":"other/1","bindings":[]}""" |> error).Contains "not supported") "schema"
                  Assert.isTrue ((ReliabilityInputs.parseAgentBinding """{"bindings":[{"sessionId":"s"}]}""" |> error).Contains "agentId") "agentId"
                  Assert.isTrue ((ReliabilityInputs.parseAgentBinding """{"bindings":[{"agentId":"a","sessionId":"s"},{"agentId":"a","sessionId":"s"}]}""" |> error).Contains "more than once") "duplicate" }

          { Name = "agent binding: the CLI reads --agent-binding, writes identical bytes twice, and refuses an invalid binding"
            Run =
              fun () ->
                  let root = Path.Combine(Path.GetTempPath(), $"praxis-agents-{Guid.NewGuid():N}")
                  Directory.CreateDirectory root |> ignore

                  try
                      let arguments binding out =
                          [ "telemetry"; "reliability-report"; "--events"; fixture "events.jsonl"; "--transcripts"; fixture "transcripts"; "--agent-binding"; binding; "--price-table"; fixture "price-table-v1.json"; "--out"; out ]

                      PraxisCli.run root None (arguments (fixture "agent-binding.json") "first.json") |> PraxisCli.ok |> ignore
                      PraxisCli.run root None (arguments (fixture "agent-binding.json") "second.json") |> PraxisCli.ok |> ignore
                      let first = File.ReadAllBytes(Path.Combine(root, "first.json"))
                      Assert.isTrue (first = File.ReadAllBytes(Path.Combine(root, "second.json"))) "two CLI runs differ"
                      Assert.equal (render (input true)) (Text.Encoding.UTF8.GetString first)
                      File.WriteAllText(Path.Combine(root, "bad.json"), """{"bindings":[{}]}""")
                      let refused = PraxisCli.run root None (arguments (Path.Combine(root, "bad.json")) "third.json")
                      Assert.equal 2 refused.ExitCode
                      Assert.isTrue (refused.Error.Contains "--agent-binding") refused.Error
                  finally
                      Directory.Delete(root, true) }

          { Name = "execution records: provider.sessionId and provider.agentInstanceId are recorded when the host sets them, and absent otherwise (I7)"
            Run =
              fun () ->
                  Assert.equal ({ SessionId = Some "s-1"; AgentInstanceId = None }: ProviderSession) (ProviderSession.fromLookup (function "CLAUDE_CODE_SESSION_ID" -> Some "s-1" | "PRAXIS_AGENT_INSTANCE_ID" -> Some "  " | _ -> None))

                  let parent = GitFixture.temporaryDirectory "agent-instance"

                  try
                      let _, clone = PraxisCli.installedRepository parent "clone"
                      let who = PraxisCli.agent "example/agent-a" "example" "agent-a" "session-a"

                      let start id environment =
                          PraxisCli.runWith clone (Some who) environment [ "work"; "start"; "--id"; id; "--type"; "feature"; "--occurred-at"; PraxisCli.now () ] |> PraxisCli.ok |> ignore

                      let newest (directory: string) (pattern: string) =
                          Directory.GetFiles(directory, pattern, SearchOption.AllDirectories) |> Array.map (fun path -> path, File.GetLastWriteTimeUtc path) |> Array.maxBy snd |> fst

                      start "FEAT-1" [ "CLAUDE_CODE_SESSION_ID", "sess-1"; "PRAXIS_AGENT_INSTANCE_ID", "agent-7" ]
                      let envelope = JsonNode.Parse(File.ReadAllText(newest (Path.Combine(clone, ".ros", "executions")) "envelope.json"))
                      Assert.equal "sess-1" (textAt "provider.sessionId" envelope)
                      Assert.equal "agent-7" (textAt "provider.agentInstanceId" envelope)
                      let recordPath = newest (Path.Combine(clone, ".ros", "telemetry", "executions")) "*.json"
                      Assert.equal "agent-7" (textAt "provider.agentInstanceId" (JsonNode.Parse(File.ReadAllText recordPath)))
                      Assert.equal (Some "agent-7") (ReliabilityInputs.parseExecution (File.ReadAllText recordPath) |> Option.get).AgentInstanceId

                      start "FEAT-2" []
                      let plain = JsonNode.Parse(File.ReadAllText(newest (Path.Combine(clone, ".ros", "executions")) "envelope.json"))
                      Assert.isTrue (isNull (at "provider" plain)) "no provider member when the host sets neither"
                      Assert.isTrue ((textAt "workItem" plain).EndsWith "FEAT-2") "the newest envelope is FEAT-2's"
                  finally
                      GitFixture.cleanup parent } ]
