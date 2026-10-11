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
module LocalWorkerOutputSpoolTests =
    let private t name run = { Name = "worker output spool: " + name; Run = run }
    let private packet = LocalAgentHandoffTests.packet
    let private now = packet.IssuedAt.AddMinutes 5.
    let private reservation = { Packet = packet; ReservedAt = now; ControllerSessionId = "fixture-controller-1" }
    let private identity = "fixture-process-incarnation"
    let private payload = LocalAgentHandoffJson.renderResult LocalAgentHandoffTests.result
    let private bytes text = UTF8Encoding(false, true).GetBytes(text: string)
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private rejects value = Assert.isTrue (Result.isError value) "unsafe spool operation succeeded"
    let private observed output error exitCode =
        { ProcessIdentity = Some identity; ProcessStarted = Some true; RootExitObserved = true
          Outcome = LocalWorkerProcessOutcome.Exited exitCode; StandardOutput = output; StandardError = error
          CapturedBytes = (bytes output).Length + (bytes error).Length }
    let private fixture test =
        let root = CliHarness.temporaryDirectory "praxis-output-spool"
        let repo, journals, archives, spools = Path.Combine(root, "repo"), Path.Combine(root, "journals"), Path.Combine(root, "archives"), Path.Combine(root, "spools")
        for directory in [ repo; journals; archives; spools ] do Directory.CreateDirectory directory |> ignore
        try test repo journals archives spools (FileLocalDispatchJournal.create repo journals |> ok) (FileLocalWorkerSubmissions.create repo archives |> ok) (FileLocalWorkerOutputSpool.create repo spools |> ok)
        finally Directory.Delete(root, true)
    let private spoolDirectory spools = Directory.GetDirectories(spools) |> Array.exactlyOne
    let private load (store: LocalWorkerOutputSpoolStore) = store.Load packet.RepositoryIdentity packet.DispatchId
    let private journal (store: LocalDispatchJournalStore) exited =
        store.Reserve reservation |> ok |> ignore
        for sequence, kind, incarnation in
            [ 1, LocalDispatchEventKind.LaunchIntent, None; 2, LocalDispatchEventKind.LaunchObserved, Some identity ] @
            (if exited then [ 3, LocalDispatchEventKind.ExitObserved, Some identity ] else []) do
            store.Append packet.RepositoryIdentity packet.DispatchId { Sequence = sequence; Kind = kind; At = now; ProcessIdentity = incarnation; Detail = "host process fixture observation" } |> ok
    let private seal (store: LocalWorkerOutputSpoolStore) text error exitCode =
        let session = store.Open reservation |> ok
        try
            // Split the bytes, including inside multibyte Unicode characters.
            let raw = bytes text
            let chunkSize = if raw.Length < 64 then 1 else 4096
            for index in 0 .. chunkSize .. raw.Length - 1 do
                session.Write LocalWorkerOutputStream.StandardOutput (raw.AsSpan(index, min chunkSize (raw.Length - index)).ToArray()) |> ok
            session.Write LocalWorkerOutputStream.StandardError (bytes error) |> ok
            session.Seal (observed text error exitCode) |> ok
        finally session.Close()
    let private recovery journal spool archives : LocalSpoolRecoveryPorts =
        { Journal = journal; Spool = spool; Submissions = archives
          NativeRecovery = fun () -> Ok()
          ProcessRecovery = fun _ -> Ok()
          Now = fun () -> now }
    let private recover ports = LocalSpooledDispatch.recover ports packet.RepositoryIdentity packet.DispatchId
    let private controller store : LocalDispatchControllerPorts =
        { Store = store; Authority = fun () -> Ok { Enabled = true; Revision = packet.AuthorityRevision; ExpectedPacket = packet }
          Prerequisites = fun () -> Ok(packet.Prerequisites |> List.map (fun p -> p.WorkItemId, p.EvidenceDigest) |> Map.ofList)
          NativeRecovery = fun () -> Ok()
          ControllerSessionId = fun () -> Ok reservation.ControllerSessionId
          Now = fun () -> now }
    let private dispatch journal spool worker : LocalSpooledDispatchPorts =
        { Dispatch = { Controller = controller journal; Worker = { Run = fun _ _ _ -> failwith "unspooled worker was used" }
                       SelectSpec = fun _ -> Ok { LocalWorkerProcessTests.spec "result" [] with Input = LocalAgentHandoffJson.renderPacket packet } }
          StreamingWorker = worker; Spool = spool }
    let private run ports = LocalSpooledDispatch.run ports packet CancellationToken.None |> fun task -> task.GetAwaiter().GetResult()
    let private streaming : LocalWorkerStreamingProcessPort =
        { RunStreaming = fun spec started output token -> LocalWorkerProcess.runWithOutputObservation (fun _ -> Ok identity) output spec started token }

    let tests =
        [ t "flushed raw Unicode bytes reopen exactly and seal both diagnostic streams" (fun () -> fixture (fun repo _ _ spools _ _ spool ->
              seal spool "α🙂\n" "é diagnostics" 17
              let reopened = FileLocalWorkerOutputSpool.create repo spools |> ok
              let saved = load reopened |> ok
              Assert.equal (observed "α🙂\n" "é diagnostics" 17) saved.Observation
              Assert.equal (LocalAgentHandoff.packetDigest packet) (LocalAgentHandoff.packetDigest saved.Reservation.Packet)
              rejects (reopened.Open reservation)
              rejects (reopened.Load "another-repository" packet.DispatchId)))
          t "partial streams survive close without becoming recoverable or reopenable" (fun () -> fixture (fun _ _ _ spools _ _ spool ->
              let session = spool.Open reservation |> ok
              session.Write LocalWorkerOutputStream.StandardOutput [| 0xf0uy; 0x9fuy |] |> ok
              session.Close()
              Assert.equal [| 0xf0uy; 0x9fuy |] (File.ReadAllBytes(Path.Combine(spoolDirectory spools, "stdout.bin")))
              rejects (load spool)
              rejects (spool.Open reservation)
              rejects (session.Write LocalWorkerOutputStream.StandardOutput [| 0x82uy |])
              rejects (session.Seal (observed "" "" 0))))
          t "combined output overflow poisons an attempt and preserves retained bytes" (fun () -> fixture (fun _ _ _ spools _ _ spool ->
              let session = spool.Open reservation |> ok
              try
                  session.Write LocalWorkerOutputStream.StandardOutput (Array.create 65536 0x61uy) |> ok
                  rejects (session.Write LocalWorkerOutputStream.StandardError [| 0x62uy |])
                  rejects (session.Seal (observed (String('a', 65536)) "" 0))
                  rejects (load spool)
                  Assert.equal 65536 (File.ReadAllBytes(Path.Combine(spoolDirectory spools, "stdout.bin"))).Length
              finally session.Close()))
          t "mismatched, interrupted and invalid UTF-8 observations cannot seal" (fun () ->
              for observation in [ observed "other" "" 0; { observed "x" "" 0 with CapturedBytes = 2 }
                                   { observed "x" "" 0 with RootExitObserved = false }
                                   { observed "x" "" 0 with ProcessIdentity = None }
                                   { observed "x" "" 0 with Outcome = LocalWorkerProcessOutcome.TimedOut } ] do
                  fixture (fun _ _ _ _ _ _ spool ->
                      let session = spool.Open reservation |> ok
                      try session.Write LocalWorkerOutputStream.StandardOutput (bytes "x") |> ok; rejects (session.Seal observation); rejects (load spool)
                      finally session.Close())
              fixture (fun _ _ _ _ _ _ spool ->
                  let session = spool.Open reservation |> ok
                  try session.Write LocalWorkerOutputStream.StandardOutput [| 0xffuy |] |> ok; rejects (session.Seal (observed "�" "" 0)); rejects (load spool)
                  finally session.Close()))
          t "seal refuses repeat writes and observed unexpected entries or links" (fun () -> fixture (fun repo _ _ spools _ _ spool ->
              rejects (FileLocalWorkerOutputSpool.create repo (Path.Combine(repo, "spool")))
              let session = spool.Open reservation |> ok
              try
                  File.WriteAllText(Path.Combine(spoolDirectory spools, "foreign"), "retain")
                  rejects (session.Write LocalWorkerOutputStream.StandardOutput (bytes "x"))
                  rejects (session.Seal (observed "" "" 0))
              finally session.Close()
              rejects (load spool)))
          t "strict seals refuse raw-byte tampering and metadata substitution without overwriting" (fun () ->
              for change in [ (fun (node: JsonNode) -> node["packetDigest"] <- JsonValue.Create("sha256:" + String('0', 64)))
                              (fun node -> node["controllerSessionId"] <- JsonValue.Create "changed")
                              (fun node -> node["stdoutBytes"] <- JsonValue.Create -1)
                              (fun node -> node["extra"] <- JsonValue.Create true) ] do
                  fixture (fun _ _ _ spools _ _ spool ->
                      seal spool "x" "diagnostic" 0
                      let file = Path.Combine(spoolDirectory spools, "seal.json")
                      let node = JsonNode.Parse(File.ReadAllText file)
                      change node
                      File.WriteAllText(file, node.ToJsonString())
                      rejects (load spool)
                      rejects (spool.Open reservation))
              for name in [ "stdout.bin"; "stderr.bin" ] do fixture (fun _ _ _ spools _ _ spool ->
                  seal spool "x" "y" 0
                  File.WriteAllText(Path.Combine(spoolDirectory spools, name), "changed")
                  rejects (load spool)))
          t "oversized streams, partial seals, duplicate fields and observed links refuse load" (fun () ->
              for mutation in [ (fun target -> File.WriteAllBytes(Path.Combine(target, "stdout.bin"), Array.zeroCreate 65537))
                                (fun target -> File.WriteAllText(Path.Combine(target, "seal.json"), "{"))
                                (fun target -> let file = Path.Combine(target, "seal.json") in File.WriteAllText(file, File.ReadAllText(file).Replace("{", "{\"exitCode\":0,"))) ] do
                  fixture (fun _ _ _ spools _ _ spool -> seal spool "x" "" 0; mutation (spoolDirectory spools); rejects (load spool))
              if not (OperatingSystem.IsWindows()) then fixture (fun _ _ _ spools _ _ spool ->
                  seal spool "x" "" 0
                  let target = spoolDirectory spools
                  let original = Path.Combine(target, "stdout.bin")
                  let outside = Path.Combine(spools, "outside.bin")
                  File.Move(original, outside)
                  File.CreateSymbolicLink(original, outside) |> ignore
                  rejects (load spool)))
          t "sealed output recovers after exit without granting integration or accepting nonzero exit" (fun () ->
              for code in [ 0; 17 ] do fixture (fun repo journals archives spools journalStore archive spool ->
                  journal journalStore true
                  seal spool payload "diagnostic" code
                  let reopenedJournal = FileLocalDispatchJournal.create repo journals |> ok
                  let reopenedArchive = FileLocalWorkerSubmissions.create repo archives |> ok
                  let reopenedSpool = FileLocalWorkerOutputSpool.create repo spools |> ok
                  let ports = recovery reopenedJournal reopenedSpool reopenedArchive
                  Assert.equal (Ok LocalSubmissionSaveOutcome.Created) (recover ports)
                  Assert.equal (Ok LocalSubmissionSaveOutcome.Existing) (recover { ports with Now = fun () -> now.AddSeconds 1. })
                  let saved = archive.Load packet.RepositoryIdentity packet.DispatchId |> ok
                  Assert.equal payload saved.Payload
                  Assert.equal code saved.ExitCode
                  Assert.equal now saved.CapturedAt))
          t "running journals and unavailable host recovery cannot archive sealed output" (fun () ->
              fixture (fun _ _ _ _ journalStore archive spool ->
                  journal journalStore false; seal spool payload "" 0
                  rejects (recover (recovery journalStore spool archive))
                  rejects (archive.Load packet.RepositoryIdentity packet.DispatchId))
              fixture (fun _ _ _ _ journalStore archive spool ->
                  journal journalStore true; seal spool payload "" 0
                  let ports = recovery journalStore spool archive
                  for denied in [ { ports with NativeRecovery = fun () -> Error "pending native transaction" }
                                  { ports with ProcessRecovery = fun _ -> Error "process state unqualified" } ] do rejects (recover denied)
                  rejects (archive.Load packet.RepositoryIdentity packet.DispatchId)))
          t "cross-wired records and changed process incarnation refuse recovery" (fun () -> fixture (fun _ _ _ _ journalStore archive spool ->
              journal journalStore true; seal spool payload "" 0
              let saved = load spool |> ok
              let ports = recovery journalStore spool archive
              for changed in [ { saved with Reservation = { saved.Reservation with ControllerSessionId = "another-controller" } }
                               { saved with Reservation = { saved.Reservation with Packet = { packet with AttemptId = "another-attempt" } } }
                               { saved with Observation = { saved.Observation with ProcessIdentity = Some "reused-pid" } } ] do
                  rejects (recover { ports with Spool = { spool with Load = fun _ _ -> Ok changed } })
              let existing = journalStore.Load packet.RepositoryIdentity packet.DispatchId |> ok
              rejects (LocalSpooledDispatch.recover { ports with Journal = { journalStore with Load = fun _ _ -> Ok existing } } "another-repo" packet.DispatchId)
              rejects (archive.Load packet.RepositoryIdentity packet.DispatchId)))
          t "actual subprocess output is sealed before journal exit and recovered before archive" (fun () -> fixture (fun _ _ _ _ journalStore archive spool ->
              let mutable sealedBeforeExit = false
              let append repo dispatch (event: LocalDispatchEvent) =
                  if event.Kind = LocalDispatchEventKind.ExitObserved then
                      load spool |> ok |> ignore
                      sealedBeforeExit <- true
                  journalStore.Append repo dispatch event
              let observedJournal = { journalStore with Append = append }
              let report = run (dispatch observedJournal spool streaming) |> ok
              Assert.isTrue sealedBeforeExit "exit was recorded without a completed spool"
              Assert.equal false report.ReconciliationRequired
              Assert.equal payload (load spool |> ok).Observation.StandardOutput
              rejects (archive.Load packet.RepositoryIdentity packet.DispatchId)
              Assert.equal (Ok LocalSubmissionSaveOutcome.Created) (recover (recovery journalStore spool archive))
              rejects (run (dispatch journalStore spool streaming))))
          t "sink failures kill the root and cannot become a normal submission" (fun () ->
              let mutable writes = 0
              let output _ _ = writes <- writes + 1; Error "disk flush failed"
              let result = LocalWorkerProcess.runWithOutputObservation (fun _ -> Ok identity) output (LocalWorkerProcessTests.spec "flood" []) (fun _ -> Ok()) CancellationToken.None
                           |> fun task -> task.GetAwaiter().GetResult()
              Assert.isTrue (writes > 0) "sink was never called"
              Assert.isTrue result.RootExitObserved "sink failure left root running"
              match result.Outcome with LocalWorkerProcessOutcome.StreamFailed _ | LocalWorkerProcessOutcome.OutputLimitExceeded -> () | other -> failwith (sprintf "%A" other))
          t "failed sealing keeps normal worker output from passing capture" (fun () -> fixture (fun _ _ _ _ journalStore archive spool ->
              let injected = { spool with Open = fun reservation -> spool.Open reservation |> Result.map (fun session -> { session with Seal = fun _ -> Error "seal flush failed" }) }
              let report = run (dispatch journalStore injected streaming) |> ok
              let capture : LocalSubmissionCapturePorts =
                  { Journal = journalStore; Submissions = archive; ControllerSessionId = fun () -> Ok reservation.ControllerSessionId
                    Now = fun () -> now }
              rejects (LocalSubmissionIntake.capture capture packet report)
              rejects (load spool)
              rejects (recover (recovery journalStore spool archive))))
          t "a seal preceding a failed exit append waits for independent journal reconciliation" (fun () ->
              for persisted in [ false; true ] do fixture (fun _ _ _ _ journalStore archive spool ->
                  let append repo dispatch (event: LocalDispatchEvent) =
                      if event.Kind = LocalDispatchEventKind.ExitObserved then
                          if persisted then journalStore.Append repo dispatch event |> ok
                          Error "exit flush uncertain"
                      else journalStore.Append repo dispatch event
                  let failing = { journalStore with Append = append }
                  let report = run (dispatch failing spool streaming) |> ok
                  Assert.isTrue report.ReconciliationRequired "failed exit append became confirmed"
                  Assert.equal payload (load spool |> ok).Observation.StandardOutput
                  let pending = { recovery journalStore spool archive with ProcessRecovery = fun _ -> Error "independent reconciliation pending" }
                  rejects (recover pending)
                  rejects (archive.Load packet.RepositoryIdentity packet.DispatchId)
                  if not persisted then
                      rejects (recover (recovery journalStore spool archive))
                      // Synthetic independent-host recovery closes the original attempt.
                      journalStore.Append packet.RepositoryIdentity packet.DispatchId
                          { Sequence = 3; At = now; Kind = LocalDispatchEventKind.ExitObserved; ProcessIdentity = Some identity; Detail = "independent host fixture reconciled root exit" } |> ok
                  Assert.equal (Ok LocalSubmissionSaveOutcome.Created) (recover (recovery journalStore spool archive))))
          t "worker budgets beyond spool capacity refuse before reservation" (fun () -> fixture (fun _ _ _ _ journalStore _ spool ->
              let ports = dispatch journalStore spool { RunStreaming = fun _ _ _ _ -> failwith "oversized budget launched" }
              let changed = { ports with Dispatch = { ports.Dispatch with SelectSpec = fun _ -> Ok { LocalWorkerProcessTests.spec "result" [] with MaxOutputBytes = 65537; Input = LocalAgentHandoffJson.renderPacket packet } } }
              rejects (run changed)
              rejects (journalStore.Load packet.RepositoryIdentity packet.DispatchId)))
          t "forced controller interruption preserves partial bytes and recovers only sealed output" (fun () ->
              for mode in [ "partial"; "sealed" ] do fixture (fun repo _ _ spools journalStore archive spool ->
                  journal journalStore true
                  let spec = { LocalWorkerProcessTests.spec "persist-output-spool" [ repo; spools; mode ] with Input = LocalDispatchJournalJson.renderReservation reservation; TimeoutMilliseconds = 2500 }
                  let result = LocalWorkerProcess.runWithIdentityObservation (fun _ -> Ok "fixture-controller-process") spec (fun _ -> Ok()) CancellationToken.None
                               |> fun task -> task.GetAwaiter().GetResult()
                  Assert.equal LocalWorkerProcessOutcome.TimedOut result.Outcome
                  Assert.equal "spool-flushed" result.StandardOutput
                  Assert.isTrue result.RootExitObserved "fixture controller exit unconfirmed"
                  Assert.equal payload (UTF8Encoding(false, true).GetString(File.ReadAllBytes(Path.Combine(spoolDirectory spools, "stdout.bin"))))
                  let reopened = FileLocalWorkerOutputSpool.create repo spools |> ok
                  if mode = "sealed" then Assert.equal (Ok LocalSubmissionSaveOutcome.Created) (recover (recovery journalStore reopened archive))
                  else rejects (load reopened); rejects (recover (recovery journalStore reopened archive))))
          t "two contenders can never acquire the same writable attempt" (fun () ->
              for _ in 1 .. 20 do fixture (fun _ _ _ _ _ _ spool ->
                  use start = new ManualResetEventSlim(false)
                  let attempt () = start.Wait(); spool.Open reservation
                  let first, second = Task.Run attempt, Task.Run attempt
                  start.Set()
                  let results = [ first.GetAwaiter().GetResult(); second.GetAwaiter().GetResult() ]
                  let winners = results |> List.choose (function Ok session -> Some session | Error _ -> None)
                  Assert.equal 1 winners.Length
                  winners |> List.iter (fun session -> session.Close())
                  rejects (load spool))) ]
