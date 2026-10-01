namespace Ros.Domain.Work

open Ros.Domain.Git

/// Whether a work item changed the repository in a meaningful way, from the
/// commit its first execution started at and the current working tree.
/// Praxis-owned state and configured ignored paths never count, so work
/// that only recorded Praxis state made no meaningful change.
[<RequireQualifiedAccess>]
type WorkMutation =
    | NotGitRepository
    | NoMeaningfulChange
    | MeaningfulChange of committedPaths: string list * uncommittedPaths: string list
    | Unknown of reason: string

[<RequireQualifiedAccess>]
module WorkMutation =
    /// `startToHead` is the raw path difference between the work item's
    /// recorded start commit and HEAD; `None` when no start commit is
    /// recorded. An unknown start is never read as "no change".
    let observe (filter: PathFilterConfig) (tree: WorkingTreeState) (startToHead: GitRead<string list> option) =
        match tree with
        | WorkingTreeState.NotRepository -> WorkMutation.NotGitRepository
        | WorkingTreeState.Unknown failure -> WorkMutation.Unknown failure.Message
        | _ ->
            let uncommitted = WorkingTreeState.meaningfulPaths tree

            match startToHead with
            | None -> WorkMutation.Unknown "the work item's start commit is not recorded, so committed changes cannot be ruled out"
            | Some(GitRead.Unavailable failure) -> WorkMutation.Unknown failure.Message
            | Some(GitRead.Observed paths) ->
                match PathFilter.meaningfulPaths filter paths |> List.distinct |> List.sort, uncommitted with
                | [], [] -> WorkMutation.NoMeaningfulChange
                | committed, uncommitted -> WorkMutation.MeaningfulChange(committed, uncommitted)

[<RequireQualifiedAccess>]
type CompletionGuardRejection =
    | NoCheckpoint
    | MutationUnknown of reason: string
    | CheckpointNotAtHead of position: LocalCheckpointPosition
    | UncommittedChanges of paths: string list
    | RemoteNoLongerVerifies of recoverability: CurrentRecoverability
    | StateUnknown of message: string

[<RequireQualifiedAccess>]
type CompletionGuardOutcome =
    /// The guard does not apply: no Git repository, or no meaningful change.
    | NotApplicable of reason: string
    /// The final state is the latest checkpoint, re-verified now.
    | Satisfied of RecordedCheckpoint
    | Rejected of CompletionGuardRejection list

[<RequireQualifiedAccess>]
module CompletionGuardRejection =
    let code rejection =
        match rejection with
        | CompletionGuardRejection.NoCheckpoint -> "no-checkpoint"
        | CompletionGuardRejection.MutationUnknown _ -> "mutation-unknown"
        | CompletionGuardRejection.CheckpointNotAtHead _ -> "checkpoint-not-at-head"
        | CompletionGuardRejection.UncommittedChanges _ -> "uncommitted-changes"
        | CompletionGuardRejection.RemoteNoLongerVerifies _ -> "remote-no-longer-verifies"
        | CompletionGuardRejection.StateUnknown _ -> "state-unknown"

    let message (workItemId: string) rejection =
        match rejection with
        | CompletionGuardRejection.NoCheckpoint ->
            $"'{workItemId}' changed the repository meaningfully but has no durable checkpoint; commit and push the final state, run 'work checkpoint', then complete"
        | CompletionGuardRejection.MutationUnknown reason ->
            $"whether '{workItemId}' changed the repository cannot be determined ({reason}); push HEAD and run 'work checkpoint' (no new commit is needed), then complete"
        | CompletionGuardRejection.CheckpointNotAtHead position ->
            let detail =
                match position with
                | LocalCheckpointPosition.CommitsAfter(count, _) -> $"HEAD has {count} commit(s) with meaningful changes after it"
                | LocalCheckpointPosition.HeadDoesNotContain _ -> "HEAD does not contain it"
                | LocalCheckpointPosition.CheckpointNotPresent -> "its commit is not present locally"
                | other -> LocalCheckpointPosition.code other

            $"the latest checkpoint of '{workItemId}' is not the final HEAD ({detail}); push the final commit and checkpoint it, then complete"
        | CompletionGuardRejection.UncommittedChanges paths ->
            let shown = paths |> List.truncate 10 |> String.concat ", "
            $"meaningful uncommitted changes remain ({shown}); commit and push them, checkpoint, then complete"
        | CompletionGuardRejection.RemoteNoLongerVerifies recoverability ->
            $"the remote no longer verifies the latest checkpoint of '{workItemId}' ({CurrentRecoverability.code recoverability}); push the final state, checkpoint it again, then complete"
        | CompletionGuardRejection.StateUnknown message ->
            $"continuity state of '{workItemId}' is unknown ({message}); completion is refused rather than assuming durability"

