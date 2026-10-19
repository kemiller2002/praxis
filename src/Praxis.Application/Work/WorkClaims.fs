namespace Praxis.Application.Work

open System
open Praxis.Domain.Work

/// Result of a compare-and-swap on one claim record.
[<RequireQualifiedAccess>]
type ClaimWrite =
    | Written of WorkClaim
    /// Another writer got there first; `current` is what it left.
    | Conflict of current: WorkClaim option

/// The claim storage port (PRX-REL-002). `CompareAndSwap expected next`
/// writes `next` only if the stored record's revision is still `expected`
/// (`None`: no record may exist yet). Implementations: in-memory, local
/// file (single host) and Git ref (remote agents).
type WorkClaimStore =
    { Read: string -> Result<WorkClaim option, string>
      CompareAndSwap: int64 option -> WorkClaim -> Result<ClaimWrite, string>
      List: unit -> Result<WorkClaim list, string> }

type WorkClaimPorts =
    { Store: WorkClaimStore
      Now: unit -> DateTimeOffset
      Claimability: string -> Claimability
      /// Appends one I1 event. Must be idempotent: an identical event is
      /// recorded once however often it is emitted.
      Emit: ClaimEvent -> Result<unit, string> }

/// Runs the independent verifier as a separate process (I2).
type CompletionVerifier = CompletionPacket -> CompletionAuthority -> VerifierRun

type CompletionPorts =
    { Claims: WorkClaimPorts
      ObserveEvidence: EvidenceSubmission -> EvidenceObservation
      /// The commit Praxis itself observes as current (e.g. Git HEAD).
      ObserveHead: unit -> Result<string, string>
      Verifier: CompletionVerifier }

[<RequireQualifiedAccess>]
type ClaimOutcome =
    | Succeeded of claim: WorkClaim * replayed: bool
    | Refused of conflict: ClaimConflict * current: WorkClaim option
    | Failed of reason: string

[<RequireQualifiedAccess>]
type CompletionOutcome =
    | Completed of WorkClaim * VerifiedCompletion
    | Replayed of WorkClaim * VerifiedCompletion
    | Rejected of findings: CompletionFinding list * current: WorkClaim option
    | Failed of reason: string

