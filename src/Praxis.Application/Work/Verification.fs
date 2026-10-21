namespace Praxis.Application.Work

open System
open Praxis.Domain.Work

/// I5 `echelon.validation-policy/1`: one validation the verifier runs.
type PolicyValidation =
    { Name: string
      EvidenceId: string
      Command: string list
      Timeout: TimeSpan }

/// I5: a path whose bytes at the candidate commit are pinned by SHA-256.
type ProtectedPath = { Path: string; Sha256: string }

/// I5 validation policy. Host-owned: it is read from the host, never from
/// the candidate tree; `Digest` is `sha256:` over the exact file bytes.
type ValidationPolicy =
    { PolicyId: string
      Digest: string
      Validations: PolicyValidation list
      ProtectedPaths: ProtectedPath list }

/// F1: what the agent client asks the verifier. It never carries the claim
/// token; the verifier re-reads the claim itself.
type VerifyRequest =
    { RepoPath: string
      WorkItemId: string
      Claimant: string
      FencingGeneration: int64
      Commit: string
      /// The claim store the verifier re-reads: `None` is the repository's
      /// local file store, `Some remote` the Git-ref store on that remote.
      Remote: string option }

/// One signed receipt and the combined output of the run it attests.
type SignedReceipt =
    { Envelope: ReceiptEnvelope
      Payload: ReceiptPayload
      Output: string }

[<RequireQualifiedAccess>]
type VerifierResponse =
    | Signed of receipts: SignedReceipt list * diagnostics: string list
    | Refused of code: string * detail: string

/// Where the agent client stored one receipt (I8 `receipts/`).
type StoredReceipt =
    { Path: string
      Digest: string
      Payload: ReceiptPayload
      /// The combined output of the run, as stored beside the receipt
      /// (`.output.txt`, I8). Read for failure excerpts, never trusted.
      Output: string }

type WorkVerifyPorts =
    { Claims: WorkClaimStore
      Now: unit -> DateTimeOffset
      RepoPath: string
      /// The claim store `Claims` reads, passed on so the verifier reads the same one.
      Remote: string option
      ObserveHead: unit -> Result<string, string>
      /// `git status --porcelain` entries; any entry is a dirty worktree.
      WorktreeStatus: unit -> Result<string list, string>
      /// The trusted verifier (over its Unix domain socket).
      Request: VerifyRequest -> Result<VerifierResponse, string>
      Store: SignedReceipt -> Result<StoredReceipt, string> }

[<RequireQualifiedAccess>]
type WorkVerifyOutcome =
    | Verified of request: VerifyRequest * receipts: StoredReceipt list * diagnostics: string list
    | Refused of CompletionFinding
    | Failed of reason: string

[<RequireQualifiedAccess>]
module VerifierEngine =
    let StaleClaimCode = "PRX.VERIFIER.STALE_CLAIM"
    let InvalidRequestCode = "PRX.VERIFIER.INVALID_REQUEST"
    let CommitUnavailableCode = "PRX.VERIFIER.COMMIT_UNAVAILABLE"
    let UnavailableCode = "PRX.VERIFIER.UNAVAILABLE"
    let ResponseMismatchCode = "PRX.VERIFIER.RESPONSE_MISMATCH"
    let ProtectedPathCode = "PRX.VERIFIER.PROTECTED_PATH"

    let private stamp (value: DateTimeOffset) = VerificationReceipt.stamp value

    /// Step 1 of the engine: the stated holder and generation must be the
    /// claim's current, unexpired holder; anything else is refused before the
    /// verifier clones or runs anything.
    let admit (now: DateTimeOffset) (request: VerifyRequest) (current: WorkClaim option) : Result<WorkClaim, string * string> =
        if not (WorkItemId.isValid request.WorkItemId) then
            Error(InvalidRequestCode, $"'{request.WorkItemId}' is not a work-item id")
        elif not (CompletionGate.isImmutableRevision request.Commit) then
            Error(InvalidRequestCode, $"commit '{request.Commit}' must be a full 40- or 64-hex Git object ID")
        else
            match current with
            | None -> Error(StaleClaimCode, $"'{request.WorkItemId}' has no claim")
            | Some claim when claim.Generation <> request.FencingGeneration ->
                Error(StaleClaimCode, $"fencing generation {request.FencingGeneration} is stale (current {claim.Generation})")
            | Some claim when claim.Holder |> Option.forall (fun holder -> holder.Claimant <> request.Claimant) ->
                Error(StaleClaimCode, $"'{request.Claimant}' is not the holder of generation {claim.Generation}")
            | Some claim when not (ClaimStatus.isActive claim.Status) ->
                Error(StaleClaimCode, $"the claim is {ClaimStatus.code claim.Status}, not held")
            | Some claim when not (WorkClaim.leaseValid now claim) ->
                Error(StaleClaimCode, $"the lease expired at {stamp claim.Holder.Value.LeaseExpiresAt}")
            | Some claim -> Ok claim

    /// Step 3, the protected-path rule (I5): every pinned path must exist at
    /// the candidate commit with exactly its pinned digest. `observe` returns
    /// the lowercase hex SHA-256 of a regular file's bytes, or `None`.
    let protectedPathFindings (policy: ValidationPolicy) (observe: string -> string option) =
        policy.ProtectedPaths
        |> List.choose (fun pinned ->
            match observe pinned.Path with
            | Some digest when String.Equals(digest, pinned.Sha256, StringComparison.OrdinalIgnoreCase) -> None
            | Some digest -> Some $"protected path '{pinned.Path}' has sha256 {digest}, pinned {pinned.Sha256}"
            | None -> Some $"protected path '{pinned.Path}' is missing or not a regular file at the candidate commit")

