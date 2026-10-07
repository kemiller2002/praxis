namespace Praxis.Tests

open System
open System.Collections.Generic
open System.IO
open System.Text.Json.Nodes

/// `praxis remote execute` (PRAXIS-REMOTE-03, DF-ROS-2026-A041): the
/// transport-independent boundary that runs one praxis.remote request with
/// the same command implementation as the local CLI. These tests exercise
/// the built F# binary against real Git repositories and pin the invariants
/// #90 requires: local/remote equivalence, idempotent replay after a lost
/// result, fail-closed duplicates, stale and dirty repository state,
/// capability opt-in, hostile input, secret isolation, and "nothing kept"
/// after a refused or interrupted mutation. Ported from the former
/// tests/remote-execute.test.mjs.
[<RequireQualifiedAccess>]
module RemoteExecuteCliTests =
    type private Remote =
        { Exit: int
          Out: string
          Response: JsonNode }

    let private agent =
        """{"kind":"agent","id":"example/cloud-agent","provider":"example","runtime":"cloud-agent"}"""

    let private agentId = "example/cloud-agent"

    let private agentEnvironment =
        [ "ROS_ACTOR_KIND", "agent"
          "ROS_ACTOR", agentId
          "ROS_TELEMETRY_PROVIDER", "example"
          "ROS_TELEMETRY_RUNTIME", "cloud-agent" ]

    let private plantedToken = "ghp_" + String('Q', 36)

    let private allGrants = [ "read"; "mutate"; "complete"; "reconcile" ]

    let private git (root: string) (arguments: string list) = CliHarness.git root arguments

    let private praxis (root: string) (arguments: string list) (environment: (string * string) list) =
        CliHarness.runIn (Some root) "dotnet" ([ CliHarness.cli; "--root"; root ] @ arguments) environment

    let private praxisOk (root: string) (arguments: string list) =
        CliPort.exitCode 0 (praxis root arguments [])

    let private commit (root: string) (message: string) =
        git root [ "add"; "-A" ] |> ignore
        git root [ "commit"; "-qm"; message ] |> ignore

    /// A committed repository with ready work item WI-0100; `None` leaves
    /// remote execution unconfigured.
    let private fixture (temporary: string -> string) (label: string) (remoteCapabilities: string list option) =
        let root = temporary $"praxis-remote-{label}"
        git root [ "init"; "-q"; "-b"; "main" ] |> ignore
        praxisOk root [ "init"; "--project"; "Remote Execute" ]

        remoteCapabilities
        |> Option.iter (fun capabilities ->
            let config = CliPort.readJson root "ros.json"
            let listed = capabilities |> List.map (fun capability -> $"\"{capability}\"") |> String.concat ","
            config["remote"] <- JsonNode.Parse("{\"capabilities\":[" + listed + "]}")
            CliPort.writeJson root "ros.json" config)

        git root [ "config"; "user.email"; "test@example.invalid" ] |> ignore
        git root [ "config"; "user.name"; "Praxis Test" ] |> ignore
        git root [ "config"; "commit.gpgsign"; "false" ] |> ignore
        praxisOk root [ "add"; "Remote item"; "--id"; "WI-0100" ]
        praxisOk root [ "work"; "backlog-transition"; "--action"; "ready"; "--id"; "WI-0100"; "--occurred-at"; CliHarness.now () ]
        commit root "baseline"
        root

    let private optedIn temporary label = fixture temporary label (Some allGrants)

    /// A praxis.remote request bound to the repository's current HEAD.
    /// `extra` entries (name, JSON text) override or add top-level fields.
    let private requestWith (root: string) (operation: string) (arguments: string) (extra: (string * string) list) =
        let dot = operation.IndexOf '.'
        let slug = if dot < 0 then operation else operation.Substring(0, dot) + "-" + operation.Substring(dot + 1)

        let body =
            CliPort.fill
                [ "requestId", $"req-{slug}-0001"
                  "operation", operation
                  "sha", git root [ "rev-parse"; "HEAD" ]
                  "actor", agent
                  "arguments", arguments ]
                """{"protocol":"praxis.remote","protocolVersion":"1.0","requestId":"{{requestId}}","operation":"{{operation}}","repository":{"ref":"refs/heads/main","expectedSha":"{{sha}}"},"actor":{{actor}},"arguments":{{arguments}}}"""
            |> JsonNode.Parse

        for name, value in extra do
            body[name] <- JsonNode.Parse value

        body

    let private request root operation arguments = requestWith root operation arguments []

    let private remoteWith (root: string) (body: string) (grants: string list) (environment: (string * string) list) (extraArguments: string list) =
        CliPort.withInputFile body (fun file ->
            let result =
                praxis
                    root
                    ([ "remote"; "execute"; "--request"; file ] @ (grants |> List.collect (fun grant -> [ "--grant"; grant ])) @ extraArguments)
                    environment

            { Exit = result.Exit
              Out = result.Out
              Response = JsonNode.Parse result.Out })

    let private remote (root: string) (body: JsonNode) = remoteWith root (CliPort.compact body) allGrants [] []

    let private status (root: string) =
        git root [ "status"; "--porcelain"; "--untracked-files=all" ]

    let private readEvents (root: string) =
        (CliHarness.read root ".ros/events/events.jsonl").Split([| "\r\n"; "\n" |], StringSplitOptions.None)
        |> Array.filter (fun line -> line <> "")
        |> Array.map JsonNode.Parse
        |> Array.toList

    let private executions (root: string) =
        let directory = Path.Combine(root, ".ros", "telemetry", "executions")

        if Directory.Exists directory then
            Directory.GetFiles directory
            |> Array.sortWith (fun a b -> String.CompareOrdinal(Path.GetFileName a, Path.GetFileName b))
            |> Array.map (File.ReadAllText >> JsonNode.Parse)
            |> Array.toList
        else
            []

    let private volatileFields =
        HashSet<string>(
            [ "executionId"; "instanceId"; "startedAt"; "discoveredAt"; "lastAssessedAt"; "recordedAt"; "collectedAt"; "measurementId"
              "commit"; "branch"; "dirtyPaths"; "dirty"; "commits"; "occurredAt"; "eventId"; "updatedAt"; "completedAt"
              "telemetryExecutionIds"; "telemetryExecutions"; "repository" ]
        )

    /// A copy without the fields that legitimately differ between two runs.
    let rec private strip (node: JsonNode) : JsonNode =
        match node with
        | null -> null
        | :? JsonArray as array -> JsonArray(array |> Seq.map strip |> Seq.toArray) :> JsonNode
        | :? JsonObject as record ->
            JsonObject(
                record
                |> Seq.filter (fun entry -> not (volatileFields.Contains entry.Key))
                |> Seq.map (fun entry -> KeyValuePair(entry.Key, strip entry.Value))
            )
            :> JsonNode
        | value -> value.DeepClone()

    let private jsonEqual (message: string) (expected: JsonNode) (actual: JsonNode) =
        if not (JsonNode.DeepEquals(expected, actual)) then
            failwith $"{message}\nExpected JSON: {CliPort.compact expected}\nActual JSON:   {CliPort.compact actual}"

    let private text (node: JsonNode) = CliPort.text node

    let private code (response: JsonNode) = text (response["failure"]["code"])

    let private outcome (response: JsonNode) = text (response["outcome"])

    let private items (node: JsonNode) = CliPort.items node

    let private find (field: string) (value: string) (array: JsonNode) =
        items array |> List.tryFind (fun item -> CliPort.stringOf item field = Some value)

    let private strings (node: JsonNode) = items node |> List.map text

    let private stateOf (root: string) (id: string) =
        CliPort.readJson root ".ros/context/current.json"
        |> fun context -> find "id" id (context["workItems"])
        |> Option.map (fun item -> text (item["state"]))

    let private succeeded (response: JsonNode) =
        if outcome response <> "succeeded" then
            failwith $"Expected a success but received {CliPort.compact response}"

    let private withTemporaries body = CliPort.withTemporaries body

    let tests =
        [ { Name = "remote execute: remote work.start produces the same work state as the local CLI, plus only the remote provenance fields"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let local = optedIn temporary "parity-local"
                      let viaRemote = optedIn temporary "parity-remote"

                      CliPort.exitCode 0 (praxis local [ "work"; "start"; "--id"; "WI-0100"; "--occurred-at"; CliHarness.now () ] agentEnvironment)

                      let result = remote viaRemote (request viaRemote "work.start" """{"workItemIds":["WI-0100"]}""")
                      Assert.equal 0 result.Exit
                      succeeded result.Response

                      jsonEqual
                          "work context"
                          (strip (CliPort.readJson local ".ros/context/current.json"))
                          (strip (CliPort.readJson viaRemote ".ros/context/current.json"))

                      jsonEqual
                          "events"
                          (strip (JsonArray(readEvents local |> List.map (fun event -> event.DeepClone()) |> List.toArray)))
                          (strip (JsonArray(readEvents viaRemote |> List.map (fun event -> event.DeepClone()) |> List.toArray)))

                      let remoteExecution = List.head (executions viaRemote) :?> JsonObject
                      let localExecution = List.head (executions local) :?> JsonObject
                      let executor = (remoteExecution["executor"])
                      let remoteRest = remoteExecution.DeepClone() :?> JsonObject
                      remoteRest.Remove "executor" |> ignore
                      let identity = (remoteRest["identity"]) :?> JsonObject
                      let assurance = (identity["assurance"])
                      identity.Remove "assurance" |> ignore
                      Assert.equal "asserted-by-request" (text assurance)
                      Assert.equal "observed-by-executor" (text (executor["assurance"]))
                      Assert.equal "local" (text (executor["kind"]))
                      Assert.isTrue (not (localExecution.ContainsKey "executor")) "a local execution has no executor"
                      jsonEqual "execution" (strip localExecution) (strip remoteRest)) }

          { Name = "remote execute: remote validate returns exactly the local validation document"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = optedIn temporary "validate"
                      let local = praxis root [ "validate"; "--json" ] []
                      let response = (remote root (request root "validate" "{}")).Response
                      Assert.equal (if local.Exit = 0 then "succeeded" else "failed") (outcome response)
                      jsonEqual "validation result" (JsonNode.Parse local.Out) (response["result"])) }

          { Name = "remote execute: a retry after a lost result replays the recorded outcome even though its own commit moved the ref"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = optedIn temporary "replay"
                      let body = request root "work.start" """{"workItemIds":["WI-0100"]}"""
                      let first = (remote root body).Response
                      succeeded first

                      Assert.isTrue
                          (strings (first["persistence"]["paths"]) |> List.contains ".ros/remote/requests/req-work-start-0001.json")
                          "the journal entry is reported for persistence"

                      // The adapter persisted the state, then the result was lost in transit.
                      commit root "praxis: work.start"
                      let eventsAfterFirst = (readEvents root).Length

                      let retry = remote root body
                      Assert.equal 0 retry.Exit
                      Assert.equal true (CliPort.boolean (retry.Response["replayed"]))
                      let replayed = retry.Response.DeepClone()
                      replayed["replayed"] <- JsonValue.Create false
                      jsonEqual "the replayed response" first replayed
                      Assert.equal "" (status root)
                      Assert.equal eventsAfterFirst (readEvents root).Length
                      Assert.equal 1 (executions root).Length

                      // The same holds when the caller refreshed its expected SHA first.
                      let refreshedBody = body.DeepClone()
                      refreshedBody["repository"]["expectedSha"] <- JsonValue.Create(git root [ "rev-parse"; "HEAD" ])
                      Assert.equal true (CliPort.boolean ((remote root refreshedBody).Response["replayed"]))) }

          { Name = "remote execute: request.status recovers whether a request was recorded without guessing"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = optedIn temporary "status"
                      let statusRequest () = request root "request.status" """{"requestId":"req-work-start-0001"}"""
                      let before = (remote root (statusRequest ())).Response
                      Assert.equal false (CliPort.boolean (before["result"]["recorded"]))

                      remote root (request root "work.start" """{"workItemIds":["WI-0100"]}""") |> ignore
                      let after = (remote root (statusRequest ())).Response
                      Assert.equal true (CliPort.boolean (after["result"]["recorded"]))
                      Assert.equal "succeeded" (text (CliPort.at [ "result"; "response"; "outcome" ] after))) }

          { Name = "remote execute: the same request ID with a different payload fails closed and writes nothing"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = optedIn temporary "conflict"
                      remote root (request root "work.start" """{"workItemIds":["WI-0100"]}""") |> ignore
                      commit root "praxis: work.start"

                      let conflicting = remote root (request root "work.start" """{"workItemIds":["WI-0200"]}""")
                      Assert.equal 1 conflicting.Exit
                      Assert.equal "idempotency-conflict" (code conflicting.Response)
                      Assert.equal "never" (text (conflicting.Response["failure"]["retry"]))
                      Assert.equal "" (status root)) }

          { Name = "remote execute: a stale request, and a dirty working tree, are refused rather than applied to another state"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = optedIn temporary "stale"
                      let body = request root "work.start" """{"workItemIds":["WI-0100"]}"""
                      CliHarness.write root "notes.txt" "moved on\n"
                      commit root "someone else moved the branch"

                      let stale = (remote root body).Response
                      Assert.equal "stale-ref" (code stale)
                      Assert.equal "after-refresh" (text (stale["failure"]["retry"]))
                      Assert.equal "" (status root)

                      CliHarness.write root "notes.txt" "uncommitted\n"
                      let dirty = (remote root (request root "work.start" """{"workItemIds":["WI-0100"]}""")).Response
                      Assert.equal "stale-ref" (code dirty)
                      // Only the pre-existing change remains.
                      Assert.equal "M notes.txt" (status root)) }

          { Name = "remote execute: remote mutation is opt-in per repository and read never implies write"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let optedOut = fixture temporary "opt-out" None
                      let refused = remote optedOut (request optedOut "work.start" """{"workItemIds":["WI-0100"]}""")
                      Assert.equal "unauthorized" (code refused.Response)
                      Assert.equal "" (status optedOut)
                      let validation = (remote optedOut (request optedOut "validate" "{}")).Response
                      Assert.isTrue (isNull (validation["failure"]) || isNull (validation["failure"]["code"])) "a read needs no opt-in"

                      let optedInRoot = optedIn temporary "read-grant"

                      let readOnly =
                          remoteWith optedInRoot (CliPort.compact (request optedInRoot "work.start" """{"workItemIds":["WI-0100"]}""")) [ "read" ] [] []

                      Assert.equal "unauthorized" (code readOnly.Response)

                      let noComplete =
                          remoteWith
                              optedInRoot
                              (CliPort.compact (request optedInRoot "work.complete" """{"workItemIds":["WI-0100"]}"""))
                              [ "read"; "mutate" ]
                              []
                              []

                      Assert.equal "unauthorized" (code noComplete.Response)) }

          { Name = "remote execute: hostile request values are refused before any command runs"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = optedIn temporary "hostile"
                      let start = """{"workItemIds":["WI-0100"]}"""

                      let attempts =
                          [ request root "work.start" """{"workItemIds":["--root=/tmp"]}"""
                            request root "work.start" """{"workItemIds":["WI-0100"],"type":"--help"}"""
                            request root "work.block" """{"workItemIds":["WI-0100"],"reason":"-x"}"""
                            requestWith root "work.start" start [ "command", "\"rm -rf /\"" ]
                            requestWith root "work.start" start [ "operation", "\"shell\"" ] ]

                      for body in attempts do
                          let result = remote root body
                          Assert.equal 1 result.Exit
                          Assert.equal "rejected" (outcome result.Response)
                          let failure = code result.Response

                          Assert.isTrue
                              (List.contains failure [ "invalid-request"; "unsupported-operation" ])
                              $"unexpected failure code {failure}"

                      Assert.equal "" (status root)
                      Assert.equal "invalid-request" (code ((remoteWith root "{not json" allGrants [] []).Response))) }

          { Name = "remote execute: credentials in the executor's environment never reach recorded state"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = optedIn temporary "secrets"

                      let response =
                          (remoteWith
                              root
                              (CliPort.compact (request root "work.start" """{"workItemIds":["WI-0100"]}"""))
                              allGrants
                              [ "GITHUB_TOKEN", plantedToken; "ANTHROPIC_API_KEY", plantedToken; "ROS_ACTOR", "a-previous-agent" ]
                              [])
                              .Response

                      succeeded response

                      for relative in strings (response["persistence"]["paths"]) do
                          let content = CliHarness.read root relative
                          Assert.isTrue (not (content.Contains plantedToken)) $"{relative} leaked a credential"
                          Assert.isTrue (not (content.Contains "a-previous-agent")) $"{relative} inherited a previous actor"

                      let blockArguments = $"""{{"workItemIds":["WI-0100"],"reason":"use {plantedToken}"}}"""

                      let secretInRequest =
                          remote root (requestWith root "work.block" blockArguments [ "requestId", "\"req-secret-0001\"" ])

                      Assert.equal "secret-detected" (code secretInRequest.Response)
                      Assert.isTrue (not (secretInRequest.Out.Contains plantedToken)) "the secret is not echoed") }

          { Name = "remote execute: a mutation that cannot finish leaves nothing behind and claims nothing"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = optedIn temporary "timeout"

                      let response =
                          (remoteWith
                              root
                              (CliPort.compact (request root "work.start" """{"workItemIds":["WI-0100"]}"""))
                              allGrants
                              []
                              [ "--timeout-seconds"; "0" ])
                              .Response

                      Assert.equal "timeout" (code response)
                      Assert.equal "same-request" (text (response["failure"]["retry"]))
                      Assert.isTrue (outcome response <> "succeeded") "a timed-out mutation does not succeed"
                      Assert.empty (strings (response["persistence"]["paths"]))
                      // Anything the interrupted command wrote was undone.
                      Assert.equal "" (status root)
                      Assert.isTrue (not (File.Exists(Path.Combine(root, ".ros", "remote", "requests", "req-work-start-0001.json")))) "no journal entry") }

          { Name = "remote execute: a domain refusal is reported as the command's own refusal and writes nothing"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = optedIn temporary "domain"
                      remote root (request root "work.start" """{"workItemIds":["WI-0100"]}""") |> ignore
                      commit root "praxis: work.start"

                      let again =
                          (remote root (requestWith root "work.start" """{"workItemIds":["WI-0100"]}""" [ "requestId", "\"req-start-again-0001\"" ]))
                              .Response

                      Assert.equal "domain-rejected" (code again)
                      Assert.equal "praxis" (text (again["failure"]["decidedBy"]))
                      CliPort.matches "cannot begin 'WI-0100' from 'active'" (text (again["failure"]["message"]))
                      Assert.equal "" (status root)) }

          { Name = "remote execute: an unidentified requester is recorded as unknown, never as the executor"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = optedIn temporary "unknown-actor"
                      let anonymous = request root "work.start" """{"workItemIds":["WI-0100"]}""" :?> JsonObject
                      anonymous.Remove "actor" |> ignore

                      let response =
                          (remoteWith
                              root
                              (CliPort.compact anonymous)
                              allGrants
                              [ "GITHUB_ACTIONS", "true"
                                "GITHUB_RUN_ID", "4242"
                                "GITHUB_RUN_ATTEMPT", "1"
                                "GITHUB_ACTOR", "octocat"
                                "GITHUB_TRIGGERING_ACTOR", "octocat" ]
                              [])
                              .Response

                      succeeded response
                      Assert.equal "github-actions" (text (response["executor"]["kind"]))
                      Assert.equal "4242" (text (response["executor"]["runId"]))
                      Assert.equal "github:octocat" (text (response["executor"]["principal"]))

                      let execution = List.head (executions root)
                      let identity = (execution["identity"]) :?> JsonObject
                      Assert.equal "unknown" (text (identity["actorKind"]))
                      Assert.equal "unknown" (text (identity["provider"]))
                      Assert.isTrue (identity.ContainsKey "runId" && isNull (identity["runId"])) "the requester's run ID is null"
                      Assert.equal "4242" (text (execution["executor"]["runId"]))
                      Assert.equal "unknown" (text (CliPort.at [ "actor"; "kind" ] (List.last (readEvents root))))) }

          { Name = "remote execute: praxis.describe tells an agent what it may do here, from Praxis's own catalog and state"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = fixture temporary "describe" (Some [ "read"; "mutate" ])

                      let response =
                          (remoteWith root (CliPort.compact (request root "praxis.describe" "{}")) [ "read"; "mutate"; "complete" ] [] [])
                              .Response

                      succeeded response
                      let described = (response["result"])
                      Assert.equal "praxis.describe" (text (described["schema"]))
                      Assert.equal true (CliPort.boolean (described["available"]))
                      Assert.equal [ "1.0"; "1.1"; "1.2"; "1.3"; "1.4" ] (strings (described["protocolVersions"]))
                      Assert.equal "docs/remote-agent-contract.md" (text (described["contract"]))
                      Assert.equal [ "read"; "mutate" ] (strings (described["repository"]["capabilities"]))
                      // The transport grant is narrowed by the repository.
                      Assert.equal [ "read"; "mutate" ] (strings (described["grants"]))
                      Assert.equal (git root [ "rev-parse"; "HEAD" ]) (text (described["repository"]["sha"]))

                      let start =
                          find "operation" "work.start" (described["operations"])
                          |> Option.defaultWith (fun () -> failwith "work.start is described")

                      CliPort.deepEqual
                          """{"operation":"work.start","capability":"mutate","mutating":true,"requiresExpectedSha":true,"requiresExecution":false,"introducedIn":"1.0","requiredArguments":["workItemIds"],"optionalArguments":["type","classifications"]}"""
                          start

                      Assert.isTrue (find "id" "WI-0100" (described["readyWork"]) |> Option.isSome) "ready work is discoverable"
                      // A repository without the adapter says so.
                      Assert.empty (items (described["transports"]))

                      let local = praxis root [ "remote"; "describe" ] []
                      Assert.equal 0 local.Exit
                      jsonEqual "local and remote discovery agree" (described["operations"]) (CliPort.at [ "operations" ] (JsonNode.Parse local.Out))) }

          { Name = "remote execute: AGENTS.md routes agents without a runtime to the contract without embedding scripts"
            Run =
              fun () ->
                  let repository = CliPort.repositoryRoot.Value
                  let agents = File.ReadAllText(Path.Combine(repository, "AGENTS.md"))
                  let parts = agents.Split("## No local runtime? Use remote execution")
                  Assert.isTrue (parts.Length > 1) "AGENTS.md has the remote-execution routing section"
                  let section = (parts[1]).Split("\n## ")[0]
                  CliPort.matches @"docs/remote-agent-contract\.md" section

                  if Text.RegularExpressions.Regex.IsMatch(section, "```|gh workflow run|curl ") then
                      failwith "no scripts in AGENTS.md"

                  for document in [ "docs/remote-agent-contract.md"; "docs/remote-protocol.md" ] do
                      Assert.isTrue (File.Exists(Path.Combine(repository, document))) document

                      let manifest =
                          CliPort.compact (JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "starter", "greenfield", "manifest.json"))))

                      Assert.isTrue (manifest.Contains $"\"{document}\"") $"{document} ships with the scaffold" }

          { Name = "remote execute: a successor agent continues in its own execution, linked to its predecessor, never impersonating it"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = optedIn temporary "successor"

                      let agentA =
                          """{"kind":"agent","id":"example/agent-a","provider":"example","runtime":"cloud-a","sessionId":"session-a"}"""

                      let agentB =
                          """{"kind":"agent","id":"other/agent-b","provider":"other","runtime":"cloud-b","sessionId":"session-b"}"""

                      let asActor actor requestId operation arguments extra =
                          (remote root (requestWith root operation arguments ([ "requestId", $"\"{requestId}\""; "actor", actor ] @ extra)))
                              .Response

                      succeeded (asActor agentA "req-a-start-001" "work.start" """{"workItemIds":["WI-0100"]}""" [])
                      commit root "praxis: A starts"

                      succeeded (
                          asActor agentA "req-a-block-001" "work.block" """{"workItemIds":["WI-0100"],"reason":"handoff to another agent"}""" []
                      )

                      commit root "praxis: A blocks"
                      let predecessor = List.head (executions root)
                      let predecessorId = text (predecessor["executionId"])

                      succeeded (asActor agentB "req-b-resume-01" "work.resume" """{"workItemIds":["WI-0100"]}""" [])
                      commit root "praxis: B resumes"

                      let all = executions root
                      Assert.equal 2 all.Length
                      let successor = all |> List.find (fun execution -> text (execution["executionId"]) <> predecessorId)
                      let successorId = text (successor["executionId"])
                      Assert.equal "other/agent-b" (text (successor["identity"]["agentId"]))
                      // Continuation names its predecessor.
                      Assert.equal predecessorId (text (successor["identity"]["parentExecutionId"]))
                      let predecessorNow = all |> List.find (fun execution -> text (execution["executionId"]) = predecessorId)
                      // The predecessor's identity is untouched.
                      Assert.equal "example/agent-a" (text (predecessorNow["identity"]["agentId"]))
                      Assert.equal "other/agent-b" (text (CliPort.at [ "actor"; "id" ] (List.last (readEvents root))))

                      // B may not record into A's execution, remotely or by naming it.
                      let intrusion =
                          asActor
                              agentB
                              "req-b-telemetry-1"
                              "telemetry.record"
                              """{"metric":"tokens.input","value":10}"""
                              [ "execution", $"""{{"id":"{predecessorId}"}}""" ]

                      Assert.equal "domain-rejected" (code intrusion)
                      CliPort.matches "belongs to another actor or run" (text (intrusion["failure"]["message"]))
                      Assert.equal "" (status root)

                      let own =
                          asActor
                              agentB
                              "req-b-telemetry-2"
                              "telemetry.record"
                              """{"metric":"tokens.input","value":10,"unit":"tokens"}"""
                              [ "execution", $"""{{"id":"{successorId}"}}""" ]

                      succeeded own

                      let recorded =
                          executions root |> List.find (fun execution -> text (execution["executionId"]) = successorId)

                      let measurement =
                          find "id" "tokens.input" (recorded["metrics"]) |> Option.defaultWith (fun () -> failwith "tokens.input recorded")

                      Assert.equal 10.0 (CliPort.number (measurement["value"]))
                      // Remotely supplied telemetry is labelled as reported, not observed.
                      Assert.equal "agent-report" (text (measurement["source"]["type"]))) }

          { Name = "remote execute: remote reconciliation (#80) attributes already-committed work without touching it and keeps three identities apart"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = optedIn temporary "reconcile"
                      CliHarness.write root "src/feature.txt" "committed before any work item was active\n"
                      git root [ "add"; "src/feature.txt" ] |> ignore

                      git root [ "-c"; "user.name=Original Author"; "-c"; "user.email=author@example.invalid"; "commit"; "-qm"; "feature" ]
                      |> ignore

                      let featureCommit = git root [ "rev-parse"; "HEAD" ]
                      let before = CliHarness.read root "src/feature.txt"

                      let reconcileArguments =
                          $"""{{"workItemId":"WI-0100","reason":"committed before the work item was begun","commits":["{featureCommit}"]}}"""

                      let response = (remote root (request root "work.reconcile" reconcileArguments)).Response
                      succeeded response
                      Assert.equal before (CliHarness.read root "src/feature.txt")

                      Assert.isTrue
                          (strings (response["persistence"]["paths"]) |> List.forall (fun relative -> relative.StartsWith ".ros/"))
                          "only Praxis-owned state is reported"

                      let event =
                          readEvents root
                          |> List.find (fun candidate -> text (candidate["type"]) = "work.attribution.reconciled")

                      Assert.equal "WI-0100" (text (event["workItem"]))
                      Assert.equal "post-hoc" (text (event["attribution"]))
                      // The reconciliation actor is the requester.
                      Assert.equal agentId (text (event["actor"]["id"]))
                      // The change author is preserved.
                      Assert.equal "Original Author" (text (CliPort.at [ "author"; "name" ] (CliPort.items (CliPort.at [ "gitEvidence"; "commits" ] event)).Head))
                      Assert.isTrue (strings (event["paths"]) |> List.contains "src/feature.txt") "the reconciled path is attributed"

                      commit root "praxis: reconcile"

                      let duplicate =
                          (remote root (requestWith root "work.reconcile" reconcileArguments [ "requestId", "\"req-reconcile-again\"" ]))
                              .Response

                      // A duplicate reconciliation is an idempotent no-op.
                      succeeded duplicate

                      Assert.equal
                          1
                          (readEvents root |> List.filter (fun candidate -> text (candidate["type"]) = "work.attribution.reconciled")).Length) }

          { Name = "remote execute: an agent records steps and step-scoped usage remotely; usage keeps its evidence quality"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = optedIn temporary "steps"

                      let sessionAgent =
                          """{"kind":"agent","id":"example/cloud-agent","provider":"example","runtime":"cloud-agent","sessionId":"agent-session"}"""

                      let v11 operation arguments (extra: (string * string) list) =
                          requestWith root operation arguments ([ "protocolVersion", "\"1.1\"" ] @ extra)

                      let started =
                          (remote root (v11 "work.start" """{"workItemIds":["WI-0100"]}""" [ "requestId", "\"req-steps-start-1\""; "actor", sessionAgent ]))
                              .Response

                      succeeded started
                      commit root "start"

                      let executionId =
                          find "id" "WI-0100" (started["result"]["workItems"])
                          |> Option.map (fun item -> text (item["telemetryExecutionIds"][0]))
                          |> Option.defaultWith (fun () -> failwith "WI-0100 is in the result")

                      let own requestId =
                          [ "requestId", $"\"{requestId}\""; "actor", sessionAgent; "execution", $"""{{"id":"{executionId}"}}""" ]

                      succeeded (remote root (v11 "step.start" """{"stepId":"implement","name":"Implement parser"}""" (own "req-step-start-1"))).Response
                      commit root "step"

                      let usage =
                          (remote
                              root
                              (v11
                                  "telemetry.record"
                                  """{"metric":"tokens.input","value":1200,"unit":"tokens","step":"implement","sourceType":"runtime-api"}"""
                                  (own "req-step-usage-1")))
                              .Response

                      succeeded usage
                      commit root "usage"

                      let unknownStep =
                          (remote root (v11 "telemetry.record" """{"metric":"tokens.input","value":1,"step":"never-started"}""" (own "req-step-usage-2")))
                              .Response

                      Assert.equal "domain-rejected" (code unknownStep)
                      succeeded (remote root (v11 "step.complete" """{"stepId":"implement"}""" (own "req-step-done-01"))).Response
                      commit root "step done"

                      let execution = List.head (executions root)

                      let steps =
                          items (execution["events"])
                          |> List.filter (fun event -> (text (event["type"])).StartsWith "step.")
                          |> List.map (fun event -> text (event["type"]), text (event["stepId"]))

                      Assert.equal [ "step.started", "implement"; "step.completed", "implement" ] steps

                      let report = JsonNode.Parse((praxis root [ "telemetry"; "usage"; "WI-0100"; "--by"; "step" ] []).Out)

                      let group =
                          items (report["groups"])
                          |> List.find (fun candidate -> text (candidate["key"]) = "implement" && text (candidate["metric"]) = "tokens.input")

                      Assert.equal 1200.0 (CliPort.number (group["total"]))
                      CliPort.deepEqual """{"provider-reported":1}""" (group["evidenceQuality"])

                      let intruder = """{"kind":"agent","id":"other/agent","provider":"other","runtime":"x"}"""

                      let refused =
                          (remote
                              root
                              (v11
                                  "step.start"
                                  """{"stepId":"sneak"}"""
                                  [ "requestId", "\"req-step-sneak-1\""; "actor", intruder; "execution", $"""{{"id":"{executionId}"}}""" ]))
                              .Response

                      Assert.equal "domain-rejected" (code refused)) }

          { Name = "remote execute: an ordered batch runs each constituent with its own identity and outcome, and a partial batch is unambiguous"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = optedIn temporary "batch"
                      praxisOk root [ "add"; "Second item"; "--id"; "WI-0200" ]
                      commit root "second item captured, not ready"

                      let batch (requestId: string) (requests: string) =
                          requestWith root "batch" $"""{{"requests":{requests}}}""" [ "requestId", $"\"{requestId}\""; "protocolVersion", "\"1.2\"" ]

                      let firstBatch =
                          """[{"requestId":"req-batch-part-1","operation":"work.start","arguments":{"workItemIds":["WI-0100"]}},{"requestId":"req-batch-part-2","operation":"work.start","arguments":{"workItemIds":["WI-0200"]}},{"requestId":"req-batch-part-3","operation":"work.block","arguments":{"workItemIds":["WI-0100"],"reason":"never runs"}}]"""

                      let partial = remote root (batch "req-batch-0001" firstBatch)
                      let response = partial.Response
                      Assert.equal 1 partial.Exit
                      Assert.equal "rejected" (outcome response)
                      Assert.equal "domain-rejected" (code response)
                      CliPort.matches "constituent 'req-batch-part-2'" (text (response["failure"]["message"]))
                      Assert.equal 1.0 (CliPort.number (response["result"]["completed"]))
                      Assert.equal "req-batch-part-2" (text (response["result"]["stoppedAt"]))
                      Assert.equal [ "req-batch-part-3" ] (strings (response["result"]["notRun"]))

                      Assert.equal
                          [ "req-batch-part-1", "succeeded"; "req-batch-part-2", "rejected" ]
                          (items (response["result"]["responses"]) |> List.map (fun part -> text (part["requestId"]), outcome part))

                      // The accepted constituent is kept and reported for persistence;
                      // the refused one left nothing behind.
                      let paths = strings (response["persistence"]["paths"])
                      Assert.isTrue (List.contains ".ros/remote/requests/req-batch-part-1.json" paths) "the accepted constituent is journalled"
                      Assert.isTrue (List.contains ".ros/remote/requests/req-batch-0001.json" paths) "the batch is journalled"
                      Assert.isTrue (not (List.contains ".ros/remote/requests/req-batch-part-2.json" paths)) "the refused constituent is not journalled"
                      Assert.equal (Some "active") (stateOf root "WI-0100")
                      Assert.equal None (stateOf root "WI-0200")
                      commit root "praxis: partial batch"

                      // Retrying the same batch replays its recorded outcome.
                      let replay = (remote root (batch "req-batch-0001" firstBatch)).Response
                      Assert.equal true (CliPort.boolean (replay["replayed"]))
                      Assert.equal "" (status root)

                      // A new batch may reuse an already-recorded constituent: it replays.
                      let next =
                          (remote
                              root
                              (batch
                                  "req-batch-0002"
                                  """[{"requestId":"req-batch-part-1","operation":"work.start","arguments":{"workItemIds":["WI-0100"]}},{"requestId":"req-batch-part-4","operation":"work.block","arguments":{"workItemIds":["WI-0100"],"reason":"handoff"}}]"""))
                              .Response

                      succeeded next
                      let parts = items (next["result"]["responses"])
                      Assert.equal true (CliPort.boolean (parts[0]["replayed"]))
                      Assert.equal "succeeded" (outcome (parts[1]))
                      Assert.equal (Some "blocked") (stateOf root "WI-0100")

                      // The replayed start did not run twice.
                      Assert.equal
                          1
                          (readEvents root
                           |> List.filter (fun event -> CliPort.stringOf event "workItem" = Some "WI-0100" && text (event["type"]) = "work.started")
                           |> List.length)) }

          { Name = "remote execute: the request journal is Praxis bookkeeping and never an unattributed change"
            Run =
              fun () ->
                  withTemporaries (fun temporary ->
                      let root = fixture temporary "journal-ignored" None
                      let config = CliPort.readJson root "ros.json"
                      let ignored = strings (config["workProtocol"]["ignoredPaths"])
                      Assert.isTrue (ignored |> List.contains ".ros/remote/**") "the scaffold ignores the journal for attribution"

                      // A journal entry committed by an adapter, with no work item active.
                      CliHarness.write root ".ros/remote/requests/req-landed-0001.json" "{}\n"
                      let result = praxis root [ "validate"; "--json" ] []

                      let findings =
                          items ((JsonNode.Parse result.Out)["findings"])
                          |> List.filter (fun finding -> text (finding["path"]) |> fun path -> path.StartsWith ".ros/remote")

                      Assert.isTrue findings.IsEmpty (findings |> List.map CliPort.compact |> String.concat ", ")) } ]
