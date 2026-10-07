namespace Praxis.Infrastructure.Pacing

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open Praxis.Application.Pacing
open Praxis.Domain.Pacing

/// Typed pacing telemetry storage (PRX-QUAL-008). `events.jsonl` holds one
/// `praxis.pacing-event/1` document per line and is the evidence channel;
/// `pace.log` is its human-readable rendering. Both live in the per-user
/// pacing directory, never in repository state.
[<RequireQualifiedAccess>]
module PacingEventLog =
    let eventsPath (directory: string) = Path.Combine(directory, "events.jsonl")
    let logPath (directory: string) = Path.Combine(directory, "pace.log")

    let private iso (value: DateTimeOffset) = value.ToString("O", CultureInfo.InvariantCulture)

    /// Pure: one event as a JSON line.
    let serialize (event: PacingEvent) =
        let node = JsonObject()
        node["schema"] <- JsonValue.Create PacingEvents.Schema
        node["code"] <- JsonValue.Create(PacingEvents.code event.Code)
        node["occurredAt"] <- JsonValue.Create(iso event.OccurredAt)
        event.Provider |> Option.iter (fun provider -> node["provider"] <- JsonValue.Create(ProviderId.code provider))
        event.WindowKey |> Option.iter (fun key -> node["window"] <- JsonValue.Create key)
        event.Reason |> Option.iter (fun reason -> node["reason"] <- JsonValue.Create(PacingReasonKind.code reason))
        node["detail"] <- JsonValue.Create event.Detail
        event.ResetsAt |> Option.iter (fun value -> node["resetsAt"] <- JsonValue.Create(iso value))
        event.ObservedAt |> Option.iter (fun value -> node["observedAt"] <- JsonValue.Create(iso value))
        node.ToJsonString()

    /// Pure: one JSON line back into an event; anything else is `None`.
    let parse (line: string) : PacingEvent option =
        try
            use document = JsonDocument.Parse line
            let root = document.RootElement
            let text name = PacingJson.tryProperty name root |> Option.bind PacingJson.tryString
            let time name = PacingJson.tryProperty name root |> Option.bind PacingJson.tryTimestamp

            match text "schema", text "code" |> Option.bind PacingEvents.tryParseCode, time "occurredAt", text "detail" with
            | Some PacingEvents.Schema, Some code, Some occurredAt, Some detail ->
                Some
                    { Code = code
                      OccurredAt = occurredAt
                      Provider = text "provider" |> Option.bind ProviderId.tryParse
                      WindowKey = text "window"
                      Reason = text "reason" |> Option.bind PacingEvents.tryParseReason
                      Detail = detail
                      ResetsAt = time "resetsAt"
                      ObservedAt = time "observedAt" }
            | _ -> None
        with :? JsonException ->
            None

    /// The last recorded event, read from the end of `events.jsonl`.
    let last (directory: string) : PacingEvent option =
        let path = eventsPath directory

        try
            if not (File.Exists path) then
                None
            else
                use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                let length = min stream.Length 65536L
                stream.Seek(-length, SeekOrigin.End) |> ignore
                use reader = new StreamReader(stream, Encoding.UTF8)

                reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                |> Array.rev
                |> Array.tryPick parse
        with
        | :? IOException
        | :? UnauthorizedAccessException -> None

    /// Appends the event and its rendering. Diagnostics must never change a
    /// gate decision, so only filesystem failures are tolerated here; they
    /// are reported on standard error rather than hidden.
    let record (directory: string) (event: PacingEvent) : unit =
        let line = serialize event + "\n"
        let rendered = iso event.OccurredAt + " [" + string Environment.ProcessId + "] " + PacingEvents.render event + Environment.NewLine

        let rec append attempt =
            try
                Directory.CreateDirectory directory |> ignore

                use stream = new FileStream(eventsPath directory, FileMode.Append, FileAccess.Write, FileShare.Read)
                let bytes = Encoding.UTF8.GetBytes line
                stream.Write(bytes, 0, bytes.Length)
                File.AppendAllText(logPath directory, rendered)
            with
            | :? IOException when attempt < 20 ->
                Thread.Sleep 25
                append (attempt + 1)
            | :? IOException as error -> eprintfn "praxis pacing: event not recorded: %s" error.Message
            | :? UnauthorizedAccessException as error -> eprintfn "praxis pacing: event not recorded: %s" error.Message

        append 0

    /// A human diagnostic that is not a pacing transition (for example a
    /// cache write failure): `pace.log` only, never the typed event stream.
    let diagnostic (directory: string) (now: DateTimeOffset) (text: string) : unit =
        try
            Directory.CreateDirectory directory |> ignore
            File.AppendAllText(logPath directory, iso now + " [" + string Environment.ProcessId + "] diagnostic: " + PacingEvents.redact text + Environment.NewLine)
        with
        | :? IOException as error -> eprintfn "praxis pacing: diagnostic not recorded: %s" error.Message
        | :? UnauthorizedAccessException as error -> eprintfn "praxis pacing: diagnostic not recorded: %s" error.Message

    let sink (directory: string) : PacingEventSink =
        { Record = record directory
          Last = fun () -> last directory }
