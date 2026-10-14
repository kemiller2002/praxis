namespace Praxis.Tests

open System.IO
open System.Text.Json.Nodes

/// PRAXIS-EXEC-01: work transitions bind and drive durable execution
/// envelopes (PRX-EXEC-014/026/030/041/053/054/055), through the real CLI.
[<RequireQualifiedAccess>]
module ExecutionBindingTests =
    open PraxisCli

    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"
    let private agentB = agent "other/agent-b" "other" "agent-b" "session-b"

    let private withRepository (test: string -> string -> string -> unit) =
        let parent = GitFixture.temporaryDirectory "execution-binding"

        try
            let bare, clone = installedRepository parent "clone-a"
            test parent bare clone
        finally
            GitFixture.cleanup parent

    let private begin' clone who (extra: string list) =
        run clone (Some who) ([ "work"; "start"; "--id"; "FEAT-1"; "--type"; "feature"; "--occurred-at"; now () ] @ extra) |> ok |> ignore

    let private envelopes clone =
        let dir = Path.Combine(clone, ".ros", "executions")

        if Directory.Exists dir then
            Directory.GetDirectories dir
            |> Array.map (fun d -> JsonNode.Parse(File.ReadAllText(Path.Combine(d, "envelope.json"))))
            |> Array.sortBy (fun e -> text e["startedAt"])
            |> Array.toList
        else
            []

    let private events clone (id: string) =
        File.ReadAllLines(Path.Combine(clone, ".ros", "executions", id, "events.jsonl")) |> Array.map JsonNode.Parse |> Array.toList

    let private telemetryIds clone =
        Directory.GetFiles(Path.Combine(clone, ".ros", "telemetry", "executions"), "*.json")
        |> Array.map Path.GetFileNameWithoutExtension
        |> Set.ofArray

    let private state clone =
        let view = run clone None [ "work"; "context"; "FEAT-1" ] |> ok
        text (view.Json["workItems"].[0].["semanticState"])

    let tests =
        [ { Name = "binding: work begin persists an envelope sharing the telemetry execution ID, bound to the checkout, with no new branch"
            Run =
              fun () ->
                  withRepository (fun _ _ clone ->
                      let worktreesBefore = GitFixture.git clone [ "worktree"; "list" ]
                      begin' clone agentA [ "--role"; "specification" ]
                      let e = envelopes clone |> List.exactlyOne
                      let id = text e["executionId"]
                      Assert.isTrue ((telemetryIds clone).Contains id) $"{id} is not a telemetry execution"
                      Assert.equal "work-transition" (text e["origin"].["kind"])
                      Assert.equal "work.begin" (text e["origin"].["reference"])
                      Assert.equal "specification" (text e["role"])
                      Assert.equal "example/agent-a" (text e["actor"].["id"])
                      Assert.equal (GitFixture.git clone [ "rev-parse"; "HEAD" ]) (text e["baselineRevision"])
                      Assert.equal "feature/x" (text e["workspaceBinding"].["branch"])
                      Assert.equal "working-directory" (text e["workspaceBinding"].["mechanism"])
                      Assert.equal "semantic-only" (text e["containment"])
                      Assert.equal worktreesBefore (GitFixture.git clone [ "worktree"; "list" ])
                      Assert.equal "feature/x" (GitFixture.git clone [ "rev-parse"; "--abbrev-ref"; "HEAD" ])
                      // Execution bookkeeping never needs attribution.
                      let status = run clone None [ "validate" ]
                      Assert.equal 0 status.ExitCode) }
          { Name = "binding: block and resume mirror the envelope; a diverged branch refuses resume until an explicit rebind"
            Run =
              fun () ->
                  withRepository (fun _ _ clone ->
                      begin' clone agentA []
                      let id = text (envelopes clone |> List.exactlyOne).["executionId"]
                      run clone (Some agentA) [ "work"; "block"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--reason"; "waiting"; "--unrecoverable-reason"; "nothing to recover yet" ] |> ok |> ignore
                      Assert.equal "blocked" (text (envelopes clone |> List.exactlyOne).["state"])
                      GitFixture.git clone [ "switch"; "-q"; "-c"; "elsewhere" ] |> ignore
                      let refused = run clone (Some agentA) [ "work"; "resume"; "--id"; "FEAT-1"; "--occurred-at"; now () ]
                      Assert.equal 3 refused.ExitCode
                      Assert.isTrue (refused.Error.Contains "bound to feature/x") refused.Error
                      Assert.equal "blocked" (state clone)
                      run clone (Some agentA) [ "work"; "resume"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--rebind-reason"; "moved the work to its own branch" ] |> ok |> ignore
                      Assert.equal "active" (state clone)
                      let e = envelopes clone |> List.exactlyOne
                      Assert.equal id (text e["executionId"])
                      Assert.equal "active" (text e["state"])
                      Assert.equal "elsewhere" (text e["workspaceBinding"].["branch"])
                      let rebound = events clone id |> List.find (fun n -> text n["entry"] = "workspace-rebound")
                      Assert.equal "moved the work to its own branch" (text rebound["reason"])) }
          { Name = "binding: work complete refuses while a bound execution's receipt mismatches, then records candidate and commits"
            Run =
              fun () ->
                  withRepository (fun _ _ clone ->
                      begin' clone agentA []
                      let id = text (envelopes clone |> List.exactlyOne).["executionId"]
                      run clone (Some agentA) [ "execution"; "step"; "declare"; id; "--step"; "check"; "--expect-command"; "false" ] |> ok |> ignore
                      Assert.equal 3 (run clone (Some agentA) [ "execution"; "step"; "run"; id; "--step"; "check" ]).ExitCode
                      GitFixture.write clone "src/feature.txt" "feature\n"
                      let candidate = pushAll clone "feature work"
                      run clone (Some agentA) [ "work"; "checkpoint"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--summary"; "done"; "--next-action"; "complete" ] |> ok |> ignore
                      let complete () = run clone (Some agentA) [ "work"; "complete"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--evidence"; "implementation=src/feature.txt"; "--evidence"; "tests=README.md" ]
                      let refused = complete ()
                      Assert.equal 3 refused.ExitCode
                      Assert.isTrue (refused.Error.Contains "not every step receipt matches") refused.Error
                      Assert.equal "active" (state clone)

                      // A second, independent execution of the same item gets its own identity (PRX-EXEC-041).
                      let explicitStart = run clone (Some agentA) [ "execution"; "start"; "--work-item"; "FEAT-1"; "--role"; "review"; "--json" ] |> ok
                      Assert.isTrue (text explicitStart.Json["executionId"] <> id) "distinct identities"
                      run clone (Some agentA) [ "execution"; "transition"; text explicitStart.Json["executionId"]; "--action"; "abandon"; "--reason"; "not needed" ] |> ok |> ignore

                      // The mismatch is resolved only by governed rework: this
                      // execution is abandoned, never rewritten.
                      run clone (Some agentA) [ "execution"; "transition"; id; "--action"; "abandon"; "--reason"; "rework in a new execution" ] |> ok |> ignore
                      Assert.equal 0 (complete ()).ExitCode
                      Assert.equal "abandoned" (text (envelopes clone |> List.find (fun n -> text n["executionId"] = id)).["state"])
                      Assert.isTrue ((events clone id |> List.filter (fun n -> text n["entry"] = "observed")).Length = 1) "the mismatch stays in the ledger") }
          { Name = "binding: work complete with matching receipts records the candidate and its commits (lineage, not attribution)"
            Run =
              fun () ->
                  withRepository (fun _ _ clone ->
                      begin' clone agentA []
                      let id = text (envelopes clone |> List.exactlyOne).["executionId"]
                      run clone (Some agentA) [ "execution"; "step"; "declare"; id; "--step"; "check"; "--expect-command"; "true" ] |> ok |> ignore
                      run clone (Some agentA) [ "execution"; "step"; "run"; id; "--step"; "check" ] |> ok |> ignore
                      GitFixture.write clone "src/feature.txt" "feature\n"
                      let first = pushAll clone "feature work"
                      GitFixture.write clone "src/feature.txt" "feature two\n"
                      let candidate = pushAll clone "more feature work"
                      run clone (Some agentA) [ "work"; "checkpoint"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--summary"; "done"; "--next-action"; "complete" ] |> ok |> ignore
                      run clone (Some agentA) [ "work"; "complete"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--evidence"; "implementation=src/feature.txt"; "--evidence"; "tests=README.md" ] |> ok |> ignore
                      let e = envelopes clone |> List.exactlyOne
                      Assert.equal "completed" (text e["state"])
                      Assert.equal candidate (text e["candidateRevision"])
                      let completed = events clone id |> List.find (fun n -> text n["entry"] = "transition" && text n["transition"] = "work.complete")
                      Assert.equal $"{first},{candidate}" (text completed["commits"])
                      // Lineage is recorded beside the transition; it is not a
                      // provenance contribution (PRX-EXEC-054).
                      Assert.isTrue (isNull completed["attributedTo"]) "no attribution by presence") }
          { Name = "binding: work continue interrupts the predecessor envelope and binds the successor as its child"
            Run =
              fun () ->
                  withRepository (fun parent bare clone ->
                      begin' clone agentA []
                      let predecessor = text (envelopes clone |> List.exactlyOne).["executionId"]
                      GitFixture.write clone "src/feature.txt" "part one\n"
                      pushAll clone "part one" |> ignore
                      run clone (Some agentA) [ "work"; "checkpoint"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--summary"; "part one"; "--next-action"; "part two" ] |> ok |> ignore
                      pushAll clone "praxis state" |> ignore
                      let second = GitFixture.secondClone parent bare "clone-b"
                      run second (Some agentB) [ "work"; "continue"; "--id"; "FEAT-1"; "--occurred-at"; now () ] |> ok |> ignore
                      let all = envelopes second
                      Assert.equal 2 all.Length
                      let old = all |> List.find (fun e -> text e["executionId"] = predecessor)
                      Assert.equal "interrupted" (text old["state"])
                      let successor = all |> List.find (fun e -> text e["executionId"] <> predecessor)
                      Assert.equal predecessor (text successor["parentExecution"])
                      Assert.equal "other/agent-b" (text successor["actor"].["id"])
                      Assert.equal "work.continue" (text successor["origin"].["reference"])
                      Assert.isTrue ((telemetryIds second).Contains(text successor["executionId"])) "the successor shares its telemetry execution ID") }
          { Name = "binding: an invalid host containment report is refused before work begin changes anything"
            Run =
              fun () ->
                  withRepository (fun parent _ clone ->
                      let report = Path.Combine(parent, "bad-evidence.json")
                      File.WriteAllText(report, """{"schema":"praxis.containment-evidence/1","host":"h","restrictions":[{"dimension":"network","status":"enforced"}]}""")
                      let refused = runWith clone (Some agentA) [ "PRAXIS_CONTAINMENT_EVIDENCE", report ] [ "work"; "start"; "--id"; "FEAT-1"; "--type"; "feature"; "--occurred-at"; now () ]
                      Assert.equal 2 refused.ExitCode
                      Assert.isTrue (refused.Error.Contains "no evidence") refused.Error
                      Assert.equal [] (envelopes clone)
                      File.WriteAllText(report, """{"schema":"praxis.containment-evidence/1","host":"h","restrictions":[{"dimension":"network","status":"enforced","evidence":"netns"}]}""")
                      runWith clone (Some agentA) [ "PRAXIS_CONTAINMENT_EVIDENCE", report ] [ "work"; "start"; "--id"; "FEAT-1"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
                      Assert.equal "host-enforced" (text (envelopes clone |> List.exactlyOne).["containment"])) } ]
