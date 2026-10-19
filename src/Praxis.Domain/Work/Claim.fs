namespace Praxis.Domain.Work

open System

/// Durable ownership state of one work item (PRX-REL-001, PRX-REL-004).
/// `Unclaimed` is also the state of an item with no claim record at all.
[<RequireQualifiedAccess>]
type ClaimStatus =
    | Unclaimed
    | Claimed
    | Running
    | AwaitingVerification
    | Completed
    | Failed
    | Expired

[<RequireQualifiedAccess>]
module ClaimStatus =
    let code status =
        match status with
        | ClaimStatus.Unclaimed -> "unclaimed"
        | ClaimStatus.Claimed -> "claimed"
        | ClaimStatus.Running -> "running"
        | ClaimStatus.AwaitingVerification -> "awaiting-verification"
        | ClaimStatus.Completed -> "completed"
        | ClaimStatus.Failed -> "failed"
        | ClaimStatus.Expired -> "expired"

    let all =
        [ ClaimStatus.Unclaimed; ClaimStatus.Claimed; ClaimStatus.Running; ClaimStatus.AwaitingVerification
          ClaimStatus.Completed; ClaimStatus.Failed; ClaimStatus.Expired ]

    let tryParse (text: string) = all |> List.tryFind (fun status -> code status = text)

    /// A status in which a holder owns the item and its lease matters.
    let isActive status =
        match status with
        | ClaimStatus.Claimed
        | ClaimStatus.Running
        | ClaimStatus.AwaitingVerification -> true
        | _ -> false

/// The most recent owner of a claim. It stays on the record after release,
/// expiry or completion so retries and audits can be answered.
type ClaimHolder =
    { Claimant: string
      ClaimToken: string
      AcquiredAt: DateTimeOffset
      LeaseExpiresAt: DateTimeOffset }

/// What a successful, Dokimos-verified completion bound (PRX-REL-006).
type VerifiedCompletion =
    { PacketDigest: string
      ReceiptDigest: string
      Commit: string
      VerificationRevision: string
      RequirementIds: string list
      TelemetryExecutions: string list
      VerifierName: string
      VerifierVersion: string
      CompletedAt: DateTimeOffset }

/// One work item's claim record. `Revision` increases by exactly one on
/// every durable write and is the compare-and-swap key; `Generation` is the
/// fencing token and increases by one on every new acquisition.
type WorkClaim =
    { WorkItemId: string
      Revision: int64
      Generation: int64
      Status: ClaimStatus
      Holder: ClaimHolder option
      PreviousClaimant: string option
      PendingPacketDigest: string option
      ConsumedPacketDigests: string list
      Completion: VerifiedCompletion option
      UpdatedAt: DateTimeOffset }

/// What a holder presents on every fenced operation.
type ClaimFence =
    { Claimant: string
      ClaimToken: string
      Generation: int64 }

type ClaimAcquisition =
    { WorkItemId: string
      Claimant: string
      ClaimToken: string
      LeaseDuration: TimeSpan
      ExpectedRevision: int64 option
      /// `work recover`: only an expired or explicitly expired claim may be taken.
      RecoverOnly: bool }

/// Whether the work item may be claimed at all, as observed from the queue.
[<RequireQualifiedAccess>]
type Claimability =
    | Claimable
    | NotClaimable of reason: string

/// A structured conflict: the caller must refresh and replan, never
/// overwrite (PRX-REL-003). Codes are stable `PRX.CLAIM.*` strings.
[<RequireQualifiedAccess>]
type ClaimConflict =
    | Held of claimant: string * leaseExpiresAt: DateTimeOffset
    | StaleRevision of expected: int64 * actual: int64
    | StaleGeneration of presented: int64 * current: int64
    | StaleToken
    | Expired of at: DateTimeOffset
    | NotClaimable of reason: string
    | NotHeld of status: ClaimStatus
    | Lost of current: int64
    | InvalidRequest of reason: string

