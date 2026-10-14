namespace Praxis.Infrastructure.Planning

open System
open System.IO
open System.Text.Json

/// Injected by the protected executor host, never by repository configuration.
/// The host owns storage, identity, revision monotonicity and audit durability.
type EcirHostGrant =
    { GroupId: string
      CohortId: string
      Source: CommittedEcirReference
      BlueprintDigest: string
      Ordo: PinnedOrdoRelease
      /// Independent work-item -> original source requirement identity.
      MemberRequirements: Map<string, string>
      ReceiptJson: string }

type EcirHostPolicy =
    { Revision: string
      RepositoryRoot: string
      QualifiedRelease: bool
      TrustedSigners: Map<string, EcirTrustedSigner>
      Grants: EcirHostGrant list }

/// A host-owned port. Implementations must serialize policy mutation and
/// dispatch, protect the policy from the construction agent, and refuse rollback.
/// Load and Commit are not implementable through an agent-writable ros.json.
type EcirHostAuthority =
    { Load: unit -> Result<EcirHostPolicy, string>
      UtcNow: unit -> DateTimeOffset
      /// Compare-and-commit under the host policy lock. If the revision or
      /// signer changed, or authorization expired, return Error without writes.
      /// The callback performs the repository transaction, under its own locks.
      Commit: string -> DateTimeOffset -> (unit -> Result<unit, string>) -> Result<unit, string> }

type EcirDispatchEvidence =
    { PolicyRevision: string
      VerifiedAt: DateTimeOffset
      Approval: EcirApprovalEvidence
      Validator: EcirValidatorResult }

[<RequireQualifiedAccess>]
module EcirHostPolicy =
    let private sameRoot left right =
        let comparison = if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
        String.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath left),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath right), comparison)

    /// Host grants must cover the exact current members. Repo-authored source
    /// claims never substitute for this independently provisioned identity map.
    let resolve (root: string) (groupId: string) (members: string list) (policy: EcirHostPolicy) =
        try
            if String.IsNullOrWhiteSpace policy.Revision then Error "ECIR host policy revision missing"
            elif not (Path.IsPathFullyQualified policy.RepositoryRoot && sameRoot root policy.RepositoryRoot) then
                Error "ECIR host policy is for a different repository"
            elif not policy.QualifiedRelease then Error "ECIR execution release has not been host-qualified"
            elif members.IsEmpty || members.Length <> (Set.ofList members).Count then
                Error "ECIR current group members are empty or duplicated"
            else
                match policy.Grants |> List.filter (fun grant -> grant.GroupId = groupId) with
                | [ grant ] when not (String.IsNullOrWhiteSpace grant.CohortId)
                                  && Set.ofList members = (grant.MemberRequirements |> Map.keys |> Set.ofSeq) ->
                    let keys = members |> List.map (fun memberId -> grant.MemberRequirements[memberId])
                    if keys |> List.exists String.IsNullOrWhiteSpace
                       || keys.Length <> (Set.ofList keys).Count then
                        Error "ECIR host member requirements are empty or duplicated"
                    else Ok(grant, keys)
                | _ -> Error "ECIR host must grant exactly one cohort for the exact current group membership"
        with
        | :? ArgumentException as e -> Error("invalid ECIR host repository identity: " + e.Message)
        | :? NotSupportedException as e -> Error("invalid ECIR host repository identity: " + e.Message)

    /// Authorization is checked AFTER Ordo has completed, with a fresh host
    /// time. A timestamp sampled before the potentially slow validator is unsafe.
    let verifyAfterValidation
        (policy: EcirHostPolicy)
        (grant: EcirHostGrant)
        (keys: string list)
        (docs: CommittedEcirDocuments)
        (validator: EcirValidatorResult)
        (nowUtc: DateTimeOffset) =
        let observed =
            if docs.Commit <> grant.Source.Commit || docs.SourceManifestDigest <> grant.Source.SourceManifestDigest
               || validator.BlueprintDigest <> grant.BlueprintDigest
               || validator.ValidatedByExecutableSha256 <> grant.Ordo.Sha256 then
                Error "ECIR validator observation differs from host source or release pins"
            else FileEcirAuthorization.deriveScope docs validator grant.GroupId grant.CohortId keys
        observed
        |> Result.bind (fun scope ->
            EcirApprovals.read grant.ReceiptJson
            |> Result.bind (EcirApprovals.verify policy.TrustedSigners scope nowUtc))

    /// Called only inside the existing repository work/group transaction lock.
    /// Work-item mutation is staged separately, so refusal discards the stage.
    /// Commit provides the host-policy linearization point; repository recovery
    /// must complete that accepted operation, not re-start a member.
    let dispatch
        (authority: EcirHostAuthority)
        (root: string)
        (groupId: string)
        (readMembers: unit -> Result<string list, string>)
        (validate: EcirHostGrant -> Result<CommittedEcirDocuments * EcirValidatorResult, string>)
        (stage: EcirDispatchEvidence -> Result<(unit -> Result<unit, string>), string>) =
        authority.Load()
        |> Result.bind (fun policy ->
            readMembers()
            |> Result.bind (fun members ->
                resolve root groupId members policy
                |> Result.bind (fun (grant, keys) ->
                    validate grant
                    |> Result.bind (fun (docs, validator) ->
                        // Derivation consumes only independently validated inputs.
                        let verifiedAt = authority.UtcNow()
                        verifyAfterValidation policy grant keys docs validator verifiedAt
                        |> Result.bind (fun approval ->
                            let evidence =
                                { PolicyRevision = policy.Revision
                                  VerifiedAt = verifiedAt
                                  Approval = approval
                                  Validator = validator }
                            stage evidence
                            |> Result.bind (fun commitRepository ->
                                // Re-read both sources after staging; never consume
                                // the previously positive preflight as a capability.
                                authority.Load()
                                |> Result.bind (fun current ->
                                    readMembers()
                                    |> Result.bind (fun currentMembers ->
                                        if current <> policy || currentMembers <> members then
                                            Error "ECIR host policy or group membership changed before dispatch"
                                        else
                                            let commitAt = authority.UtcNow()
                                            verifyAfterValidation current grant keys docs validator commitAt
                                            |> Result.bind (fun _ ->
                                                let expiresAt = DateTimeOffset.Parse approval.ExpiresAt
                                                authority.Commit policy.Revision expiresAt commitRepository
                                                |> Result.map (fun () -> evidence))))))))))
