namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Domain.Ordo
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// Integration of RQ-ROS-2026-A032 across the shared interfaces: Requirement
/// -> Ordo contract (I3) -> Praxis claim -> agent evidence -> Dokimos (I2)
/// -> atomic completion -> I1 events -> telemetry report. Scenario numbers
/// follow the reliability integration plan; scenarios covered elsewhere
/// (2, 3, 4, 10: `work claims:`; 9: `telemetry reliability report:`) are
/// referenced in docs/work-claims.md rather than duplicated here.
[<RequireQualifiedAccess>]
module ReliabilityPipelineTests =
    let private t name run = { Name = $"reliability pipeline: {name}"; Run = run }

    let private fixture name =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ordo-contract", name))

    let private validDigest = "sha256:4150ccaea61ac92479911c55ff90f1410b46f641bacd80ddb740daa9b8e48c9e"
    let private commit = String('e', 40)
    let private lease = TimeSpan.FromMinutes 30.0

    let private admitted name =
        match OrdoContractAdmission.admit OrdoContractAdmission.implementedVersions (fixture name) with
        | Ok contract -> contract
        | Error diagnostics -> failwith $"fixture {name} must be admitted: {diagnostics}"

    /// The authority a host derives for one work item before any agent runs:
    /// required sets come from the admitted contract, not from prose.
    let private baseInput =
        { OrdoContractVersion = ""
          OrdoContractDigest = ""
          SupportedOrdoContractVersions = []
          RequiredRequirementIds = []
          RequiredDecisionIds = []
          RequiredConflictResolutionIds = []
          RequiredValidationNames = [ "unit-tests" ]
          RequiredEvidenceIds = [ "EV-TESTS" ]
          ExpectedCommit = Some commit
          ExpectedVerificationRevision = Some commit }

    let private boundInput () =
        match OrdoBoundAuthority.bind (admitted "valid-contract.json") baseInput with
        | Ok input -> input
        | Error findings -> failwith $"binding must succeed: {findings}"

    let private ports (events: ResizeArray<ClaimEvent>) (clock: DateTimeOffset ref) =
        { Store = InMemoryWorkClaimStore.create ()
          Now = fun () -> clock.Value
          Claimability = fun _ -> Claimability.Claimable
          Emit = fun event -> lock events (fun () -> events.Add event); Ok() }

    let private claim (claimPorts: WorkClaimPorts) id claimant token =
        match WorkClaims.acquire claimPorts { WorkItemId = id; Claimant = claimant; ClaimToken = token; LeaseDuration = lease; ExpectedRevision = None; RecoverOnly = false } with
        | ClaimOutcome.Succeeded(claim, _) -> claim
        | other -> failwith $"expected a claim, got {other}"

    /// A file that is not a validation result (REL-GATE-001 reproduction).
    let private plainFile (content: string) =
        let root = CliHarness.temporaryDirectory "praxis-pipeline-evidence"
        let path = Path.Combine(root, "tests.txt")
        File.WriteAllText(path, content)
        path

    /// A recorded, passing `unit-tests` result for `held` at `commit`.
    let private evidence (claimPorts: WorkClaimPorts) (held: WorkClaim) = ValidationEvidenceFixtures.validate claimPorts commit held

    let private request (input: CompletionAuthorityInput) (held: WorkClaim) evidencePath =
        { WorkItemId = held.WorkItemId
          Fence = { Claimant = held.Holder.Value.Claimant; ClaimToken = held.Holder.Value.ClaimToken; Generation = held.Generation }
          Commit = commit
          VerificationRevision = commit
          OrdoContractVersion = Some input.OrdoContractVersion
          OrdoContractDigest = Some input.OrdoContractDigest
          RequirementIds = input.RequiredRequirementIds
          DecisionIds = []
          ConflictResolutionIds = []
          Validations = [ { Name = "unit-tests"; Status = CheckStatus.Passed; EvidenceIds = [ "EV-TESTS" ] } ]
          Evidence = [ { Id = "EV-TESTS"; Path = evidencePath; ClaimedDigest = None } ]
          TelemetryExecutions = [ "EXE-PIPELINE" ] }

    let private realDokimos () =
        Environment.GetEnvironmentVariable "PRAXIS_TEST_DOKIMOS_DLL"
        |> Option.ofObj
        |> Option.filter File.Exists
        |> Option.map (fun dll -> DokimosCompletionVerifier.create (Ok dll) (TimeSpan.FromSeconds 120.0))

    let private completionPorts (events: ResizeArray<ClaimEvent>) claimPorts verifier =
        { Claims = claimPorts
          ObserveEvidence = FileWorkClaims.observeEvidence "/"
          RecordedValidations = ValidationEvidenceFixtures.recorded events
          ObserveHead = fun () -> Ok commit
          Verifier = verifier }

    /// Evidence the gate establishes for `submission` under `held`; the only
    /// input `CompletionGate.packet` and `CompletionGate.authority` accept.
    let private established input (events: ResizeArray<ClaimEvent>) (held: WorkClaim) (submission: CompletionRequest) =
        let observations = submission.Evidence |> List.map (fun e -> e.Id, FileWorkClaims.observeEvidence "/" e) |> Map.ofList

        match CompletionGate.preflight input submission held (ValidationEvidenceFixtures.recorded events held.WorkItemId) observations with
        | Ok evidence -> evidence
        | Error findings -> failwith $"the evidence must be established: {findings}"

    let private dokimosDll () =
        Environment.GetEnvironmentVariable "PRAXIS_TEST_DOKIMOS_DLL" |> Option.ofObj |> Option.filter File.Exists

    /// Runs the real Dokimos CLI on wire documents edited after Praxis built
    /// them: an authority Praxis's types cannot express (a failed or stale
    /// observation) still reaches Dokimos only this way, in a test.
    let private dokimosOnWire (dll: string) (packet: JsonNode) (authority: JsonNode) =
        let root = CliHarness.temporaryDirectory "praxis-dokimos-wire"
        let packetPath = Path.Combine(root, "packet.json")
        let authorityPath = Path.Combine(root, "authority.json")
        File.WriteAllText(packetPath, packet.ToJsonString())
        File.WriteAllText(authorityPath, authority.ToJsonString())
        let run = CliHarness.run "dotnet" [ dll; "completion"; "verify"; "--packet"; packetPath; "--authority"; authorityPath ] []

        match WorkClaimJson.parseReceipt run.Out with
        | Ok receipt when run.Exit = 4 -> receipt.Diagnostics |> List.map _.Code
        | other -> failwith $"expected Dokimos to refuse (exit 4), got exit {run.Exit}: {other} {run.Err}"

    let private codes outcome =
        match outcome with
        | CompletionOutcome.Rejected(findings, _) -> findings |> List.map _.Code
        | other -> failwith $"expected a rejection, got {other}"

    let tests =
        [ t "the admitted Ordo contract supplies the authority's contract identity and requirement set" (fun () ->
              let input = boundInput ()
              Assert.equal "ecir/1" input.OrdoContractVersion
              Assert.equal validDigest input.OrdoContractDigest
              Assert.equal [ "ecir/1" ] input.SupportedOrdoContractVersions
              Assert.equal [ "requirements/domain.md#R-001"; "requirements/operations.md#R-001" ] input.RequiredRequirementIds)

          t "scenario 6: a stale Ordo contract is refused before any agent work or verification" (fun () ->
              // The host pinned the valid contract; the agent supplies the previous revision.
              match OrdoContractAdmission.admitPinned OrdoContractAdmission.implementedVersions (Some validDigest) (fixture "previous-contract.json") with
              | Error diagnostics -> Assert.isTrue (diagnostics |> List.exists (fun d -> d.Code = OrdoContractAdmission.StaleReferenceCode)) $"{diagnostics}"
              | Ok contract -> failwith $"a stale contract must be refused, got {contract.Digest}"

              // An authority pinned to another digest is a stale binding.
              let pinned = { baseInput with OrdoContractDigest = "sha256:" + String('0', 64) }

              match OrdoBoundAuthority.bind (admitted "valid-contract.json") pinned with
              | Error findings -> Assert.equal [ OrdoBoundAuthority.StaleCode ] (findings |> List.map _.Code)
              | Ok _ -> failwith "a conflicting pinned digest must be refused"

              // Required IDs that the contract does not define are refused, not trusted.
              let unknown = { baseInput with RequiredRequirementIds = [ "requirements/domain.md#R-999" ]; RequiredDecisionIds = [ "DEC-NOPE" ] }

              match OrdoBoundAuthority.bind (admitted "valid-contract.json") unknown with
              | Error findings ->
                  Assert.equal [ OrdoBoundAuthority.UnknownReferenceCode; OrdoBoundAuthority.UnknownReferenceCode ] (findings |> List.map _.Code)
              | Ok _ -> failwith "unknown contract references must be refused"

              // An unsupported contract version never reaches binding.
              match OrdoContractAdmission.admit OrdoContractAdmission.implementedVersions (fixture "unsupported-version-contract.json") with
              | Error diagnostics -> Assert.isTrue (diagnostics |> List.exists (fun d -> d.Code = OrdoContractAdmission.UnsupportedSchemaCode)) $"{diagnostics}"
              | Ok _ -> failwith "an unsupported contract version must be refused")

          t "scenario 10: independent requirements claimed concurrently by separate agents never collide (file store)" (fun () ->
              let root = CliHarness.temporaryDirectory "praxis-pipeline-concurrent"
              let events = ResizeArray()
              let claimPorts = { ports events (ref DateTimeOffset.UtcNow) with Store = FileWorkClaimStore.create root }
              let items = [ for index in 1..8 -> $"PRAXIS-PIPE-C{index}" ]
              use start = new System.Threading.Barrier(items.Length)

              let outcomes =
                  items
                  |> List.mapi (fun index id ->
                      System.Threading.Tasks.Task.Run(fun () ->
                          start.SignalAndWait()
                          id, claim claimPorts id $"agent-{index}" $"tok-{index}"))
                  |> List.map _.Result

              Assert.equal items.Length (outcomes |> List.filter (fun (id, held) -> held.WorkItemId = id && held.Generation = 1L) |> List.length)
              Assert.equal items.Length (outcomes |> List.map (fun (_, held) -> held.Holder.Value.Claimant) |> List.distinct |> List.length)
              Assert.isTrue (events |> Seq.forall (fun event -> ClaimEvent.eventType event = "work.claim.acquired")) "no rejection may occur across independent items")

          t "plain work complete is refused for claimed items until a verified completion exists" (fun () ->
              let events = ResizeArray()
              let claimPorts = ports events (ref DateTimeOffset.UnixEpoch)
              let held = claim claimPorts "PRAXIS-PIPE-1" "agent-a" "tok-a"

              match PlainCompletion.decide false held.WorkItemId (Some held) with
              | PlainCompletion.Refused finding -> Assert.equal PlainCompletion.RequiredCode finding.Code
              | PlainCompletion.Permitted -> failwith "a claimed, unverified item must not complete through the legacy path"

              Assert.equal PlainCompletion.Permitted (PlainCompletion.decide false "LEGACY-1" None)

              match PlainCompletion.decide true "LEGACY-1" None with
              | PlainCompletion.Refused finding -> Assert.equal PlainCompletion.RequiredCode finding.Code
              | PlainCompletion.Permitted -> failwith "requireVerifiedCompletion must cover unclaimed items"

              Assert.equal PlainCompletion.Permitted (PlainCompletion.decide false held.WorkItemId (Some { held with Status = ClaimStatus.Completed })))

          t "cli: work complete refuses a claimed item with PRX.COMPLETION.VERIFIED_COMPLETION_REQUIRED" (fun () ->
              let root = CliHarness.temporaryDirectory "praxis-pipeline-guard"
              CliHarness.write root ".ros/work/queue.json" """{"schemaVersion":"1.0.0","items":[{"id":"PRAXIS-PIPE-2","status":"captured"}]}"""
              let identity = [ "PRAXIS_ACTOR_KIND", "agent"; "PRAXIS_ACTOR", "test/agent-a"; "PRAXIS_TELEMETRY_PROVIDER", "test"; "PRAXIS_TELEMETRY_RUNTIME", "test" ]
              Assert.equal 0 (CliHarness.rosWith root identity [ "work"; "claim"; "--id"; "PRAXIS-PIPE-2"; "--token"; "tok-a" ]).Exit
              let refused = CliHarness.rosWith root identity [ "work"; "complete"; "--id"; "PRAXIS-PIPE-2"; "--occurred-at"; "2026-10-19T12:00:00.000Z" ]
              Assert.equal 3 refused.Exit
              Assert.isTrue (refused.Err.Contains PlainCompletion.RequiredCode) refused.Err)

          t "scenarios 1, 5, 7, 8: one requirement through the real Dokimos verifier, with telemetry" (fun () ->
              match realDokimos () with
              | None -> eprintfn "SKIP reliability pipeline: PRAXIS_TEST_DOKIMOS_DLL is not set to a built Dokimos.Cli.dll"
              | Some dokimos ->
                  let input = boundInput ()
                  let events = ResizeArray()
                  let clock = ref (DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero))
                  let claimPorts = ports events clock
                  let calls = ref 0

                  let counted packet authority =
                      calls.Value <- calls.Value + 1
                      dokimos packet authority

                  let complete = WorkClaims.completeVerified (completionPorts events claimPorts counted) input

                  // Scenario 5: a valid implementation with insufficient evidence.
                  let thin = claim claimPorts "PRAXIS-PIPE-5" "agent-a" "tok-5"
                  let missing = { request input thin (evidence claimPorts thin) with Evidence = []; Validations = [] }
                  Assert.isTrue (codes (complete missing) |> List.contains "PRX.COMPLETION.EVIDENCE_MISSING") "preflight refuses missing evidence"

                  // A required validation with no validated evidence cannot even form a
                  // packet: Praxis refuses it before Dokimos (PRAXIS-REL-005).
                  let unproven = { input with RequiredValidationNames = [ "unit-tests"; "integration-tests" ] }
                  let thinEvidence = evidence claimPorts thin
                  let before = calls.Value

                  let praxisRefusal =
                      WorkClaims.completeVerified (completionPorts events claimPorts counted) unproven (request unproven thin thinEvidence)
                      |> codes

                  Assert.equal [ CompletionGate.UnprovenValidationCode ] praxisRefusal
                  Assert.equal before calls.Value

                  // Proof Praxis can establish but the authority still requires more
                  // (a decision the packet omits) is refused by Dokimos itself.
                  let undecided = { input with RequiredDecisionIds = [ "DF-ROS-2026-A001" ] }

                  let dokimosRefusal =
                      WorkClaims.completeVerified (completionPorts events claimPorts counted) undecided (request undecided thin thinEvidence)
                      |> codes

                  Assert.isTrue (dokimosRefusal |> List.exists (fun code -> code.StartsWith "DOK.COMPLETION.")) $"Dokimos itself refuses: {dokimosRefusal}"

                  // Scenario 1: one agent completes one requirement.
                  let held = claim claimPorts "PRAXIS-PIPE-1" "agent-a" "tok-1"
                  let submission = request input held (evidence claimPorts held)

                  let verified =
                      match complete submission with
                      | CompletionOutcome.Completed(claim, completion) ->
                          Assert.equal ClaimStatus.Completed claim.Status
                          Assert.equal PlainCompletion.Permitted (PlainCompletion.decide false claim.WorkItemId (Some claim))
                          completion
                      | other -> failwith $"expected the real Dokimos to verify, got {other}"

                  // Scenario 7: a replayed completion. Praxis answers idempotently without
                  // re-verifying, and Dokimos independently refuses the consumed packet.
                  let before = calls.Value

                  match complete submission with
                  | CompletionOutcome.Replayed _ -> Assert.equal before calls.Value
                  | other -> failwith $"expected an idempotent replay, got {other}"

                  let validated = established input events held submission
                  let packet = CompletionGate.packet input submission validated
                  let replayAuthority = { CompletionGate.authority input held commit commit clock.Value validated with ConsumedPacketDigests = [ verified.PacketDigest ] }

                  match dokimos packet replayAuthority with
                  | VerifierRun.Exited(exitCode, Ok receipt, _, _) ->
                      Assert.equal 4 exitCode
                      Assert.isTrue (receipt.Diagnostics |> List.exists (fun d -> d.Code = "DOK.COMPLETION.REPLAYED_PACKET")) $"{receipt.Diagnostics}"
                  | other -> failwith $"expected Dokimos to refuse the replay, got {other}"

                  // Scenario 8: no provider usage was ingested, so token and cost ratios are
                  // unavailable while the verified-requirement count is still measured.
                  let root = CliHarness.temporaryDirectory "praxis-pipeline-telemetry"
                  let lines = events |> Seq.map (fun event -> (WorkClaimJson.eventNode "praxis-test" "1.0.0" None None event).ToJsonString())
                  let eventsPath = Path.Combine(root, "events.jsonl")
                  File.WriteAllLines(eventsPath, lines)
                  Directory.CreateDirectory(Path.Combine(root, "executions")) |> ignore
                  let out = Path.Combine(root, "report.json")
                  let run = CliHarness.ros root [ "telemetry"; "reliability-report"; "--events"; eventsPath; "--executions"; Path.Combine(root, "executions"); "--out"; out ]
                  Assert.equal 0 run.Exit
                  let report = JsonNode.Parse(File.ReadAllText out)
                  let at (path: string) = path.Split('.') |> Array.fold (fun (node: JsonNode) key -> if isNull node then null else node[key]) report
                  Assert.equal 2L (at("verification.verifiedRequirements.value").GetValue<int64>())
                  Assert.equal "unavailable" (at("usage.tokens.total.availability").GetValue<string>())
                  Assert.isTrue (isNull (at "perVerifiedRequirement.tokensTotal.value")) "tokens per verified requirement must be null, not 0"
                  Assert.equal "unavailable" (at("perVerifiedRequirement.tokensTotal.availability").GetValue<string>()))
          t "REL-GATE-001: failing, unrelated, stale and mis-bound evidence never verifies through the real Dokimos" (fun () ->
              match realDokimos () with
              | None -> eprintfn "SKIP reliability pipeline: PRAXIS_TEST_DOKIMOS_DLL is not set to a built Dokimos.Cli.dll"
              | Some dokimos ->
                  let input = boundInput ()
                  let events = ResizeArray()
                  let t0 = DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero)
                  let clock = ref t0
                  let claimPorts = ports events clock
                  let calls = ref 0

                  let counted packet authority =
                      calls.Value <- calls.Value + 1
                      dokimos packet authority

                  let complete = WorkClaims.completeVerified (completionPorts events claimPorts counted) input
                  let held = claim claimPorts "GATE-1" "agent-a" "tok-gate"

                  // Gate case 1: a file recording a failing run, produced outside any claim.
                  let failingText = plainFile "12 test(s); 7 passed; 5 failed\n"
                  Assert.equal [ ValidationEvidence.UnrecognizedCode ] (codes (complete (request input held failingText)))
                  // Gate case 2: a file that is not a test report at all.
                  Assert.equal [ ValidationEvidence.UnrecognizedCode ] (codes (complete (request input held (plainFile "not a test report\n"))))
                  // A recorded, host-observed failing run is refused too.
                  let failed = ValidationEvidenceFixtures.validateWith claimPorts commit ValidationEvidenceFixtures.failing held "unit-tests" "EV-TESTS"
                  Assert.equal [ ValidationEvidence.NotPassedCode ] (codes (complete (request input held failed)))
                  Assert.equal 0 calls.Value

                  // Defence in depth: the real Dokimos enforces what the authority now
                  // carries. Praxis cannot build a failed, stale or mis-bound
                  // observation (ValidatedEvidence), so those are edited on the wire.
                  let dll = (dokimosDll ()).Value
                  let passed = request input held (evidence claimPorts held)
                  let validated = established input events held passed
                  let packetNode () = WorkClaimJson.packetNode (CompletionGate.packet input passed validated)
                  let authorityNode () = WorkClaimJson.authorityNode (CompletionGate.authority input held commit commit clock.Value validated)

                  let refusedWith (edit: JsonNode -> unit) =
                      let authority = authorityNode ()
                      edit authority
                      dokimosOnWire dll (packetNode ()) authority

                  let firstEvidence (authority: JsonNode) = authority["Evidence"].AsArray()[0]
                  let failedCodes = refusedWith (fun a -> (firstEvidence a)["Status"] <- JsonValue.Create "failed")
                  Assert.isTrue (failedCodes |> List.contains "DOK.COMPLETION.EVIDENCE_FAILED") $"{failedCodes}"

                  let stale =
                      refusedWith (fun a ->
                          a["ClaimAcquiredAt"] <- JsonValue.Create "2026-10-19T12:01:00.000Z"
                          a["EvaluatedAt"] <- JsonValue.Create "2026-10-19T12:02:00.000Z")

                  Assert.isTrue (stale |> List.contains "DOK.COMPLETION.EVIDENCE_STALE") $"{stale}"
                  let expired = refusedWith (fun a -> a["EvaluatedAt"] <- JsonValue.Create(a["LeaseExpiresAt"].GetValue<string>()))
                  Assert.isTrue (expired |> List.contains "DOK.COMPLETION.STALE_LEASE") $"{expired}"
                  let misbound = refusedWith (fun a -> (firstEvidence a)["Validation"] <- JsonValue.Create "lint")
                  Assert.isTrue (misbound |> List.contains "DOK.COMPLETION.EVIDENCE_VALIDATION_MISMATCH") $"{misbound}"

                  // The recorded passing result for this claim verifies.
                  match complete passed with
                  | CompletionOutcome.Completed(completed, _) -> Assert.equal ClaimStatus.Completed completed.Status
                  | other -> failwith $"expected the real Dokimos to verify a recorded passing result, got {other}"

                  Assert.equal 1 calls.Value) ]
