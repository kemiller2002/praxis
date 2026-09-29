namespace Ros.Tests

open System.IO
open System.Text.Json.Nodes

/// The defining acceptance test of durable checkpoints (RQ-ROS-2026-A022,
/// PRAXIS-CONT-08-RECOVERY): executor A works only in clone A and is then
/// permanently lost (clone A is deleted from disk); executor B, a different
/// provider, recovers only from the shared remote in its own independent
/// clone, continues under its own identity, and legally completes the work.
[<RequireQualifiedAccess>]
module RecoveryProofTests =
    open PraxisCli

    let private executorA = agent "anthropic/claude-code" "anthropic" "claude-code" "claude-session-A"
    let private executorB = agent "openai/codex" "openai" "codex" "codex-thread-B"

    let private events (clone: string) =
        File.ReadAllLines(Path.Combine(clone, ".ros", "events", "events.jsonl"))
        |> Array.filter (fun line -> line.Trim().Length > 0)
        |> Array.map (fun line -> JsonNode.Parse line :?> JsonObject)
        |> Array.toList

    let private executionRecord (clone: string) (id: string) =
        JsonNode.Parse(File.ReadAllText(Path.Combine(clone, ".ros", "telemetry", "executions", $"{id}.json"))) :?> JsonObject

    let private remoteHead (clone: string) =
        (GitFixture.git clone [ "ls-remote"; "origin"; "refs/heads/feature/x" ]).Split('\t')[0]

    let private tokens (clone: string) who (extra: string list) =
        run clone (Some who) ([ "telemetry"; "record"; "FEAT-42"; "--metric"; "tokens.input"; "--unit"; "tokens"; "--source-type"; "runtime-api"; "--quiet" ] @ extra)
        |> ok
        |> ignore

    let private usageGroups (clone: string) (dimension: string) =
        (run clone None [ "telemetry"; "usage"; "FEAT-42"; "--by"; dimension ] |> ok).Json["groups"] :?> JsonArray
        |> Seq.filter (fun node -> text node["metric"] = "tokens.input")
        |> Seq.map (fun node -> text node["key"], node)
        |> Map.ofSeq

    let tests =
        [ { Name = "recovery proof: B recovers A's exact checkpoint from an independent clone after A is lost, continues and completes"
            Run =
              fun () ->
                  let parent = GitFixture.temporaryDirectory "recovery-proof"

                  try
                      let bare, cloneA = installedRepository parent "clone-a"
                      let installCommit = GitFixture.git cloneA [ "rev-parse"; "HEAD" ]
                      // B's clone exists independently from the start; it only ever
                      // talks to the shared remote.
                      let cloneB = Path.Combine(parent, "clone-b")
                      GitFixture.git parent [ "clone"; "-q"; bare; cloneB ] |> ignore
                      GitFixture.configureIdentity cloneB

                      // ---- Executor A, in clone A only ----
                      run cloneA (Some executorA) [ "work"; "start"; "--id"; "FEAT-42"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
                      // A predates step tracking: its usage is execution-scoped only.
                      tokens cloneA executorA [ "--value"; "900" ]
                      GitFixture.write cloneA "src/capability.txt" "capability boundary\n"
                      let checkpointCommitA = pushAll cloneA "Implement capability boundary"

                      let recordedA =
                          run
                              cloneA
                              (Some executorA)
                              [ "work"; "checkpoint"; "--id"; "FEAT-42"; "--occurred-at"; now ()
                                "--summary"; "Implemented capability boundary"
                                "--next-action"; "Implement Rust consumer fixture"; "--json" ]
                          |> ok

                      // A records which tests actually ran, as telemetry with its source.
                      run
                          cloneA
                          (Some executorA)
                          [ "telemetry"; "record"; "FEAT-42"; "--metric"; "tests.passed"; "--value"; "12"
                            "--source-type"; "external-tool"; "--source-name"; "dotnet-test"; "--mechanism"; "summary" ]
                      |> ok
                      |> ignore

                      let checkpointA = recordedA.Json["checkpoint"]
                      Assert.equal checkpointCommitA (text checkpointA["commit"])
                      Assert.equal checkpointCommitA (text checkpointA["remoteCommit"])
                      Assert.equal "feature/x" (text checkpointA["remoteBranch"])
                      let executionA = text checkpointA["executionId"]
                      // Strictly: local HEAD == checkpoint commit == remote branch head.
                      Assert.equal checkpointCommitA (GitFixture.git cloneA [ "rev-parse"; "HEAD" ])
                      Assert.equal checkpointCommitA (remoteHead cloneA)
                      // A makes its Praxis state durable too, then is lost.
                      pushAll cloneA "praxis: checkpoint FEAT-42" |> ignore
                      Directory.Delete(cloneA, true)
                      Assert.isTrue (not (Directory.Exists cloneA)) "clone A still exists"

                      // ---- Executor B, in clone B only; nothing of A remains ----
                      GitFixture.git cloneB [ "fetch"; "-q"; "origin" ] |> ignore
                      GitFixture.git cloneB [ "switch"; "-q"; "--track"; "origin/feature/x" ] |> ignore

                      // Discover the active work item from Praxis state alone.
                      let status = run cloneB None [ "status" ] |> ok

                      let active =
                          status.Json["workItems"] :?> JsonArray
                          |> Seq.filter (fun item -> text item["semanticState"] = "active")
                          |> Seq.map (fun item -> text item["id"])
                          |> Seq.toList

                      Assert.equal [ "FEAT-42" ] active

                      let context = run cloneB None [ "work"; "context"; "FEAT-42" ] |> ok
                      let continuity = context.Json["continuity"].[0]
                      let recoveredCommit = text continuity["checkpoint"].["commit"]
                      let recoveredBranch = text continuity["checkpoint"].["branch"]
                      Assert.equal "feature/x" recoveredBranch
                      Assert.equal "Implemented capability boundary" (text continuity["checkpoint"].["summary"])
                      Assert.equal "Implement Rust consumer fixture" (text continuity["checkpoint"].["nextAction"])
                      Assert.equal "current" (text continuity["freshness"])
                      // B verifies the SHA against the remote itself, independently of Praxis.
                      let lsRemote = GitFixture.git cloneB [ "ls-remote"; "origin"; "refs/heads/feature/x" ]
                      GitFixture.git cloneB [ "merge-base"; "--is-ancestor"; recoveredCommit; (lsRemote.Split('\t')[0]) ] |> ignore
                      GitFixture.git cloneB [ "merge-base"; "--is-ancestor"; recoveredCommit; "HEAD" ] |> ignore

                      // B continues under a new execution of its own.
                      let continued = run cloneB (Some executorB) [ "work"; "continue"; "--id"; "FEAT-42"; "--occurred-at"; now (); "--json" ] |> ok
                      let executionB = text continued.Json["executionId"]
                      Assert.isTrue (executionB <> executionA) "B reused A's execution"
                      Assert.equal executionA (text continued.Json["predecessor"].["executionId"])
                      Assert.equal "Implement Rust consumer fixture" (text continued.Json["continuity"].["checkpoint"].["nextAction"])
                      // B sees which tests actually ran, from recorded evidence, not from A's prose.
                      let ran = continued.Json["obligations"].["validationEvidence"] :?> JsonArray |> Seq.toList
                      Assert.equal [ "tests.passed" ] (ran |> List.map (fun node -> text node["metric"]))
                      Assert.equal executionA (text ran.Head["executionId"])
                      Assert.equal "external-tool" (text ran.Head["source"].["type"])
                      pushAll cloneB "praxis: FEAT-42 continued by B" |> ignore

                      // B implements the next action under step telemetry of its own,
                      // from its own start; A is never given steps retroactively.
                      run cloneB (Some executorB) [ "telemetry"; "step"; "start"; "FEAT-42"; "--step"; "rust-consumer"; "--occurred-at"; now () ] |> ok |> ignore
                      tokens cloneB executorB [ "--value"; "400"; "--step"; "rust-consumer" ]
                      GitFixture.write cloneB "src/rust-consumer.txt" "rust consumer fixture\n"
                      let checkpointCommitB = pushAll cloneB "Implement Rust consumer fixture"

                      let recordedB =
                          run
                              cloneB
                              (Some executorB)
                              [ "work"; "checkpoint"; "--id"; "FEAT-42"; "--occurred-at"; now ()
                                "--summary"; "Implemented Rust consumer fixture"
                                "--next-action"; "Run final completion transition"; "--step"; "rust-consumer"; "--json" ]
                          |> ok

                      Assert.equal checkpointCommitB (text recordedB.Json["checkpoint"].["commit"])
                      Assert.equal "rust-consumer" (text recordedB.Json["checkpoint"].["stepId"])
                      // The final checkpoint is exactly the remote branch head when recorded.
                      Assert.equal checkpointCommitB (GitFixture.git cloneB [ "rev-parse"; "HEAD" ])
                      Assert.equal checkpointCommitB (remoteHead cloneB)
                      run cloneB (Some executorB) [ "telemetry"; "step"; "complete"; "FEAT-42"; "--step"; "rust-consumer"; "--occurred-at"; now () ] |> ok |> ignore
                      Assert.equal executionB (text recordedB.Json["checkpoint"].["executionId"])
                      pushAll cloneB "praxis: checkpoint 2" |> ignore

                      // B completes legally (the durable-completion guard is enforced).
                      run
                          cloneB
                          (Some executorB)
                          [ "work"; "complete"; "--id"; "FEAT-42"; "--occurred-at"; now ()
                            "--evidence"; "implementation=src/rust-consumer.txt"; "--evidence"; "tests=src/capability.txt" ]
                      |> ok
                      |> ignore

                      pushAll cloneB "praxis: complete FEAT-42" |> ignore
                      run cloneB None [ "validate" ] |> ok |> ignore

                      // ---- Assertions ----
                      // B's recovered commit is exactly A's recorded checkpoint commit.
                      Assert.equal checkpointCommitA recoveredCommit

                      // Each executor keeps its own attribution.
                      let recordA = executionRecord cloneB executionA
                      let recordB = executionRecord cloneB executionB
                      Assert.equal "anthropic" (text recordA["identity"].["provider"])
                      Assert.equal "claude-session-A" (text recordA["identity"].["sessionId"])
                      Assert.equal "openai" (text recordB["identity"].["provider"])
                      Assert.equal "codex-thread-B" (text recordB["identity"].["sessionId"])
                      Assert.equal executionA (text recordB["identity"].["parentExecutionId"])

                      // Telemetry: A stays execution-level (truthfully unsegmented), B's
                      // step belongs to B only, and usage is attributed per executor.
                      let stepsOf (record: JsonObject) =
                          record["events"] :?> JsonArray
                          |> Seq.filter (fun node -> text node["type"] = "step.started")
                          |> Seq.map (fun node -> text node["stepId"])
                          |> Seq.toList

                      Assert.empty (stepsOf recordA)
                      Assert.equal [ "rust-consumer" ] (stepsOf recordB)
                      let view = run cloneB None [ "work"; "context"; "FEAT-42" ] |> ok
                      let segmentation = view.Json["continuity"].[0].["telemetry"].["executions"] :?> JsonArray
                      let segmentationOf id = segmentation |> Seq.find (fun node -> text node["executionId"] = id)
                      Assert.equal "execution-level" (text (segmentationOf executionA).["segmentation"])
                      Assert.equal "unavailable" (text (segmentationOf executionA).["historicalStepAttribution"])
                      Assert.isTrue ((text (segmentationOf executionB).["segmentation"]).StartsWith "step-level") "B's steps were not recognized"
                      Assert.isTrue ((segmentationOf executionB).["stepTrackingStartedAt"] <> null) "B has no step-tracking boundary"
                      let byExecution = usageGroups cloneB "execution"
                      Assert.equal 900.0 (byExecution[executionA].["total"].GetValue<float>())
                      Assert.equal 400.0 (byExecution[executionB].["total"].GetValue<float>())
                      let byStep = usageGroups cloneB "step"
                      Assert.equal 400.0 (byStep["rust-consumer"].["total"].GetValue<float>())
                      Assert.equal [ executionB ] (byStep["rust-consumer"].["reportingExecutions"] :?> JsonArray |> Seq.map text |> Seq.toList)
                      // A's 900 is never redistributed into B's (or any) step.
                      Assert.equal 900.0 (byStep["(outside any step)"].["total"].GetValue<float>())
                      Assert.equal [ executionA ] (byStep["(outside any step)"].["reportingExecutions"] :?> JsonArray |> Seq.map text |> Seq.toList)

                      let log = events cloneB
                      let checkpoints = log |> List.filter (fun node -> text node["type"] = "work.checkpointed")
                      Assert.equal [ checkpointCommitA; checkpointCommitB ] (checkpoints |> List.map (fun node -> text node["checkpoint"].["commit"]))
                      Assert.equal [ "anthropic/claude-code"; "openai/codex" ] (checkpoints |> List.map (fun node -> text node["actor"].["id"]))
                      Assert.equal [ executionA; executionB ] (checkpoints |> List.map (fun node -> text node["execution"]))
                      let continuedEvent = log |> List.find (fun node -> text node["type"] = "work.continued")
                      Assert.equal "openai/codex" (text continuedEvent["actor"].["id"])

                      // The work item ends complete.
                      let final = run cloneB None [ "work"; "context"; "FEAT-42" ] |> ok
                      Assert.equal "complete" (text final.Json["workItems"].[0].["semanticState"])
                      // The final checkpoint is the final remote branch head (only Praxis state after it).
                      let finalCheckpoint = text final.Json["workItems"].[0].["latestCheckpoint"].["commit"]
                      Assert.equal checkpointCommitB finalCheckpoint
                      let remoteHead = (GitFixture.git cloneB [ "ls-remote"; "origin"; "refs/heads/feature/x" ]).Split('\t')[0]
                      let meaningfulAfter = GitFixture.git cloneB [ "diff"; "--name-only"; finalCheckpoint; remoteHead; "--"; "."; ":(exclude).ros" ]
                      Assert.equal "" meaningfulAfter
                      GitFixture.git cloneB [ "merge-base"; "--is-ancestor"; finalCheckpoint; remoteHead ] |> ignore
                      // CI attribution: every meaningful change since installation is
                      // attributed although the work was committed before completion
                      // (checkpoint events carry Git-evidenced paths).
                      runWith cloneB None [ "ROS_BASE_REF", installCommit ] [ "validate" ] |> ok |> ignore
                      // No clone-A-only data was used: clone A no longer exists.
                      Assert.isTrue (not (Directory.Exists cloneA)) "clone A reappeared"
                  finally
                      GitFixture.cleanup parent } ]