[<RequireQualifiedAccess>]
module WorkClaims =
    /// Bounded retries after losing a compare-and-swap race: each retry
    /// re-reads and re-decides, so a loser ends with a structured conflict.
    let private maxAttempts = 8

    let private emitAll (ports: WorkClaimPorts) events =
        events
        |> List.fold (fun result event -> result |> Result.bind (fun () -> ports.Emit event)) (Ok())

    let private rejectionEvent (ports: WorkClaimPorts) workItemId claimant conflict (current: WorkClaim option) =
        match conflict with
        | ClaimConflict.InvalidRequest _ -> []
        | _ ->
            [ { WorkItemId = workItemId
                OccurredAt = ports.Now()
                Payload =
                  ClaimEventPayload.ClaimRejected(
                      claimant,
                      ClaimConflict.reason conflict,
                      current |> Option.map _.Generation |> Option.defaultValue 0L
                  ) } ]

    let private finish (ports: WorkClaimPorts) events outcome =
        match emitAll ports events with
        | Ok() -> outcome
        | Error message -> ClaimOutcome.Failed $"the claim state is recorded but its event was not appended ({message}); retry the same command to re-emit it"

    /// Decide-then-CAS loop shared by every claim transition.
    let private transact (ports: WorkClaimPorts) workItemId claimant (decide: DateTimeOffset -> WorkClaim option -> ClaimDecision) =
        let rec attempt remaining =
            match ports.Store.Read workItemId with
            | Error message -> ClaimOutcome.Failed message
            | Ok current ->
                match decide (ports.Now()) current with
                | ClaimDecision.Refuse conflict ->
                    finish ports (rejectionEvent ports workItemId claimant conflict current) (ClaimOutcome.Refused(conflict, current))
                | ClaimDecision.Replay(claim, effect) ->
                    finish ports (ClaimEvent.ofEffect effect claim |> Option.toList) (ClaimOutcome.Succeeded(claim, true))
                | ClaimDecision.Write(expected, next, effect) ->
                    match ports.Store.CompareAndSwap expected next with
                    | Error message -> ClaimOutcome.Failed message
                    | Ok(ClaimWrite.Written written) ->
                        finish ports (ClaimEvent.ofEffect effect written |> Option.toList) (ClaimOutcome.Succeeded(written, false))
                    | Ok(ClaimWrite.Conflict _) when remaining > 1 -> attempt (remaining - 1)
                    | Ok(ClaimWrite.Conflict latest) ->
                        let conflict = ClaimConflict.Lost(latest |> Option.map _.Revision |> Option.defaultValue 0L)
                        finish ports (rejectionEvent ports workItemId claimant conflict latest) (ClaimOutcome.Refused(conflict, latest))

        attempt maxAttempts

    let acquire (ports: WorkClaimPorts) (request: ClaimAcquisition) =
        let claimability = ports.Claimability request.WorkItemId
        transact ports request.WorkItemId request.Claimant (fun now current -> WorkClaim.acquire now claimability request current)

    /// `work recover`: acquire only an expired (or durably expired) claim.
    let recover (ports: WorkClaimPorts) (request: ClaimAcquisition) = acquire ports { request with RecoverOnly = true }

    /// PRX-REL-011: every slice of a cohesive batch is claimed on its own;
    /// one slice's conflict never changes another slice's ownership.
    let acquireEach (ports: WorkClaimPorts) (requests: ClaimAcquisition list) =
        requests |> List.map (fun request -> request.WorkItemId, acquire ports request)

    let renew (ports: WorkClaimPorts) workItemId (fence: ClaimFence) lease markRunning =
        transact ports workItemId fence.Claimant (fun now current -> WorkClaim.renew now fence lease markRunning current)

    let release (ports: WorkClaimPorts) workItemId (fence: ClaimFence) failed =
        transact ports workItemId fence.Claimant (fun now current -> WorkClaim.release now fence failed current)

    let expire (ports: WorkClaimPorts) workItemId =
        transact ports workItemId "" (fun now current -> WorkClaim.expire now current)

    let inspect (ports: WorkClaimPorts) (workItemId: string option) =
        match workItemId with
        | Some id -> ports.Store.Read id |> Result.map Option.toList
        | None -> ports.Store.List()

    // ---- verified completion (PRX-REL-006/007) -----------------------------

    let private completionRejected (ports: CompletionPorts) (request: CompletionRequest) findings digest verifier current =
        let event =
            { WorkItemId = request.WorkItemId
              OccurredAt = ports.Claims.Now()
              Payload =
                ClaimEventPayload.CompletionRejected(
                    request.Fence.Claimant,
                    request.Fence.ClaimToken,
                    request.Fence.Generation,
                    CompletionGate.codes findings,
                    digest,
                    verifier
                ) }

        match ports.Claims.Emit event with
        | Ok() -> CompletionOutcome.Rejected(findings, current)
        | Error message ->
            let codes = CompletionGate.codes findings |> String.concat ", "
            CompletionOutcome.Failed $"completion was refused ({codes}) and its event was not appended: {message}"

    let private conflictFinding conflict =
        { Code = ClaimConflict.code conflict; Detail = ClaimConflict.detail conflict }

    let private verifiedEvent (claim: WorkClaim) (completion: VerifiedCompletion) =
        let holder = claim.Holder.Value

        { WorkItemId = claim.WorkItemId
          OccurredAt = completion.CompletedAt
          Payload = ClaimEventPayload.CompletionVerified(holder.Claimant, holder.ClaimToken, claim.Generation, completion) }

    /// Idempotent retry: re-emit the original verified event (a no-op when
    /// it is already recorded) and record the replay.
    let private replayed (ports: CompletionPorts) (claim: WorkClaim) (completion: VerifiedCompletion) =
        let replay =
            { WorkItemId = claim.WorkItemId
              OccurredAt = ports.Claims.Now()
              Payload = ClaimEventPayload.CompletionReplayed(claim.Holder.Value.ClaimToken, claim.Generation, completion.PacketDigest) }

        match emitAll ports.Claims [ verifiedEvent claim completion; replay ] with
        | Ok() -> CompletionOutcome.Replayed(claim, completion)
        | Error message -> CompletionOutcome.Failed $"completion is recorded but its replay event was not appended: {message}"

    let private observeAll (ports: CompletionPorts) (request: CompletionRequest) =
        request.Evidence
        |> List.map (fun evidence -> evidence.Id, ports.ObserveEvidence evidence)
        |> Map.ofList

    let private expectedRevisions (ports: CompletionPorts) (input: CompletionAuthorityInput) =
        match input.ExpectedCommit, input.ExpectedVerificationRevision with
        | Some commit, Some revision -> Ok(commit, revision)
        | commit, revision ->
            ports.ObserveHead()
            |> Result.map (fun head -> commit |> Option.defaultValue head, revision |> Option.defaultValue head)

    /// The completion gate. Succeeds only when the caller holds the current
    /// token, generation and lease; the commit and verification revision are
    /// immutable; mandatory evidence exists; Dokimos returns a verified
    /// receipt for exactly this packet; and the final transition wins a CAS
    /// keyed to the same generation. Everything else fails closed.
    let completeVerified (ports: CompletionPorts) (input: CompletionAuthorityInput) (request: CompletionRequest) =
        let observations = observeAll ports request
        let packet = CompletionGate.packet input request observations
        let digest = CompletionGate.packetDigest packet
        let reject findings digest verifier current = completionRejected ports request findings digest verifier current

        match ports.Claims.Store.Read request.WorkItemId with
        | Error message -> CompletionOutcome.Failed message
        | Ok current ->
            match ClaimCompletion.start (ports.Claims.Now()) request.Fence digest current with
            | CompletionStart.AlreadyCompleted(claim, completion) -> replayed ports claim completion
            | CompletionStart.Refused conflict -> reject [ conflictFinding conflict ] (Some digest) None current
            | CompletionStart.Proceed(claim, next) ->
                match CompletionGate.preflight input request observations with
                | (_ :: _) as findings -> reject findings (Some digest) None current
                | [] ->
                    match expectedRevisions ports input with
                    | Error message ->
                        reject [ { Code = "PRX.COMPLETION.AUTHORITY_UNAVAILABLE"; Detail = message } ] (Some digest) None current
                    | Ok(expectedCommit, expectedRevision) ->
                        let pending =
                            match next with
                            | None -> Ok(Some claim)
                            | Some next ->
                                match ports.Claims.Store.CompareAndSwap (Some claim.Revision) next with
                                | Error message -> Error message
                                | Ok(ClaimWrite.Written written) -> Ok(Some written)
                                | Ok(ClaimWrite.Conflict _) -> Ok None

                        match pending with
                        | Error message -> CompletionOutcome.Failed message
                        | Ok None ->
                            let latest = ports.Claims.Store.Read request.WorkItemId |> Result.defaultValue current
                            reject [ conflictFinding (ClaimConflict.Lost claim.Revision) ] (Some digest) None latest
                        | Ok(Some awaiting) ->
                            let authority = CompletionGate.authority input awaiting expectedCommit expectedRevision observations request.Evidence

                            match CompletionGate.judge packet digest (ports.Verifier packet authority) with
                            | CompletionVerdict.Rejected(findings, verifier) ->
                                let reopened =
                                    ClaimCompletion.reopen (ports.Claims.Now()) digest awaiting
                                    |> Option.map (fun reopened ->
                                        match ports.Claims.Store.CompareAndSwap (Some awaiting.Revision) reopened with
                                        | Ok(ClaimWrite.Written written) -> Some written
                                        | _ -> Some awaiting)
                                    |> Option.flatten

                                reject findings (Some digest) verifier reopened
                            | CompletionVerdict.Verified(receipt, receiptDigest) ->
                                let completion =
                                    { PacketDigest = digest
                                      ReceiptDigest = receiptDigest
                                      Commit = packet.Commit
                                      VerificationRevision = packet.VerificationRevision
                                      RequirementIds = packet.RequirementIds |> List.distinct |> List.sort
                                      TelemetryExecutions = request.TelemetryExecutions
                                      VerifierName = "dokimos"
                                      VerifierVersion = receipt.DokimosVersion
                                      CompletedAt = ports.Claims.Now() }

                                let verifier = Some { Name = "dokimos"; Version = receipt.DokimosVersion }

                                match ClaimCompletion.finish completion.CompletedAt request.Fence completion (Some awaiting) with
                                | Error conflict -> reject [ conflictFinding conflict ] (Some digest) verifier (Some awaiting)
                                | Ok completed ->
                                    match ports.Claims.Store.CompareAndSwap (Some awaiting.Revision) completed with
                                    | Error message -> CompletionOutcome.Failed message
                                    | Ok(ClaimWrite.Written written) ->
                                        match ports.Claims.Emit(verifiedEvent written completion) with
                                        | Ok() -> CompletionOutcome.Completed(written, completion)
                                        | Error message ->
                                            CompletionOutcome.Failed
                                                $"completion is recorded but its event was not appended ({message}); retry the same command to re-emit it"
                                    | Ok(ClaimWrite.Conflict latest) ->
                                        match latest with
                                        | Some({ Status = ClaimStatus.Completed; Completion = Some prior } as winner) when prior.PacketDigest = digest ->
                                            replayed ports winner prior
                                        | _ ->
                                            reject
                                                [ { Code = "PRX.COMPLETION.CONFLICT"
                                                    Detail = "the claim changed while Dokimos verified; refresh the claim and replan" } ]
                                                (Some digest)
                                                verifier
                                                latest
