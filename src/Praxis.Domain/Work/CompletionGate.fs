namespace Praxis.Domain.Work

open System
open System.Security.Cryptography
open System.Text

/// Verified completion (PRX-REL-006/007): Praxis builds the untrusted
/// `praxis.completion-packet/1` from the agent's claim and the
/// `dokimos.completion-authority/1` from state Praxis holds or observes
/// itself, then trusts only Dokimos's receipt. Every function here is pure.
[<RequireQualifiedAccess>]
type CheckStatus =
    | Passed
    | Failed
    | Unavailable

[<RequireQualifiedAccess>]
module CheckStatus =
    let code status =
        match status with
        | CheckStatus.Passed -> "passed"
        | CheckStatus.Failed -> "failed"
        | CheckStatus.Unavailable -> "unavailable"

    let tryParse text =
        [ CheckStatus.Passed; CheckStatus.Failed; CheckStatus.Unavailable ] |> List.tryFind (fun status -> code status = text)

type ValidationClaim =
    { Name: string
      Status: CheckStatus
      EvidenceIds: string list }

type PacketEvidence =
    { Id: string
      Digest: string
      Revision: string }

type CompletionPacket =
    { WorkItemId: string
      ClaimToken: string
      FencingGeneration: int64
      Commit: string
      VerificationRevision: string
      OrdoContractVersion: string
      OrdoContractDigest: string
      RequirementIds: string list
      DecisionIds: string list
      ConflictResolutionIds: string list
      Validations: ValidationClaim list
      Evidence: PacketEvidence list }

type ObservedEvidence =
    { Id: string
      Digest: string
      Revision: string
      Status: CheckStatus
      Reusable: bool }

type CompletionAuthority =
    { WorkItemId: string
      ClaimToken: string
      FencingGeneration: int64
      ExpectedCommit: string
      ExpectedVerificationRevision: string
      SupportedOrdoContractVersions: string list
      ExpectedOrdoContractDigest: string
      RequiredRequirementIds: string list
      RequiredDecisionIds: string list
      RequiredConflictResolutionIds: string list
      RequiredValidationNames: string list
      Evidence: ObservedEvidence list
      ConsumedPacketDigests: string list }

/// Praxis's own minimal record of the authoritative completion inputs
/// (I3): the admitted Ordo contract identity and the required sets. Read
/// from a caller-supplied JSON file until Ordo admission is wired in.
type CompletionAuthorityInput =
    { OrdoContractVersion: string
      OrdoContractDigest: string
      SupportedOrdoContractVersions: string list
      RequiredRequirementIds: string list
      RequiredDecisionIds: string list
      RequiredConflictResolutionIds: string list
      RequiredValidationNames: string list
      RequiredEvidenceIds: string list
      ExpectedCommit: string option
      ExpectedVerificationRevision: string option }

type EvidenceSubmission =
    { Id: string
      Path: string
      ClaimedDigest: string option }

/// The agent's completion claim, as presented on the command line.
type CompletionRequest =
    { WorkItemId: string
      Fence: ClaimFence
      Commit: string
      VerificationRevision: string
      OrdoContractVersion: string option
      OrdoContractDigest: string option
      RequirementIds: string list
      DecisionIds: string list
      ConflictResolutionIds: string list
      Validations: ValidationClaim list
      Evidence: EvidenceSubmission list
      TelemetryExecutions: string list }

/// Praxis's independent observation of one evidence file.
[<RequireQualifiedAccess>]
type EvidenceObservation =
    | Observed of digest: string
    | Missing
    | Unreadable of reason: string

type ReceiptDiagnostic =
    { Code: string
      Subject: string
      Detail: string }

type DokimosReceipt =
    { Contract: string
      SchemaVersion: string
      DokimosVersion: string
      Verdict: string
      ExitCode: int
      WorkItemId: string
      Commit: string
      VerificationRevision: string
      PacketDigest: string
      RequirementCount: int
      EvidenceDigests: string list
      ReusedEvidenceIds: string list
      Diagnostics: ReceiptDiagnostic list }

/// What running the independent verifier process produced.
[<RequireQualifiedAccess>]
type VerifierRun =
    | Unavailable of reason: string
    | TimedOut of after: TimeSpan
    | Crashed of reason: string
    | Exited of exitCode: int * receipt: Result<DokimosReceipt, string> * receiptDigest: string * diagnostics: string

type CompletionFinding = { Code: string; Detail: string }

type VerifierIdentity = { Name: string; Version: string }

[<RequireQualifiedAccess>]
type CompletionVerdict =
    | Verified of receipt: DokimosReceipt * receiptDigest: string
    | Rejected of findings: CompletionFinding list * verifier: VerifierIdentity option

