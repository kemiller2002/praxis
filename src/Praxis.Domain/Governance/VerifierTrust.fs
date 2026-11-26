namespace Praxis.Domain.Governance

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Praxis.Domain.Work

/// A verifier identity the protected trust root admits.
[<RequireQualifiedAccess>]
type VerifierIdentity =
    /// An ECDSA P-256 key held by a host or a CI secret (the existing I6
    /// `TrustedVerifierKey`), pinned by SPKI digest.
    | PinnedKey of TrustedVerifierKey
    /// A CI workload identity: a GitHub Actions OIDC token whose claims name
    /// exactly this repository, workflow file and protected ref. No private
    /// key exists for anyone to hold, leak or rotate.
    | Workload of WorkloadIdentity

and WorkloadIdentity =
    { Issuer: string
      /// `owner/name`.
      Repository: string
      /// The `job_workflow_ref` claim: `owner/name/.github/workflows/F@refs/heads/B`.
      WorkflowRef: string
      /// Events the verification workflow may run under (`pull_request_target`, ...).
      Events: string list }

/// One entry of `praxis.verifier-trust/1`.
type TrustedVerifier =
    { Id: string
      Identity: VerifierIdentity
      ValidFrom: DateTimeOffset
      /// Normal retirement: receipts observed before it stay valid.
      ValidUntil: DateTimeOffset option
      /// Compromise: every receipt from this verifier is refused, whenever
      /// it claims to have been observed, because a compromised key can
      /// back-date what it signs.
      RevokedAt: DateTimeOffset option
      RevocationReason: string option
      /// Validation-policy IDs this verifier may attest; empty means any.
      PolicyScopes: string list }

/// `praxis.verifier-trust/1`: the completion authority's trust root. It is
/// read only from the protected branch; a candidate's copy is a proposal.
type VerifierTrustRoot =
    { ConfigVersion: int
      Repository: string
      Verifiers: TrustedVerifier list
      /// The principal who approved this version (platform login).
      ApprovedBy: string
      ApprovedAt: DateTimeOffset }

/// A receipt whose signature, signer and binding to the trust root held.
type VerifiedReceipt =
    { Payload: ReceiptPayload
      Digest: string
      VerifierId: string
      Basis: AuthenticationBasis }

