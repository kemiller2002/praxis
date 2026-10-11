namespace Praxis.Domain.Work

open System

[<RequireQualifiedAccess>]
type LocalDispatchEventKind = LaunchIntent | LaunchObserved | NoStartObserved | ExitObserved
type LocalDispatchEvent =
    { Sequence: int; At: DateTimeOffset; Kind: LocalDispatchEventKind
      ProcessIdentity: string option; Detail: string }
type LocalDispatchReservation = { Packet: LocalWorkerPacket; ReservedAt: DateTimeOffset; ControllerSessionId: string }
type LocalDispatchJournal = { Reservation: LocalDispatchReservation; Events: LocalDispatchEvent list }
[<RequireQualifiedAccess>]
type LocalDispatchPhase = Reserved | LaunchUncertain | Running of string | NoStart | Exited of string
[<RequireQualifiedAccess>]
type LocalDispatchReservationOutcome = Created | Existing

/// Journal facts are controller observations, never worker authorization.
/// Exiting is not completion or permission to integrate. An uncertain start
/// requires external reconciliation; no transition launches the attempt again.
[<RequireQualifiedAccess>]
module LocalDispatchJournal =
    let private clean (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Length <= 1024 && value = value.Trim()
        && not (value |> Seq.exists Char.IsControl)

    let private apply phase (event: LocalDispatchEvent) =
        if event.At.Offset <> TimeSpan.Zero || not (clean event.Detail)
           || event.ProcessIdentity |> Option.exists (clean >> not) then Error "local-dispatch-event-shape"
        else
            match phase, event.Kind, event.ProcessIdentity with
            | LocalDispatchPhase.Reserved, LocalDispatchEventKind.LaunchIntent, None -> Ok LocalDispatchPhase.LaunchUncertain
            | LocalDispatchPhase.LaunchUncertain, LocalDispatchEventKind.LaunchObserved, Some identity -> Ok(LocalDispatchPhase.Running identity)
            | (LocalDispatchPhase.Reserved | LocalDispatchPhase.LaunchUncertain), LocalDispatchEventKind.NoStartObserved, None -> Ok LocalDispatchPhase.NoStart
            | LocalDispatchPhase.Running expected, LocalDispatchEventKind.ExitObserved, Some actual when actual = expected -> Ok(LocalDispatchPhase.Exited expected)
            | _ -> Error "local-dispatch-illegal-transition"

    let phase (journal: LocalDispatchJournal) =
        let reservation = journal.Reservation
        if not (clean reservation.ControllerSessionId) || not (LocalAgentHandoff.packetProblems reservation.Packet).IsEmpty
           || reservation.ReservedAt.Offset <> TimeSpan.Zero
           || reservation.ReservedAt < reservation.Packet.IssuedAt || reservation.ReservedAt >= reservation.Packet.ExpiresAt then Error "local-dispatch-reservation-invalid"
        elif journal.Events.Length > 3 then Error "local-dispatch-too-many-events"
        else
            let rec fold state previous sequence remaining =
                match remaining with
                | [] -> Ok state
                | event :: _ when event.Sequence <> sequence || event.At < previous -> Error "local-dispatch-event-order"
                | event :: _ when event.Kind = LocalDispatchEventKind.LaunchIntent && event.At >= reservation.Packet.ExpiresAt -> Error "local-dispatch-launch-expired"
                | event :: rest -> apply state event |> Result.bind (fun next -> fold next event.At (sequence + 1) rest)
            fold LocalDispatchPhase.Reserved reservation.ReservedAt 1 journal.Events

    let append (journal: LocalDispatchJournal) (event: LocalDispatchEvent) =
        let candidate = { journal with Events = journal.Events @ [ event ] }
        phase candidate |> Result.map (fun _ -> candidate)

    let needsReconciliation journal =
        phase journal |> Result.map (function
            | LocalDispatchPhase.Reserved | LocalDispatchPhase.LaunchUncertain | LocalDispatchPhase.Running _ -> true
            | LocalDispatchPhase.NoStart | LocalDispatchPhase.Exited _ -> false)
