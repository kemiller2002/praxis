namespace Praxis.Tests

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Construction
open Praxis.Infrastructure.Execution
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// Mandatory construction-package scope verification (GH-234, WI-0097,
/// DF-ROS-2026-A074): real `praxis construct apply`/`extend` through the CLI,
/// the real host construction journal and protected policy, and the host
/// validator wired into the existing worker checklist. Journal forgeries below
/// simulate a compromised or replayed host root; the check must refuse them
/// from Git objects and re-admission, never trust the journal.
[<RequireQualifiedAccess>]
module ConstructionScopeTests =
    open PraxisCli

    let private t name run = { Name = "construction scope: " + name; Run = run }
    let private group = ConstructionCliTests.group
    let private target = ConstructionCliTests.target
    let private okr = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private sha c = "sha256:" + String(c, 64)

    type private Fixture =
        { Host: ConstructionCliTests.Host
          Configuration: LocalHostConfiguration
          Source: string }

    let private load (fixture: Fixture) () = (FileLocalRuntime.hostStore fixture.Configuration).Load()

    let private configured (host: ConstructionCliTests.Host) =
        LocalHostConfiguration.load host.Config |> okr

    let private withFixture test =
        ConstructionCliTests.withHost true ignore (fun host ->
            test { Host = host; Configuration = configured host; Source = ConstructionCliTests.tip host })

    /// A packet exactly as a coordinator derives it from the current grant.
    let private packetFor (fixture: Fixture) : LocalWorkerPacket =
        let policy = load fixture () |> okr
        let grant = policy.Grants |> List.find (fun grant -> grant.GroupId = group)
        let receipt = EcirApprovals.read grant.ReceiptJson |> okr
        let now = DateTimeOffset.UtcNow
        let issued = DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero).AddMinutes(-1.)
        { LocalAgentHandoffTests.packet with
            RepositoryIdentity = fixture.Configuration.RepositoryIdentity; SourceCommit = fixture.Source
            SourceManifestDigest = grant.Source.SourceManifestDigest; BlueprintDigest = grant.BlueprintDigest
            GroupId = group; CohortId = grant.CohortId
            Members = [ { WorkItemId = "WI-TIMER"; ExecutionId = "EXE-TIMER"; RequirementKeys = [ ConstructionCliTests.requirement ] } ]
            DecisionIds = receipt.Scope.DecisionIds; AllowedPaths = [ "src/Domain/Extensions.fs" ]; Prerequisites = []
            Acceptance = [ { ObligationId = "VERIFY-TIMER"; WorkItemId = "WI-TIMER"; RequirementKeys = [ ConstructionCliTests.requirement ]
                             ValidatorId = "independent-test"; InputDigest = sha 'e' } ]
            AuthorityRevision = policy.Revision; ReceiptDigest = "sha256:" + EcirDispatchTransaction.hashText grant.ReceiptJson
            IssuedAt = issued; ExpiresAt = issued.AddMinutes 30. }

    let private identify (fixture: Fixture) configuration packet candidate =
        ConstructionScope.identify configuration (load fixture) packet candidate |> okr

    /// Identification then verification, as the checklist adapter performs them.
    let private observeWith (fixture: Fixture) configuration packet candidate =
        match identify fixture configuration packet candidate with
        | None -> failwith "a construction package was not identified"
        | Some validator -> ConstructionScope.verify configuration (load fixture) (fun () -> DateTimeOffset.UtcNow) packet candidate validator

    let private observe (fixture: Fixture) candidate = observeWith fixture fixture.Configuration (packetFor fixture) candidate

    let private passes (observation: LocalConstructionScopeObservation) =
        Assert.isTrue observation.Reasons.IsEmpty (sprintf "construction scope refused: %A" observation.Reasons)

    let private refuses (fragment: string) (observation: LocalConstructionScopeObservation) =
        Assert.isTrue (observation.Reasons |> List.exists (fun reason -> reason.Contains(fragment, StringComparison.Ordinal)))
            (sprintf "expected %s in %A" fragment observation.Reasons)

    let private apply (fixture: Fixture) plan = ConstructionCliTests.construct fixture.Host "apply" plan |> ok
    let private handwritten = ConstructionCliTests.handwritten

    let private extend (fixture: Fixture) =
        ConstructionCliTests.extend fixture.Host (ConstructionCliTests.outbox fixture.Host [ "Extensions.fs", handwritten ]) |> ok

    let private messageOf (fixture: Fixture) commit = GitFixture.git fixture.Host.Clone [ "log"; "-1"; "--format=%B"; commit ]

    /// A new commit on `parent` whose tree is `commit`'s tree changed by `edit`.
    let private forge (fixture: Fixture) (commit: string) (parent: string) (edit: string -> unit) (message: string) =
        let scratch = Path.Combine(fixture.Host.Parent, "forge-" + Guid.NewGuid().ToString("N"))
        GitFixture.git fixture.Host.Clone [ "worktree"; "add"; "--detach"; scratch; commit ] |> ignore
        edit scratch
        GitFixture.git scratch [ "add"; "-A" ] |> ignore
        let tree = GitFixture.git scratch [ "write-tree" ]
        GitFixture.git fixture.Host.Clone [ "worktree"; "remove"; "--force"; scratch ] |> ignore
        GitFixture.git fixture.Host.Clone [ "commit-tree"; tree; "-p"; parent; "-m"; message ]

    let private write (root: string) (path: string) (content: string) =
        let full = Path.Combine(root, path)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)

    let private reserved = [ "locks"; "plans"; "routes" ]

    let private entries (fixture: Fixture) =
        Directory.GetDirectories fixture.Host.Journal
        |> Array.filter (fun path -> not (List.contains (Path.GetFileName path) reserved))
        |> Array.map (fun path -> path, File.ReadAllText(Path.Combine(path, "plan.json")) |> LocalIntegrationJournalJson.readPlan |> okr)
        |> Array.toList

    let private entryFor (fixture: Fixture) candidate =
        entries fixture |> List.find (fun (_, plan) -> plan.CandidateCommit = candidate)

    /// Rewrite an entry with digests recomputed, as a writer of the host root
    /// could; the receipt stays consistent so only re-derivation can refuse it.
    let private rewrite (directory: string, plan: LocalIntegrationPlan) (change: LocalIntegrationPlan -> LocalIntegrationPlan) =
        let changed = change plan
        File.WriteAllText(Path.Combine(directory, "plan.json"), LocalIntegrationJournalJson.renderPlan changed)
        let applied = Path.Combine(directory, "applied.json")
        if File.Exists applied then File.WriteAllText(applied, LocalIntegrationJournalJson.renderApplied changed DateTimeOffset.UtcNow)

    let private planDigest (applied: PraxisCli.Result) = text applied.Json["binding"].["planDigest"]

    let private rewordedPlan (fixture: Fixture) =
        let node = JsonNode.Parse(File.ReadAllText fixture.Host.Plan)
        let unresolved = node["operations"] :?> JsonArray |> Seq.find (fun op -> text op["op"] = "declareUnresolved")
        unresolved["question"] <- JsonValue.Create(text unresolved["question"] + " Reworded.")
        let path = Path.Combine(fixture.Host.Parent, "reworded-plan.json")
        File.WriteAllText(path, node.ToJsonString())
        path

    // ------------------------------------------------------------ checklist

    /// The real concrete checklist adapter: host-selected validator processes
    /// for the system and acceptance rows, plus the host construction-scope
    /// validator. Nothing here is worker-supplied.
    let private checklist (fixture: Fixture) (candidates: string list) (action: LocalWorkerPacket -> (string -> Result<WorkerChecklistAssessment, string>) -> unit) =
        let configuration = fixture.Configuration
        let evidence = configuration.EvidenceRoot
        let git = configuration.Git
        let dependency = Path.Combine(AppContext.BaseDirectory, "Praxis.Tests.dll")
        let checkouts = Dictionary<string, string>()
        let checkout candidate =
            match checkouts.TryGetValue candidate with
            | true, path -> path
            | _ ->
                let path = FileLocalGitCandidates.provisionCheckout git fixture.Host.Clone configuration.CheckoutRoot "scope" candidate CancellationToken.None |> okr
                checkouts[candidate] <- path
                path
        let request (row: WorkerChecklistRequirement) candidate : LocalValidatorRequest =
            let input = if row.Kind = WorkerCheckKind.Acceptance then row.CheckId else WorkerChecklist.kindCode row.Kind
            let directory = checkout candidate
            let spec, trx =
                if row.Kind = WorkerCheckKind.Tests then
                    let path = Path.Combine(evidence, "tests-" + candidate.Substring(0, 12) + ".trx")
                    { LocalWorkerProcessTests.spec "validator-trx" [ path; "pass" ] with WorkingDirectory = directory; Input = input }, Some path
                else { LocalWorkerProcessTests.spec "echo" [] with WorkingDirectory = directory; Input = input }, None
            { ValidatorId = "host-" + input; CandidateCommit = candidate
              GitExecutable = git.Executable; GitExecutableDigest = git.ExecutableDigest
              Dependencies = Map.ofList [ dependency, GitWorkspace.fileDigest dependency |> okr ]
              Process = spec; TrxPath = trx; EvidenceRoot = evidence }
        let packet =
            let original = packetFor fixture
            let row = WorkerChecklist.requirements original |> okr |> List.find (fun row -> row.Kind = WorkerCheckKind.Acceptance)
            let inputs = FileLocalValidator.observe (request row (List.head candidates)) CancellationToken.None |> fun task -> task.GetAwaiter().GetResult() |> okr
            { original with Acceptance = original.Acceptance |> List.map (fun a -> { a with ValidatorId = inputs.ValidatorId; InputDigest = inputs.ValidatorInputDigest }) }
        let validators : LocalChecklistValidators =
            { PolicyRevision = fun () -> Ok "host-validators-1"
              Select = fun _ row candidate -> Ok(request row candidate)
              Construction = ConstructionScope.host configuration (load fixture) (fun () -> DateTimeOffset.UtcNow) }
        FileWorkerChecklist.withEvidenceSession fixture.Host.Clone evidence LocalHostFileAccess.portableDevelopment validators
            (fun () -> DateTimeOffset.UtcNow) CancellationToken.None (fun session ->
                action packet (fun candidate ->
                    let result = { LocalAgentHandoffTests.result with OutputCommit = candidate; PacketDigest = LocalAgentHandoff.packetDigest packet }
                    WorkerChecklistOperations.verify session.Checks packet result)
                Ok())
        |> okr

    let private evidenceDocuments (fixture: Fixture) =
        Directory.GetFiles(fixture.Configuration.EvidenceRoot, "construction-scope-*.json")
        |> Array.map (fun path -> JsonNode.Parse(File.ReadAllText path)) |> Array.toList

    let tests =
        [ t "end to end: construct, extend, then the mandatory row passes the real checklist on the exact candidate" (fun () ->
              withFixture (fun fixture ->
                  apply fixture fixture.Host.Plan |> ignore
                  let extended = extend fixture
                  let candidate = text extended.Json["candidateCommit"]
                  Assert.equal candidate (ConstructionCliTests.tip fixture.Host)
                  checklist fixture [ candidate ] (fun packet verify ->
                      let assessment = verify candidate |> okr
                      Assert.isTrue (WorkerChecklist.isReady assessment) (sprintf "%A" (WorkerChecklist.blockingReasons assessment))
                      // Generated, not chosen: four system rows, the acceptance row
                      // and the construction-scope row, each bound into the digest.
                      Assert.equal 6 assessment.Rows.Length
                      let row = assessment.Rows |> List.find (fun row -> row.Row.Kind = WorkerCheckKind.ConstructionScope)
                      Assert.equal "system/WI-TIMER/construction-scope" row.Row.CheckId
                      Assert.equal WorkerChecklist.ConstructionScopeValidatorId row.Row.Validator.ValidatorId
                      Assert.equal packet.GroupId group)
                  let documents = evidenceDocuments fixture
                  Assert.isTrue (not documents.IsEmpty) "construction-scope evidence was not archived in the evidence store"
                  for document in documents do
                      Assert.equal "passed" (text document["verdict"])
                      Assert.equal candidate (text document["candidateCommit"])
                      Assert.equal "src/Domain/generated/Timer/" (text document["pins"].["ownershipRoot"])
                      Assert.equal 2 (document["pins"].["chain"] :?> JsonArray).Count))
          t "identification uses only protected facts: journal, trailers or generated paths; omitting trailers cannot evade the row" (fun () ->
              withFixture (fun fixture ->
                  let packet = packetFor fixture
                  // A plain worker change in a group the host never constructed.
                  let plain = forge fixture fixture.Source fixture.Source (fun root -> write root "src/Other.fs" "module Other\n") "plain work"
                  Assert.equal None (identify fixture fixture.Configuration packet plain)
                  // (c) Hand-written generated files without trailers.
                  let generated = forge fixture fixture.Source fixture.Source (fun root -> write root "src/Domain/generated/Timer/Forged.fs" "module Forged\n") "no trailers"
                  Assert.isTrue (identify fixture fixture.Configuration packet generated |> Option.isSome) "generated paths did not identify a construction package"
                  observeWith fixture fixture.Configuration packet generated |> refuses "construction-scope-evidence-missing"
                  // (b) Construction trailers on a host where construction is disabled.
                  let applied = apply fixture fixture.Host.Plan
                  let construction = text applied.Json["candidateCommit"]
                  let disabled = { fixture.Configuration with ConstructionRoot = None }
                  Assert.isTrue (identify fixture disabled packet construction |> Option.isSome) "trailers did not identify a construction package"
                  observeWith fixture disabled packet construction |> refuses "construction-scope-journal-unavailable"
                  // (a) The group is journalled: a trailer-free copy of the same tree
                  // is still a construction package, and is not host-applied.
                  let copied = GitFixture.git fixture.Host.Clone [ "commit-tree"; construction + "^{tree}"; "-p"; fixture.Source; "-m"; "copied without provenance" ]
                  let unrelated = forge fixture fixture.Source fixture.Source (fun root -> write root "src/Other.fs" "module Other\n") "plain work"
                  Assert.isTrue (identify fixture fixture.Configuration packet unrelated |> Option.isSome) "a constructed group escaped the row without trailers"
                  observeWith fixture fixture.Configuration packet copied |> refuses "construction-scope-candidate-not-host-applied"
                  observeWith fixture fixture.Configuration packet unrelated |> refuses "construction-scope-candidate-not-host-applied"))
          t "journal tampering is refused: altered bytes, and digests recomputed by a writer of the host root" (fun () ->
              withFixture (fun fixture ->
                  let applied = apply fixture fixture.Host.Plan
                  let construction = text applied.Json["candidateCommit"]
                  observe fixture construction |> passes
                  let directory, plan = entryFor fixture construction
                  let original = File.ReadAllText(Path.Combine(directory, "plan.json"))
                  // Bytes: the applied record no longer binds the plan.
                  File.WriteAllText(Path.Combine(directory, "plan.json"), original.Replace(plan.SubmissionDigest, sha '4'))
                  observe fixture construction |> refuses "construction-scope-journal-unreadable"
                  File.WriteAllText(Path.Combine(directory, "plan.json"), "{ not json")
                  observe fixture construction |> refuses "construction-scope-journal-unreadable"
                  // Digests: a consistent forgery of the plan and submission digests.
                  rewrite (directory, plan) (fun plan -> { plan with PacketDigest = sha '5' })
                  observe fixture construction |> refuses "construction-scope-plan-digest-mismatch"
                  rewrite (directory, plan) (fun plan -> { plan with SubmissionDigest = sha '6' })
                  observe fixture construction |> refuses "construction-scope-digest-inconsistent"
                  rewrite (directory, plan) id
                  observe fixture construction |> passes))
          t "replay of an older construction or an older journal is refused" (fun () ->
              withFixture (fun fixture ->
                  let first = text (apply fixture fixture.Host.Plan).Json["candidateCommit"]
                  let snapshot = Path.Combine(fixture.Host.Parent, "journal-snapshot")
                  for directory, _ in entries fixture do
                      let copy = Path.Combine(snapshot, Path.GetFileName directory)
                      Directory.CreateDirectory copy |> ignore
                      for file in Directory.GetFiles directory do File.Copy(file, Path.Combine(copy, Path.GetFileName file))
                  let extended = text (extend fixture).Json["candidateCommit"]
                  observe fixture extended |> passes
                  // Older journal: the host root rolled back to before the extension.
                  let extension, _ = entryFor fixture extended
                  Directory.Delete(extension, true)
                  observe fixture extended |> refuses "construction-scope-candidate-not-host-applied"
                  for directory in Directory.GetDirectories snapshot do
                      let target = Path.Combine(fixture.Host.Journal, Path.GetFileName directory)
                      for file in Directory.GetFiles directory do File.Copy(file, Path.Combine(target, Path.GetFileName file), true)
                  // Older construction: a newer plan was applied after it.
                  GitFixture.git fixture.Host.Clone [ "update-ref"; target; first ] |> ignore
                  let reworded = apply fixture (rewordedPlan fixture)
                  let second = text reworded.Json["candidateCommit"]
                  observe fixture first |> refuses "construction-scope-evidence-stale"
                  // The rolled-back journal no longer holds the extension, so the
                  // newer construction alone is current, and it passes.
                  observe fixture second |> passes
                  Assert.isTrue (planDigest reworded <> text (ConstructionCliTests.construct fixture.Host "preview" fixture.Host.Plan |> ok).Json["binding"].["planDigest"]) "the reworded plan kept its digest"))
          t "omitted and extra writes are refused inside the ownership root, outside it and through a non-slot path" (fun () ->
              withFixture (fun fixture ->
                  let applied = apply fixture fixture.Host.Plan
                  let construction = text applied.Json["candidateCommit"]
                  let message = messageOf fixture construction
                  let entry = entryFor fixture construction
                  let generated = (applied.Json["writes"] :?> JsonArray |> Seq.map (fun write -> text write["path"]) |> Seq.find (fun path -> path.EndsWith ".fs"))
                  let substitute edit =
                      let forged = forge fixture construction fixture.Source edit message
                      rewrite entry (fun plan -> { plan with CandidateCommit = forged })
                      forged
                  let omitted = substitute (fun root -> File.Delete(Path.Combine(root, generated)))
                  let observed = observe fixture omitted
                  observed |> refuses ("construction-scope-operation-missing: " + generated)
                  observed |> refuses "construction-scope-operation-mismatch"
                  let inside = substitute (fun root -> write root "src/Domain/generated/Timer/Extra.fs" "module Extra\n")
                  observe fixture inside |> refuses "construction-scope-unexpected-file: src/Domain/generated/Timer/Extra.fs (inside the generated ownership root)"
                  let outside = substitute (fun root -> write root "src/Other.fs" "module Other\n")
                  observe fixture outside |> refuses "construction-scope-unexpected-file: src/Other.fs (outside the generated ownership root and the host-declared extension slots)"
                  rewrite entry (fun plan -> { plan with CandidateCommit = construction })
                  observe fixture construction |> passes
                  // Through an extension: a non-slot file beside the declared slot.
                  let extended = text (extend fixture).Json["candidateCommit"]
                  let extension = entryFor fixture extended
                  let forged = forge fixture extended construction (fun root -> write root "src/Domain/Helpers.fs" "module Helpers\n") (messageOf fixture extended)
                  rewrite extension (fun plan -> { plan with CandidateCommit = forged })
                  let observed = observe fixture forged
                  observed |> refuses "construction-scope-unexpected-file: src/Domain/Helpers.fs (written by"
                  observed |> refuses "construction-scope-unexpected-file: src/Domain/Helpers.fs (outside the generated ownership root"
                  // An unjournalled worker commit on top of the exact construction.
                  rewrite extension (fun plan -> { plan with CandidateCommit = extended })
                  let extra = forge fixture extended extended (fun root -> write root "src/Domain/Extensions.fs" "module Replaced\n") "worker edit"
                  observe fixture extra |> refuses "construction-scope-candidate-not-host-applied"
                  observe fixture extended |> passes))
          t "candidate-commit substitution is refused: same tree, different commit" (fun () ->
              withFixture (fun fixture ->
                  apply fixture fixture.Host.Plan |> ignore
                  let extended = text (extend fixture).Json["candidateCommit"]
                  let parent = GitFixture.git fixture.Host.Clone [ "rev-parse"; extended + "^" ]
                  let twin = GitFixture.git fixture.Host.Clone [ "commit-tree"; extended + "^{tree}"; "-p"; parent; "-m"; messageOf fixture extended + "\n\nresubmitted" ]
                  Assert.isTrue (twin <> extended) "the substitute is the same commit"
                  observe fixture twin |> refuses "construction-scope-candidate-not-host-applied"
                  observe fixture parent |> refuses "construction-scope-evidence-stale"
                  observe fixture extended |> passes))
          t "crash recovery: an unreconciled transaction refuses until a rerun reconciles it" (fun () ->
              withFixture (fun fixture ->
                  let applied = apply fixture fixture.Host.Plan
                  let candidate = text applied.Json["candidateCommit"]
                  let directory, _ = entryFor fixture candidate
                  let receipt = Path.Combine(directory, "applied.json")
                  // Crash after update-ref, before the applied record.
                  File.Delete receipt
                  observe fixture candidate |> refuses "construction-scope-journal-unreconciled"
                  Assert.equal "already-current" (text (apply fixture fixture.Host.Plan).Json["outcome"])
                  Assert.isTrue (File.Exists receipt) "the rerun did not reconcile the interrupted apply"
                  observe fixture candidate |> passes
                  // Crash after the journal entry, before update-ref: still pending.
                  File.Delete receipt
                  GitFixture.git fixture.Host.Clone [ "update-ref"; target; fixture.Source; candidate ] |> ignore
                  observe fixture candidate |> refuses "construction-scope-journal-unreconciled"
                  Assert.equal "applied" (text (apply fixture fixture.Host.Plan).Json["outcome"])
                  observe fixture candidate |> passes
                  // An incomplete entry (no plan) is refused, never skipped.
                  File.Delete(Path.Combine(directory, "plan.json"))
                  observe fixture candidate |> refuses "construction-scope-journal-unreadable"))
          t "stale plan digest, wrong generator, cohort, requirement set and a revoked signer are refused" (fun () ->
              withFixture (fun fixture ->
                  let applied = apply fixture fixture.Host.Plan
                  let construction = text applied.Json["candidateCommit"]
                  observe fixture construction |> passes
                  // Stale plan digest: the plan record re-admits to another plan.
                  let digest = planDigest applied
                  let record = Path.Combine(fixture.Host.Journal, "plans", digest.Substring 7 + ".json")
                  let recorded = File.ReadAllText record
                  File.WriteAllText(record, File.ReadAllText(rewordedPlan fixture))
                  observe fixture construction |> refuses "construction-scope-plan-digest-mismatch: the host plan record re-admits to"
                  File.WriteAllText(record, recorded)
                  // Wrong generator version on an otherwise identical construction.
                  let message = messageOf fixture construction
                  let generator = message.Split('\n') |> Array.find (fun line -> line.StartsWith "Construction-Generator: ")
                  let forged = GitFixture.git fixture.Host.Clone [ "commit-tree"; construction + "^{tree}"; "-p"; fixture.Source; "-m"; message.Replace(generator, "Construction-Generator: ordo.construction/0") ]
                  let entry = entryFor fixture construction
                  rewrite entry (fun plan -> { plan with CandidateCommit = forged })
                  observe fixture forged |> refuses "construction-scope-generator-mismatch"
                  rewrite entry (fun plan -> { plan with CandidateCommit = construction })
                  // Wrong cohort and requirement set in the delegated packet.
                  let packet = packetFor fixture
                  observeWith fixture fixture.Configuration { packet with CohortId = "COH-OTHER" } construction |> refuses "construction-scope-cohort-mismatch"
                  let other = "kemiller2002/time-entry-state-machine/docs/other.md#L1-L2"
                  let widened =
                      { packet with
                          Members = packet.Members |> List.map (fun m -> { m with RequirementKeys = [ other ] })
                          Acceptance = packet.Acceptance |> List.map (fun a -> { a with RequirementKeys = [ other ] }) }
                  observeWith fixture fixture.Configuration widened construction |> refuses "construction-scope-requirement-set-mismatch"
                  // A revoked signer: authority is re-verified at check time.
                  PraxisCli.run fixture.Host.Clone (Some ConstructionCliTests.coordinator)
                      [ "local"; "operator"; "revoke"; "--host-config"; fixture.Host.Config; "--key-id"; "op-1"; "--revision"; "rev-3" ] |> ok |> ignore
                  observeWith fixture fixture.Configuration packet construction |> refuses "construction-scope-authorization-refused"))
          t "agent-supplied journal contents and unauthorized slot-policy changes are never authoritative" (fun () ->
              withFixture (fun fixture ->
                  apply fixture fixture.Host.Plan |> ignore
                  let extended = text (extend fixture).Json["candidateCommit"]
                  let packet = packetFor fixture
                  observe fixture extended |> passes
                  // A journal in a worker-writable place: inside the repository,
                  // or around the worker checkout root.
                  let inRepository = Path.Combine(fixture.Host.Clone, ".praxis-construction")
                  Directory.CreateDirectory inRepository |> ignore
                  observeWith fixture { fixture.Configuration with ConstructionRoot = Some inRepository } packet extended |> refuses "construction-scope-journal-untrusted"
                  let aroundCheckouts = Path.GetDirectoryName fixture.Configuration.CheckoutRoot
                  observeWith fixture { fixture.Configuration with ConstructionRoot = Some aroundCheckouts } packet extended
                  |> refuses "construction-scope-journal-untrusted: the construction root overlaps a worker-writable host root"
                  // An agent-written entry for its own commit, with copied trailers.
                  let worker = forge fixture extended extended (fun root -> write root "src/Domain/Extensions.fs" "module Agent\n") (messageOf fixture extended)
                  let directory, plan = entryFor fixture extended
                  let forgedDirectory = Path.Combine(fixture.Host.Journal, "agent-entry")
                  Directory.CreateDirectory forgedDirectory |> ignore
                  let forgedPlan = { plan with DispatchId = "extend-agent"; BeforeCommit = extended; CandidateCommit = worker }
                  File.WriteAllText(Path.Combine(forgedDirectory, "plan.json"), LocalIntegrationJournalJson.renderPlan forgedPlan)
                  File.WriteAllText(Path.Combine(forgedDirectory, "applied.json"), LocalIntegrationJournalJson.renderApplied forgedPlan DateTimeOffset.UtcNow)
                  observe fixture worker |> refuses "construction-scope-journal-unreadable"
                  Directory.Delete(forgedDirectory, true)
                  ignore directory
                  observe fixture extended |> passes
                  // The host slot policy changed after the extension was routed.
                  ConstructionCliTests.reconfigure fixture.Host
                      """[{"file":"Extensions.fs","contracts":["ITimerPorts.CurrentTime","ITimerPorts.Perform/Stop"],"maxBytes":8192}]""" "src/Domain"
                  let changed = configured fixture.Host
                  observeWith fixture changed packet extended |> refuses "construction-scope-slot-policy-changed"))
          t "preview and diagnostics stay read-only and usable while completion acceptance refuses" (fun () ->
              withFixture (fun fixture ->
                  let applied = apply fixture fixture.Host.Plan
                  let candidate = text applied.Json["candidateCommit"]
                  let directory, _ = entryFor fixture candidate
                  File.Delete(Path.Combine(directory, "applied.json"))
                  observe fixture candidate |> refuses "construction-scope-journal-unreconciled"
                  let journal () =
                      Directory.GetFiles(fixture.Host.Journal, "*", SearchOption.AllDirectories)
                      |> Array.filter (fun path -> not (path.Contains(string Path.DirectorySeparatorChar + "locks" + string Path.DirectorySeparatorChar)))
                      |> Array.map (fun path -> path, File.ReadAllText path) |> Array.sort
                  let before = journal ()
                  let tip = ConstructionCliTests.tip fixture.Host
                  let preview = ConstructionCliTests.construct fixture.Host "preview" fixture.Host.Plan |> ok
                  Assert.equal "praxis.construction-preview/1" (text preview.Json["schemaVersion"])
                  Assert.equal (planDigest applied) (planDigest preview)
                  Assert.equal tip (ConstructionCliTests.tip fixture.Host)
                  Assert.equal before (journal ())
                  observe fixture candidate |> refuses "construction-scope-journal-unreconciled"))
          t "the checklist refuses a construction package with every precise reason, and the row cannot be dropped" (fun () ->
              withFixture (fun fixture ->
                  apply fixture fixture.Host.Plan |> ignore
                  let extended = text (extend fixture).Json["candidateCommit"]
                  let extra = forge fixture extended extended (fun root -> write root "src/Other.fs" "module Other\n") "worker edit"
                  checklist fixture [ extended; extra ] (fun packet verify ->
                      match verify extra with
                      | Ok _ -> failwith "an unjournalled construction candidate passed the checklist"
                      | Error reason ->
                          Assert.isTrue (reason.Contains "system/WI-TIMER/construction-scope:checklist-check-failed: construction-scope-candidate-not-host-applied") reason
                      // A host that forgets the construction capability is caught by
                      // the generated identity: the bound checklist differs.
                      verify extended |> okr |> ignore)
                  let refusedDocuments = evidenceDocuments fixture |> List.filter (fun document -> text document["verdict"] = "refused")
                  Assert.isTrue (not refusedDocuments.IsEmpty) "the refusal was not archived as evidence")) ]
