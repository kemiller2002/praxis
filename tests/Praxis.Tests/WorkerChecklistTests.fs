namespace Praxis.Tests

open System
open Praxis.Domain.Work
open Praxis.Application.Work

[<RequireQualifiedAccess>]
module WorkerChecklistTests =
    let private t name run = { Name = "worker checklist: " + name; Run = run }
    let private sha c = "sha256:" + String(c, 64)
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private packet = LocalAgentHandoffTests.packet
    let private result = LocalAgentHandoffTests.result
    let private now = packet.IssuedAt.AddMinutes 5.
    let internal context (packet: LocalWorkerPacket) (result: LocalWorkerResult) : WorkerChecklistContext =
        { PacketDigest = LocalAgentHandoff.packetDigest packet; CandidateCommit = result.OutputCommit; CandidateManifestDigest = sha '9'
          Policy = { Revision = "fixture-policy-1"; Validators = WorkerChecklist.requiredKinds |> List.map (fun kind ->
              kind, { ValidatorId = "fixture-" + WorkerChecklist.kindCode kind; InputDigest = sha '8' }) |> Map.ofList } }
    let internal runs (now: DateTimeOffset) (checklist: WorkerChecklist) : WorkerCheckRun list =
        checklist.Rows |> List.map (fun row ->
            { ChecklistDigest = WorkerChecklist.digest checklist; CheckId = row.CheckId; Validator = row.Validator
              StartedAt = now.AddSeconds(-1.); FinishedAt = now; Verdict = WorkerCheckVerdict.Passed
              TestsExecuted = (if row.Kind = WorkerCheckKind.Tests then Some 3 else None); EvidenceDigest = sha '7' })
    /// Synthetic protected-observer results, not actual compiler/test evidence.
    let internal ports packet result clock : WorkerChecklistPorts =
        { Observe = fun _ _ candidate -> Ok { context packet result with CandidateCommit = candidate }
          Evaluate = fun checklist -> Ok(runs (clock()) checklist)
          Now = clock }
    let private checklist () = WorkerChecklist.generate packet (context packet result) |> ok
    let private assessment checks = WorkerChecklist.assess packet now (checklist()) checks
    let private rejects (fragment: string) (assessment: WorkerChecklistAssessment) =
        Assert.isTrue (not (WorkerChecklist.isReady assessment)) "unchecked package passed"
        Assert.isTrue (WorkerChecklist.blockingReasons assessment |> List.exists (fun reason -> reason.Contains(fragment, StringComparison.Ordinal))) (sprintf "%A" assessment)
    let private refused (fragment: string) (result: Result<WorkerChecklistAssessment, string>) =
        match result with
        | Error reason -> Assert.isTrue (reason.Contains(fragment, StringComparison.Ordinal)) reason
        | Ok _ -> failwith ("expected refusal: " + fragment)
    let private modifyFirst change values = change (List.head values) :: List.tail values

    let tests =
        [ t "generates system and acceptance checks for every member and requirement" (fun () ->
              let checklist = checklist()
              Assert.equal 10 checklist.Rows.Length
              for memberItem in packet.Members do
                  let rows = checklist.Rows |> List.filter (fun row -> row.WorkItemId = memberItem.WorkItemId)
                  Assert.equal 5 rows.Length
                  Assert.equal (Set.ofList memberItem.RequirementKeys) (rows |> List.collect _.RequirementKeys |> Set.ofList)
                  Assert.isTrue (rows |> List.forall (fun row -> row.ExecutionId = memberItem.ExecutionId)) "member execution not bound")
          t "a complete independently observed package passes" (fun () ->
              let checks = runs now (checklist())
              Assert.isTrue (WorkerChecklist.isReady (assessment checks)) "valid checks refused"
              WorkerChecklistOperations.verify (ports packet result (fun () -> now)) packet result |> ok |> ignore)
          t "a policy cannot omit or add a required system validator" (fun () ->
              let baseline = context packet result
              for validators in [ Map.empty; baseline.Policy.Validators |> Map.remove WorkerCheckKind.Tests
                                  baseline.Policy.Validators |> Map.add WorkerCheckKind.Acceptance { ValidatorId = "bypass"; InputDigest = sha '5' } ] do
                  Assert.isTrue (Result.isError (WorkerChecklist.generate packet { baseline with Policy = { baseline.Policy with Validators = validators } })) "policy escaped mandatory checks")
          t "rejects malformed policy and candidate bindings" (fun () ->
              let baseline = context packet result
              for changed in [ { baseline with PacketDigest = sha '0' }; { baseline with CandidateCommit = "HEAD" }
                               { baseline with CandidateManifestDigest = "unknown" }; { baseline with Policy = { baseline.Policy with Revision = " " } }
                               { baseline with Policy = { baseline.Policy with Validators = baseline.Policy.Validators |> Map.add WorkerCheckKind.Build { ValidatorId = "changed"; InputDigest = "not-a-digest" } } } ] do
                  Assert.isTrue (Result.isError (WorkerChecklist.generate packet changed)) "invalid binding admitted")
          t "an uncovered requirement cannot produce a checklist" (fun () ->
              let changed = { packet with Acceptance = List.tail packet.Acceptance }
              Assert.isTrue (Result.isError (WorkerChecklist.generate changed (context changed result))) "missing requirement accepted")
          t "missing, duplicated, unexpected and oversized observations refuse" (fun () ->
              let checks = runs now (checklist())
              assessment (List.tail checks) |> rejects "evidence-missing"
              assessment (checks.Head :: checks) |> rejects "evidence-ambiguous"
              assessment ({ checks.Head with CheckId = "invented" } :: checks) |> rejects "unexpected-observation"
              assessment (List.replicate 2049 checks.Head) |> rejects "exceed-bound")
          t "failed, skipped and unavailable checks cannot be marked complete" (fun () ->
              for verdict, code in [ WorkerCheckVerdict.Failed "bad", "check-failed"; WorkerCheckVerdict.Skipped "not run", "check-skipped"
                                     WorkerCheckVerdict.Unavailable "missing runtime", "check-unavailable" ] do
                  runs now (checklist()) |> modifyFirst (fun run -> { run with Verdict = verdict }) |> assessment |> rejects code)
          t "a passing test command with zero or unknown test count refuses" (fun () ->
              for count in [ None; Some 0; Some -1 ] do
                  runs now (checklist()) |> List.map (fun run -> if run.CheckId.EndsWith("/tests", StringComparison.Ordinal) then { run with TestsExecuted = count } else run)
                  |> assessment |> rejects "no-tests-executed")
          t "wrong validator, stale checklist and malformed evidence digest refuse" (fun () ->
              for change, code in [ (fun (run: WorkerCheckRun) -> { run with Validator = { run.Validator with ValidatorId = "worker-self-approval" } }), "validator-mismatch";
                                   (fun (run: WorkerCheckRun) -> { run with Validator = { run.Validator with InputDigest = sha '3' } }), "validator-mismatch";
                                   (fun run -> { run with ChecklistDigest = sha '3' }), "evidence-stale";
                                   (fun run -> { run with EvidenceDigest = "passed" }), "evidence-digest-invalid" ] do
                  runs now (checklist()) |> modifyFirst change |> assessment |> rejects code)
          t "future, reversed, pre-assignment and non-UTC run times refuse" (fun () ->
              for change in [ (fun run -> { run with FinishedAt = now.AddSeconds 1. })
                              (fun run -> { run with StartedAt = now.AddSeconds 1. })
                              (fun run -> { run with StartedAt = packet.IssuedAt.AddSeconds(-1.) })
                              (fun run -> { run with FinishedAt = now.ToOffset(TimeSpan.FromHours 1.) }) ] do
                  runs now (checklist()) |> modifyFirst change |> assessment |> rejects "evidence-time-invalid")
          t "a member cannot borrow a sibling's successful evidence" (fun () ->
              runs now (checklist()) |> List.map (fun run -> { run with CheckId = run.CheckId.Replace("WI-2", "WI-1").Replace("VERIFY-2", "VERIFY-1") })
              |> assessment |> rejects "evidence-missing")
          t "removing or rewriting a generated checklist row refuses" (fun () ->
              let original = checklist()
              for altered in [ { original with Rows = List.tail original.Rows }
                               { original with Rows = original.Rows |> modifyFirst (fun row -> { row with ExecutionId = "sibling-execution" }) } ] do
                  WorkerChecklist.assess packet now altered (runs now altered) |> rejects "not-generated-from-assignment")
          t "candidate, manifest, policy and validator changes invalidate previous passes" (fun () ->
              let original = checklist()
              let inputs = original.Context
              let checks = runs now original
              for changed in [ { inputs with CandidateCommit = String('c', 40) }; { inputs with CandidateManifestDigest = sha '6' }
                               { inputs with Policy = { inputs.Policy with Revision = "fixture-policy-2" } }
                               { inputs with Policy = { inputs.Policy with Validators = inputs.Policy.Validators |> Map.add WorkerCheckKind.Build { ValidatorId = "new-build"; InputDigest = sha '6' } } } ] do
                  let revised = WorkerChecklist.generate packet changed |> ok
                  WorkerChecklist.assess packet now revised checks |> rejects "evidence-stale")
          t "packet order does not invalidate identity but requirement and decision changes do" (fun () ->
              let original = checklist()
              let reordered = { packet with Members = List.rev packet.Members; Acceptance = List.rev packet.Acceptance; DecisionIds = List.rev packet.DecisionIds }
              Assert.equal original (WorkerChecklist.generate reordered (context reordered result) |> ok)
              let memberItem = packet.Members.Head
              let requirementChanged =
                  { packet with Members = { memberItem with RequirementKeys = [ "new-requirement" ] } :: List.tail packet.Members
                                Acceptance = packet.Acceptance |> List.map (fun row -> if row.WorkItemId = memberItem.WorkItemId then { row with RequirementKeys = [ "new-requirement" ] } else row) }
              for changed in [ requirementChanged; { packet with DecisionIds = [ "new-decision" ] }
                               { packet with AllowedPaths = [ "src/Other.fs" ] }; { packet with AuthorityRevision = "new-authority" } ] do
                  let revised = WorkerChecklist.generate changed (context changed result) |> ok
                  WorkerChecklist.assess changed now revised (runs now original) |> rejects "evidence-stale")
          t "reads candidate inputs again and rejects changes during validation" (fun () ->
              let baseline = ports packet result (fun () -> now)
              let mutable reads = 0
              let changed = { baseline with Observe = fun _ _ _ -> reads <- reads + 1; Ok { context packet result with CandidateManifestDigest = if reads = 1 then sha '9' else sha '6' } }
              WorkerChecklistOperations.verify changed packet result |> refused "inputs-changed"
              Assert.equal 2 reads)
          t "a successful assessment cannot survive a later policy change at the effect boundary" (fun () ->
              let baseline = ports packet result (fun () -> now)
              let verified = WorkerChecklistOperations.verify baseline packet result |> ok
              let changed = { baseline with Observe = fun _ _ _ -> Ok { context packet result with Policy = { (context packet result).Policy with Revision = "revoked-policy" } } }
              match WorkerChecklistOperations.recheckCandidate changed packet result result.OutputCommit verified with
              | Error reason -> Assert.isTrue (reason.Contains "changed-before-effects") reason
              | Ok _ -> failwith "stale assessment permitted effects")
          t "wrong output commit, unavailable observers and exceptions refuse" (fun () ->
              let baseline = ports packet result (fun () -> now)
              for changed, code in [ { baseline with Observe = fun _ _ _ -> Ok { context packet result with CandidateCommit = packet.SourceCommit } }, "candidate-differs"
                                     { baseline with Observe = fun _ _ _ -> Error "no repository observation" }, "no repository observation"
                                     { baseline with Evaluate = fun _ -> Error "no trusted runner" }, "no trusted runner"
                                     { baseline with Evaluate = fun _ -> failwith "secret command content" }, "host-observation-failed" ] do
                  WorkerChecklistOperations.verify changed packet result |> refused code)
          t "expiry and backwards clock during checks cannot authorize advancement" (fun () ->
              for later, code in [ packet.ExpiresAt, "assignment-expired"; now.AddSeconds(-2.), "clock-moved-backwards" ] do
                  let mutable calls = 0
                  let clock () = calls <- calls + 1; if calls = 1 then now else later
                  let baseline = ports packet result clock
                  WorkerChecklistOperations.verify { baseline with Evaluate = fun checklist -> Ok(runs now checklist) } packet result |> refused code) ]
