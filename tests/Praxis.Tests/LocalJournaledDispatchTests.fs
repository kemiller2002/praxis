namespace Praxis.Tests

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module LocalJournaledDispatchTests =
    let private t name run = { Name = "journaled worker: " + name; Run = run }
    let private packet = LocalAgentHandoffTests.packet
    let private now = packet.IssuedAt.AddMinutes 5.
    let private identity = "fixture-process-incarnation"
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private observation outcome started incarnation root =
        { Outcome = outcome; ProcessStarted = started; ProcessIdentity = incarnation; RootExitObserved = root
          StandardOutput = ""; StandardError = ""; CapturedBytes = 0 }
    let private exited = observation (LocalWorkerProcessOutcome.Exited 0) (Some true) (Some identity) true
    let private spec =
        { Executable = "/fixture/worker"; ExecutableDigest = "sha256:" + String('0', 64); Arguments = []
          WorkingDirectory = "/fixture/worktree"; Environment = Map.empty
          Input = LocalAgentHandoffJson.renderPacket packet; TimeoutMilliseconds = 1000; MaxOutputBytes = 1024 }
    let private controller store =
        { Store = store; Authority = fun () -> Ok { Enabled = true; Revision = packet.AuthorityRevision; ExpectedPacket = packet }
          Prerequisites = fun () -> Ok(packet.Prerequisites |> List.map (fun p -> p.WorkItemId, p.EvidenceDigest) |> Map.ofList)
          NativeRecovery = fun () -> Ok()
          ControllerSessionId = fun () -> Ok "fixture-controller-1"
          Now = fun () -> now }
    let private ports store worker = { Controller = controller store; Worker = worker; SelectSpec = fun _ -> Ok spec }
    let private run ports = LocalJournaledDispatch.run ports packet CancellationToken.None |> fun task -> task.GetAwaiter().GetResult()
    let private load store = store.Load packet.RepositoryIdentity packet.DispatchId |> ok
    let private phase store = load store |> LocalDispatchJournal.phase |> ok
    let private fixture test =
        let root = CliHarness.temporaryDirectory "praxis-journaled-worker"
        let repo, host = Path.Combine(root, "repo"), Path.Combine(root, "host")
        Directory.CreateDirectory repo |> ignore
        Directory.CreateDirectory host |> ignore
        try test repo host (FileLocalDispatchJournal.create repo host |> ok)
        finally Directory.Delete(root, true)
    let private successful = { Run = fun _ started _ -> task { started identity |> ok; return exited } }

    let tests =
        [ t "intent precedes start and exact root exit persists without accepting a work result" (fun () -> fixture (fun repo host store ->
              let mutable launches = 0
              let worker = { Run = fun selected started _ -> task {
                  launches <- launches + 1
                  Assert.equal LocalDispatchPhase.LaunchUncertain (phase store)
                  Assert.equal (LocalAgentHandoffJson.renderPacket packet) selected.Input
                  started identity |> ok
                  Assert.equal (LocalDispatchPhase.Running identity) (phase store)
                  return exited } }
              let report = run (ports store worker) |> ok
              Assert.equal (Some(LocalDispatchPhase.Exited identity)) report.JournalPhase
              Assert.equal false report.ReconciliationRequired
              Assert.equal [] report.Problems
              let reopened = FileLocalDispatchJournal.create repo host |> ok
              Assert.equal (LocalDispatchPhase.Exited identity) (phase reopened)
              Assert.isTrue (Result.isError (run (ports reopened worker))) "terminal dispatch launched again"
              Assert.equal 1 launches))
          t "existing reservation refuses launch even for the same controller" (fun () -> fixture (fun _ _ store ->
              LocalDispatchReservation.reserve (controller store) packet |> ok |> ignore
              let worker = { Run = fun _ _ _ -> failwith "existing reservation reached worker" }
              Assert.isTrue (Result.isError (run (ports store worker))) "existing reservation was recycled"
              Assert.equal LocalDispatchPhase.Reserved (phase store)))
          t "canonical packet ordering survives reservation serialization" (fun () -> fixture (fun _ _ store ->
              let reordered = { packet with Members = List.rev packet.Members; DecisionIds = List.rev packet.DecisionIds
                                            AllowedPaths = List.rev packet.AllowedPaths; Acceptance = List.rev packet.Acceptance }
              let report = LocalJournaledDispatch.run (ports store successful) reordered CancellationToken.None |> fun task -> task.GetAwaiter().GetResult() |> ok
              Assert.equal false report.ReconciliationRequired
              Assert.equal (Some(LocalDispatchPhase.Exited identity)) report.JournalPhase))
          t "substituted input and excessive budgets refuse before reservation" (fun () -> fixture (fun _ _ store ->
              let baseline = ports store { Run = fun _ _ _ -> failwith "invalid spec reached worker" }
              for bad in [ { spec with Input = "model prose" }; { spec with TimeoutMilliseconds = packet.TimeoutSeconds * 1000 + 1 }
                           { spec with MaxOutputBytes = packet.MaxOutputBytes + 1 }; { spec with TimeoutMilliseconds = 0 } ] do
                  Assert.isTrue (Result.isError (run { baseline with SelectSpec = fun _ -> Ok bad })) "invalid spec accepted"
                  Assert.isTrue (Result.isError (store.Load packet.RepositoryIdentity packet.DispatchId)) "invalid spec wrote reservation"))
          t "authority revoked between reservation and intent prevents worker invocation" (fun () -> fixture (fun _ _ store ->
              let mutable checks = 0
              let baseline = ports store { Run = fun _ _ _ -> failwith "revoked authority reached worker" }
              let authority () = checks <- checks + 1; Ok { Enabled = checks = 1; Revision = packet.AuthorityRevision; ExpectedPacket = packet }
              Assert.isTrue (Result.isError (run { baseline with Controller = { baseline.Controller with Authority = authority } })) "revocation accepted"
              Assert.equal 2 checks
              Assert.equal LocalDispatchPhase.Reserved (phase store)))
          t "failed intent persistence never invokes the worker even if bytes became readable" (fun () ->
              for persistBeforeError in [ false; true ] do fixture (fun _ _ store ->
                  let broken =
                      { store with
                          Append =
                              fun repo dispatch event ->
                                  if persistBeforeError then store.Append repo dispatch event |> ok
                                  Error "injected intent flush failure" }
                  let worker = { Run = fun _ _ _ -> failwith "failed intent reached worker" }
                  Assert.isTrue (Result.isError (run (ports broken worker))) "failed intent authorized launch"
                  Assert.equal (if persistBeforeError then LocalDispatchPhase.LaunchUncertain else LocalDispatchPhase.Reserved) (phase store)
                  Assert.isTrue (Result.isError (run (ports store worker))) "failed intent recycled assignment"))
          t "uncertain start and thrown launch retain intent and cannot automatically retry" (fun () ->
              for worker in [ { Run = fun _ _ _ -> Task.FromResult(observation (LocalWorkerProcessOutcome.StartUncertain "start failed") None None false) }
                              { Run = fun _ _ _ -> failwith "launch transport disappeared" } ] do
                  fixture (fun _ _ store ->
                      let report = run (ports store worker) |> ok
                      Assert.equal (Some LocalDispatchPhase.LaunchUncertain) report.JournalPhase
                      Assert.isTrue report.ReconciliationRequired "uncertain start did not require reconciliation"
                      Assert.isTrue (Result.isError (run (ports store worker))) "uncertain start relaunched"))
          t "failed or ambiguously persisted start recording stops acceptance of exit" (fun () ->
              for persistBeforeError in [ false; true ] do fixture (fun _ _ store ->
                  let broken =
                      { store with
                          Append =
                              fun repo dispatch event ->
                                  if event.Kind = LocalDispatchEventKind.LaunchObserved then
                                      if persistBeforeError then store.Append repo dispatch event |> ok
                                      Error "injected start flush failure"
                                  else store.Append repo dispatch event }
                  let worker = { Run = fun _ started _ -> task {
                      Assert.isTrue (Result.isError (started identity)) "failed append callback accepted"
                      return { exited with Outcome = LocalWorkerProcessOutcome.ObservationFailed "start recording refused" } } }
                  let report = run (ports broken worker) |> ok
                  Assert.isTrue report.ReconciliationRequired "failed start recording trusted"
                  Assert.equal (if persistBeforeError then LocalDispatchPhase.Running identity else LocalDispatchPhase.LaunchUncertain) (phase store)
                  Assert.isTrue (not ((load store).Events |> List.exists (fun e -> e.Kind = LocalDispatchEventKind.ExitObserved))) "exit accepted after failed start persistence"))
          t "failed exit persistence preserves running uncertainty even when bytes were written" (fun () ->
              for persistBeforeError in [ false; true ] do fixture (fun _ _ store ->
                  let broken =
                      { store with
                          Append =
                              fun repo dispatch event ->
                                  if event.Kind = LocalDispatchEventKind.ExitObserved then
                                      if persistBeforeError then store.Append repo dispatch event |> ok
                                      Error "injected exit flush failure"
                                  else store.Append repo dispatch event }
                  let report = run (ports broken successful) |> ok
                  Assert.isTrue report.ReconciliationRequired "failed exit persistence accepted"
                  Assert.equal (if persistBeforeError then LocalDispatchPhase.Exited identity else LocalDispatchPhase.Running identity) (phase store)
                  Assert.isTrue (Result.isError (run (ports broken successful))) "failed exit automatically relaunched"))
          t "unconfirmed root exit remains running and requires reconciliation" (fun () -> fixture (fun _ _ store ->
              let worker = { Run = fun _ started _ -> task { started identity |> ok; return { exited with RootExitObserved = false } } }
              let report = run (ports store worker) |> ok
              Assert.equal (Some(LocalDispatchPhase.Running identity)) report.JournalPhase
              Assert.isTrue report.ReconciliationRequired "unconfirmed root exit trusted"))
          t "repeated callbacks and contradictory worker observations cannot close tracking" (fun () ->
              for mode in [ "repeat"; "wrong-identity"; "no-start"; "budget" ] do fixture (fun _ _ store ->
                  let worker = { Run = fun _ started _ -> task {
                      started identity |> ok
                      if mode = "repeat" then Assert.isTrue (Result.isError (started identity)) "duplicate callback accepted"
                      return match mode with
                             | "wrong-identity" -> { exited with ProcessIdentity = Some "other-incarnation" }
                             | "no-start" -> { exited with ProcessStarted = Some false }
                             | "budget" -> { exited with CapturedBytes = spec.MaxOutputBytes + 1 }
                             | _ -> exited } }
                  let report = run (ports store worker) |> ok
                  Assert.equal (Some(LocalDispatchPhase.Running identity)) report.JournalPhase
                  Assert.isTrue report.ReconciliationRequired "contradictory observation accepted"))
          t "confirmed preflight refusal or cancellation closes no-start without reuse" (fun () ->
              for outcome in [ LocalWorkerProcessOutcome.PreflightRefused "invalid pin"; LocalWorkerProcessOutcome.Cancelled ] do fixture (fun _ _ store ->
                  let worker = { Run = fun _ _ _ -> Task.FromResult(observation outcome (Some false) None false) }
                  let report = run (ports store worker) |> ok
                  Assert.equal (Some LocalDispatchPhase.NoStart) report.JournalPhase
                  Assert.equal false report.ReconciliationRequired
                  Assert.isTrue (Result.isError (run (ports store worker))) "no-start reservation reused"))
          t "controller incarnation changed after start cannot append an exit" (fun () -> fixture (fun _ _ store ->
              let mutable session = "fixture-controller-1"
              let worker = { Run = fun _ started _ -> task { started identity |> ok; session <- "fixture-controller-2"; return exited } }
              let baseline = ports store worker
              let report = run { baseline with Controller = { baseline.Controller with ControllerSessionId = fun () -> Ok session } } |> ok
              Assert.equal None report.JournalPhase
              Assert.isTrue report.ReconciliationRequired "new controller closed old process"
              Assert.equal (LocalDispatchPhase.Running identity) (phase store)))
          t "competing dispatch calls invoke the worker only once" (fun () -> fixture (fun _ _ store ->
              let mutable launches = 0
              let worker = { Run = fun _ started _ -> task { Interlocked.Increment(&launches) |> ignore; started identity |> ok; return exited } }
              use barrier = new Barrier(2)
              let contender () = Task.Run(fun () -> barrier.SignalAndWait() |> ignore; run (ports store worker))
              let left, right = contender(), contender()
              Task.WaitAll [| left :> Task; right :> Task |]
              Assert.equal 1 launches
              Assert.equal 1 ([ left.Result; right.Result ] |> List.filter Result.isOk |> List.length)))
          t "actual subprocess mechanism receives the exact packet with synthetic fixture identity" (fun () -> fixture (fun _ _ store ->
              let selected = { LocalWorkerProcessTests.spec "echo" [] with Input = LocalAgentHandoffJson.renderPacket packet }
              let worker = { Run = LocalWorkerProcess.runWithIdentityObservation (fun _ -> Ok identity) }
              let report = run { ports store worker with SelectSpec = fun _ -> Ok selected } |> ok
              Assert.equal false report.ReconciliationRequired
              let result = report.Observation |> Option.get
              Assert.equal (LocalWorkerProcessOutcome.Exited 0) result.Outcome
              use json = JsonDocument.Parse result.StandardOutput
              Assert.equal selected.Input (json.RootElement.GetProperty("input").GetString())
              Assert.equal (LocalDispatchPhase.Exited identity) (phase store)))
          t "cancellation before dispatch leaves no reservation or launch" (fun () -> fixture (fun _ _ store ->
              use cancellation = new CancellationTokenSource()
              cancellation.Cancel()
              let baseline = ports store { Run = fun _ _ _ -> failwith "cancelled dispatch launched" }
              let report = LocalJournaledDispatch.run { baseline with SelectSpec = fun _ -> failwith "cancelled dispatch selected worker" } packet cancellation.Token |> fun task -> task.GetAwaiter().GetResult()
              Assert.isTrue (Result.isError report) "cancelled dispatch accepted"
              Assert.isTrue (Result.isError (store.Load packet.RepositoryIdentity packet.DispatchId)) "cancelled dispatch wrote reservation")) ]
