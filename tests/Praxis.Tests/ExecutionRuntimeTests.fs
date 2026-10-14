namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Cli
open Praxis.Contracts.Execution
open Praxis.Domain.Execution
open Praxis.Domain.Provenance

/// PRAXIS-EXEC-02/03/04: the governed evaluation runner, attributed ledger
/// entries, role launchers and repository execution policy, and the
/// containment profile with host enforcement evidence.
[<RequireQualifiedAccess>]
module ExecutionRuntimeTests =
    let private ok r =
        match r with
        | Ok v -> v
        | Error e -> failwith $"expected Ok, got %A{e}"

    let private t0 = DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero)

    let private actor kind : ExecutionActor =
        { Id = "actor-" + kind; Kind = kind; Provider = None; Model = None; Runtime = None }

    let private closure = [ { Kind = "gate-code"; Reference = "tests/gate.sh"; Digest = "sha256:aa" } ]

    let private envelope role =
        ExecutionEnvelope.create "EXE-1" "o/r:WI-1" (actor "agent") (RoleAuthority.defaultFor role) "abc" MutationBoundary.empty (Some(EvaluatorIdentity.create closure |> ok)) t0
        |> ok

    let private find transition (actions: LegalAction list) = actions |> List.find (fun a -> a.Transition = transition)

    let private restriction d status evidence =
        { Dimension = d
          Status = status
          Mechanism = Some "test-host"
          Evidence = evidence }

    let domain =
        [ { Name = "containment: unreported dimensions stay unknown; enforcement needs evidence"
            Run =
              fun () ->
                  Assert.isTrue (ContainmentProfile.unknown.Restrictions |> List.forall (fun r -> r.Status = RestrictionStatus.Unknown)) "nothing is inferred"
                  let profile = ContainmentProfile.fromReport "bwrap" [ restriction "network" RestrictionStatus.Enforced (Some "unshare-net") ] |> ok
                  Assert.equal [ "network" ] (ContainmentProfile.enforced profile)
                  Assert.equal RestrictionStatus.Unknown ((profile.Restrictions |> List.find (fun r -> r.Dimension = "filesystem")).Status)
                  Assert.isTrue (Result.isError (ContainmentProfile.fromReport "bwrap" [ restriction "network" RestrictionStatus.Enforced None ])) "enforced without evidence is refused"
                  Assert.isTrue (Result.isError (ContainmentProfile.fromReport "bwrap" [ restriction "gpu" RestrictionStatus.Unknown None ])) "unknown dimension refused"
                  Assert.isTrue (Result.isError (ContainmentProfile.fromReport "" [])) "host is required"
                  Assert.equal [ "filesystem" ] (ContainmentProfile.shortfall [ "network"; "filesystem" ] profile) }
          { Name = "containment: host-enforced only with enforcement evidence; a worktree stays semantic-only"
            Run =
              fun () ->
                  let semantic = Containment.SemanticOnly "git-worktree"
                  let unavailable = ContainmentProfile.fromReport "ci" [ restriction "network" RestrictionStatus.Unavailable None ] |> ok
                  Assert.equal semantic (ContainmentProfile.containment semantic unavailable)
                  Assert.equal semantic (ContainmentProfile.containment semantic ContainmentProfile.unknown)
                  let enforced = ContainmentProfile.fromReport "bwrap" [ restriction "filesystem" RestrictionStatus.Enforced (Some "ro-bind /") ] |> ok
                  Assert.isTrue (Containment.isSecuritySandbox (ContainmentProfile.containment semantic enforced)) "evidence makes it host-enforced" }
          { Name = "legal actions: evaluation needs a declared command and evaluator authority"
            Run =
              fun () ->
                  let impl = envelope ExecutionRole.Implementation
                  let undeclared = LegalActions.evaluate impl ExecutionObservation.empty "agent" |> find "execution.evaluate"
                  Assert.isTrue (not undeclared.Available) "no declared command"
                  Assert.isTrue (undeclared.Reasons |> List.exists (fun r -> r.Contains "evaluator command")) $"{undeclared.Reasons}"
                  Assert.isTrue (LegalActions.evaluate { impl with EvaluatorCommand = Some "sh tests/gate.sh" } ExecutionObservation.empty "agent" |> find "execution.evaluate").Available "declared"
                  let review = { envelope ExecutionRole.Review with EvaluatorCommand = Some "sh tests/gate.sh" }
                  let refused = LegalActions.evaluate review ExecutionObservation.empty "agent" |> find "execution.evaluate"
                  Assert.isTrue (refused.Reasons |> List.exists (fun r -> r.Contains "may not invoke the evaluator")) $"{refused.Reasons}" }
          { Name = "legal actions: verification of another candidate is stale for completion"
            Run =
              fun () ->
                  let e = envelope ExecutionRole.Implementation
                  let fp = e.Evaluator.Value.Fingerprint

                  let verified candidate =
                      { ExecutionObservation.empty with
                          Head = Some "head2"
                          Verification =
                            Some
                                { Outcome = EvaluationOutcome.Passed fp
                                  Command = "sh tests/gate.sh"
                                  Candidate = Some candidate
                                  ExitCode = Some 0
                                  ActorId = "a"
                                  ActorKind = "agent"
                                  Evidence = []
                                  At = t0 } }

                  Assert.isTrue (LegalActions.evaluate e (verified "head2") "agent" |> find "execution.complete").Available "current candidate"
                  let stale = LegalActions.evaluate e (verified "head1") "agent" |> find "execution.complete"
                  Assert.isTrue (stale.Reasons |> List.exists (fun r -> r.Contains "evaluate again")) $"{stale.Reasons}" }
          { Name = "legal actions: launch needs a configured launcher and the required host restrictions"
            Run =
              fun () ->
                  let e = envelope ExecutionRole.Verification
                  let none = LegalActions.evaluate e ExecutionObservation.empty "agent" |> find "execution.launch"
                  Assert.isTrue (none.Reasons |> List.exists (fun r -> r.Contains "no launcher")) $"{none.Reasons}"
                  let launcher = Some { Id = "verifier"; Command = "true"; ContainmentEvidence = None }
                  Assert.isTrue (LegalActions.evaluate e { ExecutionObservation.empty with Launcher = launcher } "agent" |> find "execution.launch").Available "configured"
                  let short = LegalActions.evaluate e { ExecutionObservation.empty with Launcher = launcher; ContainmentShortfall = [ "network" ] } "agent" |> find "execution.launch"
                  Assert.isTrue (short.Reasons |> List.exists (fun r -> r.Contains "'network'")) $"{short.Reasons}" }
          { Name = "legal actions: workspace divergence blocks resume and completion and offers an explicit rebind"
            Run =
              fun () ->
                  let e =
                      { envelope ExecutionRole.Implementation with
                          State = ExecutionState.Blocked "lunch"
                          Workspace = Some { Id = "checkout:EXE-1"; Branch = Some "feature/a"; Path = None; Mechanism = "working-directory" } }

                  let observed = { Present = true; Branch = Some "main"; Head = Some "h"; BaselineIsAncestor = Some false; CandidateIsAncestor = None }
                  let divergence = WorkspaceBinding.divergence e observed
                  Assert.equal 2 divergence.Length
                  let actions = LegalActions.evaluate e { ExecutionObservation.empty with Divergence = divergence } "agent"
                  Assert.isTrue (not (find "execution.resume" actions).Available) "resume refused"
                  Assert.isTrue (find "execution.rebind" actions).Available "rebind offered"
                  Assert.equal [] (WorkspaceBinding.divergence e { observed with Branch = Some "feature/a"; BaselineIsAncestor = Some true })
                  Assert.equal [ "the bound workspace no longer exists" ] (WorkspaceBinding.divergence e { observed with Present = false }) }
          { Name = "roles: one role, one contract, whoever performs it (PRX-EXEC-033, 043, 044, 045)"
            Run =
              fun () ->
                  let claude: ExecutionActor = { Id = "anthropic/claude-code"; Kind = "agent"; Provider = Some "anthropic"; Model = None; Runtime = Some "claude-code" }
                  let human: ExecutionActor = { Id = "kem"; Kind = "human"; Provider = None; Model = None; Runtime = None }
                  let make a = ExecutionEnvelope.create "EXE-1" "w" a (RoleAuthority.defaultFor ExecutionRole.Review) "abc" MutationBoundary.empty None t0 |> ok
                  Assert.equal (RoleAuthority.effective (make claude).Authority) (RoleAuthority.effective (make human).Authority)
                  let review = RoleAuthority.defaultFor ExecutionRole.Review
                  Assert.isTrue (RoleAuthority.allows Capability.RecordFindings review && not (RoleAuthority.allows Capability.ModifyImplementation review)) "review records findings, never implements"
                  let integration = RoleAuthority.defaultFor ExecutionRole.Integration
                  Assert.isTrue (RoleAuthority.allows Capability.CombineAuthorizedCandidates integration) "integration combines"
                  Assert.isTrue (not (RoleAuthority.allows Capability.ModifyAcceptanceCriteria integration || RoleAuthority.allows Capability.ApproveSpecification integration)) "integration cannot change acceptance"
                  // No legal action changes an envelope's role; a different
                  // role is a new execution (PRX-EXEC-045).
                  let actions = LegalActions.evaluate (make claude) ExecutionObservation.empty "agent"
                  Assert.isTrue (actions |> List.forall (fun a -> not (a.Transition.Contains "role"))) "no role-change transition"
                  let roundTrip = ExecutionJson.readEnvelope (ExecutionJson.envelope (make claude)) |> ok
                  Assert.equal ExecutionRole.Review roundTrip.Authority.Role }
          { Name = "envelope JSON: origin, containment profile and evaluator command round-trip; legacy envelopes read"
            Run =
              fun () ->
                  let profile = ContainmentProfile.fromReport "bwrap" [ restriction "network" RestrictionStatus.Enforced (Some "unshare-net") ] |> ok

                  let e =
                      { envelope ExecutionRole.Implementation with
                          Origin = ExecutionOrigin.WorkTransition "work.begin"
                          ContainmentProfile = profile
                          EvaluatorCommand = Some "sh tests/gate.sh" }

                  let back = ExecutionJson.readEnvelope (ExecutionJson.envelope e) |> ok
                  Assert.equal e.Origin back.Origin
                  Assert.equal e.ContainmentProfile back.ContainmentProfile
                  Assert.equal e.EvaluatorCommand back.EvaluatorCommand
                  let legacy = ExecutionJson.envelope (envelope ExecutionRole.Implementation)
                  let o = legacy.AsObject()
                  o.Remove "origin" |> ignore
                  o.Remove "containmentProfile" |> ignore
                  o.Remove "evaluatorCommand" |> ignore
                  let old = ExecutionJson.readEnvelope legacy |> ok
                  Assert.equal ExecutionOrigin.Explicit old.Origin
                  Assert.equal ContainmentProfile.unknown old.ContainmentProfile }
          { Name = "policy: ros.json execution section parses and rejects unknown roles and dimensions"
            Run =
              fun () ->
                  let parse (json: string) = ExecutionJson.readPolicy (Some(JsonNode.Parse json))
                  let p = parse """{"launchers":{"verification":{"id":"ci","command":"run-verifier"}},"worktree":{"required":["implementation"]},"containment":{"verification":{"require":["network"]}}}""" |> ok
                  Assert.equal (Some "ci") (ExecutionPolicy.launcherFor ExecutionRole.Verification p |> Option.map _.Id)
                  Assert.isTrue (ExecutionPolicy.requiresWorktree ExecutionRole.Implementation p) "worktree required"
                  Assert.equal [ "network" ] (ExecutionPolicy.requiredRestrictions ExecutionRole.Verification p)
                  Assert.isTrue (Result.isError (parse """{"launchers":{"wizard":{"command":"x"}}}""")) "unknown role"
                  Assert.isTrue (Result.isError (parse """{"containment":{"review":{"require":["gpu"]}}}""")) "unknown dimension"
                  Assert.isTrue (Result.isError (parse """{"launchers":{"review":{}}}""")) "launcher needs a command"
                  Assert.equal ExecutionPolicy.empty (ExecutionJson.readPolicy None |> ok) } ]

    // ---------------------------------------------------------------- CLI

    let private git = GitFixture.git

    let private repository (policy: string option) =
        let dir = GitFixture.temporaryDirectory "exec-runtime"
        git dir [ "init"; "-q"; "-b"; "main" ] |> ignore
        GitFixture.configureIdentity dir
        git dir [ "remote"; "add"; "origin"; "https://github.com/echelon-foundry/example.git" ] |> ignore
        Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
        Directory.CreateDirectory(Path.Combine(dir, "tests")) |> ignore
        File.WriteAllText(Path.Combine(dir, "src", "a.txt"), "a")
        File.WriteAllText(Path.Combine(dir, "tests", "gate.sh"), "exit 0\n")

        policy |> Option.iter (fun p -> File.WriteAllText(Path.Combine(dir, "ros.json"), $"{{\"execution\": {p}}}"))
        git dir [ "add"; "-A" ] |> ignore
        git dir [ "commit"; "-q"; "-m"; "baseline" ] |> ignore
        dir

    let private agent: Actor =
        { Kind = ActorKind.Agent; Id = "anthropic/claude-code"; Provider = Some "anthropic"; Model = None; Runtime = Some "claude-code" }

    let private capture (f: unit -> int) =
        let previous, previousError = Console.Out, Console.Error
        use sink = new StringWriter()
        use errors = new StringWriter()
        Console.SetOut sink
        Console.SetError errors

        try
            let code = f ()
            code, sink.ToString() + errors.ToString()
        finally
            Console.SetOut previous
            Console.SetError previousError

    let private run root args = capture (fun () -> ExecutionCommands.run root agent args)

    let private start root role extra =
        let code, out =
            run root ([ "start"; "--work-item"; "WI-1"; "--role"; role; "--evaluator"; "gate-code=tests/gate.sh"; "--json" ] @ extra)

        Assert.equal 0 code
        let node = JsonNode.Parse out
        node["executionId"].GetValue<string>(), node

    let private events root id =
        File.ReadAllLines(Path.Combine(root, ".ros", "executions", id, "events.jsonl")) |> Array.map JsonNode.Parse |> Array.toList

    let private kind (n: JsonNode) = n["entry"].GetValue<string>()

    let cli =
        [ { Name = "evaluate: only the declared command, legal-action gated, recorded with candidate, exit code and actor"
            Run =
              fun () ->
                  let root = repository None
                  let undeclared, _ = start root "implementation" []
                  let code, out = run root [ "evaluate"; undeclared ]
                  Assert.equal 3 code
                  Assert.isTrue (out.Contains "evaluator command") out

                  let id, _ = start root "implementation" [ "--evaluator-command"; "sh tests/gate.sh" ]
                  let code, out = run root [ "evaluate"; id; "--command"; "rm -rf src" ]
                  Assert.equal 3 code
                  Assert.isTrue (out.Contains "only its declared evaluator command") out
                  Assert.isTrue (File.Exists(Path.Combine(root, "src", "a.txt"))) "the undeclared command never ran"

                  Assert.equal 0 (run root [ "evaluate"; id; "--evidence"; "out/report.json" ] |> fst)
                  let record = events root id |> List.filter (fun n -> kind n = "verification") |> List.last
                  Assert.equal "passed" (record["outcome"].GetValue<string>())
                  Assert.equal (git root [ "rev-parse"; "HEAD" ]) (record["candidate"].GetValue<string>())
                  Assert.equal 0 (record["exitCode"].GetValue<int>())
                  Assert.equal "anthropic/claude-code" (record["actor"].["id"].GetValue<string>())
                  Assert.equal "out/report.json" (record["evidence"].[0].GetValue<string>())

                  // A new candidate makes the verdict stale for completion.
                  File.WriteAllText(Path.Combine(root, "src", "b.txt"), "b")
                  git root [ "add"; "-A" ] |> ignore
                  git root [ "commit"; "-q"; "-m"; "next" ] |> ignore
                  let code, out = run root [ "transition"; id; "--action"; "complete" ]
                  Assert.equal 3 code
                  Assert.isTrue (out.Contains "evaluate again") out

                  let review, _ = start root "review" [ "--evaluator-command"; "sh tests/gate.sh" ]
                  let code, out = run root [ "evaluate"; review ]
                  Assert.equal 3 code
                  Assert.isTrue (out.Contains "may not invoke the evaluator") out }
          { Name = "ledger: every appended step entry carries actor, role, revision and evaluator identity"
            Run =
              fun () ->
                  let root = repository None
                  let id, _ = start root "implementation" []
                  Assert.equal 0 (run root [ "step"; "declare"; id; "--step"; "check"; "--expect-command"; "true" ] |> fst)
                  Assert.equal 0 (run root [ "step"; "run"; id; "--step"; "check" ] |> fst)
                  let steps = events root id |> List.filter (fun n -> List.contains (kind n) [ "declared"; "started"; "observed" ])
                  Assert.equal 3 steps.Length

                  for entry in steps do
                      let a = entry["attribution"]
                      Assert.equal "anthropic/claude-code" (a["actor"].["id"].GetValue<string>())
                      Assert.equal "implementation" (a["role"].GetValue<string>())
                      Assert.equal (git root [ "rev-parse"; "HEAD" ]) (a["revision"].GetValue<string>())
                      Assert.isTrue (a["evaluator"].GetValue<string>().StartsWith "sha256:") "evaluator identity"

                  let _, shown = run root [ "show"; id; "--json" ]
                  Assert.equal 3 ((JsonNode.Parse shown).["steps"].[0].["attributions"].AsArray().Count)
                  // Declaring and observing are legal-action gated too.
                  Assert.equal 0 (run root [ "transition"; id; "--action"; "abandon"; "--reason"; "superseded" ] |> fst)
                  let code, out = run root [ "step"; "declare"; id; "--step"; "late"; "--expect-command"; "true" ]
                  Assert.equal 3 code
                  Assert.isTrue (out.Contains "step.declare is not legal now") out
                  Assert.equal 3 (run root [ "step"; "observe"; id; "--step"; "check"; "--observed-json"; "{\"source\":\"host\",\"facts\":[]}" ] |> fst) }
          { Name = "launch: the configured role launcher runs after the legal transition with the envelope in its environment"
            Run =
              fun () ->
                  let root =
                      repository (Some """{"launchers":{"verification":{"id":"verifier","command":"env | grep '^PRAXIS_EXECUTION' | sort > launched.txt"}},"worktree":{"required":["implementation"]}}""")

                  let impl, implNode = start root "implementation" []
                  Assert.isTrue (implNode["workspaceBinding"].["mechanism"].GetValue<string>() = "git-worktree") "policy requires a worktree for implementation"
                  let code, out = run root [ "launch"; impl ]
                  Assert.equal 3 code
                  Assert.isTrue (out.Contains "no launcher is configured for role implementation") out

                  let code, _ = run root [ "start"; "--work-item"; "WI-1"; "--role"; "verification"; "--launch" ]
                  Assert.equal 0 code
                  let _, listed = run root [ "list"; "--json" ]
                  let verifier = (JsonNode.Parse listed).AsArray() |> Seq.find (fun e -> e["role"].GetValue<string>() = "verification") |> fun e -> e["executionId"].GetValue<string>()
                  let launched = File.ReadAllText(Path.Combine(root, "launched.txt"))
                  Assert.isTrue (launched.Contains $"PRAXIS_EXECUTION_ID={verifier}") launched
                  Assert.isTrue (launched.Contains "PRAXIS_EXECUTION_ROLE=verification") launched
                  Assert.isTrue (launched.Contains "evaluator.invoke") launched
                  let capabilities = launched.Split('\n') |> Array.find (fun l -> l.StartsWith "PRAXIS_EXECUTION_CAPABILITIES=")
                  Assert.isTrue (not (capabilities.Contains "implementation.modify")) "the verification launcher gets the narrower capability set"
                  let finished = events root verifier |> List.find (fun n -> kind n = "launch-finished")
                  Assert.equal "0" (finished["exitCode"].GetValue<string>())
                  Assert.isTrue (verifier <> impl) "independent executions"
                  let dryCode, dry = run root [ "launch"; verifier; "--dry-run" ]
                  Assert.equal 0 dryCode
                  Assert.isTrue (dry.Contains "launcher: verifier") dry }
          { Name = "containment: host evidence is recorded per restriction; policy refuses a launch without required enforcement"
            Run =
              fun () ->
                  let root =
                      repository (Some """{"launchers":{"verification":{"command":"true"}},"containment":{"verification":{"require":["network"]}}}""")

                  let evidence = Path.Combine(root, "..", Path.GetFileName root + "-evidence.json")
                  File.WriteAllText(evidence, """{"schema":"praxis.containment-evidence/1","host":"bwrap","restrictions":[{"dimension":"filesystem","status":"enforced","mechanism":"ro-bind","evidence":"bwrap --ro-bind / /"},{"dimension":"network","status":"unavailable"}]}""")
                  let id, node = start root "verification" [ "--containment-evidence"; evidence ]
                  Assert.equal "host-enforced" (node["containment"].GetValue<string>())
                  let _, profile = run root [ "containment"; id; "--json" ]
                  let p = JsonNode.Parse profile
                  Assert.equal "praxis.containment/1" (p["schema"].GetValue<string>())
                  let status d = p["restrictions"].AsArray() |> Seq.find (fun r -> r["dimension"].GetValue<string>() = d) |> fun r -> r["status"].GetValue<string>()
                  Assert.equal "enforced" (status "filesystem")
                  Assert.equal "unavailable" (status "network")
                  Assert.equal "unknown" (status "credential")
                  let code, out = run root [ "launch"; id ]
                  Assert.equal 3 code
                  Assert.isTrue (out.Contains "'network' restriction") out

                  let plain, plainNode = start root "implementation" []
                  Assert.equal "semantic-only" (plainNode["containment"].GetValue<string>())
                  Assert.isTrue (plain <> id) "distinct"
                  File.WriteAllText(evidence, """{"schema":"praxis.containment-evidence/1","host":"bwrap","restrictions":[{"dimension":"network","status":"enforced"}]}""")
                  let code, out = run root [ "start"; "--work-item"; "WI-1"; "--role"; "verification"; "--containment-evidence"; evidence ]
                  Assert.equal 2 code
                  Assert.isTrue (out.Contains "no evidence") out }
          { Name = "containment: a launcher's host evidence and PRAXIS_CONTAINMENT_EVIDENCE are recorded; the required restriction then admits the launch"
            Run =
              fun () ->
                  let root =
                      repository (Some """{"launchers":{"verification":{"id":"sandboxed","command":"true","containmentEvidence":"host/verifier.json"}},"containment":{"verification":{"require":["network"]}}}""")

                  Directory.CreateDirectory(Path.Combine(root, "host")) |> ignore
                  File.WriteAllText(Path.Combine(root, "host", "verifier.json"), """{"schema":"praxis.containment-evidence/1","host":"bwrap","restrictions":[{"dimension":"network","status":"enforced","mechanism":"--unshare-net","evidence":"bwrap argv"}]}""")
                  let id, node = start root "verification" []
                  Assert.equal "semantic-only" (node["containment"].GetValue<string>())
                  Assert.equal 0 (run root [ "launch"; id ] |> fst)
                  let _, shown = run root [ "show"; id; "--json" ]
                  let s = JsonNode.Parse shown
                  Assert.equal "host-enforced" (s["containment"].GetValue<string>())
                  Assert.equal "bwrap" (s["containmentProfile"].["source"].GetValue<string>())
                  Assert.isTrue (events root id |> List.exists (fun n -> kind n = "containment-observed")) "the observation is in the ledger"

                  let previous = Environment.GetEnvironmentVariable "PRAXIS_CONTAINMENT_EVIDENCE"
                  Environment.SetEnvironmentVariable("PRAXIS_CONTAINMENT_EVIDENCE", Path.Combine(root, "host", "verifier.json"))

                  try
                      let _, fromHost = start root "implementation" []
                      Assert.equal "host-enforced" (fromHost["containment"].GetValue<string>())
                  finally
                      Environment.SetEnvironmentVariable("PRAXIS_CONTAINMENT_EVIDENCE", previous) } ]

    let fallback =
        [ { Name = "binding: a reconciled fallback envelope binds its execution once, with its base commit, branch and actor, and applies its requests"
            Run =
              fun () ->
                  let root = repository None
                  let baseCommit = git root [ "rev-parse"; "HEAD" ]
                  let at = DateTimeOffset(2026, 10, 6, 18, 0, 0, TimeSpan.Zero)

                  let request kind : Praxis.Domain.Work.EnvelopeRequest =
                      { RequestType = kind; OccurredAt = Some at; WorkType = Some "feature"; Reason = None; Conclusion = None; Evidence = [] }

                  let input: Praxis.Domain.Work.EnvelopeReconciliationInput =
                      { SchemaVersion = "1.0"
                        TransactionId = "tx-WI-9-001"
                        WorkItem = "WI-9"
                        Branch = "WI-9"
                        BaseCommit = baseCommit
                        PraxisInstanceId = None
                        Agent = { ActorKind = "agent"; ActorId = "provider/runtime"; Provider = Some "provider"; Model = Some "unknown"; Runtime = Some "runtime" }
                        Execution = None
                        Timeline = [ { Sequence = 1; Timestamp = at; Action = "start" }; { Sequence = 2; Timestamp = at; Action = "complete" } ]
                        Requests = [ request "work.start"; request "work.complete" ] }

                  let ports = Praxis.Infrastructure.Execution.FileExecutionPorts.create root
                  Praxis.Application.Execution.ExecutionBinding.fallback ports input |> ok
                  // A replay binds the same execution, never a second one.
                  Praxis.Application.Execution.ExecutionBinding.fallback ports input |> ok
                  let _, listed = run root [ "list"; "--json" ]
                  let all = (JsonNode.Parse listed).AsArray()
                  Assert.equal 1 all.Count
                  let e = all[0]
                  Assert.equal "fallback" (e["origin"].["kind"].GetValue<string>())
                  Assert.equal "tx-WI-9-001" (e["origin"].["reference"].GetValue<string>())
                  Assert.equal baseCommit (e["baselineRevision"].GetValue<string>())
                  Assert.equal "WI-9" (e["workspaceBinding"].["branch"].GetValue<string>())
                  Assert.equal "provider/runtime" (e["actor"].["id"].GetValue<string>())
                  Assert.isTrue (isNull e["actor"].["model"]) "an unknown model stays unset"
                  Assert.equal "completed" (e["state"].GetValue<string>()) } ]

    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "requirements", "EXECUTION-ORCHESTRATION.md")) then directory.FullName
        elif isNull directory.Parent then failwith "Could not locate requirements/EXECUTION-ORCHESTRATION.md"
        else repositoryRoot directory.Parent

    let documentation =
        [ { Name = "requirements: every EXECUTION-ORCHESTRATION requirement has exactly one status row, and every gap names its work item"
            Run =
              fun () ->
                  let text = File.ReadAllText(Path.Combine(repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory())), "requirements", "EXECUTION-ORCHESTRATION.md"))
                  let defined = Text.RegularExpressions.Regex.Matches(text, @"\*\*(PRX-[A-Z]+-\d{3})\*\*") |> Seq.map _.Groups[1].Value |> Seq.toList
                  let start, finish = text.IndexOf "<!-- status:begin -->", text.IndexOf "<!-- status:end -->"
                  Assert.isTrue (start > 0 && finish > start) "the status table is delimited"

                  let rows =
                      text.Substring(start, finish - start).Split('\n')
                      |> Array.filter _.StartsWith("| PRX-")
                      |> Array.map (fun line -> line.Split('|') |> Array.map _.Trim() |> fun cells -> cells[1], cells[2], cells[4])
                      |> Array.toList

                  Assert.isTrue (defined.Length >= 130) $"{defined.Length} requirements found"
                  Assert.equal (List.sort defined) (rows |> List.map (fun (id, _, _) -> id) |> List.sort)

                  for id, status, item in rows do
                      Assert.isTrue (List.contains status [ "Implemented"; "Partial"; "Not implemented"; "Not applicable" ]) $"{id}: unknown status '{status}'"

                      if status = "Partial" || status = "Not implemented" then
                          Assert.isTrue (item <> "-" && item <> "") $"{id} is {status} but names no work item" } ]

    let tests = domain @ cli @ fallback @ documentation
