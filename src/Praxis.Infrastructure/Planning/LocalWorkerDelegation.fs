namespace Praxis.Infrastructure.Planning

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Praxis.Domain.Work

/// Untrusted envelope. The protected signer approves the complete packet digest,
/// independently of the narrower ECIR source/cohort approval.
type SignedLocalWorkerDelegation =
    { SchemaVersion: string; KeyId: string; Approver: string
      PacketDigest: string; Signature: string }

[<RequireQualifiedAccess>]
module LocalWorkerDelegation =
    [<Literal>]
    let SchemaVersion = "praxis.local-worker-delegation/1"

    let private clean (text: string) =
        not (String.IsNullOrWhiteSpace text) && text.Length <= 1024
        && text = text.Trim() && not (text |> Seq.exists Char.IsControl)

    /// Domain-separated, UTF-8 byte-length framing. The packet digest binds all
    /// identities, time, resource, prerequisite, acceptance and budget fields.
    let signingPayload (envelope: SignedLocalWorkerDelegation) =
        let buffer = StringBuilder("praxis-signed-local-worker-delegation/1\n")
        for field in [ envelope.SchemaVersion; envelope.KeyId; envelope.Approver; envelope.PacketDigest ] do
            buffer.Append(Encoding.UTF8.GetByteCount field).Append(':').Append(field) |> ignore
        Encoding.UTF8.GetBytes(buffer.ToString())

    let render (envelope: SignedLocalWorkerDelegation) =
        JsonSerializer.Serialize {| schemaVersion = envelope.SchemaVersion; keyId = envelope.KeyId
                                    approver = envelope.Approver; packetDigest = envelope.PacketDigest
                                    signature = envelope.Signature |}

    let read (json: string) =
        try
            if isNull json || json.Length > 8192 || Encoding.UTF8.GetByteCount json > 8192 then
                Error "local delegation exceeds 8192 UTF-8 bytes"
            else
                use document = JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = 2))
                let root = document.RootElement
                let names = [ "schemaVersion"; "keyId"; "approver"; "packetDigest"; "signature" ]
                let actual = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                if actual.Length <> names.Length || Set.ofList actual <> Set.ofList names then
                    Error "local delegation has missing, unknown or duplicate fields"
                else
                    let text name =
                        let value = root.GetProperty(name: string)
                        if value.ValueKind <> JsonValueKind.String then invalidOp "expected string"
                        let result = value.GetString()
                        if not (clean result) then invalidOp "invalid delegation text"
                        result
                    Ok { SchemaVersion = text "schemaVersion"; KeyId = text "keyId"; Approver = text "approver"
                         PacketDigest = text "packetDigest"; Signature = text "signature" }
        with
        | :? JsonException | :? InvalidOperationException | :? ArgumentException -> Error "invalid local delegation JSON"

    let private verifySignature (policy: EcirHostPolicy) envelope =
        match Map.tryFind envelope.KeyId policy.TrustedSigners with
        | None -> Error "local delegation signer is revoked or unknown"
        | Some signer when signer.Approver <> envelope.Approver -> Error "local delegation signer identity differs"
        | Some signer ->
            try
                use key = ECDsa.Create()
                key.ImportFromPem signer.PublicKeyPem
                let signature = Convert.FromBase64String envelope.Signature
                if signer.PublicKeyPem <> key.ExportSubjectPublicKeyInfoPem()
                   || key.ExportParameters(false).Curve.Oid.Value <> ECCurve.NamedCurves.nistP256.Oid.Value
                   || signature.Length <> 64
                   || not (key.VerifyData(signingPayload envelope, signature, HashAlgorithmName.SHA256,
                                          DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) then
                    Error "local delegation signature did not verify"
                else Ok()
            with
            | :? CryptographicException | :? ArgumentException | :? FormatException -> Error "invalid local delegation key or signature"

    /// Call with freshly loaded host policy, an independently bound repository
    /// identity, and the packet to be dispatched. This does not qualify the OS,
    /// validator, worktree, member executions or native lifecycle observations.
    let verify root repositoryIdentity (policy: EcirHostPolicy) (now: DateTimeOffset) (packet: LocalWorkerPacket) envelope =
        let problems = LocalAgentHandoff.packetProblems packet
        if not problems.IsEmpty then Error(String.concat "; " problems)
        elif now.Offset <> TimeSpan.Zero || now < packet.IssuedAt || now >= packet.ExpiresAt then Error "local delegation expired, future-dated or clock is not UTC"
        elif not (clean repositoryIdentity) || packet.RepositoryIdentity <> repositoryIdentity then Error "local delegation repository identity differs"
        elif packet.AuthorityRevision <> policy.Revision then Error "local delegation policy revision changed"
        elif envelope.SchemaVersion <> SchemaVersion || not (clean envelope.KeyId && clean envelope.Approver)
             || not (LocalAgentHandoff.isDigest envelope.PacketDigest) || not (clean envelope.Signature)
             || envelope.PacketDigest <> LocalAgentHandoff.packetDigest packet then Error "local delegation does not bind the exact packet"
        else
            EcirHostPolicy.resolve root packet.GroupId (packet.Members |> List.map _.WorkItemId) policy
            |> Result.bind (fun (grant, keys) ->
                if packet.CohortId <> grant.CohortId || packet.SourceCommit <> grant.Source.Commit
                   || packet.SourceManifestDigest <> grant.Source.SourceManifestDigest || packet.BlueprintDigest <> grant.BlueprintDigest
                   || packet.ReceiptDigest <> "sha256:" + EcirDispatchTransaction.hashText grant.ReceiptJson
                   || packet.Members |> List.exists (fun memberItem -> memberItem.RequirementKeys <> [ grant.MemberRequirements[memberItem.WorkItemId] ]) then
                    Error "local delegation differs from current host grant"
                else
                    let scope : EcirApprovalScope =
                        { GroupId = packet.GroupId; CohortId = packet.CohortId; SourceCommit = packet.SourceCommit
                          ManifestDigest = packet.SourceManifestDigest; BlueprintDigest = packet.BlueprintDigest
                          RequirementKeys = keys; DecisionIds = packet.DecisionIds }
                    EcirApprovals.read grant.ReceiptJson |> Result.bind (fun receipt ->
                        EcirApprovals.verify policy.TrustedSigners scope now receipt |> Result.bind (fun _ ->
                            let issued = DateTimeOffset.Parse receipt.IssuedAt
                            let expires = DateTimeOffset.Parse receipt.ExpiresAt
                            if packet.IssuedAt < issued || packet.ExpiresAt > expires then Error "local delegation extends beyond ECIR approval"
                            elif receipt.KeyId <> envelope.KeyId || receipt.Approver <> envelope.Approver then Error "local delegation must use the ECIR approver key"
                            else verifySignature policy envelope
                                 |> Result.map (fun () -> { Enabled = true; Revision = policy.Revision; ExpectedPacket = LocalAgentHandoff.canonicalPacket packet }))))

    /// Every admission/reservation/intent/intake invocation reloads the host
    /// policy and envelope. Never cache a previously positive authority result.
    /// Caller still holds the controller's protected lock scope at each boundary.
    let authority root repositoryIdentity loadPolicy loadEnvelope clock packet () =
        try
            loadPolicy() |> Result.bind (fun policy ->
                loadEnvelope() |> Result.bind read |> Result.bind (verify root repositoryIdentity policy (clock()) packet))
        with _ -> Error "local delegation host observation unavailable"
