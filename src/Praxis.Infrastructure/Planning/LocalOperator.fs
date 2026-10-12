namespace Praxis.Infrastructure.Planning

open System
open System.Formats.Asn1
open System.Globalization
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Execution
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// An unsigned ECIR cohort grant, prepared for an external signer.
type LocalGrantDraft =
    { Approval: EcirSignedApproval
      ManifestPath: string; BlueprintPath: string
      Members: Map<string, string> }

/// A coordinator's proposal. Source, cohort, receipt, revision, requirement
/// keys and validator pins are filled from protected host state, never from
/// the proposer.
type LocalPacketProposal =
    { DispatchId: string; AttemptId: string; ParentExecutionId: string; ChildExecutionId: string
      WorkerId: string; Role: ExecutionRole; GroupId: string
      Members: (string * string) list; DecisionIds: string list; AllowedPaths: string list
      Prerequisites: string list
      /// obligation id, member work item, acceptance validator id
      Acceptance: (string * string * string) list
      IssuedAt: DateTimeOffset; ExpiresAt: DateTimeOffset; TimeoutSeconds: int; MaxOutputBytes: int }

/// Operator administration without private keys. Praxis writes the exact
/// bytes to sign and verifies an externally produced ECDSA P-256 signature
/// (DER or IEEE P1363) against the protected policy before using it. Key
/// generation, custody and signing remain the operator's own tools.
[<RequireQualifiedAccess>]
module LocalOperator =
    let private utc (value: DateTimeOffset) = value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)

    /// Canonical SubjectPublicKeyInfo PEM; private material is refused.
    let canonicalPublicKey (pem: string) =
        try
            if isNull pem || pem.Contains("PRIVATE KEY", StringComparison.Ordinal) then Error "only a public key may be provisioned"
            else
                use key = ECDsa.Create()
                key.ImportFromPem pem
                if key.ExportParameters(false).Curve.Oid.Value <> ECCurve.NamedCurves.nistP256.Oid.Value then Error "operator key must be NIST P-256"
                else Ok(key.ExportSubjectPublicKeyInfoPem())
        with error -> Error("invalid public key: " + error.Message)

    /// Accept a 64-byte P1363 signature or an ASN.1 DER ECDSA-Sig-Value (as
    /// produced by `openssl dgst -sha256 -sign`), returning P1363.
    let normalizeSignature (bytes: byte array) =
        try
            if bytes.Length = 64 then Ok bytes
            else
                let reader = AsnReader(ReadOnlyMemory bytes, AsnEncodingRules.DER)
                let sequence = reader.ReadSequence()
                let part () =
                    let value = sequence.ReadIntegerBytes().ToArray() |> Array.skipWhile ((=) 0uy)
                    if value.Length > 32 then invalidOp "signature integer exceeds P-256"
                    Array.append (Array.zeroCreate (32 - value.Length)) value
                let r = part ()
                let s = part ()
                sequence.ThrowIfNotEmpty()
                reader.ThrowIfNotEmpty()
                Ok(Array.append r s)
        with error -> Error("signature is neither P1363 nor DER ECDSA: " + error.Message)

    let private verifyWith (signer: EcirTrustedSigner) (payload: byte array) (signature: byte array) =
        use key = ECDsa.Create()
        key.ImportFromPem signer.PublicKeyPem
        key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)

    let private signer (policy: EcirHostPolicy) keyId approver =
        match policy.TrustedSigners |> Map.tryFind keyId with
        | Some value when value.Approver = approver -> Ok value
        | Some _ -> Error "signer approver differs from the protected policy"
        | None -> Error "signer key is not trusted by the protected policy"

    // ---------------------------------------------------------------- policy

    let initialize (configuration: LocalHostConfiguration) revision keyId approver publicPem qualifiedRelease =
        canonicalPublicKey publicPem |> Result.bind (fun pem ->
            let store = FileLocalRuntime.hostStore configuration
            store.Initialize
                { Revision = revision; RepositoryRoot = configuration.RepositoryRoot; QualifiedRelease = qualifiedRelease
                  TrustedSigners = Map.ofList [ keyId, { Approver = approver; PublicKeyPem = pem } ]
                  Grants = [] })

    // ----------------------------------------------------------------- grant

    let grantDraft (configuration: LocalHostConfiguration) keyId approver groupId cohortId sourceCommit manifestPath blueprintPath
                   (members: Map<string, string>) (decisions: string list) (issuedAt: DateTimeOffset) (expiresAt: DateTimeOffset) cancellation =
        let git, root = configuration.Git, configuration.RepositoryRoot
        if members.IsEmpty || decisions.IsEmpty then Error "a grant needs members and decisions"
        else
            FileLocalGitCandidates.resolveCommit git root sourceCommit cancellation |> Result.bind (fun source ->
                FileLocalGitCandidates.blobDigest git root source manifestPath cancellation |> Result.bind (fun manifest ->
                    FileLocalGitCandidates.blobDigest git root source blueprintPath cancellation |> Result.map (fun blueprint ->
                        { Approval =
                            { SchemaVersion = EcirApprovals.SchemaVersion; KeyId = keyId; Approver = approver
                              IssuedAt = utc issuedAt; ExpiresAt = utc expiresAt; Signature = ""
                              Scope = { GroupId = groupId; CohortId = cohortId; SourceCommit = source
                                        ManifestDigest = manifest; BlueprintDigest = blueprint
                                        RequirementKeys = members |> Map.values |> Seq.toList |> List.sort
                                        DecisionIds = List.sort decisions } }
                          ManifestPath = manifestPath; BlueprintPath = blueprintPath; Members = members })))

    let grantPayload (draft: LocalGrantDraft) = EcirApprovals.signingPayload draft.Approval

    let private receiptJson (approval: EcirSignedApproval) =
        let s = approval.Scope
        JsonSerializer.Serialize
            {| schemaVersion = approval.SchemaVersion; keyId = approval.KeyId; approver = approval.Approver
               issuedAt = approval.IssuedAt; expiresAt = approval.ExpiresAt; signature = approval.Signature
               scope = {| groupId = s.GroupId; cohortId = s.CohortId; sourceCommit = s.SourceCommit
                          manifestDigest = s.ManifestDigest; blueprintDigest = s.BlueprintDigest
                          requirementKeys = s.RequirementKeys; decisionIds = s.DecisionIds |} |}

    let renderDraft (draft: LocalGrantDraft) =
        let node = JsonObject()
        node["schemaVersion"] <- JsonValue.Create "praxis.local-grant-draft/1"
        node["approval"] <- JsonNode.Parse(receiptJson draft.Approval)
        node["manifestPath"] <- JsonValue.Create draft.ManifestPath
        node["blueprintPath"] <- JsonValue.Create draft.BlueprintPath
        let members = JsonObject()
        for KeyValue(id, key) in draft.Members do members[id] <- JsonValue.Create key
        node["members"] <- members
        node.ToJsonString(JsonSerializerOptions(WriteIndented = true))

    let readDraft (json: string) =
        try
            let node = JsonNode.Parse json :?> JsonObject
            if node.Count <> 5 || node["schemaVersion"].GetValue<string>() <> "praxis.local-grant-draft/1" then Error "unsupported grant draft"
            else
                // Drafts are unsigned; the shared strict reader requires a
                // signature field, so a placeholder is read and then cleared.
                let approval = node["approval"].DeepClone()
                if approval["signature"].GetValue<string>() <> "" then invalidOp "a grant draft must be unsigned"
                approval["signature"] <- JsonValue.Create "unsigned-draft"
                EcirApprovals.read (approval.ToJsonString()) |> Result.map (fun approval ->
                    { Approval = { approval with Signature = "" }
                      ManifestPath = node["manifestPath"].GetValue<string>(); BlueprintPath = node["blueprintPath"].GetValue<string>()
                      Members = (node["members"] :?> JsonObject) |> Seq.map (fun pair -> pair.Key, pair.Value.GetValue<string>()) |> Map.ofSeq })
        with error -> Error("invalid grant draft: " + error.Message)

    /// Verify the external signature against the protected signer and current
    /// scope, then add the grant through the audited policy compare-and-commit.
    let applyGrant (configuration: LocalHostConfiguration) (draft: LocalGrantDraft) (signature: byte array) newRevision (now: DateTimeOffset) =
        let store = FileLocalRuntime.hostStore configuration
        normalizeSignature signature |> Result.bind (fun normalized ->
            store.Load() |> Result.bind (fun policy ->
                signer policy draft.Approval.KeyId draft.Approval.Approver |> Result.bind (fun trusted ->
                    let approval = { draft.Approval with Signature = Convert.ToBase64String normalized }
                    if not (verifyWith trusted (EcirApprovals.signingPayload approval) normalized) then Error "grant signature did not verify against the trusted key"
                    elif (Set.ofList (draft.Members |> Map.values |> Seq.toList)) <> Set.ofList approval.Scope.RequirementKeys then Error "grant members differ from the signed scope"
                    else
                        let receipt = receiptJson approval
                        EcirApprovals.read receipt
                        |> Result.bind (EcirApprovals.verify policy.TrustedSigners approval.Scope now)
                        |> Result.bind (fun _ ->
                            let s = approval.Scope
                            let grant : EcirHostGrant =
                                { GroupId = s.GroupId; CohortId = s.CohortId
                                  Source = { Commit = s.SourceCommit; ManifestPath = draft.ManifestPath; BlueprintPath = draft.BlueprintPath
                                             SourceManifestDigest = s.ManifestDigest }
                                  BlueprintDigest = s.BlueprintDigest
                                  // Local delegation does not run Ordo. The pin is
                                  // explicit so the ECIR dispatch path refuses it.
                                  Ordo = { Executable = "unpinned:local-delegation"; Sha256 = "unpinned" }
                                  MemberRequirements = draft.Members; ReceiptJson = receipt }
                            let audit : EcirHostAudit =
                                { Id = "AUD-" + Guid.NewGuid().ToString("N"); Action = "granted"; Approver = approval.Approver
                                  KeyId = approval.KeyId; GroupId = Some s.GroupId; PreviousRevision = policy.Revision
                                  NewRevision = newRevision; OccurredAt = now
                                  ReceiptSha256 = Some(EcirDispatchTransaction.hashText receipt) }
                            store.Apply(policy.Revision, { policy with Revision = newRevision; Grants = policy.Grants @ [ grant ] }, audit)))))

    let revoke (configuration: LocalHostConfiguration) keyId newRevision (now: DateTimeOffset) =
        let store = FileLocalRuntime.hostStore configuration
        store.Load() |> Result.bind (fun policy ->
            match policy.TrustedSigners |> Map.tryFind keyId with
            | None -> Error "signer is not trusted"
            | Some trusted ->
                let updated = { policy with Revision = newRevision; TrustedSigners = policy.TrustedSigners.Remove keyId }
                let audit : EcirHostAudit =
                    { Id = "AUD-" + Guid.NewGuid().ToString("N"); Action = "revoked"; Approver = trusted.Approver; KeyId = keyId
                      GroupId = None; PreviousRevision = policy.Revision; NewRevision = newRevision; OccurredAt = now; ReceiptSha256 = None }
                store.Apply(policy.Revision, updated, audit))

    // ---------------------------------------------------------------- packets

    let propose (configuration: LocalHostConfiguration) (proposal: LocalPacketProposal) =
        let store = FileLocalRuntime.hostStore configuration
        store.Load() |> Result.bind (fun policy ->
            match policy.Grants |> List.filter (fun grant -> grant.GroupId = proposal.GroupId) with
            | [ grant ] ->
                let members =
                    proposal.Members |> List.map (fun (workItem, execution) ->
                        match grant.MemberRequirements |> Map.tryFind workItem with
                        | Some key -> Ok { LocalHandoffMember.WorkItemId = workItem; ExecutionId = execution; RequirementKeys = [ key ] }
                        | None -> Error("work item is not in the host grant: " + workItem))
                let acceptance =
                    proposal.Acceptance |> List.map (fun (obligation, workItem, validatorId) ->
                        match grant.MemberRequirements |> Map.tryFind workItem with
                        | None -> Error("acceptance names a work item outside the grant: " + workItem)
                        | Some key ->
                            FileLocalRuntime.validatorPin configuration validatorId |> Result.map (fun pin ->
                                { LocalHandoffAcceptance.ObligationId = obligation; WorkItemId = workItem; RequirementKeys = [ key ]
                                  ValidatorId = validatorId; InputDigest = pin }))
                let collect results =
                    results |> List.fold (fun acc item -> acc |> Result.bind (fun values -> item |> Result.map (fun value -> values @ [ value ]))) (Ok [])
                collect members |> Result.bind (fun members ->
                    collect acceptance |> Result.bind (fun acceptance ->
                        FileLocalRuntime.observePrerequisites configuration.RepositoryRoot () |> Result.bind (fun complete ->
                            let prerequisites =
                                proposal.Prerequisites |> List.map (fun id ->
                                    match complete |> Map.tryFind id with
                                    | Some digest -> Ok { LocalHandoffPrerequisite.WorkItemId = id; EvidenceDigest = digest }
                                    | None -> Error("prerequisite is not natively complete: " + id))
                            collect prerequisites |> Result.bind (fun prerequisites ->
                                let packet : LocalWorkerPacket =
                                    { SchemaVersion = LocalAgentHandoff.PacketSchema
                                      DispatchId = proposal.DispatchId; AttemptId = proposal.AttemptId
                                      ParentExecutionId = proposal.ParentExecutionId; ChildExecutionId = proposal.ChildExecutionId
                                      WorkerId = proposal.WorkerId; Role = proposal.Role
                                      RepositoryIdentity = configuration.RepositoryIdentity
                                      SourceCommit = grant.Source.Commit; SourceManifestDigest = grant.Source.SourceManifestDigest
                                      BlueprintDigest = grant.BlueprintDigest; GroupId = grant.GroupId; CohortId = grant.CohortId
                                      Members = members; DecisionIds = proposal.DecisionIds; AllowedPaths = proposal.AllowedPaths
                                      Prerequisites = prerequisites; Acceptance = acceptance
                                      AuthorityRevision = policy.Revision; ReceiptDigest = "sha256:" + EcirDispatchTransaction.hashText grant.ReceiptJson
                                      IssuedAt = proposal.IssuedAt.ToUniversalTime(); ExpiresAt = proposal.ExpiresAt.ToUniversalTime()
                                      TimeoutSeconds = proposal.TimeoutSeconds; MaxOutputBytes = proposal.MaxOutputBytes }
                                match LocalAgentHandoff.packetProblems packet with
                                | [] -> Ok(LocalAgentHandoff.canonicalPacket packet)
                                | problems -> Error(String.concat "; " problems)))))
            | _ -> Error("the protected policy has no single grant for group " + proposal.GroupId))

    // ------------------------------------------------------------- delegation

    let private unsignedDelegation keyId approver packet =
        { SchemaVersion = LocalWorkerDelegation.SchemaVersion; KeyId = keyId; Approver = approver
          PacketDigest = LocalAgentHandoff.packetDigest packet; Signature = "" }

    let delegationPayload keyId approver packet = LocalWorkerDelegation.signingPayload (unsignedDelegation keyId approver packet)

    /// Human review text for the exact packet the operator is about to sign.
    let review (packet: LocalWorkerPacket) =
        [ yield sprintf "dispatch %s attempt %s (packet %s)" packet.DispatchId packet.AttemptId (LocalAgentHandoff.packetDigest packet)
          yield sprintf "worker %s as %s; child execution %s of coordinator %s" packet.WorkerId (ExecutionRole.toWire packet.Role) packet.ChildExecutionId packet.ParentExecutionId
          yield sprintf "repository %s at source %s; group %s cohort %s; policy %s" packet.RepositoryIdentity packet.SourceCommit packet.GroupId packet.CohortId packet.AuthorityRevision
          for item in packet.Members do yield sprintf "member %s (execution %s): %s" item.WorkItemId item.ExecutionId (String.concat ", " item.RequirementKeys)
          yield "may change: " + String.concat ", " packet.AllowedPaths
          for item in packet.Prerequisites do yield sprintf "requires completed %s (%s)" item.WorkItemId item.EvidenceDigest
          for item in packet.Acceptance do yield sprintf "acceptance %s for %s by %s (%s)" item.ObligationId item.WorkItemId item.ValidatorId item.InputDigest
          yield sprintf "decisions: %s" (String.concat ", " packet.DecisionIds)
          yield sprintf "valid %s .. %s; timeout %ds; output %d bytes" (packet.IssuedAt.ToString("O")) (packet.ExpiresAt.ToString("O")) packet.TimeoutSeconds packet.MaxOutputBytes ]

    /// Assemble the delegation and verify it exactly as dispatch will.
    let assembleDelegation (configuration: LocalHostConfiguration) keyId approver (packet: LocalWorkerPacket) (signature: byte array) (now: DateTimeOffset) =
        normalizeSignature signature |> Result.bind (fun normalized ->
            let envelope = { unsignedDelegation keyId approver packet with Signature = Convert.ToBase64String normalized }
            (FileLocalRuntime.hostStore configuration).Load() |> Result.bind (fun policy ->
                LocalWorkerDelegation.verify configuration.RepositoryRoot configuration.RepositoryIdentity policy now packet envelope
                |> Result.map (fun _ -> LocalWorkerDelegation.render envelope)))