[<RequireQualifiedAccess>]
module CompletionGuard =
    /// Meaningful Git-backed work completes only when its final state is the
    /// latest checkpoint, the remote still verifies it now, and nothing
    /// meaningful is left uncommitted. Work that changed nothing completes
    /// without any commit.
    let decide (mutation: WorkMutation) (assessment: CheckpointAssessment) =
        match mutation, assessment.Checkpoint with
        | WorkMutation.NotGitRepository, _ -> CompletionGuardOutcome.NotApplicable "not a Git repository"
        | WorkMutation.NoMeaningfulChange, None -> CompletionGuardOutcome.NotApplicable "no meaningful repository change"
        | WorkMutation.Unknown reason, None -> CompletionGuardOutcome.Rejected [ CompletionGuardRejection.MutationUnknown reason ]
        | WorkMutation.MeaningfulChange(_, uncommitted), None ->
            CompletionGuardOutcome.Rejected
                [ CompletionGuardRejection.NoCheckpoint
                  if not uncommitted.IsEmpty then
                      CompletionGuardRejection.UncommittedChanges uncommitted ]
        | _, Some recorded ->
            let rejections =
                [ match assessment.Local with
                  | Some position when LocalCheckpointPosition.isEffectivelyAtCheckpoint position -> ()
                  | Some(LocalCheckpointPosition.Unknown message) -> CompletionGuardRejection.StateUnknown message
                  | Some position -> CompletionGuardRejection.CheckpointNotAtHead position
                  | None -> CompletionGuardRejection.StateUnknown "HEAD was not compared with the checkpoint"
                  match assessment.WorkingTree with
                  | WorkingTreeState.Dirty(paths, _) -> CompletionGuardRejection.UncommittedChanges paths
                  | WorkingTreeState.Unknown failure -> CompletionGuardRejection.StateUnknown failure.Message
                  | _ -> ()
                  match assessment.CurrentRecoverability with
                  | Some recoverability when CurrentRecoverability.verifiesCheckpoint recoverability -> ()
                  | Some recoverability -> CompletionGuardRejection.RemoteNoLongerVerifies recoverability
                  | None -> CompletionGuardRejection.StateUnknown "the remote was not observed" ]

            if rejections.IsEmpty then
                CompletionGuardOutcome.Satisfied recorded
            else
                CompletionGuardOutcome.Rejected rejections

[<RequireQualifiedAccess>]
type BlockGuardOutcome =
    | NotApplicable of reason: string
    /// Nothing meaningful happened after the latest checkpoint (or since the
    /// work began, without one): ordinary blocking.
    | NoNewWork
    /// The caller truthfully states the latest local state is not remotely
    /// recoverable; the block event records it.
    | RecordedUnrecoverable of reason: RequiredText * newWork: string
    | Rejected of newWork: string

