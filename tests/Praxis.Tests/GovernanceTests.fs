namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Praxis.Domain.Autonomy
open Praxis.Domain.Governance
open Praxis.Domain.Work
open Praxis.Infrastructure.Governance
open Praxis.Infrastructure.Work

/// WI-0116: enforceable governance and verified merges. The adversarial
/// cases are numbered after the initiative brief's §21 list; each test
/// asserts the failure behaviour (the refusal code), not only success.
[<RequireQualifiedAccess>]
module GovernanceTests =
    let private repository = "kemiller2002/example"
    let private now = DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero)
    let private targetHead = String('1', 40)
    let private head = String('a', 40)
    let private tree = String('b', 40)
    let private policyDigest = "sha256:" + String('c', 64)

    let private policy: ProtectedPolicy =
        { Id = "example-merge-gate-v1"; Digest = policyDigest; Repository = Some repository; RequiredValidations = [ "tests"; "praxis-validate" ] }

    let private key = lazy (VerifierKey.generate ())
    let private agentKey = lazy (VerifierKey.generate ())

    let private verifier id (k: VerifierKey) : TrustedVerifier =
        { Id = id; Identity = VerifierIdentity.PinnedKey k.Trusted; ValidFrom = now.AddDays -10.0; ValidUntil = None; RevokedAt = None; RevocationReason = None; PolicyScopes = [] }

    let private trust () : VerifierTrustRoot =
        { ConfigVersion = 1; Repository = repository; Verifiers = [ verifier "host-1" key.Value ]; ApprovedBy = "owner"; ApprovedAt = now.AddDays -10.0 }

    let private payload validation status commit : ReceiptPayload =
        { ReceiptId = Guid.NewGuid().ToString()
          WorkItemId = "WI-1"
          Claimant = "pull-request/7"
          FencingGeneration = 0L
          Commit = commit
          TreeId = tree
          OrdoContractDigest = policyDigest
          ValidationPolicyId = policy.Id
          ValidationPolicyDigest = policyDigest
          Validation = validation
          EvidenceId = validation
          Status = status
          ExitCode = Some(if status = CheckStatus.Passed then 0 else 1)
          Command = [ "dotnet"; "test" ]
          OutputSha256 = "sha256:" + String('d', 64)
          StartedAt = now.AddMinutes -5.0
          ObservedAt = now.AddMinutes -4.0
          DurationMs = 60000L
          Verifier = { Id = "praxis-verifier"; Version = "test"; KeyId = key.Value.KeyId } }

    let private signWith (k: VerifierKey) (p: ReceiptPayload) = VerifierKey.sign k (VerificationReceipt.render { p with Verifier = { p.Verifier with KeyId = k.KeyId } })

    let private present (t: VerifierTrustRoot) (envelope: ReceiptEnvelope) =
        { Source = "receipt"; Check = VerifierTrust.verifyEnvelope t envelope }

    let private passing commit =
        [ "tests"; "praxis-validate" ] |> List.map (fun v -> payload v CheckStatus.Passed commit |> signWith key.Value |> present (trust ()))

    let private authorities =
        { WorkAuthorizers = [ "owner" ]; ElevatedApprovers = [ "owner" ]; ExceptionAuthorities = [ "owner" ]; AgentIdentities = [ "claude[bot]" ] }

    let baseFacts () : MergeFacts =
        { Repository = repository
          RequestedRepository = repository
          ExpectedTarget = "main"
          TargetBranch = "main"
          TargetHead = targetHead
          CandidateHead = head
          CandidateContainsTarget = true
          ChangedPaths = [ { Path = "src/App.fs"; Kind = ChangeKind.Modified } ]
          CiReferenced = []
          DeclaredWorkItems = [ "WI-1" ]
          AuthorizedWorkItems = [ "WI-1" ]
          Standing = []
          RequireVerifiedCompletion = true
          Policy = Some policy
          Trust = Some(trust ())
          Receipts = passing head
          VerifiedCommits = Map.ofList [ head, { IsAncestorOfCandidate = true; ChangedSince = []; TreeId = Some tree } ]
          Authorities = authorities
          ElevatedApprovals = []
          Exceptions = []
          CandidateExceptionIds = []
          TrustRootWeakenings = []
          PlatformEnforced = Some true
          EvaluatedAt = now }

    let private codes (d: MergeDecision) = d.Refusals |> List.map _.Code

    let private refused (code: string) (facts: MergeFacts) =
        let d = MergeAuthorization.decide facts
        Assert.equal MergeOutcome.Refused d.Outcome
        Assert.isTrue (List.contains code (codes d)) $"expected {code}, got {codes d}"
        d

    let private approval sha : ElevatedApproval = { Approver = "owner"; CandidateHead = sha; ApprovedAt = now; ViaApplication = None; Edited = false; Reason = "rotation" }

    let private exceptionFor waives : GovernanceException =
        { Id = "EX-1"
          Repository = repository
          CandidateHeads = [ head ]
          Waives = waives
          Reason = "verification infrastructure outage"
          AuthorizedBy = "owner"
          Implementer = "claude[bot]"
          CompensatingSafeguards = [ "repository tests ran in CI" ]
          ResidualRisk = "unverified merge"
          FollowUp = [ "re-verify after the outage" ]
          GrantedAt = now.AddHours -1.0
          ExpiresAt = now.AddDays 2.0 }

    // ---- OIDC workload identity ------------------------------------------------

    let private rsa = lazy (RSA.Create 2048)
    let private b64url (bytes: byte array) = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private issuerKeys () : VerifierTrust.IssuerKey list =
        let p = rsa.Value.ExportParameters false
        [ { Kid = "k1"; Modulus = p.Modulus; Exponent = p.Exponent } ]

    let private workflowRef = $"{repository}/.github/workflows/praxis-merge-gate.yml@refs/heads/main"

    let private token (claims: (string * JsonNode) list) =
        let header = b64url (Encoding.UTF8.GetBytes """{"alg":"RS256","kid":"k1","typ":"JWT"}""")
        let body = JsonObject()
        for name, value in claims do body[name] <- value
        let encoded = b64url (Encoding.UTF8.GetBytes(body.ToJsonString()))
        let signature = rsa.Value.SignData(Encoding.ASCII.GetBytes(header + "." + encoded), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        header + "." + encoded + "." + b64url signature

    let private workloadTrust () : VerifierTrustRoot =
        { trust () with
            Verifiers =
                [ { Id = "merge-gate-v1"
                    Identity = VerifierIdentity.Workload { Issuer = VerifierTrust.GitHubIssuer; Repository = repository; WorkflowRef = workflowRef; Events = [ "pull_request_target" ] }
                    ValidFrom = now.AddDays -10.0
                    ValidUntil = None
                    RevokedAt = None
                    RevocationReason = None
                    PolicyScopes = [] } ] }

    let private workloadReceipt (payloadBytes: byte array) (overrides: (string * JsonNode) list) : VerifierTrust.WorkloadReceipt =
        let defaults: (string * JsonNode) list =
            [ "iss", JsonValue.Create VerifierTrust.GitHubIssuer
              "aud", JsonValue.Create(VerifierTrust.audienceFor payloadBytes)
              "repository", JsonValue.Create repository
              "job_workflow_ref", JsonValue.Create workflowRef
              "event_name", JsonValue.Create "pull_request_target"
              "run_id", JsonValue.Create "42"
              "iat", JsonValue.Create(now.AddMinutes(-3.0).ToUnixTimeSeconds()) ]

        let claims = defaults |> List.map (fun (k, v) -> k, (overrides |> List.tryFind (fun (o, _) -> o = k) |> Option.map snd |> Option.defaultValue v))
        { Payload = payloadBytes; Token = token claims }

    // ---- raw GitHub snapshots ------------------------------------------------------

    let private snapshotWith (permission: string) (protection: string) (rules: string) (rulesets: string) (workflows: string) =
        JsonNode.Parse(
            $$"""{ "schema": "praxis.governance-snapshot/1", "capturedAt": "2026-11-26T12:00:00Z", "repositoryName": "{{repository}}", "branch": "main",
  "repository": { "full_name": "{{repository}}", "owner": { "type": "User" }, "visibility": "public", "default_branch": "main" },
  "branchProtection": {{protection}}, "rulesForBranch": {{rules}}, "rulesets": {{rulesets}}, "rulesetsListing": [],
  "headChecks": { "check_runs": [ { "name": "praxis-merge-gate", "app": { "slug": "github-actions" }, "conclusion": "success" } ] },
  "workflowPermissions": { "default_workflow_permissions": "{{permission}}" },
  "workflows": {{workflows}} }"""
        )

    let private snapshot = snapshotWith "read"

    let private unprotected = snapshot """{ "notProtected": true }""" "[]" "[]" "[]"

    let private gateWorkflow = """[ { "path": ".github/workflows/praxis-merge-gate.yml", "triggers": [ "pull_request_target" ], "writesChecks": false } ]"""

    let private fullRules =
        """[ { "type": "deletion", "ruleset_id": 1 }, { "type": "non_fast_forward", "ruleset_id": 1 },
             { "type": "pull_request", "ruleset_id": 1, "parameters": { "required_approving_review_count": 0 } },
             { "type": "required_status_checks", "ruleset_id": 1, "parameters": { "strict_required_status_checks_policy": true,
               "required_status_checks": [ { "context": "praxis-merge-gate", "integration_id": 15368 } ] } } ]"""

    let private protectedSnapshot = snapshot """{ "notProtected": true }""" fullRules """[ { "id": 1, "bypass_actors": [] } ]""" gateWorkflow

    let private praxisReady: PraxisConfiguration =
        { Installed = true; RequireVerifiedCompletion = true; TrustRoot = Some(workloadTrust ()); TrustRootError = None; ValidationPolicyPresent = true; GovernanceProfileDeclared = true; ProfileError = None }

    let private evaluate raw praxis =
        match GitHubGovernance.toPlatform raw with
        | Ok platform -> PlatformGovernance.evaluate PlatformGovernance.defaultProfile praxis (false, [ "not assessed" ]) platform
        | Error e -> failwith e

    let private control id (report: GovernanceReport) = report.Controls |> List.find (fun c -> c.Id = id)

    // ---- Git fixtures ------------------------------------------------------------------

    let private git = GitFixture.git
    let private write = GitFixture.write

    let private repo label =
        let root = GitFixture.temporaryDirectory label
        git root [ "init"; "-q"; "-b"; "main" ] |> ignore
        GitFixture.configureIdentity root
        root

    let private commitAs (root: string) (committer: string) (message: string) =
        git root [ "add"; "-A" ] |> ignore
        git root [ "-c"; $"user.name={committer}"; "commit"; "-q"; "--allow-empty"; "-m"; message ] |> ignore
        git root [ "rev-parse"; "HEAD" ]

    let private auditRequest root : AuditRequest =
        { Root = root; Repository = None; BranchRef = "main"; Limit = 50; Since = None; PreviousHead = None; DecisionDirectory = None; GateCheck = "praxis-merge-gate" }

    let tests: TestCase list =
        [ { Name = "governance: a verified, authorized, current candidate is VERIFIED"
            Run =
              fun () ->
                  let d = MergeAuthorization.decide (baseFacts ())
                  Assert.equal MergeOutcome.Verified d.Outcome
                  Assert.empty d.Refusals
                  Assert.equal (Some head) d.VerifiedCommit
                  Assert.equal 2 d.Receipts.Length }

          { Name = "governance #1: completion without authorization is refused (MRG.WORK_ITEM_UNAUTHORIZED)"
            Run = fun () -> { baseFacts () with AuthorizedWorkItems = [] } |> refused "MRG.WORK_ITEM_UNAUTHORIZED" |> ignore }

          { Name = "governance #1b: a pull request that declares no work item is refused"
            Run = fun () -> { baseFacts () with DeclaredWorkItems = [] } |> refused "MRG.WORK_ITEM_UNDECLARED" |> ignore }

          { Name = "governance #2: a missing required validation is refused (MRG.VALIDATION_INCOMPLETE)"
            Run = fun () -> { baseFacts () with Receipts = passing head |> List.take 1 } |> refused "MRG.VALIDATION_INCOMPLETE" |> ignore }

          { Name = "governance #2b: an empty protected policy verifies nothing and is refused"
            Run = fun () -> { baseFacts () with Policy = Some { policy with RequiredValidations = [] } } |> refused "MRG.POLICY_UNAVAILABLE" |> ignore }

          { Name = "governance #3: a suite that exits 0 without running tests fails the validation"
            Run =
              fun () ->
                  let expectation = Some { CountPattern = @"(?<count>\d+) test\(s\);"; MinimumTests = 1 }
                  Assert.isTrue (Result.isError (TestExecution.gate expectation "Build succeeded.\n")) "no count must fail"
                  Assert.isTrue (Result.isError (TestExecution.gate expectation "0 test(s); 0 passed; 0 failed\n")) "zero tests must fail"
                  Assert.equal (Ok(Some 12)) (TestExecution.gate expectation "3 test(s); x\n12 test(s); 12 passed\n")
                  Assert.isTrue (Result.isError (TestExecution.gate (Some { CountPattern = "("; MinimumTests = 1 }) "1 test(s);")) "an invalid pattern fails closed"
                  Assert.isTrue (Result.isError (TestExecution.gate (Some { CountPattern = @"\d+ tests"; MinimumTests = 1 }) "5 tests")) "a pattern without 'count' fails closed" }

          { Name = "governance #4/#6: a receipt for an older commit outside the candidate's history is refused"
            Run =
              fun () ->
                  let old = String('e', 40)
                  { baseFacts () with
                      Receipts = passing old
                      VerifiedCommits = Map.ofList [ old, { IsAncestorOfCandidate = false; ChangedSince = [ "src/App.fs" ]; TreeId = Some tree } ] }
                  |> refused "MRG.ARTIFACT_CHANGED_AFTER_VERIFICATION"
                  |> ignore }

          { Name = "governance #4b: a receipt for an ancestor is reused only when nothing but Praxis records changed since"
            Run =
              fun () ->
                  let verified = String('e', 40)
                  let withChanges changed = { baseFacts () with Receipts = passing verified; VerifiedCommits = Map.ofList [ verified, { IsAncestorOfCandidate = true; ChangedSince = changed; TreeId = Some tree } ] }
                  Assert.equal MergeOutcome.Verified (MergeAuthorization.decide (withChanges [ ".ros/context/current.json"; ".praxis/receipts/WI-1/tests.json" ])).Outcome
                  withChanges [ ".ros/context/current.json"; "tests/AppTests.fs" ] |> refused "MRG.ARTIFACT_CHANGED_AFTER_VERIFICATION" |> ignore }

          { Name = "governance #5: a receipt from another repository's policy is refused (policy digest binds the repository)"
            Run =
              fun () ->
                  let foreign = { payload "tests" CheckStatus.Passed head with ValidationPolicyDigest = "sha256:" + String('9', 64) } |> signWith key.Value |> present (trust ())
                  { baseFacts () with Receipts = foreign :: (passing head |> List.tail) } |> refused "MRG.RECEIPT_POLICY_MISMATCH" |> ignore
                  { baseFacts () with RequestedRepository = "attacker/fork" } |> refused "MRG.REPOSITORY_MISMATCH" |> ignore }

          { Name = "governance #7: receipts spanning two claim generations are refused"
            Run =
              fun () ->
                  let a = { payload "tests" CheckStatus.Passed head with FencingGeneration = 1L } |> signWith key.Value |> present (trust ())
                  let b = { payload "praxis-validate" CheckStatus.Passed head with FencingGeneration = 2L } |> signWith key.Value |> present (trust ())
                  { baseFacts () with Receipts = [ a; b ] } |> refused "MRG.RECEIPT_STALE_GENERATION" |> ignore }

          { Name = "governance #8: a receipt naming the trusted key but signed by another key is forged"
            Run =
              fun () ->
                  let p = payload "tests" CheckStatus.Passed head
                  let forged = VerifierKey.sign agentKey.Value (VerificationReceipt.render p)
                  let relabelled = { forged with Signatures = forged.Signatures |> List.map (fun s -> { s with KeyId = key.Value.KeyId }) }
                  { baseFacts () with Receipts = present (trust ()) relabelled :: (passing head |> List.tail) } |> refused "MRG.RECEIPT_FORGED" |> ignore }

          { Name = "governance #8b: every single-byte change to a signed payload breaks its signature (mutation)"
            Run =
              fun () ->
                  let envelope = payload "tests" CheckStatus.Passed head |> signWith key.Value
                  let positions = [ 0 .. 17 .. envelope.Payload.Length - 1 ]

                  for position in positions do
                      let bytes = Array.copy envelope.Payload
                      bytes[position] <- bytes[position] ^^^ 0x01uy
                      match VerifierTrust.verifyEnvelope (trust ()) { envelope with Payload = bytes } with
                      | Ok _ -> failwith $"a mutation at byte {position} verified"
                      | Error f -> Assert.equal "GOV.RECEIPT_INVALID_SIGNATURE" f.Code }

          { Name = "governance #9/#13: an implementer-held (untrusted) key cannot self-attest"
            Run =
              fun () ->
                  let selfSigned = payload "tests" CheckStatus.Passed head |> signWith agentKey.Value |> present (trust ())
                  { baseFacts () with Receipts = selfSigned :: (passing head |> List.tail) } |> refused "MRG.RECEIPT_UNTRUSTED" |> ignore }

          { Name = "governance #10: receipts from a revoked verifier are refused whenever they claim to be observed"
            Run =
              fun () ->
                  let revoked = { trust () with Verifiers = [ { verifier "host-1" key.Value with RevokedAt = Some(now.AddDays 1.0); RevocationReason = Some "key exposed" } ] }
                  let receipts = [ "tests"; "praxis-validate" ] |> List.map (fun v -> payload v CheckStatus.Passed head |> signWith key.Value |> present revoked)
                  { baseFacts () with Trust = Some revoked; Receipts = receipts } |> refused "MRG.RECEIPT_UNTRUSTED" |> ignore }

          { Name = "governance #10b: a retired verifier's receipts observed before retirement remain valid; after it they do not"
            Run =
              fun () ->
                  let retired = { trust () with Verifiers = [ { verifier "host-1" key.Value with ValidUntil = Some(now.AddMinutes -4.5) } ] }
                  let p = payload "tests" CheckStatus.Passed head
                  Assert.isTrue (Result.isOk (VerifierTrust.verifyEnvelope retired ({ p with ObservedAt = now.AddMinutes -5.0 } |> signWith key.Value))) "before retirement"
                  match VerifierTrust.verifyEnvelope retired (p |> signWith key.Value) with
                  | Error f -> Assert.equal "GOV.VERIFIER_EXPIRED" f.Code
                  | Ok _ -> failwith "an expired verifier attested" }

          { Name = "governance #11: an expired standing authorization does not authorize work"
            Run =
              fun () ->
                  let standing =
                      { Id = "SA-docs"; Repositories = [ repository ]; Categories = [ { Name = "docs"; AllowedPaths = [ "docs/**" ]; MaxChangedFiles = 10 } ]
                        ProhibitedPaths = []; ApprovedBy = "owner"; ValidFrom = now.AddDays -30.0; ExpiresAt = now.AddDays -1.0; RevokedAt = None }

                  let docs = { baseFacts () with AuthorizedWorkItems = []; ChangedPaths = [ { Path = "docs/a.md"; Kind = ChangeKind.Modified } ] }
                  { docs with Standing = [ standing ] } |> refused "MRG.WORK_ITEM_UNAUTHORIZED" |> ignore
                  let active = MergeAuthorization.decide { docs with Standing = [ { standing with ExpiresAt = now.AddDays 1.0 } ] }
                  Assert.equal MergeOutcome.Verified active.Outcome
                  Assert.isTrue (active.AuthorizationBasis |> List.exists (fun b -> b.Contains "SA-docs")) "basis names the standing authorization" }

          { Name = "governance #11b: standing authorization never covers paths outside its category or prohibited paths"
            Run =
              fun () ->
                  let standing =
                      { Id = "SA-docs"; Repositories = [ repository ]; Categories = [ { Name = "docs"; AllowedPaths = [ "docs/**" ]; MaxChangedFiles = 10 } ]
                        ProhibitedPaths = [ "docs/security/**" ]; ApprovedBy = "owner"; ValidFrom = now.AddDays -1.0; ExpiresAt = now.AddDays 1.0; RevokedAt = None }

                  Assert.isTrue (Authority.standing [ standing ] repository now [ "docs/a.md"; "src/App.fs" ] |> Option.isNone) "mixed change"
                  Assert.isTrue (Authority.standing [ standing ] repository now [ "docs/security/policy.md" ] |> Option.isNone) "prohibited path"
                  Assert.isTrue (Authority.standing [ { standing with RevokedAt = Some now } ] repository now [ "docs/a.md" ] |> Option.isNone) "revoked" }

          { Name = "governance #12/#28: changing the trust root without elevated approval is refused, and the weakening is named"
            Run =
              fun () ->
                  let proposed = { trust () with ConfigVersion = 2; Verifiers = trust().Verifiers @ [ verifier "agent" agentKey.Value ] }
                  let weakenings = VerifierTrust.weakenings (trust ()) proposed
                  Assert.isTrue (weakenings |> List.exists (fun w -> w.Contains "adds verifier 'agent'")) $"{weakenings}"
                  let replaced = { trust () with ConfigVersion = 2; Verifiers = [ verifier "host-1" agentKey.Value ] }
                  Assert.isTrue (VerifierTrust.weakenings (trust ()) replaced |> List.exists (fun w -> w.Contains "replaces the identity")) "replacement"
                  let facts = { baseFacts () with ChangedPaths = [ { Path = ".praxis/trust/verifiers.json"; Kind = ChangeKind.Modified } ]; TrustRootWeakenings = weakenings }
                  let d = facts |> refused "MRG.PROTECTED_CHANGE_UNAUTHORIZED"
                  Assert.isTrue ((d.Refusals |> List.find (fun f -> f.Code = "MRG.PROTECTED_CHANGE_UNAUTHORIZED")).Detail.Contains "adds verifier") "detail names the weakening"
                  Assert.equal MergeOutcome.Verified (MergeAuthorization.decide { facts with ElevatedApprovals = [ approval head ] }).Outcome }

          { Name = "governance #12b: revoking or narrowing a verifier is not a weakening"
            Run =
              fun () ->
                  let revoked = { trust () with ConfigVersion = 2; Verifiers = [ { verifier "host-1" key.Value with RevokedAt = Some now } ] }
                  Assert.empty (VerifierTrust.weakenings (trust ()) revoked) }

          { Name = "governance #13b: an approval posted through an application, by an agent identity, or for another head is not elevated approval"
            Run =
              fun () ->
                  let facts = { baseFacts () with ChangedPaths = [ { Path = ".github/workflows/ci.yml"; Kind = ChangeKind.Modified } ] }
                  [ { approval head with ViaApplication = Some "claude" }
                    { approval head with Approver = "claude[bot]" }
                    approval (String('f', 40))
                    { approval head with Approver = "stranger" } ]
                  |> List.iter (fun a -> { facts with ElevatedApprovals = [ a ] } |> refused "MRG.PROTECTED_CHANGE_UNAUTHORIZED" |> ignore) }

          { Name = "governance #14: receipts produced under a modified (candidate) validation policy are refused"
            Run =
              fun () ->
                  let weaker = [ "tests" ] |> List.map (fun v -> { payload v CheckStatus.Passed head with ValidationPolicyDigest = "sha256:" + String('8', 64) } |> signWith key.Value |> present (trust ()))
                  let d = { baseFacts () with Receipts = weaker; ChangedPaths = [ { Path = ".praxis/validation-policy.json"; Kind = ChangeKind.Modified } ] } |> refused "MRG.RECEIPT_POLICY_MISMATCH"
                  Assert.isTrue (List.contains "MRG.PROTECTED_CHANGE_UNAUTHORIZED" (codes d)) "a policy change is also a protected change" }

          { Name = "governance #15: a modified verification workflow is a protected change, and a gate on pull_request is not enforcement"
            Run =
              fun () ->
                  { baseFacts () with ChangedPaths = [ { Path = ".github/workflows/praxis-merge-gate.yml"; Kind = ChangeKind.Modified } ] } |> refused "MRG.PROTECTED_CHANGE_UNAUTHORIZED" |> ignore
                  let spoofable = snapshot """{ "notProtected": true }""" fullRules """[ { "id": 1, "bypass_actors": [] } ]""" """[ { "path": ".github/workflows/praxis-merge-gate.yml", "triggers": [ "pull_request_target", "pull_request" ] } ]"""
                  Assert.equal ControlStatus.NotEnforced (control "GOV-GATE-WORKFLOW" (evaluate spoofable praxisReady)).Status }

          { Name = "governance #16/#22: a failed or unavailable (interrupted) validation is refused"
            Run =
              fun () ->
                  for status in [ CheckStatus.Failed; CheckStatus.Unavailable ] do
                      let receipts = [ payload "tests" status head; payload "praxis-validate" CheckStatus.Passed head ] |> List.map (signWith key.Value >> present (trust ()))
                      { baseFacts () with Receipts = receipts } |> refused "MRG.RECEIPT_NOT_PASSED" |> ignore }

          { Name = "governance #16b: a later passing receipt does not hide that the latest run of a validation failed"
            Run =
              fun () ->
                  let earlierPass = { payload "tests" CheckStatus.Passed head with ObservedAt = now.AddMinutes -10.0 }
                  let laterFail = payload "tests" CheckStatus.Failed head
                  let receipts = [ earlierPass; laterFail; payload "praxis-validate" CheckStatus.Passed head ] |> List.map (signWith key.Value >> present (trust ()))
                  { baseFacts () with Receipts = receipts } |> refused "MRG.RECEIPT_NOT_PASSED" |> ignore }

          { Name = "governance #17/#18: the decision is a pure function: concurrent or duplicate requests decide identically"
            Run =
              fun () ->
                  let facts = baseFacts ()
                  let decisions = Array.Parallel.init 8 (fun _ -> MergeAuthorization.decide facts) |> Array.toList
                  Assert.equal 1 (decisions |> List.map (fun d -> d.Outcome, d.Refusals, d.VerifiedCommit) |> List.distinct |> List.length) }

          { Name = "governance #23: a truncated (interrupted) receipt is malformed and refused, never partially trusted"
            Run =
              fun () ->
                  let text = payload "tests" CheckStatus.Passed head |> signWith key.Value |> VerificationReceipt.renderEnvelope
                  match GovernanceJson.parseReceiptDocument (text.Substring(0, text.Length / 2)) with
                  | Ok _ -> failwith "a truncated receipt parsed"
                  | Error _ -> () }

          { Name = "governance #24/#26: no trust root or no receipts (evidence not delivered, verifier down) is refused"
            Run =
              fun () ->
                  { baseFacts () with Trust = None } |> refused "MRG.TRUST_ROOT_UNAVAILABLE" |> ignore
                  { baseFacts () with Receipts = [] } |> refused "MRG.VALIDATION_INCOMPLETE" |> ignore
                  { baseFacts () with Policy = None } |> refused "MRG.POLICY_UNAVAILABLE" |> ignore }

          { Name = "governance #25: invalid exceptions are refused (self-approved, agent, open-ended, non-waivable, expired)"
            Run =
              fun () ->
                  let facts = { baseFacts () with Receipts = [] }
                  [ { exceptionFor [ "MRG.VALIDATION_INCOMPLETE" ] with AuthorizedBy = "claude[bot]" }
                    { exceptionFor [ "MRG.VALIDATION_INCOMPLETE" ] with Implementer = "owner" }
                    { exceptionFor [ "MRG.VALIDATION_INCOMPLETE" ] with ExpiresAt = now.AddDays 60.0 }
                    { exceptionFor [ "MRG.VALIDATION_INCOMPLETE" ] with ExpiresAt = now.AddMinutes -1.0 }
                    { exceptionFor [ "MRG.VALIDATION_INCOMPLETE" ] with CompensatingSafeguards = [] }
                    { exceptionFor [ "MRG.VALIDATION_INCOMPLETE" ] with CandidateHeads = [ String('f', 40) ] }
                    exceptionFor [ "MRG.RECEIPT_FORGED" ] ]
                  |> List.iter (fun e -> { facts with Exceptions = [ e ] } |> refused "MRG.EXCEPTION_INVALID" |> ignore) }

          { Name = "governance #25b: a valid exception authorizes the merge but never labels it verified"
            Run =
              fun () ->
                  let d = MergeAuthorization.decide { baseFacts () with Receipts = []; Exceptions = [ exceptionFor [ "MRG.VALIDATION_INCOMPLETE" ] ] }
                  Assert.equal (MergeOutcome.AuthorizedByException [ "EX-1" ]) d.Outcome
                  Assert.equal "MRG.VALIDATION_INCOMPLETE" (Assert.single d.Waived).Code
                  Assert.isTrue (MergeOutcome.toWire d.Outcome <> "verified") "never verified" }

          { Name = "governance #27: a candidate that does not contain the target head is stale and refused"
            Run = fun () -> { baseFacts () with CandidateContainsTarget = false } |> refused "MRG.STALE_BASE" |> ignore }

          { Name = "governance: a receipt whose tree differs from the verified commit's tree is refused as forged"
            Run =
              fun () ->
                  { baseFacts () with VerifiedCommits = Map.ofList [ head, { IsAncestorOfCandidate = true; ChangedSince = []; TreeId = Some(String('0', 40)) } ] }
                  |> refused "MRG.RECEIPT_FORGED"
                  |> ignore }

          { Name = "governance: without a verified-completion requirement, trust root or policy, a merge is never verified"
            Run = fun () -> { baseFacts () with RequireVerifiedCompletion = false; Policy = None; Trust = None } |> refused "MRG.VERIFIED_COMPLETION_NOT_REQUIRED" |> ignore }

          { Name = "governance (property): any change touching a protected path requires elevated approval"
            Run =
              fun () ->
                  let random = Random 116
                  let protectedPaths = [ ".github/workflows/x.yml"; ".praxis/trust/verifiers.json"; ".praxis/governance/authorities.json"; "ros.json"; "Directory.Build.props"; "keys/server.pem"; ".praxis/validation-policy.json" ]
                  let ordinary = [ "src/A.fs"; "docs/a.md"; "README.md"; "src/B/C.fs" ]

                  for _ in 1..200 do
                      let picks = [ for p in ordinary do if random.Next 2 = 0 then p ] @ [ protectedPaths[random.Next protectedPaths.Length] ]
                      let changes = picks |> List.map (fun p -> { Path = p; Kind = ChangeKind.Modified })
                      { baseFacts () with ChangedPaths = changes } |> refused "MRG.PROTECTED_CHANGE_UNAUTHORIZED" |> ignore }

          // ---- OIDC workload identity ----------------------------------------------

          { Name = "governance #13c: a CI-workload receipt verifies only for the pinned workflow, event, repository and payload"
            Run =
              fun () ->
                  let bytes = VerificationReceipt.render (payload "tests" CheckStatus.Passed head)
                  let t = workloadTrust ()
                  let keys = issuerKeys ()
                  let ok = VerifierTrust.verifyWorkload t keys (workloadReceipt bytes [])
                  Assert.equal AuthenticationBasis.WorkloadIdentity (match ok with Ok r -> r.Basis | Error f -> failwith f.Detail)

                  let refusedWith (receipt: VerifierTrust.WorkloadReceipt) =
                      match VerifierTrust.verifyWorkload t keys receipt with
                      | Ok _ -> failwith "an unpinned workload verified"
                      | Error f -> f.Code

                  Assert.equal "GOV.RECEIPT_UNTRUSTED_VERIFIER" (refusedWith (workloadReceipt bytes [ "job_workflow_ref", JsonValue.Create(workflowRef.Replace("refs/heads/main", "refs/pull/7/merge")) ]))
                  Assert.equal "GOV.RECEIPT_UNTRUSTED_VERIFIER" (refusedWith (workloadReceipt bytes [ "event_name", JsonValue.Create "pull_request" ]))
                  Assert.equal "GOV.RECEIPT_UNTRUSTED_VERIFIER" (refusedWith (workloadReceipt bytes [ "repository", JsonValue.Create "attacker/fork" ]))
                  Assert.equal "GOV.RECEIPT_BINDING_MISMATCH" (refusedWith (workloadReceipt bytes [ "aud", JsonValue.Create "praxis-receipt:sha256:00" ]))
                  let other = VerificationReceipt.render (payload "tests" CheckStatus.Failed head)
                  Assert.equal "GOV.RECEIPT_BINDING_MISMATCH" (refusedWith { workloadReceipt bytes [] with Payload = other })
                  let forgedToken = (workloadReceipt bytes []).Token
                  Assert.equal "GOV.RECEIPT_INVALID_SIGNATURE" (refusedWith { Payload = bytes; Token = forgedToken.Substring(0, forgedToken.Length - 4) + "AAAA" }) }

          { Name = "governance #29/#1: candidate code never reaches the attestation identity, and the gate never reads files it wrote"
            Run =
              fun () ->
                  let text = GovernanceBootstrap.mergeGateWorkflow "main"
                  let section (name: string) (next: string option) =
                      let from = text.IndexOf name
                      match next with
                      | Some n -> text.Substring(from, text.IndexOf n - from)
                      | None -> text.Substring from
                  let plan = section "  plan:" (Some "  observe:")
                  let observe = section "  observe:" (Some "  praxis-merge-gate:")
                  let gate = section "  praxis-merge-gate:" None
                  for job in [ plan; observe ] do
                      Assert.isTrue (not (job.Contains "id-token")) "only the gate job may mint OIDC tokens"
                      Assert.isTrue (not (job.Contains "secrets.") && not (job.Contains "github.token")) "jobs running candidate code see no secret or token variable"
                      Assert.isTrue (job.Contains "contents: read" && not (job.Contains ": write")) "read-only token"
                  Assert.isTrue (observe.Contains "matrix.validation") "one isolated job per validation"
                  Assert.isTrue (not (gate.Contains "governance observe") && not (gate.Contains "download-artifact")) "the gate neither runs candidate code nor reads artifacts candidate code wrote"
                  Assert.isTrue (gate.Contains "--run-id \"$GITHUB_RUN_ID\"") "results come from GitHub's record of this run's jobs"
                  Assert.isTrue (text.Contains "permissions: {}") "workflow default permissions are empty"
                  Assert.isTrue (text.Contains "branches: [main]" && text.Contains "--base \"origin/main\"") "the protected branch is fixed, not taken from the pull request"
                  Assert.isTrue (not (text.Contains "github.event.pull_request.base.ref") && not (text.Contains "head.ref") && not (text.Contains "pull_request.title")) "no untrusted text is interpolated into scripts"
                  Assert.equal [ "pull_request_target" ] (GovernanceOperations.workflowTriggers text) }

          { Name = "governance #1b: attestation maps job conclusions, never a pass for anything but success"
            Run =
              fun () ->
                  Assert.equal (CheckStatus.Passed, Some 0) (GovernanceBootstrap.statusOf "success")
                  Assert.equal (CheckStatus.Failed, Some 1) (GovernanceBootstrap.statusOf "failure")
                  for other in [ "cancelled"; "skipped"; "timed_out"; "missing"; "pending"; "neutral" ] do
                      Assert.equal (CheckStatus.Unavailable, None) (GovernanceBootstrap.statusOf other) }

          // ---- platform governance -------------------------------------------------------

          { Name = "governance #20/#21: an unprotected default branch is reported as not enforced, never as governed"
            Run =
              fun () ->
                  let report = evaluate unprotected praxisReady
                  for id in [ "GOV-PR"; "GOV-FORCE-PUSH"; "GOV-DELETION"; "GOV-UP-TO-DATE"; "GOV-CHECK:praxis-merge-gate"; "GOV-BYPASS"; "GOV-GATE-WORKFLOW" ] do
                      Assert.equal ControlStatus.NotEnforced (control id report).Status
                  Assert.isTrue ((ReadinessLevel.ordered |> List.findIndex ((=) report.Level)) < (ReadinessLevel.ordered |> List.findIndex ((=) ReadinessLevel.PlatformEnforced))) "below platform-enforced" }

          { Name = "governance: a complete ruleset with a pinned gate check and no bypass reaches verified-completion-enabled"
            Run =
              fun () ->
                  let report = evaluate protectedSnapshot praxisReady
                  Assert.equal ReadinessLevel.VerifiedCompletionEnabled report.Level
                  Assert.isTrue (report.Ladder |> List.exists (fun r -> r.Level = ReadinessLevel.OperationallyDemonstrated && not r.Reached)) "demonstration is not assumed" }

          { Name = "governance: a required check accepted from any source is spoofable and not enforced"
            Run =
              fun () ->
                  let rules = fullRules.Replace(", \"integration_id\": 15368", "")
                  let report = evaluate (snapshot """{ "notProtected": true }""" rules """[ { "id": 1, "bypass_actors": [] } ]""" gateWorkflow) praxisReady
                  Assert.equal ControlStatus.NotEnforced (control "GOV-CHECK:praxis-merge-gate" report).Status }

          { Name = "governance #21b: bypass actors, unenforced classic admin protection and unreadable bypass lists are not 'no bypass'"
            Run =
              fun () ->
                  let withBypass = evaluate (snapshot """{ "notProtected": true }""" fullRules """[ { "id": 1, "bypass_actors": [ { "actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "always" } ] } ]""" gateWorkflow) praxisReady
                  Assert.equal ControlStatus.NotEnforced (control "GOV-BYPASS" withBypass).Status
                  let hidden = evaluate (snapshot """{ "notProtected": true }""" fullRules """[ { "id": 1 } ]""" gateWorkflow) praxisReady
                  Assert.equal ControlStatus.Unknown (control "GOV-BYPASS" hidden).Status
                  let classic = """{ "enforce_admins": { "enabled": false }, "allow_force_pushes": { "enabled": false }, "allow_deletions": { "enabled": false }, "required_pull_request_reviews": { "required_approving_review_count": 0 }, "required_status_checks": { "strict": true, "checks": [ { "context": "praxis-merge-gate", "app_id": 15368 } ] } }"""
                  Assert.equal ControlStatus.NotEnforced (control "GOV-BYPASS" (evaluate (snapshot classic "[]" "[]" gateWorkflow) praxisReady)).Status }

          { Name = "governance: unreadable branch rules are unknown and fail closed"
            Run =
              fun () ->
                  let report = evaluate (snapshot """{ "unknown": true, "reason": "HTTP 403: Resource not accessible by integration" }""" "[]" "[]" gateWorkflow) praxisReady
                  Assert.equal ControlStatus.Unknown (control "GOV-RULES" report).Status
                  Assert.isTrue (report.Level <> ReadinessLevel.PlatformEnforced && report.Level <> ReadinessLevel.VerifiedCompletionEnabled) "unknown is not enforced" }

          { Name = "governance: drift reports a control that was satisfied and no longer is, and ignores improvements"
            Run =
              fun () ->
                  let before = evaluate protectedSnapshot praxisReady
                  let after = evaluate unprotected praxisReady
                  let drift = PlatformGovernance.drift before after
                  Assert.isTrue (drift |> List.exists (fun f -> f.Detail.StartsWith "GOV-PR")) $"{drift}"
                  Assert.empty (PlatformGovernance.drift after before)
                  let roundTrip = GovernanceJson.parseReport (GovernanceJson.indented (GovernanceJson.report before)) |> Result.defaultWith failwith
                  Assert.isTrue (PlatformGovernance.drift roundTrip after |> List.isEmpty |> not) "a saved report is a usable baseline" }

          { Name = "governance: the missing completion authority diagnosis names the missing authority, who resolves it, and what may continue"
            Run =
              fun () ->
                  let d =
                      AuthorityDiagnostics.diagnose
                          { RequireVerifiedCompletion = false; RequireAttestedEvidence = false; PinnedKeyIds = []; TrustRoot = None; TrustRootError = None
                            ValidationPolicyPresent = false; MergeGateWorkflowPresent = false; HostAuthorityNamed = false; HostVerifierSocketNamed = false }

                  Assert.isTrue (not d.Ready) "not ready"
                  Assert.equal Principal.OrganizationOwner d.ResolvedBy
                  Assert.isTrue (d.Missing |> List.exists (fun m -> m.Contains "trust root")) "names the trust root"
                  Assert.isTrue (d.Why.Contains "self-attestation") "explains why an agent key is not acceptable"
                  Assert.isTrue (not d.MayContinue.IsEmpty && not d.Blocked.IsEmpty) "separates safe work from blocked actions" }

          { Name = "governance: workflow triggers are read from the common YAML shapes"
            Run =
              fun () ->
                  Assert.equal [ "push" ] (GovernanceOperations.workflowTriggers "name: x\non: push\njobs: {}\n")
                  Assert.equal [ "push"; "pull_request" ] (GovernanceOperations.workflowTriggers "on: [push, pull_request]\n")
                  Assert.equal [ "pull_request"; "push" ] (GovernanceOperations.workflowTriggers "on:\n  pull_request:\n  push:\n    branches: [main]\njobs:\n")
                  Assert.equal [ "unparsed" ] (GovernanceOperations.workflowTriggers "name: none\n") }

          { Name = "governance: the trust root round-trips and rejects a keyId that is not its key's digest"
            Run =
              fun () ->
                  let text = GovernanceJson.indented (GovernanceJson.renderTrustRoot (trust ()))
                  Assert.equal (Ok(trust ())) (GovernanceJson.parseTrustRoot text |> Result.map (fun t -> { t with ApprovedAt = (trust ()).ApprovedAt; Verifiers = t.Verifiers |> List.map (fun v -> { v with ValidFrom = (trust ()).Verifiers.Head.ValidFrom }) }))
                  let tampered = text.Replace(key.Value.KeyId, agentKey.Value.KeyId)
                  Assert.isTrue (Result.isError (GovernanceJson.parseTrustRoot tampered)) "a mismatched keyId must not parse" }

          // ---- Git fixtures ---------------------------------------------------------------

          { Name = "governance #30: the echelon-assurance incident (PR merged while WI active, no verified completion) is detected"
            Run =
              fun () ->
                  let root = repo "assurance-incident"
                  write root "README.md" "# assurance\n"
                  let initial = commitAs root "GitHub" "Initial commit"
                  git root [ "checkout"; "-q"; "-b"; "claude/consolidation" ] |> ignore
                  write root "ros.json" """{ "workProtocol": { "requireDurableCheckpoint": true } }"""
                  write root ".ros/context/current.json" """{ "workItems": [ { "id": "WI-ASR-CONSOLIDATION", "state": "complete" }, { "id": "WI-ASR-FINAL", "state": "active" } ] }"""
                  write root "findings/completion-report.md" "Merge is blocked: verified completion refused (no completion authority).\n"
                  let candidate = commitAs root "Claude" "Final reconciliation"
                  git root [ "checkout"; "-q"; "main" ] |> ignore
                  git root [ "-c"; "user.name=GitHub"; "merge"; "-q"; "--no-ff"; "-m"; "Merge pull request #3 from kemiller2002/claude/consolidation"; "claude/consolidation" ] |> ignore
                  let report = GovernanceOperations.audit (auditRequest root) |> Result.defaultWith failwith
                  let found = report.Findings |> List.map _.Code
                  Assert.isTrue (List.contains "AUD.UNVERIFIED_INTEGRATION" found) $"{found}"
                  Assert.isTrue (List.contains "AUD.INTEGRATED_WHILE_ACTIVE" found) $"{found}"
                  Assert.isTrue (report.Findings |> List.exists (fun f -> f.Detail.Contains "WI-ASR-FINAL")) "names the work item"
                  Assert.isTrue (report.Findings |> List.exists (fun f -> f.Detail.Contains candidate)) "names the integrated candidate"
                  Assert.equal 0 report.VerifiedIntegrations
                  Assert.isTrue (not (fst (IntegrationAudit.demonstrated report))) "not operationally demonstrated"
                  ignore initial }

          { Name = "governance #30b: a verified gate check leaves only the pending completion record; a decision file is not platform evidence"
            Run =
              fun () ->
                  let at gate =
                      IntegrationAudit.examine "praxis-merge-gate" { Commit = head; Parents = [ targetHead; head ]; Committer = "GitHub"; Subject = "Merge pull request #4 from x"; PullRequest = Some 4; Candidate = Some head; ActiveWorkItemsIntroduced = [ "WI-9" ]; VerifiedCompletionRequired = true; Gate = gate; MergeTreeMatches = Some true; PlatformConfirmed = Some true; StrandedAfterMerge = 0 }
                      |> List.map _.Code

                  Assert.equal [ "AUD.COMPLETION_PENDING" ] (at (GateObservation.Check("success", "github-actions")))
                  Assert.equal [ "AUD.GATE_DECISION_UNAUTHENTICATED"; "AUD.INTEGRATED_WHILE_ACTIVE" ] (at (GateObservation.Decision "verified")) }

          { Name = "governance #5c: an evil merge (tree differs from the merge of its parents) and an unconfirmed merge claim are reported"
            Run =
              fun () ->
                  let root = repo "evil-merge"
                  write root "a.txt" "1\n"
                  commitAs root "GitHub" "Initial commit" |> ignore
                  git root [ "checkout"; "-q"; "-b"; "feature" ] |> ignore
                  write root "b.txt" "verified\n"
                  commitAs root "Claude" "Verified work" |> ignore
                  git root [ "checkout"; "-q"; "main" ] |> ignore
                  git root [ "-c"; "user.name=GitHub"; "merge"; "-q"; "--no-ff"; "--no-commit"; "feature" ] |> ignore
                  write root "evil.txt" "never verified\n"
                  git root [ "add"; "-A" ] |> ignore
                  git root [ "-c"; "user.name=GitHub"; "commit"; "-q"; "-m"; "Merge pull request #8 from x/feature" ] |> ignore
                  let report = GovernanceOperations.audit (auditRequest root) |> Result.defaultWith failwith
                  Assert.isTrue (report.Findings |> List.exists (fun f -> f.Code = "AUD.MERGE_TREE_MISMATCH")) $"{report.Findings}"
                  let unconfirmed =
                      IntegrationAudit.examine "praxis-merge-gate" { Commit = head; Parents = [ targetHead ]; Committer = "GitHub"; Subject = "Change (#9)"; PullRequest = Some 9; Candidate = Some head; ActiveWorkItemsIntroduced = []; VerifiedCompletionRequired = true; Gate = GateObservation.Check("success", "github-actions"); MergeTreeMatches = None; PlatformConfirmed = Some false; StrandedAfterMerge = 0 }
                  Assert.equal [ "AUD.UNCONFIRMED_MERGE" ] (unconfirmed |> List.map _.Code) }

          { Name = "governance #20b/#19: a direct push and a rewritten history are detected by the audit"
            Run =
              fun () ->
                  let root = repo "direct-push"
                  write root "a.txt" "1\n"
                  let first = commitAs root "GitHub" "Initial commit"
                  write root "a.txt" "2\n"
                  let pushed = commitAs root "GitHub" "Change straight to main (web editor)"
                  let report = GovernanceOperations.audit (auditRequest root) |> Result.defaultWith failwith
                  Assert.isTrue (report.Findings |> List.exists (fun f -> f.Code = "AUD.DIRECT_PUSH" && f.Commit = pushed)) $"{report.Findings}"
                  git root [ "reset"; "-q"; "--hard"; first ] |> ignore
                  write root "a.txt" "3\n"
                  commitAs root "GitHub" "Rewritten (#9)" |> ignore
                  let rewritten = GovernanceOperations.audit { auditRequest root with PreviousHead = Some pushed } |> Result.defaultWith failwith
                  Assert.isTrue rewritten.HistoryRewritten "history rewrite detected" }

          { Name = "governance #21c: a merge whose gate refused, or whose gate check came from another app, is reported"
            Run =
              fun () ->
                  let at gate =
                      IntegrationAudit.examine "praxis-merge-gate" { Commit = head; Parents = [ targetHead; head ]; Committer = "GitHub"; Subject = "Merge pull request #5 from x"; PullRequest = Some 5; Candidate = Some head; ActiveWorkItemsIntroduced = []; VerifiedCompletionRequired = true; Gate = gate; MergeTreeMatches = Some true; PlatformConfirmed = Some true; StrandedAfterMerge = 0 }
                      |> List.map _.Code

                  Assert.equal [ "AUD.GATE_REFUSED_BUT_MERGED" ] (at (GateObservation.Check("failure", "github-actions")))
                  Assert.equal [ "AUD.GATE_SOURCE_UNTRUSTED" ] (at (GateObservation.Check("success", "some-bot")))
                  Assert.equal [] (at (GateObservation.Check("success", "github-actions"))) }

          { Name = "governance: merge authorize end to end over Git: committed receipts verify, a later code change invalidates them"
            Run =
              fun () ->
                  let root = repo "merge-authorize"
                  let policyText = $$"""{ "schemaVersion": "echelon.validation-policy/1", "policyId": "example-merge-gate-v1", "repository": "{{repository}}", "validations": [ { "name": "tests", "evidenceId": "tests", "command": ["true"] } ] }""" + "\n"
                  let realPolicy = { policy with Digest = CompletionGate.sha256 (Encoding.UTF8.GetBytes policyText); RequiredValidations = [ "tests" ] }
                  write root "ros.json" $$"""{ "repository": { "identity": { "repository": "{{repository}}" } }, "governance": { "requireVerifiedMerge": true } }"""
                  write root GovernanceOperations.PolicyPath policyText
                  write root GovernanceOperations.TrustRootPath (GovernanceJson.indented (GovernanceJson.renderTrustRoot (trust ())))
                  write root ".ros/context/current.json" """{ "workItems": [ { "id": "WI-1", "state": "active" }, { "id": "WI-SELF", "state": "active" } ] }"""
                  write root GovernanceOperations.AuthoritiesPath """{ "schema": "praxis.governance-authorities/1", "workAuthorizers": ["owner"], "elevatedApprovers": ["owner"], "exceptionAuthorities": ["owner"], "agentIdentities": ["claude[bot]"] }"""
                  write root GovernanceOperations.WorkAuthorizationsPath """{ "authorizations": [ { "workItem": "WI-1", "authorizer": "owner" }, { "workItem": "WI-AGENT", "authorizer": "claude[bot]" } ] }"""
                  write root ".github/workflows/ci.yml" "name: ci\non: [pull_request]\npermissions:\n  contents: read\njobs:\n  t:\n    runs-on: ubuntu-latest\n    steps:\n      - run: ./scripts/ci.sh\n"
                  write root "scripts/ci.sh" "dotnet test\n"
                  commitAs root "GitHub" "Initial commit" |> ignore
                  git root [ "checkout"; "-q"; "-b"; "feature" ] |> ignore
                  write root "src/App.fs" "module App\n"
                  let verified = commitAs root "Claude" "Implement"
                  let verifiedTree = git root [ "rev-parse"; verified + "^{tree}" ]
                  let receipt = { payload "tests" CheckStatus.Passed verified with TreeId = verifiedTree; ValidationPolicyDigest = realPolicy.Digest } |> signWith key.Value
                  write root ".praxis/receipts/WI-1/tests.json" (VerificationReceipt.renderEnvelope receipt)
                  let withReceipts = commitAs root "Claude" "Record receipts"

                  let request: AuthorizeRequest =
                      { Root = root; Repository = repository; BaseRef = "main"; Candidate = withReceipts; PullRequest = None; WorkItems = [ "WI-1" ]
                        ReceiptDirectories = []; JwksFile = None; GovernanceReport = None; Live = false }

                  let ok = GovernanceOperations.authorize request |> Result.defaultWith failwith
                  Assert.equal MergeOutcome.Verified ok.Outcome
                  let selfAuthorized = GovernanceOperations.authorize { request with WorkItems = [ "WI-SELF" ] } |> Result.defaultWith failwith
                  Assert.isTrue (selfAuthorized.Refusals |> List.exists (fun f -> f.Code = "MRG.WORK_ITEM_UNAUTHORIZED")) "an item merely present in .ros/ is not authorized"
                  let agentAuthorized = GovernanceOperations.authorize { request with WorkItems = [ "WI-AGENT" ] } |> Result.defaultWith failwith
                  Assert.isTrue (agentAuthorized.Refusals |> List.exists (fun f -> f.Code = "MRG.WORK_ITEM_UNAUTHORIZED")) "an agent identity cannot authorize work"
                  Assert.equal (Some verified) ok.VerifiedCommit
                  write root "scripts/ci.sh" "exit 0\n"
                  let script = commitAs root "Claude" "Weaken the CI script"
                  let weakened = GovernanceOperations.authorize { request with Candidate = script } |> Result.defaultWith failwith
                  Assert.isTrue (weakened.Refusals |> List.exists (fun f -> f.Code = "MRG.PROTECTED_CHANGE_UNAUTHORIZED")) $"a script CI runs is protected: {weakened.Refusals}"
                  write root "src/App.fs" "module App\nlet sneaky = 1\n"
                  let changed = commitAs root "Claude" "Change after verification"
                  let late = GovernanceOperations.authorize { request with Candidate = changed } |> Result.defaultWith failwith
                  Assert.equal MergeOutcome.Refused late.Outcome
                  Assert.isTrue (late.Refusals |> List.exists (fun f -> f.Code = "MRG.ARTIFACT_CHANGED_AFTER_VERIFICATION")) $"{late.Refusals}"
                  git root [ "checkout"; "-q"; "main" ] |> ignore
                  write root "OTHER.md" "main moved\n"
                  commitAs root "GitHub" "Merge pull request #2 from x/other" |> ignore
                  let stale = GovernanceOperations.authorize request |> Result.defaultWith failwith
                  Assert.isTrue (stale.Refusals |> List.exists (fun f -> f.Code = "MRG.STALE_BASE")) $"{stale.Refusals}" }

          { Name = "governance: bootstrap writes a keyless proposal once and refuses to replace a protected trust root"
            Run =
              fun () ->
                  let root = repo "bootstrap"
                  write root "ros.json" """{ "name": "x" }"""
                  commitAs root "GitHub" "Initial commit" |> ignore

                  let request: BootstrapRequest =
                      { Root = root; Repository = repository; Branch = "main"; ApprovedBy = "owner"; AgentIdentities = [ "claude[bot]" ]
                        Validations = [ "tests", [ "dotnet"; "test" ] ]; BaseRef = "main"; Now = now }

                  let written = GovernanceBootstrap.bootstrap request |> Result.defaultWith failwith
                  Assert.isTrue (written |> List.contains GovernanceOperations.TrustRootPath) "trust root written"
                  let trustText = File.ReadAllText(Path.Combine(root, GovernanceOperations.TrustRootPath))
                  Assert.isTrue (not (trustText.Contains "PRIVATE") && not (Directory.GetFiles(root, "*.pem", SearchOption.AllDirectories) |> Array.isEmpty |> not)) "no key material is created"
                  let parsed = GovernanceJson.parseTrustRoot trustText |> Result.defaultWith failwith
                  Assert.isTrue (parsed.Verifiers |> List.forall (fun v -> match v.Identity with VerifierIdentity.Workload w -> w.WorkflowRef.EndsWith "praxis-merge-gate.yml@refs/heads/main" | _ -> false)) "workload identity pinned to the protected ref"
                  Assert.isTrue (Result.isError (GovernanceBootstrap.bootstrap request)) "a second bootstrap in the worktree is refused"
                  commitAs root "GitHub" "Merge pull request #1 from x/bootstrap" |> ignore
                  File.Delete(Path.Combine(root, GovernanceOperations.TrustRootPath))
                  match GovernanceBootstrap.bootstrap request with
                  | Error message -> Assert.isTrue (message.Contains "already has a completion authority") message
                  | Ok _ -> failwith "bootstrap replaced a protected trust root" }

          // ---- review findings (WI-0116 adversarial review round 1) -------------------

          { Name = "governance review: an edited approval comment is never an approval; a 12+ character head prefix is"
            Run =
              fun () ->
                  let facts = { baseFacts () with ChangedPaths = [ { Path = ".github/workflows/ci.yml"; Kind = ChangeKind.Modified } ] }
                  { facts with ElevatedApprovals = [ { approval head with Edited = true } ] } |> refused "MRG.PROTECTED_CHANGE_UNAUTHORIZED" |> ignore
                  Assert.equal MergeOutcome.Verified (MergeAuthorization.decide { facts with ElevatedApprovals = [ approval (head.Substring(0, 12)) ] }).Outcome
                  { facts with ElevatedApprovals = [ approval (head.Substring(0, 11)) ] } |> refused "MRG.PROTECTED_CHANGE_UNAUTHORIZED" |> ignore }

          { Name = "governance review: a pull request into any branch but the protected one is refused"
            Run = fun () -> { baseFacts () with TargetBranch = "evil" } |> refused "MRG.TARGET_MISMATCH" |> ignore }

          { Name = "governance review: work authorization requires a listed human authorizer, in person, unedited"
            Run =
              fun () ->
                  let a: WorkAuthorization = { Authorizer = "owner"; WorkItem = "WI-1"; AuthorizedAt = now; ViaApplication = None; Edited = false }
                  Assert.isTrue (Authority.workAuthorized authorities a) "the owner"
                  Assert.isTrue (not (Authority.workAuthorized authorities { a with Authorizer = "claude[bot]" })) "an agent identity"
                  Assert.isTrue (not (Authority.workAuthorized authorities { a with ViaApplication = Some "claude" })) "via an application"
                  Assert.isTrue (not (Authority.workAuthorized authorities { a with Edited = true })) "edited"
                  Assert.isTrue (not (Authority.workAuthorized authorities { a with Authorizer = "stranger" })) "not listed" }

          { Name = "governance review: a rename out of a prohibited path is not covered by standing authorization"
            Run =
              fun () ->
                  let standing =
                      { Id = "SA-docs"; Repositories = [ repository ]; Categories = [ { Name = "docs"; AllowedPaths = [ "docs/**" ]; MaxChangedFiles = 10 } ]
                        ProhibitedPaths = [ "docs/security/**" ]; ApprovedBy = "owner"; ValidFrom = now.AddDays -1.0; ExpiresAt = now.AddDays 1.0; RevokedAt = None }

                  { baseFacts () with AuthorizedWorkItems = []; Standing = [ standing ]; ChangedPaths = [ { Path = "docs/policy.md"; Kind = ChangeKind.Renamed "docs/security/policy.md" } ] }
                  |> refused "MRG.WORK_ITEM_UNAUTHORIZED"
                  |> ignore }

          { Name = "governance review: an exception the candidate adds is valid only with its authority's own approval of this head"
            Run =
              fun () ->
                  let ex = exceptionFor [ "MRG.VALIDATION_INCOMPLETE" ]
                  let facts = { baseFacts () with Receipts = []; Exceptions = [ ex ]; CandidateExceptionIds = [ ex.Id ]; ChangedPaths = [ { Path = ".praxis/governance/exceptions/EX-1.json"; Kind = ChangeKind.Added } ] }
                  facts |> refused "MRG.EXCEPTION_INVALID" |> ignore
                  let approved = MergeAuthorization.decide { facts with ElevatedApprovals = [ approval head ] }
                  Assert.equal (MergeOutcome.AuthorizedByException [ "EX-1" ]) approved.Outcome }

          { Name = "governance review: a workload verifier's events, issuer or repository change is a weakening"
            Run =
              fun () ->
                  let wider =
                      { workloadTrust () with
                          ConfigVersion = 2
                          Verifiers = (workloadTrust ()).Verifiers |> List.map (fun v -> { v with Identity = (match v.Identity with VerifierIdentity.Workload w -> VerifierIdentity.Workload { w with Events = w.Events @ [ "push" ] } | other -> other) }) }

                  Assert.isTrue (VerifierTrust.weakenings (workloadTrust ()) wider |> List.exists (fun w -> w.Contains "replaces the identity")) "events widened" }

          { Name = "governance review: a write-by-default workflow token and other check-writing PR workflows are not enforcement"
            Run =
              fun () ->
                  let writeDefault = evaluate (snapshotWith "write" """{ "notProtected": true }""" fullRules """[ { "id": 1, "bypass_actors": [] } ]""" gateWorkflow) praxisReady
                  Assert.equal ControlStatus.NotEnforced (control "GOV-DEFAULT-TOKEN" writeDefault).Status
                  let writers = gateWorkflow.Replace("]", ", { \"path\": \".github/workflows/ci.yml\", \"triggers\": [ \"pull_request\" ], \"writesChecks\": true } ]")
                  let report = evaluate (snapshot """{ "notProtected": true }""" fullRules """[ { "id": 1, "bypass_actors": [] } ]""" writers) praxisReady
                  Assert.equal ControlStatus.NotEnforced (control "GOV-CHECK-WRITERS" report).Status
                  Assert.isTrue (report.Level <> ReadinessLevel.VerifiedCompletionEnabled) "not reported as enforced"
                  Assert.isTrue (GovernanceOperations.writesChecks "on: pull_request\njobs: {}\n") "no permissions block inherits the default"
                  Assert.isTrue (not (GovernanceOperations.writesChecks "permissions:\n  contents: read\n")) "read-only" }

          { Name = "governance review: branch rules unavailable on the plan are platform-unavailable, not absent"
            Run =
              fun () ->
                  let report = evaluate (snapshot """{ "unknown": true, "reason": "HTTP 403: Upgrade to GitHub Pro or make this repository public to enable this feature." }""" "[]" "[]" gateWorkflow) praxisReady
                  Assert.equal ControlStatus.PlatformUnavailable (control "GOV-RULES" report).Status
                  Assert.equal ControlStatus.NotEnforced (control "GOV-PR" report).Status }

          { Name = "governance review: drift reports an enforced control becoming not required; profile errors are reported"
            Run =
              fun () ->
                  let enforced = evaluate protectedSnapshot praxisReady
                  let relaxed = { enforced with Controls = enforced.Controls |> List.map (fun c -> if c.Id = "GOV-PR" then { c with Status = ControlStatus.NotRequired } else c) }
                  Assert.isTrue (PlatformGovernance.drift enforced relaxed |> List.exists (fun f -> f.Detail.StartsWith "GOV-PR")) "relaxation is drift"
                  let broken = evaluate protectedSnapshot { praxisReady with ProfileError = Some "requirePullRequest must be a boolean" }
                  Assert.equal ControlStatus.NotEnforced (control "GOV-PROFILE" broken).Status }

          { Name = "governance review: branch names with slashes and quoted validation commands are handled"
            Run =
              fun () ->
                  Assert.equal "release/1.x" (GovernanceOperations.branchOf "origin/release/1.x")
                  Assert.equal "main" (GovernanceOperations.branchOf "refs/heads/main")
                  Assert.equal (Ok("tests", [ "sh"; "-c"; "make test" ])) (GovernanceFiles.validationArgument "tests=[\"sh\",\"-c\",\"make test\"]")
                  Assert.equal (Ok("tests", [ "dotnet"; "test" ])) (GovernanceFiles.validationArgument "tests=dotnet test")
                  Assert.isTrue (Result.isError (GovernanceFiles.validationArgument "tests=[]")) "an empty command is refused" }

          { Name = "governance review: the policy digest is over the exact committed bytes, trailing newline included"
            Run =
              fun () ->
                  let root = repo "policy-digest"
                  let text = "{ \"schemaVersion\": \"echelon.validation-policy/1\", \"policyId\": \"p\", \"validations\": [ { \"name\": \"t\", \"evidenceId\": \"t\", \"command\": [\"true\"] } ] }\n"
                  write root GovernanceOperations.PolicyPath text
                  commitAs root "GitHub" "Initial commit" |> ignore
                  let state = GovernanceOperations.loadProtected root "main" |> Result.defaultWith failwith
                  let fromGit = match state.Policy with Some(Ok p) -> p.Digest | _ -> failwith "no policy"
                  let fromFile = (ValidationPolicyJson.readFile (Path.Combine(root, GovernanceOperations.PolicyPath)) |> Result.defaultWith failwith).Digest
                  Assert.equal fromFile fromGit }

          { Name = "governance review: the validated rung needs a passing GitHub Actions check on the protected head, not one from another app"
            Run =
              fun () ->
                  let other = protectedSnapshot.DeepClone()
                  other["headChecks"] <- JsonNode.Parse """{ "check_runs": [ { "name": "praxis-merge-gate", "app": { "slug": "some-bot" }, "conclusion": "success" } ] }"""
                  let report = evaluate other praxisReady
                  Assert.isTrue (report.Ladder |> List.exists (fun r -> r.Level = ReadinessLevel.Validated && not r.Reached)) "another app's check does not validate" }
        ]
