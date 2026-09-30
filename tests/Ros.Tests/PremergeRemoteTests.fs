namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes

/// praxis.remote 1.3 behavior main added after PR #92 diverged, ported from
/// main's Node suites (tests/remote-checkpoint.test.mjs and the takeover case
/// of tests/remote-execute.test.mjs) so it survives PR #92's removal of
/// Node. Every request goes through `remote execute`, the same command a
/// cloud runner executes (PRAXIS-PR92-PREMERGE-REGRESSION-FENCE).
[<RequireQualifiedAccess>]
module PremergeRemoteTests =
    open PremergeFence

    let private agentOne = actor "example/cloud-agent-one" "example" "cloud-agent" "cloud-session-1"
    let private agentTwo = actor "other/cloud-agent-two" "other" "cloud-agent" "cloud-session-2"
    let private agentThree = actor "third/cloud-agent-three" "third" "cloud-agent" "cloud-session-3"

    type private Fixture =
        { Executor: string
          AgentClone: string
          Bare: string }

    /// An executor checkout with a bare "GitHub" remote, and a separate clone
    /// standing in for a cloud agent that changes code only by pushing.
    let private fixture label =
        let parent = temporaryDirectory label
        let bare = Path.Combine(parent, "github.git")
        let executor = Path.Combine(parent, "runner")
        git parent [ "init"; "-q"; "--bare"; "-b"; "main"; bare ] |> ignore
        Directory.CreateDirectory executor |> ignore
        git executor [ "init"; "-q"; "-b"; "main" ] |> ignore
        configureGitIdentity executor
        cli executor [ "init"; "--project"; "Remote Checkpoint" ] |> ok |> ignore

        editConfig executor (fun config ->
            config.["remote"] <- JsonNode.Parse """{ "capabilities": ["read", "mutate", "complete"] }"""
            (config.["workProtocol"] :?> JsonObject).["continuity"] <- JsonNode.Parse """{ "requireDurableCheckpoint": true }""")

        cli executor [ "add"; "Remote item"; "--id"; "WI-0100" ] |> ok |> ignore
        cli executor [ "work"; "backlog-transition"; "--action"; "ready"; "--id"; "WI-0100"; "--occurred-at"; now () ] |> ok |> ignore
        commitAll executor "baseline" |> ignore
        git executor [ "remote"; "add"; "origin"; bare ] |> ignore
        git executor [ "push"; "-q"; "-u"; "origin"; "main" ] |> ignore
        let agentClone = Path.Combine(parent, "agent-clone")
        git parent [ "clone"; "-q"; bare; agentClone ] |> ignore
        configureGitIdentity agentClone

        { Executor = executor
          AgentClone = agentClone
          Bare = bare }

    let private actorNode (who: Actor) =
        JsonNode.Parse(
            $$"""{ "kind": "{{who.Kind}}", "id": "{{who.Id}}", "provider": "{{who.Provider}}", "runtime": "{{who.Runtime}}", "sessionId": "{{who.Session}}" }"""
        )

    /// A praxis.remote request against the executor's current HEAD unless
    /// `expectedSha` pins another commit.
    let private request
        (executor: string)
        (operation: string)
        (arguments: string)
        (requestId: string)
        (who: Actor option)
        (execution: string option)
        (protocolVersion: string)
        (expectedSha: string option)
        =
        let body = JsonObject()
        body.["protocol"] <- JsonValue.Create "praxis.remote"
        body.["protocolVersion"] <- JsonValue.Create protocolVersion
        body.["requestId"] <- JsonValue.Create requestId
        body.["operation"] <- JsonValue.Create operation

        body.["repository"] <-
            JsonNode.Parse(
                $$"""{ "ref": "refs/heads/main", "expectedSha": "{{expectedSha |> Option.defaultWith (fun () -> git executor [ "rev-parse"; "HEAD" ])}}" }"""
            )

        who |> Option.iter (fun value -> body.["actor"] <- actorNode value)
        execution |> Option.iter (fun id -> body.["execution"] <- JsonNode.Parse $$"""{ "id": "{{id}}" }""")
        body.["arguments"] <- JsonNode.Parse arguments
        body

    let private current executor operation arguments requestId who execution =
        request executor operation arguments requestId who execution "1.3" None

    /// Executes a request exactly as the runner does and returns its response.
    let private remote (executor: string) (body: JsonObject) =
        let file = Path.Combine(Path.GetTempPath(), $"praxis-fence-request-{Guid.NewGuid():N}.json")
        File.WriteAllText(file, body.ToJsonString())

        try
            let result = cli executor [ "remote"; "execute"; "--request"; file; "--grant"; "read"; "--grant"; "mutate"; "--grant"; "complete" ]
            JsonNode.Parse(result.Output) :?> JsonObject
        finally
            File.Delete file

    let private succeeded (response: JsonObject) =
        Assert.isTrue (text response.["outcome"] = "succeeded") $"expected success: {response.ToJsonString()}"
        response

    /// The adapter's persistence step: commit exactly the reported Praxis
    /// paths and push without force.
    let private persist (executor: string) (response: JsonObject) =
        let paths = strings response.["persistence"].["paths"]
        Assert.isTrue (not paths.IsEmpty) "a succeeded mutation reports what to persist"
        paths |> List.iter (fun path -> Assert.isTrue (path.StartsWith(".ros/", StringComparison.Ordinal)) $"the adapter would refuse {path}")

        git executor ("add" :: "--" :: (paths |> List.filter (fun path -> File.Exists(Path.Combine(executor, path)))))
        |> ignore

        git executor [ "commit"; "-qm"; $"""praxis: remote {text response.["operation"]} ({text response.["requestId"]})""" ] |> ignore
        git executor [ "push"; "-q"; "origin"; "HEAD:main" ] |> ignore

    /// The cloud agent changes code only through the Git host; the next run
    /// checks out the new head.
    let private agentPushes (fixture: Fixture) (file: string) (content: string) =
        git fixture.AgentClone [ "pull"; "-q"; "--ff-only" ] |> ignore
        write fixture.AgentClone file content
        commitAll fixture.AgentClone $"agent: {file}" |> ignore
        git fixture.AgentClone [ "push"; "-q"; "origin"; "HEAD:main" ] |> ignore
        git fixture.Executor [ "pull"; "-q"; "--ff-only" ] |> ignore
        git fixture.Executor [ "rev-parse"; "HEAD" ]

    let private firstExecution executor =
        (workItem executor "WI-0100").["telemetryExecutionIds"] |> strings |> List.head

    let private checkpointArguments summary nextAction =
        $$"""{ "workItemId": "WI-0100", "summary": "{{summary}}", "nextAction": "{{nextAction}}" }"""

    let private evidence =
        """{ "workItemIds": ["WI-0100"], "evidence": [{ "type": "implementation", "path": "src/feature.txt" }, { "type": "tests", "path": "README.md" }] }"""

    let private started (fixture: Fixture) =
        current fixture.Executor "work.start" """{ "workItemIds": ["WI-0100"], "type": "feature" }""" "req-start-0001" (Some agentOne) None
        |> remote fixture.Executor
        |> succeeded
        |> persist fixture.Executor

        firstExecution fixture.Executor

    let private failureCode (response: JsonObject) = text response.["failure"].["code"]

    let tests =
        [ { Name = "fence remote 1.3: describe publishes work.checkpoint and work.continue with requiresExecution and introducedIn"
            Run =
              fun () ->
                  let fixture = fixture "describe"

                  let described =
                      request fixture.Executor "praxis.describe" "{}" "req-describe-0001" None None "1.0" None
                      |> remote fixture.Executor
                      |> succeeded

                  let result = described.["result"]
                  Assert.equal [ "1.0"; "1.1"; "1.2"; "1.3" ] (strings result.["protocolVersions"])

                  let byName name =
                      result.["operations"] |> array |> List.find (fun operation -> text operation.["operation"] = name)

                  let checkpoint = byName "work.checkpoint"
                  Assert.equal [ "workItemId"; "summary"; "nextAction" ] (strings checkpoint.["requiredArguments"])
                  Assert.equal [ "stepId" ] (strings checkpoint.["optionalArguments"])
                  Assert.equal "mutate" (text checkpoint.["capability"])
                  Assert.equal true (boolean checkpoint.["requiresExecution"])
                  Assert.equal "1.3" (text checkpoint.["introducedIn"])
                  Assert.equal false (boolean ((byName "work.continue").["requiresExecution"]))
                  Assert.equal [ "unrecoverableReason" ] (strings (byName "work.block").["optionalArguments"])
                  let start = byName "work.start"
                  Assert.equal false (boolean start.["requiresExecution"])
                  Assert.equal "1.0" (text start.["introducedIn"]) }

          { Name = "fence remote 1.3: a 1.2 request cannot checkpoint, and a checkpoint must name the requester's own execution"
            Run =
              fun () ->
                  let fixture = fixture "old-checkpoint"

                  let old =
                      request fixture.Executor "work.checkpoint" (checkpointArguments "s" "n") "req-old-checkpoint-1" (Some agentOne) None "1.2" None
                      |> remote fixture.Executor

                  Assert.equal "unsupported-operation" (failureCode old)

                  let unnamed =
                      current fixture.Executor "work.checkpoint" (checkpointArguments "s" "n") "req-unnamed-checkpoint-1" (Some agentOne) None
                      |> remote fixture.Executor

                  Assert.equal "invalid-request" (failureCode unnamed)

                  Assert.isTrue
                      (unnamed.["failure"].["problems"] |> array |> List.exists (fun problem -> text problem.["field"] = "execution.id"))
                      "the problem names execution.id" }

          { Name = "fence remote 1.3: cloud agents checkpoint, hand off, continue and complete through praxis.remote"
            Run =
              fun () ->
                  let fixture = fixture "handoff"
                  let executor = fixture.Executor
                  let executionOne = started fixture
                  let codeSha = agentPushes fixture "src/feature.txt" "part one\n"

                  let checkpointRequest =
                      current executor "work.checkpoint" (checkpointArguments "Part one implemented" "Implement part two") "req-checkpoint-0001" (Some agentOne) (Some executionOne)

                  let checkpointed = remote executor checkpointRequest |> succeeded
                  Assert.equal "recorded" (text checkpointed.["result"].["status"])
                  Assert.equal codeSha (text checkpointed.["result"].["checkpoint"].["commit"])
                  Assert.equal codeSha (text checkpointed.["result"].["checkpoint"].["remoteCommit"])
                  Assert.equal executionOne (text checkpointed.["result"].["checkpoint"].["executionId"])
                  persist executor checkpointed

                  // Retrying the same intent replays; nothing is recorded twice.
                  checkpointRequest.["repository"].["expectedSha"] <- JsonValue.Create(git executor [ "rev-parse"; "HEAD" ])
                  let replay = remote executor checkpointRequest
                  Assert.equal true (boolean replay.["replayed"])

                  let checkpointEvents =
                      events executor |> List.filter (fun event -> text event.["type"] = "work.checkpointed") |> List.length

                  Assert.equal 1 checkpointEvents

                  // A request formed against an old commit is refused.
                  let stale =
                      request executor "work.checkpoint" (checkpointArguments "again" "n") "req-checkpoint-stale-1" (Some agentOne) (Some executionOne) "1.3" (Some codeSha)
                      |> remote executor

                  Assert.equal "stale-ref" (failureCode stale)
                  Assert.equal "after-refresh" (text stale.["failure"].["retry"])

                  // Agent one disappears; agent two reads the durable state remotely.
                  let read =
                      current executor "work.context" """{ "workItemId": "WI-0100" }""" "req-context-0001" (Some agentTwo) None
                      |> remote executor
                      |> succeeded

                  let continuity = read.["result"].["continuity"].[0]
                  Assert.equal codeSha (text continuity.["checkpoint"].["commit"])
                  Assert.equal "Implement part two" (text continuity.["checkpoint"].["nextAction"])
                  Assert.equal "current" (text continuity.["freshness"])

                  // Agent two continues under a new execution of its own.
                  let continued =
                      current executor "work.continue" """{ "workItemId": "WI-0100" }""" "req-continue-0001" (Some agentTwo) None
                      |> remote executor
                      |> succeeded

                  let executionTwo = text continued.["result"].["executionId"]
                  Assert.isTrue (executionTwo <> executionOne) "the successor has its own execution"
                  Assert.equal executionOne (text continued.["result"].["predecessor"].["executionId"])
                  Assert.equal "interrupted" (text continued.["result"].["predecessor"].["disposition"])
                  persist executor continued

                  let finalSha = agentPushes fixture "src/feature.txt" "part one\npart two\n"

                  let finalCheckpoint =
                      current executor "work.checkpoint" (checkpointArguments "Part two implemented" "Run final completion transition") "req-checkpoint-0002" (Some agentTwo) (Some executionTwo)
                      |> remote executor
                      |> succeeded

                  Assert.equal finalSha (text finalCheckpoint.["result"].["checkpoint"].["commit"])
                  Assert.equal executionTwo (text finalCheckpoint.["result"].["checkpoint"].["executionId"])
                  persist executor finalCheckpoint

                  current executor "work.complete" evidence "req-complete-0001" (Some agentTwo) None
                  |> remote executor
                  |> succeeded
                  |> persist executor

                  let item = workItem executor "WI-0100"
                  Assert.equal "complete" (text item.["semanticState"])
                  Assert.equal finalSha (text item.["latestCheckpoint"].["commit"])
                  git fixture.Bare [ "merge-base"; "--is-ancestor"; finalSha; "main" ] |> ignore
                  cli executor [ "validate" ] |> ok |> ignore }

          { Name = "fence remote GH-113: completion may not finalize another agent's active execution, named or not; the successor continues first"
            Run =
              fun () ->
                  let fixture = fixture "ownership"
                  let executor = fixture.Executor
                  let executionOne = started fixture
                  agentPushes fixture "src/feature.txt" "done\n" |> ignore

                  current executor "work.checkpoint" (checkpointArguments "Done" "Complete") "req-checkpoint-0001" (Some agentOne) (Some executionOne)
                  |> remote executor
                  |> succeeded
                  |> persist executor

                  let unnamed = current executor "work.complete" evidence "req-complete-unnamed-1" (Some agentTwo) None |> remote executor
                  Assert.equal "rejected" (text unnamed.["outcome"])
                  Assert.equal "domain-rejected" (failureCode unnamed)
                  Assert.equal "never" (text unnamed.["failure"].["retry"])

                  contains
                      $"active execution '{executionOne}', which belongs to another actor or run; take the work over with work.continue"
                      (text unnamed.["failure"].["message"])
                      "unnamed completion refusal"

                  Assert.equal [ "arguments.workItemIds" ] (unnamed.["failure"].["problems"] |> array |> List.map (fun problem -> text problem.["field"]))

                  let named = current executor "work.complete" evidence "req-complete-named-1" (Some agentTwo) (Some executionOne) |> remote executor
                  Assert.equal "domain-rejected" (failureCode named)
                  contains "belongs to another actor or run; continue in your own execution" (text named.["failure"].["message"]) "named completion refusal"
                  Assert.equal "" (git executor [ "status"; "--porcelain" ])
                  Assert.equal "active" (text (workItem executor "WI-0100").["semanticState"])

                  // The same agent in another session is another run.
                  let otherRun =
                      current executor "work.complete" evidence "req-complete-rerun-1" (Some { agentOne with Session = "cloud-session-9" }) None
                      |> remote executor

                  Assert.equal "domain-rejected" (failureCode otherRun)

                  let continued =
                      current executor "work.continue" """{ "workItemId": "WI-0100" }""" "req-continue-0001" (Some agentTwo) None
                      |> remote executor
                      |> succeeded

                  let executionTwo = text continued.["result"].["executionId"]
                  Assert.equal executionOne (text continued.["result"].["predecessor"].["executionId"])
                  persist executor continued

                  current executor "work.checkpoint" (checkpointArguments "Taken over" "Complete") "req-checkpoint-0002" (Some agentTwo) (Some executionTwo)
                  |> remote executor
                  |> succeeded
                  |> persist executor

                  // The handed-over predecessor no longer blocks, but the successor's live execution does, for anyone else.
                  let intruder = current executor "work.complete" evidence "req-complete-third-1" (Some agentThree) None |> remote executor
                  Assert.equal "domain-rejected" (failureCode intruder)
                  contains $"active execution '{executionTwo}'" (text intruder.["failure"].["message"]) "intruder refusal"
                  Assert.equal "" (git executor [ "status"; "--porcelain" ])

                  current executor "work.complete" evidence "req-complete-0001" (Some agentTwo) None
                  |> remote executor
                  |> succeeded
                  |> persist executor

                  Assert.equal "complete" (text (workItem executor "WI-0100").["semanticState"])
                  cli executor [ "validate" ] |> ok |> ignore }

          { Name = "fence remote GH-113: the owner of the only active execution still completes without naming it"
            Run =
              fun () ->
                  let fixture = fixture "owner"
                  let executor = fixture.Executor
                  let executionOne = started fixture
                  agentPushes fixture "src/feature.txt" "done\n" |> ignore

                  current executor "work.checkpoint" (checkpointArguments "Done" "Complete") "req-checkpoint-0001" (Some agentOne) (Some executionOne)
                  |> remote executor
                  |> succeeded
                  |> persist executor

                  current executor "work.complete" evidence "req-complete-0001" (Some agentOne) None
                  |> remote executor
                  |> succeeded
                  |> persist executor

                  let record = readJson (Path.Combine(executor, ".ros", "telemetry", "executions", $"{executionOne}.json"))
                  Assert.equal "finalized" (text record.["status"]) }

          { Name = "fence remote: a successor takes over a predecessor that left without blocking; resume is refused until it blocks for the handoff"
            Run =
              fun () ->
                  let fixture = fixture "takeover"
                  let executor = fixture.Executor
                  let sessionA = actor "example/agent-a" "example" "cloud-a" "session-a"
                  let sessionB = { sessionA with Session = "session-b" }

                  current executor "work.start" """{ "workItemIds": ["WI-0100"] }""" "req-a-start-001" (Some sessionA) None
                  |> remote executor
                  |> succeeded
                  |> ignore

                  commitAll executor "praxis: A starts, then its session ends" |> ignore
                  let predecessor = firstExecution executor

                  let refused = current executor "work.resume" """{ "workItemIds": ["WI-0100"] }""" "req-b-resume-01" (Some sessionB) None |> remote executor
                  Assert.equal "domain-rejected" (failureCode refused)
                  Assert.equal "" (git executor [ "status"; "--porcelain" ])

                  current executor "work.block" """{ "workItemIds": ["WI-0100"], "reason": "predecessor session-a ended without a handoff; taking over" }""" "req-b-block-01" (Some sessionB) None
                  |> remote executor
                  |> succeeded
                  |> ignore

                  commitAll executor "praxis: B blocks for the handoff" |> ignore
                  let blocked = events executor |> List.last
                  Assert.equal "work.blocked" (text blocked.["type"])
                  Assert.equal sessionB.Id (text blocked.["actor"].["id"])

                  current executor "work.resume" """{ "workItemIds": ["WI-0100"] }""" "req-b-resume-02" (Some sessionB) None
                  |> remote executor
                  |> succeeded
                  |> ignore

                  let executionsDirectory = Path.Combine(executor, ".ros", "telemetry", "executions")

                  let records =
                      Directory.GetFiles(executionsDirectory, "*.json") |> Array.map readJson |> List.ofArray

                  let successor = records |> List.find (fun record -> text record.["executionId"] <> predecessor)
                  Assert.equal "session-b" (text successor.["identity"].["sessionId"])
                  Assert.equal predecessor (text successor.["identity"].["parentExecutionId"])

                  Assert.equal
                      "session-a"
                      (text (records |> List.find (fun record -> text record.["executionId"] = predecessor)).["identity"].["sessionId"]) } ]