[<RequireQualifiedAccess>]
module BlockGuard =
    let private newWork (mutation: WorkMutation) (assessment: CheckpointAssessment) =
        match assessment.Checkpoint, assessment.Local, assessment.WorkingTree with
        | _, _, WorkingTreeState.Unknown failure -> Some $"the working tree could not be observed ({failure.Message})"
        | _, _, WorkingTreeState.Dirty(paths, _) -> Some $"{paths.Length} meaningful uncommitted path(s)"
        | Some _, Some position, _ when LocalCheckpointPosition.isEffectivelyAtCheckpoint position -> None
        | Some _, Some(LocalCheckpointPosition.CommitsAfter(count, _)), _ -> Some $"{count} commit(s) after the latest checkpoint"
        | Some _, Some position, _ -> Some $"HEAD is not at the latest checkpoint ({LocalCheckpointPosition.code position})"
        | Some _, None, _ -> Some "HEAD was not compared with the checkpoint"
        | None, _, _ ->
            match mutation with
            | WorkMutation.NoMeaningfulChange
            | WorkMutation.NotGitRepository -> None
            | WorkMutation.MeaningfulChange(committed, _) -> Some $"{committed.Length} meaningful committed path(s) and no checkpoint"
            | WorkMutation.Unknown reason -> Some $"whether work changed the repository is unknown ({reason})"

    /// Blocking after meaningful work since the latest checkpoint requires a
    /// checkpoint first or an explicit, truthful record that the latest local
    /// state is not remotely recoverable. A reason offered when there is no
    /// new work is not recorded: it would state something untrue.
    let decide (mutation: WorkMutation) (assessment: CheckpointAssessment) (unrecoverableReason: string option) =
        match mutation with
        | WorkMutation.NotGitRepository -> BlockGuardOutcome.NotApplicable "not a Git repository"
        | _ ->
            match newWork mutation assessment, unrecoverableReason |> Option.bind RequiredText.tryCreate with
            | None, _ -> BlockGuardOutcome.NoNewWork
            | Some work, Some reason -> BlockGuardOutcome.RecordedUnrecoverable(reason, work)
            | Some work, None -> BlockGuardOutcome.Rejected work

/// Everything continuation needs, already observed.
type ContinuationInput =
    { WorkItemId: string
      ItemState: LiveWorkState option
      Assessment: CheckpointAssessment
      /// Active executions of this work item the caller may continue itself.
      CallerExecutions: string list
      /// Active executions of this work item that belong to someone else, oldest first.
      OtherActiveExecutions: string list
      /// The most recently started execution of the work item, of any status.
      LatestExecution: string option }

[<RequireQualifiedAccess>]
type PredecessorDisposition =
    /// It is still active and nobody finalized it: its executor is gone.
    | Interrupted
    /// It had already ended.
    | Ended

type ContinuationPlan =
    { WorkItemId: string
      Predecessor: (string * PredecessorDisposition) option
      OtherActiveExecutions: string list
      Checkpoint: RecordedCheckpoint option
      Warnings: ContinuityWarning list }

[<RequireQualifiedAccess>]
type ContinuationRejection =
    | WorkItemNotFound
    | NotActive of LiveWorkState
    | AlreadyExecuting of executionIds: string list
    | LocalChanges of paths: string list
    | CheckoutDoesNotContainCheckpoint of position: LocalCheckpointPosition
    | StateUnknown of message: string

[<RequireQualifiedAccess>]
module PredecessorDisposition =
    let code disposition =
        match disposition with
        | PredecessorDisposition.Interrupted -> "interrupted"
        | PredecessorDisposition.Ended -> "ended"