[<RequireQualifiedAccess>]
module ClaimConflict =
    let code conflict =
        match conflict with
        | ClaimConflict.Held _ -> "PRX.CLAIM.HELD"
        | ClaimConflict.StaleRevision _ -> "PRX.CLAIM.STALE_REVISION"
        | ClaimConflict.StaleGeneration _ -> "PRX.CLAIM.STALE_GENERATION"
        | ClaimConflict.StaleToken -> "PRX.CLAIM.STALE_TOKEN"
        | ClaimConflict.Expired _ -> "PRX.CLAIM.EXPIRED"
        | ClaimConflict.NotClaimable _ -> "PRX.CLAIM.NOT_CLAIMABLE"
        | ClaimConflict.NotHeld _ -> "PRX.CLAIM.NOT_HELD"
        | ClaimConflict.Lost _ -> "PRX.CLAIM.CONFLICT"
        | ClaimConflict.InvalidRequest _ -> "PRX.CLAIM.INVALID_REQUEST"

    /// The I1 `work.claim.rejected` reason vocabulary.
    let reason conflict =
        match conflict with
        | ClaimConflict.Held _ -> "held"
        | ClaimConflict.StaleRevision _
        | ClaimConflict.Lost _ -> "stale-revision"
        | ClaimConflict.StaleGeneration _
        | ClaimConflict.StaleToken -> "stale-generation"
        | ClaimConflict.Expired _ -> "expired"
        | ClaimConflict.NotClaimable _
        | ClaimConflict.NotHeld _
        | ClaimConflict.InvalidRequest _ -> "not-claimable"

    let private stamp (value: DateTimeOffset) = value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

    let detail conflict =
        match conflict with
        | ClaimConflict.Held(claimant, expires) ->
            $"held by '{claimant}' until {stamp expires}; wait for release or expiry, then refresh and replan"
        | ClaimConflict.StaleRevision(expected, actual) ->
            $"expected revision {expected} but the claim is at revision {actual}; refresh the claim and replan"
        | ClaimConflict.StaleGeneration(presented, current) ->
            $"fencing generation {presented} is stale (current {current}); this owner lost the claim, refresh and replan"
        | ClaimConflict.StaleToken -> "claim token does not match the current holder; refresh the claim and replan"
        | ClaimConflict.Expired at -> $"lease expired at {stamp at}; the owner must reacquire (or another claimant recovers)"
        | ClaimConflict.NotClaimable reason -> reason
        | ClaimConflict.NotHeld status -> $"no active claim (status {ClaimStatus.code status}); claim the item first"
        | ClaimConflict.Lost current -> $"another writer changed the claim first (now revision {current}); refresh and replan"
        | ClaimConflict.InvalidRequest reason -> reason

/// The observable effect of a decided transition; it becomes an I1 event.
[<RequireQualifiedAccess>]
type ClaimEffect =
    | Acquired of recovered: bool
    | Renewed
    | Released
    | MarkedExpired

/// A pure decision. `Write` is persisted by compare-and-swap against the
/// current record's revision (`None` = the record must not exist yet).
[<RequireQualifiedAccess>]
type ClaimDecision =
    | Write of expected: int64 option * next: WorkClaim * effect: ClaimEffect
    | Replay of current: WorkClaim * effect: ClaimEffect
    | Refuse of ClaimConflict

