namespace Praxis.Application.Work

open Praxis.Domain.Provenance
open Praxis.Domain.Work

/// The effects the input-document lifecycle needs. Every write is atomic at
/// file or directory granularity; the operations below order them so a
/// crash between any two leaves a state `recover` can finish.
type InputInboxPort =
    { Now: unit -> string
      /// The pending input at this repository-relative path, digested, if
      /// it is a file directly inside an inbox directory.
      ReadPending: string -> Result<InputSource option, string>
      ListPending: unit -> Result<InputSource list, string>
      /// Every claim record, with the location of its directory.
      ListClaims: unit -> Result<(ClaimLocation * InputClaim) list, string>
      /// Writes `claim.json` atomically, creating the claim directory.
      WriteClaim: ClaimLocation -> InputClaim -> Result<unit, string>
      /// Renames a claim directory from one location to another.
      MoveClaim: ClaimLocation -> ClaimLocation -> string -> Result<unit, string>
      /// Deletes a claim directory (only after its input is safe elsewhere).
      RemoveClaim: ClaimLocation -> string -> Result<unit, string>
      /// Digest of the input at its inbox path, if a file is there.
      InboxDigest: InputSource -> Result<string option, string>
      /// Digest of the claim directory's copy of the input, if present.
      ClaimedCopyDigest: ClaimLocation -> InputClaim -> Result<string option, string>
      /// Copies the inbox file into the claim directory (temporary file,
      /// then rename), so the copy is either absent or complete.
      CopyIntoClaim: InputClaim -> Result<unit, string>
      /// Copies the claim's copy back to the inbox path, the same way.
      CopyBackToInbox: InputClaim -> Result<unit, string>
      RemoveInboxSource: InputClaim -> Result<unit, string>
      /// The state of every derivation of a claim (exists, committed,
      /// lineage), read from the working tree, Git and the artifact index.
      ObserveDerivations: InputClaim -> Result<DerivationObservation list, string> }

type InboxListing =
    { Pending: InputSource list
      Claims: (ClaimLocation * InputClaim) list }

type RecoveredClaim = { ClaimId: string; Action: string }