/// `praxis work verify` (F1 agent client): fence-checks the caller from the
/// claim store and its token, refuses a dirty worktree, asks the trusted
/// verifier for receipts at HEAD and stores them out of tree (I8).
[<RequireQualifiedAccess>]
module WorkVerification =
    let private refused code detail = WorkVerifyOutcome.Refused { Code = code; Detail = detail }

    /// The fence the token stands for: the current holder's claimant and generation.
    let fenceOf (token: string) (claim: WorkClaim) =
        claim.Holder
        |> Option.filter (fun holder -> holder.ClaimToken = token)
        |> Option.map (fun holder -> { Claimant = holder.Claimant; ClaimToken = token; Generation = claim.Generation })

    /// `work verify` passed: at least one receipt, and every receipt passed.
    let allPassed (receipts: StoredReceipt list) =
        not receipts.IsEmpty && receipts |> List.forall (fun receipt -> receipt.Payload.Status = CheckStatus.Passed)

    let private binds (request: VerifyRequest) (payload: ReceiptPayload) =
        payload.WorkItemId = request.WorkItemId
        && payload.Claimant = request.Claimant
        && payload.FencingGeneration = request.FencingGeneration
        && payload.Commit = request.Commit

    let run (ports: WorkVerifyPorts) (workItemId: string) (token: string) =
        match ports.Claims.Read workItemId with
        | Error message -> WorkVerifyOutcome.Failed message
        | Ok current ->
            let fence =
                current
                |> Option.bind (fenceOf token)
                |> Option.defaultValue { Claimant = ""; ClaimToken = token; Generation = current |> Option.map _.Generation |> Option.defaultValue 0L }

            match WorkClaim.fence (ports.Now()) fence current with
            | Error conflict -> refused (ClaimConflict.code conflict) (ClaimConflict.detail conflict)
            | Ok _ ->
                match ports.ObserveHead(), ports.WorktreeStatus() with
                | Error message, _
                | _, Error message -> WorkVerifyOutcome.Failed message
                | Ok _, Ok((_ :: _) as dirty) ->
                    let shown = dirty |> List.truncate 10 |> String.concat ", "
                    refused ValidationEvidence.DirtyWorktreeCode $"the worktree is not clean ({shown}); commit or discard changes so the receipts name the revision you will complete"
                | Ok head, Ok [] ->
                    let request =
                        { RepoPath = ports.RepoPath
                          WorkItemId = workItemId
                          Claimant = fence.Claimant
                          FencingGeneration = fence.Generation
                          Commit = head
                          Remote = ports.Remote }

                    match ports.Request request with
                    | Error message -> refused VerifierEngine.UnavailableCode message
                    | Ok(VerifierResponse.Refused(code, detail)) -> refused code detail
                    | Ok(VerifierResponse.Signed(receipts, diagnostics)) ->
                        match receipts |> List.tryFind (fun receipt -> not (binds request receipt.Payload)) with
                        | Some foreign ->
                            refused
                                VerifierEngine.ResponseMismatchCode
                                $"the verifier returned a receipt for {foreign.Payload.WorkItemId} generation {foreign.Payload.FencingGeneration} at {foreign.Payload.Commit}, not this request"
                        | None ->
                            receipts
                            |> List.fold (fun state receipt -> state |> Result.bind (fun stored -> ports.Store receipt |> Result.map (fun item -> stored @ [ item ]))) (Ok [])
                            |> function
                                | Ok stored -> WorkVerifyOutcome.Verified(request, stored, diagnostics)
                                | Error message -> WorkVerifyOutcome.Failed $"receipts were signed but could not be stored: {message}"
