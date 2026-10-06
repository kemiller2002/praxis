namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Cli
open Praxis.Domain.Execution
open Praxis.Domain.Provenance

/// Praxis's host projection of Ordo's execution-governance contract. The
/// conformance vectors here are shared with Ordo (evaluator fingerprint
/// computed by Ordo.Core's `EvaluatorIdentity.create`).
[<RequireQualifiedAccess>]
module ExecutionGovernanceTests =
    let private walk (node: JsonNode) (keys: string list) =
        keys |> List.fold (fun (n: JsonNode) (k: string) -> if k.StartsWith "#" then n.AsArray().[int (k.Substring 1)] else n.[k]) node

    let private textAt node keys = (walk node keys).GetValue<string>()
    let private boolAt node keys = (walk node keys).GetValue<bool>()

    let private ok r =
        match r with
        | Ok v -> v
        | Error e -> failwith $"expected Ok, got %A{e}"

    let private t0 = DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)
    let private host facts = { Source = ObservationSource.Host "praxis"; Facts = facts; Narrative = None }

    let private closure =
        [ { Kind = "gate-code"; Reference = "tests/gate.fs"; Digest = "sha256:aa" }
          { Kind = "configuration"; Reference = "ros.json"; Digest = "sha256:bb" }
          { Kind = "generated-input"; Reference = "generated/x \"q\".json"; Digest = "sha256:cc" } ]

    let private boundary =
        { Scopes = [ "feature:installation" ]
          Projections = [ { Scope = "feature:installation"; Patterns = [ "src/Installation/**" ] } ]
          EvaluatorReferences = [ "tests/Installation.Gate.fs" ] }

    let private actor kind : ExecutionActor =
        { Id = "actor-" + kind; Kind = kind; Provider = None; Model = None; Runtime = None }

    let domain =
        [ { Name = "evaluator fingerprint matches Ordo.Core byte for byte (conformance vector)"
            Run =
              fun () ->
                  let identity = EvaluatorIdentity.create closure |> ok
                  // Computed by Ordo.Core EvaluatorIdentity.create over the same closure.
                  Assert.equal "sha256:0278ed6280fb844860626713433c7b80144ea7703e36a6cd203d8924bf15d112" identity.Fingerprint
                  Assert.equal identity.Fingerprint (EvaluatorIdentity.create (List.rev closure) |> ok).Fingerprint }
          { Name = "evaluator: direct, indirect and generated changes are neither pass nor fail"
            Run =
              fun () ->
                  let baseline = EvaluatorIdentity.create closure |> ok

                  for reference in [ "tests/gate.fs"; "ros.json"; "generated/x \"q\".json" ] do
                      let changed = closure |> List.map (fun i -> if i.Reference = reference then { i with Digest = "sha256:new" } else i)
                      let current = EvaluatorIdentity.create changed

                      match EvaluationOutcome.judge baseline current true "" with
                      | EvaluationOutcome.EvaluatorChanged(_, _, diff) -> Assert.equal [ reference ] diff
                      | other -> failwith $"expected evaluator-changed, got %A{other}"

                  match EvaluationOutcome.judge baseline (Ok baseline) true "" with
                  | EvaluationOutcome.Passed fp -> Assert.equal baseline.Fingerprint fp
                  | other -> failwith $"expected pass, got %A{other}" }
          { Name = "evaluator: a stale verdict is invalidated"
            Run =
              fun () ->
                  let baseline = EvaluatorIdentity.create closure |> ok
                  let verdict = EvaluationOutcome.judge baseline (Ok baseline) true ""
                  let repaired = EvaluatorIdentity.create (closure |> List.map (fun i -> { i with Digest = i.Digest + "1" })) |> ok
                  Assert.isTrue (EvaluationOutcome.isCurrent baseline verdict) "verdict is current under its own evaluator"
                  Assert.isTrue (not (EvaluationOutcome.isCurrent repaired verdict)) "verdict must not survive an evaluator change" }
          { Name = "receipts: match, mismatch, indeterminate and composite partial match"
            Run =
              fun () ->
                  let cmd = ExpectedReceipt.CommandSucceeded "build"
                  Assert.equal ReceiptOutcome.Match (Receipt.compare cmd (host [ ObservedFact.CommandExited("build", 0) ])).Outcome
                  Assert.equal ReceiptOutcome.Mismatch (Receipt.compare cmd (host [ ObservedFact.CommandExited("build", 1) ])).Outcome
                  Assert.equal ReceiptOutcome.Indeterminate (Receipt.compare cmd (host [])).Outcome
                  Assert.equal ReceiptOutcome.Indeterminate (Receipt.compare cmd (host [ ObservedFact.CommandOutcomeUnknown("build", "timeout") ])).Outcome

                  let composite = ExpectedReceipt.AllOf [ cmd; ExpectedReceipt.ArtifactExists "out.json" ]
                  let partial = Receipt.compare composite (host [ ObservedFact.CommandExited("build", 0) ])
                  Assert.equal ReceiptOutcome.Indeterminate partial.Outcome
                  Assert.equal [ ReceiptOutcome.Match; ReceiptOutcome.Indeterminate ] (partial.Constituents |> List.map _.Outcome) }
          { Name = "receipts: self-reported success is never a match; narrative is ignored"
            Run =
              fun () ->
                  let self = { Source = ObservationSource.SelfReported "agent"; Facts = [ ObservedFact.CommandExited("t", 0) ]; Narrative = Some "all good" }
                  Assert.equal ReceiptOutcome.Indeterminate (Receipt.compare (ExpectedReceipt.CommandSucceeded "t") self).Outcome
                  Assert.equal ReceiptOutcome.Mismatch (Receipt.compare (ExpectedReceipt.CommandSucceeded "t") { self with Facts = [ ObservedFact.CommandExited("t", 2) ] }).Outcome }
          { Name = "roles: verification cannot implement; implementation has no evaluator authority"
            Run =
              fun () ->
                  for role in ExecutionRole.all do
                      Assert.isTrue (not (RoleAuthority.allows Capability.ModifyEvaluationAuthority (RoleAuthority.defaultFor role))) $"{role} must not modify evaluators"

                  Assert.isTrue (not (RoleAuthority.allows Capability.ModifyImplementation (RoleAuthority.defaultFor ExecutionRole.Verification))) "verification"
                  Assert.isTrue (not (RoleAuthority.allows Capability.ModifyImplementation (RoleAuthority.defaultFor ExecutionRole.Review))) "review"
                  Assert.isTrue (not (RoleAuthority.allows Capability.ModifyAcceptanceCriteria (RoleAuthority.defaultFor ExecutionRole.Integration))) "integration" }
          { Name = "boundary: explanations do not widen; expansion is explicit and never reaches the evaluator"
            Run =
              fun () ->
                  let effects = MutationBoundary.scopeEffects boundary [ "README.md", Some "quick doc fix"; "src/Installation/A.fs", None; "tests/Installation.Gate.fs", None ]
                  Assert.equal [ "README.md"; "tests/Installation.Gate.fs" ] (effects |> List.map _.Resource)

                  let expansion =
                      { ExpansionId = "EXP-1"
                        Scopes = [ "feature:docs" ]
                        Projections = [ { Scope = "feature:docs"; Patterns = [ "README.md" ] } ]
                        Justification = "docs"
                        AuthorizedBy = "kem" }

                  let widened = MutationBoundary.expand expansion boundary |> ok
                  Assert.equal (MutationClass.Within "feature:docs") (MutationBoundary.classify widened "README.md")
                  Assert.isTrue (Result.isError (MutationBoundary.expand { expansion with Justification = "" } boundary)) "justification required"
                  Assert.isTrue (Result.isError (MutationBoundary.expand { expansion with Projections = [ { Scope = "feature:docs"; Patterns = [ "tests/**" ] } ] } boundary)) "evaluator refused" }
          { Name = "envelope: evaluator closure is excluded and a worktree is not a sandbox"
            Run =
              fun () ->
                  let leaky = { boundary with Projections = [ { Scope = "feature:installation"; Patterns = [ "**" ] } ] }
                  let ev = EvaluatorIdentity.create closure |> ok
                  Assert.isTrue (Result.isError (ExecutionEnvelope.create "EXE-1" "w" (actor "agent") (RoleAuthority.defaultFor ExecutionRole.Implementation) "abc" leaky (Some ev) t0)) "leaky boundary refused"
                  let e = ExecutionEnvelope.create "EXE-1" "w" (actor "agent") (RoleAuthority.defaultFor ExecutionRole.Implementation) "abc" boundary (Some ev) t0 |> ok
                  Assert.equal MutationClass.EvaluatorAuthority (MutationBoundary.classify e.Boundary "ros.json")
                  Assert.isTrue (not (Containment.isSecuritySandbox (Containment.SemanticOnly "git-worktree"))) "worktree is not a sandbox" }
          { Name = "ledger: resume reuses matched steps; unknown effects refuse retry until reconciled"
            Run =
              fun () ->
                  let declared =
                      [ StepEntry.Declared("build", 1, "build", [], ExpectedReceipt.CommandSucceeded "build", None, t0)
                        StepEntry.Declared("publish", 2, "publish", [ "build" ], ExpectedReceipt.CommandSucceeded "publish", None, t0) ]

                  let append entries r = entries @ [ ok r ]
                  let e1 = append declared (StepLedger.start declared "build" t0)
                  let e2 = append e1 (StepLedger.observe e1 "build" (host [ ObservedFact.CommandExited("build", 0) ]) t0)
                  let e3 = append e2 (StepLedger.start e2 "publish" t0)
                  // The process vanished after dispatch: resume from durable entries only.
                  let steps = StepLedger.reconstruct e3
                  Assert.equal StepAction.Reuse (StepLedger.nextAction steps (steps |> List.find (fun s -> s.StepId = "build")))
                  Assert.equal StepAction.Reconcile (StepLedger.nextAction steps (steps |> List.find (fun s -> s.StepId = "publish")))
                  Assert.isTrue (Result.isError (StepLedger.start e3 "publish" t0)) "retry of unknown non-idempotent effect is refused"
                  let e4 = append e3 (StepLedger.reconcile e3 "publish" (Reconciliation.DidNotOccur "version absent") t0)
                  let e5 = append e4 (StepLedger.start e4 "publish" t0)
                  Assert.equal 2 ((StepLedger.reconstruct e5 |> List.find (fun s -> s.StepId = "publish")).Attempts)
                  // Append-only: 2 declarations + start + observe + start + reconcile + start.
                  Assert.equal 7 e5.Length }
          { Name = "legal actions: completion needs matching receipts; human-only is refused to agents"
            Run =
              fun () ->
                  let e = ExecutionEnvelope.create "EXE-1" "w" (actor "agent") (RoleAuthority.defaultFor ExecutionRole.Implementation) "abc" boundary None t0 |> ok
                  let e = { e with HumanOnlyTransitions = [ "execution.complete" ] }
                  let complete kind steps = LegalActions.compute e steps [] None false kind |> List.find (fun a -> a.Transition = "execution.complete")
                  let pending = StepLedger.reconstruct [ StepEntry.Declared("s", 1, "s", [], ExpectedReceipt.CommandSucceeded "c", None, t0) ]
                  Assert.isTrue (not (complete "human" pending).Available) "incomplete steps block completion"
                  Assert.isTrue (complete "human" []).Available "a human may complete"
                  Assert.isTrue (not (complete "agent" []).Available) "an agent may not complete a human-only transition" } ]

    // ---------------------------------------------------------------- CLI

    let private git = GitFixture.git

    let private repository () =
        let dir = GitFixture.temporaryDirectory "exec"
        git dir [ "init"; "-q"; "-b"; "main" ] |> ignore
        GitFixture.configureIdentity dir
        git dir [ "remote"; "add"; "origin"; "https://github.com/echelon-foundry/example.git" ] |> ignore
        Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
        Directory.CreateDirectory(Path.Combine(dir, "tests")) |> ignore
        File.WriteAllText(Path.Combine(dir, "src", "a.txt"), "a")
        File.WriteAllText(Path.Combine(dir, "tests", "gate.sh"), "exit 0\n")
        git dir [ "add"; "-A" ] |> ignore
        git dir [ "commit"; "-q"; "-m"; "baseline" ] |> ignore
        dir

    let private agent: Actor =
        { Kind = ActorKind.Agent; Id = "anthropic/claude-code"; Provider = Some "anthropic"; Model = None; Runtime = Some "claude-code" }

    let private human: Actor = { Kind = ActorKind.Human; Id = "kem"; Provider = None; Model = None; Runtime = None }

    let private capture (f: unit -> int) =
        let previous = Console.Out
        use sink = new StringWriter()
        Console.SetOut sink

        try
            let code = f ()
            code, sink.ToString()
        finally
            Console.SetOut previous

    let private start root extra =
        let code, out =
            capture (fun () ->
                ExecutionCommands.run root agent ([ "start"; "--work-item"; "WI-1"; "--role"; "implementation"; "--scope"; "feature:a"; "--allow"; "feature:a=src/**"; "--evaluator"; "gate-code=tests/gate.sh"; "--json" ] @ extra))

        Assert.equal 0 code
        let node = JsonNode.Parse out
        textAt node [ "executionId" ], node

    let cli =
        [ { Name = "execution: worktree-per-execution with repository-qualified work identity"
            Run =
              fun () ->
                  let root = repository ()
                  let id, node = start root [ "--worktree" ]
                  Assert.equal "echelon-foundry/example:WI-1" (textAt node [ "workItem" ])
                  let binding = node["workspaceBinding"]
                  Assert.equal $"praxis/WI-1/{id}" (textAt binding [ "branch" ])
                  Assert.isTrue (not (Path.IsPathRooted(textAt binding [ "path" ]))) "the workspace path is stored relative, never as an absolute local path"
                  Assert.equal false (boolAt node [ "securitySandbox" ])
                  Assert.equal "semantic-only" (textAt node [ "containment" ])
                  Assert.isTrue (Directory.Exists(Path.Combine(root, textAt binding [ "path" ]))) "worktree exists" }
          { Name = "execution: the same work item gets two independent executions"
            Run =
              fun () ->
                  let root = repository ()
                  let first, a = start root [ "--worktree" ]
                  let second, b = start root [ "--worktree" ]
                  Assert.isTrue (first <> second) "distinct execution identities"
                  Assert.isTrue (textAt a [ "workspaceBinding"; "branch" ] <> textAt b [ "workspaceBinding"; "branch" ]) "distinct branches" }
          { Name = "execution: bounded step, receipt, out-of-bound effect, completion and cleanup"
            Run =
              fun () ->
                  let root = repository ()
                  let id, node = start root [ "--worktree" ]
                  let workspace = Path.Combine(root, textAt node [ "workspaceBinding"; "path" ])
                  let run args = capture (fun () -> ExecutionCommands.run root agent args) |> fst

                  Assert.equal 0 (run [ "step"; "declare"; id; "--step"; "write"; "--expect-command"; "test -f src/b.txt" ])
                  File.WriteAllText(Path.Combine(workspace, "src", "b.txt"), "b")
                  Assert.equal 0 (run [ "step"; "run"; id; "--step"; "write" ])
                  // Resume is reconstructed from durable state: the step is reused.
                  Assert.equal 3 (run [ "step"; "run"; id; "--step"; "write" ])

                  File.WriteAllText(Path.Combine(workspace, "README.md"), "outside")
                  Assert.equal 3 (run [ "boundary"; id ])
                  Assert.equal 3 (run [ "transition"; id; "--action"; "complete" ])
                  File.Delete(Path.Combine(workspace, "README.md"))
                  Assert.equal 0 (run [ "boundary"; id ])
                  // Uncommitted output cannot become a candidate revision.
                  Assert.equal 3 (run [ "transition"; id; "--action"; "complete" ])
                  git workspace [ "add"; "-A" ] |> ignore
                  git workspace [ "commit"; "-q"; "-m"; $"write b (Praxis-Execution: {id})" ] |> ignore
                  Assert.equal 0 (run [ "transition"; id; "--action"; "complete" ])
                  let candidate = git workspace [ "rev-parse"; "HEAD" ]
                  Assert.equal 0 (run [ "cleanup"; id ])
                  Assert.isTrue (not (Directory.Exists workspace)) "worktree removed after a terminal state"
                  let _, shown = capture (fun () -> ExecutionCommands.run root agent [ "show"; id; "--json" ])
                  Assert.equal "completed" (textAt (JsonNode.Parse shown) [ "state" ]) }
          { Name = "execution: cleanup is refused before a terminal state"
            Run =
              fun () ->
                  let root = repository ()
                  let id, _ = start root [ "--worktree" ]
                  Assert.equal 3 (capture (fun () -> ExecutionCommands.run root agent [ "cleanup"; id ]) |> fst) }
          { Name = "execution: evaluator change during candidate evaluation is not reported as pass"
            Run =
              fun () ->
                  let root = repository ()
                  let id, node = start root [ "--worktree" ]
                  let workspace = Path.Combine(root, textAt node [ "workspaceBinding"; "path" ])
                  Assert.equal 0 (capture (fun () -> ExecutionCommands.run root agent [ "evaluate"; id; "--command"; "sh tests/gate.sh" ]) |> fst)
                  File.WriteAllText(Path.Combine(workspace, "tests", "gate.sh"), "exit 0 # weakened\n")
                  let code, out = capture (fun () -> ExecutionCommands.run root agent [ "evaluate"; id; "--command"; "sh tests/gate.sh" ])
                  Assert.equal 3 code
                  Assert.isTrue (out.Contains "evaluator-changed") out }
          { Name = "execution: scope expansion is a legal transition; verification may not expand"
            Run =
              fun () ->
                  let root = repository ()
                  let id, _ = start root []
                  let run actor args = capture (fun () -> ExecutionCommands.run root actor args) |> fst
                  Assert.equal 0 (run human [ "expand-scope"; id; "--scope"; "feature:docs"; "--allow"; "feature:docs=README.md"; "--justification"; "docs" ])

                  let code, out = capture (fun () -> ExecutionCommands.run root agent [ "start"; "--work-item"; "WI-2"; "--role"; "verification"; "--json" ])
                  Assert.equal 0 code
                  let vid = textAt (JsonNode.Parse out) [ "executionId" ]
                  Assert.equal 3 (run agent [ "expand-scope"; vid; "--scope"; "feature:docs"; "--justification"; "x" ]) } ]

    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "release.json")) && Directory.Exists(Path.Combine(directory.FullName, "vendor")) then
            directory.FullName
        else
            match directory.Parent with
            | null -> failwith "repository root not found"
            | parent -> repositoryRoot parent

    let private lockValue (text: string) (key: string) =
        text.Split('\n')
        |> Array.map _.Trim()
        |> Array.tryPick (fun line -> if line.StartsWith(key + " ") then Some(line.Substring(key.Length + 1).Trim()) else None)
        |> Option.defaultWith (fun () -> failwith $"ordo-core.lock has no {key}")

    /// ORDO-CORE-PACKAGE (PRAXIS-FND-01; PRX-ARCH-001, PRX-EXEC-002): Ordo's
    /// semantics are consumed from the pinned package, never copied.
    let ordoPackage =
        [ { Name = "ordo core: the Praxis and Ordo capability and role vocabularies are one-to-one"
            Run =
              fun () ->
                  Assert.equal (Set.ofList (OrdoSemantics.ordoCapabilities ())) (Capability.all |> List.map Capability.toWire |> Set.ofList)
                  Assert.equal (Capability.all.Length) (OrdoSemantics.ordoCapabilities ()).Length

                  for role in ExecutionRole.all do
                      Assert.equal (ExecutionRole.toWire role) (Ordo.Core.ExecutionRole.ExecutionRole.toWire (OrdoSemantics.role role))

                  Assert.equal [ "gate-code"; "configuration"; "test-selection"; "schema"; "fixture"; "generated-input"; "policy"; "dependency" ] EvaluatorIdentity.kinds }
          { Name = "ordo core: every role's capability set is Ordo.Core's role authority"
            Run =
              fun () ->
                  for role in ExecutionRole.all do
                      let ordo = Ordo.Core.ExecutionRole.RoleAuthority.defaultFor (OrdoSemantics.role role)
                      let praxis = RoleAuthority.defaultFor role
                      let wire (caps: Set<Capability>) = caps |> Set.map Capability.toWire
                      Assert.equal (ordo.Grants |> Set.map Ordo.Core.ExecutionRole.ExecutionCapability.toWire) (wire praxis.Grants)
                      Assert.equal (ordo.Prohibits |> Set.map Ordo.Core.ExecutionRole.ExecutionCapability.toWire) (wire praxis.Prohibits) }
          { Name = "ordo core: boundary classification and expansion are Ordo.Core's"
            Run =
              fun () ->
                  let ordoBoundary: Ordo.Core.MutationBoundary.MutationBoundary =
                      { Scopes = [ Ordo.Core.MutationBoundary.Feature "installation" ]
                        Projections = [ { Scope = Ordo.Core.MutationBoundary.Feature "installation"; Patterns = [ "src/Installation/**" ] } ]
                        EvaluatorReferences = [ "tests/Installation.Gate.fs" ] }

                  for resource in [ "src/Installation/a.fs"; "tests/Installation.Gate.fs"; "README.md" ] do
                      let expected =
                          match Ordo.Core.MutationBoundary.MutationBoundary.classify ordoBoundary resource with
                          | Ordo.Core.MutationBoundary.WithinBoundary _ -> "within"
                          | Ordo.Core.MutationBoundary.OutsideBoundary -> "outside"
                          | Ordo.Core.MutationBoundary.EvaluatorAuthorityMutation -> "evaluator"

                      let actual =
                          match MutationBoundary.classify boundary resource with
                          | MutationClass.Within _ -> "within"
                          | MutationClass.Outside -> "outside"
                          | MutationClass.EvaluatorAuthority -> "evaluator"

                      Assert.equal expected actual }
          { Name = "ordo core: the package is the pinned, checksum-verified Ordo release artifact"
            Run =
              fun () ->
                  let root = repositoryRoot (DirectoryInfo AppContext.BaseDirectory)
                  let lock = File.ReadAllText(Path.Combine(root, "vendor", "nuget", "ordo-core.lock"))
                  let version = lockValue lock "ordo"
                  let package = Path.Combine(root, "vendor", "nuget", $"echelonfoundry.ordo.core.{version}.nupkg")
                  let digest = Convert.ToHexString(Security.Cryptography.SHA256.HashData(File.ReadAllBytes package)).ToLowerInvariant()
                  Assert.equal (lockValue lock "sha256") digest
                  Assert.equal $"https://github.com/kemiller2002/ordo/releases/download/v{version}/ordo-core.nupkg" (lockValue lock "url")
                  let domain = File.ReadAllText(Path.Combine(root, "src", "Praxis.Domain", "Praxis.Domain.fsproj"))
                  Assert.isTrue (domain.Contains $"<PackageReference Include=\"EchelonFoundry.Ordo.Core\" Version=\"{version}\" />") "Praxis.Domain pins the locked version"
                  let config = File.ReadAllText(Path.Combine(root, "NuGet.config"))
                  Assert.isTrue (config.Contains "<package pattern=\"EchelonFoundry.Ordo.Core\" />") "the package restores only from the vendored source"
                  Assert.equal [ $"echelonfoundry.ordo.core.{version}.nupkg"; "ordo-core.lock" ] (Directory.GetFiles(Path.Combine(root, "vendor", "nuget")) |> Array.map Path.GetFileName |> Array.sort |> List.ofArray) }
          { Name = "ordo core: Praxis keeps no copy of Ordo's role matrix, fingerprint or glob rules"
            Run =
              fun () ->
                  let root = repositoryRoot (DirectoryInfo AppContext.BaseDirectory)
                  let governance = File.ReadAllText(Path.Combine(root, "src", "Praxis.Domain", "Execution", "Governance.fs"))

                  for forbidden in [ "SHA256.HashData"; "StringBuilder"; "| ExecutionRole.Specification ->\n            make"; "let rec private segment" ] do
                      Assert.isTrue (not (governance.Contains forbidden)) $"Governance.fs re-implements Ordo: {forbidden}"

                  Assert.isTrue (governance.Contains "Ordo.Core.ExecutionRole.RoleAuthority.defaultFor") "role authority comes from Ordo.Core"
                  Assert.isTrue (not (governance.Contains "ORDO-CORE-PACKAGE).") || governance.Contains "consumes it from the released") "header names the consumed package" } ]

    let tests = domain @ ordoPackage @ cli
