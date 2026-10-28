namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Praxis.Domain.Autonomy
open Praxis.Infrastructure.Autonomy

/// Autonomous execution (WI-0104): the policy contract, the owner-signed run
/// authorization, the fail-closed evaluator, GitHub eligibility and
/// reconciliation, the journaled merge lifecycle against a fake GitHub, the
/// receipt chain and the deterministic independent review.
[<RequireQualifiedAccess>]
module AutonomyTests =
    let private repository = "kemiller2002/example"
    let private candidate = String('a', 40)
    let private otherSha = String('b', 40)
    let private now = DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)

    let policyText =
        """{
  "schema": "praxis.autonomous-execution-policy/1",
  "policyVersion": 1,
  "execution": { "mode": "unattended" },
  "scope": {
    "repositories": [ "kemiller2002/example" ],
    "targetBranches": [ "main", "release/*" ],
    "allowedOperations": [ "create_branch", "commit", "create_pull_request", "run_verification", "request_automated_review", "merge_eligible_pull_request" ]
  },
  "pullRequests": { "mergeStrategy": "squash" },
  "mergeRequirements": {
    "requiredChecks": [ "build" ],
    "checkProducer": "github-actions",
    "independentReviewCheck": "independent-review",
    "maxCriticalDefects": 0
  },
  "protectedPaths": [
    { "pattern": "requirements/**", "class": "frozen_requirements" },
    { "pattern": "infra/permissions/**", "class": "permission_expansion" }
  ],
  "failureHandling": { "transientRetryLimit": 3, "retryBaseDelaySeconds": 1, "checksTimeoutMinutes": 5 },
  "authorization": { "maxHours": 72 }
}
"""

    let private policyBytes = Encoding.UTF8.GetBytes policyText

    let private policy, policyDigest =
        match AutonomyJson.readPolicy policyBytes with
        | Ok value -> value
        | Error reason -> failwith reason

    let private ownerKey = lazy (ECDsa.Create(ECCurve.NamedCurves.nistP256))

    let private trust () =
        { Signers = Map.ofList [ "owner-key-1", { KeyId = "owner-key-1"; Owner = "kemiller2002"; PublicKeyPem = ownerKey.Value.ExportSubjectPublicKeyInfoPem() } ]
          RevokedRunIds = Set.empty }

    let private sign (key: ECDsa) (a: RunAuthorization) =
        let signature = key.SignData(Authorization.signingPayload a, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
        { a with Signature = Convert.ToBase64String signature }

    let private unsigned =
        { RunId = "RUN-TEST-1"
          Owner = "kemiller2002"
          KeyId = "owner-key-1"
          IssuedAt = now.AddHours -1.
          ExpiresAt = now.AddHours 23.
          PolicyVersion = 1
          PolicyDigest = policyDigest
          Repositories = [ repository ]
          TargetBranches = [ "main" ]
          PermittedOperations = [ Operation.MergeEligiblePullRequest; Operation.CreatePullRequest ]
          ProtectedOperations = ProtectedClass.all
          Signature = "" }

    let private authorized (a: RunAuthorization) = AutonomyJson.verifySignature (trust ()) (sign ownerKey.Value a)

    let private check name conclusion app =
        { Name = name; HeadSha = candidate; Status = "completed"; Conclusion = Some conclusion; App = app; Id = 10L; Suite = 1L }

    let private passingChecks = [ check "build" "success" "github-actions"; check "independent-review" "success" "github-actions" ]

    let private baseInput =
        { Operation = Operation.MergeEligiblePullRequest
          Repository = repository
          TargetBranch = "main"
          CandidateSha = candidate
          ObservedHeadSha = Some candidate
          ChangedPaths = [ { Path = "src/App.fs"; Kind = ChangeKind.Modified }; { Path = "tests/AppTests.fs"; Kind = ChangeKind.Modified } ]
          CiReferencedPaths = [ "scripts/verify.sh" ]
          Policy = PolicyState.Loaded(policy, policyDigest, "test")
          Authorization = authorized unsigned
          RevokedRunIds = Set.empty
          RevocationProblems = []
          Checks = passingChecks
          Now = now }

    let private codes (decision: Decision) = decision.Findings |> List.map _.Code

    let private expect result (code: string option) (input: EvaluationInput) =
        let decision = Evaluator.evaluate input
        Assert.equal result decision.Result
        code |> Option.iter (fun c -> Assert.isTrue (codes decision |> List.exists (fun x -> x.StartsWith c)) $"expected a finding {c}, got {codes decision}")
        decision

    let private change path = { Path = path; Kind = ChangeKind.Modified }

    // ------------------------------------------------------------------ fake GitHub

    type private FakeGitHub =
        { mutable Pull: PullRequestObservation
          mutable Checks: CheckObservation list
          mutable Rules: BranchRules
          mutable BaseHead: string
          mutable MergeResponses: Result<string, CallFailure> list
          mutable PullFailures: int
          mutable MergeCalls: int }

    let private fake () =
        { Pull =
            { Number = 7; State = "open"; Draft = false; Merged = false; MergeCommitSha = None; HeadSha = candidate
              BaseRef = "main"; Mergeable = Some true; MergeableState = "clean" }
          Checks = passingChecks
          Rules = { RequiredApprovingReviews = Some 0; RequiresCodeOwnerReview = Some false; RulesVisible = true }
          BaseHead = otherSha
          MergeResponses = [ Ok(String('c', 40)) ]
          PullFailures = 0
          MergeCalls = 0 }

    /// A merge that succeeds updates the fake's pull request and base head,
    /// like GitHub does, whatever the response the caller then receives.
    let private port (state: FakeGitHub) : GitHubPort =
        let completeMerge sha =
            state.Pull <- { state.Pull with State = "closed"; Merged = true; MergeCommitSha = Some sha }
            state.BaseHead <- sha

        { GetPull =
            fun _ _ ->
                if state.PullFailures > 0 then
                    state.PullFailures <- state.PullFailures - 1
                    Error(CallFailure.Transient "HTTP 502")
                else Ok state.Pull
          GetPullFiles = fun _ _ -> Ok [ change "src/App.fs"; change "tests/AppTests.fs" ]
          GetCheckRuns = fun _ _ -> Ok state.Checks
          GetBranchRules = fun _ _ -> Ok state.Rules
          GetBranchHead = fun _ _ -> Ok state.BaseHead
          GetFile = fun _ _ path -> Ok(if path = MergeLifecycle.PolicyPath then Some policyBytes else None)
          GetTreePaths = fun _ _ -> Ok [ MergeLifecycle.PolicyPath; "src/App.fs" ]
          Contains = fun _ commit head -> Ok(commit = head)
          Merge =
            fun _ _ sha _ ->
                state.MergeCalls <- state.MergeCalls + 1
                match state.MergeResponses with
                | [] -> Error(CallFailure.Permanent(405, "no response scripted"))
                | response :: rest ->
                    state.MergeResponses <- rest
                    if sha <> state.Pull.HeadSha then Error(CallFailure.HeadChanged "Head branch was modified")
                    else
                        match response with
                        | Ok merged ->
                            completeMerge merged
                            Ok merged
                        | Error(CallFailure.Transient detail) when detail = "lost-response" ->
                            // GitHub merged, but the response never arrived.
                            completeMerge (String('d', 40))
                            Error(CallFailure.Transient detail)
                        | Error failure -> Error failure }

    let private temporaryJournal () =
        let root = Path.Combine(Path.GetTempPath(), $"praxis-autonomy-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore
        root

    let private environment (state: FakeGitHub) root =
        { GitHub = port state
          Journal = AutonomyStore.journal root
          Now = fun () -> now
          Sleep = ignore
          PollInterval = TimeSpan.Zero
          Log = ignore }

    let private authorityOf auth revoked () = { Authorization = auth; RevokedRunIds = revoked; RevocationProblems = [] }

    let private request auth =
        { Repository = repository; PullRequest = 7; Candidate = candidate; Authority = authorityOf auth Set.empty }

    let private findingCodes (review: ReviewReport) = review.Findings |> List.map _.Code

    let private reviewInput changes added trace =
        { Changes = changes; AddedLines = added; TraceText = trace; ProtectedPaths = []; CiReferencedPaths = []; AllowedPaths = None; KnownWorkItems = None }

    let tests =
        [ { Name = "autonomy: an authorized, verified, reviewed candidate is ALLOWED"
            Run = fun () -> baseInput |> expect DecisionResult.Allow None |> fun d -> Assert.empty d.Findings }

          { Name = "autonomy: no run authorization is DENY"
            Run = fun () -> { baseInput with Authorization = AuthorizationState.Missing } |> expect DecisionResult.Deny (Some "authorization-missing") |> ignore }

          { Name = "autonomy: an expired authorization is DENY"
            Run = fun () -> { baseInput with Now = now.AddHours 24. } |> expect DecisionResult.Deny (Some "authorization-expired") |> ignore }

          { Name = "autonomy: a revoked authorization is DENY"
            Run = fun () -> { baseInput with RevokedRunIds = set [ "RUN-TEST-1" ] } |> expect DecisionResult.Deny (Some "authorization-revoked") |> ignore }

          { Name = "autonomy: a repository outside the authorization is DENY"
            Run = fun () -> { baseInput with Repository = "kemiller2002/other" } |> expect DecisionResult.Deny (Some "repository-out-of-scope") |> ignore }

          { Name = "autonomy: a target branch outside the authorization is DENY even when the policy allows it"
            Run = fun () -> { baseInput with TargetBranch = "release/1" } |> expect DecisionResult.Deny (Some "target-branch-out-of-scope") |> ignore }

          { Name = "autonomy: failed CI is DENY"
            Run = fun () -> { baseInput with Checks = [ check "build" "failure" "github-actions"; check "independent-review" "success" "github-actions" ] } |> expect DecisionResult.Deny (Some "required-check-failed") |> ignore }

          { Name = "autonomy: a missing independent review is DENY"
            Run = fun () -> { baseInput with Checks = [ check "build" "success" "github-actions" ] } |> expect DecisionResult.Deny (Some "independent-review-missing") |> ignore }

          { Name = "autonomy: an open critical defect (failed independent review) is DENY"
            Run = fun () -> { baseInput with Checks = [ check "build" "success" "github-actions"; check "independent-review" "failure" "github-actions" ] } |> expect DecisionResult.Deny (Some "independent-review-failed") |> ignore }

          { Name = "autonomy: a check written by another producer (the agent's own token) never counts"
            Run = fun () -> { baseInput with Checks = [ check "build" "success" "claude"; check "independent-review" "success" "claude" ] } |> expect DecisionResult.Deny (Some "required-check-untrusted-producer") |> ignore }

          { Name = "autonomy: a changed candidate SHA is DENY and asks for re-verification"
            Run =
              fun () ->
                  let decision = { baseInput with ObservedHeadSha = Some otherSha } |> expect DecisionResult.Deny (Some "candidate-changed")
                  Assert.isTrue (Evaluator.needsReverification decision) "a moved head must be re-verified" }

          { Name = "autonomy: evidence for another commit does not count for the candidate"
            Run = fun () -> { baseInput with Checks = passingChecks |> List.map (fun c -> { c with HeadSha = otherSha }) } |> expect DecisionResult.Deny (Some "required-check-missing") |> ignore }

          { Name = "autonomy: changing the execution policy is REQUIRES_OWNER"
            Run = fun () -> { baseInput with ChangedPaths = [ change ".praxis/autonomy/policy.json" ] } |> expect DecisionResult.RequiresOwner (Some "protected-change:execution_policy") |> ignore }

          { Name = "autonomy: weakening a workflow is REQUIRES_OWNER"
            Run = fun () -> { baseInput with ChangedPaths = [ change ".github/workflows/ci.yml" ] } |> expect DecisionResult.RequiresOwner (Some "protected-change:github_workflows") |> ignore }

          { Name = "autonomy: a declared permission-expansion path is REQUIRES_OWNER"
            Run = fun () -> { baseInput with ChangedPaths = [ change "infra/permissions/app.json" ] } |> expect DecisionResult.RequiresOwner (Some "protected-change:permission_expansion") |> ignore }

          { Name = "autonomy: classification is by effect: a script CI runs, a removed test, a test project"
            Run =
              fun () ->
                  { baseInput with ChangedPaths = [ change "scripts/verify.sh" ] } |> expect DecisionResult.RequiresOwner (Some "protected-change:verification_policy") |> ignore
                  { baseInput with ChangedPaths = [ { Path = "tests/AppTests.fs"; Kind = ChangeKind.Removed } ] } |> expect DecisionResult.RequiresOwner (Some "protected-change:verification_policy") |> ignore
                  { baseInput with ChangedPaths = [ change "tests/App.Tests/App.Tests.fsproj" ] } |> expect DecisionResult.RequiresOwner (Some "protected-change:verification_policy") |> ignore
                  { baseInput with ChangedPaths = [ change "CODEOWNERS" ] } |> expect DecisionResult.RequiresOwner (Some "protected-change:repository_rules") |> ignore
                  { baseInput with ChangedPaths = [ { Path = "keys/deploy.pem"; Kind = ChangeKind.Added } ] } |> expect DecisionResult.RequiresOwner (Some "protected-change:credentials") |> ignore }

          { Name = "autonomy: CI-referenced scripts are found in workflow text, not guessed"
            Run =
              fun () ->
                  let workflow = "steps:\n  - run: ./scripts/verify.sh --all\n  - run: dotnet build App.sln\n"
                  Assert.equal [ "scripts/verify.sh" ] (Classification.ciReferencedPaths (fun _ -> None) [ workflow ] [ "scripts/verify.sh"; "App.sln"; "src/App.fs" ]) }

          { Name = "autonomy: a gate failure dominates: an unauthorized protected change is DENY, not REQUIRES_OWNER"
            Run = fun () -> { baseInput with Authorization = AuthorizationState.Missing; ChangedPaths = [ change ".github/workflows/ci.yml" ] } |> expect DecisionResult.Deny (Some "authorization-missing") |> ignore }

          { Name = "autonomy: an authorization for a different policy digest is DENY"
            Run = fun () -> { baseInput with Authorization = authorized { unsigned with PolicyDigest = "sha256:" + String('0', 64) } } |> expect DecisionResult.Deny (Some "authorization-policy-mismatch") |> ignore }

          { Name = "autonomy: no policy is DENY (fail closed)"
            Run = fun () -> { baseInput with Policy = PolicyState.Unavailable "absent" } |> expect DecisionResult.Deny (Some "policy-unavailable") |> ignore }

          { Name = "autonomy: an authorization that does not reserve every protected class is DENY"
            Run = fun () -> { baseInput with Authorization = authorized { unsigned with ProtectedOperations = [ ProtectedClass.Credentials ] } } |> expect DecisionResult.Deny (Some "authorization-protected-incomplete") |> ignore }

          { Name = "autonomy: an authorization longer than the policy allows is DENY"
            Run = fun () -> { baseInput with Authorization = authorized { unsigned with ExpiresAt = unsigned.IssuedAt.AddHours 100. } } |> expect DecisionResult.Deny (Some "authorization-duration") |> ignore }

          { Name = "autonomy: an operation the owner did not permit is DENY"
            Run = fun () -> { baseInput with Operation = Operation.Deploy } |> expect DecisionResult.Deny (Some "operation-not-permitted") |> ignore }

          { Name = "autonomy: signatures: a tampered, unknown-key, wrong-owner or unsigned authorization is invalid"
            Run =
              fun () ->
                  let signed = sign ownerKey.Value unsigned
                  match AutonomyJson.verifySignature (trust ()) signed with
                  | AuthorizationState.Verified(_, digest) -> Assert.equal (AutonomyJson.authorizationDigest signed) digest
                  | other -> failwith $"expected verified, got {other}"
                  let invalid state = match state with AuthorizationState.Invalid _ -> true | _ -> false
                  Assert.isTrue (invalid (AutonomyJson.verifySignature (trust ()) { signed with Repositories = [ "kemiller2002/other" ] })) "tampered scope verified"
                  Assert.isTrue (invalid (AutonomyJson.verifySignature (trust ()) { signed with KeyId = "unknown" })) "unknown key verified"
                  Assert.isTrue (invalid (AutonomyJson.verifySignature (trust ()) { signed with Owner = "someone-else" })) "wrong owner verified"
                  Assert.isTrue (invalid (AutonomyJson.verifySignature (trust ()) unsigned)) "unsigned verified"
                  use agentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
                  Assert.isTrue (invalid (AutonomyJson.verifySignature (trust ()) (sign agentKey unsigned))) "a signature by a key the trust store does not name verified" }

          { Name = "autonomy: authorization JSON round-trips and keeps its signed digest"
            Run =
              fun () ->
                  let signed = sign ownerKey.Value unsigned
                  match AutonomyJson.readAuthorization ((AutonomyJson.authorizationNode signed).ToJsonString()) with
                  | Ok read -> Assert.equal (AutonomyJson.authorizationDigest signed) (AutonomyJson.authorizationDigest read)
                  | Error reason -> failwith reason }

          { Name = "autonomy: the policy reader is strict and its digest is over the exact bytes"
            Run =
              fun () ->
                  let refused (text: string) = match AutonomyJson.readPolicy (Encoding.UTF8.GetBytes text) with Error _ -> true | Ok _ -> false
                  Assert.isTrue (refused (policyText.Replace("\"maxCriticalDefects\": 0", "\"maxCriticalDefects\": 1"))) "a critical-defect allowance was accepted"
                  Assert.isTrue (refused (policyText.Replace("\"policyVersion\": 1,", "\"policyVersion\": 1, \"extra\": true,"))) "an unknown member was accepted"
                  Assert.isTrue (refused (policyText.Replace("\"checkProducer\": \"github-actions\",", ""))) "a missing member was accepted"
                  Assert.isTrue (refused (policyText.Replace("\"class\": \"frozen_requirements\"", "\"class\": \"nothing\""))) "an unknown class was accepted"
                  match AutonomyJson.readPolicy (Encoding.UTF8.GetBytes(policyText + " ")) with
                  | Ok(_, digest) -> Assert.isTrue (digest <> policyDigest) "a byte change kept the digest"
                  | Error reason -> failwith reason }

          { Name = "autonomy: a trust store inside the repository is refused"
            Run =
              fun () ->
                  let root = temporaryJournal ()
                  let path = Path.Combine(root, "trust.json")
                  File.WriteAllText(path, "{}")
                  match AutonomyStore.loadTrustStore [ root ] path with
                  | Error reason -> Assert.isTrue (reason.Contains "outside") reason
                  | Ok _ -> failwith "a trust store inside the repository was accepted" }

          { Name = "autonomy: GitHub eligibility: required human review is BLOCKED, conflicts need a new candidate, unknown waits"
            Run =
              fun () ->
                  let pull = (fake ()).Pull
                  let openRules = { RequiredApprovingReviews = Some 0; RequiresCodeOwnerReview = Some false; RulesVisible = true }
                  Assert.equal Eligibility.Eligible (Lifecycle.eligibility openRules pull)
                  match Lifecycle.eligibility { openRules with RequiredApprovingReviews = Some 1 } pull with
                  | Eligibility.Blocked("github-requires-human-review", _) -> ()
                  | other -> failwith $"{other}"
                  match Lifecycle.eligibility openRules { pull with MergeableState = "dirty"; Mergeable = Some false } with
                  | Eligibility.NeedsNewCandidate("merge-conflict", _) -> ()
                  | other -> failwith $"{other}"
                  match Lifecycle.eligibility openRules { pull with MergeableState = "unknown"; Mergeable = None } with
                  | Eligibility.Pending _ -> ()
                  | other -> failwith $"{other}"
                  match Lifecycle.eligibility { RequiredApprovingReviews = None; RequiresCodeOwnerReview = None; RulesVisible = false } { pull with MergeableState = "blocked" } with
                  | Eligibility.Blocked("github-blocked", detail) -> Assert.isTrue (detail.Contains "not readable") detail
                  | other -> failwith $"{other}" }

          { Name = "autonomy: reconciliation never claims a merge it did not submit"
            Run =
              fun () ->
                  let pull = (fake ()).Pull
                  let merged = { pull with Merged = true; State = "closed"; MergeCommitSha = Some otherSha }
                  Assert.equal (Reconciliation.AdoptRemoteMerge otherSha) (Lifecycle.reconcile (JournalPhase.Submitted(candidate, 1)) candidate merged)
                  match Lifecycle.reconcile JournalPhase.NotStarted candidate merged with
                  | Reconciliation.ForeignMerge _ -> ()
                  | other -> failwith $"{other}"
                  match Lifecycle.reconcile (JournalPhase.Submitted(candidate, 1)) candidate { merged with HeadSha = otherSha } with
                  | Reconciliation.ForeignMerge _ -> ()
                  | other -> failwith $"{other}"
                  Assert.equal (Reconciliation.AlreadyReceipted(otherSha, "d")) (Lifecycle.reconcile (JournalPhase.Completed(otherSha, "d")) candidate merged)
                  Assert.equal Reconciliation.Proceed (Lifecycle.reconcile JournalPhase.NotStarted candidate pull) }

          { Name = "autonomy: lifecycle merges an eligible candidate, verifies it and chains a receipt; a duplicate request is idempotent"
            Run =
              fun () ->
                  let state = fake ()
                  let root = temporaryJournal ()
                  let env = environment state root
                  match MergeLifecycle.run env (request (authorized unsigned)) with
                  | LifecycleOutcome.Merged(commit, _) -> Assert.equal (String('c', 40)) commit
                  | other -> failwith $"expected merged, got {other}"
                  match MergeLifecycle.run env (request (authorized unsigned)) with
                  | LifecycleOutcome.AlreadyMerged _ -> ()
                  | other -> failwith $"expected already merged, got {other}"
                  Assert.equal 1 state.MergeCalls
                  Assert.equal (Ok 1) (AutonomyStore.verifyChain env.Journal) }

          { Name = "autonomy: a merge whose response was lost is reconciled from GitHub, not resubmitted"
            Run =
              fun () ->
                  let state = { fake () with MergeResponses = [ Error(CallFailure.Transient "lost-response") ] }
                  let env = environment state (temporaryJournal ())
                  match MergeLifecycle.run env (request (authorized unsigned)) with
                  | LifecycleOutcome.Merged(commit, _) -> Assert.equal (String('d', 40)) commit
                  | other -> failwith $"expected a reconciled merge, got {other}"
                  Assert.equal 1 state.MergeCalls
                  let events = File.ReadAllText env.Journal.EventsPath
                  Assert.isTrue (events.Contains "reconcile-remote-merge") "the reconciliation was not recorded" }

          { Name = "autonomy: transient GitHub failures are retried within the bound and recorded"
            Run =
              fun () ->
                  let state = { fake () with PullFailures = 2 }
                  let env = environment state (temporaryJournal ())
                  match MergeLifecycle.run env (request (authorized unsigned)) with
                  | LifecycleOutcome.Merged _ -> ()
                  | other -> failwith $"expected merged after retries, got {other}"
                  Assert.isTrue ((File.ReadAllText env.Journal.EventsPath).Contains "\"event\":\"retry\"") "retries were not recorded"
                  let exhausted = { fake () with PullFailures = 50 }
                  match MergeLifecycle.run (environment exhausted (temporaryJournal ())) (request (authorized unsigned)) with
                  | LifecycleOutcome.Failed("github-unavailable", _) -> ()
                  | other -> failwith $"expected bounded failure, got {other}" }

          { Name = "autonomy: lifecycle outcomes for denial, a moved head, human review and revocation"
            Run =
              fun () ->
                  let run state auth = MergeLifecycle.run (environment state (temporaryJournal ())) (request auth)
                  match run (fake ()) AuthorizationState.Missing with
                  | LifecycleOutcome.Denied d -> Assert.isTrue (codes d |> List.contains "authorization-missing") "wrong denial"
                  | other -> failwith $"{other}"
                  let moved = fake ()
                  moved.Pull <- { moved.Pull with HeadSha = otherSha }
                  match run moved (authorized unsigned) with
                  | LifecycleOutcome.Reverify("candidate-changed", _) -> ()
                  | other -> failwith $"{other}"
                  let review = { fake () with Rules = { RequiredApprovingReviews = Some 1; RequiresCodeOwnerReview = Some false; RulesVisible = true } }
                  match run review (authorized unsigned) with
                  | LifecycleOutcome.Blocked("github-requires-human-review", _) -> Assert.equal 0 review.MergeCalls
                  | other -> failwith $"{other}"
                  let revoked = fake ()
                  match MergeLifecycle.run (environment revoked (temporaryJournal ())) { request (authorized unsigned) with Authority = authorityOf (authorized unsigned) (set [ "RUN-TEST-1" ]) } with
                  | LifecycleOutcome.Denied d -> Assert.isTrue (codes d |> List.contains "authorization-revoked") "wrong denial"
                  | other -> failwith $"{other}"
                  Assert.equal 0 revoked.MergeCalls }

          { Name = "autonomy: GitHub's own SHA guard is honoured: a head that moves at merge time is re-verified"
            Run =
              fun () ->
                  let state = fake ()
                  let env = environment state (temporaryJournal ())
                  // The head moves between evaluation and the merge call.
                  let racing = { env.GitHub with Merge = fun r n sha m -> (state.Pull <- { state.Pull with HeadSha = otherSha }); (port state).Merge r n sha m }
                  match MergeLifecycle.run { env with GitHub = racing } (request (authorized unsigned)) with
                  | LifecycleOutcome.Reverify("candidate-changed", _) -> ()
                  | other -> failwith $"{other}" }

          { Name = "autonomy: an edited or removed receipt breaks the chain"
            Run =
              fun () ->
                  let state = fake ()
                  let env = environment state (temporaryJournal ())
                  match MergeLifecycle.run env (request (authorized unsigned)) with
                  | LifecycleOutcome.Merged(_, digest) ->
                      let path = Path.Combine(env.Journal.Root, "receipts", digest.Replace("sha256:", "") + ".json")
                      File.WriteAllText(path, File.ReadAllText(path).Replace("\"attempts\": 1", "\"attempts\": 2"))
                      match AutonomyStore.verifyChain env.Journal with
                      | Error problems -> Assert.isTrue (problems |> List.exists (fun p -> p.Contains "modified")) $"{problems}"
                      | Ok _ -> failwith "an edited receipt verified"
                      File.Delete path
                      match AutonomyStore.verifyChain env.Journal with
                      | Error problems -> Assert.isTrue (problems |> List.exists (fun p -> p.Contains "missing")) $"{problems}"
                      | Ok _ -> failwith "a removed receipt verified"
                  | other -> failwith $"{other}" }

          { Name = "autonomy: review passes a traced, tested change and fails secrets, disabled or removed tests and untraced work"
            Run =
              fun () ->
                  let clean = Review.review (reviewInput [ change "src/App.fs"; change "tests/AppTests.fs" ] [ "src/App.fs", "let x = 1" ] "WI-0104: add x")
                  Assert.isTrue clean.Passed $"{clean.Findings}"
                  let fakeKey = "-----BEGIN " + "PRIVATE KEY-----"
                  let secret = Review.review (reviewInput [ change "src/App.fs" ] [ "src/App.fs", fakeKey ] "WI-1")
                  Assert.isTrue (not secret.Passed && findingCodes secret |> List.contains "secret-material") $"{secret.Findings}"
                  let skipped = Review.review (reviewInput [ change "tests/AppTests.fs" ] [ "tests/AppTests.fs", "  [<Ignore>]" ] "WI-1")
                  Assert.isTrue (findingCodes skipped |> List.contains "disables-test") $"{skipped.Findings}"
                  let removed = Review.review (reviewInput [ { Path = "tests/AppTests.fs"; Kind = ChangeKind.Removed } ] [] "WI-1")
                  Assert.equal 1 removed.CriticalDefects
                  let untraced = Review.review (reviewInput [ change "docs/a.md" ] [ "docs/a.md", "text" ] "tidy up")
                  Assert.isTrue (not untraced.Passed && findingCodes untraced |> List.contains "requirements-trace-missing") $"{untraced.Findings}"
                  let untested = Review.review (reviewInput [ change "src/App.fs" ] [ "src/App.fs", "let y = 2" ] "WI-1")
                  Assert.isTrue (untested.Passed && findingCodes untested |> List.contains "untested-change") "an untested change must be reported, not blocked" }

          { Name = "autonomy: review diff parsing reads renames, deletions and added lines"
            Run =
              fun () ->
                  let changes = LocalReview.parseNameStatus "M\tsrc/a.fs\nD\ttests/bTests.fs\nR100\told.md\tnew.md\nA\tdocs/c.md\n"
                  Assert.equal
                      [ change "src/a.fs"; { Path = "tests/bTests.fs"; Kind = ChangeKind.Removed }; { Path = "new.md"; Kind = ChangeKind.Renamed "old.md" }; { Path = "docs/c.md"; Kind = ChangeKind.Added } ]
                      changes
                  let added = LocalReview.parseAddedLines "diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1 +1 @@\n-old\n+new\n+++ /dev/null\n"
                  Assert.equal [ "x", "new" ] added }

          { Name = "autonomy: retry delay is exponential and bounded"
            Run =
              fun () ->
                  let failure = { TransientRetryLimit = 2; RetryBaseDelaySeconds = 3; ChecksTimeoutMinutes = 1 }
                  Assert.equal (Some(TimeSpan.FromSeconds 3.)) (Lifecycle.retryDelay failure 0)
                  Assert.equal (Some(TimeSpan.FromSeconds 6.)) (Lifecycle.retryDelay failure 1)
                  Assert.equal None (Lifecycle.retryDelay failure 2) }

          // ----------------------------------------------- security review (SECURITY-REVIEW.md)
          { Name = "autonomy security F3: a same-name check from another suite cannot mask a failed run"
            Run =
              fun () ->
                  let masked =
                      [ { check "build" "failure" "github-actions" with Id = 10L; Suite = 1L }
                        { check "build" "success" "github-actions" with Id = 12L; Suite = 2L }
                        check "independent-review" "success" "github-actions" ]
                  { baseInput with Checks = masked } |> expect DecisionResult.Deny (Some "required-check-ambiguous") |> ignore
                  // A re-run stays in its suite: the newest run there is the check's state.
                  let rerun =
                      [ { check "build" "failure" "github-actions" with Id = 10L; Suite = 1L }
                        { check "build" "success" "github-actions" with Id = 12L; Suite = 1L }
                        check "independent-review" "success" "github-actions" ]
                  { baseInput with Checks = rerun } |> expect DecisionResult.Allow None |> ignore }

          { Name = "autonomy security F2: a trust store reached through a symlink into the repository is refused"
            Run =
              fun () ->
                  let root = temporaryJournal ()
                  let inside = Path.Combine(root, "trust.json")
                  File.WriteAllText(inside, "{}")
                  let outside = Path.Combine(temporaryJournal (), "trust-link.json")
                  File.CreateSymbolicLink(outside, inside) |> ignore
                  match AutonomyStore.loadTrustStore [ root ] outside with
                  | Error reason -> Assert.isTrue (reason.Contains "outside") reason
                  | Ok _ -> failwith "a symlinked trust store inside the repository was accepted" }

          { Name = "autonomy security F4: CI-run files are found through working-directory, workspace prefixes, local actions and other scripts"
            Run =
              fun () ->
                  let workflow =
                      String.concat "\n"
                          [ "steps:"; "  - uses: ./tools/ci-action"; "  - run: ./verify.sh"; "    working-directory: scripts"
                            "  - run: $GITHUB_WORKSPACE/scripts/lint.sh"; "  - run: ${{ github.workspace }}/scripts/pack.sh" ]
                  let tracked = [ "scripts/verify.sh"; "scripts/lint.sh"; "scripts/pack.sh"; "scripts/helper.sh"; "tools/ci-action/action.yml"; "tools/ci-action/index.js"; "src/App.fs" ]
                  let read = function "scripts/verify.sh" -> Some "#!/bin/sh\n./helper.sh\n" | _ -> None
                  Assert.equal
                      [ "scripts/helper.sh"; "scripts/lint.sh"; "scripts/pack.sh"; "scripts/verify.sh"; "tools/ci-action/action.yml"; "tools/ci-action/index.js" ]
                      (Classification.ciReferencedPaths read [ workflow ] tracked) }

          { Name = "autonomy security F4: build, dependency and package configuration is owner-controlled, whatever its case"
            Run =
              fun () ->
                  [ "Directory.Packages.props"; "Directory.Build.rsp"; "NuGet.Config"; "build/common.targets"; ".config/dotnet-tools.json"
                    ".gitmodules"; "vendor/nuget/ordo-core.lock"; "packages.lock.json"; "package-lock.json"; ".github/dependabot.yml"
                    "Makefile"; "package.json"; "App.slnx" ]
                  |> List.iter (fun p ->
                      Assert.isTrue (not (Classification.classify [] [] [ change p ]).IsEmpty) $"{p} was not classified") }

          { Name = "autonomy security F5: Expecto focus, pending and skip variants are disabled tests"
            Run =
              fun () ->
                  [ "  ftestCase \"only this runs\" <| fun () -> ()"; "  ptestCaseAsync \"p\" <| async { () }"
                    "  testCase \"x\" <| fun () -> skiptest \"later\""; "  ptestProperty \"p\" <| fun (x: int) -> true" ]
                  |> List.iter (fun line ->
                      let report = Review.review (reviewInput [ change "tests/AppTests.fs" ] [ "tests/AppTests.fs", line ] "WI-1")
                      Assert.isTrue (findingCodes report |> List.contains "disables-test") $"missed: {line}") }

          { Name = "autonomy security F6: with a base backlog, a trace must name a real work item"
            Run =
              fun () ->
                  let known = Some(set [ "WI-0104" ])
                  let traced text = Review.review { reviewInput [ change "docs/a.md" ] [] text with KnownWorkItems = known }
                  Assert.isTrue (findingCodes (traced "cleanup (see AR-x)") |> List.contains "requirements-trace-unknown") "an invented id passed"
                  Assert.isTrue (traced "WI-0104: docs").Passed "a real work item failed"
                  Assert.isTrue (traced "tidy\n\nWork-Item: WI-0104").Passed "a Work-Item trailer failed"
                  Assert.equal (set [ "WI-0001"; "WI-0002" ]) (LocalReview.knownWorkItems """{"items":[{"id":"WI-0001"},{"id":"WI-0002","x":{"y":1}}]}""") }

          { Name = "autonomy security F7: an unreadable committed revocation denies, it is never ignored"
            Run =
              fun () ->
                  let state = fake ()
                  let bad = MergeLifecycle.RevocationDirectory + "stop.json"
                  let text = """{"schema":"praxis.run-revocation/1","runId":"RUN-TEST-1","revokedAt":"2026-10-28T11:00:00.000Z","reason":"stop"}"""
                  let env = environment state (temporaryJournal ())
                  let gh =
                      { env.GitHub with
                          GetTreePaths = fun _ _ -> Ok [ MergeLifecycle.PolicyPath; bad ]
                          GetFile = fun r ref p -> if p = bad then Ok(Some(Text.Encoding.UTF8.GetBytes text)) else (port state).GetFile r ref p }
                  match MergeLifecycle.run { env with GitHub = gh } (request (authorized unsigned)) with
                  | LifecycleOutcome.Denied d -> Assert.isTrue (codes d |> List.contains "revocation-unreadable") $"{codes d}"
                  | other -> failwith $"expected a denial, got {other}"
                  Assert.equal 0 state.MergeCalls }

          { Name = "autonomy security F8: a revocation made while the lifecycle waits stops the merge"
            Run =
              fun () ->
                  let state = { fake () with Checks = [ { check "build" "success" "github-actions" with Status = "in_progress"; Conclusion = None }; check "independent-review" "success" "github-actions" ] }
                  let mutable revoked = Set.empty
                  let env =
                      { environment state (temporaryJournal ()) with
                          Sleep = fun _ -> revoked <- set [ "RUN-TEST-1" ]; state.Checks <- passingChecks }
                  let req = { request (authorized unsigned) with Authority = fun () -> authorityOf (authorized unsigned) revoked () }
                  match MergeLifecycle.run env req with
                  | LifecycleOutcome.Denied d -> Assert.isTrue (codes d |> List.contains "authorization-revoked") $"{codes d}"
                  | other -> failwith $"expected a denial after the revocation, got {other}"
                  Assert.equal 0 state.MergeCalls }

          { Name = "autonomy security F9: a truncated tree is refused, never read as complete"
            Run =
              fun () ->
                  let node = System.Text.Json.Nodes.JsonNode.Parse """{"truncated":true,"tree":[{"path":"src/a.fs","type":"blob"}]}"""
                  Assert.isTrue (Result.isError (GitHubApi.parseTree candidate node)) "a truncated tree was accepted" }

          { Name = "autonomy security F10: a base that moves between the decision and the merge is decided again"
            Run =
              fun () ->
                  let state = fake ()
                  let env = environment state (temporaryJournal ())
                  let mutable reads = 0
                  let gh =
                      { env.GitHub with
                          GetBranchHead = fun _ _ ->
                              reads <- reads + 1
                              Ok(if reads = 2 then String('e', 40) else state.BaseHead) }
                  match MergeLifecycle.run { env with GitHub = gh } (request (authorized unsigned)) with
                  | LifecycleOutcome.Merged _ -> ()
                  | other -> failwith $"{other}"
                  Assert.isTrue ((File.ReadAllText env.Journal.EventsPath).Contains "base-moved") "the moved base was not re-decided" } ]
