namespace Praxis.Application.Work

open System
open Praxis.Domain.Work

/// F4: one evidence document Praxis can cite, read from the out-of-tree
/// store (I8): a verifier receipt in attested mode, a `validate-run` result
/// otherwise. Selection reads it unverified; the gate verifies it.
type EvidenceCandidate =
    { Id: string
      Path: string
      Digest: string
      Validation: string
      Status: CheckStatus
      Commit: string
      Generation: int64
      ObservedAt: DateTimeOffset
      /// Breaks ties between candidates observed at the same instant.
      Tiebreak: string }

/// The flags a caller passed explicitly. `None` (or empty) means derive it.
type ExplicitCompletion =
    { Claimant: string option
      Generation: int64 option
      Commit: string option
      VerificationRevision: string option
      OrdoContractVersion: string option
      OrdoContractDigest: string option
      RequirementIds: string list
      DecisionIds: string list
      ConflictResolutionIds: string list
      Validations: ValidationClaim list
      /// Explicit evidence, each with what Praxis observed of it (if recognized).
      Evidence: (EvidenceSubmission * EvidenceCandidate option) list
      TelemetryExecutions: string list }

type DerivedCompletionPorts =
    { Completion: CompletionPorts
      /// `git status --porcelain` entries; any entry is a dirty worktree.
      WorktreeStatus: unit -> Result<string list, string>
      /// Every stored candidate for the work item.
      Candidates: string -> Result<EvidenceCandidate list, string>
      /// Appends the tracked completion event and makes the metadata-only
      /// commit; returns its SHA (idempotent for an already published event).
      Publish: WorkClaim -> VerifiedCompletion -> Result<string, string>
      /// Ends the named timing step (I8/F4 `timings`).
      Mark: string -> unit }

type DerivedCompletionResult =
    { Outcome: CompletionOutcome
      VerifiedCommit: string option
      MetadataCommit: string option }

