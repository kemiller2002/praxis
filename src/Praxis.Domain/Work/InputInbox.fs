namespace Praxis.Domain.Work

open System
open System.Security.Cryptography
open System.Text
open Praxis.Domain.Provenance

/// What processing discovered in an input document (requirements item 9 of
/// PRAXIS-DUAL-ENTRY-RECONCILIATION, DER-09). Classification is advisory:
/// the kind is the processor's judgement, recorded with the derivation.
[<RequireQualifiedAccess>]
type DerivationKind =
    | Requirement
    | Decision
    | Constraint
    | Evidence
    | Risk
    | OpenQuestion
    | Reference

[<RequireQualifiedAccess>]
module DerivationKind =
    let all =
        [ DerivationKind.Requirement
          DerivationKind.Decision
          DerivationKind.Constraint
          DerivationKind.Evidence
          DerivationKind.Risk
          DerivationKind.OpenQuestion
          DerivationKind.Reference ]

    let code kind =
        match kind with
        | DerivationKind.Requirement -> "requirement"
        | DerivationKind.Decision -> "decision"
        | DerivationKind.Constraint -> "constraint"
        | DerivationKind.Evidence -> "evidence"
        | DerivationKind.Risk -> "risk"
        | DerivationKind.OpenQuestion -> "question"
        | DerivationKind.Reference -> "reference"

    let tryParse (value: string) = all |> List.tryFind (fun kind -> code kind = value)

/// Where the derived canonical change lives: a canonical artifact by ID,
/// or any other repository file by path.
[<RequireQualifiedAccess>]
type DerivationTarget =
    | Artifact of id: string
    | RepositoryPath of path: string

[<RequireQualifiedAccess>]
module DerivationTarget =
    let value target =
        match target with
        | DerivationTarget.Artifact id -> id
        | DerivationTarget.RepositoryPath path -> path

type Derivation =
    { Kind: DerivationKind
      Target: DerivationTarget
      Summary: string
      /// Where in the input the derivation came from (a heading, line range
      /// or quotation), when the processor states it.
      Locator: string option
      RecordedAt: string
      Actor: Actor
      ExecutionId: string option }

/// The original input's identity. The bytes are never modified; the digest
/// is how every later step proves it still holds the same input.
type InputSource =
    { /// Repository-relative path the input had in the inbox.
      Path: string
      FileName: string
      Sha256: string
      Size: int64 }

/// `Claiming`, `Releasing`: an operation recorded before its file transfer
/// (write-ahead), finished by recovery after a crash. `Completed` and
/// `Rejected` are terminal.
[<RequireQualifiedAccess>]
type InputClaimState =
    | Claiming
    | Claimed
    | Releasing
    | Completed
    | Rejected

[<RequireQualifiedAccess>]
module InputClaimState =
    let code state =
        match state with
        | InputClaimState.Claiming -> "claiming"
        | InputClaimState.Claimed -> "claimed"
        | InputClaimState.Releasing -> "releasing"
        | InputClaimState.Completed -> "completed"
        | InputClaimState.Rejected -> "rejected"

    let tryParse (value: string) =
        [ InputClaimState.Claiming
          InputClaimState.Claimed
          InputClaimState.Releasing
          InputClaimState.Completed
          InputClaimState.Rejected ]
        |> List.tryFind (fun state -> code state = value)

/// Where a claim directory lives: `.praxis/processing`, `.praxis/processed`
/// or `.praxis/rejected/documents`.
[<RequireQualifiedAccess>]
type ClaimLocation =
    | Processing
    | Processed
    | Rejected

[<RequireQualifiedAccess>]
module ClaimLocation =
    let code location =
        match location with
        | ClaimLocation.Processing -> "processing"
        | ClaimLocation.Processed -> "processed"
        | ClaimLocation.Rejected -> "rejected"

    /// The location a claim in this state belongs in once settled.
    let settled state =
        match state with
        | InputClaimState.Completed -> ClaimLocation.Processed
        | InputClaimState.Rejected -> ClaimLocation.Rejected
        | InputClaimState.Claiming
        | InputClaimState.Claimed
        | InputClaimState.Releasing -> ClaimLocation.Processing

type InputClaim =
    { ClaimId: string
      Source: InputSource
      State: InputClaimState
      ClaimedAt: string
      Claimant: Actor
      ExecutionId: string option
      Derivations: Derivation list
      /// Completion with nothing derived must say why (an input can hold
      /// nothing actionable); never implied.
      NoDerivationReason: string option
      ClosedAt: string option
      ReleaseReason: string option
      RejectionReason: string option }

/// The state of one derived change, observed from the working tree, Git
/// and the canonical artifact index.
type DerivationObservation =
    { Derivation: Derivation
      Exists: bool
      /// Tracked in HEAD with no uncommitted change: durably reconciled.
      Committed: bool
      /// For an artifact: whether its `derived_from` names this claim.
      /// `None` for a plain repository path, which cannot carry lineage.
      LineageNamesClaim: bool option }

