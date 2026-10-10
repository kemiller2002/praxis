namespace Praxis.Tests

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module LocalSubmissionIntakeTests =
    let private t name run = { Name = "worker submission: " + name; Run = run }
    let private packet = LocalAgentHandoffTests.packet
    let private now = packet.IssuedAt.AddMinutes 5.
    let private identity = "fixture-process-incarnation"
    let private payload = LocalAgentHandoffJson.renderResult LocalAgentHandoffTests.result
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private rejects result = Assert.isTrue (Result.isError result) "unsafe submission operation succeeded"
    let private observation text exitCode =
        { ProcessIdentity = Some identity; ProcessStarted = Some true; RootExitObserved = true
          Outcome = LocalWorkerProcessOutcome.Exited exitCode; StandardOutput = text; StandardError = ""
          CapturedBytes = Encoding.UTF8.GetByteCount text }
    let private report text exitCode =
        { Observation = Some(observation text exitCode); JournalPhase = Some(LocalDispatchPhase.Exited identity)
          ReconciliationRequired = false; Problems = [] }
    let private capturePorts journal submissions : LocalSubmissionCapturePorts =
        { Journal = journal; Submissions = submissions
          ControllerSessionId = fun () -> Ok "fixture-controller-1"
          Now = fun () -> now }
    let private intakePorts journal submissions : LocalSubmissionIntakePorts =
        { Journal = journal; Submissions = submissions
          NativeRecovery = fun () -> Ok()
          ProcessRecovery = fun _ -> Ok()
          Authority = fun () -> Ok { Enabled = true; Revision = packet.AuthorityRevision; ExpectedPacket = packet }
          Prerequisites = fun () -> Ok(packet.Prerequisites |> List.map (fun p -> p.WorkItemId, p.EvidenceDigest) |> Map.ofList)
          Observe = fun _ _ -> Ok LocalAgentHandoffTests.observed
          Now = fun () -> now }
    let private inspect ports = LocalSubmissionIntake.inspect ports packet.RepositoryIdentity packet.DispatchId
    let private exitJournal journal =
        journal.Reserve { Packet = packet; ReservedAt = now; ControllerSessionId = "fixture-controller-1" } |> ok |> ignore
        for sequence, kind, incarnation in [ 1, LocalDispatchEventKind.LaunchIntent, None; 2, LocalDispatchEventKind.LaunchObserved, Some identity; 3, LocalDispatchEventKind.ExitObserved, Some identity ] do
            journal.Append packet.RepositoryIdentity packet.DispatchId
                { Sequence = sequence; At = now; Kind = kind; ProcessIdentity = incarnation; Detail = "process fixture observation" } |> ok
    let private fixture exited test =
        let root = CliHarness.temporaryDirectory "praxis-worker-submissions"
        let repo, journals, submissions = Path.Combine(root, "repo"), Path.Combine(root, "journals"), Path.Combine(root, "submissions")
        for directory in [ repo; journals; submissions ] do Directory.CreateDirectory directory |> ignore
        try
            let journal = FileLocalDispatchJournal.create repo journals |> ok
            let store = FileLocalWorkerSubmissions.create repo submissions |> ok
            if exited then exitJournal journal
            test repo journals submissions journal store
        finally Directory.Delete(root, true)
    let private save journal store text exitCode = LocalSubmissionIntake.capture (capturePorts journal store) packet (report text exitCode)
    let private load (store: LocalWorkerSubmissionStore) = store.Load packet.RepositoryIdentity packet.DispatchId |> ok
    let private mutate (json: string) edit =
        let node = JsonNode.Parse json
        edit node
        node.ToJsonString()

    let tests =
        [ t "saved bytes survive reopened stores and reach only independently checked awaiting integration" (fun () -> fixture true (fun repo journals submissions journal store ->
              let raw = "\n " + payload + "\n"
              Assert.equal (Ok LocalSubmissionSaveOutcome.Created) (save journal store raw 0)
              let reopenedJournal = FileLocalDispatchJournal.create repo journals |> ok
              let reopenedStore = FileLocalWorkerSubmissions.create repo submissions |> ok
              Assert.equal raw (load reopenedStore).Payload
              Assert.equal (Ok LocalResultDisposition.AwaitingIntegration) (inspect (intakePorts reopenedJournal reopenedStore))
              Assert.equal (Ok(LocalDispatchPhase.Exited identity)) (reopenedJournal.Load packet.RepositoryIdentity packet.DispatchId |> Result.bind LocalDispatchJournal.phase)))
          t "exact duplicate archive preserves first capture time and changed content cannot replace it" (fun () -> fixture true (fun _ _ _ journal store ->
              save journal store payload 0 |> ok |> ignore
              let original = load store
              let later = { capturePorts journal store with Now = fun () -> now.AddSeconds 1. }
              Assert.equal (Ok LocalSubmissionSaveOutcome.Existing) (LocalSubmissionIntake.capture later packet (report payload 0))
              Assert.equal original (load store)
              for changed in [ { original with Payload = "{}"; PayloadDigest = LocalWorkerSubmission.digest (Encoding.UTF8.GetBytes "{}") }
                               { original with AttemptId = "other-attempt" }; { original with ProcessIdentity = "reused-pid" }; { original with ExitCode = 17 } ] do
                  rejects (store.Save changed)
                  Assert.equal original (load store)))
          t "malformed or self-approving stdout is archived unchanged and cannot pass intake" (fun () ->
              for raw in [ "{"; "{}"; ""; "{\"approved\":true,\"completed\":true}"; payload.Replace("\"submitted\"", "\"completed\"") ] do
                  fixture true (fun _ _ _ journal store ->
                      Assert.equal (Ok LocalSubmissionSaveOutcome.Created) (save journal store raw 0)
                      Assert.equal raw (load store).Payload
                      rejects (inspect (intakePorts journal store))))
          t "nonzero exit archives evidence but never calls acceptance observations" (fun () -> fixture true (fun _ _ _ journal store ->
              save journal store payload 17 |> ok |> ignore
              let ports = { intakePorts journal store with Observe = fun _ _ -> failwith "nonzero exit reached acceptance" }
              rejects (inspect ports)
              Assert.equal 17 (load store).ExitCode))
          t "uncertain dispatch, wrong incarnation, wrong byte count and interrupted streams refuse capture" (fun () -> fixture true (fun _ _ _ journal store ->
              let valid = report payload 0
              let observed = valid.Observation |> Option.get
              for changed in [ { valid with ReconciliationRequired = true }; { valid with Problems = [ "write failed" ] }
                               { valid with Observation = None }; { valid with JournalPhase = Some LocalDispatchPhase.NoStart }
                               { valid with Observation = Some { observed with ProcessIdentity = Some "other-incarnation" } }
                               { valid with Observation = Some { observed with CapturedBytes = 0 } }
                               { valid with Observation = Some { observed with RootExitObserved = false } }
                               { valid with Observation = Some { observed with Outcome = LocalWorkerProcessOutcome.OutputLimitExceeded } } ] do
                  rejects (LocalSubmissionIntake.capture (capturePorts journal store) packet changed)
                  rejects (store.Load packet.RepositoryIdentity packet.DispatchId)))
          t "missing journal, changed controller and invalid capture clocks cannot save" (fun () ->
              fixture false (fun _ _ _ journal store -> rejects (save journal store payload 0))
              fixture true (fun _ _ _ journal store ->
                  let baseline = capturePorts journal store
                  for changed in [ { baseline with ControllerSessionId = fun () -> Ok "new-controller" }
                                   { baseline with Now = fun () -> now.AddSeconds(-1.) }
                                   { baseline with Now = fun () -> now.ToOffset(TimeSpan.FromHours 1.) } ] do
                      rejects (LocalSubmissionIntake.capture changed packet (report payload 0))
                      rejects (store.Load packet.RepositoryIdentity packet.DispatchId)))
          t "expired and revoked submissions remain archived but fail fresh intake" (fun () -> fixture true (fun _ _ _ journal store ->
              save journal store payload 0 |> ok |> ignore
              let baseline = intakePorts journal store
              for changed in [ { baseline with Now = fun () -> packet.ExpiresAt }
                               { baseline with Authority = fun () -> Ok { Enabled = false; Revision = packet.AuthorityRevision; ExpectedPacket = packet } }
                               { baseline with Prerequisites = fun () -> Ok Map.empty }
                               { baseline with Now = fun () -> now.AddSeconds(-1.) } ] do rejects (inspect changed)
              Assert.equal payload (load store).Payload))
          t "late capture preserves evidence without extending the expired delegation" (fun () -> fixture true (fun _ _ _ journal store ->
              let expired = packet.ExpiresAt.AddSeconds 1.
              let archive = { capturePorts journal store with Now = fun () -> expired }
              Assert.equal (Ok LocalSubmissionSaveOutcome.Created) (LocalSubmissionIntake.capture archive packet (report payload 0))
              Assert.equal expired (load store).CapturedAt
              rejects (inspect { intakePorts journal store with Now = fun () -> expired })))
          t "well-shaped archive metadata cannot substitute the packet or process incarnation" (fun () ->
              for replace in [ (fun (s: LocalWorkerSubmission) -> { s with AttemptId = "other-attempt" })
                               (fun s -> { s with PacketDigest = "sha256:" + String('0', 64) })
                               (fun s -> { s with ProcessIdentity = "other-process-incarnation" }) ] do
                  fixture true (fun _ _ submissions journal store ->
                      save journal store payload 0 |> ok |> ignore
                      let changed = replace (load store)
                      let file = Path.Combine(Directory.GetDirectories(submissions) |> Array.exactlyOne, "submission.json")
                      File.WriteAllText(file, LocalWorkerSubmissionJson.render changed)
                      Assert.equal changed (load store)
                      let ports = { intakePorts journal store with Observe = fun _ _ -> failwith "substituted metadata reached observations" }
                      rejects (inspect ports)))
          t "revocation and expiry during independent observations are rechecked afterward" (fun () -> fixture true (fun _ _ _ journal store ->
              save journal store payload 0 |> ok |> ignore
              for expire in [ false; true ] do
                  let mutable observed = false
                  let baseline = intakePorts journal store
                  let ports =
                      { baseline with
                          Observe = fun _ _ -> observed <- true; Ok LocalAgentHandoffTests.observed
                          Authority = fun () -> Assert.isTrue observed "authority was cached before observation"; Ok { Enabled = expire; Revision = packet.AuthorityRevision; ExpectedPacket = packet }
                          Now = fun () -> if observed && expire then packet.ExpiresAt else now }
                  rejects (inspect ports)))
          t "pending native recovery and unqualified process reconciliation block acceptance" (fun () -> fixture true (fun _ _ _ journal store ->
              save journal store payload 0 |> ok |> ignore
              let baseline = { intakePorts journal store with Observe = fun _ _ -> failwith "unreconciled intake observed acceptance" }
              rejects (inspect { baseline with NativeRecovery = fun () -> Error "pending native transaction" })
              rejects (inspect { baseline with ProcessRecovery = fun _ -> Error "process incarnation unavailable" })))
          t "worker claims cannot replace actual commit, artifact or independent acceptance evidence" (fun () -> fixture true (fun _ _ _ journal store ->
              save journal store payload 0 |> ok |> ignore
              let baseline = intakePorts journal store
              let actual = LocalAgentHandoffTests.observed
              for changed in [ { actual with OutputCommit = None }; { actual with Evidence = Map.empty }
                               { actual with Acceptance = Map.empty }; { actual with ChangedPaths = Ok [ "src/hidden.fs" ] } ] do
                  rejects (inspect { baseline with Observe = fun _ _ -> Ok changed })))
          t "strict archival JSON binds raw payload bytes and rejects unknown or duplicated fields" (fun () -> fixture true (fun _ _ _ journal store ->
              save journal store payload 0 |> ok |> ignore
              let archived = load store
              let json = LocalWorkerSubmissionJson.render archived
              Assert.equal (Ok archived) (LocalWorkerSubmissionJson.read json)
              for changed in [ json.Replace("\"exitCode\":", "\"exitCode\":0,\"exitCode\":")
                               mutate json (fun node -> node["approved"] <- JsonValue.Create true)
                               mutate json (fun node -> node["payloadDigest"] <- JsonValue.Create("sha256:" + String('0', 64)))
                               mutate json (fun node -> node["payloadBase64"] <- JsonValue.Create " /w==")
                               mutate json (fun node -> node["payloadBase64"] <- JsonValue.Create "/w==")
                               mutate json (fun node -> node["capturedAt"] <- JsonValue.Create(now.ToOffset(TimeSpan.FromHours 1.).ToString("O"))) ] do
                  rejects (LocalWorkerSubmissionJson.read changed)))
          t "oversized payloads, invalid Unicode and partial archive files remain refused" (fun () -> fixture true (fun _ _ submissions journal store ->
              rejects (save journal store (String('x', 65537)) 0)
              rejects (save journal store (String(char 0xd800, 1)) 0)
              save journal store payload 0 |> ok |> ignore
              let archived = load store
              let file = Path.Combine(Directory.GetDirectories(submissions) |> Array.exactlyOne, "submission.json")
              for malformed in [ "{"; String('x', 131073) ] do
                  File.WriteAllText(file, malformed)
                  rejects (store.Load packet.RepositoryIdentity packet.DispatchId)
                  rejects (store.Save archived)
                  Assert.equal malformed (File.ReadAllText file)
              File.WriteAllBytes(file, [| 0xffuy |])
              rejects (store.Load packet.RepositoryIdentity packet.DispatchId)))
          t "failed save is not converted to success when the record became readable" (fun () -> fixture true (fun _ _ _ journal store ->
              let broken = { store with Save = fun submission -> store.Save submission |> ok |> ignore; Error "injected archive flush failure" }
              rejects (save journal broken payload 0)
              Assert.equal payload (load store).Payload))
          t "a terminated controller-store fixture leaves its flushed submission recoverable" (fun () -> fixture true (fun repo _ submissions journal store ->
              let bytes = Encoding.UTF8.GetBytes payload
              let record =
                  { SchemaVersion = LocalWorkerSubmission.Schema; RepositoryIdentity = packet.RepositoryIdentity
                    DispatchId = packet.DispatchId; AttemptId = packet.AttemptId; PacketDigest = LocalAgentHandoff.packetDigest packet
                    ProcessIdentity = identity; CapturedAt = now; ExitCode = 0; Payload = payload; PayloadDigest = LocalWorkerSubmission.digest bytes }
              let selected = { LocalWorkerProcessTests.spec "persist-submission" [ repo; submissions ] with Input = LocalWorkerSubmissionJson.render record; TimeoutMilliseconds = 5000 }
              let stopped = LocalWorkerProcess.runWithIdentityObservation (fun _ -> Ok "controller-store-fixture-incarnation") selected (fun _ -> Ok()) CancellationToken.None |> fun task -> task.GetAwaiter().GetResult()
              Assert.equal LocalWorkerProcessOutcome.TimedOut stopped.Outcome
              Assert.isTrue stopped.RootExitObserved "controller-store fixture exit unconfirmed"
              Assert.equal "submission-flushed" stopped.StandardOutput
              let reopened = FileLocalWorkerSubmissions.create repo submissions |> ok
              Assert.equal record (load reopened)
              Assert.equal (Ok LocalResultDisposition.AwaitingIntegration) (inspect (intakePorts journal reopened))))
          t "concurrent archival creates one immutable record and refuses changed replacement" (fun () -> fixture true (fun _ _ _ journal store ->
              save journal store payload 0 |> ok |> ignore
              let original = load store
              for iteration in 1..50 do
                  let candidate = { original with DispatchId = "archive-race-" + string iteration }
                  use barrier = new Barrier(2)
                  let contender () = Task.Run(fun () -> barrier.SignalAndWait() |> ignore; store.Save candidate)
                  let left, right = contender(), contender()
                  Task.WaitAll [| left :> Task; right :> Task |]
                  Assert.equal 1 ([ left.Result; right.Result ] |> List.filter ((=) (Ok LocalSubmissionSaveOutcome.Created)) |> List.length)
                  Assert.equal candidate (store.Load packet.RepositoryIdentity candidate.DispatchId |> ok)))
          t "repository roots, extra entries and observed links refuse archival access" (fun () -> fixture true (fun repo _ submissions journal store ->
              rejects (FileLocalWorkerSubmissions.create repo repo)
              save journal store payload 0 |> ok |> ignore
              let archived = load store
              let target = Directory.GetDirectories(submissions) |> Array.exactlyOne
              let extra = Path.Combine(target, "unrecognized")
              File.WriteAllText(extra, "evidence")
              rejects (store.Load packet.RepositoryIdentity packet.DispatchId)
              rejects (store.Save archived)
              File.Delete extra
              if not (OperatingSystem.IsWindows()) then
                  let file = Path.Combine(target, "submission.json")
                  let moved = Path.Combine(submissions, "linked-evidence.json")
                  File.Move(file, moved)
                  File.CreateSymbolicLink(file, moved) |> ignore
                  rejects (store.Load packet.RepositoryIdentity packet.DispatchId)
                  rejects (store.Save archived)))
          t "actual subprocess output is archived and independently inspected after reopening" (fun () -> fixture false (fun repo journals submissions journal store ->
              let selected = { LocalWorkerProcessTests.spec "result" [] with Input = LocalAgentHandoffJson.renderPacket packet }
              let controller =
                  { Store = journal; Authority = fun () -> Ok { Enabled = true; Revision = packet.AuthorityRevision; ExpectedPacket = packet }
                    Prerequisites = fun () -> Ok(packet.Prerequisites |> List.map (fun p -> p.WorkItemId, p.EvidenceDigest) |> Map.ofList)
                    NativeRecovery = fun () -> Ok()
                    ControllerSessionId = fun () -> Ok "fixture-controller-1"
                    Now = fun () -> now }
              let ports = { Controller = controller; Worker = { Run = LocalWorkerProcess.runWithIdentityObservation (fun _ -> Ok identity) }; SelectSpec = fun _ -> Ok selected }
              let dispatched = LocalSubmissionIntake.dispatchAndCapture ports (capturePorts journal store) packet CancellationToken.None |> fun task -> task.GetAwaiter().GetResult() |> ok
              Assert.equal (Ok LocalSubmissionSaveOutcome.Created) dispatched.Archive
              let reopenedJournal = FileLocalDispatchJournal.create repo journals |> ok
              let reopenedStore = FileLocalWorkerSubmissions.create repo submissions |> ok
              Assert.equal (Ok LocalResultDisposition.AwaitingIntegration) (inspect (intakePorts reopenedJournal reopenedStore)))) ]