[<RequireQualifiedAccess>]
module CompletionGate =
    let PacketSchemaVersion = "praxis.completion-packet/1"
    let AuthoritySchemaVersion = "dokimos.completion-authority/1"
    let ReceiptContract = "dokimos.completion-verification"
    let ReceiptSchemaVersion = "dokimos.completion-verification/1"

    let private finding code detail = { Code = code; Detail = detail }

    let private isHex length (value: string) =
        not (isNull value) && value.Length = length && value |> Seq.forall Uri.IsHexDigit

    /// A full, immutable 40- or 64-hex Git object ID.
    let isImmutableRevision (value: string) = isHex 40 value || isHex 64 value

    let isSha256Digest (value: string) =
        not (isNull value) && value.StartsWith("sha256:", StringComparison.Ordinal) && isHex 64 (value.Substring 7)

    let sha256 (bytes: byte array) =
        "sha256:" + Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    let private frame (value: string) =
        let text = if isNull value then "" else value
        string (Encoding.UTF8.GetByteCount text) + ":" + text

    let private framedList (values: string list) =
        let ordered = List.sortWith (fun (left: string) right -> String.CompareOrdinal(left, right)) values
        frame (string ordered.Length) + (ordered |> List.map frame |> String.concat "")

    /// Byte-for-byte the canonical digest Dokimos computes for a packet, so
    /// Praxis recognises a replay before and independently of Dokimos.
    let packetDigest (packet: CompletionPacket) =
        let ordinal (left: string) (right: string) = String.CompareOrdinal(left, right)

        let validations =
            packet.Validations
            |> List.sortWith (fun left right ->
                match ordinal left.Name right.Name with
                | 0 ->
                    match ordinal (CheckStatus.code left.Status) (CheckStatus.code right.Status) with
                    | 0 -> compare (List.sortWith ordinal left.EvidenceIds) (List.sortWith ordinal right.EvidenceIds)
                    | other -> other
                | other -> other)
            |> List.map (fun validation -> frame validation.Name + frame (CheckStatus.code validation.Status) + framedList validation.EvidenceIds)

        let evidence =
            packet.Evidence
            |> List.sortWith (fun left right ->
                match ordinal left.Id right.Id with
                | 0 ->
                    match ordinal left.Digest right.Digest with
                    | 0 -> ordinal left.Revision right.Revision
                    | other -> other
                | other -> other)
            |> List.map (fun item -> frame item.Id + frame item.Digest + frame item.Revision)

        [ frame PacketSchemaVersion
          frame packet.WorkItemId
          frame packet.ClaimToken
          frame (string packet.FencingGeneration)
          frame packet.Commit
          frame packet.VerificationRevision
          frame packet.OrdoContractVersion
          frame packet.OrdoContractDigest
          framedList packet.RequirementIds
          framedList packet.DecisionIds
          framedList packet.ConflictResolutionIds
          frame (string validations.Length)
          String.concat "" validations
          frame (string evidence.Length)
          String.concat "" evidence ]
        |> String.concat ""
        |> Encoding.UTF8.GetBytes
        |> sha256

    /// Checks Praxis enforces before any claim mutation or verifier call:
    /// immutable revisions and present, readable mandatory evidence.
    let preflight (input: CompletionAuthorityInput) (request: CompletionRequest) (observations: Map<string, EvidenceObservation>) =
        let submitted = request.Evidence |> List.map _.Id |> Set.ofList

        [ if not (isImmutableRevision request.Commit) then
              yield finding "PRX.COMPLETION.INVALID_COMMIT" $"commit '{request.Commit}' must be a full 40- or 64-hex Git object ID"
          if not (isImmutableRevision request.VerificationRevision) then
              yield
                  finding
                      "PRX.COMPLETION.INVALID_VERIFICATION_REVISION"
                      $"verification revision '{request.VerificationRevision}' must be a full 40- or 64-hex Git object ID"
          for id in input.RequiredEvidenceIds do
              if not (submitted.Contains id) then
                  yield finding "PRX.COMPLETION.EVIDENCE_MISSING" $"required evidence '{id}' was not supplied (--evidence {id}=PATH)"
          for evidence in request.Evidence do
              match observations |> Map.tryFind evidence.Id with
              | Some(EvidenceObservation.Observed _) -> ()
              | Some(EvidenceObservation.Unreadable reason) ->
                  yield finding "PRX.COMPLETION.EVIDENCE_UNREADABLE" $"evidence '{evidence.Id}' at '{evidence.Path}' cannot be read: {reason}"
              | Some EvidenceObservation.Missing
              | None -> yield finding "PRX.COMPLETION.EVIDENCE_MISSING" $"evidence '{evidence.Id}' at '{evidence.Path}' does not exist" ]

    let private observedDigest (observations: Map<string, EvidenceObservation>) id =
        match observations |> Map.tryFind id with
        | Some(EvidenceObservation.Observed digest) -> digest
        | _ -> ""

    /// The untrusted packet: the agent's claim, with Praxis-computed digests
    /// only where the agent claimed none.
    let packet (input: CompletionAuthorityInput) (request: CompletionRequest) (observations: Map<string, EvidenceObservation>) =
        { WorkItemId = request.WorkItemId
          ClaimToken = request.Fence.ClaimToken
          FencingGeneration = request.Fence.Generation
          Commit = request.Commit
          VerificationRevision = request.VerificationRevision
          OrdoContractVersion = request.OrdoContractVersion |> Option.defaultValue input.OrdoContractVersion
          OrdoContractDigest = request.OrdoContractDigest |> Option.defaultValue input.OrdoContractDigest
          RequirementIds = request.RequirementIds
          DecisionIds = request.DecisionIds
          ConflictResolutionIds = request.ConflictResolutionIds
          Validations = request.Validations
          Evidence =
            request.Evidence
            |> List.map (fun evidence ->
                { PacketEvidence.Id = evidence.Id
                  Digest = evidence.ClaimedDigest |> Option.defaultValue (observedDigest observations evidence.Id)
                  Revision = request.VerificationRevision }) }

    /// The authority comes only from Praxis-held state: the current claim's
    /// token and generation, the expected commit, the required sets, the
    /// digests Praxis computed itself, and the packets already consumed.
    let authority
        (input: CompletionAuthorityInput)
        (claim: WorkClaim)
        (expectedCommit: string)
        (expectedRevision: string)
        (observations: Map<string, EvidenceObservation>)
        (submissions: EvidenceSubmission list)
        =
        { WorkItemId = claim.WorkItemId
          ClaimToken = claim.Holder |> Option.map _.ClaimToken |> Option.defaultValue ""
          FencingGeneration = claim.Generation
          ExpectedCommit = expectedCommit
          ExpectedVerificationRevision = expectedRevision
          SupportedOrdoContractVersions = input.SupportedOrdoContractVersions
          ExpectedOrdoContractDigest = input.OrdoContractDigest
          RequiredRequirementIds = input.RequiredRequirementIds
          RequiredDecisionIds = input.RequiredDecisionIds
          RequiredConflictResolutionIds = input.RequiredConflictResolutionIds
          RequiredValidationNames = input.RequiredValidationNames
          Evidence =
            submissions
            |> List.choose (fun evidence ->
                match observations |> Map.tryFind evidence.Id with
                | Some(EvidenceObservation.Observed digest) ->
                    Some
                        { ObservedEvidence.Id = evidence.Id
                          Digest = digest
                          Revision = expectedRevision
                          Status = CheckStatus.Passed
                          Reusable = false }
                | _ -> None)
            |> List.distinctBy _.Id
          ConsumedPacketDigests = claim.ConsumedPacketDigests }

    /// Fail closed: only an exit-0 receipt bound to exactly this packet is
    /// verification. Anything else is a refusal with actionable codes.
    let judge (packet: CompletionPacket) (digest: string) (run: VerifierRun) =
        let rejected findings verifier = CompletionVerdict.Rejected(findings, verifier)
        let identity (receipt: DokimosReceipt) = Some { Name = "dokimos"; Version = receipt.DokimosVersion }

        match run with
        | VerifierRun.Unavailable reason -> rejected [ finding "PRX.COMPLETION.VERIFIER_UNAVAILABLE" reason ] None
        | VerifierRun.TimedOut after ->
            rejected [ finding "PRX.COMPLETION.VERIFIER_TIMEOUT" $"Dokimos did not finish within {after.TotalSeconds:F0}s" ] None
        | VerifierRun.Crashed reason -> rejected [ finding "PRX.COMPLETION.VERIFIER_FAILED" reason ] None
        | VerifierRun.Exited(0, Ok receipt, receiptDigest, _) ->
            let mismatches =
                [ "Contract", ReceiptContract, receipt.Contract
                  "SchemaVersion", ReceiptSchemaVersion, receipt.SchemaVersion
                  "Verdict", "verified", receipt.Verdict
                  "ExitCode", "0", string receipt.ExitCode
                  "WorkItemId", packet.WorkItemId, receipt.WorkItemId
                  "Commit", packet.Commit, receipt.Commit
                  "VerificationRevision", packet.VerificationRevision, receipt.VerificationRevision
                  "PacketDigest", digest, receipt.PacketDigest
                  "Diagnostics", "0", string receipt.Diagnostics.Length ]
                |> List.filter (fun (_, expected, actual) -> expected <> actual)
                |> List.map (fun (field, expected, actual) ->
                    finding "PRX.COMPLETION.RECEIPT_MISMATCH" $"receipt {field} is '{actual}', expected '{expected}'")

            if mismatches.IsEmpty then CompletionVerdict.Verified(receipt, receiptDigest) else rejected mismatches (identity receipt)
        | VerifierRun.Exited(4, Ok receipt, _, _) when receipt.Verdict = "rejected" && not receipt.Diagnostics.IsEmpty ->
            let codes =
                receipt.Diagnostics
                |> List.map (fun diagnostic -> finding diagnostic.Code $"{diagnostic.Subject}: {diagnostic.Detail}")

            rejected (finding "PRX.COMPLETION.VERIFICATION_REJECTED" "Dokimos refused the completion" :: codes) (identity receipt)
        | VerifierRun.Exited((0 | 4), receipt, _, diagnostics) ->
            let reason =
                match receipt with
                | Error problem -> problem
                | Ok _ -> "receipt verdict does not agree with the exit code"

            rejected [ finding "PRX.COMPLETION.RECEIPT_UNPARSABLE" ($"{reason} {diagnostics}".Trim()) ] None
        | VerifierRun.Exited(3, _, _, diagnostics) ->
            rejected [ finding "PRX.COMPLETION.VERIFIER_INPUT_REJECTED" $"Dokimos reported missing or malformed evidence: {diagnostics}" ] None
        | VerifierRun.Exited(code, _, _, diagnostics) ->
            rejected [ finding "PRX.COMPLETION.VERIFIER_FAILED" $"Dokimos exited {code}: {diagnostics}" ] None

    let codes (findings: CompletionFinding list) = findings |> List.map _.Code |> List.distinct

