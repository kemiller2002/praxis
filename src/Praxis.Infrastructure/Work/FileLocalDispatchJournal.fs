namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work

/// Isolated host-journal mechanism. The host must provision/protect its root
/// outside the repository; this implementation does not establish OS trust.
/// CreateNew plus disk flush prevents competing accepted reservations/events.
/// Partial files remain evidence and refuse recovery, never get overwritten.
[<RequireQualifiedAccess>]
module FileLocalDispatchJournal =
    let private noLinks path =
        let mutable current = DirectoryInfo(Path.GetFullPath path)
        let mutable safe = true
        while not (isNull current) && safe do
            safe <- current.LinkTarget = null
            current <- current.Parent
        safe && FileInfo(path).LinkTarget = null

    let private readBounded path =
        use input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        let buffer = Array.zeroCreate<byte> 131073
        let mutable count = 0
        let mutable finished = false
        while count < buffer.Length && not finished do
            let read = input.Read(buffer, count, buffer.Length - count)
            if read = 0 then finished <- true else count <- count + read
        if count > 131072 then invalidOp "oversized dispatch journal"
        UTF8Encoding(false, true).GetString(buffer, 0, count)

    let private writeExclusive path content =
        use output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
        let bytes = Encoding.UTF8.GetBytes(content: string)
        if bytes.Length > 131072 then invalidOp "oversized dispatch journal"
        output.Write(bytes, 0, bytes.Length)
        output.Flush(true)

    let create (repositoryRoot: string) (storeRoot: string) : Result<LocalDispatchJournalStore, string> =
        try
            let repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath repositoryRoot)
            if not (Path.IsPathFullyQualified storeRoot) then invalidOp "dispatch journal root must be absolute"
            let root = Path.TrimEndingDirectorySeparator(Path.GetFullPath storeRoot)
            let comparison = StringComparison.OrdinalIgnoreCase
            let repositoryPrefix = if Path.EndsInDirectorySeparator repository then repository else repository + string Path.DirectorySeparatorChar
            if String.Equals(root, repository, comparison) || root.StartsWith(repositoryPrefix, comparison) then invalidOp "dispatch journal root must be outside the repository"
            if not (Directory.Exists root) || not (noLinks root) then invalidOp "host must provision a non-link dispatch journal directory"
            let directory repositoryIdentity dispatchId =
                let key = Encoding.UTF8.GetBytes(repositoryIdentity + "\u0000" + dispatchId) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
                let target = Path.Combine(root, key)
                if not (noLinks root && noLinks target) then invalidOp "dispatch journal path contains a link"
                target
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