/// The digests a file transfer observes at its source and target, when
/// each exists. `Expected` is the input's recorded digest.
type TransferObservation =
    { Expected: string
      SourceDigest: string option
      TargetDigest: string option }

/// The next idempotent step of a transfer that copies then removes, so the
/// input exists at all times in at least one complete, verified copy.
[<RequireQualifiedAccess>]
type TransferStep =
    | CopyThenRemoveSource
    | RemoveSource
    | Finished
    | Unsafe of reason: string

[<RequireQualifiedAccess>]
type InputInboxError =
    | NotPending of path: string
    | UnknownClaim of claimId: string
    | WrongState of claimId: string * state: InputClaimState * operation: string
    | InvalidRequest of reason: string
    | NotReconciled of problems: string list
    | TransferUnsafe of claimId: string * reason: string
    | ReleaseTargetOccupied of path: string
    | Storage of message: string

[<RequireQualifiedAccess>]
module InputInboxError =
    let code error =
        match error with
        | InputInboxError.NotPending _ -> "input-not-pending"
        | InputInboxError.UnknownClaim _ -> "unknown-claim"
        | InputInboxError.WrongState _ -> "illegal-claim-state"
        | InputInboxError.InvalidRequest _ -> "invalid-request"
        | InputInboxError.NotReconciled _ -> "derivations-not-reconciled"
        | InputInboxError.TransferUnsafe _ -> "transfer-unsafe"
        | InputInboxError.ReleaseTargetOccupied _ -> "release-target-occupied"
        | InputInboxError.Storage _ -> "storage-failure"

    let message error =
        match error with
        | InputInboxError.NotPending path -> $"'{path}' is not a pending input in .praxis/inbox/documents or input-documents"
        | InputInboxError.UnknownClaim claimId -> $"no claim '{claimId}' exists"
        | InputInboxError.WrongState(claimId, state, operation) ->
            $"claim {claimId} is {InputClaimState.code state}; {operation} needs a claimed input"
        | InputInboxError.InvalidRequest reason -> reason
        | InputInboxError.NotReconciled problems ->
            "the input stays unprocessed until every derived change is durably reconciled: " + String.concat "; " problems
        | InputInboxError.TransferUnsafe(claimId, reason) -> $"claim {claimId}: {reason}; nothing further was moved"
        | InputInboxError.ReleaseTargetOccupied path -> $"'{path}' already holds a different file; the claim was not released"
        | InputInboxError.Storage message -> message

    /// Exit code: 2 refused input or rules, 1 storage that may be retried.
    let exitCode error =
        match error with
        | InputInboxError.Storage _
        | InputInboxError.TransferUnsafe _ -> 1
        | _ -> 2

