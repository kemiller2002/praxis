namespace Ros.Domain.Integration

open Ros.Domain.Git

/// A provider-neutral observation of one integration check. Providers may
/// call success "success", "passed", etc.; adapters normalize to this model.
[<RequireQualifiedAccess>]
type CheckState =
    | Succeeded
    | Failed
    | Pending
    | Cancelled
    | Skipped
    | Missing
    | Unknown of string

[<RequireQualifiedAccess>]
module CheckState =
    let code state =
        match state with
        | CheckState.Succeeded -> "succeeded"
        | CheckState.Failed -> "failed"
        | CheckState.Pending -> "pending"
        | CheckState.Cancelled -> "cancelled"
        | CheckState.Skipped -> "skipped"
        | CheckState.Missing -> "missing"
        | CheckState.Unknown value -> $"unknown:{value}"

    let tryParse value =
        match value with
        | "succeeded"
        | "success"
        | "passed" -> Some CheckState.Succeeded
        | "failed"
        | "failure" -> Some CheckState.Failed
        | "pending"
        | "queued"
        | "in-progress"
        | "in_progress" -> Some CheckState.Pending
        | "cancelled"
        | "canceled" -> Some CheckState.Cancelled
        | "skipped" -> Some CheckState.Skipped
        | "missing" -> Some CheckState.Missing
        | value when value.StartsWith("unknown:", System.StringComparison.Ordinal) && value.Length > 8 ->
            Some(CheckState.Unknown(value.Substring(8)))
        | value when not (System.String.IsNullOrWhiteSpace value) -> Some(CheckState.Unknown value)
        | _ -> None

type MergeReadinessPolicy =
    { Enabled: bool
      RequiredChecks: string list
      OptionalChecks: string list
      RequireCleanWorkingTree: bool
      RequireRemoteCandidate: bool }

type CheckEvidence =
    { Id: string
      State: CheckState
      Commit: CommitId option }

type MergeReadinessEvidence =
    { CandidateCommit: CommitId option
      RemoteCandidateCurrent: bool option
      Checks: CheckEvidence list }

type MergeReadinessObservation =
    { Head: CommitId option
      WorkingTreeClean: bool option
      Evidence: MergeReadinessEvidence }

[<RequireQualifiedAccess>]
type MergeBlocker =
    | CandidateCommitUnavailable
    | HeadUnavailable
    | CandidateDoesNotMatchHead of candidate: CommitId * head: CommitId
    | WorkingTreeUnknown
    | WorkingTreeDirty
    | RemoteCandidateUnknown
    | RemoteCandidateNotCurrent
    | RequiredCheckMissing of checkId: string
    | RequiredCheckNotSuccessful of checkId: string * state: CheckState
    | RequiredCheckCommitUnknown of checkId: string
    | RequiredCheckStale of checkId: string * observed: CommitId * candidate: CommitId
    | DuplicateCheckEvidence of checkId: string

[<RequireQualifiedAccess>]
module MergeBlocker =
    let code blocker =
        match blocker with
        | MergeBlocker.CandidateCommitUnavailable -> "candidate-commit-unavailable"
        | MergeBlocker.HeadUnavailable -> "head-unavailable"
        | MergeBlocker.CandidateDoesNotMatchHead _ -> "candidate-does-not-match-head"
        | MergeBlocker.WorkingTreeUnknown -> "working-tree-unknown"
        | MergeBlocker.WorkingTreeDirty -> "working-tree-dirty"
        | MergeBlocker.RemoteCandidateUnknown -> "remote-candidate-unknown"
        | MergeBlocker.RemoteCandidateNotCurrent -> "remote-candidate-not-current"
        | MergeBlocker.RequiredCheckMissing _ -> "required-check-missing"
        | MergeBlocker.RequiredCheckNotSuccessful _ -> "required-check-not-successful"
        | MergeBlocker.RequiredCheckCommitUnknown _ -> "required-check-commit-unknown"
        | MergeBlocker.RequiredCheckStale _ -> "required-check-stale"
        | MergeBlocker.DuplicateCheckEvidence _ -> "duplicate-check-evidence"

    let message blocker =
        match blocker with
        | MergeBlocker.CandidateCommitUnavailable ->
            "merge-readiness evidence does not name an exact candidate commit"
        | MergeBlocker.HeadUnavailable ->
            "the current repository HEAD could not be established"
        | MergeBlocker.CandidateDoesNotMatchHead(candidate, head) ->
            $"evidence is for {CommitId.short candidate}, but HEAD is {CommitId.short head}; a later commit invalidates prior readiness evidence"
        | MergeBlocker.WorkingTreeUnknown ->
            "the meaningful working-tree state could not be established"
        | MergeBlocker.WorkingTreeDirty ->
            "meaningful uncommitted changes exist; the candidate is not an exact integration artifact"
        | MergeBlocker.RemoteCandidateUnknown ->
            "the evidence does not establish that the remote integration candidate is current"
        | MergeBlocker.RemoteCandidateNotCurrent ->
            "the evidence says the remote integration candidate is not current"
        | MergeBlocker.RequiredCheckMissing checkId ->
            $"required check '{checkId}' has no observation"
        | MergeBlocker.RequiredCheckNotSuccessful(checkId, state) ->
            $"required check '{checkId}' is {CheckState.code state}, not succeeded"
        | MergeBlocker.RequiredCheckCommitUnknown checkId ->
            $"required check '{checkId}' is not bound to an exact commit"
        | MergeBlocker.RequiredCheckStale(checkId, observed, candidate) ->
            $"required check '{checkId}' is for {CommitId.short observed}, not candidate {CommitId.short candidate}"
        | MergeBlocker.DuplicateCheckEvidence checkId ->
            $"required check '{checkId}' has more than one observation; readiness fails closed"