[<RequireQualifiedAccess>]
module ContinuationRejection =
    let code rejection =
        match rejection with
        | ContinuationRejection.WorkItemNotFound -> "work-item-not-found"
        | ContinuationRejection.NotActive _ -> "work-item-not-active"
        | ContinuationRejection.AlreadyExecuting _ -> "already-executing"
        | ContinuationRejection.LocalChanges _ -> "local-changes"
        | ContinuationRejection.CheckoutDoesNotContainCheckpoint _ -> "checkout-does-not-contain-checkpoint"
        | ContinuationRejection.StateUnknown _ -> "state-unknown"

    let message (workItemId: string) rejection =
        match rejection with
        | ContinuationRejection.WorkItemNotFound -> $"work item '{workItemId}' is not in repository context; fetch the branch that carries its Praxis state"
        | ContinuationRejection.NotActive LiveWorkState.Blocked ->
            $"'{workItemId}' is blocked; use 'work resume' (continuation is for active work whose executor disappeared)"
        | ContinuationRejection.NotActive LiveWorkState.Ready -> $"'{workItemId}' has not begun; use 'work start'"
        | ContinuationRejection.NotActive _ -> $"'{workItemId}' is complete; there is nothing to continue"
        | ContinuationRejection.AlreadyExecuting ids ->
            $"""this process already has an active execution of '{workItemId}' ({String.concat ", " ids}); keep using it"""
        | ContinuationRejection.LocalChanges paths ->
            let shown = paths |> List.truncate 10 |> String.concat ", "
            $"this checkout has meaningful uncommitted changes ({shown}); Praxis will not overwrite or claim them. Commit them elsewhere or use a clean clone, then continue"
        | ContinuationRejection.CheckoutDoesNotContainCheckpoint position ->
            $"this checkout does not contain the latest checkpoint ({LocalCheckpointPosition.code position}); follow the recovery steps, then continue"
        | ContinuationRejection.StateUnknown message -> $"the checkout's state could not be determined ({message}); continuation is refused rather than guessing"

[<RequireQualifiedAccess>]
module Continuation =
    /// A new executor takes over an active work item. The item stays active;
    /// the successor gets its own execution; the predecessor is named, never
    /// impersonated, and never recorded as having succeeded.
    let decide (input: ContinuationInput) : Result<ContinuationPlan, ContinuationRejection> =
        match input.ItemState with
        | None -> Error ContinuationRejection.WorkItemNotFound
        | Some state when state <> LiveWorkState.Active -> Error(ContinuationRejection.NotActive state)
        | Some _ when not input.CallerExecutions.IsEmpty -> Error(ContinuationRejection.AlreadyExecuting input.CallerExecutions)
        | Some _ ->
            let assessment = input.Assessment

            let checkoutProblem =
                match assessment.WorkingTree, assessment.Checkpoint, assessment.Local with
                | WorkingTreeState.Dirty(paths, _), _, _ -> Some(ContinuationRejection.LocalChanges paths)
                | WorkingTreeState.Unknown failure, _, _ -> Some(ContinuationRejection.StateUnknown failure.Message)
                | _, None, _ -> None
                | _, Some _, Some(LocalCheckpointPosition.Unknown message) -> Some(ContinuationRejection.StateUnknown message)
                | _, Some _, Some(LocalCheckpointPosition.HeadDoesNotContain _ as position)
                | _, Some _, Some(LocalCheckpointPosition.CheckpointNotPresent as position)
                | _, Some _, Some(LocalCheckpointPosition.NoHead as position) ->
                    Some(ContinuationRejection.CheckoutDoesNotContainCheckpoint position)
                | _, Some _, _ -> None

            match checkoutProblem with
            | Some rejection -> Error rejection
            | None ->
                let fromCheckpoint =
                    assessment.Checkpoint
                    |> Option.map _.Recorded.ExecutionId
                    |> Option.filter (fun id -> List.contains id input.OtherActiveExecutions)

                let predecessor =
                    match fromCheckpoint, List.tryLast input.OtherActiveExecutions, input.LatestExecution with
                    | Some id, _, _ -> Some(id, PredecessorDisposition.Interrupted)
                    | None, Some id, _ -> Some(id, PredecessorDisposition.Interrupted)
                    | None, None, Some id -> Some(id, PredecessorDisposition.Ended)
                    | None, None, None -> None

                Ok
                    { WorkItemId = input.WorkItemId
                      Predecessor = predecessor
                      OtherActiveExecutions =
                        input.OtherActiveExecutions
                        |> List.filter (fun id -> Some id <> (predecessor |> Option.map fst))
                      Checkpoint = assessment.Checkpoint
                      Warnings = assessment.Warnings }

/// A recovery step derived from the recorded checkpoint. None of them
/// discards or rewrites anything; a dirty checkout is a stop, not a reset.
[<RequireQualifiedAccess>]
type RecoveryStep =
    | Stop of reason: string
    | Run of command: string * purpose: string
    | Verify of command: string * expectation: string
    | Note of text: string