[<RequireQualifiedAccess>]
module WorkClaim =
    let leaseValid (now: DateTimeOffset) (claim: WorkClaim) =
        claim.Holder |> Option.exists (fun holder -> now < holder.LeaseExpiresAt)

    /// The status an observer should act on: an active claim whose lease
    /// passed is expired even before anyone records that durably.
    let effectiveStatus (now: DateTimeOffset) (claim: WorkClaim) =
        if ClaimStatus.isActive claim.Status && not (leaseValid now claim) then ClaimStatus.Expired else claim.Status

    let private holds (fence: ClaimFence) (claim: WorkClaim) =
        claim.Generation = fence.Generation
        && claim.Holder
           |> Option.exists (fun holder -> holder.ClaimToken = fence.ClaimToken && holder.Claimant = fence.Claimant)

    let private bump (now: DateTimeOffset) (claim: WorkClaim) =
        { claim with Revision = claim.Revision + 1L; UpdatedAt = now }

    let private validAcquisition (request: ClaimAcquisition) =
        if not (WorkItemId.isValid request.WorkItemId) then Some $"'{request.WorkItemId}' is not a work-item id"
        elif String.IsNullOrWhiteSpace request.Claimant then Some "a claimant is required"
        elif String.IsNullOrWhiteSpace request.ClaimToken then Some "a claim token is required"
        elif request.LeaseDuration <= TimeSpan.Zero then Some "the lease duration must be positive"
        else None

    let private granted (now: DateTimeOffset) (request: ClaimAcquisition) (current: WorkClaim option) recovered =
        let previous = current |> Option.bind _.Holder |> Option.map _.Claimant

        let next =
            { WorkClaim.WorkItemId = request.WorkItemId
              Revision = (current |> Option.map _.Revision |> Option.defaultValue 0L) + 1L
              Generation = (current |> Option.map _.Generation |> Option.defaultValue 0L) + 1L
              Status = ClaimStatus.Claimed
              Holder =
                Some
                    { ClaimHolder.Claimant = request.Claimant
                      ClaimToken = request.ClaimToken
                      AcquiredAt = now
                      LeaseExpiresAt = now + request.LeaseDuration }
              PreviousClaimant = if recovered then previous else None
              PendingPacketDigest = None
              ConsumedPacketDigests = current |> Option.map _.ConsumedPacketDigests |> Option.defaultValue []
              Completion = None
              UpdatedAt = now }

        ClaimDecision.Write(current |> Option.map _.Revision, next, ClaimEffect.Acquired recovered)

    /// PRX-REL-001/002/005: at most one active holder per generation. The
    /// same claimant re-sending the same token gets the original claim back.
    let acquire (now: DateTimeOffset) (claimability: Claimability) (request: ClaimAcquisition) (current: WorkClaim option) =
        match validAcquisition request, current with
        | Some reason, _ -> ClaimDecision.Refuse(ClaimConflict.InvalidRequest reason)
        | None, Some claim when
            ClaimStatus.isActive claim.Status
            && leaseValid now claim
            && claim.Holder |> Option.exists (fun holder -> holder.ClaimToken = request.ClaimToken && holder.Claimant = request.Claimant)
            ->
            ClaimDecision.Replay(claim, ClaimEffect.Acquired(claim.PreviousClaimant.IsSome))
        | None, Some claim when claim.Holder |> Option.exists (fun holder -> holder.ClaimToken = request.ClaimToken) ->
            ClaimDecision.Refuse(ClaimConflict.InvalidRequest "claim tokens are single-use; generate a new token")
        | None, _ ->
            let actual = current |> Option.map _.Revision |> Option.defaultValue 0L

            match request.ExpectedRevision, claimability, current with
            | Some expected, _, _ when expected <> actual -> ClaimDecision.Refuse(ClaimConflict.StaleRevision(expected, actual))
            | _, Claimability.NotClaimable reason, _ -> ClaimDecision.Refuse(ClaimConflict.NotClaimable reason)
            | _, _, None when request.RecoverOnly ->
                ClaimDecision.Refuse(ClaimConflict.NotClaimable "nothing to recover: the item has no claim; use 'work claim'")
            | _, _, None -> granted now request None false
            | _, _, Some claim ->
                match claim.Status with
                | ClaimStatus.Completed -> ClaimDecision.Refuse(ClaimConflict.NotClaimable "the item's claim is already completed")
                | status when ClaimStatus.isActive status && leaseValid now claim ->
                    let holder = claim.Holder.Value
                    ClaimDecision.Refuse(ClaimConflict.Held(holder.Claimant, holder.LeaseExpiresAt))
                | status when ClaimStatus.isActive status || status = ClaimStatus.Expired -> granted now request current true
                | _ when request.RecoverOnly ->
                    ClaimDecision.Refuse(ClaimConflict.NotClaimable "nothing to recover: the claim is not expired; use 'work claim'")
                | _ -> granted now request current false

    /// The fencing check every holder operation passes first (PRX-REL-003/005).
    let fence (now: DateTimeOffset) (presented: ClaimFence) (current: WorkClaim option) =
        match current with
        | None -> Error(ClaimConflict.NotHeld ClaimStatus.Unclaimed)
        | Some claim when presented.Generation <> claim.Generation ->
            Error(ClaimConflict.StaleGeneration(presented.Generation, claim.Generation))
        | Some claim when not (holds presented claim) -> Error ClaimConflict.StaleToken
        | Some claim when not (ClaimStatus.isActive claim.Status) -> Error(ClaimConflict.NotHeld claim.Status)
        | Some claim when not (leaseValid now claim) -> Error(ClaimConflict.Expired claim.Holder.Value.LeaseExpiresAt)
        | Some claim -> Ok claim

    let renew (now: DateTimeOffset) (presented: ClaimFence) (lease: TimeSpan) (markRunning: bool) (current: WorkClaim option) =
        if lease <= TimeSpan.Zero then
            ClaimDecision.Refuse(ClaimConflict.InvalidRequest "the lease duration must be positive")
        else
            match fence now presented current with
            | Error conflict -> ClaimDecision.Refuse conflict
            | Ok claim ->
                let status = if markRunning && claim.Status = ClaimStatus.Claimed then ClaimStatus.Running else claim.Status
                let holder = { claim.Holder.Value with LeaseExpiresAt = now + lease }
                ClaimDecision.Write(Some claim.Revision, { bump now claim with Status = status; Holder = Some holder }, ClaimEffect.Renewed)

    /// Idempotent: releasing an already released (or failed) claim with the
    /// same credentials returns the original release.
    let release (now: DateTimeOffset) (presented: ClaimFence) (failed: bool) (current: WorkClaim option) =
        let target = if failed then ClaimStatus.Failed else ClaimStatus.Unclaimed

        match current with
        | Some claim when claim.Status = target && holds presented claim -> ClaimDecision.Replay(claim, ClaimEffect.Released)
        | _ ->
            match fence now presented current with
            | Error conflict -> ClaimDecision.Refuse conflict
            | Ok claim ->
                ClaimDecision.Write(Some claim.Revision, { bump now claim with Status = target; PendingPacketDigest = None }, ClaimEffect.Released)

    /// Records an expired lease durably. The generation is unchanged; the
    /// next acquisition takes generation + 1.
    let expire (now: DateTimeOffset) (current: WorkClaim option) =
        match current with
        | Some claim when claim.Status = ClaimStatus.Expired -> ClaimDecision.Replay(claim, ClaimEffect.MarkedExpired)
        | Some claim when ClaimStatus.isActive claim.Status && leaseValid now claim ->
            let holder = claim.Holder.Value
            ClaimDecision.Refuse(ClaimConflict.Held(holder.Claimant, holder.LeaseExpiresAt))
        | Some claim when ClaimStatus.isActive claim.Status ->
            ClaimDecision.Write(Some claim.Revision, { bump now claim with Status = ClaimStatus.Expired }, ClaimEffect.MarkedExpired)
        | Some claim -> ClaimDecision.Refuse(ClaimConflict.NotHeld claim.Status)
        | None -> ClaimDecision.Refuse(ClaimConflict.NotHeld ClaimStatus.Unclaimed)

