namespace Praxis.Infrastructure.Work

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Work

/// `praxis.validation-result/1` (PRAXIS-REL-005): the document `work
/// validate-run` writes and `work complete-verified` accepts as evidence.
/// Rendering is deterministic (fixed member order, indented, LF, trailing
/// newline) so the digest recorded in `work.validation.observed` is the
/// digest of exactly these bytes. Parsing is strict: every member, nothing
/// unknown, known enum values only.
[<RequireQualifiedAccess>]
module ValidationResultJson =
    let private members =
        [ "schemaVersion"; "workItemId"; "claimant"; "claimToken"; "generation"; "validation"; "evidenceId"; "revision"
          "observedAt"; "startedAt"; "status"; "exitCode"; "command"; "outputSha256" ]

    let node (result: ValidationResult) : JsonNode =
        let str (value: string) : JsonNode = JsonValue.Create value
        let time value = str (WorkClaimJson.stamp value)
        let command = JsonArray()
        result.Command |> List.iter (fun part -> command.Add(str part))
        let root = JsonObject()
        root["schemaVersion"] <- str ValidationEvidence.SchemaVersion
        root["workItemId"] <- str result.WorkItemId
        root["claimant"] <- str result.Claimant
        root["claimToken"] <- str result.ClaimToken
        root["generation"] <- JsonValue.Create result.Generation
        root["validation"] <- str result.Validation
        root["evidenceId"] <- str result.EvidenceId
        root["revision"] <- str result.Revision
        root["observedAt"] <- time result.ObservedAt
        root["startedAt"] <- time result.StartedAt
        root["status"] <- str (CheckStatus.code result.Status)
        root["exitCode"] <- (result.ExitCode |> Option.map (fun code -> JsonValue.Create code :> JsonNode) |> Option.toObj)
        root["command"] <- command
        root["outputSha256"] <- str result.OutputSha256
        root

    let render (result: ValidationResult) =
        let options = JsonSerializerOptions(WriteIndented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
        (node result).ToJsonString(options).Replace("\r\n", "\n") + "\n"

    let private text (root: JsonElement) (name: string) =
        match root.GetProperty name with
        | value when value.ValueKind = JsonValueKind.String && value.GetString().Length > 0 -> value.GetString()
        | _ -> failwith $"'{name}' must be a non-empty string"

    let private instant (root: JsonElement) (name: string) =
        match DateTimeOffset.TryParseExact(text root name, "yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal) with
        | true, value -> value
        | _ -> failwith $"'{name}' must be a UTC timestamp yyyy-MM-ddTHH:mm:ss.fffZ"

    let parse (json: string) : Result<ValidationResult, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Error "not a JSON object"
            else
                let names = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList

                match root.TryGetProperty "schemaVersion" with
                | true, version when version.ValueKind = JsonValueKind.String && version.GetString() = ValidationEvidence.SchemaVersion ->
                    if List.sort names <> List.sort members then
                        Error $"members differ from {ValidationEvidence.SchemaVersion}"
                    else
                        let generation = root.GetProperty "generation"
                        let exitCode = root.GetProperty "exitCode"
                        let command = root.GetProperty "command"

                        if generation.ValueKind <> JsonValueKind.Number then failwith "'generation' must be an integer"
                        if command.ValueKind <> JsonValueKind.Array then failwith "'command' must be an array"

                        Ok
                            { WorkItemId = text root "workItemId"
                              Claimant = text root "claimant"
                              ClaimToken = text root "claimToken"
                              Generation = generation.GetInt64()
                              Validation = text root "validation"
                              EvidenceId = text root "evidenceId"
                              Revision = text root "revision"
                              ObservedAt = instant root "observedAt"
                              StartedAt = instant root "startedAt"
                              Status =
                                text root "status"
                                |> CheckStatus.tryParse
                                |> Option.defaultWith (fun () -> failwith "'status' must be passed, failed or unavailable")
                              ExitCode =
                                match exitCode.ValueKind with
                                | JsonValueKind.Null -> None
                                | JsonValueKind.Number -> Some(exitCode.GetInt32())
                                | _ -> failwith "'exitCode' must be an integer or null"
                              Command =
                                command.EnumerateArray()
                                |> Seq.map (fun part -> if part.ValueKind = JsonValueKind.String then part.GetString() else failwith "'command' must hold strings")
                                |> Seq.toList
                              OutputSha256 =
                                match text root "outputSha256" with
                                | digest when CompletionGate.isSha256Digest digest -> digest
                                | _ -> failwith "'outputSha256' must be sha256:<64 hex>" }
                | _ -> Error $"schemaVersion is not {ValidationEvidence.SchemaVersion}"
        with error ->
            Error error.Message

    /// The bytes of `path` as evidence: a recognised result, or not.
    let observe (bytes: byte array) =
        let digest = CompletionGate.sha256 bytes

        let decoded =
            try
                Ok(Text.UTF8Encoding(false, true).GetString bytes)
            with _ ->
                Error "not UTF-8 text"

        match decoded with
        | Ok json when VerificationReceipt.looksLikeEnvelope json ->
            match VerificationReceipt.parseEnvelope json with
            | Ok envelope -> EvidenceObservation.Enveloped(VerificationReceipt.identity envelope.Payload, envelope)
            | Error reason -> EvidenceObservation.MalformedEnvelope(digest, reason)
        | _ ->
            match decoded |> Result.bind parse with
            | Ok result -> EvidenceObservation.Observed(digest, result)
            | Error reason -> EvidenceObservation.Unrecognized(digest, reason)

/// Reads `work.validation.observed` events back from the out-of-tree I1 log
/// (`TransientStore.events`, I8) every claim event is appended to. Read-only.
[<RequireQualifiedAccess>]
module FileValidationEvents =
    let private recorded (workItemId: string) (line: string) =
        use document = JsonDocument.Parse line
        let root = document.RootElement

        let text name =
            match root.TryGetProperty(name: string) with
            | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
            | _ -> None

        match text "type", text "workItem", text "claimToken", text "resultDigest", root.TryGetProperty "generation" with
        | Some kind, Some item, Some token, Some digest, (true, generation) when
            kind = ValidationEvidence.ObservedEventType && item = workItemId && generation.ValueKind = JsonValueKind.Number
            ->
            Some
                { RecordedValidation.WorkItemId = item
                  ClaimToken = token
                  Generation = generation.GetInt64()
                  ResultDigest = digest }
        | _ -> None

    let read (root: string) (workItemId: string) : Result<RecordedValidation list, string> =
        let path = TransientStore.events root

        try
            if File.Exists path then
                File.ReadAllLines path
                |> Array.filter (fun line -> line.Contains(ValidationEvidence.ObservedEventType, StringComparison.Ordinal))
                |> Array.choose (recorded workItemId)
                |> Array.toList
                |> Ok
            else
                Ok []
        with error ->
            Error error.Message
