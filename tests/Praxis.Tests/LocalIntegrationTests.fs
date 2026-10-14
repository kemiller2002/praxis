namespace Praxis.Tests

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module LocalIntegrationTests =
    let private t name run = { Name = "local integration: " + name; Run = run }
    let private packet = LocalAgentHandoffTests.packet
    let private candidate = String('c', 40)
    let private target = "refs/heads/integration"
    let private now = packet.IssuedAt.AddMinutes 5.
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private rejects value = Assert.isTrue (Result.isError value) "unsafe integration succeeded"
    let private run ports = LocalIntegration.run ports packet.RepositoryIdentity packet.DispatchId target
    let private load (store: LocalIntegrationJournalStore) = store.Load packet.RepositoryIdentity packet.DispatchId |> ok |> Option.get
    let internal fixtureWith p result observed test =
        let root = CliHarness.temporaryDirectory "praxis-local-integration"
        let repo, journalRoot, archiveRoot, host = Path.Combine(root, "repo"), Path.Combine(root, "dispatch"), Path.Combine(root, "archive"), Path.Combine(root, "host")
        for directory in [ repo; journalRoot; archiveRoot; host ] do Directory.CreateDirectory directory |> ignore
        try
            let now = if p.IssuedAt = packet.IssuedAt then now else p.IssuedAt.AddSeconds 1.
            let dispatch = FileLocalDispatchJournal.create repo journalRoot |> ok
            dispatch.Reserve { Packet = p; ReservedAt = now; ControllerSessionId = "fixture-controller" } |> ok |> ignore
            for sequence, kind, id in [ 1, LocalDispatchEventKind.LaunchIntent, None; 2, LocalDispatchEventKind.LaunchObserved, Some "fixture-process"; 3, LocalDispatchEventKind.ExitObserved, Some "fixture-process" ] do
                dispatch.Append p.RepositoryIdentity p.DispatchId { Sequence = sequence; At = now; Kind = kind; ProcessIdentity = id; Detail = "fixture host observation" } |> ok
            let archive = FileLocalWorkerSubmissions.create repo archiveRoot |> ok
            let payload = LocalAgentHandoffJson.renderResult result
            archive.Save { SchemaVersion = LocalWorkerSubmission.Schema; RepositoryIdentity = p.RepositoryIdentity; DispatchId = p.DispatchId; AttemptId = p.AttemptId
                           PacketDigest = LocalAgentHandoff.packetDigest p; ProcessIdentity = "fixture-process"; CapturedAt = now; ExitCode = 0
                           Payload = payload; PayloadDigest = LocalWorkerSubmission.digest (Encoding.UTF8.GetBytes payload) } |> ok |> ignore
            let journal = FileLocalIntegrationJournal.create repo host |> ok
            let head, swaps, clock, enabled = ref p.SourceCommit, ref 0, ref now, ref true
            let intake : LocalSubmissionIntakePorts =
                { Journal = dispatch; Submissions = archive
                  NativeRecovery = fun () -> Ok()
                  ProcessRecovery = fun _ -> Ok()
                  Authority = fun () -> Ok { Enabled = enabled.Value; Revision = p.AuthorityRevision; ExpectedPacket = p }
                  Prerequisites = fun () -> Ok(p.Prerequisites |> List.map (fun item -> item.WorkItemId, item.EvidenceDigest) |> Map.ofList)
                  Observe = fun _ _ -> Ok observed
                  Checklists = WorkerChecklistTests.ports p result (fun () -> clock.Value)
                  Now = fun () -> clock.Value }
            let compare (plan: LocalIntegrationPlan) =
                swaps.Value <- swaps.Value + 1
                if head.Value <> plan.BeforeCommit then Error "expected head changed"
                else head.Value <- plan.CandidateCommit; Ok()
            let ports : LocalIntegrationPorts =
                { Journal = journal; Intake = intake; Acquire = FileLocalIntegrationJournal.acquire repo host
                  ReadTarget = fun _ -> Ok head.Value
                  Stage = fun _ _ _ -> Ok candidate
                  ValidateCandidate =
                      fun snapshot plan ->
                          if snapshot.Result = result && plan.CandidateCommit = candidate then Ok() else Error "fixture candidate pin differs"
                  CompareAndSwap = compare }
            test repo host journal ports head swaps clock enabled
        finally Directory.Delete(root, true)
    let internal fixture test = fixtureWith packet LocalAgentHandoffTests.result LocalAgentHandoffTests.observed test

    let tests =
        [ t "successful source checks cannot substitute for checks on the staged integration candidate" (fun () -> fixture (fun _ _ journal ports head swaps _ _ ->
              let original = head.Value
              let baseline = ports.Intake.Checklists
              let mutable candidates = []
              let checks =
                  { baseline with
                      Evaluate = fun checklist ->
                          candidates <- checklist.Context.CandidateCommit :: candidates
                          if checklist.Context.CandidateCommit = candidate then Ok [] else baseline.Evaluate checklist }
              rejects (run { ports with Intake = { ports.Intake with Checklists = checks } })
              Assert.isTrue (List.contains candidate candidates) "staged candidate was not checked"
              Assert.equal original head.Value
              Assert.equal 0 swaps.Value
              Assert.equal None (journal.Load packet.RepositoryIdentity packet.DispatchId |> ok)))
          t "prepared recovery rechecks the candidate checklist before updating target" (fun () -> fixture (fun _ _ journal ports head swaps _ _ ->
              rejects (run { ports with CompareAndSwap = fun _ -> Error "interrupted before target update" })
              Assert.equal None (load journal).AppliedAt
              let original = head.Value
              let baseline = ports.Intake.Checklists
              let checks = { baseline with Evaluate = fun checklist -> if checklist.Context.CandidateCommit = candidate then Ok [] else baseline.Evaluate checklist }
              rejects (run { ports with Intake = { ports.Intake with Checklists = checks } })
              Assert.equal original head.Value
              Assert.equal 0 swaps.Value))
          t "policy drift during the final source refresh invalidates earlier candidate checks" (fun () -> fixture (fun _ _ _ ports head swaps _ _ ->
              let original = head.Value
              let baseline = ports.Intake.Checklists
              let mutable candidateChecked = false
              let mutable changed = false
              let checklist =
                  { baseline with
                      Observe = fun p r commit -> baseline.Observe p r commit |> Result.map (fun context ->
                          if changed then { context with Policy = { context.Policy with Revision = "new-policy" } } else context)
                      Evaluate = fun checklist ->
                          if checklist.Context.CandidateCommit = candidate then candidateChecked <- true
                          elif candidateChecked then changed <- true
                          baseline.Evaluate checklist }
              rejects (run { ports with Intake = { ports.Intake with Checklists = checklist } })
              Assert.equal original head.Value
              Assert.equal 0 swaps.Value))
          t "prepared plan precedes target update and an applied target is never updated twice" (fun () -> fixture (fun repo host journal ports head swaps clock _ ->
              let apply plan =
                  Assert.equal plan (load journal).Plan
                  Assert.equal None (load journal).AppliedAt
                  ports.CompareAndSwap plan
              Assert.equal (Ok LocalIntegrationDisposition.TargetApplied) (run { ports with CompareAndSwap = apply })
              Assert.equal candidate head.Value
              Assert.equal 1 swaps.Value
              clock.Value <- clock.Value.AddSeconds 1.
              let reopened = FileLocalIntegrationJournal.create repo host |> ok
              Assert.equal (Ok LocalIntegrationDisposition.TargetAlreadyObserved) (run { ports with Journal = reopened })
              Assert.equal (Some now) (load reopened).AppliedAt
              Assert.equal 1 swaps.Value))
          t "failure before target update retains a prepared transaction and revalidates on restart" (fun () -> fixture (fun _ _ journal ports _ swaps _ _ ->
              rejects (run { ports with CompareAndSwap = fun _ -> Error "update refused" })
              Assert.equal None (load journal).AppliedAt
              let mutable checks = 0
              let restarted = { ports with Stage = fun _ _ _ -> failwith "recovery restaged immutable candidate"
                                           ValidateCandidate = fun snapshot plan -> checks <- checks + 1; ports.ValidateCandidate snapshot plan }
              Assert.equal (Ok LocalIntegrationDisposition.TargetApplied) (run restarted)
              Assert.isTrue (checks > 0) "candidate was not freshly checked"
              Assert.equal 1 swaps.Value))
          t "lost target acknowledgement closes observation after expiry without applying again" (fun () -> fixture (fun _ _ journal ports _ swaps clock enabled ->
              let apply plan = ports.CompareAndSwap plan |> ok; Error "target acknowledgement lost"
              rejects (run { ports with CompareAndSwap = apply })
              Assert.equal None (load journal).AppliedAt
              clock.Value <- packet.ExpiresAt.AddMinutes 1.
              enabled.Value <- false
              let recovery = { ports with Stage = fun _ _ _ -> failwith "expired transaction restaged"
                                          ValidateCandidate = fun _ _ -> failwith "historical observation granted new authority"
                                          CompareAndSwap = fun _ -> failwith "observed applied target applied twice" }
              Assert.equal (Ok LocalIntegrationDisposition.TargetApplied) (run recovery)
              Assert.equal 1 swaps.Value))
          t "receipt-write failures before or after persistence require recovery without duplicate update" (fun () ->
              for persisted in [ false; true ] do fixture (fun _ _ journal ports _ swaps _ _ ->
                  let record plan at =
                      if persisted then journal.RecordApplied plan at |> ok
                      Error "applied receipt flush uncertain"
                  rejects (run { ports with Journal = { journal with RecordApplied = record } })
                  Assert.equal 1 swaps.Value
                  run ports |> ok |> ignore
                  Assert.equal 1 swaps.Value
                  Assert.isTrue (load journal).AppliedAt.IsSome "recovery did not close tracking"))
          t "prepare failures never apply even when plan bytes became readable" (fun () ->
              for persisted in [ false; true ] do fixture (fun _ _ journal ports _ swaps _ _ ->
                  let prepare plan =
                      if persisted then journal.Prepare plan |> ok |> ignore
                      Error "plan flush uncertain"
                  rejects (run { ports with Journal = { journal with Prepare = prepare } })
                  Assert.equal 0 swaps.Value
                  Assert.equal (Ok LocalIntegrationDisposition.TargetApplied) (run ports)
                  Assert.equal 1 swaps.Value))
          t "revocation during candidate validation or preparation blocks every new target update" (fun () ->
              for duringPrepare in [ false; true ] do fixture (fun _ _ journal ports _ swaps _ enabled ->
                  let denied =
                      if duringPrepare then
                          { ports with Journal = { journal with Prepare = fun plan -> let result = journal.Prepare plan in enabled.Value <- false; result } }
                      else { ports with ValidateCandidate = fun _ _ -> enabled.Value <- false; Ok() }
                  rejects (run denied)
                  Assert.equal 0 swaps.Value
                  rejects (run ports)
                  Assert.equal 0 swaps.Value))
          t "expiry during candidate validation or pending recovery prevents new target mutation" (fun () -> fixture (fun _ _ _ ports _ swaps clock _ ->
              rejects (run { ports with CompareAndSwap = fun _ -> Error "interrupted before update" })
              clock.Value <- packet.ExpiresAt
              rejects (run ports)
              Assert.equal 0 swaps.Value))
          t "target drift during staging, validation or CAS remains refused and cannot overwrite" (fun () ->
              for phase in [ "stage"; "validate"; "cas" ] do fixture (fun _ _ _ ports head swaps _ _ ->
                  let changed = String('d', 40)
                  let drift =
                      match phase with
                      | "stage" -> { ports with Stage = fun _ _ _ -> head.Value <- changed; Ok candidate }
                      | "validate" -> { ports with ValidateCandidate = fun _ _ -> head.Value <- changed; Ok() }
                      | _ -> { ports with CompareAndSwap = fun plan -> head.Value <- changed; ports.CompareAndSwap plan }
                  rejects (run drift)
                  Assert.equal changed head.Value
                  rejects (run ports)
                  Assert.isTrue (swaps.Value <= 1) "diverged transaction retried target mutation"))
          t "changed immutable submission metadata is detected before pending integration resumes" (fun () -> fixture (fun _ _ _ ports _ swaps clock _ ->
              rejects (run { ports with CompareAndSwap = fun _ -> Error "interrupted" })
              clock.Value <- clock.Value.AddSeconds 1.
              let archive = ports.Intake.Submissions
              let changed = { archive with Load = fun repo dispatch -> archive.Load repo dispatch |> Result.map (fun record -> { record with CapturedAt = clock.Value }) }
              rejects (run { ports with Intake = { ports.Intake with Submissions = changed } })
              Assert.equal 0 swaps.Value))
          t "partial member results never stage or integrate" (fun () ->
              let result = { LocalAgentHandoffTests.result with Members = LocalAgentHandoffTests.result.Members |> List.map (fun m -> { m with Outcome = LocalWorkerOutcome.Blocked }) }
              fixtureWith packet result LocalAgentHandoffTests.observed (fun _ _ _ ports _ swaps _ _ ->
                  rejects (run { ports with Stage = fun _ _ _ -> failwith "partial result reached staging" })
                  Assert.equal 0 swaps.Value))
          t "candidate refusal and unsafe target names have no target effects" (fun () -> fixture (fun _ _ _ ports _ swaps _ _ ->
              rejects (run { ports with ValidateCandidate = fun _ _ -> Error "candidate acceptance failed" })
              for value in [ "main"; "refs/tags/v1"; "refs/heads/../escape"; "refs/heads/a.lock"; "refs/heads/a b"; "refs/heads/.hidden"; "refs/heads/a@{x" ] do
                  rejects (LocalIntegration.run ports packet.RepositoryIdentity packet.DispatchId value)
              rejects (run { ports with Acquire = fun _ -> Ok null })
              Assert.equal 0 swaps.Value))
          t "substituted requested journal, target, packet or candidate cannot replace original plan" (fun () -> fixture (fun _ _ journal ports _ swaps _ _ ->
              rejects (run { ports with CompareAndSwap = fun _ -> Error "interrupted" })
              let saved = load journal
              rejects (LocalIntegration.run ports packet.RepositoryIdentity packet.DispatchId "refs/heads/another")
              for changed in [ { saved.Plan with DispatchId = "other" }; { saved.Plan with PacketDigest = "sha256:" + String('0', 64) }
                               { saved.Plan with CandidateCommit = String('e', 40) } ] do
                  rejects (run { ports with Journal = { journal with Load = fun _ _ -> Ok(Some { saved with Plan = changed }) } })
              rejects (journal.Prepare { saved.Plan with CandidateCommit = String('e', 40) })
              Assert.equal 0 swaps.Value))
          t "applied receipt does not permit rollback or hide a subsequently diverged target" (fun () -> fixture (fun _ _ _ ports head swaps _ _ ->
              run ports |> ok |> ignore
              head.Value <- packet.SourceCommit
              rejects (run ports)
              head.Value <- String('d', 40)
              rejects (run ports)
              Assert.equal 1 swaps.Value))
          t "pending native recovery blocks historical target observation" (fun () -> fixture (fun _ _ _ ports _ swaps _ _ ->
              run ports |> ok |> ignore
              rejects (run { ports with Intake = { ports.Intake with NativeRecovery = fun () -> Error "pending native recovery" } })
              Assert.equal 1 swaps.Value))
          t "another pending or partial integration blocks new mutation across dispatches" (fun () ->
              for corrupt in [ false; true ] do fixture (fun _ host journal ports _ swaps _ _ ->
                  rejects (run { ports with CompareAndSwap = fun _ -> Error "interrupted" })
                  let other = { (load journal).Plan with DispatchId = "DISPATCH-OTHER" }
                  journal.Prepare other |> ok |> ignore
                  if corrupt then
                      let path = Directory.GetDirectories host |> Array.filter (fun p -> Path.GetFileName p <> "locks")
                                 |> Array.find (fun p -> File.ReadAllText(Path.Combine(p, "plan.json")).Contains "DISPATCH-OTHER")
                      File.WriteAllText(Path.Combine(path, "plan.json"), "{")
                  rejects (run ports)
                  Assert.equal 0 swaps.Value))
          t "non-UTC or backwards clocks and expiry during candidate checks refuse mutation" (fun () ->
              fixture (fun _ _ _ ports _ swaps clock _ ->
                  rejects (run { ports with ValidateCandidate = fun _ _ -> clock.Value <- packet.ExpiresAt; Ok() })
                  Assert.equal 0 swaps.Value)
              for time in [ now.ToOffset(TimeSpan.FromHours 1.); packet.IssuedAt.AddSeconds(-1.) ] do fixture (fun _ _ _ ports _ swaps clock _ ->
                  clock.Value <- time
                  rejects (run ports)
                  Assert.equal 0 swaps.Value))
          t "strict plan and receipt decoding rejects duplicate, changed, oversized and non-UTC records" (fun () -> fixture (fun _ _ journal ports _ _ _ _ ->
              run ports |> ok |> ignore
              let plan = (load journal).Plan
              let json = LocalIntegrationJournalJson.renderPlan plan
              let nonUtc = JsonNode.Parse json
              nonUtc["preparedAt"] <- JsonValue.Create(now.ToOffset(TimeSpan.FromHours 1.).ToString("O"))
              for invalid in [ "{"; json.Replace("{", "{\"dispatchId\":\"extra\","); nonUtc.ToJsonString(); String('x', 16385) ] do rejects (LocalIntegrationJournalJson.readPlan invalid)
              let receipt = LocalIntegrationJournalJson.renderApplied plan now
              rejects (LocalIntegrationJournalJson.readApplied { plan with CandidateCommit = String('e', 40) } receipt)
              rejects (LocalIntegrationJournalJson.readApplied plan (receipt.Replace("{", "{\"unknown\":true,")))
              rejects (LocalIntegrationJournalJson.readApplied plan (LocalIntegrationJournalJson.renderApplied plan (now.AddSeconds -1.)))))
          t "partial, unexpected and linked journal entries are preserved and refused" (fun () ->
              for mutation in [ (fun dir -> File.WriteAllText(Path.Combine(dir, "plan.json"), "{"))
                                (fun dir -> File.WriteAllText(Path.Combine(dir, "foreign"), "retain"))
                                (fun dir -> File.WriteAllText(Path.Combine(dir, "applied.json"), String('x', 131073))) ] do fixture (fun _ host journal ports _ _ _ _ ->
                  rejects (run { ports with CompareAndSwap = fun _ -> Error "interrupted" })
                  let dir = Directory.GetDirectories(host) |> Array.filter (fun path -> Path.GetFileName path <> "locks") |> Array.exactlyOne
                  mutation dir
                  rejects (journal.Load packet.RepositoryIdentity packet.DispatchId)
                  rejects (run ports))
              if not (OperatingSystem.IsWindows()) then fixture (fun _ host journal ports _ _ _ _ ->
                  rejects (run { ports with CompareAndSwap = fun _ -> Error "interrupted" })
                  let dir = Directory.GetDirectories(host) |> Array.filter (fun path -> Path.GetFileName path <> "locks") |> Array.exactlyOne
                  let source, outside = Path.Combine(dir, "plan.json"), Path.Combine(host, "outside.json")
                  File.Move(source, outside)
                  File.CreateSymbolicLink(source, outside) |> ignore
                  rejects (journal.Load packet.RepositoryIdentity packet.DispatchId)
                  rejects (run ports)))
          t "repository lease excludes other dispatches and releases after failure" (fun () -> fixture (fun repo host _ ports _ _ _ _ ->
              let lease = FileLocalIntegrationJournal.acquire repo host packet.RepositoryIdentity |> ok
              try rejects (run ports)
              finally lease.Dispose()
              Assert.equal (Ok LocalIntegrationDisposition.TargetApplied) (run ports)
              use next = FileLocalIntegrationJournal.acquire repo host packet.RepositoryIdentity |> ok
              rejects (FileLocalIntegrationJournal.acquire repo host packet.RepositoryIdentity)))
          t "actual Git expected-head update is recovered after acknowledgement loss without another update" (fun () ->
              let root = CliHarness.temporaryDirectory "praxis-integration-git-ref"
              try
                  CliHarness.git root [ "init"; "--quiet" ] |> ignore
                  CliHarness.git root [ "config"; "user.name"; "Integration Fixture" ] |> ignore
                  CliHarness.git root [ "config"; "user.email"; "fixture@example.invalid" ] |> ignore
                  CliHarness.git root [ "commit"; "--allow-empty"; "-m"; "baseline" ] |> ignore
                  let before = CliHarness.git root [ "rev-parse"; "HEAD" ]
                  Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
                  File.WriteAllText(Path.Combine(root, "src/A.fs"), "fixture")
                  CliHarness.git root [ "add"; "src/A.fs" ] |> ignore
                  CliHarness.git root [ "commit"; "-m"; "worker candidate" ] |> ignore
                  let after = CliHarness.git root [ "rev-parse"; "HEAD" ]
                  CliHarness.git root [ "update-ref"; target; before ] |> ignore
                  let p = { packet with SourceCommit = before }
                  let result = { LocalAgentHandoffTests.result with OutputCommit = after; PacketDigest = LocalAgentHandoff.packetDigest p }
                  let observation = { LocalAgentHandoffTests.observed with OutputCommit = Some after; ReservedPacketDigest = result.PacketDigest }
                  fixtureWith p result observation (fun _ _ _ ports _ _ _ _ ->
                      let mutable swaps = 0
                      let gitPorts =
                          { ports with ReadTarget = fun reference -> Ok(CliHarness.git root [ "rev-parse"; reference ])
                                       Stage = fun _ _ _ -> Ok after
                                       ValidateCandidate =
                                           fun _ plan ->
                                               let verdict = CliHarness.run "git" [ "-C"; root; "merge-base"; "--is-ancestor"; plan.BeforeCommit; plan.CandidateCommit ] []
                                               if verdict.Exit = 0 then Ok() else Error "candidate lost baseline"
                                       CompareAndSwap =
                                           fun plan ->
                                               swaps <- swaps + 1
                                               CliHarness.git root [ "update-ref"; plan.TargetRef; plan.CandidateCommit; plan.BeforeCommit ] |> ignore
                                               Error "Git acknowledgement lost" }
                      rejects (run gitPorts)
                      Assert.equal after (CliHarness.git root [ "rev-parse"; target ])
                      Assert.equal (Ok LocalIntegrationDisposition.TargetApplied) (run gitPorts)
                      Assert.equal (Ok LocalIntegrationDisposition.TargetAlreadyObserved) (run gitPorts)
                      Assert.equal 1 swaps)
              finally Directory.Delete(root, true)) ]
