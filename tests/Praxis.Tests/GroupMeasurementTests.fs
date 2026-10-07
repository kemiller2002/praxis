namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Domain.Planning
open Praxis.Domain.Telemetry
open Praxis.Domain.Work

/// PRX-GRP-150..158 (PRAXIS-PLAN-10; PRX-GRP-190 cases 40-43): context and
/// cost measurement for grouped work.
[<RequireQualifiedAccess>]
module GroupMeasurementTests =
    open PraxisCli

    let private t name run = { Name = $"group measurement: {name}"; Run = run }
    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"
    let private cli clone arguments = run clone (Some agentA) arguments
    let private groupId = "GROUP-FIXTURE-001"

    let private entry kind (at: string) (tools: TranscriptToolUse list) (usage: TranscriptUsage option) : TranscriptEntry =
        { Kind = kind
          Subtype = None
          Timestamp = Some(DateTimeOffset.Parse at)
          Cwd = Some "/repo"
          SessionId = Some "s-1"
          RuntimeVersion = None
          RequestKey = Some at
          Model = Some "model-x"
          Usage = usage
          ToolUses = tools
          ToolErrors = 0
          IsCompactSummary = false
          CostUsd = None }

    let private usage input cacheRead cacheWrite : TranscriptUsage option =
        Some { Input = input; Output = 10L; CacheRead = cacheRead; CacheCreation = cacheWrite }

    let private read path = { Name = "Read"; FilePath = Some $"/repo/{path}"; Command = None }
    let private edit path = { Name = "Edit"; FilePath = Some $"/repo/{path}"; Command = None }

    let private executed (id: string) (cost: decimal option) (activeMs: int64 option) : HistoricalExecution =
        { ExecutionId = id
          WorkItemId = $"WI-{id}"
          Status = Praxis.Domain.Planning.ExecutionStatus.Finalized
          StartedAt = "2026-10-01T00:00:00.000Z"
          FinalizedAt = Some "2026-10-01T01:00:00.000Z"
          Classes = [ "development" ]
          WallMs = Some 3_600_000L
          BlockedMs = None
          Provider = "p"
          Runtime = "r"
          Model = None
          Costs = cost |> Option.map (fun amount -> { MetricId = "cost.execution_total"; Amount = amount; Currency = Some "USD"; Kind = CostEvidenceKind.Observed }) |> Option.toList
          TokenMetrics = 0
          Session = { SessionEvidence.none with ActiveMs = activeMs } }

    let private withGroupExecutionMode (extra: string list) (test: string -> string -> string -> unit) =
        let parent = GitFixture.temporaryDirectory "group-measurement"

        try
            let _, clone = installedRepository parent "clone"

            for id in [ "ITEM-1"; "ITEM-2" ] do
                cli clone [ "add"; $"Work {id}"; "--id"; id ] |> ok |> ignore
                cli clone [ "work"; "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; now () ] |> ok |> ignore

            cli clone ([ "work"; "group"; "create"; "--group"; groupId; "--member"; "ITEM-1"; "--member"; "ITEM-2"; "--occurred-at"; now () ] @ extra) |> ok |> ignore
            pushAll clone "declare the group" |> ignore
            let begun = cli clone [ "plan"; "execute-group"; groupId; "--occurred-at"; now (); "--json" ] |> ok
            test clone (text begun.Json["groupExecution"].["id"]) (text begun.Json["memberExecution"])
        finally
            GitFixture.cleanup parent

    let private withGroupExecution test = withGroupExecutionMode [] test

    let tests =
        [ t "every adapter declares a capability for every context metric, and the registry defines each" (fun () ->
              let rec repositoryRoot (directory: DirectoryInfo) =
                  if File.Exists(Path.Combine(directory.FullName, "release.json")) then directory.FullName else repositoryRoot directory.Parent

              let registry = File.ReadAllText(Path.Combine(repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory())), "telemetry", "metrics.json"))
              let document = JsonNode.Parse registry
              let defined = document["metrics"].AsArray() |> Seq.map (fun metric -> text metric["id"]) |> Set.ofSeq

              for metricId in ContextMetrics.ids do
                  Assert.isTrue (defined.Contains metricId) $"{metricId} is not in telemetry/metrics.json"

                  for adapter in TelemetryAdapters.all do
                      let code = ContextMetrics.code (ContextMetrics.declaration adapter metricId)
                      Assert.isTrue (code = "observes" || code = "unsupported") $"{adapter} {metricId}"

              Assert.equal ContextMetricSupport.Unsupported (ContextMetrics.declaration "anthropic-claude-session" ContextMetrics.windowTokens)
              Assert.equal ContextMetricSupport.Observes (ContextMetrics.declaration "anthropic-claude-session" ContextMetrics.peakTokens))

          t "session metrics: first productive change follows the configured paths, peak tokens and distinct re-reads are measured, unseen values stay unavailable" (fun () ->
              let entries =
                  [ entry "assistant" "2026-10-07T10:00:00.000Z" [ read "AGENTS.md" ] (usage 100L 50L 5L)
                    entry "assistant" "2026-10-07T10:01:00.000Z" [ read "AGENTS.md"; read "docs/a.md"; read "docs/a.md"; read "docs/a.md" ] (usage 300L 900L 20L)
                    entry "assistant" "2026-10-07T10:03:00.000Z" [ edit "docs/a.md" ] (usage 200L 100L 0L)
                    entry "assistant" "2026-10-07T10:07:00.000Z" [ edit "src/x.fs" ] None ]

              let summary = SessionTranscript.summarizeWith (fun path -> path.StartsWith "docs/" || path.StartsWith "src/") entries
              Assert.equal (Some 180_000L) summary.MsToFirstProductiveChange
              Assert.equal (Some 420_000L) summary.MsToFirstCodeChange
              Assert.equal (Some 1220L) summary.PeakContextTokens
              let measured = SessionTranscript.measurements summary |> List.map (fun measurement -> measurement.MetricId, measurement.Value) |> Map.ofList
              Assert.equal (Some 2m) measured["context.repeated_file_reads_distinct"]
              Assert.equal (Some 3m) measured["context.repeated_file_reads"]
              Assert.equal (Some 180_000m) measured["time.first_productive_change_ms"]
              let empty = SessionTranscript.measurements (SessionTranscript.summarize [])
              Assert.isTrue (empty |> List.forall (fun measurement -> measurement.Value.IsNone)) "an empty transcript recorded a value"
              let noUsage = SessionTranscript.summarize [ entry "assistant" "2026-10-07T10:00:00.000Z" [] None ]
              Assert.equal None noUsage.PeakContextTokens)

          t "shared cost is apportioned deterministically; allocations sum exactly to the total and are labelled allocated" (fun () ->
              let direct id = Map.ofList [ "A", Some 1m; "B", None; "C", Some 2m ] |> Map.find id
              let split = GroupCost.apportion (Some 10m) (Some "USD") [ "A"; "B"; "C" ] direct
              let allocated = split.Allocated |> List.sumBy (fun line -> line.Amount.Value)
              Assert.equal 10m (allocated + split.GroupShared.Amount.Value)
              Assert.isTrue (split.Allocated |> List.forall (fun line -> line.Quality = "allocated")) "an allocation was not labelled allocated"
              Assert.equal (Some 2.333333m) (split.Allocated |> List.find (fun line -> line.Member = Some "B")).Amount
              Assert.equal (Some 3.333333m) (split.Allocated |> List.find (fun line -> line.Member = Some "A")).Amount
              Assert.equal (Some 0.000001m) split.GroupShared.Amount
              Assert.equal split (GroupCost.apportion (Some 10m) (Some "USD") [ "A"; "B"; "C" ] direct)
              let unknown = GroupCost.apportion None None [ "A"; "B" ] (fun _ -> Some 1m)
              Assert.isTrue (unknown.Allocated |> List.forall (fun line -> line.Amount.IsNone)) "an unknown total produced an allocation"
              let inconsistent = GroupCost.apportion (Some 1m) (Some "USD") [ "A"; "C" ] direct
              Assert.isTrue (inconsistent.Statement.Contains "exceeds") inconsistent.Statement)

          t "the planner prices grouping only from at least three measured samples, otherwise unknown, never across currencies" (fun () ->
              let independent = [ executed "E1" (Some 4m) (Some 600_000L); executed "E2" (Some 6m) (Some 900_000L) ]
              let thin = GroupPricing.price independent []
              Assert.equal (None, None, 2) (thin.Independent.CostPerMember, thin.Grouped.CostPerMember, thin.Independent.CostSamples)
              Assert.isTrue (thin.Statement.Contains "unknown") thin.Statement
              let three = GroupPricing.price (independent @ [ executed "E3" (Some 5m) None ]) []
              Assert.equal (Some 5m, Some(4m, 6m), Some "USD") (three.Independent.CostPerMember, three.Independent.CostRange, three.Independent.Currency)
              Assert.equal "3 of 3 independent executions carry cost.execution_total" three.Independent.Coverage
              let sample id total currency = { GroupExecutionId = id; Mode = "grouped"; Members = 2; ExecutionIds = [ $"{id}-a"; $"{id}-b" ]; CostTotal = total; Currency = currency; ActiveMs = Some 1_200_000L }
              let grouped = GroupPricing.price [] [ sample "G1" (Some 6m) (Some "USD"); sample "G2" (Some 8m) (Some "USD"); sample "G3" (Some 10m) (Some "USD") ]
              Assert.equal (Some 4m, Some 600_000L) (grouped.Grouped.CostPerMember, grouped.Grouped.ActiveMsPerMember)
              let mixed = GroupPricing.price [] [ sample "G1" (Some 6m) (Some "USD"); sample "G2" (Some 8m) (Some "EUR"); sample "G3" (Some 10m) (Some "USD") ]
              Assert.equal None mixed.Grouped.CostPerMember
              Assert.isTrue (mixed.Grouped.Coverage.Contains "currencies differ") mixed.Grouped.Coverage)

          t "cli a shared session is ingested once into the group execution and never counted twice" (fun () ->
              withGroupExecution (fun clone gex execution ->
                  let input = Path.Combine(clone, "..", "session.json")
                  File.WriteAllText(input, """{"snapshotId":"shared-session-1","prompt":"NEEDLE-PROMPT","message":"NEEDLE-MESSAGE","toolInput":"NEEDLE-TOOL-INPUT","command":"NEEDLE-COMMAND","metrics":[{"id":"cost.execution_total","value":3.5,"unit":"currency","currency":"USD","quality":"observed"}]}""")
                  let first = cli clone [ "telemetry"; "ingest"; gex; "--input"; input; "--json" ] |> ok
                  Assert.equal true (first.Json["changed"].GetValue<bool>())
                  let again = cli clone [ "telemetry"; "ingest"; gex; "--input"; input; "--json" ] |> ok
                  Assert.equal false (again.Json["changed"].GetValue<bool>())
                  let member' = cli clone [ "telemetry"; "ingest"; execution; "--input"; input ]
                  Assert.equal 1 member'.ExitCode
                  Assert.isTrue (member'.Error.Contains "counted once") member'.Error
                  File.WriteAllText(input, """{"snapshotId":"member-session-1","metrics":[{"id":"cost.execution_total","value":1,"unit":"currency","currency":"USD","quality":"observed"}]}""")
                  cli clone [ "telemetry"; "ingest"; execution; "--input"; input ] |> ok |> ignore
                  let refused = cli clone [ "telemetry"; "ingest"; gex; "--input"; input ]
                  Assert.equal 1 refused.ExitCode
                  let cost = cli clone [ "work"; "group"; "cost"; groupId; "--json" ] |> ok
                  let split = cost.Json["groupExecutions"].[0]
                  Assert.equal 3.5m (split["total"].GetValue<decimal>())
                  Assert.equal "allocated" (text split["allocated"].[0].["quality"])
                  let stored = File.ReadAllText(Path.Combine(clone, ".ros", "work", "groups.json"))
                  Assert.isTrue (not (stored.Contains "\"raw\"")) "group-execution telemetry stored a raw payload"
                  Assert.isTrue (not (stored.Contains "NEEDLE-")) "group-execution telemetry stored prompt, message, tool or command text"))

          t "cli completing a group-execution member without cost or an explained capability state warns and still completes" (fun () ->
              withGroupExecutionMode [ "--execution-mode"; "independent"; "--reason"; "measured separately" ] (fun clone _ execution ->
                  GitFixture.write clone "src/one.txt" "one\n"
                  pushAll clone "ITEM-1: change" |> ignore
                  cli clone [ "work"; "checkpoint"; "--id"; "ITEM-1"; "--occurred-at"; now (); "--summary"; "done"; "--next-action"; "complete" ] |> ok |> ignore
                  let completed = cli clone [ "work"; "complete"; "--id"; "ITEM-1"; "--occurred-at"; now (); "--evidence"; "implementation=src/one.txt"; "--evidence"; "tests=src/one.txt" ]
                  Assert.isTrue (completed.Error.Contains "records no cost.execution_total") completed.Error
                  Assert.isTrue (completed.Error.Contains execution && completed.ExitCode = 0) completed.Error))

          t "cli plan groups, explain-group and compare --groups report pricing with its basis and sample counts" (fun () ->
              withGroupExecution (fun clone _ _ ->
                  let pricingOf (group: JsonNode) =
                      let pricing = group["pricing"]
                      Assert.isTrue ((text pricing["statement"]).Contains "unknown") (pricing.ToJsonString())

                      for arm in [ "grouped"; "independent" ] do
                          Assert.isTrue (not (isNull pricing[arm].["samples"]) && not (isNull pricing[arm].["costSamples"]) && text pricing[arm].["coverage"] <> "") (pricing.ToJsonString())

                      pricing.ToJsonString()

                  let listed = (run clone None [ "plan"; "groups"; "--json" ] |> ok).Json["groups"].AsArray() |> Seq.find (fun group -> text group["id"] = groupId)
                  let explained = (run clone None [ "plan"; "explain-group"; groupId; "--json" ] |> ok).Json["group"]
                  Assert.equal (pricingOf listed) (pricingOf explained)
                  let compared = run clone None [ "plan"; "compare"; "--groups"; "--json" ]
                  Assert.isTrue (compared.ExitCode = 0 || compared.ExitCode = 3) compared.Error
                  let saving = text (compared.Json["tradeoffs"].AsArray() |> Seq.find (fun row -> text row["group"] = groupId)).["contextSaving"]
                  Assert.isTrue (saving.Contains "priced from measured samples only" && saving.Contains "fewer than 3 priced samples" && saving.Contains "0 of 0 grouped executions") saving))

          t "cli the prediction is frozen when a group execution starts and compared with the outcome when it ends" (fun () ->
              withGroupExecution (fun clone gex _ ->
                  let stored () = JsonNode.Parse(File.ReadAllText(Path.Combine(clone, ".ros", "work", "groups.json")))
                  let record () =
                      let document = stored ()
                      document["groups"].[0].["executions"].[0]
                  let started = record ()
                  let prediction = started["prediction"]
                  Assert.equal "ITEM-1" (text prediction["members"].[0])
                  Assert.isTrue (prediction["cost"].["lower"] = null) "a cost was predicted without samples"
                  cli clone [ "plan"; "execute-group"; groupId; "--occurred-at"; now () ] |> ok |> ignore
                  let finished = cli clone [ "plan"; "execute-group"; groupId; "--occurred-at"; now (); "--json" ] |> ok
                  Assert.equal ("finished", gex) (text finished.Json["status"], text finished.Json["groupExecution"].["id"])
                  let ended = record ()
                  let outcome = ended["outcome"]
                  Assert.equal 2 (outcome["membersBegun"].GetValue<int>())
                  Assert.isTrue ((text outcome["statement"]).Contains "PRX-GRP-158") (outcome.ToJsonString())
                  let later = record ()
                  Assert.equal (prediction.ToJsonString()) (later["prediction"].ToJsonString()))) ]
