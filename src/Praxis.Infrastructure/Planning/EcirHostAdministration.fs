namespace Praxis.Infrastructure.Planning

open System
open System.Text.Json

/// Durable host audit, committed in the SAME host transaction as policy changes.
/// Storage and authenticated caller identity belong to the host, not the agent.
type EcirHostAudit =
    { Id: string
      Action: string
      Approver: string
      KeyId: string
      GroupId: string option
      PreviousRevision: string
      NewRevision: string
      OccurredAt: DateTimeOffset
      ReceiptSha256: string option }

type EcirHostAdministration =
    { Load: unit -> Result<EcirHostPolicy, string>
      AuthenticatedApprover: unit -> Result<string, string>
      UtcNow: unit -> DateTimeOffset
      /// Runs in a signer service; this port never exposes private keys.
      Sign: string -> byte array -> Result<byte array, string>
      /// Host authentication must enforce administrative revocation rights.
      CanRevoke: string -> string -> bool
      /// CAS and atomic policy + append-only audit persistence. On error,
      /// neither policy nor audit may become visible. No receipt is returned.
      Apply: string -> EcirHostPolicy -> EcirHostAudit -> Result<unit, string> }

[<RequireQualifiedAccess>]
module EcirHostAdministration =
    let private serialize (receipt: EcirSignedApproval) =
        let s = receipt.Scope
        JsonSerializer.Serialize
            {| schemaVersion = receipt.SchemaVersion; keyId = receipt.KeyId; approver = receipt.Approver
               issuedAt = receipt.IssuedAt; expiresAt = receipt.ExpiresAt; signature = receipt.Signature
               scope = {| groupId = s.GroupId; cohortId = s.CohortId; sourceCommit = s.SourceCommit
                          manifestDigest = s.ManifestDigest; blueprintDigest = s.BlueprintDigest
                          requirementKeys = s.RequirementKeys; decisionIds = s.DecisionIds |} |}

    let private audit action approver keyId groupId revision now receipt =
        { Id = Guid.NewGuid().ToString("N"); Action = action; Approver = approver; KeyId = keyId
          GroupId = groupId; PreviousRevision = revision; NewRevision = Guid.NewGuid().ToString("N")
          OccurredAt = now; ReceiptSha256 = receipt |> Option.map EcirDispatchTransaction.hashText }

    /// Admin review approves ONE independently validated scope. Every grant
    /// member and decision is covered; an agent cannot issue itself a receipt.
    let issue
        (host: EcirHostAdministration)
        (root: string)
        (groupId: string)
        (members: string list)
        (keyId: string)
        (lifetime: TimeSpan)
        (validate: EcirHostGrant -> Result<CommittedEcirDocuments * EcirValidatorResult, string>) =
        if lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours 24. then Error "ECIR approval lifetime must be positive and at most 24 hours"
        else
            host.AuthenticatedApprover()
            |> Result.bind (fun approver ->
                host.Load()
                |> Result.bind (fun policy ->
                    match policy.TrustedSigners |> Map.tryFind keyId with
                    | None -> Error "ECIR issuance key is revoked or unknown"
                    | Some signer when signer.Approver <> approver -> Error "ECIR authenticated approver does not own this signer key"
                    | Some _ ->
                        EcirHostPolicy.resolve root groupId members policy
                        |> Result.bind (fun (grant, keys) ->
                            validate grant
                            |> Result.bind (fun (docs, validator) ->
                                if docs.Commit <> grant.Source.Commit || docs.SourceManifestDigest <> grant.Source.SourceManifestDigest
                                   || validator.BlueprintDigest <> grant.BlueprintDigest
                                   || validator.ValidatedByExecutableSha256 <> grant.Ordo.Sha256 then
                                    Error "ECIR issuance validation differs from host pins"
                                else
                                    FileEcirAuthorization.deriveScope docs validator groupId grant.CohortId keys
                                    |> Result.bind (fun scope ->
                                        let now = host.UtcNow()
                                        let format (at: DateTimeOffset) = at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
                                        let unsigned =
                                            { SchemaVersion = EcirApprovals.SchemaVersion; KeyId = keyId; Approver = approver
                                              IssuedAt = format now; ExpiresAt = format (now.Add lifetime)
                                              Scope = scope; Signature = "" }
                                        host.Sign keyId (EcirApprovals.signingPayload unsigned)
                                        |> Result.bind (fun signature ->
                                            let signed = { unsigned with Signature = Convert.ToBase64String signature }
                                            // Independently verify signer-service output before publishing it.
                                            EcirApprovals.verify policy.TrustedSigners scope (host.UtcNow()) signed
                                            |> Result.bind (fun _ ->
                                                let json = serialize signed
                                                let record = audit "issued" approver keyId (Some groupId) policy.Revision now (Some json)
                                                let updated =
                                                    { policy with Revision = record.NewRevision
                                                                  Grants = policy.Grants |> List.map (fun g -> if g.GroupId = groupId then { g with ReceiptJson = json } else g) }
                                                host.Apply policy.Revision updated record
                                                |> Result.map (fun () -> json))))))))

    /// Removing a key invalidates all its receipts. Audit and policy mutation
    /// share the same host CAS boundary; approval identities are not CLI flags.
    let revoke (host: EcirHostAdministration) (keyId: string) =
        host.AuthenticatedApprover()
        |> Result.bind (fun approver ->
            if not (host.CanRevoke approver keyId) then Error "ECIR signer revocation requires host administrator rights"
            else
                host.Load()
                |> Result.bind (fun policy ->
                    if not (policy.TrustedSigners.ContainsKey keyId) then Error "ECIR signer key is already revoked or unknown"
                    else
                        let record = audit "revoked" approver keyId None policy.Revision (host.UtcNow()) None
                        let updated = { policy with Revision = record.NewRevision; TrustedSigners = policy.TrustedSigners.Remove keyId }
                        host.Apply policy.Revision updated record))
