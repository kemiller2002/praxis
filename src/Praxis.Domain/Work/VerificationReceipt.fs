namespace Praxis.Domain.Work

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

/// I6: one verifier public key the host trusts. `PublicKey` is the base64
/// SubjectPublicKeyInfo DER; `KeyId` is `sha256:<hex of SHA-256 over it>`.
type TrustedVerifierKey =
    { KeyId: string
      Algorithm: string
      PublicKey: string }

/// The verifier that signed a receipt (I4 `verifier`).
type ReceiptVerifier =
    { Id: string
      Version: string
      KeyId: string }

/// I4 `echelon.verification-receipt/1` payload: what the trusted verifier
/// observed for one validation at one commit for one claim generation. It
/// never carries the claim token.
type ReceiptPayload =
    { ReceiptId: string
      WorkItemId: string
      Claimant: string
      FencingGeneration: int64
      Commit: string
      TreeId: string
      OrdoContractDigest: string
      ValidationPolicyId: string
      ValidationPolicyDigest: string
      Validation: string
      EvidenceId: string
      Status: CheckStatus
      ExitCode: int option
      Command: string list
      OutputSha256: string
      StartedAt: DateTimeOffset
      ObservedAt: DateTimeOffset
      DurationMs: int64
      Verifier: ReceiptVerifier }

type ReceiptSignature = { KeyId: string; Signature: byte array }

/// A DSSE v1 envelope as read: the exact payload bytes (never re-serialized)
/// and the envelope JSON text it arrived as.
type ReceiptEnvelope =
    { PayloadType: string
      Payload: byte array
      Signatures: ReceiptSignature list }

/// What an attested observation must bind to (I6), all from Praxis-held
/// state: the host authority, the current claim and Praxis's own clock.
type ReceiptBinding =
    { WorkItemId: string
      Claimant: string
      FencingGeneration: int64
      Commit: string
      OrdoContractDigest: string
      ValidationPolicyDigest: string option
      EvidenceId: string
      CitedBy: string list
      ClaimAcquiredAt: DateTimeOffset option
      LeaseExpiresAt: DateTimeOffset option
      EvaluatedAt: DateTimeOffset }