/// The I1 work events a claim or completion outcome produces.
[<RequireQualifiedAccess>]
type ClaimEventPayload =
    | ClaimAcquired of claimant: string * claimToken: string * generation: int64 * leaseExpiresAt: DateTimeOffset * revision: int64 * recovered: bool * previousClaimant: string option
    | ClaimRejected of claimant: string * reason: string * currentGeneration: int64
    | ClaimRenewed of claimant: string * claimToken: string * generation: int64 * leaseExpiresAt: DateTimeOffset
    | ClaimReleased of claimant: string * claimToken: string * generation: int64
    | CompletionRejected of claimant: string * claimToken: string * generation: int64 * codes: string list * packetDigest: string option * verifier: VerifierIdentity option
    | CompletionVerified of claimant: string * claimToken: string * generation: int64 * completion: VerifiedCompletion
    | CompletionReplayed of claimToken: string * generation: int64 * packetDigest: string

type ClaimEvent =
    { WorkItemId: string
      OccurredAt: DateTimeOffset
      Payload: ClaimEventPayload }

[<RequireQualifiedAccess>]
module ClaimEvent =
    let eventType event =
        match event.Payload with
        | ClaimEventPayload.ClaimAcquired _ -> "work.claim.acquired"
        | ClaimEventPayload.ClaimRejected _ -> "work.claim.rejected"
        | ClaimEventPayload.ClaimRenewed _ -> "work.claim.renewed"
        | ClaimEventPayload.ClaimReleased _ -> "work.claim.released"
        | ClaimEventPayload.CompletionRejected _ -> "work.completion.rejected"
        | ClaimEventPayload.CompletionVerified _ -> "work.completion.verified"
        | ClaimEventPayload.CompletionReplayed _ -> "work.completion.replayed"

    /// The event a written or replayed claim transition stands for. A
    /// replay reproduces the original event exactly (same time, same
    /// content), so an idempotent sink never records it twice.
    let ofEffect (effect: ClaimEffect) (claim: WorkClaim) =
        let holder = claim.Holder.Value
        let at = claim.UpdatedAt

        match effect with
        | ClaimEffect.Acquired recovered ->
            Some
                { WorkItemId = claim.WorkItemId
                  OccurredAt = holder.AcquiredAt
                  Payload =
                    ClaimEventPayload.ClaimAcquired(
                        holder.Claimant, holder.ClaimToken, claim.Generation, holder.LeaseExpiresAt, claim.Revision, recovered, claim.PreviousClaimant
                    ) }
        | ClaimEffect.Renewed ->
            Some
                { WorkItemId = claim.WorkItemId
                  OccurredAt = at
                  Payload = ClaimEventPayload.ClaimRenewed(holder.Claimant, holder.ClaimToken, claim.Generation, holder.LeaseExpiresAt) }
        | ClaimEffect.Released ->
            Some
                { WorkItemId = claim.WorkItemId
                  OccurredAt = at
                  Payload = ClaimEventPayload.ClaimReleased(holder.Claimant, holder.ClaimToken, claim.Generation) }
        | ClaimEffect.MarkedExpired -> None
