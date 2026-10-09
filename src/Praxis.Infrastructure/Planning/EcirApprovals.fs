namespace Praxis.Infrastructure.Planning

open System
open System.Globalization
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json

/// Scope supplied by the trusted executor, independently of the receipt
/// and the construction agent. A signed approval never invents its own scope.
type EcirApprovalScope =
    { GroupId: string
      CohortId: string
      SourceCommit: string
      ManifestDigest: string
      BlueprintDigest: string
      RequirementKeys: string list
      DecisionIds: string list }

/// Untrusted transport. This record has NO authority until the signature,
/// signer policy, time interval, and exact expected scope are all verified.
type EcirSignedApproval =
    { SchemaVersion: string
      KeyId: string
      Approver: string
      IssuedAt: string
      ExpiresAt: string
      Scope: EcirApprovalScope
      Signature: string }

/// These identities and public keys MUST come from a trusted installation
/// or administrator-controlled store outside the agent's editable repo.
type EcirTrustedSigner =
    { Approver: string
      PublicKeyPem: string }

type EcirApprovalEvidence =
    { Approver: string
      KeyId: string
      GroupId: string
      CohortId: string
      SourceCommit: string
      ManifestDigest: string
      BlueprintDigest: string
      RequirementKeys: string list
      DecisionIds: string list
      ExpiresAt: string }

