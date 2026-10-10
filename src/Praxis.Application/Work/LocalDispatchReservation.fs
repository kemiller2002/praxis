namespace Praxis.Application.Work

open System
open Praxis.Domain.Work

type LocalDispatchJournalStore =
    { Reserve: LocalDispatchReservation -> Result<LocalDispatchReservationOutcome, string>
      Load: string -> string -> Result<LocalDispatchJournal, string>
      Append: string -> string -> LocalDispatchEvent -> Result<unit, string> }
type LocalDispatchControllerPorts =
    { Store: LocalDispatchJournalStore
      Authority: unit -> Result<LocalHandoffAuthority, string>
      Prerequisites: unit -> Result<Map<string, string>, string>
      NativeRecovery: unit -> Result<unit, string>
      ControllerSessionId: unit -> Result<string, string>
      Now: unit -> DateTimeOffset }

/// The future protected controller supplies all observations. These callbacks
/// alone do not authenticate an operator or prove OS containment. No launcher
/// Caller holds protected controller policy/repository locks through checking
/// and the exclusive journal write; an ordinary callback is not such a lock.
/// No launcher is exposed here: recording intent leaves the attempt uncertain until the
/// host reconciles the actual process identity, including its start identity.
[<RequireQualifiedAccess>]
module LocalDispatchReservation =
    let private check ports packet =
        ports.NativeRecovery() |> Result.bind (fun () ->
            ports.Authority() |> Result.bind (fun authority ->
                ports.Prerequisites() |> Result.bind (fun dependencies ->
                    let now = ports.Now()
                    LocalAgentHandoff.validatePacket authority now dependencies packet
                    |> Result.mapError (String.concat "; ") |> Result.map (fun () -> now))))

    let reserve ports packet =
        ports.ControllerSessionId() |> Result.bind (fun identity ->
            check ports packet |> Result.bind (fun now ->
                ports.Store.Reserve { Packet = packet; ReservedAt = now; ControllerSessionId = identity }))

    let recordLaunchIntent ports repositoryIdentity dispatchId =
        ports.Store.Load repositoryIdentity dispatchId |> Result.bind (fun journal ->
            ports.ControllerSessionId() |> Result.bind (fun identity ->
              if identity <> journal.Reservation.ControllerSessionId then Error "local dispatch needs previous controller-session reconciliation"
              else check ports journal.Reservation.Packet |> Result.bind (fun now ->
                let event = { Sequence = journal.Events.Length + 1; At = now; Kind = LocalDispatchEventKind.LaunchIntent
                              ProcessIdentity = None; Detail = "controller persisted launch intent; start outcome requires observation" }
                LocalDispatchJournal.append journal event |> Result.bind (fun _ -> ports.Store.Append repositoryIdentity dispatchId event))))
