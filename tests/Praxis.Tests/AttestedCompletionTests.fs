namespace Praxis.Tests

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Threading
open Praxis.Application.Work
open Praxis.Domain.Ordo
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// PRAXIS-REL-009 (F1 attested evidence), PRAXIS-REL-011 (F3 clean
/// completion identity) and PRAXIS-REL-012 (F4 derived completion), as REL-001
/// scenarios against real Git repositories, the real verifier engine and
/// signing, and the out-of-tree store. Praxis-side refusals use an in-process
/// Dokimos stand-in, so they never depend on a Dokimos build; the end-to-end
/// cases run the real Dokimos (`PRAXIS_TEST_DOKIMOS_DLL`) when it provides
/// `completion.verify-attested/1` and report themselves skipped otherwise.
[<RequireQualifiedAccess>]
module AttestedCompletionTests =
    let private t name run = { Name = $"attested completion: {name}"; Run = run }

    let private item = "PRAXIS-ATT-1"
    let private identity = [ "PRAXIS_ACTOR_KIND", "agent"; "PRAXIS_ACTOR", "test/agent-a"; "PRAXIS_TELEMETRY_PROVIDER", "test"; "PRAXIS_TELEMETRY_RUNTIME", "test" ]
    let private lease = TimeSpan.FromMinutes 30.0
    let private hex (text: string) = Convert.ToHexString(Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

    let private field (node: JsonNode) (name: string) : JsonNode = node[name]
    let private first (node: JsonNode) : JsonNode = node.AsArray()[0]
    let private text (node: JsonNode) = node.GetValue<string>()

    type private Fixture =
        { Root: string
          Host: string
          Key: VerifierKey
          Policy: ValidationPolicy
          ContractDigest: string
          Authority: string
          Contract: string }

    /// A repository with a pinned acceptance file, and host-owned inputs
    /// (policy, contract, authority trusting `key`) outside the candidate tree.
    let private fixture (command: string list) (attested: bool) =
        let root = CliHarness.temporaryDirectory "praxis-attested"
        let host = CliHarness.temporaryDirectory "praxis-attested-host"
        CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
        CliHarness.write root ".ros/work/queue.json" $"{{\"schemaVersion\":\"1.0.0\",\"items\":[{{\"id\":\"{item}\",\"status\":\"captured\"}}]}}"
        CliHarness.write root "src/code.txt" "v1\n"
        CliHarness.write root "tests/Acceptance.txt" "the oracle\n"
        CliHarness.commitAll root "fixture"
        let commands = command |> List.map (sprintf "\"%s\"") |> String.concat ","
        let oracleHash = hex "the oracle\n"

        CliHarness.write host "policy.json"
            ($"{{\"schemaVersion\":\"echelon.validation-policy/1\",\"policyId\":\"praxis-test\",\"validations\":[{{\"name\":\"unit-tests\",\"evidenceId\":\"EV-TESTS\",\"command\":[{commands}],\"timeoutSeconds\":60}}],"
             + $"\"protectedPaths\":[{{\"path\":\"tests/Acceptance.txt\",\"sha256\":\"{oracleHash}\"}}]}}")

        let policy = ValidationPolicyJson.readFile (Path.Combine(host, "policy.json")) |> Result.defaultWith failwith
        let contract = OrdoTestContracts.contract [ item ] []
        CliHarness.write host "contract.json" contract
        let digest = (OrdoContractAdmission.admit OrdoContractAdmission.implementedVersions contract |> Result.defaultWith (fun d -> failwith $"{d}")).Digest
        let key = VerifierKey.generate ()
        let trusted = key.Trusted

        let attestation =
            if attested then
                $",\"TrustedVerifierKeys\":[{{\"KeyId\":\"{trusted.KeyId}\",\"Algorithm\":\"{trusted.Algorithm}\",\"PublicKey\":\"{trusted.PublicKey}\"}}],\"ValidationPolicyDigest\":\"{policy.Digest}\""
            else
                ""

        CliHarness.write host "authority.json"
            ($"{{\"OrdoContractVersion\":\"\",\"OrdoContractDigest\":\"{digest}\",\"SupportedOrdoContractVersions\":[\"ecir/1\"],\"RequiredRequirementIds\":[\"{item}\"],"
             + $"\"RequiredValidationNames\":[\"unit-tests\"],\"RequiredEvidenceIds\":[\"EV-TESTS\"]{attestation}}}")

        { Root = root; Host = host; Key = key; Policy = policy; ContractDigest = digest
          Authority = Path.Combine(host, "authority.json"); Contract = Path.Combine(host, "contract.json") }

    let private claimPorts (f: Fixture) = FileWorkClaims.ports f.Root ClaimStoreChoice.Local None None

    let private claim (f: Fixture) claimant token =
        match WorkClaims.acquire (claimPorts f) { WorkItemId = item; Claimant = claimant; ClaimToken = token; LeaseDuration = lease; ExpectedRevision = None; RecoverOnly = false } with
        | ClaimOutcome.Succeeded(claim, _) -> claim
        | other -> failwith $"expected a claim, got {other}"

    let private head (f: Fixture) = CliHarness.git f.Root [ "rev-parse"; "HEAD" ]

    /// `work verify` through the real engine (in process, no socket).
    let private verify (f: Fixture) token =
        let ports = { FileWorkVerification.ports f.Root None (TimeSpan.FromMinutes 5.0) with Request = fun request -> Ok(TrustedVerifierEngine.run f.Key f.Policy f.ContractDigest request) }
        WorkVerification.run ports item token

    let private receipts outcome =
        match outcome with
        | WorkVerifyOutcome.Verified(_, stored, _) -> stored
        | other -> failwith $"expected receipts, got {other}"

    /// The Dokimos stand-in: accepts exactly the packet it is given and records the authority.
    let private accepting (seen: ResizeArray<CompletionAuthority>) : CompletionVerifier =
        fun packet authority ->
            seen.Add authority

            VerifierRun.Exited(
                0,
                Ok
                    { Contract = CompletionGate.ReceiptContract; SchemaVersion = CompletionGate.ReceiptSchemaVersion; DokimosVersion = "stand-in"
                      Verdict = "verified"; ExitCode = 0; WorkItemId = packet.WorkItemId; Commit = packet.Commit; VerificationRevision = packet.VerificationRevision
                      PacketDigest = CompletionGate.packetDigest packet; RequirementCount = packet.RequirementIds.Length
                      EvidenceDigests = packet.Evidence |> List.map _.Digest; ReusedEvidenceIds = []; Diagnostics = [] },
                "sha256:" + String('a', 64),
                ""
            )

    let private noExplicit =
        { Claimant = None; Generation = None; Commit = None; VerificationRevision = None; OrdoContractVersion = None; OrdoContractDigest = None
          RequirementIds = []; DecisionIds = []; ConflictResolutionIds = []; Validations = []; Evidence = []; TelemetryExecutions = [] }

    let private arguments (f: Fixture) token =
        { WorkItemId = item; Token = token; AuthorityPath = Some f.Authority; ContractPath = Some f.Contract; ExpectedOrdoDigest = None
          Explicit = noExplicit; ExplicitEvidence = []; Timeout = TimeSpan.FromSeconds 120.0; Execution = None }

    let private completeWith (f: Fixture) verifier (args: CompletionArguments) =
        FileDerivedCompletion.run f.Root ClaimStoreChoice.Local None (Some verifier) args |> Result.defaultWith failwith

    let private complete (f: Fixture) verifier token = completeWith f verifier (arguments f token)

    let private codes (report: DerivedCompletionReport) =
        match report.Result.Outcome with
        | CompletionOutcome.Rejected(findings, _) -> findings |> List.map _.Code |> List.distinct
        | other -> failwith $"expected a refusal, got {other}"

    let private status (f: Fixture) = CliHarness.git f.Root [ "status"; "--porcelain" ]

    /// Re-signs `payload` (possibly edited) with `key`.
    let private signed (key: VerifierKey) (payload: ReceiptPayload) = VerifierKey.sign key (VerificationReceipt.render payload)

    let private storeEnvelope (f: Fixture) (name: string) (envelope: ReceiptEnvelope) =
        let path = Path.Combine(FileWorkVerification.directory f.Root item, name + ".json")
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, VerificationReceipt.renderEnvelope envelope)
        path

    /// The gate's verdict on one envelope under `f`'s bound authority and `held`.
    let private gate (f: Fixture) (held: WorkClaim) (revision: string) (envelope: ReceiptEnvelope) =
        let input = FileWorkClaims.readAuthorityInput f.Root f.Authority |> Result.defaultWith failwith
        let bound = OrdoBoundAuthority.bind None (File.ReadAllText f.Contract) input |> Result.defaultWith (fun e -> failwith $"{e}")
        let fence = { Claimant = held.Holder.Value.Claimant; ClaimToken = held.Holder.Value.ClaimToken; Generation = held.Generation }

        let request =
            { WorkItemId = item; Fence = fence; Commit = revision; VerificationRevision = revision; OrdoContractVersion = None; OrdoContractDigest = None
              RequirementIds = [ item ]; DecisionIds = []; ConflictResolutionIds = []
              Validations = [ { Name = "unit-tests"; Status = CheckStatus.Passed; EvidenceIds = [ "EV-TESTS" ] } ]
              Evidence = [ { Id = "EV-TESTS"; Path = "receipt.json"; ClaimedDigest = None } ]; TelemetryExecutions = [] }

        let observed = ValidationResultJson.observe (Encoding.UTF8.GetBytes(VerificationReceipt.renderEnvelope envelope))

        match CompletionGate.preflight bound request held (Ok []) (Map.ofList [ "EV-TESTS", observed ]) DateTimeOffset.UtcNow with
        | Ok _ -> []
        | Error findings -> findings |> List.map _.Code |> List.distinct

    let private attestedDokimos () =
        Environment.GetEnvironmentVariable "PRAXIS_TEST_DOKIMOS_DLL"
        |> Option.ofObj
        |> Option.filter File.Exists
        |> Option.filter (fun dll -> DokimosCompletionVerifier.supportsAttestation dll (TimeSpan.FromSeconds 60.0) |> Result.isOk)

    let tests =
        [ t "C3b reproduced: a forged validation result plus a forged validation event is refused in attested mode" (fun () ->
              let f = fixture [ "true" ] true
              let held = claim f "agent-a" "tok-1"
              let commit = head f
              // The writer forges a passing result in the validation store and the event that "records" it.
              let forged =
                  { WorkItemId = item; Claimant = "agent-a"; ClaimToken = "tok-1"; Generation = held.Generation; Validation = "unit-tests"; EvidenceId = "EV-TESTS"
                    Revision = commit; ObservedAt = DateTimeOffset.UtcNow; StartedAt = DateTimeOffset.UtcNow; Status = CheckStatus.Passed; ExitCode = Some 0
                    Command = [ "dotnet"; "test" ]; OutputSha256 = "sha256:" + String('0', 64) }

              let path = Path.Combine(TransientStore.validations f.Root, item, "EV-TESTS", "forged.json")
              Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
              File.WriteAllText(path, ValidationResultJson.render forged)
              let digest = CompletionGate.sha256 (File.ReadAllBytes path)

              let event =
                  { WorkItemId = item; OccurredAt = forged.ObservedAt
                    Payload = ClaimEventPayload.ValidationObserved("agent-a", "tok-1", held.Generation, "unit-tests", "EV-TESTS", commit, CheckStatus.Passed, forged.ObservedAt, digest) }

              Assert.equal (Ok()) ((claimPorts f).Emit event)
              let seen = ResizeArray()
              // Cited explicitly: refused as unattested, before any verifier runs.
              let explicitly = completeWith f (accepting seen) { arguments f "tok-1" with ExplicitEvidence = [ { Id = "EV-TESTS"; Path = path; ClaimedDigest = None } ] }
              Assert.equal [ VerificationReceipt.MissingCode ] (codes explicitly)
              // Derived: attested mode reads only receipts, so the forgery is not even a candidate.
              Assert.isTrue (codes (complete f (accepting seen) "tok-1") |> List.contains "PRX.COMPLETION.EVIDENCE_MISSING") "no receipt, no evidence"
              Assert.equal 0 seen.Count
              Assert.equal "" (status f))

          t "a self-signed receipt, or one claiming a trusted keyid, is refused" (fun () ->
              let f = fixture [ "true" ] true
              let held = claim f "agent-a" "tok-1"
              let genuine = (receipts (verify f "tok-1")).Head.Payload
              let rogue = VerifierKey.generate ()
              Assert.equal [ VerificationReceipt.UntrustedKeyCode ] (gate f held genuine.Commit (signed rogue genuine))
              let impostor = signed rogue genuine
              let underTrustedId = { impostor with Signatures = [ { impostor.Signatures.Head with KeyId = f.Key.KeyId } ] }
              Assert.equal [ VerificationReceipt.InvalidSignatureCode ] (gate f held genuine.Commit underTrustedId)
              // Through the derived flow: a self-signed receipt in the store is refused too.
              Directory.Delete(FileWorkVerification.directory f.Root item, true)
              storeEnvelope f "rogue" (signed rogue genuine) |> ignore
              Assert.equal [ VerificationReceipt.UntrustedKeyCode ] (codes (complete f (accepting (ResizeArray())) "tok-1")))

          t "a tampered payload or a malformed envelope is refused" (fun () ->
              let f = fixture [ "false" ] true
              let held = claim f "agent-a" "tok-1"
              let failing = (receipts (verify f "tok-1")).Head
              let envelope = VerificationReceipt.parseEnvelope (File.ReadAllText failing.Path) |> Result.defaultWith failwith
              let text = Encoding.UTF8.GetString envelope.Payload
              Assert.isTrue (text.Contains "\"status\":\"failed\"") text
              let tampered = { envelope with Payload = Encoding.UTF8.GetBytes(text.Replace("\"status\":\"failed\"", "\"status\":\"passed\"")) }
              Assert.equal [ VerificationReceipt.InvalidSignatureCode ] (gate f held failing.Payload.Commit tampered)
              Assert.equal [ VerificationReceipt.MalformedCode ] (gate f held failing.Payload.Commit { envelope with PayloadType = "application/json" })
              let garbage = signed f.Key failing.Payload |> fun e -> { e with Payload = Encoding.UTF8.GetBytes "{\"schemaVersion\":\"x\"}" }
              Assert.equal [ VerificationReceipt.InvalidSignatureCode ] (gate f held failing.Payload.Commit garbage)
              let signedGarbage = VerifierKey.sign f.Key (Encoding.UTF8.GetBytes "{\"schemaVersion\":\"x\"}")
              Assert.equal [ VerificationReceipt.MalformedCode ] (gate f held failing.Payload.Commit signedGarbage))

          t "a genuine receipt for another work item, generation, commit, contract or policy is refused" (fun () ->
              let f = fixture [ "true" ] true
              let held = claim f "agent-a" "tok-1"
              let genuine = (receipts (verify f "tok-1")).Head.Payload
              let refused payload = gate f held genuine.Commit (signed f.Key payload)
              Assert.equal [] (refused genuine)
              Assert.equal [ VerificationReceipt.BindingMismatchCode ] (refused { genuine with WorkItemId = "PRAXIS-ATT-2" })
              Assert.equal [ VerificationReceipt.BindingMismatchCode ] (refused { genuine with FencingGeneration = genuine.FencingGeneration + 1L })
              Assert.equal [ VerificationReceipt.BindingMismatchCode ] (refused { genuine with Commit = String('b', 40) })
              Assert.equal [ VerificationReceipt.BindingMismatchCode ] (refused { genuine with ValidationPolicyDigest = "sha256:" + String('c', 64) })
              Assert.equal [ VerificationReceipt.BindingMismatchCode ] (refused { genuine with OrdoContractDigest = "sha256:" + String('d', 64) })
              Assert.equal [ VerificationReceipt.BindingMismatchCode ] (refused { genuine with Validation = "lint" })
              Assert.equal [ VerificationReceipt.BindingMismatchCode ] (refused { genuine with EvidenceId = "EV-OTHER" })
              Assert.equal [ VerificationReceipt.StaleCode ] (refused { genuine with ObservedAt = held.Holder.Value.AcquiredAt - TimeSpan.FromSeconds 1.0 })
              Assert.equal [ VerificationReceipt.NotPassedCode ] (refused { genuine with Status = CheckStatus.Unavailable }))

          t "a genuine failing receipt prevents completion" (fun () ->
              let f = fixture [ "false" ] true
              claim f "agent-a" "tok-1" |> ignore
              let verified = verify f "tok-1"
              Assert.equal [ CheckStatus.Failed ] (receipts verified |> List.map _.Payload.Status)
              let seen = ResizeArray()
              Assert.equal [ VerificationReceipt.NotPassedCode ] (codes (complete f (accepting seen) "tok-1"))
              Assert.equal 0 seen.Count)

          t "a genuine passing receipt completes with a clean worktree and a metadata-only commit on the verified commit" (fun () ->
              let f = fixture [ "true" ] true
              claim f "agent-a" "tok-1" |> ignore
              Assert.equal "" (status f)
              let stored = receipts (verify f "tok-1")
              Assert.equal "" (status f)
              let verifiedCommit = head f
              let seen = ResizeArray()
              let report = complete f (accepting seen) "tok-1"

              match report.Result.Outcome with
              | CompletionOutcome.Completed(completed, completion) ->
                  Assert.equal ClaimStatus.Completed completed.Status
                  Assert.equal (stored |> List.map _.Digest) completion.EvidenceDigests
              | other -> failwith $"expected a verified completion, got {other}"

              Assert.equal (Some verifiedCommit) report.Result.VerifiedCommit
              let metadata = report.Result.MetadataCommit.Value
              Assert.equal metadata (head f)
              Assert.equal verifiedCommit (CliHarness.git f.Root [ "rev-parse"; "HEAD^" ])
              Assert.equal ".ros/events/events.jsonl" (CliHarness.git f.Root [ "diff"; "--name-only"; verifiedCommit; "HEAD" ])
              Assert.equal "" (status f)
              let tracked = CliHarness.read f.Root ".ros/events/events.jsonl"
              Assert.isTrue (tracked.Contains "\"type\":\"work.completion.verified\"" && tracked.Contains $"\"verifiedCommit\":\"{verifiedCommit}\"" && tracked.Contains stored.Head.Digest) tracked
              Assert.isTrue (not (tracked.Contains "tok-1")) "the tracked event never carries the claim token"
              // Dokimos received the receipt as the observation's Attestation (I6 member names).
              let wire = WorkClaimJson.authorityNode seen[0]
              let observation = wire["Evidence"].AsArray()[0]
              Assert.equal VerificationReceipt.PayloadType (text (field (field observation "Attestation") "payloadType"))
              Assert.equal stored.Head.Digest (observation["Digest"].GetValue<string>())
              Assert.equal f.Key.KeyId (text (field (first (field wire "TrustedVerifierKeys")) "KeyId"))
              Assert.equal f.Policy.Digest (wire["ValidationPolicyDigest"].GetValue<string>())
              // Timings cover every step; the second admission of the same digest is a cache hit.
              Assert.equal [ "admission"; "claim"; "receipts"; "dokimos"; "metadataCommit" ] (report.Timings |> List.map fst)
              Assert.equal (Some "miss") report.AdmissionCache
              let replay = complete f (accepting seen) "tok-1"

              match replay.Result.Outcome with
              | CompletionOutcome.Replayed _ -> ()
              | other -> failwith $"expected an idempotent replay, got {other}"

              Assert.equal (Some "hit") replay.AdmissionCache
              Assert.equal (Some metadata) replay.Result.MetadataCommit
              Assert.equal metadata (head f)
              Assert.equal 1 seen.Count
              // completion-identity: HEAD's diff from verifiedCommit is limited to .ros/.
              let identityRun = CliHarness.ros f.Root [ "work"; "completion-identity"; "--id"; item ]
              Assert.equal 0 identityRun.Exit
              Assert.equal "consistent" (text (field (CliHarness.json identityRun.Out) "outcome"))
              CliHarness.write f.Root "src/code.txt" "v2\n"
              CliHarness.commitAll f.Root "source change after completion"
              let violated = CliHarness.ros f.Root [ "work"; "completion-identity"; "--id"; item ]
              Assert.equal 3 violated.Exit
              Assert.isTrue (violated.Out.Contains CompletionIdentity.ViolatedCode && violated.Out.Contains "src/code.txt") violated.Out)

          t "a dirty worktree, a moved HEAD and a source change after the receipt all fail closed" (fun () ->
              let f = fixture [ "true" ] true
              claim f "agent-a" "tok-1" |> ignore
              receipts (verify f "tok-1") |> ignore
              let seen = ResizeArray()
              CliHarness.write f.Root "src/code.txt" "uncommitted\n"
              Assert.equal [ DerivedCompletion.DirtyWorktreeCode ] (codes (complete f (accepting seen) "tok-1"))
              CliHarness.write f.Root "notes.txt" "untracked\n"
              CliHarness.git f.Root [ "checkout"; "-q"; "--"; "src/code.txt" ] |> ignore
              Assert.equal [ DerivedCompletion.DirtyWorktreeCode ] (codes (complete f (accepting seen) "tok-1"))
              File.Delete(Path.Combine(f.Root, "notes.txt"))
              CliHarness.write f.Root "src/code.txt" "v2\n"
              CliHarness.commitAll f.Root "source change after the receipt"
              Assert.equal [ DerivedCompletion.HeadMismatchCode ] (codes (complete f (accepting seen) "tok-1"))
              // The agent client refuses to request receipts for a dirty worktree.
              CliHarness.write f.Root "src/code.txt" "v3\n"

              match verify f "tok-1" with
              | WorkVerifyOutcome.Refused finding -> Assert.equal ValidationEvidence.DirtyWorktreeCode finding.Code
              | other -> failwith $"expected a dirty-worktree refusal, got {other}"

              Assert.equal 0 seen.Count)

          t "a receipt replayed after re-claim, or from another generation, is refused; the verifier refuses a stale holder" (fun () ->
              let f = fixture [ "true" ] true
              let first = claim f "agent-a" "tok-1"
              let old = (receipts (verify f "tok-1")).Head
              let fence = { Claimant = "agent-a"; ClaimToken = "tok-1"; Generation = first.Generation }
              WorkClaims.release (claimPorts f) item fence false |> ignore
              let second = claim f "agent-a" "tok-2"
              Assert.equal (first.Generation + 1L) second.Generation
              let seen = ResizeArray()
              // Derived: the old generation's receipt is not selected at all.
              Assert.isTrue (codes (complete f (accepting seen) "tok-2") |> List.contains "PRX.COMPLETION.EVIDENCE_MISSING") "no receipt for this generation"
              // Cited explicitly: its binding does not match the current generation.
              let replayed = completeWith f (accepting seen) { arguments f "tok-2" with ExplicitEvidence = [ { Id = "EV-TESTS"; Path = old.Path; ClaimedDigest = None } ] }
              // Bound to the old generation, and observed before the new lease began.
              Assert.equal [ VerificationReceipt.BindingMismatchCode; VerificationReceipt.StaleCode ] (codes replayed)
              Assert.equal 0 seen.Count
              // The verifier itself re-reads the claim and refuses the stale generation or holder.
              let request = { RepoPath = f.Root; WorkItemId = item; Claimant = "agent-a"; FencingGeneration = first.Generation; Commit = head f }

              match TrustedVerifierEngine.run f.Key f.Policy f.ContractDigest request with
              | VerifierResponse.Refused(code, _) -> Assert.equal VerifierEngine.StaleClaimCode code
              | other -> failwith $"expected a stale-claim refusal, got {other}"

              match TrustedVerifierEngine.run f.Key f.Policy f.ContractDigest { request with Claimant = "agent-b"; FencingGeneration = second.Generation } with
              | VerifierResponse.Refused(code, _) -> Assert.equal VerifierEngine.StaleClaimCode code
              | other -> failwith $"expected a stale-holder refusal, got {other}"

              // And the stale token is refused by the client before it asks.
              match verify f "tok-1" with
              | WorkVerifyOutcome.Refused finding -> Assert.equal "PRX.CLAIM.STALE_TOKEN" finding.Code
              | other -> failwith $"expected a stale-token refusal, got {other}")

          t "a protected-path change yields unavailable receipts and runs nothing" (fun () ->
              let f = fixture [ "sh"; "-c"; "touch ran-validation" ] true
              claim f "agent-a" "tok-1" |> ignore
              CliHarness.write f.Root "tests/Acceptance.txt" "a weakened oracle\n"
              CliHarness.commitAll f.Root "weaken the acceptance test"

              match verify f "tok-1" with
              | WorkVerifyOutcome.Verified(_, stored, diagnostics) ->
                  Assert.equal [ CheckStatus.Unavailable ] (stored |> List.map _.Payload.Status)
                  Assert.equal [ None ] (stored |> List.map _.Payload.ExitCode)
                  Assert.isTrue (diagnostics |> List.exists (fun d -> d.StartsWith VerifierEngine.ProtectedPathCode && d.Contains "tests/Acceptance.txt")) $"{diagnostics}"
              | other -> failwith $"expected unavailable receipts, got {other}"

              Assert.isTrue (not (File.Exists(Path.Combine(f.Root, "ran-validation")))) "nothing runs in the agent's worktree"
              Assert.equal [ VerificationReceipt.NotPassedCode ] (codes (complete f (accepting (ResizeArray())) "tok-1")))

          t "the verifier serves over a Unix domain socket and work verify stores compact receipts out of tree" (fun () ->
              let f = fixture [ "true" ] true
              let socket = Path.Combine(CliHarness.temporaryDirectory "praxis-verifier-socket", "v.sock")
              let ready = new ManualResetEventSlim(false)
              let startup = ref ""

              let server =
                  Thread(
                      (fun () ->
                          VerifierSocket.serve socket f.Key f.Policy f.ContractDigest (fun line -> startup.Value <- line; ready.Set())
                          |> ignore),
                      IsBackground = true
                  )

              server.Start()
              Assert.isTrue (ready.Wait(TimeSpan.FromSeconds 30.0)) "the verifier starts"
              let announced = CliHarness.json startup.Value
              Assert.equal f.Key.KeyId (announced["keyId"].GetValue<string>())
              Assert.equal f.Policy.Digest (announced["policyDigest"].GetValue<string>())
              Assert.equal f.Key.PublicKey (announced["publicKey"].GetValue<string>())
              Assert.equal 0 (CliHarness.rosWith f.Root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-1" ]).Exit
              let run = CliHarness.rosWith f.Root (("PRAXIS_VERIFIER_SOCKET", socket) :: identity) [ "work"; "verify"; "--id"; item; "--token"; "tok-1" ]
              Assert.equal 0 run.Exit
              let document = CliHarness.json run.Out
              Assert.equal "passed" (document["outcome"].GetValue<string>())
              let receipt = document["receipts"].AsArray()[0]
              Assert.equal "passed" (receipt["status"].GetValue<string>())
              Assert.isTrue ((receipt["path"].GetValue<string>()).StartsWith(Path.Combine(f.Root, ".git", "praxis", "receipts", item))) run.Out
              Assert.equal "" (status f)
              let missing = CliHarness.rosWith f.Root identity [ "work"; "verify"; "--id"; item; "--token"; "tok-1" ]
              Assert.equal 3 missing.Exit
              Assert.equal VerifierEngine.UnavailableCode (text (field (CliHarness.json missing.Out) "code")))

          t "verifier run is the same engine with a PEM key and writes the response document" (fun () ->
              let f = fixture [ "true" ] true
              let held = claim f "agent-a" "tok-1"
              let pem = Path.Combine(f.Host, "key.pem")
              File.WriteAllText(pem, VerifierKey.exportPem f.Key)
              let out = Path.Combine(f.Host, "response.json")

              let run =
                  CliHarness.ros f.Root
                      [ "verifier"; "run"; "--repo"; f.Root; "--commit"; head f; "--work-item"; item; "--claimant"; "agent-a"; "--generation"; string held.Generation
                        "--policy"; Path.Combine(f.Host, "policy.json"); "--contract-digest"; f.ContractDigest; "--key-file"; pem; "--out"; out ]

              Assert.equal 0 run.Exit
              Assert.equal f.Key.KeyId (text (field (CliHarness.json run.Out) "keyId"))

              match VerifierWire.parseResponse (File.ReadAllText out) with
              | Ok(VerifierResponse.Signed([ receipt ], _)) -> Assert.equal [] (gate f held receipt.Payload.Commit receipt.Envelope)
              | other -> failwith $"expected one signed receipt, got {other}")

          t "F4: explicit flags must equal the derived values" (fun () ->
              let f = fixture [ "true" ] true
              claim f "agent-a" "tok-1" |> ignore
              receipts (verify f "tok-1") |> ignore
              let seen = ResizeArray()
              let mismatch explicit = codes (completeWith f (accepting seen) { arguments f "tok-1" with Explicit = explicit })
              Assert.equal [ DerivedCompletion.MismatchCode ] (mismatch { noExplicit with Generation = Some 7L })
              Assert.equal [ DerivedCompletion.MismatchCode ] (mismatch { noExplicit with Claimant = Some "agent-b" })
              Assert.equal [ DerivedCompletion.MismatchCode ] (mismatch { noExplicit with Commit = Some(String('f', 40)) })
              Assert.equal [ DerivedCompletion.MismatchCode ] (mismatch { noExplicit with RequirementIds = [ "OTHER" ] })
              Assert.equal [ DerivedCompletion.MismatchCode ] (mismatch { noExplicit with Validations = [ { Name = "lint"; Status = CheckStatus.Passed; EvidenceIds = [ "EV-TESTS" ] } ] })
              Assert.equal 0 seen.Count
              // Equal explicit values are accepted.
              let agreeing = { noExplicit with Generation = Some 1L; Claimant = Some "agent-a"; Commit = Some(head f); RequirementIds = [ item ] }

              match (completeWith f (accepting seen) { arguments f "tok-1" with Explicit = agreeing }).Result.Outcome with
              | CompletionOutcome.Completed _ -> ()
              | other -> failwith $"expected agreeing flags to complete, got {other}")

          t "legacy .ros/claims are refused with a stable code until work claims migrate moves them" (fun () ->
              let f = fixture [ "true" ] false
              let held = claim f "agent-a" "tok-1"
              // Simulate a pre-I8 repository: the claim lives only in the tracked legacy store.
              let legacy = Path.Combine(f.Root, ".ros", "claims", item)
              Directory.CreateDirectory(Path.GetDirectoryName legacy) |> ignore
              Directory.Move(FileWorkClaimStore.itemDirectory f.Root item, legacy)
              let refused = CliHarness.ros f.Root [ "work"; "claims"; item ]
              Assert.equal 3 refused.Exit
              Assert.equal FileWorkClaimStore.LegacyStoreCode (text (field (CliHarness.json refused.Out) "code"))
              let renewed = CliHarness.rosWith f.Root identity [ "work"; "renew"; "--id"; item; "--claimant"; "agent-a"; "--token"; "tok-1"; "--generation"; "1" ]
              Assert.equal 3 renewed.Exit
              Assert.isTrue (renewed.Out.Contains FileWorkClaimStore.LegacyStoreCode) renewed.Out
              let migrated = CliHarness.ros f.Root [ "work"; "claims"; "migrate" ]
              Assert.equal 0 migrated.Exit
              Assert.equal "migrated" (text (field (first (field (CliHarness.json migrated.Out) "claims")) "outcome"))
              Assert.isTrue (not (Directory.Exists legacy)) "the legacy record is removed only after it reads back"
              let shown = CliHarness.ros f.Root [ "work"; "claims"; item ]
              Assert.equal 0 shown.Exit
              Assert.equal held.Generation ((field (first (field (CliHarness.json shown.Out) "claims")) "generation").GetValue<int64>()))

          t "attested mode fails closed on a Dokimos without completion.verify-attested" (fun () ->
              let directory = CliHarness.temporaryDirectory "praxis-old-dokimos"
              let script = Path.Combine(directory, "dokimos")
              File.WriteAllText(script, "#!/bin/sh\nif [ \"$1\" = version ]; then echo '{\"ReleaseVersion\":\"0.6.0\"}'; exit 0; fi\nexit 9\n")
              File.SetUnixFileMode(script, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
              let f = fixture [ "true" ] true
              claim f "agent-a" "tok-1" |> ignore
              receipts (verify f "tok-1") |> ignore
              let old = DokimosCompletionVerifier.create (Ok script) (TimeSpan.FromSeconds 30.0)
              Assert.equal [ "PRX.COMPLETION.VERIFIER_ATTESTATION_UNSUPPORTED" ] (codes (complete f old "tok-1"))
              Assert.equal "" (status f))

          t "F4 end to end: complete-verified --id --token alone, through the real Dokimos" (fun () ->
              match Environment.GetEnvironmentVariable "PRAXIS_TEST_DOKIMOS_DLL" |> Option.ofObj |> Option.filter File.Exists with
              | None -> eprintfn "SKIP attested completion: PRAXIS_TEST_DOKIMOS_DLL is not set to a built Dokimos.Cli.dll"
              | Some dll ->
                  // Non-attested mode: the validate-run path, with its state out of tree.
                  let f = fixture [ "true" ] false
                  Assert.equal 0 (CliHarness.rosWith f.Root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-1" ]).Exit
                  let validate = [ "work"; "validate-run"; "--id"; item; "--token"; "tok-1"; "--generation"; "1"; "--validation"; "unit-tests"; "--evidence-id"; "EV-TESTS"; "--"; "true" ]
                  Assert.equal 0 (CliHarness.rosWith f.Root identity validate).Exit
                  Assert.equal "" (status f)
                  let verifiedCommit = head f
                  let environment = [ "PRAXIS_DOKIMOS_EXECUTABLE", dll; FileDerivedCompletion.AuthorityVariable, f.Authority; FileDerivedCompletion.ContractVariable, f.Contract ]
                  let run = CliHarness.rosWith f.Root (environment @ identity) [ "work"; "complete-verified"; "--id"; item; "--token"; "tok-1" ]
                  Assert.equal 0 run.Exit
                  let document = CliHarness.json run.Out
                  Assert.equal "verified" (document["outcome"].GetValue<string>())
                  Assert.equal verifiedCommit (document["verifiedCommit"].GetValue<string>())
                  Assert.equal (head f) (document["metadataCommit"].GetValue<string>())
                  for step in [ "claim"; "admission"; "receipts"; "dokimos"; "metadataCommit" ] do
                      Assert.isTrue (not (isNull (field (field document "timings") step))) $"timing {step}: {run.Out}"
                  Assert.equal "" (status f)
                  Assert.equal verifiedCommit (CliHarness.git f.Root [ "rev-parse"; "HEAD^" ]))

          t "end to end through a Dokimos with attested mode: genuine receipts verify, forged ones are refused by Dokimos too" (fun () ->
              match attestedDokimos () with
              | None -> eprintfn "SKIP attested completion: PRAXIS_TEST_DOKIMOS_DLL does not provide completion.verify-attested/1"
              | Some dll ->
                  let f = fixture [ "true" ] true
                  Assert.equal 0 (CliHarness.rosWith f.Root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-1" ]).Exit
                  let stored = receipts (verify f "tok-1")
                  let verifiedCommit = head f
                  let dokimos = DokimosCompletionVerifier.create (Ok dll) (TimeSpan.FromSeconds 120.0)
                  // Defence in depth: Dokimos itself refuses a self-signed receipt on the wire.
                  let seen = ResizeArray()
                  let rogue = signed (VerifierKey.generate ()) stored.Head.Payload

                  let forging : CompletionVerifier =
                      fun packet authority ->
                          seen.Add authority
                          let wire = WorkClaimJson.authorityNode authority
                          let observation = first (field wire "Evidence")
                          observation["Attestation"] <- VerificationReceipt.envelopeNode rogue
                          let directory = CliHarness.temporaryDirectory "praxis-attested-wire"
                          File.WriteAllText(Path.Combine(directory, "packet.json"), (WorkClaimJson.packetNode packet).ToJsonString())
                          File.WriteAllText(Path.Combine(directory, "authority.json"), wire.ToJsonString())
                          let run = CliHarness.run "dotnet" [ dll; "completion"; "verify"; "--packet"; Path.Combine(directory, "packet.json"); "--authority"; Path.Combine(directory, "authority.json") ] []
                          VerifierRun.Exited(run.Exit, WorkClaimJson.parseReceipt run.Out, CompletionGate.sha256 (Encoding.UTF8.GetBytes run.Out), run.Err)

                  let refused = codes (complete f forging "tok-1")
                  Assert.isTrue (refused |> List.contains "DOK.COMPLETION.ATTESTATION_UNTRUSTED_KEY") $"{refused}"
                  Assert.equal "" (status f)
                  // The genuine receipt verifies, and the completion identity is clean.
                  let report = complete f dokimos "tok-1"

                  match report.Result.Outcome with
                  | CompletionOutcome.Completed(_, completion) -> Assert.isTrue (completion.VerifierVersion.StartsWith "0.7") completion.VerifierVersion
                  | other -> failwith $"expected the real Dokimos to verify the attested completion, got {other}"

                  Assert.equal (Some verifiedCommit) report.Result.VerifiedCommit
                  Assert.equal verifiedCommit (CliHarness.git f.Root [ "rev-parse"; "HEAD^" ])
                  Assert.equal "" (status f)) ]
