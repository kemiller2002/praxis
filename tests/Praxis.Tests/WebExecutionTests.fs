namespace Praxis.Tests

open System.Text
open System.Text.Json.Nodes
open Praxis.Cli

/// PRAXIS-EXEC-05/06: the local control plane's execution API and the
/// operator execution views of `praxis web serve`.
[<RequireQualifiedAccess>]
module WebExecutionTests =
    let private form (fields: (string * string) list) =
        FormBody(fields, [])

    let private sample =
        """{"executionId":"EXE-1","workItem":"o/r:WI-1","state":"blocked","stateReason":"waiting on review","role":"implementation",
            "actor":{"id":"anthropic/claude-code","kind":"agent","provider":"anthropic","model":null,"runtime":"claude-code"},
            "baselineRevision":"abc","containment":"semantic-only",
            "containmentProfile":{"source":null,"restrictions":[{"dimension":"network","status":"unknown"}]},
            "steps":[{"stepId":"build","status":"mismatch","attempts":1},{"stepId":"publish","status":"indeterminate","attempts":1}],
            "scopeEffects":[{"resource":"README.md","classification":"outside-boundary"}],
            "divergence":["the workspace is on branch main, but the execution is bound to feature/x"],
            "legalActions":[
              {"transition":"execution.complete","target":null,"available":false,"reasons":["not every step receipt matches","requires a human actor"],"actorRequirement":"human-required"},
              {"transition":"execution.abandon","target":null,"available":true,"reasons":[],"actorRequirement":"any"}]}"""

    let unitTests =
        [ { Name = "web executions: a human form records a human actor; agent requests stay the server's identity"
            Run =
              fun () ->
                  Assert.equal
                      (Some [ "execution"; "transition"; "EXE-1"; "--action"; "complete"; "--reason"; "ok"; "--actor-kind"; "human"; "--actor"; "kem" ])
                      (WebExecutions.transitionArgs "EXE-1" (form [ "action", "complete"; "reason", "ok"; "human", "yes"; "operator", "kem" ]))

                  Assert.equal (Some [ "execution"; "transition"; "EXE-1"; "--action"; "block"; "--reason"; "" ]) (WebExecutions.transitionArgs "EXE-1" (form [ "action", "block" ]))
                  Assert.equal (Some [ "execution"; "rebind"; "EXE-1"; "--reason"; "moved" ]) (WebExecutions.transitionArgs "EXE-1" (form [ "action", "rebind"; "reason", "moved" ]))
                  Assert.equal None (WebExecutions.transitionArgs "EXE-1" (form [])) }
          { Name = "web executions: the control plane declares its listen scope"
            Run =
              fun () ->
                  let local = WebExecutions.scopeDocument { Host = "127.0.0.1"; Port = 4310 }
                  Assert.equal true (local["loopbackOnly"].GetValue<bool>())
                  Assert.equal 4310 (local["port"].GetValue<int>())
                  Assert.equal false ((WebExecutions.scopeDocument { Host = "0.0.0.0"; Port = 1 }).["loopbackOnly"].GetValue<bool>()) }
          { Name = "web executions: the execution page separates mismatch, unknown effects, obligations and human-required actions with reasons"
            Run =
              fun () ->
                  let html = WebExecutions.renderExecution (JsonNode.Parse sample) []
                  Http.contains "receipt mismatch" html
                  Http.contains "unknown effect" html
                  Http.contains "step publish: effect unknown; reconcile before retrying" html
                  Http.contains "unresolved scope effect: README.md (outside-boundary)" html
                  Http.contains "workspace divergence: the workspace is on branch main" html
                  Http.contains "<strong>Blocked:</strong> waiting on review" html
                  Http.contains "human required" html
                  Http.contains "<li>not every step receipt matches</li>" html
                  Http.contains "Execution host</dt><dd>anthropic / claude-code" html
                  // Only legal transitions get a form; the blocked completion gets none.
                  Http.contains "value=\"abandon\"" html
                  Assert.isTrue (not (html.Contains "value=\"complete\"")) "no form for an unavailable transition"
                  Assert.isTrue (not (html.Contains "<script")) "no client script" } ]

    let private withServer (test: string -> ServedProcess -> unit) =
        let root = CliHarness.initializedRepository "ros-web-exec" None
        CliHarness.optOutOfDurableCheckpoints root
        CliHarness.commitAll root "pre-continuity completion semantics"

        try
            use server = new ServedProcess(root, [ "web"; "serve" ])
            test root server
        finally
            CliHarness.removeDirectory root

    let apiTests =
        [ { Name = "web serve: /api/executions exposes work-bound executions with legal actions, reasons and actor requirements"
            Run =
              fun () ->
                  withServer (fun root server ->
                      server.PostJson("/api/work", """{"title":"Governed","id":"WI-GOV"}""") |> ignore
                      server.PostJson("/api/work/WI-GOV/ready", "") |> ignore
                      Assert.equal 200 (Http.status (server.PostJson("/api/work/WI-GOV/start", """{"type":"feature"}""")))
                      let listed = (Http.json (server.Get "/api/executions?workItem=WI-GOV")).AsArray()
                      Assert.equal 1 listed.Count
                      let id = Http.text listed[0] "executionId"
                      Assert.equal "work-transition" (Http.text listed[0].["origin"] "kind")
                      let shown = Http.json (server.Get $"/api/executions/{id}")
                      let actions = shown["legalActions"].AsArray()
                      Assert.isTrue (actions |> Seq.forall (fun a -> not (isNull a["reasons"]) && not (isNull a["actorRequirement"]))) "reasons and actor requirement"
                      let evaluate = actions |> Seq.find (fun a -> Http.text a "transition" = "execution.evaluate")
                      Assert.equal false (evaluate["available"].GetValue<bool>())
                      Assert.isTrue (evaluate["reasons"].AsArray().Count > 0) "the engine's reasons"
                      let scope = Http.json (server.Get "/api/control-plane")
                      Assert.equal true (scope["loopbackOnly"].GetValue<bool>())
                      // The work detail page lists the item's executions.
                      let detail = Http.body (server.Get "/work/WI-GOV")
                      Http.contains $"/executions/{id}" detail
                      Http.contains "id=\"executions\"" detail
                      let page = Http.body (server.Get $"/executions/{id}")
                      Http.contains "Legal actions" page
                      Assert.isTrue (not (page.Contains "<script")) "no script") }
          { Name = "web serve: a refused execution transition is a structured 409 and changes nothing; a human form transition is recorded as human"
            Run =
              fun () ->
                  withServer (fun root server ->
                      let started = CliHarness.rosOk root [ "execution"; "start"; "--work-item"; "WI-H"; "--role"; "review"; "--human-only"; "execution.complete"; "--json" ]
                      let id = Http.text (JsonNode.Parse started.Out) "executionId"
                      let refused = server.PostJson($"/api/executions/{id}/transitions", """{"action":"complete"}""")
                      Assert.equal 409 (Http.status refused)
                      let body = Http.json refused
                      Assert.equal true (body["refused"].GetValue<bool>())
                      Http.contains "requires a human actor" (Http.text body "error")
                      Assert.equal "active" (Http.text (Http.json (server.Get $"/api/executions/{id}")) "state")
                      let posted = server.PostForm($"/executions/{id}/transitions", [ "action", "complete"; "reason", "reviewed"; "human", "yes"; "operator", "casey" ])
                      Assert.equal 303 (Http.status posted)
                      Http.contains "notice=" (Http.location posted)
                      let after = Http.json (server.Get $"/api/executions/{id}")
                      Assert.equal "completed" (Http.text after "state")
                      let ledger = CliHarness.read root $".ros/executions/{id}/events.jsonl"
                      Http.contains "\"actor\":\"casey\"" ledger
                      Http.contains "\"actorKind\":\"human\"" ledger
                      Assert.equal 400 (Http.status (server.Get "/api/executions/EXE-missing"))) } ]

    let tests = unitTests @ apiTests