/// The pure rules of the input-document lifecycle (DER-09, DER-10):
/// claim -> derive* -> complete, or release / reject, every step
/// write-ahead and idempotent so a crash at any point is recoverable
/// without losing or duplicating the input.
[<RequireQualifiedAccess>]
module InputInbox =
    let private hex (bytes: byte array) = Convert.ToHexString(bytes).ToLowerInvariant()

    /// Deterministic from the inbox path and content, so a repeated claim of
    /// the same input finds the same claim instead of creating another.
    let claimId (relativePath: string) (sha256: string) =
        let digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{relativePath.Replace('\\', '/')}\n{sha256}")) |> hex
        "INPUT-" + digest.Substring(0, 16)

    let isClaimId (value: string) =
        value.Length = 22
        && value.StartsWith("INPUT-", StringComparison.Ordinal)
        && value.Substring(6) |> Seq.forall (fun character -> Char.IsAsciiHexDigitLower character || Char.IsAsciiDigit character)

    let nextTransferStep (observation: TransferObservation) : TransferStep =
        let matches digest = digest = Some observation.Expected

        match observation.SourceDigest, observation.TargetDigest with
        | _, Some _ when not (matches observation.TargetDigest) ->
            TransferStep.Unsafe "the target holds a different file than the recorded input"
        | Some _, Some _ when not (matches observation.SourceDigest) ->
            TransferStep.Unsafe "the source changed since it was claimed"
        | Some _, Some _ -> TransferStep.RemoveSource
        | None, Some _ -> TransferStep.Finished
        | Some _, None when matches observation.SourceDigest -> TransferStep.CopyThenRemoveSource
        | Some _, None -> TransferStep.Unsafe "the source changed since it was claimed"
        | None, None -> TransferStep.Unsafe "the recorded input exists at neither the source nor the target"

    let create (source: InputSource) (claimedAt: string) (claimant: Actor) (execution: string option) =
        { ClaimId = claimId source.Path source.Sha256
          Source = source
          State = InputClaimState.Claiming
          ClaimedAt = claimedAt
          Claimant = claimant
          ExecutionId = execution
          Derivations = []
          NoDerivationReason = None
          ClosedAt = None
          ReleaseReason = None
          RejectionReason = None }

    let private requireClaimed operation (claim: InputClaim) =
        if claim.State = InputClaimState.Claimed then Ok claim
        else Error(InputInboxError.WrongState(claim.ClaimId, claim.State, operation))

    let private sameTarget (left: Derivation) (right: Derivation) = left.Kind = right.Kind && left.Target = right.Target

    /// Records a derivation. Repeating the same kind and target is
    /// idempotent: the first record stands and nothing changes.
    let derive (derivation: Derivation) (claim: InputClaim) : Result<InputClaim * bool, InputInboxError> =
        requireClaimed "derive" claim
        |> Result.bind (fun claim ->
            let target = DerivationTarget.value derivation.Target

            if String.IsNullOrWhiteSpace derivation.Summary then
                Error(InputInboxError.InvalidRequest "a derivation needs a non-empty --summary")
            elif String.IsNullOrWhiteSpace target || target.Contains ".." || target.StartsWith "/" then
                Error(InputInboxError.InvalidRequest $"'{target}' is not a repository-relative path or artifact ID")
            elif claim.Derivations |> List.exists (sameTarget derivation) then
                Ok(claim, false)
            else
                Ok({ claim with Derivations = claim.Derivations @ [ derivation ] }, true))

    /// Every reason the input may not leave processing yet. Completion is
    /// refused while any derived change is missing, uncommitted, or (for an
    /// artifact) does not name the input in its lineage.
    let completionProblems (claim: InputClaim) (observations: DerivationObservation list) (noDerivationReason: string option) =
        [ if claim.Derivations.IsEmpty && (noDerivationReason |> Option.forall String.IsNullOrWhiteSpace) then
              yield "nothing was derived; record derivations or complete with --no-derivations REASON"
          if not claim.Derivations.IsEmpty && noDerivationReason.IsSome then
              yield "--no-derivations contradicts the recorded derivations"
          for observation in observations do
              let target = DerivationTarget.value observation.Derivation.Target

              if not observation.Exists then
                  yield $"{target} does not exist"
              elif not observation.Committed then
                  yield $"{target} is not committed in HEAD (or has uncommitted changes)"

              if observation.LineageNamesClaim = Some false then
                  yield $"{target} does not name {claim.ClaimId} in derived_from (praxis provenance record --id {target} --operation created --derived-from {claim.ClaimId})" ]

    let complete
        (closedAt: string)
        (noDerivationReason: string option)
        (observations: DerivationObservation list)
        (claim: InputClaim)
        : Result<InputClaim, InputInboxError> =
        requireClaimed "complete" claim
        |> Result.bind (fun claim ->
            match completionProblems claim observations noDerivationReason with
            | [] ->
                Ok
                    { claim with
                        State = InputClaimState.Completed
                        ClosedAt = Some closedAt
                        NoDerivationReason = noDerivationReason |> Option.filter (String.IsNullOrWhiteSpace >> not) }
            | problems -> Error(InputInboxError.NotReconciled problems))

    let release (at: string) (reason: string) (claim: InputClaim) =
        requireClaimed "release" claim
        |> Result.bind (fun claim ->
            if String.IsNullOrWhiteSpace reason then
                Error(InputInboxError.InvalidRequest "release needs a non-empty --reason")
            else
                Ok { claim with State = InputClaimState.Releasing; ClosedAt = Some at; ReleaseReason = Some reason })

    let reject (at: string) (reason: string) (claim: InputClaim) =
        match claim.State with
        | InputClaimState.Claimed when String.IsNullOrWhiteSpace reason ->
            Error(InputInboxError.InvalidRequest "reject needs a non-empty --reason")
        | InputClaimState.Claimed -> Ok { claim with State = InputClaimState.Rejected; ClosedAt = Some at; RejectionReason = Some reason }
        | state -> Error(InputInboxError.WrongState(claim.ClaimId, state, "reject"))

    /// What recovery must do with a claim found at `location`, so an
    /// interrupted operation is finished, never repeated or abandoned.
    [<RequireQualifiedAccess>]
    type Recovery =
        | FinishClaimTransfer
        | FinishRelease
        | Settle of ClaimLocation
        | Consistent

    let recovery (location: ClaimLocation) (claim: InputClaim) =
        match claim.State with
        | InputClaimState.Claiming -> Recovery.FinishClaimTransfer
        | InputClaimState.Releasing -> Recovery.FinishRelease
        | state when ClaimLocation.settled state <> location -> Recovery.Settle(ClaimLocation.settled state)
        | _ -> Recovery.Consistent
