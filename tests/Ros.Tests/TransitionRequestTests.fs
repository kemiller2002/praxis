namespace Ros.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Ros.Cli
open Ros.Domain.Work

/// The control-plane transition request (`POST /api/v1/work/ID/transitions`,
/// PRX-CTL-006, PRX-UI-024, PRX-UI-027).
[<RequireQualifiedAccess>]
module TransitionRequestTests =
    let private jsonRequest (path: string) (json: string) =
        { Method = "POST"
          Segments = HttpMessages.pathSegments path
          Query = []
          ContentType = Some "application/json"
          Body = Encoding.UTF8.GetBytes json }

    let private intent action operation : TransitionIntent =
        { Id = "WI-1"
          Action = action
          Operation = operation
          Actor = None }

    let private legal reason evidence =
        ActionAvailability.Legal { Reason = reason; EvidenceTypes = evidence }

    let private item kernel (actions: (string * ActionAvailability) list) : WorkItemState =
        { Id = "WI-1"
          Title = "WI-1"
          SemanticState = "active"
          GovernedBy = kernel
          Backlog = None
          Live = None
          Actions = actions |> List.map (fun (action, availability) -> { Action = action; Availability = availability })
          Obligations = Recorded.Unavailable "not needed"
          Unknowns = Recorded.Unavailable "not needed" }

    /// Every file under `root` except Git's own, as relative path -> SHA-256.
    let private snapshot (root: string) : Map<string, string> =
        let gitDirectory = Path.Combine(root, ".git") + string Path.DirectorySeparatorChar

        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        |> Seq.filter (fun path -> not (path.StartsWith(gitDirectory, StringComparison.Ordinal)))
        |> Seq.map (fun path -> Path.GetRelativePath(root, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)))
        |> Map.ofSeq

    let private withServerEnvironment (environment: (string * string) list) (test: string -> ServedProcess -> unit) =
        let root = CliHarness.initializedRepository "ros-transition" None
        CliHarness.optOutOfDurableCheckpoints root
        CliHarness.commitAll root "pre-continuity completion semantics"

        try
            use server = new ServedProcess(root, [ "web"; "serve" ], environment = environment)
            test root server
        finally
            CliHarness.removeDirectory root

    let private withServer test = withServerEnvironment [] test

    let private transitionPath id = $"/api/v1/work/{id}/transitions"

    let private capture (server: ServedProcess) (id: string) =
        Assert.equal 200 (Http.status (server.PostJson("/api/work", $$"""{"title":"{{id}}","id":"{{id}}"}""")))

    let private request (server: ServedProcess) (id: string) (json: string) = server.PostJson(transitionPath id, json)

    /// A request that must succeed, returning its `work-transition` item.
    let private accepted (server: ServedProcess) (id: string) (json: string) =
        let response = request server id json
        let body = Http.body response
        Assert.isTrue (Http.status response = 200) $"expected 200 for {json}, got {Http.status response}: {body}"
        let document = JsonNode.Parse body
        Assert.equal "praxis.work-state" (Http.text document "contract")
        Assert.equal "work-transition" (Http.text document "kind")
        document["item"]

    /// A request that must be refused with `status` and `code`, leaving every
    /// file of the repository byte-for-byte unchanged.
    let private refused (root: string) (server: ServedProcess) (id: string) (json: string) (status: int) (code: string) =
        let before = snapshot root
        let response = request server id json
        let body = Http.body response
        let after = snapshot root
        Assert.isTrue (Http.status response = status) $"expected {status} for {json}, got {Http.status response}: {body}"
        let document = JsonNode.Parse body
        Assert.equal "praxis.error" (Http.text document "contract")
        Assert.equal code (Http.text document "code")
        Assert.equal id (Http.text document "workItemId")
        Assert.equal (Http.text document "error") (Http.text document "message")
        Assert.isTrue (Http.text document "message" <> "") "a refusal carries the kernel's message"

        let changed =
            Set.union (Set.ofSeq before.Keys) (Set.ofSeq after.Keys)
            |> Set.filter (fun path -> before.TryFind path <> after.TryFind path)

        Assert.isTrue changed.IsEmpty $"""a refused '{json}' changed: {String.Join(", ", changed)}"""
        document

    let private unitTests =
        [ { Name = "transition request: the route reads the action and the per-action arguments"
            Run =
              fun () ->
                  Assert.equal
                      (WebRoute.Transition(Ok(intent TransitionAction.Block (WorkOperation.Block("WI-1", Some "waiting")))))
                      (WebInterface.route (jsonRequest "/api/v1/work/WI-1/transitions" """{"action":"block","reason":" waiting "}"""))

                  Assert.equal
                      (WebRoute.Transition(
                          Ok
                              { intent TransitionAction.Start (WorkOperation.Start("WI-1", Some "feature", Some "me")) with
                                  Actor = Some "me" }
                      ))
                      (WebInterface.route (jsonRequest "/api/v1/work/WI-1/transitions" """{"action":"start","type":"feature","actor":"me"}"""))

                  match WebInterface.route (jsonRequest "/api/v1/work/WI-1/transitions" """{"action":"complete","evidence":[{"type":"tests","path":"t.fs"}]}""") with
                  | WebRoute.Transition(Ok { Action = TransitionAction.Complete; Operation = WorkOperation.Complete("WI-1", evidence, None, None) }) ->
                      Assert.equal [ { EvidenceInput.Type = "tests"; Path = "t.fs" } ] evidence
                  | other -> failwith $"unexpected route {other}" }

          { Name = "transition request: a missing, unknown or non-transition action is an invalid request"
            Run =
              fun () ->
                  let refusal json =
                      match WebInterface.route (jsonRequest "/api/v1/work/WI-1/transitions" json) with
                      | WebRoute.Transition(Error refusal) -> refusal
                      | other -> failwith $"unexpected route {other}"

                  let missing = refusal "{}"
                  Assert.equal RefusalCategory.InvalidRequest missing.Category
                  Assert.equal None missing.RequestedAction

                  for action in [ "update"; "attachments"; "launch" ] do
                      let unknown = refusal $$"""{"action":"{{action}}"}"""
                      Assert.equal RefusalCategory.InvalidRequest unknown.Category
                      Assert.equal (Some action) unknown.RequestedAction
                      Http.contains $"unknown action '{action}'" unknown.Message

                  match WebInterface.route (jsonRequest "/api/v1/work/WI-1/transitions" "{not json") with
                  | WebRoute.Transition(Error refusal) -> Assert.equal RefusalCategory.InvalidRequest refusal.Category
                  | other -> failwith $"unexpected route {other}" }

          { Name = "transition request: the governing kernel picks the one CLI command"
            Run =
              fun () ->
                  let abandon = { intent TransitionAction.Abandon (WorkOperation.Abandon("WI-1", Some "gone")) with Actor = Some "me" }
                  Assert.equal (WorkOperation.Abandon("WI-1", Some "gone")) (WebInterface.transitionOperation GoverningKernel.Backlog abandon)
                  Assert.equal (WorkOperation.AbandonWork("WI-1", Some "gone", Some "me")) (WebInterface.transitionOperation GoverningKernel.Live abandon)
                  let start = intent TransitionAction.Start (WorkOperation.Start("WI-1", None, None))
                  Assert.equal start.Operation (WebInterface.transitionOperation GoverningKernel.Live start)
                  Assert.equal "begin" (WebInterface.kernelActionCode GoverningKernel.Live TransitionAction.Start)
                  Assert.equal "start" (WebInterface.kernelActionCode GoverningKernel.Backlog TransitionAction.Start)

                  Assert.equal
                      [ "work"; "abandon"; "--id"; "W"; "--occurred-at"; "T"; "--reason"; "r"; "--actor"; "me" ]
                      (WebInterface.commandLine "T" [] (WorkOperation.AbandonWork("W", Some "r", Some "me"))) }

          { Name = "transition request: a refusal is categorized from the kernel's projected availability"
            Run =
              fun () ->
                  let category kernel actions transition exit =
                      WebInterface.refusalCategory (item kernel actions) transition exit

                  let refusedAction = ActionAvailability.Refused("illegal-transition", "cannot")
                  let block reason = intent TransitionAction.Block (WorkOperation.Block("WI-1", reason))
                  let complete types = intent TransitionAction.Complete (WorkOperation.Complete("WI-1", types |> List.map (fun t -> { EvidenceInput.Type = t; Path = "p" }), None, None))

                  Assert.equal RefusalCategory.IllegalTransition (category GoverningKernel.Backlog [ "block", refusedAction ] (block (Some "r")) 1)
                  Assert.equal RefusalCategory.IllegalTransition (category GoverningKernel.Backlog [] (intent TransitionAction.Resume (WorkOperation.Resume("WI-1", None))) 1)
                  Assert.equal RefusalCategory.IllegalTransition (category GoverningKernel.Live [ "begin", refusedAction ] (intent TransitionAction.Start (WorkOperation.Start("WI-1", None, None))) 1)
                  Assert.equal RefusalCategory.ReasonRequired (category GoverningKernel.Backlog [ "block", legal true [] ] (block None) 1)
                  Assert.equal RefusalCategory.ReasonRequired (category GoverningKernel.Live [ "abandon", legal true [] ] (intent TransitionAction.Abandon (WorkOperation.Abandon("WI-1", None))) 2)
                  Assert.equal RefusalCategory.EvidenceRequired (category GoverningKernel.Live [ "complete", legal false [ "implementation"; "tests" ] ] (complete [ "tests" ]) 1)
                  Assert.equal RefusalCategory.TransitionRefused (category GoverningKernel.Live [ "complete", legal false [ "tests" ] ] (complete [ "tests" ]) 1)
                  Assert.equal RefusalCategory.InvalidRequest (category GoverningKernel.Backlog [ "block", legal true [] ] (block (Some "r")) 2) }

          { Name = "transition request: a refusal document carries category, message and requested action with its status"
            Run =
              fun () ->
                  let status, body =
                      WebInterface.refusalResponse
                          { Category = RefusalCategory.IllegalTransition
                            Message = "cannot start backlog item 'WI-1' from 'captured'; mark it ready first"
                            RequestedAction = Some "start"
                            WorkItemId = "WI-1" }

                  Assert.equal 409 status
                  Assert.equal "praxis.error" (Http.text body "contract")
                  Assert.equal "illegal-transition" (Http.text body "code")
                  Assert.equal "start" (Http.text body "requestedAction")
                  Assert.equal "WI-1" (Http.text body "workItemId")
                  Http.contains "mark it ready first" (Http.text body "message")
                  Http.contains "mark it ready first" (Http.text body "error")

                  let expected =
                      [ RefusalCategory.InvalidRequest, 400
                        RefusalCategory.WorkItemNotFound, 404
                        RefusalCategory.IllegalTransition, 409
                        RefusalCategory.TransitionRefused, 409
                        RefusalCategory.ReasonRequired, 422
                        RefusalCategory.EvidenceRequired, 422
                        RefusalCategory.StateUnreadable, 500
                        RefusalCategory.ExecutionFailed, 500 ]

                  Assert.equal (expected |> List.map snd) (expected |> List.map (fst >> WebInterface.refusalStatus)) } ]

    let private apiTests =
        [ { Name = "web serve: transition requests run the CLI's transitions and return the resulting typed state"
            Run =
              fun () ->
                  withServer (fun root server ->
                      capture server "WI-FLOW"
                      let ready = accepted server "WI-FLOW" """{"action":"ready"}"""
                      Assert.equal "ready" (Http.text ready "semanticState")
                      Assert.equal "backlog" (Http.text ready "governedBy")
                      let started = accepted server "WI-FLOW" """{"action":"start","type":"feature"}"""
                      Assert.equal "active" (Http.text started "semanticState")
                      Assert.equal "live" (Http.text started "governedBy")
                      Assert.equal "feature" (Http.text started["live"] "workType")
                      let blocked = accepted server "WI-FLOW" """{"action":"block","reason":"waiting on benchmark"}"""
                      Assert.equal "blocked" (Http.text blocked "semanticState")
                      Assert.equal "waiting on benchmark" (Http.text blocked["live"] "blockReason")
                      Assert.equal "active" (Http.text (accepted server "WI-FLOW" """{"action":"resume"}""") "semanticState")
                      CliHarness.write root "src/Flow.fs" "module Flow\n"
                      CliHarness.write root "tests/FlowTests.fs" "// passed by fixture\n"

                      let completed =
                          accepted
                              server
                              "WI-FLOW"
                              """{"action":"complete","evidence":[{"type":"implementation","path":"src/Flow.fs"},{"type":"tests","path":"tests/FlowTests.fs"}]}"""

                      Assert.equal "complete" (Http.text completed "semanticState")
                      Assert.equal 2 (completed.["live"].["evidence"].AsArray().Count)

                      // The same state the read route reports.
                      let read = Http.json (server.Get "/api/v1/work/WI-FLOW")
                      Assert.equal (Http.text read["item"] "semanticState") (Http.text completed "semanticState")) }

          { Name = "web serve: abandon goes to the backlog for backlog items and to work abandon for live work"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      capture server "WI-IDEA"
                      let backlog = accepted server "WI-IDEA" """{"action":"abandon","reason":"no longer relevant"}"""
                      Assert.equal "abandoned" (Http.text backlog "semanticState")
                      capture server "WI-LIVE"
                      accepted server "WI-LIVE" """{"action":"ready"}""" |> ignore
                      accepted server "WI-LIVE" """{"action":"start","type":"feature"}""" |> ignore
                      let live = accepted server "WI-LIVE" """{"action":"abandon","reason":"superseded"}"""
                      Assert.equal "abandoned" (Http.text live "semanticState")
                      Assert.equal "live" (Http.text live "governedBy")) }

          { Name = "web serve: a refused transition is a structured refusal and leaves every file byte-for-byte unchanged"
            Run =
              fun () ->
                  withServer (fun root server ->
                      capture server "WI-NEW"

                      let tooSoon = refused root server "WI-NEW" """{"action":"start","type":"feature"}""" 409 "illegal-transition"
                      Assert.equal "start" (Http.text tooSoon "requestedAction")
                      Http.contains "mark it ready first" (Http.text tooSoon "message")

                      refused root server "WI-NEW" """{"action":"resume"}""" 409 "illegal-transition" |> ignore
                      refused root server "WI-NEW" """{"action":"block","reason":"later"}""" 409 "illegal-transition" |> ignore
                      capture server "WI-READY"
                      accepted server "WI-READY" """{"action":"ready"}""" |> ignore
                      let noReason = refused root server "WI-READY" """{"action":"block"}""" 422 "reason-required"
                      Http.contains "reason" (Http.text noReason "message")

                      capture server "WI-RUN"
                      accepted server "WI-RUN" """{"action":"ready"}""" |> ignore
                      accepted server "WI-RUN" """{"action":"start","type":"feature"}""" |> ignore
                      CliHarness.commitAll root "started"

                      let noEvidence = refused root server "WI-RUN" """{"action":"complete","evidence":[]}""" 422 "evidence-required"
                      Assert.equal "complete" (Http.text noEvidence "requestedAction")
                      Http.contains "completion evidence missing" (Http.text noEvidence "message")
                      refused root server "WI-RUN" """{"action":"abandon"}""" 422 "reason-required" |> ignore
                      refused root server "WI-RUN" """{"action":"ready"}""" 409 "illegal-transition" |> ignore
                      refused root server "WI-RUN" """{"action":"start","type":"feature"}""" 409 "illegal-transition" |> ignore

                      let unknown = refused root server "NOPE-1" """{"action":"ready"}""" 404 "work-item-not-found"
                      Assert.equal "ready" (Http.text unknown "requestedAction")
                      let invalid = refused root server "WI-RUN" """{"action":"launch"}""" 400 "invalid-request"
                      Assert.equal "launch" (Http.text invalid "requestedAction")
                      let missing = refused root server "WI-RUN" """{}""" 400 "invalid-request"
                      Assert.isTrue (isNull missing["requestedAction"]) "no action was requested"

                      // The item is still where it was.
                      Assert.equal "active" (Http.text (Http.json (server.Get "/api/v1/work/WI-RUN")).["item"] "semanticState")) }

          { Name = "web serve: an API transition records the CLI's actor and provenance, never one inferred from the HTTP client"
            Run =
              fun () ->
                  let identity =
                      [ "PRAXIS_ACTOR_KIND", "agent"
                        "PRAXIS_ACTOR", "agent:example/operator"
                        "PRAXIS_TELEMETRY_PROVIDER", "example"
                        "PRAXIS_TELEMETRY_RUNTIME", "example-runtime" ]

                  withServerEnvironment identity (fun root server ->
                      for id in [ "WI-API"; "WI-CLI" ] do
                          capture server id
                          accepted server id """{"action":"ready"}""" |> ignore

                      let hostileHeaders =
                          [ "User-Agent", "codex-cli/9.9 impostor"
                            "X-Praxis-Actor", "human:impostor"
                            "X-Forwarded-For", "203.0.113.9"
                            "From", "impostor@example.invalid" ]

                      let viaApi = server.PostJsonWithHeaders(transitionPath "WI-API", """{"action":"start","type":"feature"}""", hostileHeaders)
                      Assert.isTrue (Http.status viaApi = 200) (Http.body viaApi)
                      let viaCli = CliHarness.rosWith root identity [ "work"; "start"; "--id"; "WI-CLI"; "--occurred-at"; CliHarness.now (); "--type"; "feature" ]
                      Assert.isTrue (viaCli.Exit = 0) viaCli.Err

                      let started id =
                          File.ReadAllLines(Path.Combine(root, ".ros/events/events.jsonl"))
                          |> Array.map JsonNode.Parse
                          |> Array.filter (fun event -> Http.text event "type" = "work.started" && Http.text event "workItem" = id)
                          |> Array.exactlyOne

                      let apiEvent, cliEvent = started "WI-API", started "WI-CLI"
                      Assert.equal (cliEvent["actor"].ToJsonString()) (apiEvent["actor"].ToJsonString())
                      Assert.equal "agent" (Http.text apiEvent.["actor"] "kind")
                      Assert.equal "agent:example/operator" (Http.text apiEvent.["actor"] "id")

                      let execution (event: JsonNode) =
                          let id = event["telemetryExecutions"].AsArray() |> Seq.exactlyOne
                          JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".ros/telemetry/executions", id.GetValue<string>() + ".json")))

                      let apiExecution, cliExecution = execution apiEvent, execution cliEvent
                      Assert.equal (cliExecution["identity"].ToJsonString()) (apiExecution["identity"].ToJsonString())
                      Assert.equal (cliExecution.["provenance"].["sources"].ToJsonString()) (apiExecution.["provenance"].["sources"].ToJsonString())

                      let recorded =
                          Directory.EnumerateFiles(Path.Combine(root, ".ros"), "*", SearchOption.AllDirectories)
                          |> Seq.filter (fun path -> File.ReadAllText(path).Contains "impostor")
                          |> Seq.toList

                      Assert.isTrue recorded.IsEmpty $"""request headers reached recorded state: {String.Join(", ", recorded)}""") }

          { Name = "web serve: the per-action routes keep working beside the transition endpoint"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      capture server "WI-OLD"
                      Assert.equal 200 (Http.status (server.PostJson("/api/work/WI-OLD/ready", "")))
                      let refusedOld = server.PostJson("/api/work/WI-OLD/resume", "")
                      Assert.equal 400 (Http.status refusedOld)
                      Assert.isTrue (Http.text (Http.json refusedOld) "error" <> "") "the per-action route keeps its error shape"
                      accepted server "WI-OLD" """{"action":"block","reason":"later"}""" |> ignore
                      Assert.equal "blocked" (Http.text (Http.json (server.Get "/api/work/WI-OLD")) "status")) } ]

    let tests = unitTests @ apiTests