[<RequireQualifiedAccess>]
type MergeReadinessDecision =
    | Disabled
    | NotReady of candidate: CommitId option * blockers: MergeBlocker list
    | Ready of candidate: CommitId

[<RequireQualifiedAccess>]
module MergeReadiness =
    let private distinctNonBlank values =
        values
        |> List.filter (System.String.IsNullOrWhiteSpace >> not)
        |> List.distinct

    let normalizePolicy policy =
        { policy with
            RequiredChecks = distinctNonBlank policy.RequiredChecks
            OptionalChecks = distinctNonBlank policy.OptionalChecks }

    let private checkBlockers candidate requiredChecks checks =
        requiredChecks
        |> List.collect (fun checkId ->
            let matches = checks |> List.filter (fun check -> check.Id = checkId)

            match matches with
            | [] -> [ MergeBlocker.RequiredCheckMissing checkId ]
            | _ :: _ :: _ -> [ MergeBlocker.DuplicateCheckEvidence checkId ]
            | [ check ] ->
                match check.State with
                | CheckState.Succeeded ->
                    match candidate, check.Commit with
                    | Some candidateCommit, Some observed when observed = candidateCommit -> []
                    | Some candidateCommit, Some observed ->
                        [ MergeBlocker.RequiredCheckStale(checkId, observed, candidateCommit) ]
                    | _, None -> [ MergeBlocker.RequiredCheckCommitUnknown checkId ]
                    | None, _ -> []
                | state -> [ MergeBlocker.RequiredCheckNotSuccessful(checkId, state) ])

    /// Merge readiness is an exact-candidate decision. NotReady is an
    /// ordinary development result: it does not make an in-progress branch
    /// invalid and has no relationship to checkpoint recoverability.
    let decide rawPolicy observation =
        let policy = normalizePolicy rawPolicy

        if not policy.Enabled then
            MergeReadinessDecision.Disabled
        else
            let candidate = observation.Evidence.CandidateCommit

            let candidateBlockers =
                [ if candidate.IsNone then
                      yield MergeBlocker.CandidateCommitUnavailable

                  match candidate, observation.Head with
                  | _, None -> yield MergeBlocker.HeadUnavailable
                  | Some candidateCommit, Some head when candidateCommit <> head ->
                      yield MergeBlocker.CandidateDoesNotMatchHead(candidateCommit, head)
                  | _ -> ()

                  if policy.RequireCleanWorkingTree then
                      match observation.WorkingTreeClean with
                      | Some true -> ()
                      | Some false -> yield MergeBlocker.WorkingTreeDirty
                      | None -> yield MergeBlocker.WorkingTreeUnknown

                  if policy.RequireRemoteCandidate then
                      match observation.Evidence.RemoteCandidateCurrent with
                      | Some true -> ()
                      | Some false -> yield MergeBlocker.RemoteCandidateNotCurrent
                      | None -> yield MergeBlocker.RemoteCandidateUnknown ]

            let blockers =
                candidateBlockers
                @ checkBlockers candidate policy.RequiredChecks observation.Evidence.Checks

            match candidate, blockers with
            | Some commit, [] -> MergeReadinessDecision.Ready commit
            | _ -> MergeReadinessDecision.NotReady(candidate, blockers)
