namespace Praxis.Contracts.Work

open System
open System.Globalization
open System.Text.Json
open Praxis.Domain.Work

[<RequireQualifiedAccess>]
module LocalDispatchJournalJson =
    let private fields names (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then invalidOp "expected object"
        let actual = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        if actual.Length <> List.length names || Set.ofList actual <> Set.ofList names then invalidOp "unexpected, missing or duplicate journal fields"
    let private text name (value: JsonElement) =
        let field = value.GetProperty(name: string)
        if field.ValueKind <> JsonValueKind.String then invalidOp "expected string"
        field.GetString()
    let private time name value =
        match DateTimeOffset.TryParseExact(text name value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, parsed when parsed.Offset = TimeSpan.Zero -> parsed
        | _ -> invalidOp "expected canonical UTC time"
    let private read parse (json: string) =
        try
            if isNull json || System.Text.Encoding.UTF8.GetByteCount json > 131072 then invalidOp "oversized journal document"
            use document = JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = 14))
            Ok(parse document.RootElement)
        with
        | :? JsonException as e -> Error e.Message
        | :? InvalidOperationException as e -> Error e.Message
        | :? System.Collections.Generic.KeyNotFoundException as e -> Error e.Message
        | :? FormatException as e -> Error e.Message
    let renderReservation (reservation: LocalDispatchReservation) =
        "{\"schemaVersion\":\"praxis.local-dispatch-reservation/1\",\"reservedAt\":"
        + JsonSerializer.Serialize(reservation.ReservedAt.ToString("O")) + ",\"controllerSessionId\":" + JsonSerializer.Serialize(reservation.ControllerSessionId) + ",\"packet\":"
        + LocalAgentHandoffJson.renderPacket reservation.Packet + "}"
    let readReservation json =
        read (fun value ->
            fields [ "schemaVersion"; "reservedAt"; "controllerSessionId"; "packet" ] value
            if text "schemaVersion" value <> "praxis.local-dispatch-reservation/1" then invalidOp "unknown reservation schema"
            let packet = LocalAgentHandoffJson.readPacket (value.GetProperty("packet").GetRawText()) |> function Ok p -> p | Error e -> invalidOp e
            let reservation = { Packet = packet; ReservedAt = time "reservedAt" value; ControllerSessionId = text "controllerSessionId" value }
            LocalDispatchJournal.phase { Reservation = reservation; Events = [] } |> function Ok _ -> reservation | Error e -> invalidOp e) json
    let private code = function
        | LocalDispatchEventKind.LaunchIntent -> "launch-intent"
        | LocalDispatchEventKind.LaunchObserved -> "launch-observed"
        | LocalDispatchEventKind.NoStartObserved -> "no-start-observed"
        | LocalDispatchEventKind.ExitObserved -> "exit-observed"
    let renderEvent (event: LocalDispatchEvent) =
        JsonSerializer.Serialize
            {| schemaVersion = "praxis.local-dispatch-event/1"; sequence = event.Sequence
               at = event.At.ToString("O"); kind = code event.Kind
               processIdentity = event.ProcessIdentity |> Option.toObj; detail = event.Detail |}
    let readEvent json =
        read (fun value ->
            fields [ "schemaVersion"; "sequence"; "at"; "kind"; "processIdentity"; "detail" ] value
            if text "schemaVersion" value <> "praxis.local-dispatch-event/1" then invalidOp "unknown event schema"
            let kind =
                match text "kind" value with
                | "launch-intent" -> LocalDispatchEventKind.LaunchIntent
                | "launch-observed" -> LocalDispatchEventKind.LaunchObserved
                | "no-start-observed" -> LocalDispatchEventKind.NoStartObserved
                | "exit-observed" -> LocalDispatchEventKind.ExitObserved
                | _ -> invalidOp "unknown event kind"
            { Sequence = value.GetProperty("sequence").GetInt32(); At = time "at" value; Kind = kind
              ProcessIdentity = if value.GetProperty("processIdentity").ValueKind = JsonValueKind.Null then None else Some(text "processIdentity" value)
              Detail = text "detail" value }) json
