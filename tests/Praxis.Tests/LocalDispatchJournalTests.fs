namespace Praxis.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module LocalDispatchJournalTests =
    let private t name run = { Name = "local dispatch: " + name; Run = run }
    let private packet = LocalAgentHandoffTests.packet
    let private now = packet.IssuedAt.AddMinutes 5.
    let private reservation = { Packet = packet; ReservedAt = now; ControllerSessionId = "controller-incarnation-1" }
    let private empty = { Reservation = reservation; Events = [] }
    let private event sequence kind identity =
        { Sequence = sequence; At = now.AddSeconds(float sequence); Kind = kind; ProcessIdentity = identity; Detail = "host observation fixture" }
    let private intent = event 1 LocalDispatchEventKind.LaunchIntent None
    let private started = event 2 LocalDispatchEventKind.LaunchObserved (Some "pid:42/start:unique-1")
    let private exited = event 3 LocalDispatchEventKind.ExitObserved started.ProcessIdentity
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private rejects result = Assert.isTrue (Result.isError result) "unsafe journal operation succeeded"
    let private fixture run =
        let root = CliHarness.temporaryDirectory "praxis-dispatch-journal"
        let repo, host = Path.Combine(root, "repo"), Path.Combine(root, "host")
        Directory.CreateDirectory repo |> ignore
        Directory.CreateDirectory host |> ignore
        try run repo host (FileLocalDispatchJournal.create repo host |> ok)
        finally Directory.Delete(root, true)
    let private ports store =
        { Store = store; Authority = fun () -> Ok { Enabled = true; Revision = packet.AuthorityRevision; ExpectedPacket = packet }
          Prerequisites = fun () -> Ok(packet.Prerequisites |> List.map (fun p -> p.WorkItemId, p.EvidenceDigest) |> Map.ofList)
          NativeRecovery = fun () -> Ok()
          ControllerSessionId = fun () -> Ok reservation.ControllerSessionId
          Now = fun () -> now }

    let tests =
        [ t "reservation and intent survive reload without giving restart permission" (fun () -> fixture (fun repo host store ->
              Assert.equal (Ok LocalDispatchReservationOutcome.Created) (LocalDispatchReservation.reserve (ports store) packet)
              Assert.equal (Ok LocalDispatchReservationOutcome.Existing) (LocalDispatchReservation.reserve (ports store) packet)
              Assert.equal (Ok true) (store.Load packet.RepositoryIdentity packet.DispatchId |> Result.bind LocalDispatchJournal.needsReconciliation)
              Assert.equal (Ok()) (LocalDispatchReservation.recordLaunchIntent (ports store) packet.RepositoryIdentity packet.DispatchId)
              let reopened = FileLocalDispatchJournal.create repo host |> ok
              let journal = reopened.Load packet.RepositoryIdentity packet.DispatchId |> ok
              Assert.equal (Ok LocalDispatchPhase.LaunchUncertain) (LocalDispatchJournal.phase journal)
              rejects (LocalDispatchReservation.recordLaunchIntent (ports store) packet.RepositoryIdentity packet.DispatchId)))
          t "a new controller incarnation cannot launch the old reservation" (fun () -> fixture (fun _ _ store ->
              LocalDispatchReservation.reserve (ports store) packet |> ok |> ignore
              let restarted = { ports store with ControllerSessionId = fun () -> Ok "controller-incarnation-2" }
              Assert.equal (Ok LocalDispatchReservationOutcome.Existing) (LocalDispatchReservation.reserve restarted packet)
              rejects (LocalDispatchReservation.recordLaunchIntent restarted packet.RepositoryIdentity packet.DispatchId)
              Assert.equal [] (store.Load packet.RepositoryIdentity packet.DispatchId |> ok).Events))
          t "native recovery, revocation, expiry and dependency changes refuse intent before append" (fun () -> fixture (fun _ _ store ->
              let basePorts = ports store
              LocalDispatchReservation.reserve basePorts packet |> ok |> ignore
              for refused in [ { basePorts with NativeRecovery = fun () -> Error "pending native transaction" }
                               { basePorts with Authority = fun () -> Ok { Enabled = false; Revision = packet.AuthorityRevision; ExpectedPacket = packet } }
                               { basePorts with Now = fun () -> packet.ExpiresAt }
                               { basePorts with Prerequisites = fun () -> Ok Map.empty } ] do
                  rejects (LocalDispatchReservation.recordLaunchIntent refused packet.RepositoryIdentity packet.DispatchId)
                  Assert.equal [] (store.Load packet.RepositoryIdentity packet.DispatchId |> ok).Events))
          t "observed process start and exit bind exact process incarnation and never complete work" (fun () -> fixture (fun _ _ store ->
              store.Reserve reservation |> ok |> ignore
              store.Append packet.RepositoryIdentity packet.DispatchId intent |> ok
              store.Append packet.RepositoryIdentity packet.DispatchId started |> ok
              rejects (store.Append packet.RepositoryIdentity packet.DispatchId { exited with ProcessIdentity = Some "pid:42/start:reused-pid" })
              store.Append packet.RepositoryIdentity packet.DispatchId exited |> ok
              let journal = store.Load packet.RepositoryIdentity packet.DispatchId |> ok
              Assert.equal (Ok(LocalDispatchPhase.Exited "pid:42/start:unique-1")) (LocalDispatchJournal.phase journal)
              rejects (store.Append packet.RepositoryIdentity packet.DispatchId { intent with Sequence = 4 })
              Assert.equal (Ok LocalDispatchReservationOutcome.Existing) (store.Reserve reservation)))
          t "confirmed no-start is terminal and cannot be recycled into a launch" (fun () ->
              let confirmed = LocalDispatchJournal.append empty (event 1 LocalDispatchEventKind.NoStartObserved None) |> ok
              Assert.equal (Ok LocalDispatchPhase.NoStart) (LocalDispatchJournal.phase confirmed)
              rejects (LocalDispatchJournal.append confirmed { intent with Sequence = 2 })
              let uncertain = LocalDispatchJournal.append empty intent |> ok
              Assert.equal (Ok true) (LocalDispatchJournal.needsReconciliation uncertain)
              rejects (LocalDispatchJournal.append uncertain { intent with Sequence = 2 }))
          t "strict journal shapes refuse duplicated fields, gaps, changed clocks and illegal observed events" (fun () ->
              Assert.equal (Ok reservation) (LocalDispatchJournalJson.renderReservation reservation |> LocalDispatchJournalJson.readReservation)
              Assert.equal (Ok intent) (LocalDispatchJournalJson.renderEvent intent |> LocalDispatchJournalJson.readEvent)
              LocalDispatchJournalJson.renderEvent intent |> fun json -> json.Replace("\"sequence\":", "\"sequence\":1,\"sequence\":") |> LocalDispatchJournalJson.readEvent |> rejects
              for bad in [ { intent with Sequence = 2 }; { intent with At = now.AddSeconds(-1.) }; { intent with At = packet.ExpiresAt }
                           { intent with ProcessIdentity = Some "worker-claim" }; started; exited ] do
                  rejects (LocalDispatchJournal.append empty bad))
          t "competing reservations have exactly one exclusive winner" (fun () -> fixture (fun _ _ store ->
              for iteration in 1..50 do
                  let candidate = { reservation with Packet = { packet with DispatchId = "dispatch-race-" + string iteration } }
                  use barrier = new Barrier(2)
                  let race () = Task.Run(fun () -> barrier.SignalAndWait() |> ignore; store.Reserve candidate)
                  let left, right = race(), race()
                  Task.WaitAll [| left :> Task; right :> Task |]
                  Assert.equal 1 ([ left.Result; right.Result ] |> List.filter ((=) (Ok LocalDispatchReservationOutcome.Created)) |> List.length)
                  Assert.equal candidate (store.Load packet.RepositoryIdentity candidate.Packet.DispatchId |> ok).Reservation))
          t "competing intent writes have one winner and one durable event" (fun () -> fixture (fun _ _ store ->
              store.Reserve reservation |> ok |> ignore
              use barrier = new Barrier(2)
              let race () = Task.Run(fun () -> barrier.SignalAndWait() |> ignore; store.Append packet.RepositoryIdentity packet.DispatchId intent)
              let left, right = race(), race()
              Task.WaitAll [| left :> Task; right :> Task |]
              Assert.equal 1 ([ left.Result; right.Result ] |> List.filter Result.isOk |> List.length)
              Assert.equal [ intent ] (store.Load packet.RepositoryIdentity packet.DispatchId |> ok).Events))
          t "partial reservations and events are retained and refuse recovery or overwrite" (fun () -> fixture (fun _ host store ->
              store.Reserve reservation |> ok |> ignore
              let target = Directory.GetDirectories(host) |> Array.exactlyOne
              let file = Path.Combine(target, "reservation.json")
              File.WriteAllText(file, "{")
              rejects (store.Load packet.RepositoryIdentity packet.DispatchId)
              rejects (store.Reserve reservation)
              Assert.equal "{" (File.ReadAllText file)
              File.WriteAllText(file, LocalDispatchJournalJson.renderReservation reservation)
              let partial = Path.Combine(target, "event-000001.json")
              File.WriteAllText(partial, "{")
              rejects (store.Load packet.RepositoryIdentity packet.DispatchId)
              rejects (store.Append packet.RepositoryIdentity packet.DispatchId intent)
              Assert.equal "{" (File.ReadAllText partial)))
          t "immutable dispatch binding, missing sequence and host-root links are refused" (fun () -> fixture (fun repo host store ->
              store.Reserve reservation |> ok |> ignore
              rejects (store.Reserve { reservation with Packet = { packet with AttemptId = "another-attempt" } })
              store.Append packet.RepositoryIdentity packet.DispatchId intent |> ok
              let target = Directory.GetDirectories(host) |> Array.exactlyOne
              File.Move(Path.Combine(target, "event-000001.json"), Path.Combine(target, "event-000002.json"))
              rejects (store.Load packet.RepositoryIdentity packet.DispatchId)
              rejects (FileLocalDispatchJournal.create repo repo)
              if not (OperatingSystem.IsWindows()) then
                  let link = Path.Combine(Path.GetDirectoryName host, "host-link")
                  Directory.CreateSymbolicLink(link, host) |> ignore
                  rejects (FileLocalDispatchJournal.create repo link))) ]