/// I4/I6: DSSE framing, strict payload intake and the attested-mode checks.
/// Every function is pure; signature verification uses only the BCL.
[<RequireQualifiedAccess>]
module VerificationReceipt =
    let SchemaVersion = "echelon.verification-receipt/1"
    let PayloadType = "application/vnd.echelon.verification-receipt.v1+json"
    let Algorithm = "ecdsa-p256-sha256"

    let MissingCode = "PRX.COMPLETION.RECEIPT_MISSING"
    let UntrustedKeyCode = "PRX.COMPLETION.RECEIPT_UNTRUSTED_KEY"
    let InvalidSignatureCode = "PRX.COMPLETION.RECEIPT_INVALID_SIGNATURE"
    let MalformedCode = "PRX.COMPLETION.RECEIPT_MALFORMED"
    let BindingMismatchCode = "PRX.COMPLETION.RECEIPT_BINDING_MISMATCH"
    let NotPassedCode = "PRX.COMPLETION.RECEIPT_NOT_PASSED"
    let StaleCode = "PRX.COMPLETION.RECEIPT_STALE"

    let stamp (value: DateTimeOffset) =
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)

    let private hex (bytes: byte array) = Convert.ToHexString(bytes).ToLowerInvariant()

    /// `sha256:<hex>` over the SubjectPublicKeyInfo DER.
    let keyId (subjectPublicKeyInfo: byte array) = "sha256:" + hex (SHA256.HashData subjectPublicKeyInfo)

    /// The receipt identity and evidence observation digest.
    let identity (payload: byte array) = "sha256:" + hex (SHA256.HashData payload)

    /// DSSE v1 pre-authentication encoding over the exact payload bytes.
    let pae (payloadType: string) (payload: byte array) =
        let typeBytes = Encoding.UTF8.GetBytes payloadType
        let head = Encoding.UTF8.GetBytes $"DSSEv1 {typeBytes.Length} {payloadType} {payload.Length} "
        Array.append head payload

    // ---- payload wire --------------------------------------------------------

    let private members =
        [ "schemaVersion"; "receiptId"; "workItemId"; "claimant"; "fencingGeneration"; "commit"; "treeId"; "ordoContractDigest"
          "validationPolicyId"; "validationPolicyDigest"; "validation"; "evidenceId"; "status"; "exitCode"; "command"; "outputSha256"
          "startedAt"; "observedAt"; "durationMs"; "verifier" ]

    /// Deterministic compact rendering (fixed member order). Signers sign
    /// these bytes; verifiers never re-render.
    let render (payload: ReceiptPayload) =
        let str (value: string) : JsonNode = JsonValue.Create value
        let root = JsonObject()
        root["schemaVersion"] <- str SchemaVersion
        root["receiptId"] <- str payload.ReceiptId
        root["workItemId"] <- str payload.WorkItemId
        root["claimant"] <- str payload.Claimant
        root["fencingGeneration"] <- JsonValue.Create payload.FencingGeneration
        root["commit"] <- str payload.Commit
        root["treeId"] <- str payload.TreeId
        root["ordoContractDigest"] <- str payload.OrdoContractDigest
        root["validationPolicyId"] <- str payload.ValidationPolicyId
        root["validationPolicyDigest"] <- str payload.ValidationPolicyDigest
        root["validation"] <- str payload.Validation
        root["evidenceId"] <- str payload.EvidenceId
        root["status"] <- str (CheckStatus.code payload.Status)
        root["exitCode"] <- (payload.ExitCode |> Option.map (fun code -> JsonValue.Create code :> JsonNode) |> Option.toObj)
        let command = JsonArray()
        payload.Command |> List.iter (fun part -> command.Add(str part))
        root["command"] <- command
        root["outputSha256"] <- str payload.OutputSha256
        root["startedAt"] <- str (stamp payload.StartedAt)
        root["observedAt"] <- str (stamp payload.ObservedAt)
        root["durationMs"] <- JsonValue.Create payload.DurationMs
        let verifier = JsonObject()
        verifier["id"] <- str payload.Verifier.Id
        verifier["version"] <- str payload.Verifier.Version
        verifier["keyId"] <- str payload.Verifier.KeyId
        root["verifier"] <- verifier
        Encoding.UTF8.GetBytes(root.ToJsonString(JsonSerializerOptions(WriteIndented = false)))

    let private isHex length (value: string) =
        not (isNull value) && value.Length = length && value |> Seq.forall Uri.IsHexDigit

    let private text (root: JsonElement) (name: string) =
        match root.GetProperty name with
        | value when value.ValueKind = JsonValueKind.String && value.GetString().Length > 0 -> value.GetString()
        | _ -> failwith $"'{name}' must be a non-empty string"

    let private instant (root: JsonElement) (name: string) =
        let styles = DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal

        match DateTimeOffset.TryParseExact(text root name, "yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, styles) with
        | true, value -> value
        | _ -> failwith $"'{name}' must be a UTC timestamp yyyy-MM-ddTHH:mm:ss.fffZ"

    let private digestText (root: JsonElement) (name: string) =
        match text root name with
        | value when value.StartsWith("sha256:", StringComparison.Ordinal) && isHex 64 (value.Substring 7) -> value
        | _ -> failwith $"'{name}' must be sha256:<64 hex>"

    let private exactly (expected: string list) (element: JsonElement) (what: string) =
        if element.ValueKind <> JsonValueKind.Object then failwith $"{what} must be an object"
        let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        if List.sort names <> List.sort expected then failwith $"{what} members differ from {SchemaVersion}"

    /// Strict payload intake: every member, nothing unknown, known values only.
    let parsePayload (bytes: byte array) : Result<ReceiptPayload, string> =
        try
            let json = UTF8Encoding(false, true).GetString bytes
            use document = JsonDocument.Parse json
            let root = document.RootElement
            // Every I4 member is required; extra members are signed but bind
            // nothing (as in Dokimos 0.7.0, e.g. `diagnostics`).
            let present = root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
            match members |> List.tryFind (present.Contains >> not) with
            | Some name -> failwith $"'{name}' is required"
            | None -> ()
            if Seq.length (root.EnumerateObject()) <> present.Count then failwith "duplicate payload member"
            if text root "schemaVersion" <> SchemaVersion then failwith $"schemaVersion is not {SchemaVersion}"
            let number name = match root.GetProperty(name: string) with
                               | value when value.ValueKind = JsonValueKind.Number -> value.GetInt64()
                               | _ -> failwith $"'{name}' must be an integer"
            let commit = text root "commit"
            let treeId = text root "treeId"
            if not (isHex 40 commit || isHex 64 commit) then failwith "'commit' must be a full Git object ID"
            if not (isHex 40 treeId || isHex 64 treeId) then failwith "'treeId' must be a full Git object ID"
            let command = root.GetProperty "command"
            if command.ValueKind <> JsonValueKind.Array then failwith "'command' must be an array"
            let verifier = root.GetProperty "verifier"
            exactly [ "id"; "version"; "keyId" ] verifier "'verifier'"
            let exitCode = root.GetProperty "exitCode"
            let receiptId = text root "receiptId"
            if not (fst (Guid.TryParse receiptId)) then failwith "'receiptId' must be a UUID"

            Ok
                { ReceiptId = receiptId
                  WorkItemId = text root "workItemId"
                  Claimant = text root "claimant"
                  FencingGeneration = number "fencingGeneration"
                  Commit = commit
                  TreeId = treeId
                  OrdoContractDigest = digestText root "ordoContractDigest"
                  ValidationPolicyId = text root "validationPolicyId"
                  ValidationPolicyDigest = digestText root "validationPolicyDigest"
                  Validation = text root "validation"
                  EvidenceId = text root "evidenceId"
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
                  OutputSha256 = digestText root "outputSha256"
                  StartedAt = instant root "startedAt"
                  ObservedAt = instant root "observedAt"
                  DurationMs = number "durationMs"
                  Verifier = { Id = text verifier "id"; Version = text verifier "version"; KeyId = text verifier "keyId" } }
        with error ->
            Error error.Message

    // ---- envelope wire -------------------------------------------------------

    /// The DSSE envelope object `{ payloadType, payload, signatures }`.
    let envelopeNode (envelope: ReceiptEnvelope) : JsonNode =
        let root = JsonObject()
        root["payloadType"] <- JsonValue.Create envelope.PayloadType
        root["payload"] <- JsonValue.Create(Convert.ToBase64String envelope.Payload)
        let signatures = JsonArray()

        for signature in envelope.Signatures do
            let entry = JsonObject()
            entry["keyid"] <- JsonValue.Create signature.KeyId
            entry["sig"] <- JsonValue.Create(Convert.ToBase64String signature.Signature)
            signatures.Add(entry :> JsonNode)

        root["signatures"] <- signatures
        root

    let renderEnvelope (envelope: ReceiptEnvelope) =
        (envelopeNode envelope).ToJsonString(JsonSerializerOptions(WriteIndented = false))

    /// Whether `json` is shaped like a DSSE envelope at all (it names a
    /// `payloadType`): anything else carries no attestation.
    let looksLikeEnvelope (json: string) =
        try
            use document = JsonDocument.Parse json
            document.RootElement.ValueKind = JsonValueKind.Object && fst (document.RootElement.TryGetProperty "payloadType")
        with _ ->
            false

    let parseEnvelope (json: string) : Result<ReceiptEnvelope, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement
            exactly [ "payloadType"; "payload"; "signatures" ] root "the envelope"
            let signatures = root.GetProperty "signatures"
            if signatures.ValueKind <> JsonValueKind.Array || signatures.GetArrayLength() = 0 then failwith "'signatures' must be a non-empty array"

            Ok
                { PayloadType = text root "payloadType"
                  Payload = Convert.FromBase64String(text root "payload")
                  Signatures =
                    signatures.EnumerateArray()
                    |> Seq.map (fun entry ->
                        exactly [ "keyid"; "sig" ] entry "a signature"
                        { KeyId = text entry "keyid"; Signature = Convert.FromBase64String(text entry "sig") })
                    |> Seq.toList }
        with error ->
            Error error.Message

    /// Verifies one IEEE-P1363 ECDSA P-256 / SHA-256 signature over the PAE
    /// under `key`, whose KeyId must be the digest of its own SPKI.
    let verifySignature (key: TrustedVerifierKey) (envelope: ReceiptEnvelope) (signature: ReceiptSignature) =
        try
            let spki = Convert.FromBase64String key.PublicKey

            if key.Algorithm <> Algorithm || keyId spki <> key.KeyId || signature.Signature.Length <> 64 then
                false
            else
                use ecdsa = ECDsa.Create()
                ecdsa.ImportSubjectPublicKeyInfo(ReadOnlySpan spki) |> ignore

                ecdsa.KeySize = 256
                && ecdsa.VerifyData(pae envelope.PayloadType envelope.Payload, signature.Signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
        with _ ->
            false

    let private finding code detail = { Code = code; Detail = detail }

    /// Binding, status and freshness findings for a payload whose signature
    /// already verified under `signedBy`.
    let private bindingFindings (binding: ReceiptBinding) (signedBy: string) (payload: ReceiptPayload) =
        let mismatch name (expected: string) (actual: string) =
            if expected = actual then None else Some $"{name} is '{actual}', expected '{expected}'"

        let mismatches =
            [ mismatch "workItemId" binding.WorkItemId payload.WorkItemId
              mismatch "claimant" binding.Claimant payload.Claimant
              mismatch "fencingGeneration" (string binding.FencingGeneration) (string payload.FencingGeneration)
              mismatch "commit" binding.Commit payload.Commit
              mismatch "ordoContractDigest" binding.OrdoContractDigest payload.OrdoContractDigest
              (match binding.ValidationPolicyDigest with
               | Some digest -> mismatch "validationPolicyDigest" digest payload.ValidationPolicyDigest
               | None -> Some "the host authority pins no ValidationPolicyDigest, so no receipt can bind to a policy")
              mismatch "evidenceId" binding.EvidenceId payload.EvidenceId
              mismatch "verifier.keyId" signedBy payload.Verifier.KeyId ]
            @ (binding.CitedBy |> List.distinct |> List.map (fun name -> mismatch "validation" name payload.Validation))
            |> List.choose id

        let fresh =
            binding.ClaimAcquiredAt |> Option.forall (fun at -> payload.ObservedAt >= at)
            && binding.LeaseExpiresAt |> Option.forall (fun at -> payload.ObservedAt < at)
            && payload.ObservedAt <= binding.EvaluatedAt

        let window =
            match binding.ClaimAcquiredAt, binding.LeaseExpiresAt with
            | Some acquired, Some expires -> $"[{stamp acquired}, {stamp expires})"
            | _ -> "(no lease window)"

        [ for detail in mismatches do
              yield finding BindingMismatchCode $"receipt for evidence '{binding.EvidenceId}': {detail}"
          if payload.Status <> CheckStatus.Passed then
              yield
                  finding
                      NotPassedCode
                      $"receipt for evidence '{binding.EvidenceId}' records a {CheckStatus.code payload.Status} run of '{payload.Validation}' (exit {payload.ExitCode})"
          if not fresh then
              yield
                  finding
                      StaleCode
                      $"receipt for evidence '{binding.EvidenceId}' was observed at {stamp payload.ObservedAt}, outside the lease window {window} or after evaluation at {stamp binding.EvaluatedAt}" ]

    /// Every I6 check, in order: envelope shape, trusted key, signature,
    /// payload shape, then binding, status and freshness. The first three
    /// stop evaluation; the payload is only read once its signature verified.
    let check (keys: TrustedVerifierKey list) (binding: ReceiptBinding) (envelope: ReceiptEnvelope) : Result<ReceiptPayload, CompletionFinding list> =
        let subject = $"receipt for evidence '{binding.EvidenceId}'"

        if envelope.PayloadType <> PayloadType then
            Error [ finding MalformedCode $"{subject} has payloadType '{envelope.PayloadType}', expected '{PayloadType}'" ]
        else
            let trusted =
                envelope.Signatures
                |> List.choose (fun signature -> keys |> List.tryFind (fun key -> key.KeyId = signature.KeyId) |> Option.map (fun key -> key, signature))

            match trusted with
            | [] ->
                let named = envelope.Signatures |> List.map _.KeyId |> String.concat ", "
                Error [ finding UntrustedKeyCode $"{subject} is signed only by untrusted key(s) {named}" ]
            | _ ->
                match trusted |> List.tryFind (fun (key, signature) -> verifySignature key envelope signature) with
                | None -> Error [ finding InvalidSignatureCode $"{subject}: no signature verifies under the trusted key it names" ]
                | Some(key, _) ->
                    match parsePayload envelope.Payload with
                    | Error reason -> Error [ finding MalformedCode $"{subject}: the payload is not a {SchemaVersion} document ({reason})" ]
                    | Ok payload ->
                        match bindingFindings binding key.KeyId payload with
                        | [] -> Ok payload
                        | findings -> Error findings

/// How a validated evidence item was proven: a Praxis-recorded
/// `praxis.validation-result/1` (non-attested mode, PRAXIS-REL-005) or a
/// verifier-signed I4 receipt (attested mode, PRAXIS-REL-009).
[<RequireQualifiedAccess>]
type EvidenceProof =
    | Recorded of ValidationResult
    | Attested of payload: ReceiptPayload * envelope: ReceiptEnvelope

/// INVARIANT (PRAXIS-REL-005, PRAXIS-REL-009): passing completion evidence
/// can only come from `ValidatedEvidence.establish` (a recorded result that
/// passed every binding check) or `ValidatedEvidence.attest` (a receipt whose
/// signature verified under a host-trusted key and whose payload binds this
/// item, generation, commit, contract and policy, passed and is fresh). The
/// constructor is private, so no other code can build one.
/// `CompletionGate.packet` and `CompletionGate.authority` accept only these
/// values, and the Dokimos authority's evidence observations *are* these
/// values, so a `Passed` observation built from a raw file, a failed or
/// unavailable result, an unrecorded document or an unverified receipt does
/// not type-check.
[<Sealed>]
type ValidatedEvidence private (id: string, digest: string, revision: string, observedAt: DateTimeOffset, validation: string, proof: EvidenceProof) =
    /// The evidence ID it is cited as.
    member _.Id = id
    /// SHA-256 of the exact result document bytes, or the receipt identity
    /// (SHA-256 of the exact payload bytes) for an attested item.
    member _.Digest = digest
    member _.Revision = revision
    /// Always `Passed`: both constructors refuse anything else.
    member _.Status = CheckStatus.Passed
    member _.ObservedAt = observedAt
    member _.Validation = validation
    member _.Proof = proof
    /// The I4 envelope an attested item carries to Dokimos (I6 `Attestation`).
    member _.Attestation =
        match proof with
        | EvidenceProof.Attested(_, envelope) -> Some envelope
        | EvidenceProof.Recorded _ -> None
    /// Praxis never asserts reuse across claims.
    member _.Reusable = false

    /// Non-attested mode. The error side carries the stable
    /// `PRX.COMPLETION.EVIDENCE_*` refusal codes.
    static member establish
        (workItemId: string)
        (fence: ClaimFence)
        (holder: ClaimHolder option)
        (verificationRevision: string)
        (recorded: Result<RecordedValidation list, string>)
        (evidenceId: string)
        (citedBy: string list)
        (digest: string)
        (result: ValidationResult)
        : Result<ValidatedEvidence, CompletionFinding list> =
        match ValidationEvidence.check workItemId fence holder verificationRevision recorded evidenceId citedBy digest result with
        | [] when result.Status = CheckStatus.Passed ->
            Ok(ValidatedEvidence(evidenceId, digest, result.Revision, result.ObservedAt, result.Validation, EvidenceProof.Recorded result))
        | [] -> Error [ { Code = ValidationEvidence.NotPassedCode; Detail = $"evidence '{evidenceId}' did not pass" } ]
        | findings -> Error findings

    /// Attested mode (I6). The error side carries the stable
    /// `PRX.COMPLETION.RECEIPT_*` refusal codes.
    static member attest (keys: TrustedVerifierKey list) (binding: ReceiptBinding) (envelope: ReceiptEnvelope) : Result<ValidatedEvidence, CompletionFinding list> =
        VerificationReceipt.check keys binding envelope
        |> Result.map (fun payload ->
            ValidatedEvidence(
                binding.EvidenceId,
                VerificationReceipt.identity envelope.Payload,
                payload.Commit,
                payload.ObservedAt,
                payload.Validation,
                EvidenceProof.Attested(payload, envelope)
            ))