/// How a verified-completion attempt starts against the current claim.
[<RequireQualifiedAccess>]
type CompletionStart =
    /// The same holder already completed with this exact packet.
    | AlreadyCompleted of WorkClaim * VerifiedCompletion
    /// Persist `next` (when given) by CAS, then run the verifier.
    | Proceed of current: WorkClaim * next: WorkClaim option
    | Refused of ClaimConflict

[<RequireQualifiedAccess>]
module ClaimCompletion =
    /// PRX-REL-005/007: only the current, unexpired holder may start
    /// verification; the record moves to `awaiting-verification` first so a
    /// crash between Dokimos and the final write is recoverable.
    let start (now: DateTimeOffset) (presented: ClaimFence) (packetDigest: string) (current: WorkClaim option) =
        match current with
        | Some({ Status = ClaimStatus.Completed; Completion = Some completion } as claim) when
            completion.PacketDigest = packetDigest
            && claim.Generation = presented.Generation
            && claim.Holder |> Option.exists (fun holder -> holder.ClaimToken = presented.ClaimToken)
            ->
            CompletionStart.AlreadyCompleted(claim, completion)
        | Some({ Status = ClaimStatus.Completed } as claim) when claim.Generation <> presented.Generation ->
            CompletionStart.Refused(ClaimConflict.StaleGeneration(presented.Generation, claim.Generation))
        | Some { Status = ClaimStatus.Completed } ->
            CompletionStart.Refused(ClaimConflict.NotClaimable "the item was already completed with a different packet")
        | _ ->
            match WorkClaim.fence now presented current with
            | Error conflict -> CompletionStart.Refused conflict
            | Ok claim when claim.Status = ClaimStatus.AwaitingVerification && claim.PendingPacketDigest = Some packetDigest ->
                CompletionStart.Proceed(claim, None)
            | Ok claim ->
                let next =
                    { claim with
                        Revision = claim.Revision + 1L
                        UpdatedAt = now
                        Status = ClaimStatus.AwaitingVerification
                        PendingPacketDigest = Some packetDigest }

                CompletionStart.Proceed(claim, Some next)

    /// The final transition, keyed to the same generation and pending packet.
    let finish (now: DateTimeOffset) (presented: ClaimFence) (completion: VerifiedCompletion) (current: WorkClaim option) =
        match WorkClaim.fence now presented current with
        | Error conflict -> Error conflict
        | Ok claim when claim.Status <> ClaimStatus.AwaitingVerification || claim.PendingPacketDigest <> Some completion.PacketDigest ->
            Error(ClaimConflict.Lost claim.Revision)
        | Ok claim ->
            Ok
                { claim with
                    Revision = claim.Revision + 1L
                    UpdatedAt = now
                    Status = ClaimStatus.Completed
                    PendingPacketDigest = None
                    ConsumedPacketDigests = claim.ConsumedPacketDigests @ [ completion.PacketDigest ] |> List.distinct
                    Completion = Some completion }

    /// After a refusal the holder keeps the claim and may fix and resubmit.
    let reopen (now: DateTimeOffset) (packetDigest: string) (current: WorkClaim) =
        if current.Status = ClaimStatus.AwaitingVerification && current.PendingPacketDigest = Some packetDigest then
            Some
                { current with
                    Revision = current.Revision + 1L
                    UpdatedAt = now
                    Status = ClaimStatus.Running
                    PendingPacketDigest = None }
        else
            None