/// F3 + F4: `praxis work complete-verified --id ID --token TOKEN` derives
/// every other argument from Praxis-held state, enforces the clean
/// completion identity (I8) and publishes the tracked completion event in a
/// metadata-only commit whose parent is the verified commit.
[<RequireQualifiedAccess>]
module DerivedCompletion =
    let MismatchCode = "PRX.COMPLETION.ARGUMENT_MISMATCH"
    let DirtyWorktreeCode = "PRX.COMPLETION.DIRTY_WORKTREE"
    let HeadMismatchCode = "PRX.COMPLETION.HEAD_MISMATCH"
    let MetadataCommitFailedCode = "PRX.COMPLETION.METADATA_COMMIT_FAILED"

    let private finding code detail = { Code = code; Detail = detail }

    let private ordinal (values: string list) =
        values |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    /// The latest candidate per evidence ID for `generation`.
    let latest (generation: int64) (candidates: EvidenceCandidate list) =
        candidates
        |> List.filter (fun candidate -> candidate.Generation = generation)
        |> List.groupBy _.Id
        |> List.map (fun (_, group) -> group |> List.maxBy (fun candidate -> candidate.ObservedAt, candidate.Tiebreak))
        |> List.sortBy _.Id

    /// One validation claim per validation name, from the selected evidence.
    let validationsOf (selected: EvidenceCandidate list) =
        selected
        |> List.groupBy _.Validation
        |> List.map (fun (name, group) ->
            { Name = name
              Status =
                group
                |> List.map _.Status
                |> List.tryFind (fun status -> status <> CheckStatus.Passed)
                |> Option.defaultValue CheckStatus.Passed
              EvidenceIds = group |> List.map _.Id |> ordinal })
        |> List.sortBy _.Name

    let private shape (validations: ValidationClaim list) =
        validations |> List.map (fun validation -> validation.Name, ordinal validation.EvidenceIds) |> List.sort

    /// Pure derivation (F4). `published` is true when the claim is already
    /// completed: then the identity was checked when it completed, and HEAD
    /// may be its metadata commit.
    let derive
        (bound: BoundCompletionAuthority)
        (claim: WorkClaim)
        (token: string)
        (candidates: EvidenceCandidate list)
        (head: string)
        (worktree: string list)
        (explicit: ExplicitCompletion)
        : Result<CompletionRequest, CompletionFinding list> =
        let input = BoundCompletionAuthority.input bound

        match WorkVerification.fenceOf token claim with
        | None -> Error [ finding (ClaimConflict.code ClaimConflict.StaleToken) (ClaimConflict.detail ClaimConflict.StaleToken) ]
        | Some fence ->
            let published = claim.Status = ClaimStatus.Completed

            let selected, submissions =
                match explicit.Evidence with
                | [] ->
                    let chosen = latest fence.Generation candidates
                    chosen, chosen |> List.map (fun candidate -> { Id = candidate.Id; Path = candidate.Path; ClaimedDigest = None })
                | given -> given |> List.choose snd, given |> List.map fst

            let commits = selected |> List.map _.Commit |> List.distinct

            let commit =
                match commits with
                | [ single ] -> single
                | _ -> head

            let same name (expected: string) (given: string option) =
                match given with
                | Some value when value <> expected -> Some(finding MismatchCode $"{name} '{value}' differs from the derived value '{expected}'")
                | _ -> None

            let sameSet name (expected: string list) (given: string list) =
                if given.IsEmpty || ordinal given = ordinal expected then None
                else Some(finding MismatchCode $"""{name} {ordinal given |> String.concat ","} differs from the derived set {ordinal expected |> String.concat ","}""")

            let derivedValidations = validationsOf selected

            let validationMismatch =
                if explicit.Validations.IsEmpty || not explicit.Evidence.IsEmpty || shape explicit.Validations = shape derivedValidations then
                    None
                else
                    Some(finding MismatchCode "--validation differs from the validations of the latest evidence for this generation")

            let identity =
                [ if commits.Length > 1 then
                      yield finding HeadMismatchCode $"""the selected evidence names different commits ({commits |> String.concat ", "}); verify again at one commit"""
                  if not published && commits.Length = 1 && commit <> head then
                      yield
                          finding
                              HeadMismatchCode
                              $"HEAD is {head} but the evidence was verified at {commit}; a completion must name exactly the verified commit (reset to it, or verify again)"
                  if not published && not worktree.IsEmpty then
                      let shown = worktree |> List.truncate 10 |> String.concat ", "
                      yield finding DirtyWorktreeCode $"the worktree is not clean ({shown}); a completion must name exactly the verified commit" ]

            let mismatches =
                [ same "--claimant" fence.Claimant explicit.Claimant
                  same "--generation" (string fence.Generation) (explicit.Generation |> Option.map string)
                  same "--commit" commit explicit.Commit
                  same "--verification-revision" commit explicit.VerificationRevision
                  same "--ordo-contract-version" input.OrdoContractVersion explicit.OrdoContractVersion
                  same "--ordo-contract-digest" input.OrdoContractDigest explicit.OrdoContractDigest
                  sameSet "--requirement" input.RequiredRequirementIds explicit.RequirementIds
                  sameSet "--decision" input.RequiredDecisionIds explicit.DecisionIds
                  sameSet "--conflict-resolution" input.RequiredConflictResolutionIds explicit.ConflictResolutionIds
                  validationMismatch ]
                |> List.choose id

            match mismatches @ identity with
            | [] ->
                Ok
                    { WorkItemId = claim.WorkItemId
                      Fence = fence
                      Commit = commit
                      VerificationRevision = commit
                      OrdoContractVersion = Some input.OrdoContractVersion
                      OrdoContractDigest = Some input.OrdoContractDigest
                      RequirementIds = input.RequiredRequirementIds
                      DecisionIds = input.RequiredDecisionIds
                      ConflictResolutionIds = input.RequiredConflictResolutionIds
                      Validations = if explicit.Validations.IsEmpty then derivedValidations else explicit.Validations
                      Evidence = submissions
                      TelemetryExecutions = explicit.TelemetryExecutions }
            | findings -> Error findings

    let private publish (ports: DerivedCompletionPorts) (claim: WorkClaim) (completion: VerifiedCompletion) outcome =
        let result = ports.Completion.Claims.Store.Read claim.WorkItemId |> Result.defaultValue (Some claim)

        match ports.Publish claim completion with
        | Ok metadataCommit ->
            ports.Mark "metadataCommit"
            { Outcome = outcome; VerifiedCommit = Some completion.Commit; MetadataCommit = Some metadataCommit }
        | Error reason ->
            ports.Mark "metadataCommit"

            { Outcome =
                CompletionOutcome.Rejected(
                    [ finding
                          MetadataCommitFailedCode
                          $"Dokimos verified the completion, but the metadata-only commit could not be made ({reason}); the tracked files were restored. Fix the cause and run the same command again: it replays without re-verifying" ],
                    result
                )
              VerifiedCommit = Some completion.Commit
              MetadataCommit = None }

    /// The whole F3/F4 flow over already-bound authority. The claim must
    /// exist; `explicit` holds the caller's flags, each checked against its
    /// derived value.
    let complete (ports: DerivedCompletionPorts) (bound: BoundCompletionAuthority) (workItemId: string) (token: string) (explicit: ExplicitCompletion) =
        let rejected findings current = { Outcome = CompletionOutcome.Rejected(findings, current); VerifiedCommit = None; MetadataCommit = None }

        match ports.Completion.Claims.Store.Read workItemId with
        | Error message -> { Outcome = CompletionOutcome.Failed message; VerifiedCommit = None; MetadataCommit = None }
        | Ok None -> rejected [ finding (ClaimConflict.code (ClaimConflict.NotHeld ClaimStatus.Unclaimed)) $"'{workItemId}' has no claim" ] None
        | Ok(Some claim) ->
            ports.Mark "claim"

            match ports.Completion.ObserveHead(), ports.WorktreeStatus(), ports.Candidates workItemId with
            | Error message, _, _
            | _, Error message, _
            | _, _, Error message -> { Outcome = CompletionOutcome.Failed message; VerifiedCommit = None; MetadataCommit = None }
            | Ok head, Ok worktree, Ok candidates ->
                ports.Mark "receipts"

                match derive bound claim token candidates head worktree explicit with
                | Error findings -> rejected findings (Some claim)
                | Ok request ->
                    // HEAD is the derived commit (or the claim is already
                    // completed), so the gate's observed revision is the verified one.
                    let completion = { ports.Completion with ObserveHead = fun () -> Ok request.Commit }
                    let outcome = WorkClaims.completeVerified completion bound request
                    ports.Mark "dokimos"

                    match outcome with
                    | CompletionOutcome.Completed(completed, verified)
                    | CompletionOutcome.Replayed(completed, verified) -> publish ports completed verified outcome
                    | other -> { Outcome = other; VerifiedCommit = None; MetadataCommit = None }
