namespace Ros.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Ros.Cli
open Ros.Contracts.Execution
open Ros.Domain.Execution
open Ros.Domain.Telemetry
open Ros.Infrastructure.Execution
open Ros.Infrastructure.Work

/// The control-plane read API for executions, receipts, evidence,
/// checkpoints and telemetry (PRX-CTL-005, PRX-CTL-011, PRX-CTL-012).
[<RequireQualifiedAccess>]
module ControlPlaneReadTests =
    let private at = DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)
    let private expected = ExpectedReceipt.CommandSucceeded "make test"

    let private comparison outcome : ReceiptResult =
        { Expected = expected
          Outcome = outcome
          Reason = (if outcome = ReceiptOutcome.Match then None else Some "exit code 1")
          Evidence = []
          Constituents = [] }

    let private observed: ObservedReceipt =
        { Source = ObservationSource.Host "praxis"
          Facts = [ ObservedFact.CommandExited("make test", 0) ]
          Narrative = None }

    let private declared = StepEntry.Declared("s1", 1, "Test", [], expected, None, at)
    let private started attempt = StepEntry.Started("s1", attempt, at.AddMinutes(float attempt))
    let private observedAs attempt outcome = StepEntry.Observed("s1", attempt, observed, comparison outcome, at.AddMinutes(float attempt + 0.5))
    let private step entries = StepLedger.reconstruct entries |> Assert.single

    let private actor provider model runtime : ExecutionActor =
        { Id = "agent:example/one"
          Kind = "agent"
          Provider = provider
          Model = model
          Runtime = runtime }

    let private envelope (executionActor: ExecutionActor) =
        match ExecutionEnvelope.create "EXE-1" "owner/repo:WI-1" executionActor (RoleAuthority.defaultFor ExecutionRole.Implementation) "abc123" { Scopes = []; Projections = []; EvaluatorReferences = [] } None at with
        | Ok value -> value
        | Error message -> failwith message

    let private definition id unit : MetricDefinition =
        { Id = id
          Unit = unit
          Aggregation = "sum"
          Collection = "optional" }

    let private group metric unit currency total reporting unavailable : UsageGroup =
        { Key = "WI-1"
          MetricId = metric
          Unit = unit
          Currency = currency
          Total = total
          Measurements = List.length reporting
          ReportingExecutions = reporting
          UnavailableExecutions = unavailable
          Qualities = [] }

    let private domainTests =
        [ { Name = "control plane: a step's receipt is not-observed, indeterminate, or the recorded comparison of its current attempt"
            Run =
              fun () ->
                  let state entries = (StepReceipt.current (step entries)).State
                  Assert.equal ReceiptState.NotObserved (state [ declared ])
                  Assert.equal ReceiptState.Indeterminate (state [ declared; started 1 ])
                  Assert.equal (ReceiptState.Compared ReceiptOutcome.Match) (state [ declared; started 1; observedAs 1 ReceiptOutcome.Match ])
                  Assert.equal (ReceiptState.Compared ReceiptOutcome.Mismatch) (state [ declared; started 1; observedAs 1 ReceiptOutcome.Mismatch ])
                  Assert.equal (ReceiptState.Compared ReceiptOutcome.Indeterminate) (state [ declared; started 1; observedAs 1 ReceiptOutcome.Indeterminate ])

                  let retried = StepReceipt.current (step [ declared; started 1; observedAs 1 ReceiptOutcome.Mismatch; started 2 ])
                  Assert.equal 2 retried.Attempt
                  Assert.equal ReceiptState.Indeterminate retried.State
                  Assert.isTrue retried.Observation.IsNone "the earlier attempt's receipt is not the current attempt's"

                  let passed = StepReceipt.current (step [ declared; started 1; observedAs 1 ReceiptOutcome.Mismatch; started 2; observedAs 2 ReceiptOutcome.Match ])
                  Assert.equal (ReceiptState.Compared ReceiptOutcome.Match) passed.State
                  Assert.equal expected passed.Expected
                  Assert.equal [ "not-observed"; "indeterminate"; "match"; "mismatch" ] ([ ReceiptState.NotObserved; ReceiptState.Indeterminate; ReceiptState.Compared ReceiptOutcome.Match; ReceiptState.Compared ReceiptOutcome.Mismatch ] |> List.map ReceiptState.toWire) }

          { Name = "control plane: the latest reconciliation of a step is reported with its attempt"
            Run =
              fun () ->
                  let view = step [ declared; started 1; StepEntry.Reconciled("s1", 1, Reconciliation.DidNotOccur "no artifact", at.AddHours 1.0) ]
                  let reconciliation = StepReceipt.reconciliation view |> Option.get
                  Assert.equal 1 reconciliation.Attempt
                  Assert.equal (Reconciliation.DidNotOccur "no artifact") reconciliation.Finding
                  Assert.equal None (StepReceipt.reconciliation (step [ declared ])) }

          { Name = "control plane: usage and cost coverage is recorded, partial or unknown, never zero-filled"
            Run =
              fun () ->
                  let definitions = [ definition "tokens.input" "tokens"; definition "tokens.output" "tokens"; definition "cost.execution_total" "currency"; definition "git.lines_added" "lines" ]
                  let executions = [ "EXE-B"; "EXE-A" ]

                  let groups =
                      [ group "tokens.input" "tokens" None (Some 1200.0) [ "EXE-A"; "EXE-B" ] []
                        group "tokens.output" "tokens" None (Some 40.0) [ "EXE-A" ] [ "EXE-B" ]
                        group "cost.execution_total" "currency" (Some "USD") None [] [ "EXE-A"; "EXE-B" ] ]

                  let usage = UsageCoverage.coverage (UsageCoverage.metricsWithUnit UsageCoverage.usageUnit definitions) executions groups
                  Assert.equal [ "tokens.input"; "tokens.output" ] (usage |> List.map _.MetricId)
                  Assert.equal [ CoverageAvailability.Recorded; CoverageAvailability.Partial ] (usage |> List.map _.Availability)
                  Assert.equal [ "EXE-B" ] usage[1].UnavailableExecutions
                  Assert.equal [ 1200.0 ] (usage[0].Totals |> List.map _.Total)

                  let cost = UsageCoverage.coverage (UsageCoverage.metricsWithUnit UsageCoverage.costUnit definitions) executions groups |> Assert.single
                  Assert.equal CoverageAvailability.Unknown cost.Availability
                  Assert.empty cost.Totals
                  Assert.equal [ "EXE-A"; "EXE-B" ] cost.UnavailableExecutions

                  let none = UsageCoverage.coverage (UsageCoverage.metricsWithUnit UsageCoverage.usageUnit definitions) [] [] |> List.map _.Availability
                  Assert.equal [ CoverageAvailability.Unknown; CoverageAvailability.Unknown ] none }

          { Name = "control plane: cost recorded in two currencies keeps one total per currency"
            Run =
              fun () ->
                  let groups =
                      [ group "cost.execution_total" "currency" (Some "EUR") (Some 1.5) [ "EXE-A" ] [ "EXE-B" ]
                        group "cost.execution_total" "currency" (Some "USD") (Some 2.0) [ "EXE-B" ] [ "EXE-A" ] ]

                  let cost = UsageCoverage.coverage [ definition "cost.execution_total" "currency" ] [ "EXE-A"; "EXE-B" ] groups |> Assert.single
                  Assert.equal CoverageAvailability.Recorded cost.Availability
                  Assert.equal [ Some "EUR", 1.5; Some "USD", 2.0 ] (cost.Totals |> List.map (fun total -> total.Currency, total.Total)) } ]

    let private contractTests =
        [ { Name = "execution contract: provider, model and runtime are execution-host information, never part of the actor or the work-state owner"
            Run =
              fun () ->
                  let node = ExecutionStateJson.summary (envelope (actor (Some "anthropic") None (Some "claude-code")))
                  let actorNode = node["actor"].AsObject()
                  Assert.equal [ "id"; "kind" ] (actorNode |> Seq.map _.Key |> Seq.toList)
                  Assert.equal "anthropic" (node["executionHost"].["provider"].GetValue<string>())
                  Assert.equal "unknown" (node["executionHost"].["model"].GetValue<string>())
                  Assert.equal "claude-code" (node["executionHost"].["runtime"].GetValue<string>())
                  Assert.equal "repository" (node["workStateOwner"].["kind"].GetValue<string>())
                  Assert.equal "owner/repo:WI-1" (node["workStateOwner"].["workItem"].GetValue<string>())
                  Assert.equal "implementation" (node["role"].GetValue<string>())
                  Assert.equal "active" (node["status"].GetValue<string>()) }

          { Name = "execution contract: a step carries its expected and observed receipts in the ordo.execution/1 shapes"
            Run =
              fun () ->
                  let view = step [ declared; started 1; observedAs 1 ReceiptOutcome.Mismatch ]
                  let node = ExecutionStateJson.step view
                  Assert.equal "mismatch" (node["status"].GetValue<string>())
                  let receipt = node["receipt"]
                  Assert.equal "mismatch" (receipt["state"].GetValue<string>())
                  Assert.equal ((ExecutionJson.expected expected).ToJsonString()) (receipt["expected"].ToJsonString())
                  Assert.equal ((ExecutionJson.observed observed).ToJsonString()) (receipt["observed"].ToJsonString())
                  Assert.equal ((ExecutionJson.result (comparison ReceiptOutcome.Mismatch)).ToJsonString()) (receipt["comparison"].ToJsonString())
                  Assert.equal "exit code 1" (receipt["reason"].GetValue<string>())

                  let pending = ExecutionStateJson.step (step [ declared ])
                  Assert.equal "not-observed" (pending["receipt"].["state"].GetValue<string>())
                  Assert.isTrue (isNull pending["receipt"].["observed"]) "nothing observed is null, not an empty receipt" }

          { Name = "control plane: routes and pure responses for executions, evidence and telemetry"
            Run =
              fun () ->
                  let request path query : HttpRequestData =
                      { Method = "GET"; Segments = HttpMessages.pathSegments path; Query = query; ContentType = None; Body = [||] }

                  Assert.equal (WebRoute.Control(ControlQuery.Executions None)) (WebInterface.route (request "/api/v1/executions" []))
                  Assert.equal (WebRoute.Control(ControlQuery.Executions(Some "WI-1"))) (WebInterface.route (request "/api/v1/executions" [ "workItem", "WI-1" ]))
                  Assert.equal (WebRoute.Control(ControlQuery.Execution "EXE-1")) (WebInterface.route (request "/api/v1/executions/EXE-1" []))
                  Assert.equal (WebRoute.Control(ControlQuery.Evidence("WI-1", false))) (WebInterface.route (request "/api/v1/work/WI-1/evidence" []))
                  Assert.equal (WebRoute.Control(ControlQuery.Evidence("WI-1", true))) (WebInterface.route (request "/api/v1/work/WI-1/evidence" [ "offline", "true" ]))
                  Assert.equal (WebRoute.Control(ControlQuery.Telemetry "WI-1")) (WebInterface.route (request "/api/v1/work/WI-1/telemetry" []))

                  let missing, body = WebInterface.executionResponse (Error(ExecutionReadFailure.NotFound "EXE-9"))
                  Assert.equal 404 missing
                  Assert.equal "execution-not-found" (body["code"].GetValue<string>())
                  let broken, _ = WebInterface.executionResponse (Error(ExecutionReadFailure.Unreadable("EXE-9", "bad json")))
                  Assert.equal 500 broken

                  let readable = envelope (actor None None None)
                  let status, list = WebInterface.executionsResponse (Some "owner/repo:WI-1") [ "EXE-1", Ok readable; "EXE-2", Error "bad json" ]
                  Assert.equal 200 status
                  Assert.equal 1 (list["items"].AsArray().Count)
                  Assert.equal "EXE-2" (list["unreadable"].[0].["executionId"].GetValue<string>())
                  let _, other = WebInterface.executionsResponse (Some "owner/repo:WI-OTHER") [ "EXE-1", Ok readable ]
                  Assert.equal 0 (other["items"].AsArray().Count)

                  let never () = failwith "an unknown item must not be read"
                  let unknown, notFound = WebInterface.evidenceResponse "WI-404" (Some false) never never
                  Assert.equal 404 unknown
                  Assert.equal "work-item-not-found" (notFound["code"].GetValue<string>())
                  let unknownTelemetry, _ = WebInterface.telemetryResponse "WI-404" (Some false) never never never
                  Assert.equal 404 unknownTelemetry

                  let backlogOnly, document =
                      WebInterface.evidenceResponse "WI-1" (Some true) never (fun () -> Error(CheckpointShowFailure.NotInContext "WI-1"))

                  Assert.equal 200 backlogOnly
                  Assert.equal "unavailable" (document["evidence"].["availability"].GetValue<string>())
                  Assert.equal "unavailable" (document["checkpoints"].["availability"].GetValue<string>())
                  Assert.isTrue (isNull document["evidence"].["items"]) "unavailable is not an empty list" } ]

    // ---- the real server over a repository with a remote ----

    /// Every file under `root` with its content hash, `.git` included.
    let private snapshot (root: string) =
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        |> Seq.map (fun path -> Path.GetRelativePath(root, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)))
        |> Map.ofSeq

    let private who = PraxisCli.agent "example/agent-a" "example" "agent-a" "session-a"
    let private cli clone arguments = PraxisCli.run clone (Some who) arguments
    let private cliOk clone arguments = cli clone arguments |> PraxisCli.ok

    type private Fixture =
        { Clone: string
          ExecutionId: string
          WorkItem: string
          BacklogItem: string }

    /// A repository with a bare remote holding: a completed work item with
    /// evidence and a pushed durable checkpoint, recorded token usage and no
    /// cost, an execution with matched, mismatched, in-flight and pending
    /// steps, and a backlog-only item.
    let private withFixture (test: Fixture -> unit) =
        let parent = GitFixture.temporaryDirectory "control-plane"

        try
            let _, clone = PraxisCli.installedRepository parent "clone"
            let item = "FEAT-CTL"
            cliOk clone [ "work"; "start"; "--id"; item; "--type"; "feature"; "--occurred-at"; PraxisCli.now () ] |> ignore
            cliOk clone [ "telemetry"; "record"; item; "--metric"; "tokens.input"; "--value"; "1200"; "--unit"; "tokens"; "--source-type"; "runtime-api"; "--quiet" ] |> ignore
            Directory.CreateDirectory(Path.Combine(clone, "src")) |> ignore
            Directory.CreateDirectory(Path.Combine(clone, "tests")) |> ignore
            File.WriteAllText(Path.Combine(clone, "src", "feature.txt"), "feature\n")
            File.WriteAllText(Path.Combine(clone, "tests", "feature-test.txt"), "test\n")
            PraxisCli.pushAll clone "FEAT-CTL: implement" |> ignore

            cliOk clone [ "work"; "checkpoint"; "--id"; item; "--occurred-at"; PraxisCli.now (); "--summary"; "implemented"; "--next-action"; "complete" ] |> ignore

            cliOk
                clone
                [ "work"; "complete"; "--id"; item; "--occurred-at"; PraxisCli.now (); "--evidence"; "implementation=src/feature.txt"; "--evidence"; "tests=tests/feature-test.txt" ]
            |> ignore

            PraxisCli.pushAll clone "FEAT-CTL: record completion" |> ignore

            let started = cliOk clone [ "execution"; "start"; "--work-item"; item; "--role"; "implementation"; "--json" ]
            let executionId = started.Json["executionId"].GetValue<string>()
            let declare stepId sequence command = cliOk clone [ "execution"; "step"; "declare"; executionId; "--step"; stepId; "--sequence"; string sequence; "--expect-command"; command ] |> ignore
            declare "passes" 1 "true"
            declare "fails" 2 "false"
            declare "in-flight" 3 "true"
            declare "pending" 4 "true"
            cliOk clone [ "execution"; "step"; "run"; executionId; "--step"; "passes" ] |> ignore
            cli clone [ "execution"; "step"; "run"; executionId; "--step"; "fails" ] |> ignore
            cliOk clone [ "execution"; "step"; "start"; executionId; "--step"; "in-flight" ] |> ignore

            cliOk clone [ "add"; "Later work" ] |> ignore
            let backlogItem = (cliOk clone [ "work"; "list"; "--status"; "captured" ]).Output |> JsonNode.Parse |> fun rows -> rows[0].["id"].GetValue<string>()

            test
                { Clone = clone
                  ExecutionId = executionId
                  WorkItem = item
                  BacklogItem = backlogItem }
        finally
            GitFixture.cleanup parent

    let private withServer (fixture: Fixture) (test: ServedProcess -> unit) =
        use server = new ServedProcess(fixture.Clone, [ "web"; "serve" ])
        test server

    let private httpTests =
        [ { Name = "web serve: executions are listed (all and per work item) and shown with role, status, actor, host and step receipts as execution show reads them"
            Run =
              fun () ->
                  withFixture (fun fixture ->
                      let shown = (cliOk fixture.Clone [ "execution"; "show"; fixture.ExecutionId; "--json" ]).Json

                      withServer fixture (fun server ->
                          let all = Http.json (server.Get "/api/v1/executions")
                          Assert.equal "praxis.execution-state" (Http.text all "contract")
                          Assert.equal "execution-list" (Http.text all "kind")
                          Assert.equal [ fixture.ExecutionId ] (all["items"].AsArray() |> Seq.map (fun item -> Http.text item "executionId") |> Seq.toList)

                          let mine = Http.json (server.Get $"/api/v1/executions?workItem={fixture.WorkItem}")
                          Assert.equal 1 (mine["items"].AsArray().Count)
                          Assert.equal (shown["workItem"].GetValue<string>()) (Http.text mine "workItem")
                          let other = Http.json (server.Get "/api/v1/executions?workItem=FEAT-OTHER")
                          Assert.equal 0 (other["items"].AsArray().Count)

                          let response = server.Get $"/api/v1/executions/{fixture.ExecutionId}"
                          Assert.equal 200 (Http.status response)
                          let execution = (Http.json response).["execution"]
                          Assert.equal (shown["role"].GetValue<string>()) (Http.text execution "role")
                          Assert.equal (shown["state"].GetValue<string>()) (Http.text execution "status")
                          Assert.equal (shown["actor"].["id"].GetValue<string>()) (Http.text execution["actor"] "id")
                          Assert.equal (shown["actor"].["provider"].GetValue<string>()) (Http.text execution["executionHost"] "provider")
                          Assert.equal (shown["actor"].["runtime"].GetValue<string>()) (Http.text execution["executionHost"] "runtime")
                          Assert.isTrue (isNull execution["actor"].["provider"]) "the provider is not presented as part of the actor"
                          Assert.equal "repository" (Http.text execution["workStateOwner"] "kind")

                          let steps = execution["steps"].AsArray() |> Seq.toList
                          let cliSteps = shown["steps"].AsArray() |> Seq.toList
                          Assert.equal (cliSteps |> List.map (fun s -> Http.text s "stepId", Http.text s "status")) (steps |> List.map (fun s -> Http.text s "stepId", Http.text s "status"))
                          Assert.equal (cliSteps |> List.map (fun s -> s["expected"].ToJsonString())) (steps |> List.map (fun s -> s["receipt"].["expected"].ToJsonString()))
                          Assert.equal [ "match"; "mismatch"; "indeterminate"; "not-observed" ] (steps |> List.map (fun s -> Http.text s["receipt"] "state"))
                          Assert.equal "command-exited" (Http.text (steps[1].["receipt"].["observed"].["facts"].[0]) "kind")
                          Assert.equal "mismatch" (Http.text steps[1].["receipt"].["comparison"] "result")

                          let missing = server.Get "/api/v1/executions/EXE-NOPE"
                          Assert.equal 404 (Http.status missing)
                          Assert.equal "execution-not-found" (Http.text (Http.json missing) "code"))) }

          { Name = "web serve: a work item's evidence and durable checkpoints are those of work context and work checkpoint show"
            Run =
              fun () ->
                  withFixture (fun fixture ->
                      let context = (cliOk fixture.Clone [ "work"; "context"; fixture.WorkItem ]).Json
                      let checkpoints = (cliOk fixture.Clone [ "work"; "checkpoint"; "show"; fixture.WorkItem; "--json" ]).Json

                      withServer fixture (fun server ->
                          let document = Http.json (server.Get $"/api/v1/work/{fixture.WorkItem}/evidence")
                          Assert.equal "work-evidence" (Http.text document "kind")
                          let evidence = document["evidence"]
                          Assert.equal "available" (Http.text evidence "availability")
                          Assert.equal (context["workItems"].[0].["evidence"].ToJsonString()) (evidence["items"].ToJsonString())
                          Assert.equal [ "implementation"; "tests" ] (Http.strings evidence "requiredForCompletion")

                          let recorded = document["checkpoints"]
                          Assert.equal "available" (Http.text recorded "availability")
                          Assert.isTrue (recorded["remoteObserved"].GetValue<bool>()) "the remote is observed by default, as the CLI does"
                          Assert.equal (checkpoints["history"].ToJsonString()) (recorded["history"].ToJsonString())
                          Assert.equal (checkpoints["continuity"].ToJsonString()) (recorded["continuity"].ToJsonString())
                          Assert.equal 1 (recorded["history"].AsArray().Count)

                          let offline = Http.json (server.Get $"/api/v1/work/{fixture.WorkItem}/evidence?offline=true")
                          Assert.isTrue (not (offline["checkpoints"].["remoteObserved"].GetValue<bool>())) "offline does not observe the remote"

                          let backlog = Http.json (server.Get $"/api/v1/work/{fixture.BacklogItem}/evidence")
                          Assert.equal "unavailable" (Http.text backlog["evidence"] "availability")
                          Assert.equal "unavailable" (Http.text backlog["checkpoints"] "availability")
                          Assert.equal 404 (Http.status (server.Get "/api/v1/work/FEAT-NOPE/evidence")))) }

          { Name = "web serve: telemetry reports recorded usage, unknown cost, and the telemetry usage groups"
            Run =
              fun () ->
                  withFixture (fun fixture ->
                      let usage = (cliOk fixture.Clone [ "telemetry"; "usage"; fixture.WorkItem ]).Json

                      withServer fixture (fun server ->
                          let document = Http.json (server.Get $"/api/v1/work/{fixture.WorkItem}/telemetry")
                          Assert.equal "work-telemetry" (Http.text document "kind")
                          Assert.equal 1 (document["executions"].AsArray().Count)
                          let metric name (list: JsonNode) = list.AsArray() |> Seq.find (fun node -> Http.text node "metric" = name)
                          let input = metric "tokens.input" document["usage"]
                          Assert.equal "recorded" (Http.text input "availability")
                          Assert.equal 1200.0 (input["totals"].[0].["total"].GetValue<float>())
                          Assert.equal "unknown" (Http.text (metric "tokens.output" document["usage"]) "availability")
                          let cost = document["cost"].AsArray() |> Seq.toList
                          Assert.isTrue (not cost.IsEmpty) "every registry cost metric is listed"
                          Assert.isTrue (cost |> List.forall (fun node -> Http.text node "availability" = "unknown" && node["totals"].AsArray().Count = 0)) "unrecorded cost is unknown with no total"
                          Assert.equal (usage["groups"].ToJsonString()) (document["groups"].ToJsonString())
                          Assert.equal 404 (Http.status (server.Get "/api/v1/work/FEAT-NOPE/telemetry")))) }

          { Name = "web serve: the control-plane read endpoints leave every repository file byte-identical"
            Run =
              fun () ->
                  withFixture (fun fixture ->
                      withServer fixture (fun server ->
                          let before = snapshot fixture.Clone

                          [ "/api/v1/executions"
                            $"/api/v1/executions?workItem={fixture.WorkItem}"
                            $"/api/v1/executions/{fixture.ExecutionId}"
                            "/api/v1/executions/EXE-NOPE"
                            $"/api/v1/work/{fixture.WorkItem}/evidence"
                            $"/api/v1/work/{fixture.WorkItem}/evidence?offline=true"
                            $"/api/v1/work/{fixture.BacklogItem}/evidence"
                            $"/api/v1/work/{fixture.WorkItem}/telemetry"
                            "/api/v1/work/FEAT-NOPE/telemetry" ]
                          |> List.iter (fun path -> server.Get path |> ignore)

                          let after = snapshot fixture.Clone
                          let changed = Set.union (Set.ofSeq before.Keys) (Set.ofSeq after.Keys) |> Set.filter (fun path -> before.TryFind path <> after.TryFind path)
                          Assert.isTrue changed.IsEmpty $"""reads changed: {String.Join(", ", changed)}""")) } ]

    let tests = domainTests @ contractTests @ httpTests
