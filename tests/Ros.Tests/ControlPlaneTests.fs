namespace Ros.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Ros.Cli
open Ros.Contracts.ControlPlane
open Ros.Domain.Work

/// The versioned control-plane API: `praxis control-plane ...`, its
/// `/api/v1` relay in `web serve`, and the hub's aggregation over each
/// registered repository's own Praxis.
[<RequireQualifiedAccess>]
module ControlPlaneTests =
    // ---- helpers -----------------------------------------------------

    let private now = CliHarness.now

    /// The node at a property path, and an array node's items.
    let private at (node: JsonNode) (path: string list) = path |> List.fold (fun (current: JsonNode) (key: string) -> current[key]) node
    let private items (node: JsonNode) = node.AsArray() |> Seq.toList

    /// Every file under `directory` (skipping `.git`) with its content
    /// digest, so two snapshots are equal only if nothing was written.
    let private snapshot (directory: string) : Map<string, string> =
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        |> Seq.map (fun path -> Path.GetRelativePath(directory, path).Replace('\\', '/'), path)
        |> Seq.filter (fun (relative, _) -> not (relative.StartsWith ".git/"))
        |> Seq.map (fun (relative, path) -> relative, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)))
        |> Map.ofSeq

    let private stateSnapshot (root: string) = snapshot (Path.Combine(root, ".ros"))

    let private assertUnchanged (label: string) (before: Map<string, string>) (after: Map<string, string>) =
        let changed =
            Set.union (before |> Map.keys |> Set.ofSeq) (after |> Map.keys |> Set.ofSeq)
            |> Set.filter (fun path -> before.TryFind path <> after.TryFind path)

        Assert.isTrue changed.IsEmpty $"""{label}: files changed: {String.Join(", ", changed)}"""

    let private cli (root: string) (arguments: string list) = CliHarness.ros root arguments

    let private document (root: string) (arguments: string list) =
        JsonNode.Parse((cli root ("control-plane" :: arguments)).Out)

    let private capture (root: string) (title: string) =
        Http.text (JsonNode.Parse((CliHarness.rosOk root [ "work"; "capture"; "--title"; title; "--occurred-at"; now () ]).Out)) "id"

    /// A committed repository whose items complete without a remote.
    let private repository (prefix: string) =
        let root = CliHarness.initializedRepository prefix None
        CliHarness.optOutOfDurableCheckpoints root
        CliHarness.commitAll root "pre-continuity completion semantics"
        root

    let private withRepository (prefix: string) (test: string -> unit) =
        let root = repository prefix

        try
            test root
        finally
            CliHarness.removeDirectory root

    let private withServer (environment: (string * string) list) (test: string -> ServedProcess -> unit) =
        withRepository "ros-cp-web" (fun root ->
            use server = new ServedProcess(root, [ "web"; "serve" ], environment)
            test root server)

    let private actionView (item: JsonNode) (action: string) =
        (at item [ "actions" ]).AsArray() |> Seq.find (fun entry -> Http.text entry "action" = action)

    let private legal (item: JsonNode) = Http.strings item "legalActions"

    let private live state : LiveRecord =
        { State = state
          SemanticState = ControlPlaneView.parseLiveState state
          SemanticCode = state
          WorkType = Some "feature"
          Evidence = [ "implementation", "src/a.fs" ]
          RequiredEvidence = [ "implementation"; "tests" ]
          NextAction = Some "write the tests"
          TelemetryExecutionIds = []
          Node = JsonObject() }

    let private request methodName (path: string) (query: (string * string) list) (body: string option) : HttpRequestData =
        { Method = methodName
          Segments = HttpMessages.pathSegments path
          Query = query
          ContentType = body |> Option.map (fun _ -> "application/json")
          Body = body |> Option.map Encoding.UTF8.GetBytes |> Option.defaultValue [||] }

    // ---- pure ---------------------------------------------------------

    let private unitTests =
        [ { Name = "control plane: backlog actions and refusal reasons are the kernel's own decisions"
            Run =
              fun () ->
                  let views = ControlPlaneView.actions "WI-1" (Some "captured") None
                  let find action = views |> List.find (fun view -> view.Action = action)
                  Assert.isTrue (find "ready").Legal "captured -> ready is legal"
                  Assert.isTrue (find "abandon").Legal "captured -> abandon is legal"
                  Assert.isTrue (not (find "start").Legal) "captured items cannot start"
                  Assert.equal (Some("illegal-transition", "cannot start backlog item 'WI-1' from 'captured'")) (find "start").Refusal
                  Assert.equal (Some("illegal-transition", "cannot block backlog item 'WI-1' from 'captured'")) (find "block").Refusal
                  // The kernel has no rule for resuming a backlog item, so it gives no reason.
                  Assert.isTrue (not (find "resume").Legal) "resume is not a backlog action"
                  Assert.equal None (find "resume").Refusal
                  let ready = ControlPlaneView.actions "WI-1" (Some "ready") None |> List.find (fun view -> view.Action = "block")
                  Assert.isTrue ready.Legal "ready -> block is legal"
                  Assert.equal [ "reason" ] ready.Requires }

          { Name = "control plane: live actions name what they still require, from the kernel's rejections"
            Run =
              fun () ->
                  let views = ControlPlaneView.actions "WI-2" (Some "ready") (Some(live "active"))
                  let find action = views |> List.find (fun view -> view.Action = action)
                  Assert.equal [ "evidence:implementation"; "evidence:tests" ] (find "complete").Requires
                  Assert.isTrue (find "complete").Legal "active -> complete is legal with evidence"
                  Assert.equal [ "reason" ] (find "abandon").Requires
                  Assert.equal (Some("illegal-transition", "cannot resume 'WI-2' from 'active'")) (find "resume").Refusal
                  Assert.equal None (find "ready").Refusal

                  match ControlPlaneView.decide "WI-2" None (Some(live "active")) "complete" { ControlPlaneView.noArguments with Evidence = [ "implementation", "a" ] } with
                  | ControlPlaneView.NeedsArguments(RefusalCategory.MissingArgument, "missing-evidence", message, [ "evidence:tests" ]) ->
                      Http.contains "tests" message
                  | other -> failwith $"unexpected decision {other}"

                  match ControlPlaneView.decide "WI-2" None None "ready" ControlPlaneView.noArguments with
                  | ControlPlaneView.Refused(RefusalCategory.NotFound, _, _) -> ()
                  | other -> failwith $"unexpected decision {other}" }

          { Name = "control plane: obligations and unknowns are reported unavailable, never empty, without a source"
            Run =
              fun () ->
                  let backlogOnly = ControlPlaneJson.availability ControlPlaneJson.obligations (ControlPlaneView.obligations None)
                  Assert.equal "unavailable" (Http.text backlogOnly "status")
                  Assert.isTrue (Http.text backlogOnly "reason" <> "") "an unavailable source says why"
                  let unknowns = ControlPlaneJson.availability (fun (_: string list) -> JsonArray() :> JsonNode) ControlPlaneView.unknowns
                  Assert.equal "unavailable" (Http.text unknowns "status")

                  match ControlPlaneView.obligations (Some(live "active")) with
                  | Availability.Available value ->
                      Assert.equal [ "tests" ] value.MissingEvidenceForCompletion
                      Assert.equal (Some "write the tests") value.NextAction
                  | Availability.Unavailable reason -> failwith reason }

          { Name = "control plane: refusal categories map to HTTP statuses and documents are version-checked"
            Run =
              fun () ->
                  Assert.equal 404 (ControlPlaneJson.httpStatus RefusalCategory.NotFound)
                  Assert.equal 400 (ControlPlaneJson.httpStatus RefusalCategory.InvalidRequest)
                  Assert.equal 409 (ControlPlaneJson.httpStatus RefusalCategory.IllegalTransition)
                  Assert.equal 422 (ControlPlaneJson.httpStatus RefusalCategory.MissingArgument)
                  Assert.equal 422 (ControlPlaneJson.httpStatus RefusalCategory.Rejected)
                  Assert.equal 503 (ControlPlaneJson.httpStatus RefusalCategory.Unavailable)
                  Assert.equal 502 (ControlPlaneJson.httpStatus RefusalCategory.Incompatible)

                  let source =
                      { Repository = "r"
                        Commit = Some "abc"
                        Branch = None
                        StateFingerprint = "sha256:x" }

                  let refused =
                      ControlPlaneJson.refusalDocument
                          "transition"
                          (Some source)
                          { Category = RefusalCategory.IllegalTransition
                            Code = Some "illegal-transition"
                            Message = "no"
                            Action = Some "start"
                            WorkItemId = Some "WI-1" }

                  Assert.equal 409 (ControlPlaneJson.statusOf refused)
                  Assert.equal 200 (ControlPlaneJson.statusOf (ControlPlaneJson.document "source" source (JsonObject())))
                  Assert.isTrue (ControlPlaneJson.isCompatible refused) "a v1 document is compatible"
                  let other = JsonObject()
                  other["schema"] <- JsonValue.Create ControlPlaneJson.Schema
                  other["schemaVersion"] <- JsonValue.Create 2
                  Assert.isTrue (not (ControlPlaneJson.isCompatible other)) "another version is not" }

          { Name = "control plane: the state fingerprint depends on content, not on enumeration order"
            Run =
              fun () ->
                  let a = "context/current.json", Encoding.UTF8.GetBytes "{}"
                  let b = "work/queue.json", Encoding.UTF8.GetBytes "[]"
                  Assert.equal (ControlPlaneSource.digest [ a; b ]) (ControlPlaneSource.digest [ b; a ])
                  Assert.isTrue (ControlPlaneSource.digest [ a; b ] <> ControlPlaneSource.digest [ a; fst b, Encoding.UTF8.GetBytes "[1]" ]) "content changes the fingerprint"
                  Assert.isTrue ((ControlPlaneSource.digest []).StartsWith "sha256:") "fingerprints are labelled" }

          { Name = "control plane: an execution's provider, model and runtime are its host, not the work's owner"
            Run =
              fun () ->
                  let node =
                      JsonNode.Parse(
                          """{"executionId":"EXE-1","workItem":"o/r:WI-1","actor":{"id":"a","kind":"agent","provider":"p","model":"m","runtime":"rt"},"role":"implementation","state":"active","stateReason":null,"startedAt":"t","steps":[]}"""
                      )
                          .AsObject()

                  let view = ControlPlaneView.execution node
                  Assert.equal "p" (Http.text (at view [ "executionHost" ]) "provider")
                  Assert.equal "rt" (Http.text (at view [ "executionHost" ]) "runtime")
                  Assert.isTrue (isNull (at view [ "actor"; "provider" ])) "the actor carries no provider"
                  Assert.equal "repository" (Http.text view "workStateOwner")
                  Assert.equal "active" (Http.text view "status") }

          { Name = "control plane: a permitted request runs the CLI's own transition command line"
            Run =
              fun () ->
                  let arguments = { ControlPlaneView.noArguments with Reason = Some "stop"; Evidence = [ "tests", "t.fs" ] }
                  Assert.equal [ "work"; "backlog-transition"; "--id"; "WI-1"; "--action"; "abandon"; "--occurred-at"; "T"; "--reason"; "stop" ] (ControlPlaneCommands.commandLine "T" "WI-1" false "abandon" arguments)
                  Assert.equal [ "work"; "abandon"; "--id"; "WI-1"; "--occurred-at"; "T"; "--reason"; "stop" ] (ControlPlaneCommands.commandLine "T" "WI-1" true "abandon" arguments)
                  Assert.equal [ "work"; "complete"; "--id"; "WI-1"; "--occurred-at"; "T"; "--evidence"; "tests=t.fs" ] (ControlPlaneCommands.commandLine "T" "WI-1" true "complete" arguments)
                  Assert.equal [ "work"; "resume"; "--id"; "WI-1"; "--occurred-at"; "T" ] (ControlPlaneCommands.commandLine "T" "WI-1" true "resume" arguments) }

          { Name = "control plane: /api/v1 routes are control-plane command lines and never carry identity from the request"
            Run =
              fun () ->
                  let routeOf methodName path query body = WebInterface.route (request methodName path query body)
                  Assert.equal (WebRoute.ControlPlane [ "control-plane"; "work"; "--tag"; "a"; "--status"; "ready" ]) (routeOf "GET" "/api/v1/work" [ "tag", "a"; "status", "ready" ] None)
                  Assert.equal (WebRoute.ControlPlane [ "control-plane"; "work"; "WI-1" ]) (routeOf "GET" "/api/v1/work/WI-1" [] None)
                  Assert.equal (WebRoute.ControlPlane [ "control-plane"; "evidence"; "WI-1" ]) (routeOf "GET" "/api/v1/work/WI-1/evidence" [] None)
                  Assert.equal (WebRoute.ControlPlane [ "control-plane"; "executions"; "--work-item"; "WI-1" ]) (routeOf "GET" "/api/v1/executions" [ "workItem", "WI-1" ] None)
                  Assert.equal (WebRoute.ControlPlane [ "control-plane"; "execution"; "EXE-1" ]) (routeOf "GET" "/api/v1/executions/EXE-1" [] None)

                  let posted =
                      routeOf "POST" "/api/v1/work/WI-1/transitions" [] (Some """{"action":"block","reason":"waiting","actor":"mallory","agent":"mallory"}""")

                  Assert.equal (WebRoute.ControlPlane [ "control-plane"; "transition"; "--id"; "WI-1"; "--action"; "block"; "--reason"; "waiting" ]) posted

                  match routeOf "POST" "/api/v1/work/WI-1/transitions" [] (Some """{"action":"block","reason":"--actor"}""") with
                  | WebRoute.ControlPlaneRefusal refusal -> Assert.equal RefusalCategory.InvalidRequest refusal.Category
                  | other -> failwith $"an option-like value must be refused, got {other}"

                  match routeOf "GET" "/api/v1/nothing" [] None with
                  | WebRoute.ControlPlaneRefusal refusal -> Assert.equal RefusalCategory.NotFound refusal.Category
                  | other -> failwith $"unexpected route {other}"

                  // The legacy routes are untouched.
                  Assert.equal (WebRoute.Api(WorkOperation.Show "WI-1")) (routeOf "GET" "/api/work/WI-1" [] None) }

          { Name = "control plane: hub /api/v1 routes aggregate or relay to one repository"
            Run =
              fun () ->
                  let routeOf methodName path query body = HubWeb.controlPlaneRoute (request methodName path query body) (HttpMessages.pathSegments path |> List.skip 2)
                  Assert.equal HubControlPlaneRoute.Repositories (routeOf "GET" "/api/v1/repos" [] None)
                  Assert.equal (HubControlPlaneRoute.Work [ "--status"; "ready" ]) (routeOf "GET" "/api/v1/work" [ "status", "ready" ] None)
                  Assert.equal (HubControlPlaneRoute.Repository("alpha", [ "work"; "WI-1" ])) (routeOf "GET" "/api/v1/repos/alpha/work/WI-1" [] None)

                  Assert.equal
                      (HubControlPlaneRoute.Repository("alpha", [ "transition"; "--id"; "WI-1"; "--action"; "ready" ]))
                      (routeOf "POST" "/api/v1/repos/alpha/work/WI-1/transitions" [] (Some """{"action":"ready","actor":"mallory"}""")) } ]

    // ---- the host writes nothing itself ------------------------------

    /// Source files of the two hosts. Each write API they call is listed
    /// here; any other write fails this check (PRAXIS-CTL-05).
    let private hostSources = [ "src/Ros.Cli/WebHttp.fs"; "src/Ros.Cli/WebInterface.fs"; "src/Ros.Cli/Hub.fs"; "src/Ros.Cli/ControlPlane.fs" ]

    let private permittedWrites =
        [ "src/Ros.Cli/WebInterface.fs", "Directory.CreateDirectory directory |> ignore" // a private temp directory for uploads
          "src/Ros.Cli/WebInterface.fs", "File.WriteAllBytes(path, upload.Data)" // into that temp directory
          "src/Ros.Cli/WebInterface.fs", "Directory.Delete(directory, true)" // removing it again
          "src/Ros.Cli/Hub.fs", "Directory.CreateDirectory(Path.GetDirectoryName(registryPath root)) |> ignore" // hub registration data
          "src/Ros.Cli/Hub.fs", "File.WriteAllText(registryPath root, HubRegistry.render registry, utf8)"
          "src/Ros.Cli/Hub.fs", "File.WriteAllText(registryMarkdownPath root, HubRegistry.renderMarkdown registry.Repos, utf8)" ]

    let private repositoryRoot () =
        let rec up (directory: DirectoryInfo) =
            if isNull directory then failwith "repository root not found"
            elif File.Exists(Path.Combine(directory.FullName, "Ros.slnx")) then directory.FullName
            else up directory.Parent

        up (DirectoryInfo AppContext.BaseDirectory)

    let private sourceTests =
        [ { Name = "control plane: the web and hub hosts call no file-writing API beyond uploads and hub registration"
            Run =
              fun () ->
                  let root = repositoryRoot ()
                  let writeApi = Regex(@"\b(File\.(Write|Append|Delete|Move|Copy|Create|Replace|Open)\w*|Directory\.(Create|Delete|Move)\w*|new\s+(FileStream|StreamWriter)|FileStream\(|StreamWriter\()")

                  let found =
                      hostSources
                      |> List.collect (fun relative ->
                          File.ReadAllLines(Path.Combine(root, relative))
                          |> Array.filter (fun line -> writeApi.IsMatch line && not (line.TrimStart().StartsWith "//"))
                          |> Array.map (fun line -> relative, line.Trim())
                          |> Array.toList)

                  let unexpected = found |> List.filter (fun write -> not (List.contains write permittedWrites))
                  Assert.isTrue unexpected.IsEmpty $"""host writes outside the CLI transition path: {unexpected}""" } ]

    // ---- the CLI documents ---------------------------------------------

    let private cliTests =
        [ { Name = "control-plane work: documents carry the source they were derived from"
            Run =
              fun () ->
                  withRepository "ros-cp-cli" (fun root ->
                      let id = capture root "First"
                      let list = document root [ "work" ]
                      Assert.equal ControlPlaneJson.Schema (Http.text list "schema")
                      Assert.equal 1 (Http.number list "schemaVersion")
                      Assert.equal "work-list" (Http.text list "kind")
                      Assert.equal (CliHarness.git root [ "rev-parse"; "HEAD" ]) (Http.text (at list [ "source" ]) "commit")
                      Assert.equal "main" (Http.text (at list [ "source" ]) "branch")
                      Assert.equal (Http.text (at (CliHarness.json (CliHarness.read root "ros.json")) [ "repository" ]) "id") (Http.text (at list [ "source" ]) "repository")
                      let item = (at list [ "data" ]).AsArray() |> Seq.find (fun row -> Http.text row "id" = id)
                      Assert.equal "captured" (Http.text item "semanticState")
                      Assert.equal "captured" (Http.text (at item [ "backlog" ]) "status")
                      Assert.isTrue (isNull (at item [ "live" ])) "a captured item has no live record"
                      Assert.equal [ "ready"; "abandon" ] (legal item)
                      Assert.equal "unavailable" (Http.text (at item [ "obligations" ]) "status")
                      Assert.equal "unavailable" (Http.text (at item [ "unknowns" ]) "status")
                      let missing = cli root [ "control-plane"; "work"; "WI-9999" ]
                      Assert.equal 1 missing.Exit
                      Assert.equal "not-found" (Http.text (at (JsonNode.Parse missing.Out) [ "refusal" ]) "category")) }

          { Name = "control-plane evidence: recorded evidence, checkpoints and usage, or why they are unavailable"
            Run =
              fun () ->
                  withRepository "ros-cp-evidence" (fun root ->
                      let id = capture root "Evidence"
                      let before = document root [ "evidence"; id ]
                      Assert.equal "unavailable" (Http.text (at before [ "data"; "evidence" ]) "status")
                      Assert.equal "unavailable" (Http.text (at before [ "data"; "checkpoints" ]) "status")
                      Assert.equal "unavailable" (Http.text (at before [ "data"; "usage" ]) "status")
                      CliHarness.rosOk root [ "work"; "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; now () ] |> ignore
                      CliHarness.rosOk root [ "work"; "start"; "--id"; id; "--occurred-at"; now () ] |> ignore
                      CliHarness.rosOk root [ "telemetry"; "record"; id; "--metric"; "tokens.input"; "--value"; "1200"; "--unit"; "tokens"; "--source-type"; "runtime-api"; "--quiet" ] |> ignore
                      CliHarness.rosOk root [ "work"; "complete"; "--id"; id; "--occurred-at"; now (); "--evidence"; "implementation=ros.json"; "--evidence"; "tests=ros.json" ] |> ignore
                      let after = document root [ "evidence"; id ]
                      let recorded = items (at after [ "data"; "evidence"; "value" ]) |> List.map (fun entry -> Http.text entry "type")
                      Assert.equal [ "implementation"; "tests" ] recorded
                      Assert.equal "available" (Http.text (at after [ "data"; "checkpoints" ]) "status")
                      Assert.equal "work checkpoint show" (Http.text (at after [ "data"; "checkpoints"; "value" ]) "command")
                      // The same document `telemetry usage` prints, relayed rather than re-derived.
                      let usage = JsonNode.Parse((CliHarness.rosOk root [ "telemetry"; "usage"; id ]).Out)
                      Assert.equal (usage.ToJsonString()) ((at after [ "data"; "usage"; "value" ]).ToJsonString())) }

          { Name = "control-plane executions: steps show expected and observed receipts and their match state"
            Run =
              fun () ->
                  withRepository "ros-cp-exec" (fun root ->
                      let id = capture root "Executed"
                      let started = JsonNode.Parse((CliHarness.rosOk root [ "execution"; "start"; "--work-item"; id; "--role"; "implementation"; "--json"; "--provider"; "acme"; "--model"; "m-1"; "--runtime"; "cli" ]).Out)
                      let executionId = Http.text started "executionId"
                      CliHarness.rosOk root [ "execution"; "step"; "declare"; executionId; "--step"; "s1"; "--expect-command"; "true" ] |> ignore
                      CliHarness.rosOk root [ "execution"; "step"; "declare"; executionId; "--step"; "s2"; "--expect-command"; "false"; "--sequence"; "2" ] |> ignore
                      CliHarness.rosOk root [ "execution"; "step"; "run"; executionId; "--step"; "s1"; "--command"; "true" ] |> ignore
                      CliHarness.ros root [ "execution"; "step"; "run"; executionId; "--step"; "s2"; "--command"; "false" ] |> ignore
                      let listed = document root [ "executions"; "--work-item"; id ]
                      Assert.equal [ executionId ] ((at listed [ "data" ]).AsArray() |> Seq.map (fun entry -> Http.text entry "executionId") |> Seq.toList)
                      let shown = document root [ "execution"; executionId ]
                      let data = (at shown [ "data" ])
                      Assert.equal "implementation" (Http.text data "role")
                      Assert.equal "active" (Http.text data "status")
                      Assert.equal "acme" (Http.text (at data [ "executionHost" ]) "provider")
                      Assert.equal "m-1" (Http.text (at data [ "executionHost" ]) "model")
                      let steps = (at data [ "steps" ]).AsArray() |> Seq.toList
                      let step name = steps |> List.find (fun entry -> Http.text entry "stepId" = name)
                      Assert.equal "match" (Http.text (step "s1") "status")
                      Assert.equal "mismatch" (Http.text (step "s2") "status")
                      Assert.equal "command-succeeded" (Http.text (at (step "s1") [ "expected" ]) "kind")
                      let observation = items (at (step "s2") [ "observations" ]) |> List.head
                      let fact = items (at observation [ "observed"; "facts" ]) |> List.head
                      Assert.equal 1 (Http.number fact "exitCode")
                      Assert.equal "mismatch" (Http.text (at observation [ "result" ]) "result")
                      // `execution show --json` exposes the same observations.
                      let raw = JsonNode.Parse((CliHarness.rosOk root [ "execution"; "show"; executionId; "--json" ]).Out)
                      Assert.equal ((at raw [ "steps" ]).ToJsonString()) ((at data [ "steps" ]).ToJsonString())
                      let unknown = cli root [ "control-plane"; "execution"; "EXE-missing" ]
                      Assert.equal "not-found" (Http.text (at (JsonNode.Parse unknown.Out) [ "refusal" ]) "category")) } ]

    // ---- web serve /api/v1 --------------------------------------------

    let private webTests =
        [ { Name = "web serve v1: typed work documents, a structured 404, and unchanged legacy routes"
            Run =
              fun () ->
                  withServer [] (fun root server ->
                      let id = capture root "Served"
                      let legacyBefore = Http.body (server.Get "/api/work")
                      let list = server.Get "/api/v1/work"
                      Assert.equal 200 (Http.status list)
                      let listed = Http.json list
                      Assert.equal "work-list" (Http.text listed "kind")
                      Assert.isTrue ((at listed [ "data" ]).AsArray() |> Seq.exists (fun row -> Http.text row "id" = id)) "the item is listed"
                      let item = Http.json (server.Get $"/api/v1/work/{id}")
                      Assert.equal [ "ready"; "abandon" ] (legal (at item [ "data" ]))
                      let missing = server.Get "/api/v1/work/WI-9999"
                      Assert.equal 404 (Http.status missing)
                      let body = Http.json missing
                      Assert.equal "not-found" (Http.text (at body [ "refusal" ]) "category")
                      Http.contains "WI-9999" (Http.text (at body [ "refusal" ]) "message")
                      Assert.equal 404 (Http.status (server.Get "/api/v1/nowhere"))
                      Assert.equal "not-found" (Http.text (at (Http.json (server.Get "/api/v1/nowhere")) [ "refusal" ]) "category")
                      // The unversioned API answers exactly as before.
                      Assert.equal legacyBefore (Http.body (server.Get "/api/work"))
                      Assert.equal "captured" (Http.text (Http.json (server.Get $"/api/work/{id}")) "status")) }

          { Name = "web serve v1: a refused transition is structured and leaves Praxis state byte for byte unchanged"
            Run =
              fun () ->
                  // Durable checkpoints stay required here, so completing
                  // meaningful uncommitted work is refused by the CLI itself.
                  let root = CliHarness.initializedRepository "ros-cp-refuse" None

                  try
                      let id = capture root "Guarded"
                      CliHarness.commitAll root "capture"
                      use server = new ServedProcess(root, [ "web"; "serve" ])

                      let refuse (body: string) (status: int) (category: string) =
                          let before = stateSnapshot root
                          let response = server.PostJson($"/api/v1/work/{id}/transitions", body)
                          Assert.equal status (Http.status response)
                          let refusal = (at (Http.json response) [ "refusal" ])
                          Assert.equal category (Http.text refusal "category")
                          assertUnchanged body before (stateSnapshot root)
                          refusal

                      let illegal = refuse """{"action":"start"}""" 409 "illegal-transition"
                      Assert.equal "illegal-transition" (Http.text illegal "code")
                      Assert.equal "start" (Http.text illegal "action")
                      Assert.equal id (Http.text illegal "workItemId")
                      refuse """{"action":"fly"}""" 400 "invalid-request" |> ignore
                      refuse """{}""" 400 "invalid-request" |> ignore
                      Assert.equal 404 (Http.status (server.PostJson("/api/v1/work/WI-9999/transitions", """{"action":"ready"}""")))
                      Assert.equal 200 (Http.status (server.PostJson($"/api/v1/work/{id}/transitions", """{"action":"ready"}""")))
                      let missing = refuse """{"action":"block"}""" 422 "missing-argument"
                      Assert.equal "block-reason-required" (Http.text missing "code")
                      Assert.equal 200 (Http.status (server.PostJson($"/api/v1/work/{id}/transitions", """{"action":"start","type":"feature"}""")))
                      File.WriteAllText(Path.Combine(root, "change.txt"), "meaningful\n")
                      let rejected = refuse """{"action":"complete","evidence":[{"type":"implementation","path":"change.txt"},{"type":"tests","path":"change.txt"}]}""" 422 "rejected"
                      Http.contains "durable" (Http.text rejected "message")
                      Assert.equal "active" (Http.text (at (Http.json (server.Get $"/api/v1/work/{id}")) [ "data" ]) "semanticState")
                  finally
                      CliHarness.removeDirectory root }

          { Name = "web serve v1: a transition runs the CLI's path, returns the resulting state, and records the host's identity"
            Run =
              fun () ->
                  let environment = [ "PRAXIS_ACTOR_KIND", "agent"; "PRAXIS_ACTOR", "host-agent"; "PRAXIS_TELEMETRY_PROVIDER", "host-provider" ]

                  withServer environment (fun root server ->
                      let viaApi = capture root "Via API"
                      let viaCli = capture root "Via CLI"

                      for id in [ viaApi; viaCli ] do
                          CliHarness.rosOk root [ "work"; "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; now () ] |> ignore

                      let response = server.PostJson($"/api/v1/work/{viaApi}/transitions", """{"action":"start","type":"feature","actor":"mallory","agent":"mallory","provider":"mallory"}""")
                      Assert.equal 200 (Http.status response)
                      let started = Http.json response
                      Assert.equal "transition" (Http.text started "kind")
                      Assert.equal "active" (Http.text (at started [ "data"; "workItem" ]) "semanticState")
                      Assert.equal "feature" (Http.text (at started [ "data"; "workItem"; "live" ]) "workType")
                      CliHarness.rosWith root environment [ "work"; "start"; "--id"; viaCli; "--occurred-at"; now (); "--type"; "feature" ] |> ignore

                      let identityOf id =
                          let context = JsonNode.Parse((CliHarness.rosOk root [ "work"; "context"; id; "--offline" ]).Out)
                          let executionId = (items (at (items (at context [ "workItems" ]) |> List.head) [ "telemetryExecutionIds" ]) |> List.head).GetValue<string>()
                          let record = CliHarness.read root $".ros/telemetry/executions/{executionId}.json"
                          Assert.isTrue (not (record.Contains "mallory")) "nothing from the HTTP request reaches provenance"
                          (at (JsonNode.Parse record) [ "identity" ])

                      let api = identityOf viaApi
                      let direct = identityOf viaCli
                      Assert.equal "host-provider" (Http.text api "provider")
                      Assert.equal (direct.ToJsonString()) (api.ToJsonString())) }

          { Name = "web serve v1: a restarted host answers identically, and CLI changes appear without a restart"
            Run =
              fun () ->
                  withRepository "ros-cp-restart" (fun root ->
                      let id = capture root "Persisted"
                      let paths = [ "/api/v1/source"; "/api/v1/work"; $"/api/v1/work/{id}"; $"/api/v1/work/{id}/evidence"; "/api/v1/executions" ]

                      let answers () =
                          use server = new ServedProcess(root, [ "web"; "serve" ])
                          paths |> List.map (fun path -> path, Http.body (server.Get path))

                      let first = answers ()
                      let second = answers ()
                      Assert.equal first second

                      use server = new ServedProcess(root, [ "web"; "serve" ])
                      let fingerprint () = Http.text (at (Http.json (server.Get "/api/v1/source")) [ "source" ]) "stateFingerprint"
                      let before = fingerprint ()
                      CliHarness.rosOk root [ "work"; "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; now () ] |> ignore
                      let item = Http.json (server.Get $"/api/v1/work/{id}")
                      Assert.equal "ready" (Http.text (at item [ "data" ]) "semanticState")
                      Assert.isTrue (fingerprint () <> before) "the fingerprint reflects the CLI's change"
                      Assert.equal (Http.text (at item [ "source" ]) "stateFingerprint") (fingerprint ())) }

          { Name = "web serve v1: no read endpoint writes any repository file"
            Run =
              fun () ->
                  withServer [] (fun root server ->
                      let id = capture root "Read only"
                      CliHarness.rosOk root [ "work"; "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; now () ] |> ignore
                      CliHarness.rosOk root [ "work"; "start"; "--id"; id; "--occurred-at"; now () ] |> ignore
                      let before = snapshot root

                      for path in [ "/api/v1/source"; "/api/v1/work"; "/api/v1/work?status=active"; $"/api/v1/work/{id}"; $"/api/v1/work/{id}/evidence"; "/api/v1/executions"; $"/api/v1/executions?workItem={id}"; "/api/v1/executions/EXE-none"; "/api/v1/work/WI-none" ] do
                          server.Get path |> ignore

                      assertUnchanged "reads" before (snapshot root)) } ]

    // ---- hub serve /api/v1 --------------------------------------------

    let private spoke (prefix: string) =
        let root = repository prefix
        let launcher = Path.Combine(root, "praxis")
        File.WriteAllText(launcher, $"#!/bin/sh\nexec dotnet \"{CliHarness.cli}\" \"$@\"\n")

        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(launcher, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

        root

    let private hubTests =
        [ { Name = "hub serve v1: per-repository documents come from each repository's own Praxis; failures stay per repository"
            Run =
              fun () ->
                  let hubRoot = CliHarness.initializedRepository "ros-cp-hub" (Some "project-administration")
                  let spokes = [ spoke "ros-cp-spoke1"; spoke "ros-cp-spoke2"; spoke "ros-cp-spoke3" ]

                  try
                      let ids = spokes |> List.map (fun root -> Http.text (JsonNode.Parse((CliHarness.rosOk hubRoot [ "hub"; "register"; root ]).Out)) "id")
                      let healthy, gone, old = spokes[0], spokes[1], spokes[2]
                      let healthyId, goneId, oldId = ids[0], ids[1], ids[2]
                      let item = capture healthy "Spoke item"
                      Directory.Move(gone, gone + "-moved")
                      // A Praxis that predates the control plane: no such command.
                      File.WriteAllText(Path.Combine(old, "praxis"), "#!/bin/sh\necho \"Usage: praxis ...\" >&2\nexit 2\n")
                      use server = new ServedProcess(hubRoot, [ "hub"; "serve" ])
                      let hubBefore = snapshot hubRoot

                      let repos = Http.json (server.Get "/api/v1/repos")
                      Assert.equal "hub-repositories" (Http.text repos "kind")
                      let availability id = (at repos [ "data" ]).AsArray() |> Seq.find (fun entry -> Http.text entry "id" = id) |> fun entry -> Http.text (at entry [ "availability" ]) "status"
                      Assert.equal "available" (availability healthyId)
                      Assert.equal "unreachable" (availability goneId)
                      Assert.equal "incompatible" (availability oldId)

                      let work = server.Get "/api/v1/work"
                      Assert.equal 200 (Http.status work)
                      let entry = items (at (Http.json work) [ "data" ]) |> List.find (fun repo -> Http.text repo "id" = healthyId)
                      Assert.isTrue (items (at entry [ "document"; "data" ]) |> List.exists (fun row -> Http.text row "id" = item)) "the healthy repository's work is listed"

                      // The same per-repository contract the repository itself prints.
                      let relayed = Http.json (server.Get $"/api/v1/repos/{healthyId}/work/{item}")
                      let direct = document healthy [ "work"; item ]
                      Assert.equal (direct.ToJsonString()) (relayed.ToJsonString())
                      Assert.equal 404 (Http.status (server.Get $"/api/v1/repos/{healthyId}/work/WI-9999"))
                      Assert.equal 404 (Http.status (server.Get "/api/v1/repos/nope/work"))
                      Assert.equal 503 (Http.status (server.Get $"/api/v1/repos/{goneId}/work"))
                      Assert.equal 502 (Http.status (server.Get $"/api/v1/repos/{oldId}/work"))

                      // A change made in the repository, without the hub, is visible at once.
                      CliHarness.rosOk healthy [ "work"; "backlog-transition"; "--id"; item; "--action"; "ready"; "--occurred-at"; now () ] |> ignore
                      Assert.equal "ready" (Http.text (at (Http.json (server.Get $"/api/v1/repos/{healthyId}/work/{item}")) [ "data" ]) "semanticState")

                      // Mutation is delegated to the owning repository's transition path.
                      let refused = server.PostJson($"/api/v1/repos/{healthyId}/work/{item}/transitions", """{"action":"ready"}""")
                      Assert.equal 409 (Http.status refused)
                      let started = server.PostJson($"/api/v1/repos/{healthyId}/work/{item}/transitions", """{"action":"start","type":"feature"}""")
                      Assert.equal 200 (Http.status started)
                      let context = JsonNode.Parse((CliHarness.rosOk healthy [ "work"; "context"; item; "--offline" ]).Out)
                      Assert.equal "active" (Http.text ((at context [ "workItems" ]).AsArray()[0]) "semanticState")

                      // The hub stored nothing: its own files are exactly as registration left them.
                      assertUnchanged "hub" hubBefore (snapshot hubRoot)
                      Directory.Move(gone + "-moved", gone)
                  finally
                      hubRoot :: spokes |> List.iter CliHarness.removeDirectory
                      CliHarness.removeDirectory (spokes[1] + "-moved") } ]

    let tests = unitTests @ sourceTests @ cliTests @ webTests @ hubTests
