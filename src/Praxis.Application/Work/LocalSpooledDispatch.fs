namespace Praxis.Application.Work

open System
open Praxis.Domain.Work

type LocalWorkerOutputSpoolSession =
    { Write: LocalWorkerOutputStream -> byte array -> Result<unit, string>
      Seal: LocalWorkerProcessObservation -> Result<unit, string>
      Close: unit -> unit }
type LocalWorkerSpooledOutput =
    { Reservation: LocalDispatchReservation; Observation: LocalWorkerProcessObservation }
type LocalWorkerOutputSpoolStore =
    { Open: LocalDispatchReservation -> Result<LocalWorkerOutputSpoolSession, string>
      Load: string -> string -> Result<LocalWorkerSpooledOutput, string> }
type LocalSpooledDispatchPorts =
    { Dispatch: LocalJournaledDispatchPorts; StreamingWorker: LocalWorkerStreamingProcessPort
      Spool: LocalWorkerOutputSpoolStore }
type LocalSpoolRecoveryPorts =
    { Journal: LocalDispatchJournalStore; Spool: LocalWorkerOutputSpoolStore
      Submissions: LocalWorkerSubmissionStore
      NativeRecovery: unit -> Result<unit, string>
      ProcessRecovery: LocalDispatchJournal -> Result<unit, string>
      Now: unit -> DateTimeOffset }

/// Optional isolated host composition. Spools grant neither authority nor retry.
/// Completion is sealed BEFORE journaled dispatch records root exit.
[<RequireQualifiedAccess>]
module LocalSpooledDispatch =
    let private protect operation = try operation() with e -> Error("output spool host operation failed: " + e.GetType().Name)
    let run (ports: LocalSpooledDispatchPorts) packet cancellation =
        let controller = ports.Dispatch.Controller
        let prepare () = protect (fun () ->
            controller.Store.Load packet.RepositoryIdentity packet.DispatchId |> Result.bind (fun journal ->
                if LocalAgentHandoff.canonicalPacket journal.Reservation.Packet <> LocalAgentHandoff.canonicalPacket packet then Error "spool assignment differs"
                else controller.ControllerSessionId() |> Result.bind (fun session ->
                    if session <> journal.Reservation.ControllerSessionId then Error "spool controller incarnation changed"
                    else
                        match LocalDispatchJournal.phase journal with
                        | Ok LocalDispatchPhase.LaunchUncertain -> ports.Spool.Open journal.Reservation
                        | _ -> Error "spool requires a fresh journaled launch intent")))
        let worker : LocalWorkerProcessPort =
            { Run = fun spec onStarted token -> task {
                match prepare() with
                | Error error -> return raise (InvalidOperationException error)
                | Ok spool ->
                    try
                        let! observation = ports.StreamingWorker.RunStreaming spec onStarted spool.Write token
                        match observation.Outcome with
                        | LocalWorkerProcessOutcome.Exited _ ->
                            match protect (fun () -> spool.Seal observation) with
                            | Ok() -> return observation
                            | Error error -> return { observation with Outcome = LocalWorkerProcessOutcome.StreamFailed error }
                        | _ -> return observation
                    finally spool.Close() } }
        let select packet = ports.Dispatch.SelectSpec packet |> Result.bind (fun spec ->
            if spec.MaxOutputBytes > 65536 then Error "spooled dispatch requires a combined output budget at most 64 KiB"
            else Ok spec)
        LocalJournaledDispatch.run { ports.Dispatch with Worker = worker; SelectSpec = select } packet cancellation

    /// Read-only spool recovery followed by immutable untrusted archival.
    /// Independent recovery must establish an Exited journal; files cannot do it.
    let recover (ports: LocalSpoolRecoveryPorts) repositoryIdentity dispatchId = protect (fun () ->
        ports.Journal.Load repositoryIdentity dispatchId |> Result.bind (fun journal ->
            let packet = journal.Reservation.Packet
            if packet.RepositoryIdentity <> repositoryIdentity || packet.DispatchId <> dispatchId then Error "recovery journal differs from request"
            else ports.NativeRecovery() |> Result.bind (fun () ->
                ports.ProcessRecovery journal |> Result.bind (fun () ->
                    ports.Spool.Load repositoryIdentity dispatchId |> Result.bind (fun output ->
                        if LocalAgentHandoff.canonicalPacket output.Reservation.Packet <> LocalAgentHandoff.canonicalPacket packet
                           || output.Reservation.ReservedAt <> journal.Reservation.ReservedAt
                           || output.Reservation.ControllerSessionId <> journal.Reservation.ControllerSessionId then Error "spool reservation differs from original journal"
                        else LocalDispatchJournal.phase journal |> Result.bind (fun phase ->
                            let capture : LocalSubmissionCapturePorts =
                                { Journal = ports.Journal; Submissions = ports.Submissions
                                  ControllerSessionId = fun () -> Ok journal.Reservation.ControllerSessionId
                                  Now = ports.Now }
                            LocalSubmissionIntake.capture capture packet
                                { Observation = Some output.Observation; JournalPhase = Some phase
                                  ReconciliationRequired = false; Problems = [] }))))))
