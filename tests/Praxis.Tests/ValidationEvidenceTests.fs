namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// PRAXIS-REL-005 / REL-GATE-001: completion evidence is a host-observed
/// `praxis.validation-result/1` bound to the claim, the verification
/// revision and the lease window, recorded by `work.validation.observed`.
/// Every refusal code, the validate-run service, the wire forms and the CLI.
/// Clocks are injected; the CLI tests run real Git and real child processes.
[<RequireQualifiedAccess>]
module ValidationEvidenceTests =
    let private t name run = { Name = $"validation evidence: {name}"; Run = run }

    let private t0 = DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero)
    let private lease = TimeSpan.FromMinutes 30.0
    let private item = "PRAXIS-REL-005"
    let private commit = String('a', 40)
    let private ordoVersion = "ordo.constructibility-contract/1"

    type private Harness =
        { Ports: WorkClaimPorts
          Clock: DateTimeOffset ref
          Events: ResizeArray<ClaimEvent> }

    let private harness () =
        let clock = ref t0
        let events = ResizeArray<ClaimEvent>()

        { Clock = clock
          Events = events
          Ports =
            { Store = InMemoryWorkClaimStore.create ()
              Now = fun () -> clock.Value
              Claimability = fun _ -> Claimability.Claimable
              Emit = fun event -> lock events (fun () -> events.Add event); Ok() } }

    let private acquire (h: Harness) id token =
        match WorkClaims.acquire h.Ports { WorkItemId = id; Claimant = "agent-a"; ClaimToken = token; LeaseDuration = lease; ExpectedRevision = None; RecoverOnly = false } with
        | ClaimOutcome.Succeeded(claim, _) -> claim
        | other -> failwith $"expected a claim, got {other}"

    let private hostInput =
        { OrdoContractVersion = ordoVersion
          OrdoContractDigest = "sha256:" + String('b', 64)
          SupportedOrdoContractVersions = [ ordoVersion ]
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

    let private completion (claim: WorkClaim) (path: string) =
        { WorkItemId = claim.WorkItemId
          Fence = { Claimant = claim.Holder.Value.Claimant; ClaimToken = claim.Holder.Value.ClaimToken; Generation = claim.Generation }
          Commit = commit
          VerificationRevision = commit
          OrdoContractVersion = None
          OrdoContractDigest = None
          RequirementIds = [ item ]
          DecisionIds = []
          ConflictResolutionIds = []
          Validations = [ { Name = "unit-tests"; Status = CheckStatus.Passed; EvidenceIds = [ "EV-TESTS" ] } ]
          Evidence = [ { Id = "EV-TESTS"; Path = path; ClaimedDigest = None } ]
          TelemetryExecutions = [] }

    /// Verifies everything it is asked and records each authority it saw.
    let private accepting (seen: ResizeArray<CompletionAuthority>) : CompletionVerifier =
        fun packet authority ->
            seen.Add authority

            let receipt =
                { Contract = CompletionGate.ReceiptContract
                  SchemaVersion = CompletionGate.ReceiptSchemaVersion
                  DokimosVersion = "0.6.0-test"
                  Verdict = "verified"
                  ExitCode = 0
                  WorkItemId = packet.WorkItemId
                  Commit = packet.Commit
                  VerificationRevision = packet.VerificationRevision
                  PacketDigest = CompletionGate.packetDigest packet
                  RequirementCount = packet.RequirementIds.Length
                  EvidenceDigests = []
                  ReusedEvidenceIds = []
                  Diagnostics = [] }

            VerifierRun.Exited(0, Ok receipt, "sha256:" + String('c', 64), "")

    let private completionPorts (h: Harness) recorded verifier =
        { Claims = h.Ports
          ObserveEvidence = FileWorkClaims.observeEvidence "/"
          RecordedValidations = recorded
          ObserveHead = fun () -> Ok commit
          Verifier = verifier }

    /// Completes `request` against a verifier that would accept anything:
    /// Praxis's own refusal codes, and how often the verifier was reached.
    let private refusal (h: Harness) (request: CompletionRequest) =
        let seen = ResizeArray()

        match WorkClaims.completeVerified (completionPorts h (ValidationEvidenceFixtures.recorded h.Events) (accepting seen)) input request with
        | CompletionOutcome.Rejected(findings, _) -> findings |> List.map _.Code |> List.distinct, seen.Count
        | other -> failwith $"expected a refusal, got {other}"

    let private run (h: Harness) (command: CommandRun) (claim: WorkClaim) validation evidenceId =
        ValidationEvidenceFixtures.validateWith h.Ports commit command claim validation evidenceId

    /// The JSON value at `path` in a command's stdout document.
    let private at (path: string list) (text: string) =
        path |> List.fold (fun (node: JsonNode) (key: string) -> node[key]) (CliHarness.json text)

    let private identity = [ "PRAXIS_ACTOR_KIND", "agent"; "PRAXIS_ACTOR", "test/agent-a"; "PRAXIS_TELEMETRY_PROVIDER", "test"; "PRAXIS_TELEMETRY_RUNTIME", "test" ]

    let tests =
        [ t "a recorded passing result completes, and the authority carries the result and freshness members" (fun () ->
              let h = harness ()
              let claim = acquire h item "tok-a"
              let path = ValidationEvidenceFixtures.validate h.Ports commit claim
              h.Clock.Value <- t0 + TimeSpan.FromMinutes 5.0
              let seen = ResizeArray()

              match WorkClaims.completeVerified (completionPorts h (ValidationEvidenceFixtures.recorded h.Events) (accepting seen)) input (completion claim path) with
              | CompletionOutcome.Completed(done', _) -> Assert.equal ClaimStatus.Completed done'.Status
              | other -> failwith $"expected completion, got {other}"

              let authority = seen |> Seq.exactlyOne
              Assert.equal (Some t0) authority.ClaimAcquiredAt
              Assert.equal (Some(t0 + lease)) authority.LeaseExpiresAt
              Assert.equal (Some(t0 + TimeSpan.FromMinutes 5.0)) authority.EvaluatedAt
              let observed = authority.Evidence |> List.exactlyOne
              Assert.equal (CheckStatus.Passed, commit, t0, "unit-tests", false) (observed.Status, observed.Revision, observed.ObservedAt, observed.Validation, observed.Reusable)
              let wire = WorkClaimJson.authorityNode authority
              Assert.equal "2026-10-19T12:00:00.000Z" (wire["ClaimAcquiredAt"].GetValue<string>())
              Assert.equal "2026-10-19T12:30:00.000Z" (wire["LeaseExpiresAt"].GetValue<string>())
              Assert.equal "2026-10-19T12:05:00.000Z" (wire["EvaluatedAt"].GetValue<string>())
              let evidence = wire["Evidence"].AsArray()[0]
              Assert.equal "2026-10-19T12:00:00.000Z" (evidence["ObservedAt"].GetValue<string>())
              Assert.equal "unit-tests" (evidence["Validation"].GetValue<string>())
              // Unset optional members are omitted, never null (Dokimos refuses null).
              let bare = WorkClaimJson.authorityNode { authority with ClaimAcquiredAt = None; LeaseExpiresAt = None; EvaluatedAt = None }
              Assert.isTrue (not (bare.AsObject().ContainsKey "ClaimAcquiredAt")) "absent freshness members are omitted")

          t "REL-GATE-001 reproductions are refused: a failing-run text file and an unrelated file (EVIDENCE_UNRECOGNIZED)" (fun () ->
              let h = harness ()
              let claim = acquire h item "tok-a"
              let root = CliHarness.temporaryDirectory "praxis-gate-evidence"
              let failing = Path.Combine(root, "failing.txt")
              let unrelated = Path.Combine(root, "unrelated.txt")
              File.WriteAllText(failing, "12 test(s); 7 passed; 5 failed\n")
              File.WriteAllText(unrelated, "not a test report\n")
              Assert.equal ([ ValidationEvidence.UnrecognizedCode ], 0) (refusal h (completion claim failing))
              Assert.equal ([ ValidationEvidence.UnrecognizedCode ], 0) (refusal h (completion claim unrelated))
              // A result document missing a member is not a result either.
              let real = File.ReadAllText(ValidationEvidenceFixtures.validate h.Ports commit claim)
              let truncated = Path.Combine(root, "truncated.json")
              File.WriteAllText(truncated, real.Replace("\"outputSha256\"", "\"outputDigest\""))
              Assert.equal ([ ValidationEvidence.UnrecognizedCode ], 0) (refusal h (completion claim truncated)))

          t "a recorded failing or unavailable run is refused (EVIDENCE_NOT_PASSED)" (fun () ->
              let h = harness ()
              let claim = acquire h item "tok-a"
              let failed = run h ValidationEvidenceFixtures.failing claim "unit-tests" "EV-TESTS"
              Assert.equal ([ ValidationEvidence.NotPassedCode ], 0) (refusal h (completion claim failed))
              let timedOut = run h (CommandRun.TimedOut(TimeSpan.FromSeconds 5.0)) claim "unit-tests" "EV-TESTS"
              Assert.equal ([ ValidationEvidence.NotPassedCode ], 0) (refusal h (completion claim timedOut)))

          t "a well-formed result no work.validation.observed event recorded is refused (EVIDENCE_UNRECORDED)" (fun () ->
              let h = harness ()
              let claim = acquire h item "tok-a"
              let root = CliHarness.temporaryDirectory "praxis-forged-result"

              let forged =
                  { WorkItemId = item; Claimant = "agent-a"; ClaimToken = "tok-a"; Generation = 1L; Validation = "unit-tests"; EvidenceId = "EV-TESTS"
                    Revision = commit; ObservedAt = t0; StartedAt = t0; Status = CheckStatus.Passed; ExitCode = Some 0; Command = [ "true" ]
                    OutputSha256 = "sha256:" + String('d', 64) }

              let written = FileValidationRuns.record root forged "" |> Result.defaultWith failwith
              let path = Path.Combine(root, written.ResultPath)
              Assert.equal ([ ValidationEvidence.UnrecordedCode ], 0) (refusal h (completion claim path))
              // Editing a recorded result changes its digest, so it is no longer recorded.
              let recordedPath = ValidationEvidenceFixtures.validate h.Ports commit claim
              File.WriteAllText(recordedPath, File.ReadAllText(recordedPath).Replace("\"dotnet\"", "\"dotnet \""))
              Assert.equal ([ ValidationEvidence.UnrecordedCode ], 0) (refusal h (completion claim recordedPath))
              // An unreadable event log fails closed.
              let honest = ValidationEvidenceFixtures.validate h.Ports commit claim
              let seen = ResizeArray()
              let ports = completionPorts h (fun _ -> Error "disk on fire") (accepting seen)

              match WorkClaims.completeVerified ports input (completion claim honest) with
              | CompletionOutcome.Rejected(findings, _) -> Assert.equal [ ValidationEvidence.UnrecordedCode ] (findings |> List.map _.Code)
              | other -> failwith $"expected a refusal, got {other}"

              Assert.equal 0 seen.Count)

          t "a result from another work item or an earlier generation is refused (EVIDENCE_FOREIGN_CLAIM)" (fun () ->
              let h = harness ()
              let other = acquire h "PRAXIS-REL-OTHER" "tok-o"
              let foreign = ValidationEvidenceFixtures.validate h.Ports commit other
              let claim = acquire h item "tok-a"
              let codes, calls = refusal h (completion claim foreign)
              Assert.isTrue (codes |> List.contains ValidationEvidence.ForeignClaimCode) $"{codes}"
              Assert.equal 0 calls
              // Same item, same agent, but the result predates a recovery at generation 2.
              let earlier = ValidationEvidenceFixtures.validate h.Ports commit claim
              h.Clock.Value <- t0 + lease
              let recovered = acquire h item "tok-a2"
              Assert.equal 2L recovered.Generation
              let codes, _ = refusal h (completion recovered earlier)
              Assert.isTrue (codes |> List.contains ValidationEvidence.ForeignClaimCode) $"{codes}")

          t "a result run at another revision is refused (EVIDENCE_REVISION_MISMATCH)" (fun () ->
              let h = harness ()
              let claim = acquire h item "tok-a"
              let elsewhere = ValidationEvidenceFixtures.validateWith h.Ports (String('9', 40)) ValidationEvidenceFixtures.passing claim "unit-tests" "EV-TESTS"
              Assert.equal ([ ValidationEvidence.RevisionMismatchCode ], 0) (refusal h (completion claim elsewhere)))

          t "a result observed before acquisition or at/after lease expiry is refused (EVIDENCE_STALE)" (fun () ->
              let h = harness ()
              let claim = acquire h item "tok-a"
              // The injected run moves the clock, so the observation lands outside the window.
              let root = CliHarness.temporaryDirectory "praxis-stale"

              let at (instant: DateTimeOffset) =
                  { ValidationEvidenceFixtures.runPorts h.Ports root commit ValidationEvidenceFixtures.passing with
                      RunCommand =
                          fun _ _ ->
                              h.Clock.Value <- instant
                              ValidationEvidenceFixtures.passing }

              let absolute (instant: DateTimeOffset) =
                  match ValidationRuns.run (at instant) (ValidationEvidenceFixtures.runRequest claim "unit-tests" "EV-TESTS") with
                  | ValidationRunOutcome.Observed(_, written) ->
                      h.Clock.Value <- t0 + TimeSpan.FromMinutes 1.0
                      Path.Combine(root, written.ResultPath)
                  | other -> failwith $"expected an observed run, got {other}"

              let before = absolute (t0 - TimeSpan.FromSeconds 1.0)
              Assert.equal ([ ValidationEvidence.StaleCode ], 0) (refusal h (completion claim before))
              let after = absolute (t0 + lease)
              Assert.equal ([ ValidationEvidence.StaleCode ], 0) (refusal h (completion claim after)))

          t "a result cited for another validation or evidence ID is refused (EVIDENCE_VALIDATION_MISMATCH)" (fun () ->
              let h = harness ()
              let claim = acquire h item "tok-a"
              let lint = run h ValidationEvidenceFixtures.passing claim "lint" "EV-TESTS"
              Assert.equal ([ ValidationEvidence.ValidationMismatchCode ], 0) (refusal h (completion claim lint))
              let otherEvidence = run h ValidationEvidenceFixtures.passing claim "unit-tests" "EV-OTHER"
              Assert.equal ([ ValidationEvidence.ValidationMismatchCode ], 0) (refusal h (completion claim otherEvidence)))

          t "a required validation with no established evidence cannot form a packet (VALIDATION_UNPROVEN)" (fun () ->
              let h = harness ()
              let claim = acquire h item "tok-a"
              let path = ValidationEvidenceFixtures.validate h.Ports commit claim
              let strict = OrdoTestContracts.bind { hostInput with RequiredValidationNames = [ "unit-tests"; "integration-tests" ] }
              let seen = ResizeArray()

              match WorkClaims.completeVerified (completionPorts h (ValidationEvidenceFixtures.recorded h.Events) (accepting seen)) strict (completion claim path) with
              | CompletionOutcome.Rejected(findings, _) -> Assert.equal [ CompletionGate.UnprovenValidationCode ] (findings |> List.map _.Code)
              | other -> failwith $"expected a refusal, got {other}"

              Assert.equal 0 seen.Count)

          t "invariant: ValidatedEvidence has no public constructor and establish yields only passed, bound results" (fun () ->
              // Compile-time guarantee, checked here at runtime as a tripwire: the
              // only way to obtain ValidatedEvidence is ValidatedEvidence.establish.
              let kind = typeof<ValidatedEvidence>
              Assert.equal 0 (kind.GetConstructors(Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Instance)).Length
              Assert.isTrue kind.IsSealed "ValidatedEvidence is sealed"
              // CompletionAuthority evidence is ValidatedEvidence, not a raw observation record.
              Assert.equal typeof<ValidatedEvidence list> (typeof<CompletionAuthority>.GetProperty("Evidence").PropertyType)
              let h = harness ()
              let claim = acquire h item "tok-a"
              let fence = { Claimant = "agent-a"; ClaimToken = "tok-a"; Generation = 1L }

              let result status =
                  { WorkItemId = item; Claimant = "agent-a"; ClaimToken = "tok-a"; Generation = 1L; Validation = "unit-tests"; EvidenceId = "EV-TESTS"
                    Revision = commit; ObservedAt = t0; StartedAt = t0; Status = status; ExitCode = Some 0; Command = [ "true" ]
                    OutputSha256 = "sha256:" + String('d', 64) }

              let recorded = Ok [ { RecordedValidation.WorkItemId = item; ClaimToken = "tok-a"; Generation = 1L; ResultDigest = "sha256:x" } ]
              let establish status = ValidatedEvidence.establish item fence claim.Holder commit recorded "EV-TESTS" [ "unit-tests" ] "sha256:x" (result status)

              match establish CheckStatus.Passed with
              | Ok evidence -> Assert.equal (CheckStatus.Passed, "sha256:x") (evidence.Status, evidence.Digest)
              | Error findings -> failwith $"a recorded, bound, passed result must establish: {findings}"

              for status in [ CheckStatus.Failed; CheckStatus.Unavailable ] do
                  match establish status with
                  | Error findings -> Assert.equal [ ValidationEvidence.NotPassedCode ] (findings |> List.map _.Code)
                  | Ok _ -> failwith "a non-passing result must never establish")

          t "validate-run is fenced, refuses a dirty worktree and invalid names, and runs nothing then" (fun () ->
              let h = harness ()
              let claim = acquire h item "tok-a"
              let calls = ref 0
              let root = CliHarness.temporaryDirectory "praxis-validate-fence"

              let ports dirty =
                  { ValidationEvidenceFixtures.runPorts h.Ports root commit ValidationEvidenceFixtures.passing with
                      DirtyPaths = fun () -> Ok dirty
                      RunCommand = fun _ _ -> calls.Value <- calls.Value + 1; ValidationEvidenceFixtures.passing }

              let request = ValidationEvidenceFixtures.runRequest claim "unit-tests" "EV-TESTS"

              let code outcome =
                  match outcome with
                  | ValidationRunOutcome.Refused(finding, _) -> finding.Code
                  | other -> failwith $"expected a refusal, got {other}"

              Assert.equal "PRX.CLAIM.STALE_TOKEN" (ValidationRuns.run (ports []) { request with Fence = { request.Fence with ClaimToken = "stolen" } } |> code)
              Assert.equal "PRX.CLAIM.STALE_GENERATION" (ValidationRuns.run (ports []) { request with Fence = { request.Fence with Generation = 7L } } |> code)
              Assert.equal ValidationEvidence.DirtyWorktreeCode (ValidationRuns.run (ports [ "src/Program.fs" ]) request |> code)
              Assert.equal "PRX.CLAIM.INVALID_REQUEST" (ValidationRuns.run (ports []) { request with EvidenceId = "../escape" } |> code)
              Assert.equal "PRX.CLAIM.INVALID_REQUEST" (ValidationRuns.run (ports []) { request with Command = [] } |> code)
              h.Clock.Value <- t0 + lease
              Assert.equal "PRX.CLAIM.EXPIRED" (ValidationRuns.run (ports []) request |> code)
              Assert.equal 0 calls.Value
              Assert.isTrue (h.Events |> Seq.forall (fun event -> ClaimEvent.eventType event <> ValidationEvidence.ObservedEventType)) "no refused run is recorded")

          t "validate-run writes a deterministic result whose recorded digest is the digest of its bytes" (fun () ->
              let h = harness ()
              let claim = acquire h item "tok-a"
              let root = CliHarness.temporaryDirectory "praxis-validate-record"
              let ports = ValidationEvidenceFixtures.runPorts h.Ports root commit (CommandRun.Exited(0, "ok\n"))

              match ValidationRuns.run ports (ValidationEvidenceFixtures.runRequest claim "unit-tests" "EV-TESTS") with
              | ValidationRunOutcome.Observed(result, written) ->
                  // I8: results live in the out-of-tree store, never in the tracked tree.
                  Assert.equal (Path.Combine(TransientStore.validations root, item, "EV-TESTS", "1-aaaaaaaaaaaa-20261019T120000000Z.json")) written.ResultPath
                  let bytes = File.ReadAllBytes(Path.Combine(root, written.ResultPath))
                  Assert.equal (CompletionGate.sha256 bytes) written.ResultDigest
                  Assert.equal (CompletionGate.sha256 (File.ReadAllBytes(Path.Combine(root, written.OutputPath)))) result.OutputSha256
                  Assert.equal (Ok result) (ValidationResultJson.parse (Text.Encoding.UTF8.GetString bytes))
                  Assert.equal (ValidationResultJson.render result) (Text.Encoding.UTF8.GetString bytes)
                  let members = (JsonNode.Parse(Text.Encoding.UTF8.GetString bytes)).AsObject() |> Seq.map _.Key |> Seq.toList

                  Assert.equal
                      [ "schemaVersion"; "workItemId"; "claimant"; "claimToken"; "generation"; "validation"; "evidenceId"; "revision"; "observedAt"
                        "startedAt"; "status"; "exitCode"; "command"; "outputSha256" ]
                      members

                  match h.Events |> Seq.last |> _.Payload with
                  | ClaimEventPayload.ValidationObserved(_, token, generation, validation, evidence, revision, status, _, digest) ->
                      Assert.equal ("tok-a", 1L, "unit-tests", "EV-TESTS", commit, CheckStatus.Passed, written.ResultDigest)
                          (token, generation, validation, evidence, revision, status, digest)
                  | other -> failwith $"expected work.validation.observed, got {other}"
              | other -> failwith $"expected an observed run, got {other}"

              // Result files are never overwritten, so a second run at the same instant fails rather than replacing the first.
              match ValidationRuns.run ports (ValidationEvidenceFixtures.runRequest claim "unit-tests" "EV-TESTS") with
              | ValidationRunOutcome.Failed reason -> Assert.isTrue (reason.Contains "already exists") reason
              | other -> failwith $"expected a refused overwrite, got {other}"

              h.Clock.Value <- t0 + TimeSpan.FromSeconds 1.0
              let unavailable = ValidationEvidenceFixtures.runPorts h.Ports root commit (CommandRun.NotStarted "no such file")

              match ValidationRuns.run unavailable (ValidationEvidenceFixtures.runRequest claim "unit-tests" "EV-TESTS") with
              | ValidationRunOutcome.Observed(result, _) -> Assert.equal (CheckStatus.Unavailable, None) (result.Status, result.ExitCode)
              | other -> failwith $"expected an unavailable run to be recorded, got {other}")

          t "the event-log reader returns only this item's work.validation.observed events" (fun () ->
              let root = CliHarness.temporaryDirectory "praxis-validation-events"
              let emit = FileWorkClaimEvents.create root None None
              let observed id digest = { WorkItemId = id; OccurredAt = t0; Payload = ClaimEventPayload.ValidationObserved("a", "t", 1L, "unit-tests", "EV-TESTS", commit, CheckStatus.Passed, t0, digest) }
              Assert.equal (Ok()) (emit (observed item "sha256:1"))
              Assert.equal (Ok()) (emit (observed "PRAXIS-REL-OTHER" "sha256:2"))
              Assert.equal (Ok()) (emit { WorkItemId = item; OccurredAt = t0; Payload = ClaimEventPayload.ClaimReleased("a", "t", 1L) })
              Assert.equal (Ok [ { RecordedValidation.WorkItemId = item; ClaimToken = "t"; Generation = 1L; ResultDigest = "sha256:1" } ]) (FileValidationEvents.read root item)
              Assert.equal (Ok []) (FileValidationEvents.read (CliHarness.temporaryDirectory "praxis-no-events") item))

          t "cli: validate-run with true, false and a dirty worktree in a real Git repository" (fun () ->
              let root = CliHarness.temporaryDirectory "praxis-validate-cli"
              CliHarness.git root [ "init"; "-q" ] |> ignore
              CliHarness.write root ".ros/work/queue.json" $"{{\"schemaVersion\":\"1.0.0\",\"items\":[{{\"id\":\"{item}\",\"status\":\"captured\"}}]}}"
              CliHarness.write root "src/code.txt" "v1\n"
              CliHarness.commitAll root "fixture"
              let head = CliHarness.git root [ "rev-parse"; "HEAD" ]
              Assert.equal 0 (CliHarness.rosWith root identity [ "work"; "claim"; "--id"; item; "--token"; "tok-a" ]).Exit
              let fence = [ "work"; "validate-run"; "--id"; item; "--token"; "tok-a"; "--generation"; "1"; "--validation"; "unit-tests"; "--evidence-id"; "EV-TESTS" ]
              let validate command = CliHarness.rosWith root identity (fence @ ("--" :: command))
              // Options after `--` belong to the command, not to Praxis.
              let passed = validate [ "true"; "--store"; "nfs" ]
              Assert.equal 0 passed.Exit
              let document = CliHarness.json passed.Out
              Assert.equal "passed" (document["status"].GetValue<string>())
              Assert.equal head ((at [ "result"; "revision" ] passed.Out).GetValue<string>())
              let resultPath = document["resultPath"].GetValue<string>()
              Assert.isTrue (resultPath.StartsWith(Path.Combine(root, ".git", "praxis", "validations", item, "EV-TESTS", $"1-{head.Substring(0, 12)}-"))) resultPath
              Assert.equal "" (CliHarness.git root [ "status"; "--porcelain" ])
              let digest = CompletionGate.sha256 (File.ReadAllBytes(Path.Combine(root, resultPath)))
              Assert.equal digest (document["resultDigest"].GetValue<string>())
              let events = File.ReadAllText(TransientStore.events root)
              Assert.isTrue (events.Contains "\"type\":\"work.validation.observed\"" && events.Contains digest) events
              let failed = validate [ "false" ]
              Assert.equal 3 failed.Exit
              Assert.equal "failed" ((at [ "status" ] failed.Out).GetValue<string>())
              Assert.equal 1 ((at [ "result"; "exitCode" ] failed.Out).GetValue<int>())
              CliHarness.write root "src/code.txt" "v2\n"
              let dirty = validate [ "true" ]
              Assert.equal 3 dirty.Exit
              Assert.equal ValidationEvidence.DirtyWorktreeCode ((at [ "code" ] dirty.Out).GetValue<string>())
              CliHarness.git root [ "checkout"; "-q"; "--"; "src/code.txt" ] |> ignore
              Assert.equal 2 (CliHarness.rosWith root identity fence).Exit
              let stale = CliHarness.rosWith root identity ([ "work"; "validate-run"; "--id"; item; "--token"; "nope"; "--generation"; "1"; "--validation"; "unit-tests"; "--evidence-id"; "EV-TESTS"; "--"; "true" ])
              Assert.equal 3 stale.Exit
              Assert.equal "PRX.CLAIM.STALE_TOKEN" ((at [ "code" ] stale.Out).GetValue<string>())

              // With a built Dokimos, the recorded passing result verifies end to end.
              match Environment.GetEnvironmentVariable "PRAXIS_TEST_DOKIMOS_DLL" |> Option.ofObj |> Option.filter File.Exists with
              | None -> eprintfn "SKIP validation evidence cli: PRAXIS_TEST_DOKIMOS_DLL is not set to a built Dokimos.Cli.dll"
              | Some dll ->
                  // The ready contract, not the authority, supplies the contract identity (REL-GATE-002).
                  // Host inputs live outside the candidate tree: a completion needs a clean worktree (I8).
                  let host = CliHarness.temporaryDirectory "praxis-validate-host"
                  CliHarness.write host "contract.json" (OrdoTestContracts.contract [ item ] [])
                  CliHarness.write host "authority.json" $"{{\"OrdoContractVersion\":\"\",\"OrdoContractDigest\":\"\",\"SupportedOrdoContractVersions\":[\"ecir/1\"],\"RequiredRequirementIds\":[\"{item}\"],\"RequiredValidationNames\":[\"unit-tests\"],\"RequiredEvidenceIds\":[\"EV-TESTS\"]}}"

                  let complete evidence =
                      CliHarness.rosWith root (("PRAXIS_DOKIMOS_EXECUTABLE", dll) :: identity)
                          [ "work"; "complete-verified"; "--id"; item; "--token"; "tok-a"; "--generation"; "1"; "--commit"; head; "--verification-revision"; head
                            "--authority"; Path.Combine(host, "authority.json"); "--ordo-contract"; Path.Combine(host, "contract.json"); "--requirement"; item
                            "--validation"; "unit-tests=passed:EV-TESTS"; "--evidence"; $"EV-TESTS={evidence}" ]

                  let failedPath = (at [ "resultPath" ] failed.Out).GetValue<string>()
                  let refused = complete failedPath
                  Assert.equal 3 refused.Exit
                  Assert.isTrue (refused.Out.Contains ValidationEvidence.NotPassedCode) refused.Out
                  let verified = complete resultPath
                  Assert.equal 0 verified.Exit
                  Assert.equal "verified" ((at [ "outcome" ] verified.Out).GetValue<string>())) ]
