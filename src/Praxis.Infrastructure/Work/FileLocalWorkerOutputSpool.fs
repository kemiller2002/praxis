namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Text
open System.Text.Json
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work

/// Host-owned raw transport, not accepted evidence or OS protection.
/// Existing/partial attempts are retained and never reopened for writing.
[<RequireQualifiedAccess>]
module FileLocalWorkerOutputSpool =
    let private utf8 = UTF8Encoding(false, true)
    let private readBytes path =
        use input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        let bytes = Array.zeroCreate<byte> 65537
        let mutable count = 0
        let mutable finished = false
        while count < bytes.Length && not finished do
            let read = input.Read(bytes, count, bytes.Length - count)
            if read = 0 then finished <- true else count <- count + read
        if count > 65536 then invalidOp "oversized output spool stream"
        bytes.AsSpan(0, count).ToArray()
    let private entries target expected =
        let names = Directory.GetFileSystemEntries target |> Array.map Path.GetFileName |> Array.sort
        if names <> Array.sort expected then invalidOp "incomplete or unexpected output spool"
        for name in names do
            if not (LocalHostJournalFiles.noLinks (Path.Combine(target, name))) then invalidOp "output spool contains a link"
    let private names = [| "reservation.json"; "stdout.bin"; "stderr.bin"; "seal.json" |]

    let create repositoryRoot storeRoot : Result<LocalWorkerOutputSpoolStore, string> =
        try
            let root = LocalHostJournalFiles.rootOutside repositoryRoot storeRoot
            let directory repositoryIdentity dispatchId = LocalHostJournalFiles.directory root repositoryIdentity dispatchId
            let guarded operation = try operation() with e -> Error("local output spool refused: " + e.Message)
            let load repositoryIdentity dispatchId = guarded (fun () ->
                let target = directory repositoryIdentity dispatchId
                entries target names
                let reservation = LocalHostJournalFiles.readBounded (Path.Combine(target, "reservation.json"))
                                  |> LocalDispatchJournalJson.readReservation |> function Ok value -> value | Error error -> invalidOp error
                let packet = reservation.Packet
                if packet.RepositoryIdentity <> repositoryIdentity || packet.DispatchId <> dispatchId then invalidOp "spool lookup identity differs"
                use document = JsonDocument.Parse(LocalHostJournalFiles.readBounded (Path.Combine(target, "seal.json")), JsonDocumentOptions(MaxDepth = 2))
                let value = document.RootElement
                if value.ValueKind <> JsonValueKind.Object then invalidOp "expected output seal object"
                let expected = [ "schemaVersion"; "packetDigest"; "controllerSessionId"; "processIdentity"; "exitCode"; "stdoutDigest"; "stderrDigest"; "stdoutBytes"; "stderrBytes" ]
                let actual = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                if actual.Length <> expected.Length || Set.ofList actual <> Set.ofList expected then invalidOp "unexpected, missing or duplicate seal fields"
                let text name =
                    let field = value.GetProperty(name: string)
                    if field.ValueKind <> JsonValueKind.String then invalidOp "expected seal string"
                    let content = field.GetString()
                    if String.IsNullOrWhiteSpace content || content.Length > 1024 || content.Trim() <> content || Seq.exists Char.IsControl content then invalidOp "invalid seal identity"
                    content
                if text "schemaVersion" <> "praxis.local-worker-output-seal/1"
                   || text "packetDigest" <> LocalAgentHandoff.packetDigest packet
                   || text "controllerSessionId" <> reservation.ControllerSessionId then invalidOp "seal assignment changed"
                let stdout = readBytes (Path.Combine(target, "stdout.bin"))
                let stderr = readBytes (Path.Combine(target, "stderr.bin"))
                let total = stdout.Length + stderr.Length
                if total > min 65536 packet.MaxOutputBytes
                   || stdout.Length <> value.GetProperty("stdoutBytes").GetInt32()
                   || stderr.Length <> value.GetProperty("stderrBytes").GetInt32()
                   || LocalWorkerSubmission.digest stdout <> text "stdoutDigest"
                   || LocalWorkerSubmission.digest stderr <> text "stderrDigest" then invalidOp "sealed stream bytes changed"
                let observation =
                    { ProcessIdentity = Some(text "processIdentity"); ProcessStarted = Some true; RootExitObserved = true
                      Outcome = LocalWorkerProcessOutcome.Exited(value.GetProperty("exitCode").GetInt32())
                      StandardOutput = utf8.GetString stdout; StandardError = utf8.GetString stderr; CapturedBytes = total }
                Ok { Reservation = reservation; Observation = observation })
            let openSpool reservation = guarded (fun () ->
                LocalDispatchJournal.phase { Reservation = reservation; Events = [] } |> Result.bind (fun _ ->
                    let packet = reservation.Packet
                    let target = directory packet.RepositoryIdentity packet.DispatchId
                    Directory.CreateDirectory target |> ignore
                    if Directory.GetFileSystemEntries(target).Length <> 0 then invalidOp "existing output spool requires recovery; reopening refused"
                    LocalHostJournalFiles.writeExclusive (Path.Combine(target, "reservation.json")) (LocalDispatchJournalJson.renderReservation reservation)
                    let stdout = new FileStream(Path.Combine(target, "stdout.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)
                    let stderr =
                        try new FileStream(Path.Combine(target, "stderr.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)
                        with e -> stdout.Dispose(); raise e
                    let gate = obj()
                    let mutable total = 0
                    let mutable failed = false
                    let mutable closed = false
                    let close () = lock gate (fun () ->
                        if not closed then
                            closed <- true
                            try stdout.Dispose() finally stderr.Dispose())
                    let write stream (bytes: byte array) = lock gate (fun () ->
                        try
                            if failed || closed then invalidOp "output spool is closed or failed"
                            if isNull bytes || bytes.Length > min 65536 packet.MaxOutputBytes - total then invalidOp "output spool budget exceeded"
                            entries target (names |> Array.filter ((<>) "seal.json"))
                            let sink = match stream with LocalWorkerOutputStream.StandardOutput -> stdout | LocalWorkerOutputStream.StandardError -> stderr
                            sink.Write(bytes, 0, bytes.Length)
                            sink.Flush(true)
                            total <- total + bytes.Length
                            Ok()
                        with e -> failed <- true; Error("output spool write failed: " + e.Message))
                    let seal (observation: LocalWorkerProcessObservation) = lock gate (fun () ->
                        try
                            if failed || closed then invalidOp "output spool is closed or failed"
                            let identity, exitCode =
                                match observation.ProcessStarted, observation.ProcessIdentity, observation.RootExitObserved, observation.Outcome with
                                | Some true, Some identity, true, LocalWorkerProcessOutcome.Exited code
                                    when not (String.IsNullOrWhiteSpace identity) && identity.Length <= 1024 && identity.Trim() = identity && not (Seq.exists Char.IsControl identity) -> identity, code
                                | _ -> invalidOp "only complete normal-exit streams can be sealed"
                            stdout.Flush(true)
                            stderr.Flush(true)
                            entries target (names |> Array.filter ((<>) "seal.json"))
                            let outputBytes = readBytes (Path.Combine(target, "stdout.bin"))
                            let errorBytes = readBytes (Path.Combine(target, "stderr.bin"))
                            if outputBytes <> utf8.GetBytes observation.StandardOutput || errorBytes <> utf8.GetBytes observation.StandardError
                               || total <> observation.CapturedBytes || total <> outputBytes.Length + errorBytes.Length then invalidOp "spool and supervisor observations differ"
                            close()
                            let json = JsonSerializer.Serialize
                                           {| schemaVersion = "praxis.local-worker-output-seal/1"; packetDigest = LocalAgentHandoff.packetDigest packet
                                              controllerSessionId = reservation.ControllerSessionId; processIdentity = identity; exitCode = exitCode
                                              stdoutDigest = LocalWorkerSubmission.digest outputBytes; stderrDigest = LocalWorkerSubmission.digest errorBytes
                                              stdoutBytes = outputBytes.Length; stderrBytes = errorBytes.Length |}
                            LocalHostJournalFiles.writeExclusive (Path.Combine(target, "seal.json")) json
                            Ok()
                        with e -> failed <- true; Error("output spool seal failed: " + e.Message))
                    Ok { Write = write; Seal = seal; Close = close }))
            Ok { Open = openSpool; Load = load }
        with e -> Error("cannot configure local output spool: " + e.Message)
