namespace Praxis.Infrastructure.Work

open System
open System.IO
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work

/// Isolated host-journal mechanism. The host must provision/protect its root
/// outside the repository; this implementation does not establish OS trust.
/// CreateNew plus disk flush prevents competing accepted reservations/events.
/// Partial files remain evidence and refuse recovery, never get overwritten.
[<RequireQualifiedAccess>]
module FileLocalDispatchJournal =
    let private noLinks = LocalHostJournalFiles.noLinks
    let private readBounded = LocalHostJournalFiles.readBounded
    let private writeExclusive = LocalHostJournalFiles.writeExclusive

    let create (repositoryRoot: string) (storeRoot: string) : Result<LocalDispatchJournalStore, string> =
        try
            let root = LocalHostJournalFiles.rootOutside repositoryRoot storeRoot
            let directory repositoryIdentity dispatchId = LocalHostJournalFiles.directory root repositoryIdentity dispatchId
            let guarded operation =
                try operation() with e -> Error("local dispatch journal refused: " + e.Message)
            let load repositoryIdentity dispatchId = guarded (fun () ->
                let target = directory repositoryIdentity dispatchId
                let names = Directory.GetFileSystemEntries target |> Array.map Path.GetFileName |> Array.sort
                if names.Length = 0 || names.Length > 4 || not (Array.contains "reservation.json" names) then invalidOp "incomplete dispatch journal"
                for name in names do
                    if name <> "reservation.json" && not ([ "event-000001.json"; "event-000002.json"; "event-000003.json" ] |> List.contains name) then invalidOp "unexpected dispatch journal entry"
                    if not (noLinks (Path.Combine(target, name))) then invalidOp "dispatch journal entry contains a link"
                let reservation = readBounded (Path.Combine(target, "reservation.json")) |> LocalDispatchJournalJson.readReservation |> function Ok r -> r | Error e -> invalidOp e
                if reservation.Packet.RepositoryIdentity <> repositoryIdentity || reservation.Packet.DispatchId <> dispatchId then invalidOp "dispatch journal identity differs"
                let eventNames = names |> Array.filter ((<>) "reservation.json")
                eventNames |> Array.iteri (fun index name -> if name <> sprintf "event-%06d.json" (index + 1) then invalidOp "dispatch journal sequence has a gap")
                let events = eventNames |> Array.map (fun name ->
                    readBounded (Path.Combine(target, name)) |> LocalDispatchJournalJson.readEvent |> function Ok e -> e | Error error -> invalidOp error) |> Array.toList
                let journal = { Reservation = reservation; Events = events }
                LocalDispatchJournal.phase journal |> Result.map (fun _ -> journal))
            let reserve (reservation: LocalDispatchReservation) = guarded (fun () ->
                LocalDispatchJournal.phase { Reservation = reservation; Events = [] } |> Result.bind (fun _ ->
                    let p = reservation.Packet
                    let target = directory p.RepositoryIdentity p.DispatchId
                    Directory.CreateDirectory target |> ignore
                    let file = Path.Combine(target, "reservation.json")
                    if not (noLinks file) then invalidOp "dispatch reservation contains a link"
                    try
                        if Directory.GetFileSystemEntries(target).Length <> 0 then raise (IOException "dispatch already reserved or incomplete")
                        writeExclusive file (LocalDispatchJournalJson.renderReservation reservation)
                        Ok LocalDispatchReservationOutcome.Created
                    with :? IOException ->
                        load p.RepositoryIdentity p.DispatchId |> Result.bind (fun existing ->
                            if LocalAgentHandoff.canonicalPacket existing.Reservation.Packet = LocalAgentHandoff.canonicalPacket p then Ok LocalDispatchReservationOutcome.Existing
                            else Error "dispatch identity is already bound to another immutable packet")))
            let append repositoryIdentity dispatchId event = guarded (fun () ->
                load repositoryIdentity dispatchId |> Result.bind (fun journal ->
                    LocalDispatchJournal.append journal event |> Result.bind (fun _ ->
                        let path = Path.Combine(directory repositoryIdentity dispatchId, sprintf "event-%06d.json" event.Sequence)
                        if not (noLinks path) then invalidOp "dispatch event contains a link"
                        writeExclusive path (LocalDispatchJournalJson.renderEvent event)
                        Ok())))
            Ok { Reserve = reserve; Load = load; Append = append }
        with e -> Error("cannot configure local dispatch journal: " + e.Message)
