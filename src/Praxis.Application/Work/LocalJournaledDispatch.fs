namespace Praxis.Application.Work

open System
open System.Text
open System.Threading
open Praxis.Contracts.Work
open Praxis.Domain.Work

type LocalJournaledDispatchPorts =
    { Controller: LocalDispatchControllerPorts
      Worker: LocalWorkerProcessPort
      SelectSpec: LocalWorkerPacket -> Result<LocalWorkerProcessSpec, string> }
type LocalJournaledDispatchReport =
    { Observation: LocalWorkerProcessObservation option
      JournalPhase: LocalDispatchPhase option
      ReconciliationRequired: bool
      Problems: string list }

/// Isolated composition, not a qualified controller or runtime entry point.
/// Caller holds protected policy/repository locks across authorization and launch.
/// Observed exit only closes process tracking; result intake remains separate.
[<RequireQualifiedAccess>]
module LocalJournaledDispatch =
    let private protect action =
        try action() with e -> Error("host operation failed: " + e.GetType().Name)

    let run ports (packet: LocalWorkerPacket) (cancellation: CancellationToken) = task {
        let controller = ports.Controller
        let load () =
            controller.Store.Load packet.RepositoryIdentity packet.DispatchId
            |> Result.bind (fun journal ->
                if LocalAgentHandoff.canonicalPacket journal.Reservation.Packet <> LocalAgentHandoff.canonicalPacket packet then Error "journal assignment changed"
                else controller.ControllerSessionId() |> Result.bind (fun session ->
                    if session <> journal.Reservation.ControllerSessionId then Error "controller incarnation changed"
                    else Ok journal))
        let append kind identity detail = protect (fun () ->
            load() |> Result.bind (fun journal ->
                let event = { Sequence = journal.Events.Length + 1; At = controller.Now()
                              Kind = kind; ProcessIdentity = identity; Detail = detail }
                LocalDispatchJournal.append journal event |> Result.bind (fun _ ->
                    controller.Store.Append packet.RepositoryIdentity packet.DispatchId event)))
        let prepare () =
            if cancellation.IsCancellationRequested then Error "dispatch cancelled before reservation"
            else ports.SelectSpec packet |> Result.bind (fun spec ->
                let input = LocalAgentHandoffJson.renderPacket packet
                if spec.Input <> input then Error "worker input must be the exact canonical delegated packet"
                elif Encoding.UTF8.GetByteCount input > 65536 then Error "delegated packet exceeds worker input bound"
                elif spec.TimeoutMilliseconds <= 0 || int64 spec.TimeoutMilliseconds > int64 packet.TimeoutSeconds * 1000L
                     || spec.MaxOutputBytes <= 0 || spec.MaxOutputBytes > packet.MaxOutputBytes then Error "worker budget exceeds delegation"
                else LocalDispatchReservation.reserve controller packet |> Result.bind (function
                    | LocalDispatchReservationOutcome.Existing -> Error "existing dispatch requires reconciliation; automatic launch refused"
                    | LocalDispatchReservationOutcome.Created ->
                        LocalDispatchReservation.recordLaunchIntent controller packet.RepositoryIdentity packet.DispatchId
                        |> Result.map (fun () -> spec)))
        match protect prepare with
        | Error reason -> return Error reason
        | Ok spec ->
            let mutable callbacks = 0
            let mutable recordedIdentity = None
            let mutable problems = []
            let add reason = problems <- problems @ [ reason ]
            let onStarted identity =
                callbacks <- callbacks + 1
                if callbacks <> 1 then
                    add "worker repeated start observation"
                    Error "worker repeated start observation"
                else
                    match append LocalDispatchEventKind.LaunchObserved (Some identity) "host observed worker process incarnation" with
                    | Ok() -> recordedIdentity <- Some identity; Ok()
                    | Error reason -> add ("start persistence failed: " + reason); Error reason
            let! observation = task {
                try
                    let! value = ports.Worker.Run spec onStarted cancellation
                    return Some value
                with e -> add ("worker port failed after intent: " + e.GetType().Name); return None }
            match observation with
            | None -> () // A thrown launch cannot prove no-start or permit retry.
            | Some value ->
                if value.CapturedBytes < 0 || value.CapturedBytes > spec.MaxOutputBytes then add "worker reported an invalid output budget"
                match value.ProcessStarted, value.ProcessIdentity, recordedIdentity with
                | Some false, None, None when callbacks = 0 && not value.RootExitObserved &&
                    (match value.Outcome with LocalWorkerProcessOutcome.PreflightRefused _ | LocalWorkerProcessOutcome.Cancelled -> true | _ -> false) ->
                    match append LocalDispatchEventKind.NoStartObserved None "host supervisor confirmed no process started" with
                    | Ok() -> () | Error reason -> add ("no-start persistence failed: " + reason)
                | Some true, Some actual, Some expected when actual = expected && callbacks = 1 && problems.IsEmpty ->
                    if value.RootExitObserved then
                        match append LocalDispatchEventKind.ExitObserved (Some actual) "host supervisor observed root exit; result intake pending" with
                        | Ok() -> () | Error reason -> add ("exit persistence failed: " + reason)
                    else add "worker root exit remains unconfirmed"
                | _ -> add "worker launch observations remain uncertain or inconsistent"
            let phase = protect (fun () -> load() |> Result.bind LocalDispatchJournal.phase)
            let observedPhase = match phase with Ok value -> Some value | Error reason -> add ("journal recovery failed: " + reason); None
            let reconciliation =
                not problems.IsEmpty || (match observedPhase with
                                        | Some LocalDispatchPhase.NoStart | Some(LocalDispatchPhase.Exited _) -> false
                                        | _ -> true)
            return Ok { Observation = observation; JournalPhase = observedPhase
                        ReconciliationRequired = reconciliation; Problems = problems } }
