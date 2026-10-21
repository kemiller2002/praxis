namespace Praxis.Tests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Praxis.Application.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// PRX-REL-001..011 (RQ-ROS-2026-A032): atomic claims, fencing, leases,
/// idempotent transitions and the Dokimos-verified completion gate. Clocks
/// and tokens are injected; concurrency tests race real threads, files and
/// Git pushes against one shared store and assert exactly one winner.
[<RequireQualifiedAccess>]
module WorkClaimTests =
    let private t name run = { Name = $"work claims: {name}"; Run = run }

    let private t0 = DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero)
    let private lease = TimeSpan.FromMinutes 30.0
    let private item = "PRAXIS-REL-001"
    let private commit = String('a', 40)
    let private ordoDigest = "sha256:" + String('b', 64)

    type private Harness =
        { Ports: WorkClaimPorts
          Clock: DateTimeOffset ref
          Events: ResizeArray<ClaimEvent> }

    let private eventId (event: ClaimEvent) =
        let node = WorkClaimJson.eventNode "test" "1.0.0" None None event
        node["eventId"].GetValue<string>()

    /// An idempotent in-memory event sink, like the file sink: an identical
    /// event (same content, same id) is recorded once.
    let private harnessWith (store: WorkClaimStore) =
        let clock = ref t0
        let events = ResizeArray<ClaimEvent>()
        let seen = HashSet<string>()

        { Clock = clock
          Events = events
          Ports =
            { Store = store
              Now = fun () -> clock.Value
              Claimability = fun _ -> Claimability.Claimable
              Emit = fun event -> lock events (fun () -> (if seen.Add(eventId event) then events.Add event); Ok()) } }

    let private harness () = harnessWith (InMemoryWorkClaimStore.create ())

    let private request claimant token =
        { WorkItemId = item
          Claimant = claimant
          ClaimToken = token
          LeaseDuration = lease
          ExpectedRevision = None
          RecoverOnly = false }

    let private claimed outcome =
        match outcome with
        | ClaimOutcome.Succeeded(claim, _) -> claim
        | other -> failwith $"expected a claim, got {other}"

    let private refusedCode outcome =
        match outcome with
        | ClaimOutcome.Refused(conflict, _) -> ClaimConflict.code conflict
        | other -> failwith $"expected a refusal, got {other}"

    let private fenceOf (claim: WorkClaim) =
        { Claimant = claim.Holder.Value.Claimant; ClaimToken = claim.Holder.Value.ClaimToken; Generation = claim.Generation }

    let private eventTypes (h: Harness) = h.Events |> Seq.map ClaimEvent.eventType |> Seq.toList

    /// Races `count` acquirers through `acquire` from a common start line.
    let private race count (acquire: int -> ClaimOutcome) =
        use start = new Barrier(count)

        [ for index in 0 .. count - 1 ->
              Tasks.Task.Run(fun () ->
                  start.SignalAndWait()
                  acquire index) ]
        |> List.map _.Result

    let private winners outcomes =
        outcomes
        |> List.filter (function
            | ClaimOutcome.Succeeded(_, false) -> true
            | _ -> false)

    let private assertSingleWinner (outcomes: ClaimOutcome list) =
        Assert.equal 1 (winners outcomes).Length

        for outcome in outcomes do
            match outcome with
            | ClaimOutcome.Succeeded(_, false) -> ()
            | ClaimOutcome.Refused(ClaimConflict.Held _, _)
            | ClaimOutcome.Refused(ClaimConflict.Lost _, _) -> ()
            | other -> failwith $"a losing acquirer must get a structured conflict, got {other}"

    // ---- completion fixtures -------------------------------------------------

    let private hostInput =
        { OrdoContractVersion = "ordo.constructibility-contract/1"
          OrdoContractDigest = ordoDigest
          SupportedOrdoContractVersions = [ "ordo.constructibility-contract/1" ]
          RequiredRequirementIds = [ item ]
          RequiredDecisionIds = []
          RequiredConflictResolutionIds = []
          RequiredValidationNames = [ "unit-tests" ]
          RequiredEvidenceIds = [ "EV-TESTS" ]
          ExpectedCommit = Some commit
          ExpectedVerificationRevision = Some commit 
          TrustedVerifierKeys = []
          ValidationPolicyDigest = None }

    /// The host input bound to a contract ready for it (REL-GATE-002).
    let private input = OrdoTestContracts.bind hostInput

    /// A passing `unit-tests` result for EV-TESTS, recorded for `claim`
    /// through `ValidationRuns.run` into the harness's event sink.
    let private evidenceFile (h: Harness) (claim: WorkClaim) = ValidationEvidenceFixtures.validate h.Ports commit claim

    let private completionRequest (claim: WorkClaim) (evidencePath: string) =
        { WorkItemId = item
          Fence = fenceOf claim
          Commit = commit
          VerificationRevision = commit
          OrdoContractVersion = None
          OrdoContractDigest = None
          RequirementIds = [ item ]
          DecisionIds = []
          ConflictResolutionIds = []
          Validations = [ { Name = "unit-tests"; Status = CheckStatus.Passed; EvidenceIds = [ "EV-TESTS" ] } ]
          Evidence = [ { Id = "EV-TESTS"; Path = evidencePath; ClaimedDigest = None } ]
          TelemetryExecutions = [ "EXE-TEST" ] }

    let private receipt (packet: CompletionPacket) verdict (diagnostics: ReceiptDiagnostic list) =
        { Contract = CompletionGate.ReceiptContract
          SchemaVersion = CompletionGate.ReceiptSchemaVersion
          DokimosVersion = "0.5.0-test"
          Verdict = verdict
          ExitCode = (if verdict = "verified" then 0 else 4)
          WorkItemId = packet.WorkItemId
          Commit = packet.Commit
          VerificationRevision = packet.VerificationRevision
          PacketDigest = CompletionGate.packetDigest packet
          RequirementCount = packet.RequirementIds.Length
          EvidenceDigests = []
          ReusedEvidenceIds = []
          Diagnostics = diagnostics }

    /// An in-process stand-in for Dokimos's fencing and replay checks.
    let private fakeDokimos (calls: int ref) : CompletionVerifier =
        fun packet authority ->
            Interlocked.Increment(&calls.contents) |> ignore
            let digest = CompletionGate.packetDigest packet

            let diagnostics =
                [ if packet.ClaimToken <> authority.ClaimToken then
                      yield { Code = "DOK.COMPLETION.STALE_CLAIM"; Subject = "claimToken"; Detail = "stale" }
                  if packet.FencingGeneration <> authority.FencingGeneration then
                      yield { Code = "DOK.COMPLETION.STALE_FENCE"; Subject = "fencingGeneration"; Detail = "stale" }
                  if authority.ConsumedPacketDigests |> List.contains digest then
                      yield { Code = "DOK.COMPLETION.REPLAYED_PACKET"; Subject = digest; Detail = "consumed" }
                  for evidence in packet.Evidence do
                      if not (authority.Evidence |> List.exists (fun observed -> observed.Id = evidence.Id && observed.Digest = evidence.Digest)) then
                          yield { Code = "DOK.COMPLETION.EVIDENCE_DIGEST_MISMATCH"; Subject = evidence.Id; Detail = "mismatch" } ]

            if diagnostics.IsEmpty then
                VerifierRun.Exited(0, Ok(receipt packet "verified" []), "sha256:" + String('c', 64), "")
            else
                VerifierRun.Exited(4, Ok(receipt packet "rejected" diagnostics), "sha256:" + String('d', 64), "")

    let private completionPorts (h: Harness) verifier =
        { Claims = h.Ports
          ObserveEvidence = FileWorkClaims.observeEvidence "/"
          RecordedValidations = ValidationEvidenceFixtures.recorded h.Events
          ObserveHead = fun () -> Ok commit
          Verifier = verifier }

    let private findingCodes outcome =
        match outcome with
        | CompletionOutcome.Rejected(findings, _) -> findings |> List.map _.Code
        | other -> failwith $"expected a rejection, got {other}"

    let private script (body: string) =
        let directory = CliHarness.temporaryDirectory "praxis-fake-dokimos"
        let path = Path.Combine(directory, "dokimos")
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n")
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        path

    let private scriptVerifier (body: string) timeout =
        DokimosCompletionVerifier.create (Ok(script body)) timeout

    let private receiptJson (r: DokimosReceipt) =
        JsonSerializer.Serialize
            {| Contract = r.Contract; SchemaVersion = r.SchemaVersion; DokimosVersion = r.DokimosVersion; Verdict = r.Verdict
               ExitCode = r.ExitCode; WorkItemId = r.WorkItemId; Commit = r.Commit; VerificationRevision = r.VerificationRevision
               PacketDigest = r.PacketDigest; RequirementCount = r.RequirementCount; EvidenceDigests = r.EvidenceDigests
               ReusedEvidenceIds = r.ReusedEvidenceIds
               Diagnostics = r.Diagnostics |> List.map (fun d -> {| Code = d.Code; Subject = d.Subject; Detail = d.Detail |}) |}

    let private prepared () =
        let h = harness ()
        let claim = WorkClaims.acquire h.Ports (request "agent-a" "token-a") |> claimed
        let evidence = evidenceFile h claim
        h, claim, evidence, completionRequest claim evidence

    /// The packet Praxis builds for `request`: only from evidence the gate
    /// establishes as `ValidatedEvidence` against the harness's claim.
    let private packetFor (h: Harness) (request: CompletionRequest) =
        let observations = request.Evidence |> List.map (fun e -> e.Id, FileWorkClaims.observeEvidence "/" e) |> Map.ofList
        let claim = h.Ports.Store.Read request.WorkItemId |> Result.defaultValue None |> Option.get

        match CompletionGate.preflight input request claim (ValidationEvidenceFixtures.recorded h.Events request.WorkItemId) observations claim.Holder.Value.AcquiredAt with
        | Ok evidence -> CompletionGate.packet input request evidence
        | Error findings -> failwith $"the fixture evidence must be established: {findings}"

    // ---- Git fixtures --------------------------------------------------------

    let private gitRemote () =
        let root = CliHarness.temporaryDirectory "praxis-claim-git"
        let origin = Path.Combine(root, "origin.git")
        CliHarness.git root [ "init"; "-q"; "--bare"; origin ] |> ignore

        let clone name =
            let path = Path.Combine(root, name)
            CliHarness.git root [ "clone"; "-q"; origin; path ] |> ignore
            path

        origin, clone

    let private gitStore clone = GitRefWorkClaimStore.create clone "origin" (TimeSpan.FromSeconds 60.0)

    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "release.json")) && Directory.Exists(Path.Combine(directory.FullName, ".ros", "work")) then
            directory.FullName
        elif isNull directory.Parent then failwith "repository root not found"
        else repositoryRoot directory.Parent

    let tests =
        [ t "first acquisition takes generation 1 and emits work.claim.acquired" (fun () ->
              let h = harness ()
              let claim = WorkClaims.acquire h.Ports (request "agent-a" "token-a") |> claimed
              Assert.equal (1L, 1L, ClaimStatus.Claimed) (claim.Revision, claim.Generation, claim.Status)
              Assert.equal [ "work.claim.acquired" ] (eventTypes h))

          t "a second claimant is refused while the lease is valid (PRX.CLAIM.HELD)" (fun () ->
              let h = harness ()
              WorkClaims.acquire h.Ports (request "agent-a" "token-a") |> claimed |> ignore
              Assert.equal "PRX.CLAIM.HELD" (WorkClaims.acquire h.Ports (request "agent-b" "token-b") |> refusedCode)
              Assert.equal [ "work.claim.acquired"; "work.claim.rejected" ] (eventTypes h))

          t "re-sending the same acquisition returns the original claim without a new event" (fun () ->
              let h = harness ()
              let first = WorkClaims.acquire h.Ports (request "agent-a" "token-a") |> claimed
              h.Clock.Value <- t0.AddMinutes 1.0

              match WorkClaims.acquire h.Ports (request "agent-a" "token-a") with
              | ClaimOutcome.Succeeded(claim, true) -> Assert.equal first claim
              | other -> failwith $"expected a replay, got {other}"

              Assert.equal [ "work.claim.acquired" ] (eventTypes h))

          t "N parallel acquirers on one item: exactly one winner (in-memory store)" (fun () ->
              let h = harness ()
              race 16 (fun index -> WorkClaims.acquire h.Ports (request $"agent-{index}" $"token-{index}")) |> assertSingleWinner)

          t "N parallel acquirers on one item: exactly one winner (local file store)" (fun () ->
              let root = CliHarness.temporaryDirectory "praxis-claim-file"
              let h = harnessWith (FileWorkClaimStore.create root)
              race 12 (fun index -> WorkClaims.acquire h.Ports (request $"agent-{index}" $"token-{index}")) |> assertSingleWinner
              let store = FileWorkClaimStore.create root
              let stored = store.Read item |> Result.defaultWith failwith |> Option.get
              Assert.equal 1L stored.Generation
              // A writer still expecting "absent" loses; so does one at a stale revision.
              Assert.equal (Ok(ClaimWrite.Conflict(Some stored))) (store.CompareAndSwap None { stored with Holder = None })
              let renewed = { stored with Revision = 2L }
              Assert.equal (Ok(ClaimWrite.Written renewed)) (store.CompareAndSwap (Some 1L) renewed)
              Assert.equal (Ok(ClaimWrite.Conflict(Some renewed))) (store.CompareAndSwap (Some 1L) { renewed with Generation = 9L }))

          t "two independent clones racing through a bare origin: exactly one winner (git-ref store)" (fun () ->
              let _, clone = gitRemote ()
              let stores = [ for index in 0..3 -> gitStore (clone $"clone-{index}") ]

              let outcomes =
                  race 4 (fun index -> WorkClaims.acquire (harnessWith stores[index]).Ports (request $"agent-{index}" $"token-{index}"))

              assertSingleWinner outcomes
              let winner = (winners outcomes).Head |> claimed
              // Every clone reads the same linearized claim from origin.
              for store in stores do
                  Assert.equal (Ok(Some winner)) (store.Read item))

          t "git-ref store rejects a stale compare-and-swap and later renewals of a stale generation" (fun () ->
              let _, clone = gitRemote ()
              let first = harnessWith (gitStore (clone "first"))
              let second = harnessWith (gitStore (clone "second"))
              let claim = WorkClaims.acquire first.Ports (request "agent-a" "token-a") |> claimed
              let stale = { claim with Holder = Some { claim.Holder.Value with Claimant = "forged" } }

              match second.Ports.Store.CompareAndSwap None stale with
              | Ok(ClaimWrite.Conflict(Some current)) -> Assert.equal claim current
              | other -> failwith $"expected a create-only conflict, got {other}"

              second.Clock.Value <- t0 + lease + TimeSpan.FromSeconds 1.0
              let recovered = WorkClaims.recover second.Ports (request "agent-b" "token-b") |> claimed
              Assert.equal (2L, Some "agent-a") (recovered.Generation, recovered.PreviousClaimant)
              first.Clock.Value <- second.Clock.Value
              Assert.equal "PRX.CLAIM.STALE_GENERATION" (WorkClaims.renew first.Ports item (fenceOf claim) lease false |> refusedCode))

          t "a stale expected revision is refused (PRX.CLAIM.STALE_REVISION) and a stale store write conflicts" (fun () ->
              let h = harness ()
              let claim = WorkClaims.acquire h.Ports (request "agent-a" "token-a") |> claimed
              WorkClaims.release h.Ports item (fenceOf claim) false |> claimed |> ignore
              let stale = { request "agent-b" "token-b" with ExpectedRevision = Some 1L }
              Assert.equal "PRX.CLAIM.STALE_REVISION" (WorkClaims.acquire h.Ports stale |> refusedCode)

              match h.Ports.Store.CompareAndSwap (Some 1L) { claim with Revision = 2L } with
              | Ok(ClaimWrite.Conflict(Some current)) -> Assert.equal 2L current.Revision
              | other -> failwith $"expected a CAS conflict, got {other}")

          t "renewal extends the lease, marks running, and is fenced" (fun () ->
              let h = harness ()
              let claim = WorkClaims.acquire h.Ports (request "agent-a" "token-a") |> claimed
              h.Clock.Value <- t0.AddMinutes 10.0
              let renewed = WorkClaims.renew h.Ports item (fenceOf claim) lease true |> claimed
              Assert.equal (ClaimStatus.Running, t0.AddMinutes 40.0) (renewed.Status, renewed.Holder.Value.LeaseExpiresAt)
              let wrongToken = { fenceOf claim with ClaimToken = "guess" }
              Assert.equal "PRX.CLAIM.STALE_TOKEN" (WorkClaims.renew h.Ports item wrongToken lease false |> refusedCode))

          t "after lease expiry the owner cannot renew, and a new claimant recovers with generation + 1" (fun () ->
              let h = harness ()
              let claim = WorkClaims.acquire h.Ports (request "agent-a" "token-a") |> claimed
              h.Clock.Value <- t0 + lease
              Assert.equal "PRX.CLAIM.EXPIRED" (WorkClaims.renew h.Ports item (fenceOf claim) lease false |> refusedCode)
              let recovered = WorkClaims.acquire h.Ports (request "agent-b" "token-b") |> claimed
              Assert.equal (2L, Some "agent-a", ClaimStatus.Claimed) (recovered.Generation, recovered.PreviousClaimant, recovered.Status)

              match h.Events |> Seq.last with
              | { Payload = ClaimEventPayload.ClaimAcquired(_, _, 2L, _, _, true, Some "agent-a") } -> ()
              | other -> failwith $"expected a recovered acquisition event, got {other}")

          t "recover refuses a live lease; expire records an expired lease durably" (fun () ->
              let h = harness ()
              WorkClaims.acquire h.Ports (request "agent-a" "token-a") |> claimed |> ignore
              Assert.equal "PRX.CLAIM.HELD" (WorkClaims.recover h.Ports (request "agent-b" "token-b") |> refusedCode)
              Assert.equal "PRX.CLAIM.HELD" (WorkClaims.expire h.Ports item |> refusedCode)
              h.Clock.Value <- t0 + lease
              let expired = WorkClaims.expire h.Ports item |> claimed
              Assert.equal (ClaimStatus.Expired, 1L) (expired.Status, expired.Generation)
              Assert.equal 2L ((WorkClaims.recover h.Ports (request "agent-b" "token-b") |> claimed).Generation))

          t "release is idempotent and a released item is claimable at the next generation" (fun () ->
              let h = harness ()
              let claim = WorkClaims.acquire h.Ports (request "agent-a" "token-a") |> claimed
              let released = WorkClaims.release h.Ports item (fenceOf claim) false |> claimed

              match WorkClaims.release h.Ports item (fenceOf claim) false with
              | ClaimOutcome.Succeeded(again, true) -> Assert.equal released again
              | other -> failwith $"expected a replayed release, got {other}"

              Assert.equal [ "work.claim.acquired"; "work.claim.released" ] (eventTypes h)
              Assert.equal "PRX.CLAIM.INVALID_REQUEST" (WorkClaims.acquire h.Ports (request "agent-c" "token-a") |> refusedCode)
              Assert.equal 2L ((WorkClaims.acquire h.Ports (request "agent-b" "token-b") |> claimed).Generation))

          t "a completed or abandoned queue item is not claimable" (fun () ->
              let h = harness ()
              let ports = { h.Ports with Claimability = fun _ -> Claimability.NotClaimable "complete in the queue" }
              Assert.equal "PRX.CLAIM.NOT_CLAIMABLE" (WorkClaims.acquire ports (request "agent-a" "token-a") |> refusedCode))

          t "verified completion binds the receipt and emits work.completion.verified" (fun () ->
              let h, _, _, completion = prepared ()
              let calls = ref 0

              match WorkClaims.completeVerified (completionPorts h (fakeDokimos calls)) input completion with
              | CompletionOutcome.Completed(claim, verified) ->
                  Assert.equal (ClaimStatus.Completed, 1L) (claim.Status, claim.Generation)
                  Assert.equal [ verified.PacketDigest ] claim.ConsumedPacketDigests
                  Assert.equal [ item ] verified.RequirementIds
              | other -> failwith $"expected completion, got {other}"

              Assert.equal [ "work.claim.acquired"; "work.validation.observed"; "work.completion.verified" ] (eventTypes h))

          t "a replayed completion is idempotent: no second verification and one verified event" (fun () ->
              let h, _, _, completion = prepared ()
              let calls = ref 0
              let ports = completionPorts h (fakeDokimos calls)
              let first = WorkClaims.completeVerified ports input completion
              h.Clock.Value <- t0 + lease + lease

              match first, WorkClaims.completeVerified ports input completion with
              | CompletionOutcome.Completed(claim, verified), CompletionOutcome.Replayed(again, replayed) ->
                  Assert.equal (claim, verified) (again, replayed)
              | other -> failwith $"expected completion then replay, got {other}"

              Assert.equal 1 calls.Value
              Assert.equal [ "work.claim.acquired"; "work.validation.observed"; "work.completion.verified"; "work.completion.replayed" ] (eventTypes h))

          t "a stale owner cannot complete after another claimant recovered" (fun () ->
              let h, _, _, completion = prepared ()
              h.Clock.Value <- t0 + lease
              WorkClaims.acquire h.Ports (request "agent-b" "token-b") |> claimed |> ignore
              let calls = ref 0
              let codes = WorkClaims.completeVerified (completionPorts h (fakeDokimos calls)) input completion |> findingCodes
              Assert.equal [ "PRX.CLAIM.STALE_GENERATION" ] codes
              Assert.equal 0 calls.Value
              Assert.equal [ "work.claim.acquired"; "work.validation.observed"; "work.claim.acquired"; "work.completion.rejected" ] (eventTypes h))

          t "an expired owner cannot publish a late completion" (fun () ->
              let h, _, _, completion = prepared ()
              h.Clock.Value <- t0 + lease
              let codes = WorkClaims.completeVerified (completionPorts h (fakeDokimos (ref 0))) input completion |> findingCodes
              Assert.equal [ "PRX.CLAIM.EXPIRED" ] codes)

          t "a crash between Dokimos and the final CAS recovers without double completion" (fun () ->
              let h, _, _, completion = prepared ()
              let armed = ref true
              let inner = h.Ports.Store

              let crashing =
                  { inner with
                      CompareAndSwap =
                          fun expected next ->
                              if armed.Value && next.Status = ClaimStatus.Completed then
                                  armed.Value <- false
                                  Error "simulated crash before the final compare-and-swap"
                              else
                                  inner.CompareAndSwap expected next }

              let calls = ref 0
              let ports = completionPorts { h with Ports = { h.Ports with Store = crashing } } (fakeDokimos calls)

              match WorkClaims.completeVerified ports input completion with
              | CompletionOutcome.Failed _ -> ()
              | other -> failwith $"expected the injected failure, got {other}"

              Assert.equal (Ok(Some ClaimStatus.AwaitingVerification)) (inner.Read item |> Result.map (Option.map _.Status))

              match WorkClaims.completeVerified ports input completion with
              | CompletionOutcome.Completed(claim, _) -> Assert.equal ClaimStatus.Completed claim.Status
              | other -> failwith $"expected recovery to complete, got {other}"

              match WorkClaims.completeVerified ports input completion with
              | CompletionOutcome.Replayed _ -> ()
              | other -> failwith $"expected a replay, got {other}"

              Assert.equal 2 calls.Value
              Assert.equal 1 (eventTypes h |> List.filter ((=) "work.completion.verified")).Length)

          t "a crashed completion whose lease expired is recovered by a new owner and never completed by the old one" (fun () ->
              let h, _, _, completion = prepared ()
              let failing = fun (_: CompletionPacket) (_: CompletionAuthority) -> VerifierRun.Crashed "verifier killed"
              let crashed = WorkClaims.completeVerified (completionPorts h failing) input completion |> findingCodes
              Assert.equal [ "PRX.COMPLETION.VERIFIER_FAILED" ] crashed
              h.Clock.Value <- t0 + lease
              let recovered = WorkClaims.acquire h.Ports (request "agent-b" "token-b") |> claimed
              Assert.equal 2L recovered.Generation
              let late = WorkClaims.completeVerified (completionPorts h (fakeDokimos (ref 0))) input completion |> findingCodes
              Assert.equal [ "PRX.CLAIM.STALE_GENERATION" ] late)

          t "missing mandatory evidence is refused before any verifier call" (fun () ->
              let h, _, _, completion = prepared ()
              let calls = ref 0
              let ports = completionPorts h (fakeDokimos calls)
              let none = WorkClaims.completeVerified ports input { completion with Evidence = []; Validations = [] } |> findingCodes
              Assert.equal [ "PRX.COMPLETION.EVIDENCE_MISSING" ] none
              let gone = { completion with Evidence = [ { completion.Evidence.Head with Path = "/nonexistent/evidence.txt" } ] }
              Assert.equal [ "PRX.COMPLETION.EVIDENCE_MISSING" ] (WorkClaims.completeVerified ports input gone |> findingCodes)
              Assert.equal 0 calls.Value)

          t "a mutable commit or verification revision is refused" (fun () ->
              let h, _, _, completion = prepared ()
              let ports = completionPorts h (fakeDokimos (ref 0))
              let codes = WorkClaims.completeVerified ports input { completion with Commit = "HEAD"; VerificationRevision = "abc123" } |> findingCodes
              Assert.equal [ "PRX.COMPLETION.INVALID_COMMIT"; "PRX.COMPLETION.INVALID_VERIFICATION_REVISION" ] codes)

          t "a forged evidence digest is refused by verification and the holder may resubmit" (fun () ->
              let h, _, _, completion = prepared ()
              let ports = completionPorts h (fakeDokimos (ref 0))
              let forged = { completion with Evidence = [ { completion.Evidence.Head with ClaimedDigest = Some("sha256:" + String('e', 64)) } ] }

              match WorkClaims.completeVerified ports input forged with
              | CompletionOutcome.Rejected(findings, Some claim) ->
                  Assert.isTrue (findings |> List.exists (fun f -> f.Code = "DOK.COMPLETION.EVIDENCE_DIGEST_MISMATCH")) $"{findings}"
                  Assert.equal ClaimStatus.Running claim.Status
              | other -> failwith $"expected a Dokimos refusal, got {other}"

              match WorkClaims.completeVerified ports input completion with
              | CompletionOutcome.Completed _ -> ()
              | other -> failwith $"expected the honest resubmission to complete, got {other}")

          t "verifier missing fails closed (PRX.COMPLETION.VERIFIER_UNAVAILABLE)" (fun () ->
              let h, _, _, completion = prepared ()
              let missing = DokimosCompletionVerifier.resolve (fun name -> if name = "PRAXIS_DOKIMOS_EXECUTABLE" then Some "/nonexistent/dokimos" else None)
              let ports = completionPorts h (DokimosCompletionVerifier.create missing (TimeSpan.FromSeconds 5.0))
              Assert.equal [ "PRX.COMPLETION.VERIFIER_UNAVAILABLE" ] (WorkClaims.completeVerified ports input completion |> findingCodes)
              let notOnPath = DokimosCompletionVerifier.resolve (fun name -> if name = "PATH" then Some "/nonexistent" else None)
              Assert.isTrue (Result.isError notOnPath) "dokimos must not be found on an empty PATH")

          t "a crashing, hanging or garbled verifier process fails closed" (fun () ->
              let h, _, _, completion = prepared ()
              let attempt verifier = WorkClaims.completeVerified (completionPorts h verifier) input completion |> findingCodes
              Assert.equal [ "PRX.COMPLETION.VERIFIER_FAILED" ] (attempt (scriptVerifier "echo boom >&2; exit 70" (TimeSpan.FromSeconds 20.0)))
              Assert.equal [ "PRX.COMPLETION.RECEIPT_UNPARSABLE" ] (attempt (scriptVerifier "echo not-json; exit 0" (TimeSpan.FromSeconds 20.0)))
              Assert.equal [ "PRX.COMPLETION.VERIFIER_INPUT_REJECTED" ] (attempt (scriptVerifier "echo '{}'; exit 3" (TimeSpan.FromSeconds 20.0)))
              Assert.equal [ "PRX.COMPLETION.VERIFIER_TIMEOUT" ] (attempt (scriptVerifier "sleep 30" (TimeSpan.FromSeconds 1.0)))
              let claim = h.Ports.Store.Read item |> Result.map (Option.map _.Status)
              Assert.equal (Ok(Some ClaimStatus.Running)) claim)

          t "a fake Dokimos executable: refusal codes are copied verbatim, a receipt for another packet is refused, a bound receipt verifies" (fun () ->
              let h, _, _, completion = prepared ()
              let packet = packetFor h completion
              let attempt body = WorkClaims.completeVerified (completionPorts h (scriptVerifier body (TimeSpan.FromSeconds 20.0))) input completion
              let refusal = receipt packet "rejected" [ { Code = "DOK.COMPLETION.MISSING_DECISION"; Subject = "DF-1"; Detail = "required decision is absent" } ]
              let refused = attempt $"cat <<'JSON'\n{receiptJson refusal}\nJSON\nexit 4" |> findingCodes
              Assert.equal [ "PRX.COMPLETION.VERIFICATION_REJECTED"; "DOK.COMPLETION.MISSING_DECISION" ] refused
              let foreign = { receipt packet "verified" [] with PacketDigest = "sha256:" + String('f', 64) }
              Assert.equal [ "PRX.COMPLETION.RECEIPT_MISMATCH" ] (attempt $"cat <<'JSON'\n{receiptJson foreign}\nJSON\nexit 0" |> findingCodes)

              let bound = receiptJson (receipt packet "verified" [])

              match attempt $"cat <<'JSON'\n{bound}\nJSON\nexit 0" with
              | CompletionOutcome.Completed(_, verified) -> Assert.isTrue (CompletionGate.isSha256Digest verified.ReceiptDigest) verified.ReceiptDigest
              | other -> failwith $"expected completion, got {other}")

          t "batched slices keep independent claims and evidence (PRX-REL-011)" (fun () ->
              let h = harness ()
              let slice id token = { request "agent-a" token with WorkItemId = id }
              WorkClaims.acquire h.Ports { slice "PRAXIS-REL-B2" "other" with Claimant = "agent-z" } |> claimed |> ignore
              let outcomes = WorkClaims.acquireEach h.Ports [ slice "PRAXIS-REL-B1" "t1"; slice "PRAXIS-REL-B2" "t2"; slice "PRAXIS-REL-B3" "t3" ]

              let states =
                  outcomes
                  |> List.map (fun (id, outcome) ->
                      id,
                      match outcome with
                      | ClaimOutcome.Succeeded(claim, _) -> claim.Holder.Value.ClaimToken
                      | other -> refusedCode other)

              Assert.equal [ "PRAXIS-REL-B1", "t1"; "PRAXIS-REL-B2", "PRX.CLAIM.HELD"; "PRAXIS-REL-B3", "t3" ] states
              let all = h.Ports.Store.List() |> Result.defaultValue []
              Assert.equal 3 (all |> List.map _.Holder.Value.ClaimToken |> List.distinct).Length)

          t "I1 event payloads carry exactly the frozen members" (fun () ->
              let completed =
                  { PacketDigest = "sha256:" + String('1', 64); ReceiptDigest = "sha256:" + String('2', 64); EvidenceDigests = []; Commit = commit
                    VerificationRevision = commit; RequirementIds = [ item ]; TelemetryExecutions = [ "EXE-1" ]
                    VerifierName = "dokimos"; VerifierVersion = "0.5.0"; Attestation = CompletionAttestation.Unattested; VerifierKeyIds = []; CompletedAt = t0 }

              let envelope = set [ "schemaVersion"; "type"; "workItem"; "repository"; "protocolVersion"; "occurredAt"; "eventId" ]

              let keys payload =
                  let node = WorkClaimJson.eventNode "repo" "1.0.0" None None { WorkItemId = item; OccurredAt = t0; Payload = payload }
                  Set.difference (node.AsObject() |> Seq.map _.Key |> Set.ofSeq) envelope |> Set.toList

              Assert.equal [ "claimToken"; "claimant"; "generation"; "leaseExpiresAt"; "previousClaimant"; "recovered"; "revision" ]
                  (keys (ClaimEventPayload.ClaimAcquired("a", "t", 2L, t0, 3L, true, Some "b")))
              Assert.equal [ "claimant"; "currentGeneration"; "reason" ] (keys (ClaimEventPayload.ClaimRejected("a", "held", 1L)))
              Assert.equal [ "claimToken"; "claimant"; "generation"; "leaseExpiresAt" ] (keys (ClaimEventPayload.ClaimRenewed("a", "t", 1L, t0)))
              Assert.equal [ "claimToken"; "claimant"; "generation" ] (keys (ClaimEventPayload.ClaimReleased("a", "t", 1L)))
              Assert.equal [ "claimToken"; "claimant"; "codes"; "generation"; "packetDigest"; "verifier" ]
                  (keys (ClaimEventPayload.CompletionRejected("a", "t", 1L, [ "PRX.X" ], Some "sha256:x", None)))
              Assert.equal
                  [ "attestation"; "claimToken"; "claimant"; "commit"; "generation"; "packetDigest"; "receiptDigest"; "requirementIds"; "telemetryExecutions"
                    "verificationRevision" ]
                  (keys (ClaimEventPayload.CompletionVerified("a", "t", 1L, completed)))
              // REL-GATE-004: an attested completion also names its receipt keyids.
              Assert.equal
                  [ "attestation"; "claimToken"; "claimant"; "commit"; "generation"; "packetDigest"; "receiptDigest"; "requirementIds"; "telemetryExecutions"
                    "verificationRevision"; "verifierKeyIds" ]
                  (keys (ClaimEventPayload.CompletionVerified("a", "t", 1L, { completed with Attestation = CompletionAttestation.Attested; VerifierKeyIds = [ "sha256:" + String('3', 64) ] })))
              Assert.equal [ "claimToken"; "generation"; "packetDigest" ] (keys (ClaimEventPayload.CompletionReplayed("t", 1L, "sha256:x")))
              Assert.equal
                  [ "claimToken"; "claimant"; "evidenceId"; "generation"; "observedAt"; "resultDigest"; "revision"; "status"; "validation" ]
                  (keys (ClaimEventPayload.ValidationObserved("a", "t", 1L, "unit-tests", "EV-TESTS", commit, CheckStatus.Passed, t0, "sha256:x"))))

          t "the file event sink appends an identical event once" (fun () ->
              let root = CliHarness.temporaryDirectory "praxis-claim-events"
              let emit = FileWorkClaimEvents.create root None None
              let event = { WorkItemId = item; OccurredAt = t0; Payload = ClaimEventPayload.ClaimReleased("a", "t", 1L) }
              Assert.equal (Ok()) (emit event)
              Assert.equal (Ok()) (emit event)
              let lines = File.ReadAllLines(TransientStore.events root)
              Assert.isTrue (not (File.Exists(Path.Combine(root, ".ros", "events", "events.jsonl")))) "claim-protocol events stay out of the tracked tree (I8)"
              Assert.equal 1 lines.Length)

          t "migration: the repository's queue.json loads unchanged with zero lost records beside a claims store" (fun () ->
              let source = Path.Combine(repositoryRoot (DirectoryInfo(AppContext.BaseDirectory)), ".ros", "work", "queue.json")
              let root = CliHarness.temporaryDirectory "praxis-claim-migration"
              let queue = Path.Combine(root, ".ros", "work", "queue.json")
              Directory.CreateDirectory(Path.GetDirectoryName queue) |> ignore
              File.Copy(source, queue)
              let before = File.ReadAllBytes queue
              use document = JsonDocument.Parse(ReadOnlyMemory<byte>(before))
              let recorded = document.RootElement.GetProperty("items").GetArrayLength()
              Assert.equal "1.0.0" (document.RootElement.GetProperty("schemaVersion").GetString())
              let ids = FileBacklogQueueRepository.readItems root |> List.map _.Id
              let open' = FileBacklogQueueRepository.readItems root |> List.find (fun entry -> entry.Status = "captured" || entry.Status = "ready")
              let h = harnessWith (FileWorkClaimStore.create root)
              let ports = { h.Ports with Claimability = FileWorkClaims.claimability root }
              WorkClaims.acquire ports { request "agent-a" "token-a" with WorkItemId = open'.Id } |> claimed |> ignore
              let complete = FileBacklogQueueRepository.readItems root |> List.find (fun entry -> entry.Status = "complete")
              Assert.equal "PRX.CLAIM.NOT_CLAIMABLE" (WorkClaims.acquire ports { request "agent-a" "token-c" with WorkItemId = complete.Id } |> refusedCode)
              Assert.isTrue (before = File.ReadAllBytes queue) "queue.json must be byte-for-byte unchanged"
              Assert.equal recorded ids.Length
              Assert.equal recorded (FileBacklogQueueRepository.readItems root).Length
              Assert.isTrue (File.Exists(FileWorkClaimStore.path root open'.Id 1L)) "the claim lives in the separate claims store")

          t "cli: claim, conflicting claim and inspection use documented exit codes" (fun () ->
              let root = CliHarness.temporaryDirectory "praxis-claim-cli"
              CliHarness.write root ".ros/work/queue.json" """{"schemaVersion":"1.0.0","items":[{"id":"PRAXIS-REL-001","status":"captured"}]}"""
              let identity name = [ "PRAXIS_ACTOR_KIND", "agent"; "PRAXIS_ACTOR", name; "PRAXIS_TELEMETRY_PROVIDER", "test"; "PRAXIS_TELEMETRY_RUNTIME", "test" ]
              let first = CliHarness.rosWith root (identity "test/agent-a") [ "work"; "claim"; "--id"; item; "--token"; "tok-a" ]
              Assert.equal 0 first.Exit
              let second = CliHarness.rosWith root (identity "test/agent-b") [ "work"; "claim"; "--id"; item ]
              Assert.equal 3 second.Exit
              let refusal = CliHarness.json second.Out
              Assert.equal "PRX.CLAIM.HELD" (refusal["code"].GetValue<string>())
              let shown = CliHarness.ros root [ "work"; "claims"; item ]
              let listed = CliHarness.json shown.Out
              let first = listed["claims"].AsArray()[0]
              let holder = first["holder"]
              Assert.equal "test/agent-a" (holder["claimant"].GetValue<string>())

              // Completion evidence is a recorded validation result run at HEAD.
              CliHarness.git root [ "init"; "-q" ] |> ignore
              CliHarness.commitAll root "fixture"
              let head = CliHarness.git root [ "rev-parse"; "HEAD" ]
              let fence = [ "--id"; item; "--token"; "tok-a"; "--generation"; "1" ]
              let validated = CliHarness.rosWith root (identity "test/agent-a") ([ "work"; "validate-run" ] @ fence @ [ "--validation"; "unit-tests"; "--evidence-id"; "EV-TESTS"; "--"; "true" ])
              Assert.equal 0 validated.Exit
              let validatedDocument = CliHarness.json validated.Out
              let resultPath = validatedDocument["resultPath"].GetValue<string>()

              let host = CliHarness.temporaryDirectory "praxis-claim-cli-host"

              let complete authority evidence =
                  CliHarness.rosWith root (("PRAXIS_DOKIMOS_EXECUTABLE", "/nonexistent/dokimos") :: identity "test/agent-a")
                      [ "work"; "complete-verified"; "--id"; item; "--token"; "tok-a"; "--generation"; "1"; "--commit"; head
                        "--verification-revision"; head; "--authority"; authority; "--requirement"; item
                        "--ordo-contract"; Path.Combine(host, "contract.json")
                        "--validation"; "unit-tests=passed:EV-TESTS"; "--evidence"; $"EV-TESTS={evidence}" ]

              Assert.equal 2 (complete "missing.json" resultPath).Exit
              // Host inputs and the stray file live outside the candidate tree: completion needs a clean worktree (I8).
              CliHarness.write host "evidence/tests.txt" "ok\n"
              // The host authority names no contract identity: the ready contract supplies it (REL-GATE-002).
              CliHarness.write host "contract.json" (OrdoTestContracts.contract [ item ] [])
              CliHarness.write host "authority.json" ($"{{\"OrdoContractVersion\":\"\",\"OrdoContractDigest\":\"\",\"SupportedOrdoContractVersions\":[\"ecir/1\"],\"RequiredRequirementIds\":[\"{item}\"],\"RequiredValidationNames\":[\"unit-tests\"],\"RequiredEvidenceIds\":[\"EV-TESTS\"],\"ExpectedCommit\":\"{head}\",\"ExpectedVerificationRevision\":\"{head}\"}}")
              let plain = complete (Path.Combine(host, "authority.json")) (Path.Combine(host, "evidence/tests.txt"))
              Assert.equal 3 plain.Exit
              Assert.isTrue (plain.Out.Contains ValidationEvidence.UnrecognizedCode) plain.Out
              let unavailable = complete (Path.Combine(host, "authority.json")) resultPath
              Assert.equal 3 unavailable.Exit
              Assert.isTrue (unavailable.Out.Contains "PRX.COMPLETION.VERIFIER_UNAVAILABLE") unavailable.Out
              Assert.equal 2 (CliHarness.ros root [ "work"; "claim"; "--id"; item; "--store"; "nfs" ]).Exit) ]

    /// Runs the real Dokimos CLI end to end when `PRAXIS_TEST_DOKIMOS_DLL`
    /// names a built `Dokimos.Cli.dll`; otherwise it reports itself skipped.
    let integration =
        [ t "integration: the real Dokimos CLI verifies a Praxis-built packet and authority" (fun () ->
              match Environment.GetEnvironmentVariable "PRAXIS_TEST_DOKIMOS_DLL" |> Option.ofObj |> Option.filter File.Exists with
              | None -> eprintfn "SKIP work claims integration: PRAXIS_TEST_DOKIMOS_DLL is not set to a built Dokimos.Cli.dll"
              | Some dll ->
                  let h, _, _, completion = prepared ()
                  let calls = ref 0
                  let real = DokimosCompletionVerifier.create (Ok dll) (TimeSpan.FromSeconds 120.0)

                  let counted packet authority =
                      calls.Value <- calls.Value + 1
                      real packet authority

                  let ports = completionPorts h counted

                  match WorkClaims.completeVerified ports input completion with
                  | CompletionOutcome.Completed(_, verified) ->
                      Assert.equal (CompletionGate.packetDigest (packetFor h completion)) verified.PacketDigest
                  | other -> failwith $"expected the real Dokimos to verify, got {other}"

                  // The same packet again is a Praxis-side replay; Dokimos is not asked twice.
                  match WorkClaims.completeVerified ports input completion with
                  | CompletionOutcome.Replayed _ -> Assert.equal 1 calls.Value
                  | other -> failwith $"expected a replay, got {other}"

                  // A second claimant's packet that omits the required decision is refused by Dokimos itself.
                  let h2, _, _, completion2 = prepared ()
                  let strict = OrdoTestContracts.bind { hostInput with RequiredDecisionIds = [ "DF-ROS-2026-A001" ] }

                  match WorkClaims.completeVerified (completionPorts h2 real) strict completion2 with
                  | CompletionOutcome.Rejected(findings, _) ->
                      Assert.isTrue (findings |> List.exists (fun f -> f.Code = "DOK.COMPLETION.MISSING_DECISION")) $"{findings}"
                  | other -> failwith $"expected a Dokimos refusal, got {other}") ]