[<RequireQualifiedAccess>]
module EcirApprovals =
    [<Literal>]
    let SchemaVersion = "ecir.approval/1"

    let private textBytes (value: string) = Encoding.UTF8.GetBytes(value)
    let private isBlank (s: string) = String.IsNullOrWhiteSpace s

    let private sha256 (value: string) =
        not (isBlank value)
        && value.Length = 71
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && (value.Substring(7) |> Seq.forall (fun c ->
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

    let private commitSha (value: string) =
        not (isBlank value)
        && value.Length = 40
        && (value |> Seq.forall Uri.IsHexDigit)

    let private setExactly (actual: string list) (expected: string list) =
        not (List.isEmpty actual)
        && not (List.isEmpty expected)
        && (actual |> List.forall (isBlank >> not))
        && (expected |> List.forall (isBlank >> not))
        && actual.Length = (actual |> Set.ofList).Count
        && expected.Length = (expected |> Set.ofList).Count
        && Set.ofList actual = Set.ofList expected

    /// Canonical, unambiguous versioned byte sequence. All fields are
    /// UTF-8 byte-length framed, with set-valued identities sorted ordinally.
    /// Signing is intentionally NOT implemented in production.
    let signingPayload (approval: EcirSignedApproval) =
        let buffer = StringBuilder("ecir-signed-approval/1\n")
        let frame (value: string) =
            buffer.Append(Encoding.UTF8.GetByteCount value).Append(':').Append(value) |> ignore

        let s = approval.Scope
        [ approval.SchemaVersion; approval.KeyId; approval.Approver
          approval.IssuedAt; approval.ExpiresAt; s.GroupId; s.CohortId
          s.SourceCommit; s.ManifestDigest; s.BlueprintDigest ]
        |> List.iter frame

        [ s.RequirementKeys; s.DecisionIds ]
        |> List.iter (fun values ->
            let normalized = values |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
            frame (string normalized.Length)
            normalized |> List.iter frame)
        textBytes (buffer.ToString())

    let private readString (name: string) (value: JsonElement) =
        let property = value.GetProperty name
        if property.ValueKind <> JsonValueKind.String then
            invalidArg name ("expected a string field: " + name)
        let output = property.GetString()
        if isBlank output then invalidArg name ("missing " + name)
        output

    let private readStrings (name: string) (value: JsonElement) =
        let property = value.GetProperty name
        if property.ValueKind <> JsonValueKind.Array then
            invalidArg name ("expected a string array: " + name)
        property.EnumerateArray()
        |> Seq.map (fun item ->
            if item.ValueKind <> JsonValueKind.String then invalidArg name ("invalid item in " + name)
            let result = item.GetString()
            if isBlank result then invalidArg name ("blank item in " + name)
            result)
        |> Seq.toList

    let private checkShape (names: string list) (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then invalidArg "json" "expected object"
        let supplied = value.EnumerateObject() |> Seq.map (fun pair -> pair.Name) |> Seq.toList
        if supplied.Length <> names.Length
           || supplied.Length <> (supplied |> Set.ofList).Count
           || Set.ofList supplied <> Set.ofList names then
            invalidArg "json" "missing, unexpected or duplicate ECIR approval fields"

    /// Decode with an exact allow-list and reject duplicate JSON member names.
    /// Parsing an approval never supplies authority.
    let read (json: string) : Result<EcirSignedApproval, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement
            checkShape [ "schemaVersion"; "keyId"; "approver"; "issuedAt"; "expiresAt"; "scope"; "signature" ] root
            let s = root.GetProperty "scope"
            checkShape [ "groupId"; "cohortId"; "sourceCommit"; "manifestDigest"; "blueprintDigest"; "requirementKeys"; "decisionIds" ] s

            Ok
                { SchemaVersion = readString "schemaVersion" root
                  KeyId = readString "keyId" root
                  Approver = readString "approver" root
                  IssuedAt = readString "issuedAt" root
                  ExpiresAt = readString "expiresAt" root
                  Scope =
                    { GroupId = readString "groupId" s
                      CohortId = readString "cohortId" s
                      SourceCommit = readString "sourceCommit" s
                      ManifestDigest = readString "manifestDigest" s
                      BlueprintDigest = readString "blueprintDigest" s
                      RequirementKeys = readStrings "requirementKeys" s
                      DecisionIds = readStrings "decisionIds" s }
                  Signature = readString "signature" root }
        with
        | :? JsonException as ex -> Error("malformed signed ECIR approval: " + ex.Message)
        | :? ArgumentException as ex -> Error("invalid signed ECIR approval: " + ex.Message)
        | :? KeyNotFoundException as ex -> Error("missing signed ECIR approval field: " + ex.Message)
        | :? InvalidOperationException as ex -> Error("invalid ECIR approval field: " + ex.Message)

    let private timestamp (value: string) =
        let mutable parsed = DateTimeOffset.MinValue
        if DateTimeOffset.TryParseExact(
            value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal, &parsed) then
            Some parsed
        else None

    /// Strict scope and signer validation. The caller supplies trusted
    /// available keys, expected cohort/requirements/decisions and UTC time.
    /// A signed receipt from an unknown key or an already expired/revoked
    /// authority is NOT valid. Key revocation is enforced by removing the
    /// signer from the trusted map on the host.
    let verify
        (trustedKeys: Map<string, EcirTrustedSigner>)
        (expected: EcirApprovalScope)
        (nowUtc: DateTimeOffset)
        (receipt: EcirSignedApproval)
        : Result<EcirApprovalEvidence, string> =

        let s = receipt.Scope

        if receipt.SchemaVersion <> SchemaVersion then
            Error "unknown ECIR approval schema"
        elif isBlank receipt.KeyId || isBlank receipt.Approver then
            Error "ECIR signer identity missing"
        elif not (commitSha s.SourceCommit && commitSha expected.SourceCommit)
             || not (sha256 s.ManifestDigest && sha256 s.BlueprintDigest
                     && sha256 expected.ManifestDigest && sha256 expected.BlueprintDigest) then
            Error "ECIR source commit or digest is malformed"
        elif isBlank s.GroupId || isBlank s.CohortId
             || s.GroupId <> expected.GroupId
             || s.CohortId <> expected.CohortId
             || s.SourceCommit <> expected.SourceCommit
             || s.ManifestDigest <> expected.ManifestDigest
             || s.BlueprintDigest <> expected.BlueprintDigest then
            Error "approval does not match the independently expected group, cohort, commit and blueprint"
        elif not (setExactly s.RequirementKeys expected.RequirementKeys
                  && setExactly s.DecisionIds expected.DecisionIds) then
            Error "approval does not cover exactly the expected requirements and decisions"
        else
            match timestamp receipt.IssuedAt, timestamp receipt.ExpiresAt, Map.tryFind receipt.KeyId trustedKeys with
            | None, _, _ | _, None, _ ->
                Error "signed ECIR approval has an invalid UTC timestamp"
            | _, _, None ->
                Error "ECIR signer key is not on the trusted host allow-list"
            | Some issued, Some expires, Some signer when
                issued > nowUtc || expires <= nowUtc
                || expires <= issued || expires - issued > TimeSpan.FromHours 24. ->
                Error "ECIR approval is expired, future-dated or longer than the allowed 24 hours"
            | Some _, Some _, Some signer when
                isBlank signer.PublicKeyPem || signer.Approver <> receipt.Approver
                || not (signer.PublicKeyPem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
                || signer.PublicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal) ->
                Error "ECIR public key or authenticated signer identity is invalid"
            | Some _, Some _, Some signer ->
                try
                    let bytes = Convert.FromBase64String receipt.Signature
                    use publicKey = ECDsa.Create()
                    publicKey.ImportFromPem signer.PublicKeyPem
                    if publicKey.KeySize <> 256
                       || bytes.Length <> 64
                       || not (publicKey.VerifyData(signingPayload receipt, bytes, HashAlgorithmName.SHA256)) then
                        Error "ECIR approval cryptographic signature did not verify"
                    else
                        Ok
                            { Approver = receipt.Approver
                              KeyId = receipt.KeyId
                              GroupId = s.GroupId
                              CohortId = s.CohortId
                              SourceCommit = s.SourceCommit
                              ManifestDigest = s.ManifestDigest
                              BlueprintDigest = s.BlueprintDigest
                              RequirementKeys = s.RequirementKeys |> List.sort
                              DecisionIds = s.DecisionIds |> List.sort
                              ExpiresAt = receipt.ExpiresAt }
                with
                | :? FormatException -> Error "ECIR approval signature is not valid base64"
                | :? CryptographicException -> Error "ECIR approval signer key/signature could not be verified"
                | :? ArgumentException -> Error "ECIR approval signer key is malformed"
