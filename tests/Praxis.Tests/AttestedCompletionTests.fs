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
/// `completion.verify-attested/1`, report themselves skipped while it is unset,
/// and fail when it is set but unusable (EP-A3-R2, `DokimosTestRuntime`).
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
        let ports = { FileWorkVerification.ports f.Root ClaimStoreChoice.Local None (TimeSpan.FromMinutes 5.0) with Request = fun request -> Ok(TrustedVerifierEngine.run f.Key f.Policy f.ContractDigest request) }
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

    let private attestedDokimos () = DokimosTestRuntime.attested ()

    /// A bare remote seeded from `f.Root`, and two clones of it (agents A and B).
    let private shared (f: Fixture) =
        let parent = CliHarness.temporaryDirectory "praxis-attested-remote"
        let origin = Path.Combine(parent, "origin.git")
        CliHarness.git parent [ "init"; "-q"; "--bare"; origin ] |> ignore
        CliHarness.git f.Root [ "push"; "-q"; origin; "main" ] |> ignore
        let clone name =
            let path = Path.Combine(parent, name)
            CliHarness.git parent [ "clone"; "-q"; "--branch"; "main"; origin; path ] |> ignore
            path
        clone "a", clone "b"

    let private gitClaim (root: string) claimant token (duration: TimeSpan) =
        let ports = FileWorkClaims.ports root (ClaimStoreChoice.GitRef "origin") None None

        match WorkClaims.acquire ports { WorkItemId = item; Claimant = claimant; ClaimToken = token; LeaseDuration = duration; ExpectedRevision = None; RecoverOnly = false } with
        | ClaimOutcome.Succeeded(claim, _) -> claim
        | other -> failwith $"expected a claim on the remote, got {other}"

    /// `work verify` from `root` through `choice`, with the real engine in process.
    let private verifyIn (f: Fixture) (root: string) (choice: ClaimStoreChoice) token =
        let ports = { FileWorkVerification.ports root choice None (TimeSpan.FromMinutes 5.0) with Request = fun request -> Ok(TrustedVerifierEngine.run f.Key f.Policy f.ContractDigest request) }
        WorkVerification.run ports item token

    /// GATE-3 / C3b: the writer forges a passing result in the validation
    /// store and the event that "records" it. Returns the forged file.
    let private forgeValidation (f: Fixture) (held: WorkClaim) =
        let commit = head f

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
        path

    /// REL-GATE-003: the `attestation` label as the stored claim record, the
    /// tracked completion event and the `complete-verified` output carry it.
    let private labels (f: Fixture) (report: DerivedCompletionReport) =
        let stored =
            Directory.GetFiles(FileWorkClaimStore.itemDirectory f.Root item, "*.json", SearchOption.AllDirectories)
            |> Array.choose (fun path ->
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as record when not (isNull (field record "completion")) -> Some(text (field (field record "completion") "attestation"))
                | _ -> None)
            |> Array.distinct
            |> Array.toList

        let tracked =
            File.ReadAllLines(Path.Combine(f.Root, ".ros", "events", "events.jsonl"))
            |> Array.map JsonNode.Parse
            |> Array.filter (fun event -> not (isNull (field event "type")) && text (field event "type") = "work.completion.verified")
            |> Array.map (fun event -> text (field event "attestation"))
            |> Array.toList

        let output, _ = VerificationJson.completion report
        stored, tracked, text (field (CliHarness.json output) "attestation")

    /// A ros.json whose workProtocol holds `members` and pins `keys`.
    let private pinnedPolicy (members: string) (keys: string list) =
        let pinned = keys |> List.map (sprintf "\"%s\"") |> String.concat ","
        $"{{\"workProtocol\":{{{members},\"trustedVerifierKeyIds\":[{pinned}]}}}}"

    /// A copy of `f`'s authority at `path` that trusts `keys` (none: keyless).
    let private authorityTrusting (f: Fixture) (keys: TrustedVerifierKey list) (path: string) =
        let node = JsonNode.Parse(File.ReadAllText f.Authority).AsObject()
        node.Remove "TrustedVerifierKeys" |> ignore
        node.Remove "ValidationPolicyDigest" |> ignore

        if not keys.IsEmpty then
            node["TrustedVerifierKeys"] <-
                JsonArray(
                    keys
                    |> List.map (fun key -> JsonObject(dict [ "KeyId", JsonValue.Create key.KeyId :> JsonNode; "Algorithm", JsonValue.Create key.Algorithm; "PublicKey", JsonValue.Create key.PublicKey ]) :> JsonNode)
                    |> Array.ofList
                )

            node["ValidationPolicyDigest"] <- JsonValue.Create f.Policy.Digest

        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, node.ToJsonString())
        path

    let private claimIn (root: string) claimant token =
        match WorkClaims.acquire (FileWorkClaims.ports root ClaimStoreChoice.Local None None) { WorkItemId = item; Claimant = claimant; ClaimToken = token; LeaseDuration = lease; ExpectedRevision = None; RecoverOnly = false } with
        | ClaimOutcome.Succeeded(claim, _) -> claim
        | other -> failwith $"expected a claim, got {other}"

    let private completeIn (root: string) (f: Fixture) verifier (args: CompletionArguments) =
        FileDerivedCompletion.run root ClaimStoreChoice.Local None (Some verifier) args |> Result.defaultWith failwith

    let private policy members = { ProtectedCompletionPolicy.empty with RequireVerifiedCompletion = List.contains "verified" members; RequireAttestedEvidence = List.contains "attested" members; AllowUnattestedEvidence = List.contains "optout" members }

    let private key c = "sha256:" + String(c, 64)

    let tests =
        [ t "C3b reproduced: a forged validation result plus a forged validation event is refused in attested mode" (fun () ->
              let f = fixture [ "true" ] true
              let held = claim f "agent-a" "tok-1"
              let path = forgeValidation f held
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
                  Assert.equal CompletionAttestation.Attested completion.Attestation
              | other -> failwith $"expected a verified completion, got {other}"

              Assert.equal ([ "attested" ], [ "attested" ], "attested") (labels f report)

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
              // Stamps are millisecond-precise and the lease window is half-open
              // [acquired, expires): a re-claim in the same millisecond as the old
              // observation would make it look fresh. Start the next lease strictly later.
              let observed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
              SpinWait.SpinUntil(fun () -> DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > observed) |> ignore
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
              let request = { RepoPath = f.Root; WorkItemId = item; Claimant = "agent-a"; FencingGeneration = first.Generation; Commit = head f; Remote = None }

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
              match DokimosTestRuntime.dll () with
              | None -> DokimosTestRuntime.skip "attested completion"
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
              | None -> DokimosTestRuntime.skip "attested completion"
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
                  Assert.equal "" (status f))

          t "workProtocol.requireAttestedEvidence refuses a host authority that trusts no verifier key (rollout gate)" (fun () ->
              let f = fixture [ "true" ] false
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"requireAttestedEvidence\":true}}"
              CliHarness.commitAll f.Root "policy"
              claim f "agent-a" "tok-1" |> ignore
              let seen = ResizeArray()
              let refused = codes (complete f (accepting seen) "tok-1")
              Assert.equal [ FileDerivedCompletion.AttestationRequiredCode ] refused
              Assert.equal 0 seen.Count)

          t "workProtocol.requireAttestedEvidence that is not a boolean fails closed" (fun () ->
              let f = fixture [ "true" ] true
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"requireAttestedEvidence\":\"yes\"}}"
              CliHarness.commitAll f.Root "policy"
              claim f "agent-a" "tok-1" |> ignore
              Assert.equal [ FileDerivedCompletion.AttestationRequiredCode ] (codes (complete f (accepting (ResizeArray())) "tok-1")))

          t "workProtocol.requireAttestedEvidence admits an authority that pins verifier keys" (fun () ->
              let f = fixture [ "true" ] true
              // REL-GATE-004: the host verifier's key is pinned in protected policy.
              CliHarness.write f.Root "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true" [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll f.Root "policy"
              claim f "agent-a" "tok-1" |> ignore
              verify f "tok-1" |> ignore
              let report = complete f (accepting (ResizeArray())) "tok-1"
              match report.Result.Outcome with
              | CompletionOutcome.Completed _ -> ()
              | other -> failwith $"expected completion under the policy, got {other}")

          t "REL-GATE-003: workProtocol.requireVerifiedCompletion alone implies requireAttestedEvidence" (fun () ->
              let f = fixture [ "true" ] false
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"requireVerifiedCompletion\":true}}"
              CliHarness.commitAll f.Root "policy"
              let held = claim f "agent-a" "tok-1"
              forgeValidation f held |> ignore
              let seen = ResizeArray()
              let report = complete f (accepting seen) "tok-1"
              Assert.equal [ FileDerivedCompletion.AttestationRequiredCode ] (codes report)

              match report.Result.Outcome with
              | CompletionOutcome.Rejected(findings, _) ->
                  Assert.isTrue (findings.Head.Detail.Contains "requireVerifiedCompletion") findings.Head.Detail
              | other -> failwith $"expected a refusal, got {other}"

              Assert.equal 0 seen.Count
              Assert.equal "" (status f)
              // An explicit allowUnattestedEvidence: false is the same as absent.
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"requireVerifiedCompletion\":true,\"allowUnattestedEvidence\":false}}"
              CliHarness.commitAll f.Root "policy: explicit false"
              Assert.equal [ FileDerivedCompletion.AttestationRequiredCode ] (codes (complete f (accepting seen) "tok-1"))
              Assert.equal 0 seen.Count)

          t "REL-GATE-003: with the allowUnattestedEvidence opt-out the GATE-3 forgery completes and is labelled unattested" (fun () ->
              let f = fixture [ "true" ] false
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"requireVerifiedCompletion\":true,\"allowUnattestedEvidence\":true}}"
              CliHarness.commitAll f.Root "policy"
              let held = claim f "agent-a" "tok-1"
              forgeValidation f held |> ignore
              let report = complete f (accepting (ResizeArray())) "tok-1"

              match report.Result.Outcome with
              | CompletionOutcome.Completed(_, completion) -> Assert.equal CompletionAttestation.Unattested completion.Attestation
              | other -> failwith $"expected the opt-out to admit unattested evidence, got {other}"

              Assert.equal ([ "unattested" ], [ "unattested" ], "unattested") (labels f report)
              Assert.equal "" (status f)
              // The label survives a replay.
              let replay = complete f (accepting (ResizeArray())) "tok-1"

              match replay.Result.Outcome with
              | CompletionOutcome.Replayed(_, completion) -> Assert.equal CompletionAttestation.Unattested completion.Attestation
              | other -> failwith $"expected an idempotent replay, got {other}"

              let _, _, replayed = labels f replay
              Assert.equal "unattested" replayed)

          t "REL-GATE-003: a malformed allowUnattestedEvidence fails closed" (fun () ->
              for value in [ "\"yes\""; "1"; "null"; "{}" ] do
                  let f = fixture [ "true" ] false
                  CliHarness.write f.Root "ros.json" $"{{\"workProtocol\":{{\"requireVerifiedCompletion\":true,\"allowUnattestedEvidence\":{value}}}}}"
                  CliHarness.commitAll f.Root "policy"
                  let held = claim f "agent-a" "tok-1"
                  forgeValidation f held |> ignore
                  let seen = ResizeArray()
                  Assert.equal [ FileDerivedCompletion.AttestationRequiredCode ] (codes (complete f (accepting seen) "tok-1"))
                  Assert.equal 0 seen.Count

              // Malformed is refused even when the authority is attested and nothing else is required.
              let f = fixture [ "true" ] true
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"allowUnattestedEvidence\":\"yes\"}}"
              CliHarness.commitAll f.Root "policy"
              claim f "agent-a" "tok-1" |> ignore
              verify f "tok-1" |> ignore
              Assert.equal [ FileDerivedCompletion.AttestationRequiredCode ] (codes (complete f (accepting (ResizeArray())) "tok-1")))

          t "REL-GATE-003: allowUnattestedEvidence never overrides requireAttestedEvidence" (fun () ->
              let f = fixture [ "true" ] false
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"requireVerifiedCompletion\":true,\"requireAttestedEvidence\":true,\"allowUnattestedEvidence\":true}}"
              CliHarness.commitAll f.Root "policy"
              let held = claim f "agent-a" "tok-1"
              forgeValidation f held |> ignore
              let seen = ResizeArray()
              Assert.equal [ FileDerivedCompletion.AttestationRequiredCode ] (codes (complete f (accepting seen) "tok-1"))
              Assert.equal 0 seen.Count)

          t "REL-GATE-003: requireVerifiedCompletion admits an attested authority and labels the completion attested" (fun () ->
              let f = fixture [ "true" ] true
              // REL-GATE-004: the host verifier's key is pinned in protected policy.
              CliHarness.write f.Root "ros.json" (pinnedPolicy "\"requireVerifiedCompletion\":true" [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll f.Root "policy"
              claim f "agent-a" "tok-1" |> ignore
              receipts (verify f "tok-1") |> ignore
              let report = complete f (accepting (ResizeArray())) "tok-1"

              match report.Result.Outcome with
              | CompletionOutcome.Completed(_, completion) -> Assert.equal CompletionAttestation.Attested completion.Attestation
              | other -> failwith $"expected an attested completion under the policy, got {other}"

              Assert.equal ([ "attested" ], [ "attested" ], "attested") (labels f report))

          t "REL-GATE-003: the attestation policy is a pure function of the three flags" (fun () ->
              let required a v o = (FileWorkClaims.attestationRequirement a v o).IsSome

              Assert.equal
                  [ false; false; true; false; true; true; true; true ]
                  [ for a in [ false; true ] do
                        for v in [ false; true ] do
                            for o in [ false; true ] do
                                yield required a v o ]
              // Absent ros.json: nothing required.
              Assert.equal (Ok None) (FileWorkClaims.readAttestationRequirement (CliHarness.temporaryDirectory "praxis-no-policy")))

          t "git store: a claim on the shared remote is verified and completed from a clone with no local claim" (fun () ->
              let f = fixture [ "true" ] true
              let a, b = shared f
              let remoteA = ClaimStoreChoice.GitRef "origin"
              let held = gitClaim a "agent-a" "tok-1" lease
              Assert.isTrue (not (Directory.Exists(FileWorkClaimStore.itemDirectory a item))) "no local claim exists"
              // A verifies through the remote's claim ref; the verifier re-reads it there.
              let stored = receipts (verifyIn f a remoteA "tok-1")
              Assert.equal [ CheckStatus.Passed ] (stored |> List.map _.Payload.Status)
              Assert.equal [ held.Generation ] (stored |> List.map _.Payload.FencingGeneration)
              // The local path still does not see the remote claim.
              match verifyIn f a ClaimStoreChoice.Local "tok-1" with
              | WorkVerifyOutcome.Refused finding -> Assert.equal "PRX.CLAIM.NOT_HELD" finding.Code
              | other -> failwith $"expected the local store to hold nothing, got {other}"

              let seen = ResizeArray()
              let report = FileDerivedCompletion.run a remoteA None (Some(accepting seen)) { arguments f "tok-1" with WorkItemId = item } |> Result.defaultWith failwith

              match report.Result.Outcome with
              | CompletionOutcome.Completed(completed, _) -> Assert.equal ClaimStatus.Completed completed.Status
              | other -> failwith $"expected a verified completion through the git store, got {other}"

              Assert.equal (Some(CliHarness.git a [ "rev-parse"; "HEAD^" ])) report.Result.VerifiedCommit
              Assert.equal "" (CliHarness.git a [ "status"; "--porcelain" ])
              // B, on another clone, sees the completed claim on the remote.
              match (GitRefWorkClaimStore.create b "origin" FileWorkClaims.gitTimeout).Read item with
              | Ok(Some claim) -> Assert.equal ClaimStatus.Completed claim.Status
              | other -> failwith $"expected B to read the completed claim, got {other}")

          t "git store: another clone cannot verify the holder's claim, and a stale generation after remote recovery is refused" (fun () ->
              let f = fixture [ "true" ] true
              let a, b = shared f
              let remote = ClaimStoreChoice.GitRef "origin"
              let first = gitClaim a "agent-a" "tok-1" (TimeSpan.FromSeconds 2.0)
              // B holds no token for A's claim; asking as itself is refused by the verifier too.
              match verifyIn f b remote "tok-b" with
              | WorkVerifyOutcome.Refused finding -> Assert.equal "PRX.CLAIM.STALE_TOKEN" finding.Code
              | other -> failwith $"expected B to be refused, got {other}"

              let asB = { RepoPath = b; WorkItemId = item; Claimant = "agent-b"; FencingGeneration = first.Generation; Commit = CliHarness.git b [ "rev-parse"; "HEAD" ]; Remote = Some "origin" }

              match TrustedVerifierEngine.run f.Key f.Policy f.ContractDigest asB with
              | VerifierResponse.Refused(code, _) -> Assert.equal VerifierEngine.StaleClaimCode code
              | other -> failwith $"expected a stale-holder refusal, got {other}"

              // A's lease passes; B recovers on the remote at generation + 1.
              Thread.Sleep(TimeSpan.FromSeconds 2.5)
              let ports = FileWorkClaims.ports b remote None None

              let recovered =
                  match WorkClaims.recover ports { WorkItemId = item; Claimant = "agent-b"; ClaimToken = "tok-2"; LeaseDuration = lease; ExpectedRevision = None; RecoverOnly = true } with
                  | ClaimOutcome.Succeeded(claim, _) -> claim
                  | other -> failwith $"expected B to recover, got {other}"

              Assert.equal (first.Generation + 1L) recovered.Generation

              match verifyIn f a remote "tok-1" with
              | WorkVerifyOutcome.Refused finding -> Assert.isTrue (finding.Code.StartsWith "PRX.CLAIM.") finding.Code
              | other -> failwith $"expected A's stale claim to be refused, got {other}"

              let stale = { asB with RepoPath = a; Claimant = "agent-a"; Commit = CliHarness.git a [ "rev-parse"; "HEAD" ] }

              match TrustedVerifierEngine.run f.Key f.Policy f.ContractDigest stale with
              | VerifierResponse.Refused(code, detail) -> Assert.isTrue (code = VerifierEngine.StaleClaimCode && detail.Contains "stale") detail
              | other -> failwith $"expected a stale-generation refusal, got {other}"

              // The new holder verifies from its own clone.
              Assert.equal [ recovered.Generation ] (receipts (verifyIn f b remote "tok-2") |> List.map _.Payload.FencingGeneration)
              // The CLI parses --store git for verify (no socket here: refused as unavailable, not as a local NOT_HELD).
              let cli = CliHarness.rosWith b identity [ "work"; "verify"; "--id"; item; "--token"; "tok-2"; "--store"; "git"; "--remote"; "origin" ]
              Assert.equal 3 cli.Exit
              Assert.equal VerifierEngine.UnavailableCode (text (field (CliHarness.json cli.Out) "code")))

          // ---- REL-GATE-004: protected policy and a pinned trust root ----

          t "REL-GATE-004 PROBE-3: a committed ros.json that drops requireAttestedEvidence is refused as a policy change" (fun () ->
              let f = fixture [ "true" ] false
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"requireAttestedEvidence\":true}}"
              CliHarness.commitAll f.Root "policy"
              let held = claim f "agent-a" "tok-1"
              // The agent weakens repository policy in its own commit, then forges store evidence.
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{}}"
              CliHarness.commitAll f.Root "agent drops attestation"
              forgeValidation f held |> ignore
              let seen = ResizeArray()
              let report = complete f (accepting seen) "tok-1"
              Assert.equal [ ProtectedCompletionPolicy.PolicyChangedCode ] (codes report)

              match report.Result.Outcome with
              | CompletionOutcome.Rejected(findings, _) -> Assert.isTrue (findings.Head.Detail.Contains "requireAttestedEvidence was removed") findings.Head.Detail
              | other -> failwith $"expected a refusal, got {other}"

              Assert.equal 0 seen.Count
              // Deleting ros.json outright, or only in the worktree, is the same change.
              File.Delete(Path.Combine(f.Root, "ros.json"))
              Assert.equal [ ProtectedCompletionPolicy.PolicyChangedCode ] (codes (complete f (accepting seen) "tok-1"))
              CliHarness.git f.Root [ "checkout"; "-q"; "HEAD~1"; "--"; "ros.json" ] |> ignore
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"requireVerifiedCompletion\":true,\"allowUnattestedEvidence\":true}}"
              Assert.equal [ ProtectedCompletionPolicy.PolicyChangedCode ] (codes (complete f (accepting seen) "tok-1"))
              Assert.equal 0 seen.Count)

          t "REL-GATE-004: editing ros.json in the claimed branch cannot disable requireAttestedEvidence set on the protected branch" (fun () ->
              let f = fixture [ "true" ] true
              CliHarness.write f.Root "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true" [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll f.Root "policy on main"
              let a, _ = shared f
              CliHarness.git a [ "checkout"; "-q"; "-b"; "agent/work" ] |> ignore
              let held = claimIn a "agent-a" "tok-1"
              let keyless = authorityTrusting f [] (Path.Combine(CliHarness.temporaryDirectory "praxis-agent-host", "keyless.json"))
              let seen = ResizeArray()

              for weakened in [ "{\"workProtocol\":{}}"; "{\"workProtocol\":{\"requireAttestedEvidence\":false,\"trustedVerifierKeyIds\":[]}}"; "{}" ] do
                  CliHarness.write a "ros.json" weakened
                  CliHarness.commitAll a "agent weakens policy"
                  let forgedHead = CliHarness.git a [ "rev-parse"; "HEAD" ]
                  let forged =
                      { WorkItemId = item; Claimant = "agent-a"; ClaimToken = "tok-1"; Generation = held.Generation; Validation = "unit-tests"; EvidenceId = "EV-TESTS"
                        Revision = forgedHead; ObservedAt = DateTimeOffset.UtcNow; StartedAt = DateTimeOffset.UtcNow; Status = CheckStatus.Passed; ExitCode = Some 0
                        Command = [ "true" ]; OutputSha256 = "sha256:" + String('0', 64) }
                  let path = Path.Combine(TransientStore.validations a, item, "EV-TESTS", "forged.json")
                  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                  File.WriteAllText(path, ValidationResultJson.render forged)
                  let report = completeIn a f (accepting seen) { arguments f "tok-1" with AuthorityPath = Some keyless }
                  Assert.equal [ ProtectedCompletionPolicy.PolicyChangedCode ] (codes report)

              // Moving the upstream locator in the branch is a policy change too.
              CliHarness.write a "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true,\"upstreamSync\":{\"remote\":\"origin\",\"branch\":\"agent/work\"}" [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll a "agent moves the protected branch"
              Assert.equal [ ProtectedCompletionPolicy.PolicyChangedCode ] (codes (completeIn a f (accepting seen) { arguments f "tok-1" with AuthorityPath = Some keyless }))
              Assert.equal 0 seen.Count
              // Restoring the protected policy restores the genuine path.
              CliHarness.write a "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true" [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll a "restore policy"
              receipts (verifyIn f a ClaimStoreChoice.Local "tok-1") |> ignore

              match (completeIn a f (accepting seen) (arguments f "tok-1")).Result.Outcome with
              | CompletionOutcome.Completed(_, completion) -> Assert.equal [ f.Key.Trusted.KeyId ] completion.VerifierKeyIds
              | other -> failwith $"expected the protected policy to admit the host verifier, got {other}")

          t "REL-GATE-004: strengthening the policy in the branch is not a policy change" (fun () ->
              let f = fixture [ "true" ] true
              CliHarness.write f.Root "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true" [ f.Key.Trusted.KeyId; key 'e' ])
              CliHarness.commitAll f.Root "policy on main"
              let a, _ = shared f
              claimIn a "agent-a" "tok-1" |> ignore
              CliHarness.write a "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true,\"requireVerifiedCompletion\":true" [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll a "stricter"
              receipts (verifyIn f a ClaimStoreChoice.Local "tok-1") |> ignore

              match (completeIn a f (accepting (ResizeArray())) (arguments f "tok-1")).Result.Outcome with
              | CompletionOutcome.Completed _ -> ()
              | other -> failwith $"expected a stricter branch policy to complete, got {other}")

          t "REL-GATE-004: an upstreamSync pointing at the agent's own branch cannot make its policy the protected one" (fun () ->
              let f = fixture [ "true" ] true
              CliHarness.write f.Root "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true" [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll f.Root "policy on main"
              let a, _ = shared f
              CliHarness.git a [ "checkout"; "-q"; "-b"; "agent/x" ] |> ignore
              claimIn a "agent-a" "tok-1" |> ignore
              let keyless = authorityTrusting f [] (Path.Combine(CliHarness.temporaryDirectory "praxis-agent-host", "keyless.json"))
              let own = VerifierKey.generate ()
              let ownAuthority = authorityTrusting f [ own.Trusted ] (Path.Combine(CliHarness.temporaryDirectory "praxis-agent-host", "own-key.json"))
              let seen = ResizeArray()
              let locator branch = $"\"upstreamSync\":{{\"remote\":\"origin\",\"branch\":\"{branch}\"}}"

              let attempts =
                  [ "{\"workProtocol\":{" + locator "agent/x" + "}}", keyless
                    "{\"workProtocol\":{\"requireVerifiedCompletion\":true,\"allowUnattestedEvidence\":true," + locator "agent/x" + "}}", keyless
                    pinnedPolicy ("\"requireAttestedEvidence\":true," + locator "agent/x") [ f.Key.Trusted.KeyId; own.Trusted.KeyId ], ownAuthority ]

              for weakened, authority in attempts do
                  CliHarness.write a "ros.json" weakened
                  CliHarness.commitAll a "agent weakens policy and points upstreamSync at its own branch"
                  // The remote-tracking ref of the agent's branch is HEAD: by update-ref, then by an ordinary push.
                  CliHarness.git a [ "update-ref"; "refs/remotes/origin/agent/x"; "HEAD" ] |> ignore
                  Assert.equal [ ProtectedCompletionPolicy.PolicyChangedCode ] (codes (completeIn a f (accepting seen) { arguments f "tok-1" with AuthorityPath = Some authority }))
                  CliHarness.git a [ "push"; "-q"; "origin"; "agent/x" ] |> ignore
                  Assert.equal [ ProtectedCompletionPolicy.PolicyChangedCode ] (codes (completeIn a f (accepting seen) { arguments f "tok-1" with AuthorityPath = Some authority }))

              // A locator naming a branch that does not exist is a moved locator, not a history pass.
              CliHarness.write a "ros.json" (pinnedPolicy ("\"requireAttestedEvidence\":true," + locator "nowhere") [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll a "agent points upstreamSync at a missing branch"
              let report = completeIn a f (accepting seen) { arguments f "tok-1" with AuthorityPath = Some keyless }
              Assert.equal [ ProtectedCompletionPolicy.PolicyChangedCode ] (codes report)

              match report.Result.Outcome with
              | CompletionOutcome.Rejected(findings, _) -> Assert.isTrue (findings.Head.Detail.Contains "upstreamSync moved from origin/main") findings.Head.Detail
              | other -> failwith $"expected a moved-locator refusal, got {other}"

              Assert.equal 0 seen.Count)

          t "REL-GATE-004: a protected policy that configures a non-main upstream is located there once and still completes" (fun () ->
              let f = fixture [ "true" ] true
              let develop = $"\"upstreamSync\":{{\"remote\":\"origin\",\"branch\":\"develop\"}}"
              CliHarness.write f.Root "ros.json" (pinnedPolicy ("\"requireAttestedEvidence\":true," + develop) [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll f.Root "policy on main names develop"
              let a, _ = shared f
              CliHarness.git a [ "push"; "-q"; "origin"; "main:develop" ] |> ignore
              CliHarness.git a [ "fetch"; "-q"; "origin" ] |> ignore
              CliHarness.git a [ "checkout"; "-q"; "-b"; "develop"; "origin/develop" ] |> ignore
              // develop moves ahead of main with a stricter policy; the relocated base is develop's.
              CliHarness.write a "ros.json" (pinnedPolicy ("\"requireAttestedEvidence\":true,\"requireVerifiedCompletion\":true," + develop) [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll a "develop: stricter"
              CliHarness.git a [ "push"; "-q"; "origin"; "develop" ] |> ignore
              CliHarness.git a [ "checkout"; "-q"; "-b"; "feature" ] |> ignore
              claimIn a "agent-a" "tok-1" |> ignore
              // Dropping develop's stricter flag in the feature branch is a change relative to develop, not to main.
              CliHarness.write a "ros.json" (pinnedPolicy ("\"requireAttestedEvidence\":true," + develop) [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll a "feature drops requireVerifiedCompletion"
              Assert.equal [ ProtectedCompletionPolicy.PolicyChangedCode ] (codes (completeIn a f (accepting (ResizeArray())) (arguments f "tok-1")))
              CliHarness.write a "ros.json" (pinnedPolicy ("\"requireAttestedEvidence\":true,\"requireVerifiedCompletion\":true," + develop) [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll a "feature restores develop's policy"
              receipts (verifyIn f a ClaimStoreChoice.Local "tok-1") |> ignore

              match (completeIn a f (accepting (ResizeArray())) (arguments f "tok-1")).Result.Outcome with
              | CompletionOutcome.Completed(_, completion) -> Assert.equal [ f.Key.Trusted.KeyId ] completion.VerifierKeyIds
              | other -> failwith $"expected the develop-configured protected policy to complete, got {other}")

          t "REL-GATE-004: the bootstrap locator and the single re-location are pure" (fun () ->
              Assert.equal ("origin", "main") (ProtectedCompletionPolicy.bootstrapLocator None)
              Assert.equal ("origin", "trunk") (ProtectedCompletionPolicy.bootstrapLocator (ProtectedCompletionPolicy.parseOriginHead "refs/remotes/origin/trunk\n"))
              Assert.equal None (ProtectedCompletionPolicy.parseOriginHead "refs/heads/main")
              Assert.equal None (ProtectedCompletionPolicy.parseOriginHead "refs/remotes/origin/")
              Assert.equal None (ProtectedCompletionPolicy.relocation ("origin", "main") (policy []))
              Assert.equal (Some("origin", "develop")) (ProtectedCompletionPolicy.relocation ("origin", "main") { policy [] with Upstream = "origin", "develop" }))

          t "REL-GATE-004 PROBE-4: an authority that pins the caller's own key is untrusted" (fun () ->
              let f = fixture [ "true" ] true
              // No trust root pinned: the caller-chosen authority cannot be one.
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"requireAttestedEvidence\":true}}"
              CliHarness.commitAll f.Root "policy"
              let held = claim f "agent-a" "tok-1"
              let own = VerifierKey.generate ()
              let stored = receipts (verify f "tok-1")
              storeEnvelope f "self-signed" (signed own stored.Head.Payload) |> ignore
              let ownAuthority = authorityTrusting f [ own.Trusted ] (Path.Combine(CliHarness.temporaryDirectory "praxis-agent-host", "own-key.json"))
              let seen = ResizeArray()
              let refused = complete f (accepting seen) "tok-1" |> codes
              Assert.equal [ ProtectedCompletionPolicy.UntrustedAuthorityCode ] refused
              Assert.equal [ ProtectedCompletionPolicy.UntrustedAuthorityCode ] (codes (completeWith f (accepting seen) { arguments f "tok-1" with AuthorityPath = Some ownAuthority }))
              // Adding a pin later is itself a policy change: required attestation with no pin trusts no key.
              CliHarness.write f.Root "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true" [ own.Trusted.KeyId ])
              CliHarness.commitAll f.Root "agent pins its own key"
              Assert.equal [ ProtectedCompletionPolicy.PolicyChangedCode ] (codes (completeWith f (accepting seen) { arguments f "tok-1" with AuthorityPath = Some ownAuthority }))
              // With the host key pinned from the start, an authority adding the caller's key is still untrusted ...
              let g = fixture [ "true" ] true
              CliHarness.write g.Root "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true" [ g.Key.Trusted.KeyId ])
              CliHarness.commitAll g.Root "policy pins the host verifier"
              claim g "agent-a" "tok-1" |> ignore
              receipts (verify g "tok-1") |> ignore
              let ownOnly = authorityTrusting g [ own.Trusted ] (Path.Combine(CliHarness.temporaryDirectory "praxis-agent-host", "own-key.json"))
              let both = authorityTrusting g [ g.Key.Trusted; own.Trusted ] (Path.Combine(CliHarness.temporaryDirectory "praxis-agent-host", "both.json"))
              Assert.equal [ ProtectedCompletionPolicy.UntrustedAuthorityCode ] (codes (completeWith g (accepting seen) { arguments g "tok-1" with AuthorityPath = Some ownOnly }))
              Assert.equal [ ProtectedCompletionPolicy.UntrustedAuthorityCode ] (codes (completeWith g (accepting seen) { arguments g "tok-1" with AuthorityPath = Some both }))
              // ... and adding the caller's key to the pin in a later commit is a policy change.
              CliHarness.write g.Root "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true" [ g.Key.Trusted.KeyId; own.Trusted.KeyId ])
              CliHarness.commitAll g.Root "agent adds its own key"
              Assert.equal [ ProtectedCompletionPolicy.PolicyChangedCode ] (codes (completeWith g (accepting seen) { arguments g "tok-1" with AuthorityPath = Some both }))
              Assert.equal 0 seen.Count
              ignore held)

          t "REL-GATE-004: under required attestation an authority inside the worktree or the Git directory is untrusted" (fun () ->
              let f = fixture [ "true" ] true
              CliHarness.write f.Root "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true" [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll f.Root "policy"
              let inTree = authorityTrusting f [ f.Key.Trusted ] (Path.Combine(f.Root, "host", "authority.json"))
              CliHarness.commitAll f.Root "an authority in the tree"
              claim f "agent-a" "tok-1" |> ignore
              receipts (verify f "tok-1") |> ignore
              let seen = ResizeArray()
              Assert.equal [ ProtectedCompletionPolicy.UntrustedAuthorityCode ] (codes (completeWith f (accepting seen) { arguments f "tok-1" with AuthorityPath = Some inTree }))
              Assert.equal [ ProtectedCompletionPolicy.UntrustedAuthorityCode ] (codes (completeWith f (accepting seen) { arguments f "tok-1" with AuthorityPath = Some "host/authority.json" }))
              let gitDir = CliHarness.git f.Root [ "rev-parse"; "--path-format=absolute"; "--git-common-dir" ]
              let inGit = authorityTrusting f [ f.Key.Trusted ] (Path.Combine(gitDir, "praxis", "authority.json"))
              Assert.equal [ ProtectedCompletionPolicy.UntrustedAuthorityCode ] (codes (completeWith f (accepting seen) { arguments f "tok-1" with AuthorityPath = Some inGit }))
              // A symbolic link outside the repository that points into it is followed.
              let link = Path.Combine(CliHarness.temporaryDirectory "praxis-agent-host", "authority.json")
              File.CreateSymbolicLink(link, inTree) |> ignore
              Assert.equal [ ProtectedCompletionPolicy.UntrustedAuthorityCode ] (codes (completeWith f (accepting seen) { arguments f "tok-1" with AuthorityPath = Some link }))
              let linkedDirectory = Path.Combine(CliHarness.temporaryDirectory "praxis-agent-host", "linked")
              Directory.CreateSymbolicLink(linkedDirectory, Path.Combine(f.Root, "host")) |> ignore
              Assert.equal [ ProtectedCompletionPolicy.UntrustedAuthorityCode ] (codes (completeWith f (accepting seen) { arguments f "tok-1" with AuthorityPath = Some(Path.Combine(linkedDirectory, "authority.json")) }))
              Assert.equal 0 seen.Count
              // The host-held authority completes, and the completion names the receipt keyid.
              let report = complete f (accepting seen) "tok-1"

              match report.Result.Outcome with
              | CompletionOutcome.Completed(_, completion) ->
                  Assert.equal CompletionAttestation.Attested completion.Attestation
                  Assert.equal [ f.Key.Trusted.KeyId ] completion.VerifierKeyIds
              | other -> failwith $"expected the host-held authority to complete, got {other}"

              let output, _ = VerificationJson.completion report
              Assert.equal f.Key.Trusted.KeyId (text (first (field (CliHarness.json output) "verifierKeyIds"))))

          t "REL-GATE-004: the completion record and event name the receipt keyid" (fun () ->
              let f = fixture [ "true" ] true
              CliHarness.write f.Root "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true" [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll f.Root "policy"
              claim f "agent-a" "tok-1" |> ignore
              receipts (verify f "tok-1") |> ignore
              complete f (accepting (ResizeArray())) "tok-1" |> ignore

              let stored =
                  Directory.GetFiles(FileWorkClaimStore.itemDirectory f.Root item, "*.json", SearchOption.AllDirectories)
                  |> Array.choose (fun path ->
                      match JsonNode.Parse(File.ReadAllText path) with
                      | :? JsonObject as record when not (isNull (field record "completion")) -> Some(text (first (field (field record "completion") "verifierKeyIds")))
                      | _ -> None)
                  |> Array.distinct
                  |> Array.toList

              let tracked =
                  File.ReadAllLines(Path.Combine(f.Root, ".ros", "events", "events.jsonl"))
                  |> Array.map JsonNode.Parse
                  |> Array.filter (fun event -> not (isNull (field event "type")) && text (field event "type") = "work.completion.verified")
                  |> Array.map (fun event -> text (first (field event "verifierKeyIds")))
                  |> Array.toList

              Assert.equal [ f.Key.Trusted.KeyId ] stored
              Assert.equal [ f.Key.Trusted.KeyId ] tracked)

          t "REL-GATE-004: an unattested completion records no verifier keyid, and an older record reads as none" (fun () ->
              let f = fixture [ "true" ] false
              let held = claim f "agent-a" "tok-1"
              forgeValidation f held |> ignore

              match (complete f (accepting (ResizeArray())) "tok-1").Result.Outcome with
              | CompletionOutcome.Completed(_, completion) -> Assert.equal [] completion.VerifierKeyIds
              | other -> failwith $"expected the unattested completion of a policy-free repository, got {other}"

              let record =
                  { WorkItemId = item; Revision = 1L; Generation = 1L; Status = ClaimStatus.Completed; Holder = None; PreviousClaimant = None; PendingPacketDigest = None
                    ConsumedPacketDigests = []; UpdatedAt = DateTimeOffset.UtcNow
                    Completion =
                      Some
                          { PacketDigest = key 'a'; ReceiptDigest = key 'b'; EvidenceDigests = []; Commit = String('c', 40); VerificationRevision = String('c', 40)
                            RequirementIds = []; TelemetryExecutions = []; VerifierName = "dokimos"; VerifierVersion = "0.7.0"; Attestation = CompletionAttestation.Attested
                            VerifierKeyIds = [ key 'd' ]; CompletedAt = DateTimeOffset.UtcNow } }

              let node = WorkClaimJson.claimNode record
              Assert.equal (Ok [ key 'd' ]) (WorkClaimJson.parseClaim (node.ToJsonString()) |> Result.map (fun claim -> claim.Completion.Value.VerifierKeyIds))
              (node["completion"].AsObject()).Remove "verifierKeyIds" |> ignore
              Assert.equal (Ok []) (WorkClaimJson.parseClaim (node.ToJsonString()) |> Result.map (fun claim -> claim.Completion.Value.VerifierKeyIds)))

          t "REL-GATE-004: a malformed protected base or trust pin fails closed" (fun () ->
              // Malformed ros.json on the protected branch.
              let f = fixture [ "true" ] true
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"requireAttestedEvidence\":\"yes\"}}"
              CliHarness.commitAll f.Root "malformed policy on main"
              let a, _ = shared f
              claimIn a "agent-a" "tok-1" |> ignore
              CliHarness.write a "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true" [ f.Key.Trusted.KeyId ])
              CliHarness.commitAll a "agent repairs policy in its branch"
              receipts (verifyIn f a ClaimStoreChoice.Local "tok-1") |> ignore
              let seen = ResizeArray()
              Assert.equal [ ProtectedCompletionPolicy.PolicyUnavailableCode ] (codes (completeIn a f (accepting seen) (arguments f "tok-1")))
              // A branch with no history in common with the protected branch.
              CliHarness.git a [ "checkout"; "-q"; "--orphan"; "agent/orphan" ] |> ignore
              CliHarness.commitAll a "orphan"
              Assert.equal [ ProtectedCompletionPolicy.PolicyUnavailableCode ] (codes (completeIn a f (accepting seen) (arguments f "tok-1")))
              Assert.equal 0 seen.Count
              // A malformed trust pin in the worktree fails closed as before.
              for pin in [ "\"sha256:abc\""; "1"; "\"SHA256:" + String('A', 64) + "\"" ] do
                  let g = fixture [ "true" ] true
                  CliHarness.write g.Root "ros.json" $"{{\"workProtocol\":{{\"requireAttestedEvidence\":true,\"trustedVerifierKeyIds\":[{pin}]}}}}"
                  CliHarness.commitAll g.Root "policy"
                  claim g "agent-a" "tok-1" |> ignore
                  Assert.equal [ ProtectedCompletionPolicy.AttestationRequiredCode ] (codes (complete g (accepting seen) "tok-1"))

              Assert.equal 0 seen.Count
              // Outside any Git repository there is no protected policy to read.
              match ProtectedCompletionPolicy.decide (Ok(policy [ "attested" ])) (FileWorkClaims.protectedPolicySource (CliHarness.temporaryDirectory "praxis-no-git")) with
              | Error finding -> Assert.equal ProtectedCompletionPolicy.PolicyUnavailableCode finding.Code
              | Ok _ -> failwith "expected an unavailable protected policy to fail closed")

          t "REL-GATE-004: plain work complete cannot bypass a requireVerifiedCompletion the worktree dropped" (fun () ->
              let f = fixture [ "true" ] false
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"requireVerifiedCompletion\":true}}"
              CliHarness.commitAll f.Root "policy"
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{}}"
              CliHarness.commitAll f.Root "agent drops verified completion"

              match FileWorkClaims.plainCompletion f.Root ClaimStoreChoice.Local [ item ] with
              | Ok(PlainCompletion.Refused finding) -> Assert.equal ProtectedCompletionPolicy.PolicyChangedCode finding.Code
              | other -> failwith $"expected the dropped requirement to be refused, got {other}"

              // Outside Git the worktree policy alone decides, as before.
              let bare = CliHarness.temporaryDirectory "praxis-no-git"
              Assert.equal (Ok PlainCompletion.Permitted) (FileWorkClaims.plainCompletion bare ClaimStoreChoice.Local [ item ]))

          t "REL-GATE-004: weakenings is a pure function of the protected and candidate policy" (fun () ->
              let all = [ "verified"; "attested"; "optout" ]
              let subsets = [ for v in [ false; true ] do for a in [ false; true ] do for o in [ false; true ] do yield all |> List.filter (fun m -> (m = "verified" && v) || (m = "attested" && a) || (m = "optout" && o)) ]

              for p in subsets do
                  for c in subsets do
                      let weaker = ProtectedCompletionPolicy.weakenings (policy p) (policy c)
                      let expected =
                          (List.contains "verified" p && not (List.contains "verified" c))
                          || (List.contains "attested" p && not (List.contains "attested" c))
                          || (not (List.contains "optout" p) && List.contains "optout" c)
                          // Required attestation with no pin trusts no key; dropping the requirement drops that constraint.
                          || ((ProtectedCompletionPolicy.requirement (policy p)).IsSome && (ProtectedCompletionPolicy.requirement (policy c)).IsNone)
                      Assert.equal expected (not weaker.IsEmpty)
                      // Identical policy is never weaker.
                      Assert.equal [] (ProtectedCompletionPolicy.weakenings (policy p) (policy p))

              // Trust pins: a subset is stronger, any added key is weaker.
              let pinned keys = { policy [ "attested" ] with TrustedVerifierKeyIds = Some keys }
              Assert.equal [] (ProtectedCompletionPolicy.weakenings (pinned [ key 'a'; key 'b' ]) (pinned [ key 'a' ]))
              Assert.equal 1 (ProtectedCompletionPolicy.weakenings (pinned [ key 'a' ]) (pinned [ key 'a'; key 'b' ])).Length
              Assert.equal 1 (ProtectedCompletionPolicy.weakenings (policy [ "attested" ]) (pinned [ key 'a' ])).Length
              Assert.equal [] (ProtectedCompletionPolicy.weakenings (policy []) (pinned [ key 'a' ]))
              Assert.equal 1 (ProtectedCompletionPolicy.weakenings { policy [] with TrustedVerifierKeyIds = Some [ key 'a' ] } (policy [])).Length
              // decide: the worktree's policy when no weaker; a locator move is a change.
              let protectedBase = ProtectedPolicySource.Base(String('b', 40), Ok(pinned [ key 'a' ]))
              Assert.equal (Ok(pinned [ key 'a' ])) (ProtectedCompletionPolicy.decide (Ok(pinned [ key 'a' ])) protectedBase)
              let moved = { pinned [ key 'a' ] with Upstream = "origin", "agent" }
              Assert.equal (Error ProtectedCompletionPolicy.PolicyChangedCode) (ProtectedCompletionPolicy.decide (Ok moved) protectedBase |> Result.mapError _.Code)
              Assert.equal (Error ProtectedCompletionPolicy.AttestationRequiredCode) (ProtectedCompletionPolicy.decide (Error "malformed") protectedBase |> Result.mapError _.Code)
              Assert.equal (Error ProtectedCompletionPolicy.PolicyUnavailableCode) (ProtectedCompletionPolicy.decide (Ok(policy [])) (ProtectedPolicySource.Base(String('b', 40), Error "malformed")) |> Result.mapError _.Code)
              Assert.equal (Error ProtectedCompletionPolicy.PolicyUnavailableCode) (ProtectedCompletionPolicy.decide (Ok(policy [])) (ProtectedPolicySource.Unavailable "no git") |> Result.mapError _.Code)
              // History: every committed version binds.
              let history = ProtectedPolicySource.History [ String('2', 40), policy []; String('1', 40), policy [ "attested" ] ]
              Assert.equal (Error ProtectedCompletionPolicy.PolicyChangedCode) (ProtectedCompletionPolicy.decide (Ok(policy [])) history |> Result.mapError _.Code)
              Assert.equal (Ok(policy [])) (ProtectedCompletionPolicy.decide (Ok(policy [])) (ProtectedPolicySource.History []))
              // trust: keyless, unpinned, outside the pin, inside the repository, and the genuine case.
              let code result = result |> Result.mapError _.Code
              Assert.equal (Error ProtectedCompletionPolicy.AttestationRequiredCode) (code (ProtectedCompletionPolicy.trust (pinned [ key 'a' ]) [] None))
              Assert.equal (Error ProtectedCompletionPolicy.UntrustedAuthorityCode) (code (ProtectedCompletionPolicy.trust (policy [ "attested" ]) [ key 'a' ] None))
              Assert.equal (Error ProtectedCompletionPolicy.UntrustedAuthorityCode) (code (ProtectedCompletionPolicy.trust (pinned [ key 'a' ]) [ key 'a'; key 'b' ] None))
              Assert.equal (Error ProtectedCompletionPolicy.UntrustedAuthorityCode) (code (ProtectedCompletionPolicy.trust (pinned [ key 'a' ]) [ key 'a' ] (Some "/repo")))
              Assert.equal (Ok()) (code (ProtectedCompletionPolicy.trust (pinned [ key 'a'; key 'b' ]) [ key 'a' ] None))
              // Not required: unattested is allowed; a present pin still binds an attested authority.
              Assert.equal (Ok()) (code (ProtectedCompletionPolicy.trust (policy []) [] None))
              Assert.equal (Ok()) (code (ProtectedCompletionPolicy.trust (policy []) [ key 'a' ] (Some "/repo")))
              Assert.equal (Error ProtectedCompletionPolicy.UntrustedAuthorityCode) (code (ProtectedCompletionPolicy.trust { policy [] with TrustedVerifierKeyIds = Some [ key 'a' ] } [ key 'b' ] None)))

          t "REL-GATE-004 end to end through the real Dokimos: a pinned host verifier completes, PROBE-3 and PROBE-4 are refused" (fun () ->
              match attestedDokimos () with
              | None -> DokimosTestRuntime.skip "attested completion"
              | Some dll ->
                  let dokimos = DokimosCompletionVerifier.create (Ok dll) (TimeSpan.FromSeconds 120.0)
                  // Genuine: the host verifier's key pinned in protected policy.
                  let f = fixture [ "true" ] true
                  CliHarness.write f.Root "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true" [ f.Key.Trusted.KeyId ])
                  CliHarness.commitAll f.Root "policy"
                  Assert.equal 0 (CliHarness.rosWith f.Root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-1" ]).Exit
                  receipts (verify f "tok-1") |> ignore
                  let verifiedCommit = head f
                  let report = complete f dokimos "tok-1"

                  match report.Result.Outcome with
                  | CompletionOutcome.Completed(_, completion) ->
                      Assert.isTrue (completion.VerifierVersion.StartsWith "0.7") completion.VerifierVersion
                      Assert.equal [ f.Key.Trusted.KeyId ] completion.VerifierKeyIds
                  | other -> failwith $"expected the real Dokimos to verify the pinned attested completion, got {other}"

                  Assert.equal verifiedCommit (CliHarness.git f.Root [ "rev-parse"; "HEAD^" ])
                  Assert.equal "" (status f)
                  // PROBE-3 through the CLI: the agent drops attestation in a commit and forges store evidence.
                  let g = fixture [ "true" ] false
                  CliHarness.write g.Root "ros.json" "{\"workProtocol\":{\"requireAttestedEvidence\":true}}"
                  CliHarness.commitAll g.Root "policy"
                  Assert.equal 0 (CliHarness.rosWith g.Root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-1" ]).Exit
                  CliHarness.write g.Root "ros.json" "{\"workProtocol\":{}}"
                  CliHarness.commitAll g.Root "agent drops attestation"
                  let validate = [ "work"; "validate-run"; "--id"; item; "--token"; "tok-1"; "--generation"; "1"; "--validation"; "unit-tests"; "--evidence-id"; "EV-TESTS"; "--"; "true" ]
                  Assert.equal 0 (CliHarness.rosWith g.Root identity validate).Exit
                  let environment = [ "PRAXIS_DOKIMOS_EXECUTABLE", dll; FileDerivedCompletion.AuthorityVariable, g.Authority; FileDerivedCompletion.ContractVariable, g.Contract ]
                  let probe3 = CliHarness.rosWith g.Root (environment @ identity) [ "work"; "complete-verified"; "--id"; item; "--token"; "tok-1" ]
                  Assert.equal 3 probe3.Exit
                  Assert.isTrue (probe3.Out.Contains ProtectedCompletionPolicy.PolicyChangedCode) probe3.Out
                  // PROBE-4 through the CLI: an own-key authority under an unpinned attested policy.
                  let h = fixture [ "true" ] true
                  CliHarness.write h.Root "ros.json" "{\"workProtocol\":{\"requireAttestedEvidence\":true}}"
                  CliHarness.commitAll h.Root "policy"
                  Assert.equal 0 (CliHarness.rosWith h.Root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-1" ]).Exit
                  receipts (verify h "tok-1") |> ignore
                  let environment = [ "PRAXIS_DOKIMOS_EXECUTABLE", dll; FileDerivedCompletion.AuthorityVariable, h.Authority; FileDerivedCompletion.ContractVariable, h.Contract ]
                  let probe4 = CliHarness.rosWith h.Root (environment @ identity) [ "work"; "complete-verified"; "--id"; item; "--token"; "tok-1" ]
                  Assert.equal 3 probe4.Exit
                  Assert.isTrue (probe4.Out.Contains ProtectedCompletionPolicy.UntrustedAuthorityCode) probe4.Out) ]

    // ---- REL-TOKEN-003: `praxis work finish` ----------------------------

    let private verifyPorts (f: Fixture) =
        { FileWorkVerification.ports f.Root ClaimStoreChoice.Local None (TimeSpan.FromMinutes 5.0) with Request = fun request -> Ok(TrustedVerifierEngine.run f.Key f.Policy f.ContractDigest request) }

    let private finishWith (f: Fixture) verifier (args: CompletionArguments) =
        FileWorkFinish.run f.Root ClaimStoreChoice.Local None (verifyPorts f) (Some verifier) args

    let private finish (f: Fixture) verifier token = finishWith f verifier (arguments f token)

    /// The refusal codes of whichever step ended `finish`.
    let private finishCodes (outcome: FinishOutcome<FinishCompletion>) =
        match outcome with
        | FinishOutcome.Stopped(WorkVerifyOutcome.Refused finding) -> [ finding.Code ]
        | FinishOutcome.Finished(_, Ok report)
        | FinishOutcome.Replayed(Ok report) -> codes report
        | other -> failwith $"expected a refusal, got {other}"

    let private completed (f: Fixture) =
        match (FileWorkClaims.store f.Root ClaimStoreChoice.Local).Read item with
        | Ok(Some claim) -> claim.Status = ClaimStatus.Completed
        | _ -> false

    /// The types of the tracked events and the statuses of the stored claim
    /// records, in order: what `finish` must leave exactly as verify + complete-verified do.
    let private records (f: Fixture) =
        let tracked =
            File.ReadAllLines(Path.Combine(f.Root, ".ros", "events", "events.jsonl"))
            |> Array.map JsonNode.Parse
            |> Array.choose (fun event -> event["type"] |> Option.ofObj |> Option.map text)
            |> Array.toList

        let stored =
            Directory.GetFiles(FileWorkClaimStore.itemDirectory f.Root item, "*.json", SearchOption.AllDirectories)
            |> Array.choose (fun path ->
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as record when not (isNull (field record "status")) && not (isNull (field record "revision")) ->
                    Some(record["revision"].GetValue<int64>(), text (field record "status"))
                | _ -> None)
            |> Array.sortBy fst
            |> Array.map snd
            |> Array.toList

        tracked, stored

    /// A failing validation with long output: the failure sits mid-way, the runner's summary at the end.
    let private failingCommand =
        [ "sh"; "-c"; "seq 1 150; echo '  Failed Broken.Tests.adds [3 ms]'; echo '  Error Message:'; echo '   Assert.Equal() Failure: expected 4, actual 5'; seq 1000 1200; echo 'Failed!  - Failed: 1, Passed: 41, Skipped: 0, Total: 42'; exit 1" ]

    let private serveVerifier (f: Fixture) =
        let socket = Path.Combine(CliHarness.temporaryDirectory "praxis-verifier-socket", "v.sock")
        let ready = new ManualResetEventSlim(false)
        Thread((fun () -> VerifierSocket.serve socket f.Key f.Policy f.ContractDigest (fun _ -> ready.Set()) |> ignore), IsBackground = true).Start()
        Assert.isTrue (ready.Wait(TimeSpan.FromSeconds 30.0)) "the verifier starts"
        socket

    let finishTests =
        [ t "REL-TOKEN-003: finish on a clean passing HEAD leaves the same records as verify then complete-verified, and replays" (fun () ->
              let a = fixture [ "true" ] true
              let b = fixture [ "true" ] true

              for f in [ a; b ] do
                  CliHarness.write f.Root "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true" [ f.Key.Trusted.KeyId ])
                  CliHarness.commitAll f.Root "policy"
                  claim f "agent-a" "tok-1" |> ignore

              // a: the two separate commands.
              receipts (verify a "tok-1") |> ignore
              let separate = complete a (accepting (ResizeArray())) "tok-1"
              // b: one finish.
              let verifiedCommit = head b
              let outcome = finish b (accepting (ResizeArray())) "tok-1"

              let report =
                  match outcome with
                  | FinishOutcome.Finished(WorkVerifyOutcome.Verified(_, stored, _), Ok report) ->
                      Assert.isTrue (WorkVerification.allPassed stored) "every receipt passed"
                      report
                  | other -> failwith $"expected verify then completion, got {other}"

              match separate.Result.Outcome, report.Result.Outcome with
              | CompletionOutcome.Completed(_, x), CompletionOutcome.Completed(_, y) ->
                  Assert.equal x.Attestation y.Attestation
                  Assert.equal CompletionAttestation.Attested y.Attestation
                  Assert.equal x.VerifierKeyIds.Length y.VerifierKeyIds.Length
                  Assert.equal x.EvidenceDigests.Length y.EvidenceDigests.Length
              | other -> failwith $"expected both to complete, got {other}"

              Assert.equal (records a) (records b)
              Assert.isTrue (fst (records b) |> List.contains "work.completion.verified") $"{records b}"
              Assert.isTrue (snd (records b) |> List.contains "completed") $"{records b}"
              Assert.equal (Some verifiedCommit) report.Result.VerifiedCommit
              Assert.equal verifiedCommit (CliHarness.git b.Root [ "rev-parse"; "HEAD^" ])
              Assert.equal "" (status b)
              // Each step keeps its timing.
              for step in [ "claim"; "admission"; "receipts"; "dokimos"; "metadataCommit" ] do
                  Assert.isTrue (report.Timings |> List.exists (fst >> (=) step)) $"timing {step}"

              let line, exit = WorkFinishOutput.text item WorkFinish.ExcerptLines outcome
              Assert.equal 0 exit
              Assert.equal 1 (line.Split('\n').Length)
              Assert.isTrue (line.StartsWith $"{item} verified (attested) commit {verifiedCommit.Substring(0, 12)} metadata ") line
              // Replay: no new verification, no new metadata commit, same records.
              let metadata = head b
              let before = records b
              let replay = finish b (accepting (ResizeArray())) "tok-1"

              match replay with
              | FinishOutcome.Replayed(Ok again) ->
                  match again.Result.Outcome with
                  | CompletionOutcome.Replayed _ -> ()
                  | other -> failwith $"expected a replay, got {other}"
              | other -> failwith $"expected finish to replay, got {other}"

              Assert.equal 0 (snd (WorkFinishOutput.text item WorkFinish.ExcerptLines replay))
              Assert.equal metadata (head b)
              Assert.equal before (records b))

          t "REL-TOKEN-003: failing tests stop finish at verify with a bounded excerpt; nothing is completed" (fun () ->
              let f = fixture failingCommand true
              claim f "agent-a" "tok-1" |> ignore
              let seen = ResizeArray()
              let outcome = finish f (accepting seen) "tok-1"

              match outcome with
              | FinishOutcome.Stopped(WorkVerifyOutcome.Verified(_, [ receipt ], _)) -> Assert.equal CheckStatus.Failed receipt.Payload.Status
              | other -> failwith $"expected a not-passed verification, got {other}"

              Assert.equal 0 seen.Count
              Assert.isTrue (not (completed f)) "the claim is not completed"
              let shown, exit = WorkFinishOutput.text item WorkFinish.ExcerptLines outcome
              Assert.equal 3 exit
              let lines = shown.Split('\n')
              Assert.isTrue (lines[0].StartsWith $"{item} not-passed at verify") lines[0]
              Assert.isTrue (lines[0].Contains "EV-TESTS failed (exit 1)") lines[0]
              Assert.isTrue (lines.Length <= 2 + WorkFinish.ExcerptLines) $"{lines.Length} lines"
              Assert.isTrue (shown.Contains "Failed Broken.Tests.adds") shown
              Assert.isTrue (shown.Contains "Assert.Equal() Failure: expected 4, actual 5") shown
              Assert.isTrue (shown.Contains "Failed!  - Failed: 1, Passed: 41") shown
              Assert.isTrue (not (shown.Contains "\n1\n")) "the leading noise is not shown"
              // The excerpt names the full output stored beside the receipt.
              let excerpt = (WorkFinish.excerpts WorkFinish.ExcerptLines (match outcome with FinishOutcome.Stopped v -> v | _ -> failwith "stopped")).Head
              Assert.isTrue (File.Exists excerpt.OutputPath) excerpt.OutputPath
              Assert.equal 355 excerpt.TotalLines
              let json, jsonExit = WorkFinishOutput.json item WorkFinish.ExcerptLines outcome
              Assert.equal 3 jsonExit
              let document = CliHarness.json json
              Assert.equal "not-passed" (text (field document "outcome"))
              Assert.equal "verify" (text (field document "step"))
              Assert.isTrue (isNull (field document "completion")) json
              let shownLines = (field (first (field document "excerpts")) "lines").AsArray().Count
              Assert.equal WorkFinish.ExcerptLines shownLines)

          t "REL-TOKEN-003: finish refuses a dirty worktree and a stale token at verify, before anything runs" (fun () ->
              let f = fixture [ "sh"; "-c"; "touch ran-validation" ] true
              claim f "agent-a" "tok-1" |> ignore
              let seen = ResizeArray()
              CliHarness.write f.Root "notes.txt" "untracked\n"
              Assert.equal [ ValidationEvidence.DirtyWorktreeCode ] (finishCodes (finish f (accepting seen) "tok-1"))
              File.Delete(Path.Combine(f.Root, "notes.txt"))
              Assert.equal [ ClaimConflict.code ClaimConflict.StaleToken ] (finishCodes (finish f (accepting seen) "tok-other"))
              Assert.equal 0 seen.Count
              Assert.isTrue (not (completed f)) "the claim is not completed")

          t "REL-TOKEN-003 PROBE-3: finish refuses a committed ros.json that drops requireAttestedEvidence" (fun () ->
              let f = fixture [ "true" ] false
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"requireAttestedEvidence\":true}}"
              CliHarness.commitAll f.Root "policy"
              let held = claim f "agent-a" "tok-1"
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{}}"
              CliHarness.commitAll f.Root "agent drops attestation"
              forgeValidation f held |> ignore
              let seen = ResizeArray()
              let outcome = finish f (accepting seen) "tok-1"
              Assert.equal [ ProtectedCompletionPolicy.PolicyChangedCode ] (finishCodes outcome)
              Assert.equal 3 (snd (WorkFinishOutput.text item WorkFinish.ExcerptLines outcome))
              Assert.isTrue ((fst (WorkFinishOutput.text item WorkFinish.ExcerptLines outcome)).Contains ProtectedCompletionPolicy.PolicyChangedCode) "the code is shown"
              Assert.equal 0 seen.Count
              Assert.isTrue (not (completed f)) "the claim is not completed")

          t "REL-TOKEN-003 PROBE-4: finish refuses an authority the protected policy does not pin" (fun () ->
              let f = fixture [ "true" ] true
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"requireAttestedEvidence\":true}}"
              CliHarness.commitAll f.Root "policy"
              claim f "agent-a" "tok-1" |> ignore
              let own = VerifierKey.generate ()
              let ownAuthority = authorityTrusting f [ own.Trusted ] (Path.Combine(CliHarness.temporaryDirectory "praxis-agent-host", "own-key.json"))
              let seen = ResizeArray()
              Assert.equal [ ProtectedCompletionPolicy.UntrustedAuthorityCode ] (finishCodes (finish f (accepting seen) "tok-1"))
              Assert.equal [ ProtectedCompletionPolicy.UntrustedAuthorityCode ] (finishCodes (finishWith f (accepting seen) { arguments f "tok-1" with AuthorityPath = Some ownAuthority }))
              Assert.equal 0 seen.Count
              Assert.isTrue (not (completed f)) "the claim is not completed")

          t "REL-TOKEN-003: finish refuses an unattested authority under required attestation" (fun () ->
              let f = fixture [ "true" ] false
              CliHarness.write f.Root "ros.json" "{\"workProtocol\":{\"requireAttestedEvidence\":true}}"
              CliHarness.commitAll f.Root "policy"
              claim f "agent-a" "tok-1" |> ignore
              let seen = ResizeArray()
              Assert.equal [ FileDerivedCompletion.AttestationRequiredCode ] (finishCodes (finish f (accepting seen) "tok-1"))
              // REL-GATE-003: requireVerifiedCompletion alone implies it too.
              let g = fixture [ "true" ] false
              CliHarness.write g.Root "ros.json" "{\"workProtocol\":{\"requireVerifiedCompletion\":true}}"
              CliHarness.commitAll g.Root "policy"
              claim g "agent-a" "tok-1" |> ignore
              Assert.equal [ FileDerivedCompletion.AttestationRequiredCode ] (finishCodes (finish g (accepting seen) "tok-1"))
              Assert.equal 0 seen.Count)

          t "REL-TOKEN-003: the excerpt is a pure, bounded window" (fun () ->
              Assert.equal (0, 0, []) (WorkFinish.excerpt 40 "")
              Assert.equal (1, 2, [ "a"; "b" ]) (WorkFinish.excerpt 40 "a\r\nb\n\n")
              let noisy = [ for i in 1..100 -> $"line {i}" ] |> String.concat "\n"
              Assert.equal (91, 100, [ for i in 91..100 -> $"line {i}" ]) (WorkFinish.excerpt 10 noisy)
              let failing = ([ for i in 1..50 -> $"line {i}" ] @ [ "  Failed X"; "detail" ] @ [ for i in 1..50 -> $"tail {i}" ] @ [ "Failed!  - Failed: 1" ]) |> String.concat "\n"
              let first, total, lines = WorkFinish.excerpt 5 failing
              Assert.equal 49 first
              Assert.equal 103 total
              Assert.equal [ "line 49"; "line 50"; "  Failed X"; "detail"; "Failed!  - Failed: 1" ] lines
              let _, _, wide = WorkFinish.excerpt 5 (String('x', 1000))
              Assert.equal WorkFinish.LineWidth wide.Head.Length)

          t "REL-TOKEN-003: work finish through the CLI with a host verifier: argument errors, failing tests, and the end to end success line" (fun () ->
              let f = fixture failingCommand true
              let socket = serveVerifier f
              Assert.equal 0 (CliHarness.rosWith f.Root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-1" ]).Exit
              let verifierEnv = [ "PRAXIS_VERIFIER_SOCKET", socket ]
              // No authority: invalid arguments before verification runs.
              let missing = CliHarness.rosWith f.Root (verifierEnv @ identity) [ "work"; "finish"; "--id"; item; "--token"; "tok-1" ]
              Assert.equal 2 missing.Exit
              Assert.isTrue (not (Directory.Exists(FileWorkVerification.directory f.Root item))) "nothing was verified"
              let environment = verifierEnv @ [ FileDerivedCompletion.AuthorityVariable, f.Authority; FileDerivedCompletion.ContractVariable, f.Contract ] @ identity
              let failing = CliHarness.rosWith f.Root environment [ "work"; "finish"; "--id"; item; "--token"; "tok-1" ]
              Assert.equal 3 failing.Exit
              Assert.isTrue (failing.Out.Contains "not-passed at verify") failing.Out
              Assert.isTrue (failing.Out.Contains "Assert.Equal() Failure: expected 4, actual 5") failing.Out
              Assert.isTrue (failing.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= 2 + WorkFinish.ExcerptLines) failing.Out
              let json = CliHarness.rosWith f.Root environment [ "work"; "finish"; "--id"; item; "--token"; "tok-1"; "--json"; "--excerpt-lines"; "5" ]
              Assert.equal 3 json.Exit
              let shortExcerpt = (field (first (field (CliHarness.json json.Out) "excerpts")) "lines").AsArray().Count
              Assert.equal 5 shortExcerpt

              match attestedDokimos () with
              | None -> DokimosTestRuntime.skip "attested completion"
              | Some dll ->
                  let g = fixture [ "true" ] true
                  CliHarness.write g.Root "ros.json" (pinnedPolicy "\"requireAttestedEvidence\":true" [ g.Key.Trusted.KeyId ])
                  CliHarness.commitAll g.Root "policy"
                  let socket = serveVerifier g
                  Assert.equal 0 (CliHarness.rosWith g.Root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-1" ]).Exit
                  let verifiedCommit = head g
                  let environment = [ "PRAXIS_VERIFIER_SOCKET", socket; "PRAXIS_DOKIMOS_EXECUTABLE", dll; FileDerivedCompletion.AuthorityVariable, g.Authority; FileDerivedCompletion.ContractVariable, g.Contract ] @ identity
                  let run = CliHarness.rosWith g.Root environment [ "work"; "finish"; "--id"; item; "--token"; "tok-1" ]
                  Assert.equal 0 run.Exit
                  Assert.equal $"{item} verified (attested) commit {verifiedCommit.Substring(0, 12)} metadata {(head g).Substring(0, 12)}" (run.Out.Trim())
                  Assert.equal verifiedCommit (CliHarness.git g.Root [ "rev-parse"; "HEAD^" ])
                  Assert.equal "" (status g)
                  let recordsBefore, metadata = records g, head g
                  let replay = CliHarness.rosWith g.Root environment [ "work"; "finish"; "--id"; item; "--token"; "tok-1"; "--json" ]
                  Assert.equal 0 replay.Exit
                  Assert.equal "replayed" (text (field (CliHarness.json replay.Out) "outcome"))
                  // EP-A3-R3: the CLI replay adds no completion record and no metadata commit.
                  Assert.equal recordsBefore (records g)
                  Assert.equal metadata (head g)
                  Assert.equal 1 (fst (records g) |> List.filter ((=) "work.completion.verified") |> List.length)
                  // PROBE-4 through the CLI: an unpinned attested policy.
                  let h = fixture [ "true" ] true
                  CliHarness.write h.Root "ros.json" "{\"workProtocol\":{\"requireAttestedEvidence\":true}}"
                  CliHarness.commitAll h.Root "policy"
                  let socket = serveVerifier h
                  Assert.equal 0 (CliHarness.rosWith h.Root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-1" ]).Exit
                  let environment = [ "PRAXIS_VERIFIER_SOCKET", socket; "PRAXIS_DOKIMOS_EXECUTABLE", dll; FileDerivedCompletion.AuthorityVariable, h.Authority; FileDerivedCompletion.ContractVariable, h.Contract ] @ identity
                  let probe4 = CliHarness.rosWith h.Root environment [ "work"; "finish"; "--id"; item; "--token"; "tok-1" ]
                  Assert.equal 3 probe4.Exit
                  Assert.isTrue (probe4.Out.Contains ProtectedCompletionPolicy.UntrustedAuthorityCode) probe4.Out) ]

    // ---- EP-A3-R3: idempotence contract, end to end through the CLI -----

    /// Every file under `directory` that `keep` accepts, as (relative path, content digest), sorted.
    let private filesUnder (directory: string) (keep: string -> bool) =
        if Directory.Exists directory then
            Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            |> Array.filter keep
            |> Array.map (fun path -> Path.GetRelativePath(directory, path), hex (File.ReadAllText path))
            |> Array.sort
            |> Array.toList
        else
            []

    /// The repository's completion state: HEAD, the worktree, tracked `.ros/`
    /// state, and the out-of-tree claim, validation and receipt stores. The
    /// admission cache and lock files are bookkeeping, not state. The
    /// transient event log is returned apart, because a refusal is audited.
    let private completionState (f: Fixture) =
        let store = TransientStore.directory f.Root
        let stores = [ TransientStore.claims f.Root; TransientStore.validations f.Root; TransientStore.receipts f.Root ]
        let underStores (path: string) = stores |> List.exists (fun s -> path.StartsWith(s + string Path.DirectorySeparatorChar, StringComparison.Ordinal))
        head f, status f, filesUnder (Path.Combine(f.Root, ".ros")) (fun _ -> true), filesUnder store (fun path -> underStores path && not (path.EndsWith ".lock"))

    /// The transient claim-protocol events, as types, in order.
    let private transientEvents (f: Fixture) =
        let path = TransientStore.events f.Root

        if File.Exists path then
            File.ReadAllLines path
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.choose (fun line -> (JsonNode.Parse line)["type"] |> Option.ofObj |> Option.map text)
            |> Array.toList
        else
            []

    /// The distinct `PRX.*` codes a run printed, sorted: what a repeat must reproduce.
    let private printedCodes (run: CliHarness.Run) =
        RegularExpressions.Regex.Matches(run.Out + run.Err, @"\b(?:PRX|DOK)\.[A-Z0-9_.]*[A-Z0-9_]")
        |> Seq.map _.Value
        |> Seq.distinct
        |> Seq.sort
        |> Seq.toList

    /// A Dokimos stand-in that must never run: it leaves a marker and fails.
    let private untouchableDokimos () =
        let directory = CliHarness.temporaryDirectory "praxis-untouchable-dokimos"
        let marker = Path.Combine(directory, "ran")
        let script = Path.Combine(directory, "dokimos")
        File.WriteAllText(script, $"#!/bin/sh\ntouch '{marker}'\nexit 9\n")
        File.SetUnixFileMode(script, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        script, marker

    /// Runs `arguments` twice and asserts the refusal contract: exit 3 both
    /// times, the same non-empty codes, no change to the completion state,
    /// and at most audit events of type `work.completion.rejected` added.
    let private refusesIdempotently (f: Fixture) environment arguments =
        let before = completionState f
        let eventsBefore = transientEvents f
        let first = CliHarness.rosWith f.Root environment arguments
        let afterFirst = completionState f
        let second = CliHarness.rosWith f.Root environment arguments
        Assert.equal 3 first.Exit
        Assert.equal 3 second.Exit
        let codes = printedCodes first
        Assert.isTrue (not codes.IsEmpty) $"the refusal names its codes: {first.Out}{first.Err}"
        Assert.equal codes (printedCodes second)
        Assert.equal before afterFirst
        Assert.equal before (completionState f)
        let added = transientEvents f |> List.skip eventsBefore.Length
        Assert.equal eventsBefore (transientEvents f |> List.truncate eventsBefore.Length)
        Assert.isTrue (added |> List.forall ((=) "work.completion.rejected")) $"only refusals are audited: {added}"
        Assert.isTrue (not (completed f)) "the claim is not completed"
        codes

    let idempotenceTests =
        [ t "EP-A3-R3 cli: complete-verified with missing evidence exits 3, repeats the same codes, and changes no state" (fun () ->
              let f = fixture [ "true" ] false
              Assert.equal 0 (CliHarness.rosWith f.Root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-1" ]).Exit
              let dokimos, marker = untouchableDokimos ()
              let environment = [ "PRAXIS_DOKIMOS_EXECUTABLE", dokimos; FileDerivedCompletion.AuthorityVariable, f.Authority; FileDerivedCompletion.ContractVariable, f.Contract ] @ identity
              let codes = refusesIdempotently f environment [ "work"; "complete-verified"; "--id"; item; "--token"; "tok-1" ]
              Assert.isTrue (codes |> List.forall (fun code -> code.StartsWith "PRX.")) $"{codes}"
              Assert.isTrue (not (File.Exists marker)) "Dokimos is never asked")

          t "EP-A3-R3 cli: complete-verified with evidence stale against HEAD exits 3, repeats the same codes, and changes no state" (fun () ->
              let f = fixture [ "true" ] false
              Assert.equal 0 (CliHarness.rosWith f.Root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-1" ]).Exit
              let validate = [ "work"; "validate-run"; "--id"; item; "--token"; "tok-1"; "--generation"; "1"; "--validation"; "unit-tests"; "--evidence-id"; "EV-TESTS"; "--"; "true" ]
              Assert.equal 0 (CliHarness.rosWith f.Root identity validate).Exit
              // The candidate moves on after the evidence was recorded.
              CliHarness.write f.Root "src/code.txt" "v2\n"
              CliHarness.commitAll f.Root "change after validation"
              let dokimos, marker = untouchableDokimos ()
              let environment = [ "PRAXIS_DOKIMOS_EXECUTABLE", dokimos; FileDerivedCompletion.AuthorityVariable, f.Authority; FileDerivedCompletion.ContractVariable, f.Contract ] @ identity
              refusesIdempotently f environment [ "work"; "complete-verified"; "--id"; item; "--token"; "tok-1" ] |> ignore
              Assert.isTrue (not (File.Exists marker)) "Dokimos is never asked")

          t "EP-A3-R3 cli: work finish refused before verification (stale token) exits 3, repeats the same codes, and changes no state" (fun () ->
              let f = fixture [ "sh"; "-c"; "touch ran-validation" ] true
              let socket = serveVerifier f
              Assert.equal 0 (CliHarness.rosWith f.Root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-1" ]).Exit
              let dokimos, marker = untouchableDokimos ()
              let environment = [ "PRAXIS_VERIFIER_SOCKET", socket; "PRAXIS_DOKIMOS_EXECUTABLE", dokimos; FileDerivedCompletion.AuthorityVariable, f.Authority; FileDerivedCompletion.ContractVariable, f.Contract ] @ identity
              let codes = refusesIdempotently f environment [ "work"; "finish"; "--id"; item; "--token"; "tok-other" ]
              Assert.equal [ ClaimConflict.code ClaimConflict.StaleToken ] codes
              Assert.isTrue (not (File.Exists marker)) "Dokimos is never asked")

          t "EP-A3-R3 cli: work finish with required evidence missing from the policy exits 3, repeats the same codes, and changes no claim or tracked state" (fun () ->
              let f = fixture [ "true" ] true
              let socket = serveVerifier f
              Assert.equal 0 (CliHarness.rosWith f.Root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-1" ]).Exit
              // The host demands evidence the validation policy never produces.
              let demanding = Path.Combine(f.Host, "demanding.json")
              File.WriteAllText(demanding, File.ReadAllText(f.Authority).Replace("\"RequiredEvidenceIds\":[\"EV-TESTS\"]", "\"RequiredEvidenceIds\":[\"EV-TESTS\",\"EV-MISSING\"]"))
              let dokimos, marker = untouchableDokimos ()
              let environment = [ "PRAXIS_VERIFIER_SOCKET", socket; "PRAXIS_DOKIMOS_EXECUTABLE", dokimos; FileDerivedCompletion.AuthorityVariable, demanding; FileDerivedCompletion.ContractVariable, f.Contract ] @ identity
              let arguments = [ "work"; "finish"; "--id"; item; "--token"; "tok-1" ]
              let claimState () = let h, s, ros, stores = completionState f in h, s, ros, stores |> List.filter (fst >> fun path -> not (path.StartsWith "receipts"))
              let before = claimState ()
              let first = CliHarness.rosWith f.Root environment arguments
              let second = CliHarness.rosWith f.Root environment arguments
              Assert.equal 3 first.Exit
              Assert.equal 3 second.Exit
              Assert.isTrue (not (printedCodes first).IsEmpty) $"{first.Out}{first.Err}"
              Assert.equal (printedCodes first) (printedCodes second)
              // finish re-verifies, so receipts may be added; the claim, tracked state and HEAD do not move.
              Assert.equal before (claimState ())
              Assert.isTrue (not (completed f)) "the claim is not completed"
              Assert.isTrue (not (File.Exists marker)) "Dokimos is never asked")

          t "EP-A3-R3 cli: valid evidence completed twice through the real Dokimos replays with exit 0 and no duplicate records" (fun () ->
              match DokimosTestRuntime.dll () with
              | None -> DokimosTestRuntime.skip "attested completion"
              | Some dll ->
                  let f = fixture [ "true" ] false
                  Assert.equal 0 (CliHarness.rosWith f.Root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-1" ]).Exit
                  let validate = [ "work"; "validate-run"; "--id"; item; "--token"; "tok-1"; "--generation"; "1"; "--validation"; "unit-tests"; "--evidence-id"; "EV-TESTS"; "--"; "true" ]
                  Assert.equal 0 (CliHarness.rosWith f.Root identity validate).Exit
                  let environment = [ "PRAXIS_DOKIMOS_EXECUTABLE", dll; FileDerivedCompletion.AuthorityVariable, f.Authority; FileDerivedCompletion.ContractVariable, f.Contract ] @ identity
                  let arguments = [ "work"; "complete-verified"; "--id"; item; "--token"; "tok-1" ]
                  let first = CliHarness.rosWith f.Root environment arguments
                  Assert.equal 0 first.Exit
                  Assert.equal "verified" (text (field (CliHarness.json first.Out) "outcome"))
                  let afterFirst = completionState f
                  let recordsAfterFirst = records f
                  let eventsAfterFirst = transientEvents f
                  let second = CliHarness.rosWith f.Root environment arguments
                  Assert.equal 0 second.Exit
                  Assert.equal "replayed" (text (field (CliHarness.json second.Out) "outcome"))
                  Assert.equal afterFirst (completionState f)
                  Assert.equal recordsAfterFirst (records f)
                  Assert.equal 1 (fst (records f) |> List.filter ((=) "work.completion.verified") |> List.length)
                  Assert.equal 1 (transientEvents f |> List.filter ((=) "work.completion.verified") |> List.length)
                  let added = transientEvents f |> List.skip eventsAfterFirst.Length
                  Assert.isTrue (added |> List.forall ((=) "work.completion.replayed")) $"a replay adds no completion record: {added}"
                  Assert.equal "" (status f)) ]