[<RequireQualifiedAccess>]
module RecoveryStep =
    let kind step =
        match step with
        | RecoveryStep.Stop _ -> "stop"
        | RecoveryStep.Run _ -> "run"
        | RecoveryStep.Verify _ -> "verify"
        | RecoveryStep.Note _ -> "note"

    let render step =
        match step with
        | RecoveryStep.Stop reason -> $"STOP: {reason}"
        | RecoveryStep.Run(command, purpose) -> $"run:    {command}    # {purpose}"
        | RecoveryStep.Verify(command, expectation) -> $"verify: {command}    # {expectation}"
        | RecoveryStep.Note text -> $"note:   {text}"

[<RequireQualifiedAccess>]
module RecoveryInstructions =
    let derive (assessment: CheckpointAssessment) : RecoveryStep list =
        match assessment.Checkpoint with
        | None ->
            [ RecoveryStep.Note
                  "no durable checkpoint is recorded; nothing guarantees which remote state is the intended recovery point, so inspect the branch history before relying on it" ]
        | Some recorded ->
            let git = DurableLocation.git recorded.Recorded.Location
            let commit = git.RemoteCommit.Value
            let remote = git.Remote.Name
            let verifyStep = RecoveryStep.Verify($"git merge-base --is-ancestor {commit} HEAD", $"HEAD contains checkpoint {CommitId.short git.RemoteCommit}")

            let checkout =
                match assessment.WorkingTree, assessment.Local with
                | WorkingTreeState.Dirty(paths, _), _ ->
                    [ RecoveryStep.Stop
                          $"this checkout has {paths.Length} meaningful uncommitted path(s); Praxis will not discard or claim them. Commit them to a branch of their own or use a separate clean clone" ]
                | WorkingTreeState.Unknown failure, _ -> [ RecoveryStep.Stop $"the checkout's state is unknown ({failure.Message}); inspect it yourself first" ]
                | _, Some position when LocalCheckpointPosition.isEffectivelyAtCheckpoint position ->
                    [ RecoveryStep.Note $"this checkout already holds checkpoint {CommitId.short git.RemoteCommit} of branch {git.Branch}"; verifyStep ]
                | _, Some(LocalCheckpointPosition.CommitsAfter(count, _)) ->
                    [ RecoveryStep.Note
                          $"this checkout has {count} commit(s) with meaningful changes after the checkpoint that no checkpoint verifies; review them (git log {commit}..HEAD) before relying on them"
                      verifyStep ]
                | _ ->
                    [ yield!
                          git.Remote.Url
                          |> Option.map (fun url ->
                              RecoveryStep.Note $"in a fresh environment: git clone --branch {git.RemoteBranch} {url}")
                          |> Option.toList
                      RecoveryStep.Run($"git fetch {remote} {git.RemoteBranch}", "retrieve the recorded remote branch")
                      RecoveryStep.Run($"git switch {git.Branch}", $"use the work's branch (if it does not exist locally: git switch --track {remote}/{git.RemoteBranch})")
                      verifyStep ]

            let remoteNote =
                match assessment.CurrentRecoverability with
                | Some(CurrentRecoverability.NotContained _)
                | Some CurrentRecoverability.RemoteBranchMissing ->
                    [ RecoveryStep.Note
                          $"{remote}/{git.RemoteBranch} no longer carries the checkpoint; it may still be retrievable by its ID (git fetch {remote} {commit}) if the host keeps it" ]
                | Some(CurrentRecoverability.RemoteAdvanced(_, count, _)) ->
                    [ RecoveryStep.Note $"{remote}/{git.RemoteBranch} has {count} commit(s) after the checkpoint; someone continued without checkpointing" ]
                | Some(CurrentRecoverability.RemoteUnreachable message) ->
                    [ RecoveryStep.Note $"{remote} could not be reached ({message}); the checkpoint's current recoverability is unknown" ]
                | _ -> []

            checkout @ remoteNote
