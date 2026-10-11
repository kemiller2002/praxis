namespace Praxis.Contracts.Work

open System
open System.Globalization
open System.Text
open System.Text.Json
open Praxis.Domain.Work

[<RequireQualifiedAccess>]
module LocalWorkerSubmissionJson =
    let render (submission: LocalWorkerSubmission) =
        let bytes = LocalWorkerSubmission.payloadBytes submission.Payload |> function Ok bytes -> bytes | Error error -> invalidOp error
        JsonSerializer.Serialize
            {| schemaVersion = submission.SchemaVersion; repositoryIdentity = submission.RepositoryIdentity
               dispatchId = submission.DispatchId; attemptId = submission.AttemptId; packetDigest = submission.PacketDigest
               processIdentity = submission.ProcessIdentity; capturedAt = submission.CapturedAt.ToString("O")
               exitCode = submission.ExitCode; payloadBase64 = Convert.ToBase64String bytes; payloadDigest = submission.PayloadDigest |}
    let read (json: string) =
        try
            if isNull json || Encoding.UTF8.GetByteCount json > 131072 then invalidOp "oversized submission record"
            use document = JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = 2))
            let value = document.RootElement
            if value.ValueKind <> JsonValueKind.Object then invalidOp "expected submission object"
            let names = [ "schemaVersion"; "repositoryIdentity"; "dispatchId"; "attemptId"; "packetDigest"; "processIdentity"; "capturedAt"; "exitCode"; "payloadBase64"; "payloadDigest" ]
            let actual = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            if actual.Length <> names.Length || Set.ofList actual <> Set.ofList names then invalidOp "unexpected, missing or duplicate submission fields"
            let text name =
                let field = value.GetProperty(name: string)
                if field.ValueKind <> JsonValueKind.String then invalidOp "expected submission string"
                field.GetString()
            let capturedAt =
                match DateTimeOffset.TryParseExact(text "capturedAt", "O", CultureInfo.InvariantCulture, DateTimeStyles.None) with
                | true, time when time.Offset = TimeSpan.Zero -> time
                | _ -> invalidOp "expected canonical UTC capture time"
            let encoded = text "payloadBase64"
            if encoded.Length > 87384 then invalidOp "oversized encoded submission"
            let bytes = Convert.FromBase64String encoded
            if bytes.Length > 65536 || Convert.ToBase64String bytes <> encoded then invalidOp "invalid canonical submission encoding"
            let submission =
                { SchemaVersion = text "schemaVersion"; RepositoryIdentity = text "repositoryIdentity"; DispatchId = text "dispatchId"
                  AttemptId = text "attemptId"; PacketDigest = text "packetDigest"; ProcessIdentity = text "processIdentity"
                  CapturedAt = capturedAt; ExitCode = value.GetProperty("exitCode").GetInt32()
                  Payload = UTF8Encoding(false, true).GetString bytes; PayloadDigest = text "payloadDigest" }
            let problems = LocalWorkerSubmission.problems submission
            if not problems.IsEmpty then invalidOp (String.concat "; " problems)
            Ok submission
        with
        | :? JsonException as e -> Error e.Message
        | :? InvalidOperationException as e -> Error e.Message
        | :? System.Collections.Generic.KeyNotFoundException as e -> Error e.Message
        | :? FormatException as e -> Error e.Message
        | :? DecoderFallbackException as e -> Error e.Message