[<RequireQualifiedAccess>]
module VerifierTrust =
    [<Literal>]
    let Schema = "praxis.verifier-trust/1"

    [<Literal>]
    let GitHubIssuer = "https://token.actions.githubusercontent.com"

    let identityKey (verifier: TrustedVerifier) =
        match verifier.Identity with
        | VerifierIdentity.PinnedKey key -> key.KeyId
        | VerifierIdentity.Workload workload -> "workload:" + workload.WorkflowRef

    let private finding code detail remedy who = GovernanceFinding.create code detail remedy who

    /// Whether `verifier` may attest a receipt observed at `observedAt` under
    /// `policyId`. Refusal codes: VERIFIER_REVOKED, VERIFIER_NOT_YET_VALID,
    /// VERIFIER_EXPIRED, VERIFIER_OUT_OF_SCOPE.
    let admits (verifier: TrustedVerifier) (policyId: string) (observedAt: DateTimeOffset) =
        match verifier.RevokedAt with
        | Some revokedAt ->
            let reason = verifier.RevocationReason |> Option.defaultValue "no reason recorded"
            Error(
                finding
                    "GOV.VERIFIER_REVOKED"
                    $"verifier '{verifier.Id}' was revoked at {revokedAt:O} ({reason})"
                    "re-verify the work with a current verifier"
                    Principal.VerificationService
            )
        | None when observedAt < verifier.ValidFrom ->
            Error(finding "GOV.VERIFIER_NOT_YET_VALID" $"verifier '{verifier.Id}' is valid from {verifier.ValidFrom:O}, after the receipt" "re-verify the work" Principal.VerificationService)
        | None when not (Window.contains verifier.ValidFrom verifier.ValidUntil observedAt) ->
            Error(finding "GOV.VERIFIER_EXPIRED" $"verifier '{verifier.Id}' expired before the receipt was observed" "re-verify the work with the current verifier" Principal.VerificationService)
        | None when not verifier.PolicyScopes.IsEmpty && not (List.contains policyId verifier.PolicyScopes) ->
            Error(finding "GOV.VERIFIER_OUT_OF_SCOPE" $"verifier '{verifier.Id}' may not attest policy '{policyId}'" "use the verifier the trust root scopes to this policy" Principal.RepositoryAdministrator)
        | None -> Ok()

    /// How a candidate trust root differs from the protected one. Any added
    /// verifier, widened scope, extended validity or removed revocation is a
    /// weakening: it lets something attest that could not before. Weakening
    /// requires elevated authorization; revoking or narrowing does not.
    let weakenings (protectedRoot: VerifierTrustRoot) (candidate: VerifierTrustRoot) =
        let byId (root: VerifierTrustRoot) = root.Verifiers |> List.map (fun v -> v.Id, v) |> Map.ofList
        let before = byId protectedRoot

        let changed =
            candidate.Verifiers
            |> List.collect (fun verifier ->
                match before |> Map.tryFind verifier.Id with
                | None -> [ $"adds verifier '{verifier.Id}'" ]
                | Some previous ->
                    [ if identityKey previous <> identityKey verifier then $"replaces the identity of verifier '{verifier.Id}'"
                      if previous.RevokedAt.IsSome && verifier.RevokedAt.IsNone then $"removes the revocation of verifier '{verifier.Id}'"
                      if verifier.ValidFrom < previous.ValidFrom then $"moves verifier '{verifier.Id}' validity earlier"
                      match previous.ValidUntil, verifier.ValidUntil with
                      | Some _, None -> $"removes the expiry of verifier '{verifier.Id}'"
                      | Some a, Some b when b > a -> $"extends verifier '{verifier.Id}'"
                      | _ -> ()
                      if not previous.PolicyScopes.IsEmpty
                         && (verifier.PolicyScopes.IsEmpty || verifier.PolicyScopes |> List.exists (fun s -> not (List.contains s previous.PolicyScopes))) then
                          $"widens the scope of verifier '{verifier.Id}'" ])

        let repository = if candidate.Repository <> protectedRoot.Repository then [ "changes the repository the trust root governs" ] else []
        let version = if candidate.ConfigVersion <= protectedRoot.ConfigVersion && candidate <> protectedRoot then [ "changes the trust root without increasing configVersion" ] else []
        repository @ version @ changed

    // ---- receipts signed by a pinned key ----------------------------------

    let private parsePayloadAfterSignature (payload: byte array) =
        VerificationReceipt.parsePayload payload
        |> Result.mapError (fun message -> finding "GOV.RECEIPT_MALFORMED" message "re-verify the work" Principal.VerificationService)

    /// Verify one DSSE receipt against the trust root at its own observation
    /// time. The payload is parsed only after a signature verified.
    let verifyEnvelope (root: VerifierTrustRoot) (envelope: ReceiptEnvelope) : Result<VerifiedReceipt, GovernanceFinding> =
        let keyed =
            root.Verifiers
            |> List.choose (fun verifier ->
                match verifier.Identity with
                | VerifierIdentity.PinnedKey key -> Some(verifier, key)
                | VerifierIdentity.Workload _ -> None)

        let signedBy =
            envelope.Signatures
            |> List.tryPick (fun signature ->
                keyed
                |> List.tryFind (fun (_, key) -> key.KeyId = signature.KeyId)
                |> Option.map (fun (verifier, key) -> verifier, key, signature))

        match signedBy with
        | None ->
            Error(
                finding
                    "GOV.RECEIPT_UNTRUSTED_VERIFIER"
                    "no signature names a verifier the protected trust root pins"
                    "verify through the provisioned verifier; an implementer-held key is never trusted"
                    Principal.VerificationService
            )
        | Some(verifier, key, signature) when not (VerificationReceipt.verifySignature key envelope signature) ->
            Error(finding "GOV.RECEIPT_INVALID_SIGNATURE" $"the signature for verifier '{verifier.Id}' does not verify" "re-verify the work" Principal.VerificationService)
        | Some(verifier, key, _) ->
            parsePayloadAfterSignature envelope.Payload
            |> Result.bind (fun payload ->
                if payload.Verifier.KeyId <> key.KeyId then
                    Error(finding "GOV.RECEIPT_BINDING_MISMATCH" "the receipt names a different verifier key than the one that signed it" "re-verify the work" Principal.VerificationService)
                else
                    admits verifier payload.ValidationPolicyId payload.ObservedAt
                    |> Result.map (fun () ->
                        { Payload = payload
                          Digest = VerificationReceipt.identity envelope.Payload
                          VerifierId = verifier.Id
                          Basis = AuthenticationBasis.PinnedKey }))

    // ---- receipts bound to a CI workload identity -------------------------

    /// One RSA key of the issuer's JWKS (RFC 7517 `kty: RSA`).
    type IssuerKey = { Kid: string; Modulus: byte array; Exponent: byte array }

    /// `praxis.workload-receipt/1`: the exact receipt payload bytes and an
    /// OIDC token whose audience is `praxis-receipt:<payload identity>`, so
    /// the issuer's signature covers this payload and nothing else.
    type WorkloadReceipt = { Payload: byte array; Token: string }

    let audienceFor (payload: byte array) = "praxis-receipt:" + VerificationReceipt.identity payload

    let base64UrlDecode (value: string) =
        let padded =
            let s = value.Replace('-', '+').Replace('_', '/')
            match s.Length % 4 with
            | 2 -> s + "=="
            | 3 -> s + "="
            | _ -> s

        Convert.FromBase64String padded

    type TokenClaims =
        { Issuer: string
          Audience: string list
          Repository: string
          JobWorkflowRef: string
          EventName: string
          IssuedAt: DateTimeOffset
          RunId: string }

    let private claimsOf (json: byte array) =
        try
            use document = JsonDocument.Parse(ReadOnlyMemory json)
            let root = document.RootElement

            let text name =
                match root.TryGetProperty(name: string) with
                | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
                | true, value when value.ValueKind = JsonValueKind.Number -> value.GetRawText()
                | _ -> ""

            let audience =
                match root.TryGetProperty "aud" with
                | true, value when value.ValueKind = JsonValueKind.String -> [ value.GetString() ]
                | true, value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
                | _ -> []

            let issuedAt =
                match root.TryGetProperty "iat" with
                | true, value when value.ValueKind = JsonValueKind.Number -> DateTimeOffset.FromUnixTimeSeconds(value.GetInt64())
                | _ -> failwith "the token has no 'iat'"

            Ok
                { Issuer = text "iss"
                  Audience = audience
                  Repository = text "repository"
                  JobWorkflowRef = text "job_workflow_ref"
                  EventName = text "event_name"
                  IssuedAt = issuedAt
                  RunId = text "run_id" }
        with error ->
            Error error.Message

    /// Verify an RS256 JWT against the issuer keys and return its claims.
    /// Expiry is deliberately not checked: a receipt is verified long after
    /// its token's few minutes of validity. Freshness comes from the issuer
    /// key still being published (or archived) and from the trust window.
    let verifyToken (keys: IssuerKey list) (token: string) : Result<TokenClaims, string> =
        match token.Split '.' with
        | [| header; body; signature |] ->
            try
                use headerDocument = JsonDocument.Parse(base64UrlDecode header)
                let headerRoot = headerDocument.RootElement
                let algorithm = headerRoot.GetProperty("alg").GetString()
                let kid = match headerRoot.TryGetProperty "kid" with | true, v -> v.GetString() | _ -> ""

                if algorithm <> "RS256" then
                    Error $"token algorithm '{algorithm}' is not RS256"
                else
                    match keys |> List.tryFind (fun key -> key.Kid = kid) with
                    | None -> Error $"no issuer key '{kid}'"
                    | Some key ->
                        use rsa = RSA.Create()
                        rsa.ImportParameters(RSAParameters(Modulus = key.Modulus, Exponent = key.Exponent))
                        let signed = Encoding.ASCII.GetBytes(header + "." + body)

                        if rsa.VerifyData(signed, base64UrlDecode signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) then
                            claimsOf (base64UrlDecode body)
                        else
                            Error "the token signature does not verify"
            with error ->
                Error $"the token cannot be read: {error.Message}"
        | _ -> Error "the token is not a compact JWS"

    let verifyWorkload (root: VerifierTrustRoot) (keys: IssuerKey list) (receipt: WorkloadReceipt) : Result<VerifiedReceipt, GovernanceFinding> =
        let workloads =
            root.Verifiers
            |> List.choose (fun verifier ->
                match verifier.Identity with
                | VerifierIdentity.Workload workload -> Some(verifier, workload)
                | VerifierIdentity.PinnedKey _ -> None)

        let untrusted detail =
            Error(finding "GOV.RECEIPT_UNTRUSTED_VERIFIER" detail "verify through the protected verification workflow on the protected branch" Principal.VerificationService)

        match verifyToken keys receipt.Token with
        | Error message -> Error(finding "GOV.RECEIPT_INVALID_SIGNATURE" message "re-verify the work" Principal.VerificationService)
        | Ok claims when not (List.contains (audienceFor receipt.Payload) claims.Audience) ->
            Error(finding "GOV.RECEIPT_BINDING_MISMATCH" "the token's audience does not bind this receipt payload" "re-verify the work" Principal.VerificationService)
        | Ok claims ->
            match
                workloads
                |> List.tryFind (fun (_, workload) ->
                    workload.Issuer = claims.Issuer
                    && String.Equals(workload.Repository, claims.Repository, StringComparison.OrdinalIgnoreCase)
                    && workload.WorkflowRef = claims.JobWorkflowRef
                    && List.contains claims.EventName workload.Events)
            with
            | None -> untrusted $"workload '{claims.JobWorkflowRef}' ({claims.EventName}) is not a pinned verifier"
            | Some(verifier, _) ->
                parsePayloadAfterSignature receipt.Payload
                |> Result.bind (fun payload ->
                    // The token is the attestation time the issuer vouches for;
                    // a payload observed after it was not covered by it.
                    if payload.ObservedAt > claims.IssuedAt.AddMinutes 5.0 then
                        Error(finding "GOV.RECEIPT_BINDING_MISMATCH" "the receipt claims an observation after its attestation" "re-verify the work" Principal.VerificationService)
                    else
                        admits verifier payload.ValidationPolicyId claims.IssuedAt
                        |> Result.map (fun () ->
                            { Payload = payload
                              Digest = VerificationReceipt.identity receipt.Payload
                              VerifierId = verifier.Id
                              Basis = AuthenticationBasis.WorkloadIdentity }))

    let utc (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
