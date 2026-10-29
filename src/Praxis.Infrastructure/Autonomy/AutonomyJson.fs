namespace Praxis.Infrastructure.Autonomy

open System
open System.Collections.Generic
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Autonomy
open Praxis.Infrastructure.Json
open Praxis.Infrastructure.Planning

/// One owner key the trust store names.
type TrustedOwnerKey =
    { KeyId: string
      Owner: string
      PublicKeyPem: string }

/// `praxis.autonomy-trust/1`: the owner keys and owner-revoked runs. It must
/// live outside any repository an agent edits (TrustStore.load refuses a
/// path inside the repository root), because whoever writes it decides whose
/// signature counts.
type TrustStore =
    { Signers: Map<string, TrustedOwnerKey>
      RevokedRunIds: Set<string> }

/// Strict readers and writers for the autonomy contracts. Every reader uses
/// an exact allow-list of members, refuses duplicates and never supplies a
/// default for a missing member: an unreadable contract is an error, and an
/// error denies.
[<RequireQualifiedAccess>]
module AutonomyJson =
    [<Literal>]
    let PolicySchema = "praxis.autonomous-execution-policy/1"

    [<Literal>]
    let TrustSchema = "praxis.autonomy-trust/1"

    [<Literal>]
    let RevocationSchema = "praxis.run-revocation/1"

    [<Literal>]
    let DecisionSchema = "praxis.autonomy-decision/1"

    [<Literal>]
    let ReceiptSchema = "praxis.merge-receipt/1"

    exception ContractError of string

    let private fail message = raise (ContractError message)

    let sha256Bytes (bytes: byte array) =
        "sha256:" + (SHA256.HashData bytes |> Convert.ToHexString).ToLowerInvariant()

    let private exactly (element: JsonElement) (required: string list) (optional: string list) (what: string) =
        if element.ValueKind <> JsonValueKind.Object then fail $"{what} must be an object"
        let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        if names.Length <> (Set.ofList names).Count then fail $"{what} has duplicate members"
        let allowed = Set.ofList (required @ optional)
        match names |> List.filter (allowed.Contains >> not) with
        | [] -> ()
        | unknown -> fail $"""{what} has unknown members: {String.Join(", ", unknown)}"""
        match required |> List.filter (fun name -> not (List.contains name names)) with
        | [] -> ()
        | missing -> fail $"""{what} is missing members: {String.Join(", ", missing)}"""

    let private prop (element: JsonElement) (name: string) = element.GetProperty name

    let private text (element: JsonElement) name =
        let value = prop element name
        if value.ValueKind <> JsonValueKind.String || String.IsNullOrWhiteSpace(value.GetString()) then
            fail $"{name} must be a non-empty string"
        value.GetString()

    let private integer (element: JsonElement) name low high =
        let value = prop element name
        match value.ValueKind, value.TryGetInt32() with
        | JsonValueKind.Number, (true, n) when n >= low && n <= high -> n
        | _ -> fail $"{name} must be an integer from {low} to {high}"

    let private texts (element: JsonElement) name =
        let value = prop element name
        if value.ValueKind <> JsonValueKind.Array then fail $"{name} must be an array"
        let items =
            value.EnumerateArray()
            |> Seq.map (fun item ->
                if item.ValueKind <> JsonValueKind.String || String.IsNullOrWhiteSpace(item.GetString()) then
                    fail $"{name} has a blank or non-string item"
                item.GetString())
            |> Seq.toList
        if items.Length <> (Set.ofList items).Count then fail $"{name} has duplicate items"
        items

    let private nonEmpty name (values: 'a list) = if values.IsEmpty then fail $"{name} must not be empty" else values

    let private parseAll name (parse: string -> 'a option) (values: string list) =
        values |> List.map (fun raw -> match parse raw with Some v -> v | None -> fail $"{name}: unknown value '{raw}'")

    let private timestamp name (raw: string) =
        match DateTimeOffset.TryParseExact(raw, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                                           DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal) with
        | true, value -> value
        | _ -> fail $"{name} must be a UTC timestamp yyyy-MM-ddTHH:mm:ssZ"

    let private guard (what: string) (read: JsonElement -> 'a) (json: string) : Result<'a, string> =
        try
            use document = JsonDocument.Parse(json, JsonDocumentOptions(AllowTrailingCommas = false))
            Ok(read document.RootElement)
        with
        | ContractError message -> Error $"{what}: {message}"
        | :? JsonException as error -> Error $"{what}: malformed JSON: {error.Message}"

    // ---------------------------------------------------------------- policy

    let private readPolicyElement (root: JsonElement) =
        exactly root [ "schema"; "policyVersion"; "execution"; "scope"; "pullRequests"; "mergeRequirements"; "protectedPaths"; "failureHandling"; "authorization" ] [ "description" ] "policy"
        if text root "schema" <> PolicySchema then fail $"schema must be {PolicySchema}"
        let execution = prop root "execution"
        exactly execution [ "mode" ] [] "execution"
        let scope = prop root "scope"
        exactly scope [ "repositories"; "targetBranches"; "allowedOperations" ] [] "scope"
        let pulls = prop root "pullRequests"
        exactly pulls [ "mergeStrategy" ] [] "pullRequests"
        let merge = prop root "mergeRequirements"
        exactly merge [ "requiredChecks"; "checkProducer"; "independentReviewCheck"; "maxCriticalDefects" ] [] "mergeRequirements"
        let failure = prop root "failureHandling"
        exactly failure [ "transientRetryLimit"; "retryBaseDelaySeconds"; "checksTimeoutMinutes" ] [] "failureHandling"
        let authorization = prop root "authorization"
        exactly authorization [ "maxHours" ] [] "authorization"

        let protectedPaths =
            let value = prop root "protectedPaths"
            if value.ValueKind <> JsonValueKind.Array then fail "protectedPaths must be an array"
            value.EnumerateArray()
            |> Seq.map (fun entry ->
                exactly entry [ "pattern"; "class" ] [] "protectedPaths item"
                { Pattern = text entry "pattern"
                  Class =
                    match ProtectedClass.tryParse (text entry "class") with
                    | Some c -> c
                    | None -> fail $"""protectedPaths: unknown class '{text entry "class"}'""" })
            |> Seq.toList

        { PolicyVersion = integer root "policyVersion" 1 1
          Mode =
            match text execution "mode" with
            | "unattended" -> ExecutionMode.Unattended
            | "attended" -> ExecutionMode.Attended
            | other -> fail $"execution.mode: unknown value '{other}'"
          Repositories = texts scope "repositories" |> nonEmpty "scope.repositories"
          TargetBranches = texts scope "targetBranches" |> nonEmpty "scope.targetBranches"
          AllowedOperations = texts scope "allowedOperations" |> nonEmpty "scope.allowedOperations" |> parseAll "scope.allowedOperations" Operation.tryParse
          MergeStrategy =
            match MergeStrategy.tryParse (text pulls "mergeStrategy") with
            | Some s -> s
            | None -> fail "pullRequests.mergeStrategy must be squash, merge or rebase"
          Merge =
            { RequiredChecks = texts merge "requiredChecks" |> nonEmpty "mergeRequirements.requiredChecks"
              CheckProducer = text merge "checkProducer"
              IndependentReviewCheck = text merge "independentReviewCheck"
              // Version 1 admits no open critical defect at all.
              MaxCriticalDefects = integer merge "maxCriticalDefects" 0 0 }
          ProtectedPaths = protectedPaths
          Failure =
            { TransientRetryLimit = integer failure "transientRetryLimit" 0 10
              RetryBaseDelaySeconds = integer failure "retryBaseDelaySeconds" 1 300
              ChecksTimeoutMinutes = integer failure "checksTimeoutMinutes" 1 360 }
          MaxAuthorizationHours = integer authorization "maxHours" 1 168 }

    /// The policy and its digest, which is over the exact bytes read.
    let readPolicy (bytes: byte array) : Result<ExecutionPolicy * string, string> =
        Encoding.UTF8.GetString bytes
        |> guard "autonomous-execution policy" readPolicyElement
        |> Result.map (fun policy -> policy, sha256Bytes bytes)

    // --------------------------------------------------------- authorization

    let private readAuthorizationElement (root: JsonElement) =
        exactly root [ "schema"; "runId"; "owner"; "keyId"; "issuedAt"; "expiresAt"; "policyVersion"; "policyDigest"; "repositories"; "targetBranches"; "permittedOperations"; "protectedOperations"; "signature" ] [] "run authorization"
        if text root "schema" <> Authorization.Schema then fail $"schema must be {Authorization.Schema}"
        let signature = prop root "signature"
        if signature.ValueKind <> JsonValueKind.String then fail "signature must be a string"

        { RunId = text root "runId"
          Owner = text root "owner"
          KeyId = text root "keyId"
          IssuedAt = text root "issuedAt" |> timestamp "issuedAt"
          ExpiresAt = text root "expiresAt" |> timestamp "expiresAt"
          PolicyVersion = integer root "policyVersion" 1 1
          PolicyDigest = text root "policyDigest"
          Repositories = texts root "repositories" |> nonEmpty "repositories"
          TargetBranches = texts root "targetBranches" |> nonEmpty "targetBranches"
          PermittedOperations = texts root "permittedOperations" |> nonEmpty "permittedOperations" |> parseAll "permittedOperations" Operation.tryParse
          ProtectedOperations = texts root "protectedOperations" |> parseAll "protectedOperations" ProtectedClass.tryParse
          Signature = signature.GetString() }

    let readAuthorization (json: string) = guard "run authorization" readAuthorizationElement json

    let private array (values: string seq) =
        let result = JsonArray()
        values |> Seq.iter (fun v -> result.Add(JsonValue.Create v))
        result

    let authorizationNode (a: RunAuthorization) =
        JsonObject(
            [ "schema", JsonValue.Create Authorization.Schema :> JsonNode
              "runId", JsonValue.Create a.RunId
              "owner", JsonValue.Create a.Owner
              "keyId", JsonValue.Create a.KeyId
              "issuedAt", JsonValue.Create(Authorization.utc a.IssuedAt)
              "expiresAt", JsonValue.Create(Authorization.utc a.ExpiresAt)
              "policyVersion", JsonValue.Create a.PolicyVersion
              "policyDigest", JsonValue.Create a.PolicyDigest
              "repositories", array a.Repositories
              "targetBranches", array a.TargetBranches
              "permittedOperations", array (a.PermittedOperations |> List.map Operation.toWire)
              "protectedOperations", array (a.ProtectedOperations |> List.map ProtectedClass.toWire)
              "signature", JsonValue.Create a.Signature ]
            |> List.map KeyValuePair.Create)

    /// The authorization's identity: the digest of the exact signed bytes.
    let authorizationDigest (a: RunAuthorization) = sha256Bytes (Authorization.signingPayload a)

    /// Verify the owner's signature over the framed payload with a key the
    /// trust store names for that owner. Nothing about the authorization's
    /// scope or time is decided here; that is the evaluator's.
    let verifySignature (trust: TrustStore) (a: RunAuthorization) : AuthorizationState =
        match trust.Signers |> Map.tryFind a.KeyId with
        | None -> AuthorizationState.Invalid $"signer key {a.KeyId} is not in the trust store"
        | Some signer when signer.Owner <> a.Owner ->
            AuthorizationState.Invalid $"key {a.KeyId} belongs to {signer.Owner}, not {a.Owner}"
        | Some signer ->
            try
                let raw = Convert.FromBase64String a.Signature
                match LocalOperator.normalizeSignature raw with
                | Error reason -> AuthorizationState.Invalid reason
                | Ok signature ->
                    use key = ECDsa.Create()
                    key.ImportFromPem signer.PublicKeyPem
                    if key.ExportParameters(false).Curve.Oid.Value <> ECCurve.NamedCurves.nistP256.Oid.Value then
                        AuthorizationState.Invalid "owner keys must be NIST P-256"
                    elif key.VerifyData(Authorization.signingPayload a, signature, HashAlgorithmName.SHA256,
                                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation) then
                        AuthorizationState.Verified(a, authorizationDigest a)
                    else
                        AuthorizationState.Invalid "the owner signature does not verify"
            with
            | :? FormatException -> AuthorizationState.Invalid "the signature is not base64"
            | :? CryptographicException as error -> AuthorizationState.Invalid $"signature verification failed: {error.Message}"
            | :? ArgumentException as error -> AuthorizationState.Invalid $"signature verification failed: {error.Message}"

    // ------------------------------------------------------------ trust store

    let private readTrustElement (root: JsonElement) =
        exactly root [ "schema"; "signers"; "revokedRunIds" ] [ "description" ] "trust store"
        if text root "schema" <> TrustSchema then fail $"schema must be {TrustSchema}"
        let signers = prop root "signers"
        if signers.ValueKind <> JsonValueKind.Array then fail "signers must be an array"

        { Signers =
            signers.EnumerateArray()
            |> Seq.map (fun entry ->
                exactly entry [ "keyId"; "owner"; "publicKeyPem" ] [] "signer"
                let pem =
                    match LocalOperator.canonicalPublicKey (text entry "publicKeyPem") with
                    | Ok pem -> pem
                    | Error reason -> fail reason
                let key = { KeyId = text entry "keyId"; Owner = text entry "owner"; PublicKeyPem = pem }
                key.KeyId, key)
            |> Map.ofSeq
          RevokedRunIds = texts root "revokedRunIds" |> Set.ofList }

    let readTrustStore (json: string) = guard "trust store" readTrustElement json

    // ------------------------------------------------------------ revocation

    let private readRevocationElement (root: JsonElement) =
        exactly root [ "schema"; "runId"; "revokedAt"; "reason" ] [] "run revocation"
        if text root "schema" <> RevocationSchema then fail $"schema must be {RevocationSchema}"
        { RunId = text root "runId"; RevokedAt = text root "revokedAt" |> timestamp "revokedAt"; Reason = text root "reason" }

    let readRevocation (json: string) = guard "run revocation" readRevocationElement json

    let revocationNode (r: RunRevocation) =
        JsonObject(
            [ "schema", JsonValue.Create RevocationSchema :> JsonNode
              "runId", JsonValue.Create r.RunId
              "revokedAt", JsonValue.Create(Authorization.utc r.RevokedAt)
              "reason", JsonValue.Create r.Reason ]
            |> List.map KeyValuePair.Create)

    // -------------------------------------------------- decisions and receipts

    let private optional (value: 'a option) (create: 'a -> JsonNode) : JsonNode =
        match value with
        | Some v -> create v
        | None -> null

    let decisionNode (d: Decision) =
        let findings = JsonArray()
        d.Findings
        |> List.iter (fun f ->
            let stage =
                match f.Stage with
                | FindingStage.Gate -> "gate"
                | FindingStage.Protected -> "protected"
                | FindingStage.Evidence -> "evidence"
            findings.Add(JsonObject([ "stage", JsonValue.Create stage :> JsonNode; "code", JsonValue.Create f.Code; "detail", JsonValue.Create f.Detail ] |> List.map KeyValuePair.Create)))

        JsonObject(
            [ "schema", JsonValue.Create DecisionSchema :> JsonNode
              "operation", JsonValue.Create(Operation.toWire d.Operation)
              "repository", JsonValue.Create d.Repository
              "targetBranch", JsonValue.Create d.TargetBranch
              "candidateSha", JsonValue.Create d.CandidateSha
              "policyVersion", optional d.PolicyVersion (fun v -> JsonValue.Create v)
              "policyDigest", optional d.PolicyDigest (fun v -> JsonValue.Create v)
              "runId", optional d.RunId (fun v -> JsonValue.Create v)
              "authorizationDigest", optional d.AuthorizationDigest (fun v -> JsonValue.Create v)
              "result", JsonValue.Create(DecisionResult.toWire d.Result)
              "findings", findings
              "evaluatedAt", JsonValue.Create(Authorization.utc d.EvaluatedAt) ]
            |> List.map KeyValuePair.Create)

    let private checkNode (c: CheckObservation) =
        JsonObject(
            [ "name", JsonValue.Create c.Name :> JsonNode
              "id", JsonValue.Create c.Id
              "headSha", JsonValue.Create c.HeadSha
              "app", JsonValue.Create c.App
              "status", JsonValue.Create c.Status
              "conclusion", optional c.Conclusion (fun v -> JsonValue.Create v) ]
            |> List.map KeyValuePair.Create)

    let receiptNode (r: MergeReceipt) =
        let checks = JsonArray()
        r.Checks |> List.iter (checkNode >> checks.Add)

        JsonObject(
            [ "schema", JsonValue.Create ReceiptSchema :> JsonNode
              "runId", JsonValue.Create r.RunId
              "authorizationDigest", JsonValue.Create r.AuthorizationDigest
              "policyVersion", JsonValue.Create r.PolicyVersion
              "policyDigest", JsonValue.Create r.PolicyDigest
              "repository", JsonValue.Create r.Repository
              "pullRequest", JsonValue.Create r.PullRequest
              "targetBranch", JsonValue.Create r.TargetBranch
              "candidateSha", JsonValue.Create r.CandidateSha
              "mergeMethod", JsonValue.Create(MergeStrategy.toWire r.MergeMethod)
              "decisionDigest", JsonValue.Create r.DecisionDigest
              "checks", checks
              "mergeCommitSha", JsonValue.Create r.MergeCommitSha
              "targetHeadSha", JsonValue.Create r.TargetHeadSha
              "targetContainsMerge", JsonValue.Create r.TargetContainsMerge
              "attempts", JsonValue.Create r.Attempts
              "reconciled", JsonValue.Create r.Reconciled
              "previousReceiptDigest", optional r.PreviousReceiptDigest (fun v -> JsonValue.Create v)
              "recordedAt", JsonValue.Create(Authorization.utc r.RecordedAt) ]
            |> List.map KeyValuePair.Create)

    /// Content address of a node: SHA-256 over its key-sorted compact form.
    let digest (node: JsonNode) = "sha256:" + CanonicalJson.contentDigest 64 node

    let pretty (node: JsonNode) = node.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n"