[<RequireQualifiedAccess>]
module InputInboxOperations =
    let private storage result = result |> Result.mapError InputInboxError.Storage

    let private sequence (results: Result<'a, InputInboxError> list) =
        List.foldBack (fun item state -> Result.bind (fun items -> item |> Result.map (fun value -> value :: items)) state) results (Ok [])

    let private transfer (claim: InputClaim) (observe: unit -> Result<TransferObservation, string>) copy removeSource =
        observe ()
        |> storage
        |> Result.bind (fun observation ->
            match InputInbox.nextTransferStep observation with
            | TransferStep.CopyThenRemoveSource -> copy claim |> Result.bind (fun () -> removeSource claim) |> storage
            | TransferStep.RemoveSource -> removeSource claim |> storage
            | TransferStep.Finished -> Ok()
            | TransferStep.Unsafe reason -> Error(InputInboxError.TransferUnsafe(claim.ClaimId, reason)))

    let private observation (claim: InputClaim) (source: Result<string option, string>) (target: Result<string option, string>) =
        match source, target with
        | Ok source, Ok target -> Ok { Expected = claim.Source.Sha256; SourceDigest = source; TargetDigest = target }
        | Error message, _
        | _, Error message -> Error message

    /// Inbox -> processing, then the record says `claimed`.
    let private finishClaim (port: InputInboxPort) (claim: InputClaim) =
        transfer
            claim
            (fun () -> observation claim (port.InboxDigest claim.Source) (port.ClaimedCopyDigest ClaimLocation.Processing claim))
            port.CopyIntoClaim
            port.RemoveInboxSource
        |> Result.bind (fun () ->
            let claimed = { claim with State = InputClaimState.Claimed }
            port.WriteClaim ClaimLocation.Processing claimed |> storage |> Result.map (fun () -> claimed))

    /// Processing -> inbox, then the claim directory goes. A different file
    /// already at the inbox path stops the release with nothing moved.
    let private finishRelease (port: InputInboxPort) (claim: InputClaim) =
        port.InboxDigest claim.Source
        |> storage
        |> Result.bind (fun atInbox ->
            match atInbox with
            | Some digest when digest <> claim.Source.Sha256 -> Error(InputInboxError.ReleaseTargetOccupied claim.Source.Path)
            | _ ->
                transfer
                    claim
                    (fun () -> observation claim (port.ClaimedCopyDigest ClaimLocation.Processing claim) (Ok atInbox))
                    port.CopyBackToInbox
                    (fun claim -> port.RemoveClaim ClaimLocation.Processing claim.ClaimId)
                |> Result.bind (fun () ->
                    // A transfer already finished leaves only the record.
                    port.RemoveClaim ClaimLocation.Processing claim.ClaimId |> storage))

    let private recoverOne (port: InputInboxPort) (location: ClaimLocation, claim: InputClaim) =
        match InputInbox.recovery location claim with
        | InputInbox.Recovery.Consistent -> Ok None
        | InputInbox.Recovery.FinishClaimTransfer ->
            finishClaim port claim |> Result.map (fun _ -> Some { ClaimId = claim.ClaimId; Action = "finished claim" })
        | InputInbox.Recovery.FinishRelease ->
            finishRelease port claim |> Result.map (fun () -> Some { ClaimId = claim.ClaimId; Action = "finished release" })
        | InputInbox.Recovery.Settle target ->
            port.MoveClaim location target claim.ClaimId
            |> storage
            |> Result.map (fun () -> Some { ClaimId = claim.ClaimId; Action = $"moved to {ClaimLocation.code target}" })

    /// Finishes every interrupted operation. Safe to run at any time and
    /// repeatedly; every mutating command runs it first.
    let recover (port: InputInboxPort) : Result<RecoveredClaim list, InputInboxError> =
        port.ListClaims ()
        |> storage
        |> Result.bind (fun claims -> claims |> List.map (recoverOne port) |> sequence)
        |> Result.map (List.choose id)

    let private find (port: InputInboxPort) (claimId: string) =
        port.ListClaims ()
        |> storage
        |> Result.bind (fun claims ->
            match claims |> List.tryFind (fun (_, claim) -> claim.ClaimId = claimId) with
            | Some found -> Ok found
            | None -> Error(InputInboxError.UnknownClaim claimId))

    let private inProcessing (port: InputInboxPort) claimId =
        recover port |> Result.bind (fun _ -> find port claimId) |> Result.map snd

    let list (port: InputInboxPort) : Result<InboxListing, InputInboxError> =
        match port.ListPending (), port.ListClaims () with
        | Ok pending, Ok claims -> Ok { Pending = pending; Claims = claims }
        | Error message, _
        | _, Error message -> Error(InputInboxError.Storage message)

    let show (port: InputInboxPort) (claimId: string) = find port claimId

    /// Claims a pending input: the record is written before anything moves
    /// (write-ahead), then the input is copied and the inbox file removed.
    /// Claiming an input that is already claimed returns that claim.
    let claim (port: InputInboxPort) (path: string) (claimant: Actor) (execution: string option) : Result<InputClaim * bool, InputInboxError> =
        recover port
        |> Result.bind (fun _ -> port.ReadPending path |> storage)
        |> Result.bind (fun pending ->
            port.ListClaims ()
            |> storage
            |> Result.bind (fun claims ->
                let normalized = path.Replace('\\', '/').TrimStart('.', '/')

                let existing =
                    claims
                    |> List.tryFind (fun (_, claim) ->
                        match pending with
                        | Some source -> claim.ClaimId = InputInbox.claimId source.Path source.Sha256
                        | None -> claim.Source.Path.TrimStart('.', '/') = normalized && claim.State = InputClaimState.Claimed)

                match pending, existing with
                | _, Some(_, claim) when claim.State = InputClaimState.Claimed -> Ok(claim, false)
                | _, Some(_, claim) -> Error(InputInboxError.WrongState(claim.ClaimId, claim.State, "claim"))
                | None, None -> Error(InputInboxError.NotPending path)
                | Some source, None ->
                    let claim = InputInbox.create source (port.Now()) claimant execution

                    port.WriteClaim ClaimLocation.Processing claim
                    |> storage
                    |> Result.bind (fun () -> finishClaim port claim)
                    |> Result.map (fun claimed -> claimed, true)))

    let derive
        (port: InputInboxPort)
        (claimId: string)
        (kind: DerivationKind)
        (target: DerivationTarget)
        (summary: string)
        (locator: string option)
        (actor: Actor)
        (execution: string option)
        : Result<InputClaim * bool, InputInboxError> =
        inProcessing port claimId
        |> Result.bind (fun claim ->
            let derivation =
                { Kind = kind
                  Target = target
                  Summary = summary
                  Locator = locator
                  RecordedAt = port.Now()
                  Actor = actor
                  ExecutionId = execution }

            InputInbox.derive derivation claim)
        |> Result.bind (fun (claim, changed) ->
            if changed then port.WriteClaim ClaimLocation.Processing claim |> storage |> Result.map (fun () -> claim, true)
            else Ok(claim, false))

    /// Leaves processing only when every derived change is durably
    /// reconciled. The completed record is written first, then the
    /// directory moves to `processed`.
    let complete (port: InputInboxPort) (claimId: string) (noDerivationReason: string option) : Result<InputClaim, InputInboxError> =
        inProcessing port claimId
        |> Result.bind (fun claim ->
          if claim.State = InputClaimState.Completed then Ok claim
          else
            port.ObserveDerivations claim
            |> storage
            |> Result.bind (fun observations -> InputInbox.complete (port.Now()) noDerivationReason observations claim)
            |> Result.bind (fun completed ->
                port.WriteClaim ClaimLocation.Processing completed
                |> Result.bind (fun () -> port.MoveClaim ClaimLocation.Processing ClaimLocation.Processed completed.ClaimId)
                |> storage
                |> Result.map (fun () -> completed)))

    /// Returns an unfinished claim's input to the inbox, unchanged.
    let release (port: InputInboxPort) (claimId: string) (reason: string) : Result<InputClaim, InputInboxError> =
        inProcessing port claimId
        |> Result.bind (InputInbox.release (port.Now()) reason)
        |> Result.bind (fun releasing ->
            port.WriteClaim ClaimLocation.Processing releasing
            |> storage
            |> Result.bind (fun () -> finishRelease port releasing)
            |> Result.map (fun () -> releasing))

    /// Rejects a claimed input, or a pending one (claimed first, so the
    /// same write-ahead rules apply): its record and bytes move to
    /// `.praxis/rejected/documents` with the reason.
    let reject (port: InputInboxPort) (target: string) (reason: string) (actor: Actor) (execution: string option) =
        let claimed =
            if InputInbox.isClaimId target then inProcessing port target
            else claim port target actor execution |> Result.map fst

        claimed
        |> Result.bind (fun claim ->
            if claim.State = InputClaimState.Rejected then Ok claim
            else
                InputInbox.reject (port.Now()) reason claim
                |> Result.bind (fun rejected ->
                    port.WriteClaim ClaimLocation.Processing rejected
                    |> Result.bind (fun () -> port.MoveClaim ClaimLocation.Processing ClaimLocation.Rejected rejected.ClaimId)
                    |> storage
                    |> Result.map (fun () -> rejected)))
