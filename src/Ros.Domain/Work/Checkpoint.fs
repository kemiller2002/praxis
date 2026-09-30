namespace Ros.Domain.Work

open Ros.Domain.Git

/// Text that is never blank. Construction trims surrounding whitespace, so a
/// whitespace-only summary or next action can never be recorded.
type RequiredText =
    private
    | RequiredText of string

    member this.Value =
        let (RequiredText value) = this
        value

    override this.ToString() = this.Value

[<RequireQualifiedAccess>]
module RequiredText =
    let tryCreate (value: string) =
        if System.String.IsNullOrWhiteSpace value then None else Some(RequiredText(value.Trim()))

    let value (text: RequiredText) = text.Value

/// How a checkpoint's durability was established. Git remote observation is
/// the first mechanism; another durability provider is a new case, never a
/// reinterpretation of this one.
[<RequireQualifiedAccess>]
type DurabilityMechanism = | GitRemoteObservation

[<RequireQualifiedAccess>]
module DurabilityMechanism =
    let code mechanism =
        match mechanism with
        | DurabilityMechanism.GitRemoteObservation -> "git-remote-observation"

    let tryParse value =
        match value with
        | "git-remote-observation" -> Some DurabilityMechanism.GitRemoteObservation
        | _ -> None

/// Where a Git checkpoint is durably retrievable, exactly as Praxis observed
/// it when the checkpoint was accepted. The local commit and the remote
/// commit are separate observations of separate places; at acceptance they
/// were equal.
type GitDurableLocation =
    { /// The repository identity (`ros.json` `repository.id`).
      Repository: string
      /// The local branch HEAD was on.
      Branch: string
      /// Local HEAD when the checkpoint was accepted.
      LocalCommit: CommitId
      /// The durability target.
      Remote: RemoteIdentity
      /// The upstream branch on that remote.
      RemoteBranch: string
      /// The remote branch head Praxis read from the remote itself.
      RemoteCommit: CommitId }

[<RequireQualifiedAccess>]
type DurableLocation = GitRemoteBranch of GitDurableLocation

[<RequireQualifiedAccess>]
module DurableLocation =
    let commit location =
        match location with
        | DurableLocation.GitRemoteBranch git -> git.RemoteCommit

    let git location =
        match location with
        | DurableLocation.GitRemoteBranch git -> git

/// Only verified checkpoints are ever recorded, so a recorded verification
/// is always `Verified`; the status is still stated explicitly so a reader
/// never has to infer it.
[<RequireQualifiedAccess>]
type VerificationStatus = | Verified

type CheckpointVerification =
    { Status: VerificationStatus
      Mechanism: DurabilityMechanism }

/// A durable checkpoint: the historical fact that, at `RecordedAt`, Praxis
/// observed `Location` and accepted it for `ExecutionId` of `WorkItemId`.
/// It is evidence about recoverability, never a lifecycle state. The
/// representation is private: a value exists only because `verify` accepted
/// fresh observations or `rehydrate` re-checked a stored record, so a
/// checkpoint whose local and remote commits differ, or whose summary is
/// blank, cannot be constructed.
type Checkpoint =
    private
        { workItemId: string
          executionId: string
          stepId: string option
          summary: RequiredText
          nextAction: RequiredText
          recordedAt: string
          location: DurableLocation
          verification: CheckpointVerification }

    member this.WorkItemId = this.workItemId
    member this.ExecutionId = this.executionId
    member this.StepId = this.stepId
    member this.Summary = this.summary.Value
    member this.NextAction = this.nextAction.Value
    member this.RecordedAt = this.recordedAt
    member this.Location = this.location
    member this.Verification = this.verification
    member this.Commit = DurableLocation.commit this.location

/// A checkpoint as stored: its identity is the content-addressed event id of
/// the `work.checkpointed` event that recorded it.
type RecordedCheckpoint =
    { CheckpointId: string
      Recorded: Checkpoint }

/// What the caller asks to checkpoint. Nothing here is trusted: every field
/// is re-checked by `CheckpointVerification.verify`.
type CheckpointCandidate =
    { WorkItemId: string
      Repository: string
      Summary: string
      NextAction: string
      StepId: string option
      OccurredAt: string }

/// Which execution a checkpoint would belong to, as the infrastructure
/// resolved it from the caller's identity. Identity policy stays in the
/// provenance model; the domain only decides what each answer permits.
[<RequireQualifiedAccess>]
type ExecutionObservation =
    | Resolved of executionId: string * startedSteps: string list
    | NoneActive
    | Ambiguous of executionIds: string list
    | Refused of reason: string

/// Everything `verify` needs, observed before any decision. The optional
/// fields are only observed when the previous observation made them
/// meaningful (no upstream means there is no remote branch to read).
type CheckpointObservations =
    { WorkItemState: LiveWorkState option
      Execution: ExecutionObservation
      Head: GitRead<HeadState>
      Upstream: GitRead<UpstreamState> option
      RemoteBranch: RemoteBranchObservation option
      /// Local HEAD (left) against the remote branch head (right), observed
      /// only when they differ.
      LocalToRemote: CommitRelationObservation option
      WorkingTree: GitStatusObservation
      PathFilter: PathFilterConfig
      BaselineDirtyPaths: string list }

[<RequireQualifiedAccess>]
type CheckpointRejection =
    | BlankSummary
    | BlankNextAction
    | WorkItemNotFound of workItemId: string
    | WorkItemNotActive of workItemId: string * state: LiveWorkState
    | NoActiveExecution of workItemId: string
    | AmbiguousExecution of executionIds: string list
    | ExecutionRefused of reason: string
    | InvalidStep of stepId: string * reason: string
    | NotGitRepository
    | GitUnavailable of message: string
    | MalformedGitResponse of message: string
    | UnknownGitState of message: string
    | NoHead of branch: string option
    | DetachedHead of commit: CommitId
    | NoUpstream of branch: string
    | RemoteMissing of remoteName: string * remoteBranch: string
    | RemoteUnreachable of remote: RemoteIdentity * message: string
    | RemoteBranchMissing of remote: RemoteIdentity * remoteBranch: string
    | LocalAhead of count: int * remote: RemoteIdentity * remoteBranch: string
    | RemoteAhead of count: int * remote: RemoteIdentity * remoteBranch: string
    | Diverged of ahead: int * behind: int * remote: RemoteIdentity * remoteBranch: string
    | NotRemotelyVisible of local: CommitId * remoteHead: CommitId * remote: RemoteIdentity * remoteBranch: string
    | UncommittedChanges of paths: string list

[<RequireQualifiedAccess>]
module CheckpointRejection =
    /// A stable, machine-readable cause. These codes are a public contract.
    let code rejection =
        match rejection with
        | CheckpointRejection.BlankSummary -> "blank-summary"
        | CheckpointRejection.BlankNextAction -> "blank-next-action"
        | CheckpointRejection.WorkItemNotFound _ -> "work-item-not-found"
        | CheckpointRejection.WorkItemNotActive _ -> "work-item-not-active"
        | CheckpointRejection.NoActiveExecution _ -> "missing-execution"
        | CheckpointRejection.AmbiguousExecution _ -> "ambiguous-execution"
        | CheckpointRejection.ExecutionRefused _ -> "execution-refused"
        | CheckpointRejection.InvalidStep _ -> "invalid-step"
        | CheckpointRejection.NotGitRepository -> "not-git-repository"
        | CheckpointRejection.GitUnavailable _ -> "git-unavailable"
        | CheckpointRejection.MalformedGitResponse _ -> "malformed-git-response"
        | CheckpointRejection.UnknownGitState _ -> "unknown-git-state"
        | CheckpointRejection.NoHead _ -> "no-head"
        | CheckpointRejection.DetachedHead _ -> "detached-head"
        | CheckpointRejection.NoUpstream _ -> "no-upstream"
        | CheckpointRejection.RemoteMissing _ -> "remote-missing"
        | CheckpointRejection.RemoteUnreachable _ -> "remote-unreachable"
        | CheckpointRejection.RemoteBranchMissing _ -> "remote-branch-missing"
        | CheckpointRejection.LocalAhead _ -> "local-ahead"
        | CheckpointRejection.RemoteAhead _ -> "remote-ahead"
        | CheckpointRejection.Diverged _ -> "diverged"
        | CheckpointRejection.NotRemotelyVisible _ -> "not-remotely-visible"
        | CheckpointRejection.UncommittedChanges _ -> "uncommitted-changes"

    let private remoteRef (remote: RemoteIdentity) (branch: string) = $"{remote.Name}/{branch}"

    let private pathList (paths: string list) =
        let shown = paths |> List.truncate 10 |> String.concat ", "
        if paths.Length > 10 then $"{shown}, and {paths.Length - 10} more" else shown

    let message rejection =
        match rejection with
        | CheckpointRejection.BlankSummary -> "a checkpoint requires a non-blank --summary of the completed work"
        | CheckpointRejection.BlankNextAction -> "a checkpoint of active work requires a non-blank --next-action"
        | CheckpointRejection.WorkItemNotFound id -> $"work item '{id}' is not in repository context"
        | CheckpointRejection.WorkItemNotActive(id, state) ->
            let code =
                match state with
                | LiveWorkState.Ready -> "ready"
                | LiveWorkState.Active -> "active"
                | LiveWorkState.Blocked -> "blocked"
                | LiveWorkState.Complete -> "complete"
                | LiveWorkState.Abandoned -> "abandoned"

            $"work item '{id}' is {code}; only active work can be checkpointed"
        | CheckpointRejection.NoActiveExecution id -> $"no active execution of '{id}' belongs to this process's identity"
        | CheckpointRejection.AmbiguousExecution ids ->
            $"""several active executions could be this process ({String.concat ", " ids})"""
        | CheckpointRejection.ExecutionRefused reason -> reason
        | CheckpointRejection.InvalidStep(stepId, reason) -> $"step '{stepId}' cannot be linked: {reason}"
        | CheckpointRejection.NotGitRepository -> "the repository is not a Git repository, so no Git checkpoint can exist"
        | CheckpointRejection.GitUnavailable message -> $"Git is unavailable: {message}"
        | CheckpointRejection.MalformedGitResponse message -> $"Git answered in a form Praxis could not read: {message}"
        | CheckpointRejection.UnknownGitState message -> $"the Git state could not be determined: {message}"
        | CheckpointRejection.NoHead branch ->
            match branch with
            | Some name -> $"branch '{name}' has no commits yet"
            | None -> "HEAD does not name a commit"
        | CheckpointRejection.DetachedHead commit ->
            $"HEAD is detached at {CommitId.short commit}; a checkpoint needs a named branch that carries the work"
        | CheckpointRejection.NoUpstream branch -> $"branch '{branch}' has no upstream remote branch"
        | CheckpointRejection.RemoteMissing(name, branch) ->
            $"the upstream of this branch names remote '{name}' (branch '{branch}'), which is not configured"
        | CheckpointRejection.RemoteUnreachable(remote, message) -> $"remote '{remote.Name}' could not be reached: {message}"
        | CheckpointRejection.RemoteBranchMissing(remote, branch) -> $"remote branch {remoteRef remote branch} does not exist"
        | CheckpointRejection.LocalAhead(count, remote, branch) ->
            $"local HEAD has {count} commit(s) that {remoteRef remote branch} does not; the candidate commit is not pushed"
        | CheckpointRejection.RemoteAhead(count, remote, branch) ->
            $"{remoteRef remote branch} has {count} commit(s) that local HEAD does not"
        | CheckpointRejection.Diverged(ahead, behind, remote, branch) ->
            $"local HEAD and {remoteRef remote branch} have diverged ({ahead} local-only, {behind} remote-only commit(s))"
        | CheckpointRejection.NotRemotelyVisible(local, remoteHead, remote, branch) ->
            $"{remoteRef remote branch} is at {CommitId.short remoteHead}, not local HEAD {CommitId.short local}, and that remote commit is not present locally"
        | CheckpointRejection.UncommittedChanges paths ->
            $"meaningful uncommitted changes exist and would be hidden by the checkpoint: {pathList paths}"

    /// A safe remedy. None of these discards work or rewrites history; the
    /// executor decides what to commit and push.
    let remedy rejection =
        match rejection with
        | CheckpointRejection.BlankSummary -> "Pass --summary \"what this checkpoint completed\"."
        | CheckpointRejection.BlankNextAction ->
            "Pass --next-action \"the next intended step\" (for a final checkpoint: \"Run final completion transition\")."
        | CheckpointRejection.WorkItemNotFound _ -> "Check the ID with './ros work list'; begin the work item first."
        | CheckpointRejection.WorkItemNotActive _ ->
            "Resume blocked work with 'work resume', or begin ready work with 'work start', then checkpoint."
        | CheckpointRejection.NoActiveExecution _ ->
            "Begin or continue the work under your own identity ('work start' or 'work continue'); never use another executor's execution."
        | CheckpointRejection.AmbiguousExecution _ -> "Name your own execution with --execution EXE-..."
        | CheckpointRejection.ExecutionRefused _ -> "Name an active execution of this work item that is yours with --execution EXE-..."
        | CheckpointRejection.InvalidStep _ ->
            "Start the step in your execution first ('telemetry step start --step ID'), or omit --step."
        | CheckpointRejection.NotGitRepository -> "Work that is not in a Git repository has no Git checkpoint; nothing to do."
        | CheckpointRejection.GitUnavailable _ -> "Install Git or make it available on PATH, then retry."
        | CheckpointRejection.MalformedGitResponse _
        | CheckpointRejection.UnknownGitState _ -> "Inspect 'git status' and 'git remote -v' yourself, then retry."
        | CheckpointRejection.NoHead _ -> "Commit the work, push it, then checkpoint."
        | CheckpointRejection.DetachedHead _ ->
            "Create or switch to a named branch for the work (git switch -c NAME), push it with an upstream, then checkpoint."
        | CheckpointRejection.NoUpstream branch -> $"Push the branch with an upstream (git push -u REMOTE {branch}), then checkpoint."
        | CheckpointRejection.RemoteMissing(name, _) -> $"Configure remote '{name}' (git remote add {name} URL) or set the branch's upstream, then checkpoint."
        | CheckpointRejection.RemoteUnreachable _ -> "Restore access to the remote and retry; an unreachable remote is never treated as durable."
        | CheckpointRejection.RemoteBranchMissing(remote, branch) -> $"Push the branch (git push -u {remote.Name} {branch}), then checkpoint."
        | CheckpointRejection.LocalAhead(_, remote, branch) -> $"Push the commits (git push {remote.Name} HEAD:{branch}), then checkpoint."
        | CheckpointRejection.RemoteAhead(_, remote, _) ->
            $"Fetch ({remote.Name}) and integrate the remote commits yourself (merge or rebase as your branch policy allows), push, then checkpoint."
        | CheckpointRejection.Diverged(_, _, remote, _) ->
            $"Fetch ({remote.Name}) and reconcile the histories yourself without discarding either side, push, then checkpoint."
        | CheckpointRejection.NotRemotelyVisible(_, _, remote, _) ->
            $"Fetch ({remote.Name}) to see what the remote holds, push your commit, then checkpoint."
        | CheckpointRejection.UncommittedChanges _ ->
            "Commit the coherent work and push it, then checkpoint. Praxis never commits, stashes, or discards changes for you."

    /// Validation and argument problems are the caller's to fix (exit 2);
    /// everything else is an observed state that refuses the checkpoint.
    let isArgumentError rejection =
        match rejection with
        | CheckpointRejection.BlankSummary
        | CheckpointRejection.BlankNextAction -> true
        | _ -> false

/// The working tree as it matters for continuity: meaningful dirty paths,
/// with pre-existing baseline paths reported separately and never counted.
[<RequireQualifiedAccess>]
type WorkingTreeState =
    | Clean of excludedBaselinePaths: string list
    | Dirty of meaningfulPaths: string list * excludedBaselinePaths: string list
    | NotRepository
    | Unknown of GitFailure

[<RequireQualifiedAccess>]
module WorkingTreeState =
    let observe (filter: PathFilterConfig) (baseline: string list) (status: GitStatusObservation) =
        match status with
        | GitStatusObservation.Clean -> WorkingTreeState.Clean []
        | GitStatusObservation.Unavailable failure when failure.Reason = GitUnavailableReason.NotRepository ->
            WorkingTreeState.NotRepository
        | GitStatusObservation.Unavailable failure -> WorkingTreeState.Unknown failure
        | GitStatusObservation.Changed changes ->
            let meaningful =
                changes
                |> List.collect GitChangePaths.touchedPaths
                |> List.distinct
                |> PathFilter.meaningfulPaths filter

            let baselineSet = Set.ofList baseline
            let excluded, counted = meaningful |> List.partition baselineSet.Contains
            let sort = List.sortWith (fun (left: string) right -> System.String.CompareOrdinal(left, right))

            if counted.IsEmpty then
                WorkingTreeState.Clean(sort excluded)
            else
                WorkingTreeState.Dirty(sort counted, sort excluded)

    let meaningfulPaths state =
        match state with
        | WorkingTreeState.Dirty(paths, _) -> paths
        | _ -> []

    let excludedBaselinePaths state =
        match state with
        | WorkingTreeState.Clean excluded
        | WorkingTreeState.Dirty(_, excluded) -> excluded
        | _ -> []

[<RequireQualifiedAccess>]
module CheckpointVerification =
    let private gitFailureRejection (failure: GitFailure) =
        match failure.Reason with
        | GitUnavailableReason.NotRepository -> CheckpointRejection.NotGitRepository
        | GitUnavailableReason.ToolUnavailable -> CheckpointRejection.GitUnavailable failure.Message
        | GitUnavailableReason.MalformedOutput -> CheckpointRejection.MalformedGitResponse failure.Message
        | GitUnavailableReason.CommandFailed -> CheckpointRejection.UnknownGitState failure.Message

    let private textRejections (candidate: CheckpointCandidate) =
        [ if (RequiredText.tryCreate candidate.Summary).IsNone then
              CheckpointRejection.BlankSummary
          if (RequiredText.tryCreate candidate.NextAction).IsNone then
              CheckpointRejection.BlankNextAction ]

    let private workRejections (candidate: CheckpointCandidate) (observations: CheckpointObservations) =
        match observations.WorkItemState with
        | None -> [ CheckpointRejection.WorkItemNotFound candidate.WorkItemId ]
        | Some LiveWorkState.Active ->
            match observations.Execution, candidate.StepId with
            | ExecutionObservation.NoneActive, _ -> [ CheckpointRejection.NoActiveExecution candidate.WorkItemId ]
            | ExecutionObservation.Ambiguous ids, _ -> [ CheckpointRejection.AmbiguousExecution ids ]
            | ExecutionObservation.Refused reason, _ -> [ CheckpointRejection.ExecutionRefused reason ]
            | ExecutionObservation.Resolved(executionId, steps), Some stepId when not (List.contains stepId steps) ->
                [ CheckpointRejection.InvalidStep(stepId, $"it was never started in execution '{executionId}'") ]
            | ExecutionObservation.Resolved _, _ -> []
        | Some state -> [ CheckpointRejection.WorkItemNotActive(candidate.WorkItemId, state) ]

    /// The Git side of the invariant `local HEAD == candidate == remote
    /// branch head`. Returns the verified location or the first reason the
    /// candidate is not remotely recoverable.
    let private gitLocation (repository: string) (observations: CheckpointObservations) =
        match observations.Head with
        | GitRead.Unavailable failure -> Error(gitFailureRejection failure)
        | GitRead.Observed(HeadState.Unborn branch) -> Error(CheckpointRejection.NoHead branch)
        | GitRead.Observed(HeadState.Detached commit) -> Error(CheckpointRejection.DetachedHead commit)
        | GitRead.Observed(HeadState.OnBranch(branch, head)) ->
            match observations.Upstream with
            | None -> Error(CheckpointRejection.UnknownGitState "the upstream branch was not observed")
            | Some(GitRead.Unavailable failure) -> Error(gitFailureRejection failure)
            | Some(GitRead.Observed UpstreamState.NoUpstream) -> Error(CheckpointRejection.NoUpstream branch)
            | Some(GitRead.Observed(UpstreamState.RemoteNotConfigured(name, remoteBranch))) ->
                Error(CheckpointRejection.RemoteMissing(name, remoteBranch))
            | Some(GitRead.Observed(UpstreamState.Tracking(remote, remoteBranch))) ->
                let located remoteCommit =
                    { Repository = repository
                      Branch = branch
                      LocalCommit = head
                      Remote = remote
                      RemoteBranch = remoteBranch
                      RemoteCommit = remoteCommit }

                match observations.RemoteBranch with
                | None -> Error(CheckpointRejection.UnknownGitState "the remote branch was not observed")
                | Some(RemoteBranchObservation.Unreachable failure) ->
                    Error(CheckpointRejection.RemoteUnreachable(remote, failure.Message))
                | Some(RemoteBranchObservation.Unavailable failure) -> Error(gitFailureRejection failure)
                | Some RemoteBranchObservation.Missing -> Error(CheckpointRejection.RemoteBranchMissing(remote, remoteBranch))
                | Some(RemoteBranchObservation.At remoteHead) when remoteHead = head -> Ok(located remoteHead)
                | Some(RemoteBranchObservation.At remoteHead) ->
                    match observations.LocalToRemote with
                    | Some(CommitRelationObservation.Related(CommitRelation.Ahead count)) ->
                        Error(CheckpointRejection.LocalAhead(count, remote, remoteBranch))
                    | Some(CommitRelationObservation.Related(CommitRelation.Behind count)) ->
                        Error(CheckpointRejection.RemoteAhead(count, remote, remoteBranch))
                    | Some(CommitRelationObservation.Related(CommitRelation.Diverged(ahead, behind))) ->
                        Error(CheckpointRejection.Diverged(ahead, behind, remote, remoteBranch))
                    | Some(CommitRelationObservation.ObjectMissing _) ->
                        Error(CheckpointRejection.NotRemotelyVisible(head, remoteHead, remote, remoteBranch))
                    | Some(CommitRelationObservation.Unavailable failure) -> Error(gitFailureRejection failure)
                    | Some(CommitRelationObservation.Related CommitRelation.Same)
                    | None ->
                        // The heads are different commits, so "same" is a
                        // contradictory observation; never accept it.
                        Error(CheckpointRejection.NotRemotelyVisible(head, remoteHead, remote, remoteBranch))

    let private treeRejections (observations: CheckpointObservations) =
        match WorkingTreeState.observe observations.PathFilter observations.BaselineDirtyPaths observations.WorkingTree with
        | WorkingTreeState.Dirty(paths, _) -> [ CheckpointRejection.UncommittedChanges paths ]
        | WorkingTreeState.Unknown failure -> [ gitFailureRejection failure ]
        | WorkingTreeState.NotRepository
        | WorkingTreeState.Clean _ -> []

    /// Decides whether a candidate is a durable checkpoint. Every problem
    /// that can be known independently is reported, so the caller can fix
    /// them together; nothing unknown is ever accepted.
    let verify (candidate: CheckpointCandidate) (observations: CheckpointObservations) : Result<Checkpoint, CheckpointRejection list> =
        let text = textRejections candidate
        let work = workRejections candidate observations
        let location = gitLocation candidate.Repository observations

        let tree =
            match location with
            | Error CheckpointRejection.NotGitRepository -> []
            | _ -> treeRejections observations

        let rejections =
            text @ work @ (match location with Error rejection -> [ rejection ] | Ok _ -> []) @ tree
            |> List.distinct

        match rejections, location, observations.Execution with
        | [], Ok git, ExecutionObservation.Resolved(executionId, _) ->
            Ok
                { workItemId = candidate.WorkItemId
                  executionId = executionId
                  stepId = candidate.StepId
                  summary = (RequiredText.tryCreate candidate.Summary).Value
                  nextAction = (RequiredText.tryCreate candidate.NextAction).Value
                  recordedAt = candidate.OccurredAt
                  location = DurableLocation.GitRemoteBranch git
                  verification =
                    { Status = VerificationStatus.Verified
                      Mechanism = DurabilityMechanism.GitRemoteObservation } }
        | [], _, _ -> Error [ CheckpointRejection.UnknownGitState "the checkpoint could not be verified" ]
        | rejections, _, _ -> Error rejections

    /// Only the durability half of `verify`: the same Git invariant (`local
    /// HEAD == remote branch head`, read from the remote itself) and the same
    /// clean-working-tree rule, with no work item or execution. A group
    /// checkpoint (PRX-GRP-044) is held to exactly this. Outside a Git
    /// repository there is no durable location, so that is a rejection here.
    let verifyLocation (repository: string) (observations: CheckpointObservations) : Result<GitDurableLocation, CheckpointRejection list> =
        match gitLocation repository observations, treeRejections observations with
        | Ok location, [] -> Ok location
        | location, tree -> Error((match location with Error rejection -> [ rejection ] | Ok _ -> []) @ tree |> List.distinct)

/// The fields of a stored checkpoint, before they are re-checked.
type StoredCheckpoint =
    { WorkItemId: string
      ExecutionId: string
      StepId: string option
      Summary: string
      NextAction: string
      RecordedAt: string
      Repository: string
      Branch: string
      Commit: string
      RemoteName: string
      RemoteUrl: string option
      RemoteBranch: string
      RemoteCommit: string
      VerificationStatus: string
      Mechanism: string }

[<RequireQualifiedAccess>]
module Checkpoint =
    /// Rebuilds a checkpoint from storage, re-checking every invariant a
    /// freshly verified checkpoint satisfies. A stored record that violates
    /// one is reported, never silently trusted.
    let rehydrate (stored: StoredCheckpoint) : Result<Checkpoint, string list> =
        let commit = CommitId.tryParse stored.Commit
        let remoteCommit = CommitId.tryParse stored.RemoteCommit
        let summary = RequiredText.tryCreate stored.Summary
        let nextAction = RequiredText.tryCreate stored.NextAction
        let mechanism = DurabilityMechanism.tryParse stored.Mechanism

        let problems =
            [ if not (WorkItemId.isValid stored.WorkItemId) then
                  $"workItem '{stored.WorkItemId}' is not a valid work-item ID"
              if not (stored.ExecutionId.StartsWith "EXE-") then
                  $"executionId '{stored.ExecutionId}' is not an execution ID"
              if summary.IsNone then "summary is blank"
              if nextAction.IsNone then "nextAction is blank"
              if System.String.IsNullOrWhiteSpace stored.Branch then "branch is blank"
              if System.String.IsNullOrWhiteSpace stored.RemoteName then "remote is blank"
              if System.String.IsNullOrWhiteSpace stored.RemoteBranch then "remoteBranch is blank"
              if commit.IsNone then $"commit '{stored.Commit}' is not a full commit ID"
              if remoteCommit.IsNone then $"remoteCommit '{stored.RemoteCommit}' is not a full commit ID"
              if commit.IsSome && remoteCommit.IsSome && commit <> remoteCommit then
                  "commit and remoteCommit differ; a checkpoint is accepted only when they are equal"
              if stored.VerificationStatus <> "verified" then
                  $"verification status '{stored.VerificationStatus}' is not 'verified'"
              if mechanism.IsNone then $"verification mechanism '{stored.Mechanism}' is not supported" ]

        match problems with
        | [] ->
            Ok
                { workItemId = stored.WorkItemId
                  executionId = stored.ExecutionId
                  stepId = stored.StepId
                  summary = summary.Value
                  nextAction = nextAction.Value
                  recordedAt = stored.RecordedAt
                  location =
                    DurableLocation.GitRemoteBranch
                        { Repository = stored.Repository
                          Branch = stored.Branch
                          LocalCommit = commit.Value
                          Remote =
                            { Name = stored.RemoteName
                              Url = stored.RemoteUrl }
                          RemoteBranch = stored.RemoteBranch
                          RemoteCommit = remoteCommit.Value }
                  verification =
                    { Status = VerificationStatus.Verified
                      Mechanism = mechanism.Value } }
        | problems -> Error problems

    let store (checkpoint: Checkpoint) : StoredCheckpoint =
        let git = DurableLocation.git checkpoint.Location

        { WorkItemId = checkpoint.WorkItemId
          ExecutionId = checkpoint.ExecutionId
          StepId = checkpoint.StepId
          Summary = checkpoint.Summary
          NextAction = checkpoint.NextAction
          RecordedAt = checkpoint.RecordedAt
          Repository = git.Repository
          Branch = git.Branch
          Commit = git.LocalCommit.Value
          RemoteName = git.Remote.Name
          RemoteUrl = git.Remote.Url
          RemoteBranch = git.RemoteBranch
          RemoteCommit = git.RemoteCommit.Value
          VerificationStatus = "verified"
          Mechanism = DurabilityMechanism.code checkpoint.Verification.Mechanism }

// ---------------------------------------------------------------------------
// Current recoverability and freshness: what is true *now*, observed at read
// time and reported separately from the historical checkpoint fact.
// ---------------------------------------------------------------------------

/// Where local HEAD stands relative to the checkpoint commit.
[<RequireQualifiedAccess>]
type LocalCheckpointPosition =
    | AtCheckpoint
    /// HEAD descends from the checkpoint, and nothing meaningful differs:
    /// the commits after it carry no meaningful change of the item's own: only
    /// Praxis-owned or ignored state, or commits other items' recorded
    /// checkpoints or reconciliations already own (PRAXIS-CONT-12).
    | OnlyNonMeaningfulCommitsAfter of commits: int
    | CommitsAfter of commits: int * meaningfulPaths: string list
    /// HEAD does not contain the checkpoint (another branch, or history the
    /// checkpoint's branch no longer has).
    | HeadDoesNotContain of head: CommitId
    /// The checkpoint commit is not present locally; fetch to know more.
    | CheckpointNotPresent
    | NoHead
    | Unknown of message: string

/// Whether the checkpoint is still recoverable from its recorded remote
/// branch *now*. Never derived from the historical record.
[<RequireQualifiedAccess>]
type CurrentRecoverability =
    | AtRemoteHead
    /// The remote branch moved past the checkpoint only by commits that
    /// change no meaningful path (for example, persisted Praxis state).
    | ContainedWithoutMeaningfulChange of remoteHead: CommitId * commits: int
    /// The remote branch still contains the checkpoint but has meaningful
    /// commits after it.
    | RemoteAdvanced of remoteHead: CommitId * commits: int * meaningfulPaths: string list
    /// The remote branch moved to a commit whose ancestry is not known
    /// locally; fetching would tell.
    | RemoteMovedAncestryUnknown of remoteHead: CommitId
    /// The remote branch no longer contains the checkpoint (rewritten or
    /// force-pushed history).
    | NotContained of remoteHead: CommitId
    | RemoteBranchMissing
    | RemoteUnreachable of message: string
    | Unknown of message: string

[<RequireQualifiedAccess>]
module CurrentRecoverability =
    let code value =
        match value with
        | CurrentRecoverability.AtRemoteHead -> "at-remote-head"
        | CurrentRecoverability.ContainedWithoutMeaningfulChange _ -> "contained-without-meaningful-change"
        | CurrentRecoverability.RemoteAdvanced _ -> "remote-advanced"
        | CurrentRecoverability.RemoteMovedAncestryUnknown _ -> "remote-moved-ancestry-unknown"
        | CurrentRecoverability.NotContained _ -> "not-contained"
        | CurrentRecoverability.RemoteBranchMissing -> "remote-branch-missing"
        | CurrentRecoverability.RemoteUnreachable _ -> "remote-unreachable"
        | CurrentRecoverability.Unknown _ -> "unknown"

    /// Whether the checkpoint commit can currently be fetched from the
    /// recorded remote branch.
    let isRecoverable value =
        match value with
        | CurrentRecoverability.AtRemoteHead
        | CurrentRecoverability.ContainedWithoutMeaningfulChange _
        | CurrentRecoverability.RemoteAdvanced _ -> Some true
        | CurrentRecoverability.NotContained _
        | CurrentRecoverability.RemoteBranchMissing -> Some false
        | CurrentRecoverability.RemoteMovedAncestryUnknown _
        | CurrentRecoverability.RemoteUnreachable _
        | CurrentRecoverability.Unknown _ -> None

    /// Whether the remote branch still verifies the checkpoint as the
    /// recoverable state of the work: the head, or only non-meaningful
    /// commits after it.
    let verifiesCheckpoint value =
        match value with
        | CurrentRecoverability.AtRemoteHead
        | CurrentRecoverability.ContainedWithoutMeaningfulChange _ -> true
        | _ -> false


[<RequireQualifiedAccess>]
module LocalCheckpointPosition =
    let code value =
        match value with
        | LocalCheckpointPosition.AtCheckpoint -> "at-checkpoint"
        | LocalCheckpointPosition.OnlyNonMeaningfulCommitsAfter _ -> "only-non-meaningful-commits-after"
        | LocalCheckpointPosition.CommitsAfter _ -> "commits-after-checkpoint"
        | LocalCheckpointPosition.HeadDoesNotContain _ -> "head-does-not-contain-checkpoint"
        | LocalCheckpointPosition.CheckpointNotPresent -> "checkpoint-not-present-locally"
        | LocalCheckpointPosition.NoHead -> "no-head"
        | LocalCheckpointPosition.Unknown _ -> "unknown"

    /// Whether HEAD's meaningful content is exactly the checkpoint's.
    let isEffectivelyAtCheckpoint value =
        match value with
        | LocalCheckpointPosition.AtCheckpoint
        | LocalCheckpointPosition.OnlyNonMeaningfulCommitsAfter _ -> true
        | _ -> false

[<RequireQualifiedAccess>]
type CheckpointFreshness =
    | None
    | Current
    | CommitsAfterCheckpoint
    | UncommittedChangesAfterCheckpoint
    | RemoteUnavailable
    | RemoteMoved
    | NoLongerCurrentlyVerifiable
    | HeadDivergedFromCheckpoint
    | Unknown

[<RequireQualifiedAccess>]
module CheckpointFreshness =
    let code value =
        match value with
        | CheckpointFreshness.None -> "none"
        | CheckpointFreshness.Current -> "current"
        | CheckpointFreshness.CommitsAfterCheckpoint -> "commits-after-checkpoint"
        | CheckpointFreshness.UncommittedChangesAfterCheckpoint -> "uncommitted-changes-after-checkpoint"
        | CheckpointFreshness.RemoteUnavailable -> "remote-unavailable"
        | CheckpointFreshness.RemoteMoved -> "remote-moved"
        | CheckpointFreshness.NoLongerCurrentlyVerifiable -> "checkpoint-no-longer-currently-verifiable"
        | CheckpointFreshness.HeadDivergedFromCheckpoint -> "head-diverged-from-checkpoint"
        | CheckpointFreshness.Unknown -> "unknown"

/// A continuity risk stated as an observed fact.
[<RequireQualifiedAccess>]
type ContinuityWarning =
    | NoDurableCheckpoint of workItemId: string
    | CommitsAfterCheckpoint of workItemId: string * commits: int
    | UncommittedWorkAfterCheckpoint of workItemId: string * paths: string list
    | UncommittedWorkWithoutCheckpoint of workItemId: string * paths: string list
    | CheckpointNotRemoteHead of workItemId: string * remoteBranch: string
    | CheckpointNoLongerVerifiable of workItemId: string * reason: string
    | HeadDoesNotContainCheckpoint of workItemId: string
    | RemoteUnavailable of workItemId: string * message: string
    | StateUnknown of workItemId: string * message: string

[<RequireQualifiedAccess>]
module ContinuityWarning =
    let code warning =
        match warning with
        | ContinuityWarning.NoDurableCheckpoint _ -> "no-durable-checkpoint"
        | ContinuityWarning.CommitsAfterCheckpoint _ -> "commits-after-checkpoint"
        | ContinuityWarning.UncommittedWorkAfterCheckpoint _ -> "uncommitted-work-after-checkpoint"
        | ContinuityWarning.UncommittedWorkWithoutCheckpoint _ -> "uncommitted-work-without-checkpoint"
        | ContinuityWarning.CheckpointNotRemoteHead _ -> "checkpoint-not-remote-head"
        | ContinuityWarning.CheckpointNoLongerVerifiable _ -> "checkpoint-no-longer-verifiable"
        | ContinuityWarning.HeadDoesNotContainCheckpoint _ -> "head-does-not-contain-checkpoint"
        | ContinuityWarning.RemoteUnavailable _ -> "remote-unavailable"
        | ContinuityWarning.StateUnknown _ -> "state-unknown"

    let workItemId warning =
        match warning with
        | ContinuityWarning.NoDurableCheckpoint id
        | ContinuityWarning.CommitsAfterCheckpoint(id, _)
        | ContinuityWarning.UncommittedWorkAfterCheckpoint(id, _)
        | ContinuityWarning.UncommittedWorkWithoutCheckpoint(id, _)
        | ContinuityWarning.CheckpointNotRemoteHead(id, _)
        | ContinuityWarning.CheckpointNoLongerVerifiable(id, _)
        | ContinuityWarning.HeadDoesNotContainCheckpoint id
        | ContinuityWarning.RemoteUnavailable(id, _)
        | ContinuityWarning.StateUnknown(id, _) -> id

    let message warning =
        match warning with
        | ContinuityWarning.NoDurableCheckpoint id -> $"active work {id} has no durable checkpoint"
        | ContinuityWarning.CommitsAfterCheckpoint(id, commits) ->
            $"{id}: HEAD contains {commits} commit(s) with meaningful changes after the latest durable checkpoint"
        | ContinuityWarning.UncommittedWorkAfterCheckpoint(id, paths) ->
            $"{id}: meaningful uncommitted work exists after the latest checkpoint ({paths.Length} path(s))"
        | ContinuityWarning.UncommittedWorkWithoutCheckpoint(id, paths) ->
            $"{id}: meaningful uncommitted work exists and no durable checkpoint covers it ({paths.Length} path(s))"
        | ContinuityWarning.CheckpointNotRemoteHead(id, remoteBranch) ->
            $"{id}: the previously verified checkpoint is no longer the head of its recorded remote branch {remoteBranch}"
        | ContinuityWarning.CheckpointNoLongerVerifiable(id, reason) ->
            $"{id}: the latest checkpoint is no longer currently verifiable ({reason})"
        | ContinuityWarning.HeadDoesNotContainCheckpoint id -> $"{id}: local HEAD does not contain the latest checkpoint commit"
        | ContinuityWarning.RemoteUnavailable(id, message) -> $"{id}: the checkpoint's remote could not be observed ({message})"
        | ContinuityWarning.StateUnknown(id, message) -> $"{id}: continuity state is unknown ({message})"

/// What was observed about the repository relative to one checkpoint. The
/// application layer fills in only what the previous observations made
/// meaningful.
type RecoverabilityObservations =
    { Head: GitRead<HeadState>
      WorkingTree: GitStatusObservation
      PathFilter: PathFilterConfig
      BaselineDirtyPaths: string list
      /// Checkpoint commit (left) against local HEAD (right).
      HeadRelation: CommitRelationObservation option
      /// Raw paths that differ between the checkpoint commit and HEAD.
      HeadDiff: GitRead<string list> option
      /// The recorded remote branch's head now.
      RemoteBranch: RemoteBranchObservation option
      /// Checkpoint commit (left) against the remote head (right).
      RemoteRelation: CommitRelationObservation option
      /// Raw paths that differ between the checkpoint commit and the remote head.
      RemoteDiff: GitRead<string list> option }

type CheckpointAssessment =
    { Checkpoint: RecordedCheckpoint option
      Local: LocalCheckpointPosition option
      WorkingTree: WorkingTreeState
      CurrentRecoverability: CurrentRecoverability option
      Freshness: CheckpointFreshness
      Warnings: ContinuityWarning list }

[<RequireQualifiedAccess>]
module CheckpointAssessment =
    let private meaningful filter (read: GitRead<string list> option) =
        match read with
        | Some(GitRead.Observed paths) -> Ok(PathFilter.meaningfulPaths filter paths |> List.distinct |> List.sort)
        | Some(GitRead.Unavailable failure) -> Error failure.Message
        | None -> Error "the difference was not observed"

    let private headCommit head =
        match head with
        | GitRead.Observed(HeadState.OnBranch(_, commit))
        | GitRead.Observed(HeadState.Detached commit) -> Some commit
        | _ -> None

    let localPosition (checkpoint: Checkpoint) (observations: RecoverabilityObservations) =
        match observations.Head with
        | GitRead.Unavailable failure -> LocalCheckpointPosition.Unknown failure.Message
        | GitRead.Observed(HeadState.Unborn _) -> LocalCheckpointPosition.NoHead
        | head ->
            match headCommit head with
            | None -> LocalCheckpointPosition.Unknown "HEAD was not observed"
            | Some commit when commit = checkpoint.Commit -> LocalCheckpointPosition.AtCheckpoint
            | Some commit ->
                match observations.HeadRelation with
                | Some(CommitRelationObservation.Related(CommitRelation.Behind count)) ->
                    match meaningful observations.PathFilter observations.HeadDiff with
                    | Ok [] -> LocalCheckpointPosition.OnlyNonMeaningfulCommitsAfter count
                    | Ok paths -> LocalCheckpointPosition.CommitsAfter(count, paths)
                    | Error message -> LocalCheckpointPosition.Unknown message
                | Some(CommitRelationObservation.Related(CommitRelation.Ahead _))
                | Some(CommitRelationObservation.Related(CommitRelation.Diverged _)) ->
                    LocalCheckpointPosition.HeadDoesNotContain commit
                | Some(CommitRelationObservation.Related CommitRelation.Same) -> LocalCheckpointPosition.AtCheckpoint
                | Some(CommitRelationObservation.ObjectMissing _) -> LocalCheckpointPosition.CheckpointNotPresent
                | Some(CommitRelationObservation.Unavailable failure) -> LocalCheckpointPosition.Unknown failure.Message
                | None -> LocalCheckpointPosition.Unknown "the relation between HEAD and the checkpoint was not observed"

    let currentRecoverability (checkpoint: Checkpoint) (observations: RecoverabilityObservations) =
        match observations.RemoteBranch with
        | None -> CurrentRecoverability.Unknown "the remote branch was not observed"
        | Some(RemoteBranchObservation.Unreachable failure) -> CurrentRecoverability.RemoteUnreachable failure.Message
        | Some(RemoteBranchObservation.Unavailable failure) -> CurrentRecoverability.Unknown failure.Message
        | Some RemoteBranchObservation.Missing -> CurrentRecoverability.RemoteBranchMissing
        | Some(RemoteBranchObservation.At remoteHead) when remoteHead = checkpoint.Commit -> CurrentRecoverability.AtRemoteHead
        | Some(RemoteBranchObservation.At remoteHead) ->
            match observations.RemoteRelation with
            | Some(CommitRelationObservation.Related(CommitRelation.Behind count)) ->
                match meaningful observations.PathFilter observations.RemoteDiff with
                | Ok [] -> CurrentRecoverability.ContainedWithoutMeaningfulChange(remoteHead, count)
                | Ok paths -> CurrentRecoverability.RemoteAdvanced(remoteHead, count, paths)
                | Error message -> CurrentRecoverability.Unknown message
            | Some(CommitRelationObservation.Related CommitRelation.Same) -> CurrentRecoverability.AtRemoteHead
            | Some(CommitRelationObservation.Related(CommitRelation.Ahead _))
            | Some(CommitRelationObservation.Related(CommitRelation.Diverged _)) -> CurrentRecoverability.NotContained remoteHead
            | Some(CommitRelationObservation.ObjectMissing _)
            | None -> CurrentRecoverability.RemoteMovedAncestryUnknown remoteHead
            | Some(CommitRelationObservation.Unavailable failure) -> CurrentRecoverability.Unknown failure.Message

    let private freshnessOf local (tree: WorkingTreeState) recoverability =
        match local, tree, recoverability with
        | LocalCheckpointPosition.Unknown _, _, _
        | LocalCheckpointPosition.NoHead, _, _
        | _, WorkingTreeState.Unknown _, _ -> CheckpointFreshness.Unknown
        | LocalCheckpointPosition.HeadDoesNotContain _, _, _
        | LocalCheckpointPosition.CheckpointNotPresent, _, _ -> CheckpointFreshness.HeadDivergedFromCheckpoint
        | LocalCheckpointPosition.CommitsAfter _, _, _ -> CheckpointFreshness.CommitsAfterCheckpoint
        | _, WorkingTreeState.Dirty _, _ -> CheckpointFreshness.UncommittedChangesAfterCheckpoint
        | _, _, CurrentRecoverability.RemoteUnreachable _ -> CheckpointFreshness.RemoteUnavailable
        | _, _, CurrentRecoverability.NotContained _
        | _, _, CurrentRecoverability.RemoteBranchMissing -> CheckpointFreshness.NoLongerCurrentlyVerifiable
        | _, _, CurrentRecoverability.RemoteAdvanced _
        | _, _, CurrentRecoverability.RemoteMovedAncestryUnknown _ -> CheckpointFreshness.RemoteMoved
        | _, _, CurrentRecoverability.Unknown _ -> CheckpointFreshness.Unknown
        | _, _, CurrentRecoverability.AtRemoteHead
        | _, _, CurrentRecoverability.ContainedWithoutMeaningfulChange _ -> CheckpointFreshness.Current

    let private warningsFor (workItemId: string) (remoteBranch: string) local tree recoverability =
        [ match local with
          | LocalCheckpointPosition.CommitsAfter(count, _) -> ContinuityWarning.CommitsAfterCheckpoint(workItemId, count)
          | LocalCheckpointPosition.HeadDoesNotContain _
          | LocalCheckpointPosition.CheckpointNotPresent -> ContinuityWarning.HeadDoesNotContainCheckpoint workItemId
          | LocalCheckpointPosition.Unknown message -> ContinuityWarning.StateUnknown(workItemId, message)
          | _ -> ()
          match tree with
          | WorkingTreeState.Dirty(paths, _) -> ContinuityWarning.UncommittedWorkAfterCheckpoint(workItemId, paths)
          | WorkingTreeState.Unknown failure -> ContinuityWarning.StateUnknown(workItemId, failure.Message)
          | _ -> ()
          match recoverability with
          | CurrentRecoverability.RemoteAdvanced _
          | CurrentRecoverability.RemoteMovedAncestryUnknown _ -> ContinuityWarning.CheckpointNotRemoteHead(workItemId, remoteBranch)
          | CurrentRecoverability.NotContained _ ->
              ContinuityWarning.CheckpointNoLongerVerifiable(workItemId, "the remote branch no longer contains it")
          | CurrentRecoverability.RemoteBranchMissing ->
              ContinuityWarning.CheckpointNoLongerVerifiable(workItemId, "the remote branch no longer exists")
          | CurrentRecoverability.RemoteUnreachable message -> ContinuityWarning.RemoteUnavailable(workItemId, message)
          | CurrentRecoverability.Unknown message -> ContinuityWarning.StateUnknown(workItemId, message)
          | _ -> () ]

    /// Assesses one work item's continuity. With no checkpoint, active work
    /// is warned about; there is nothing to be fresh relative to.
    let assess (workItemId: string) (itemState: LiveWorkState) (checkpoint: RecordedCheckpoint option) (observations: RecoverabilityObservations) =
        let tree = WorkingTreeState.observe observations.PathFilter observations.BaselineDirtyPaths observations.WorkingTree

        match checkpoint with
        | None ->
            { Checkpoint = None
              Local = None
              WorkingTree = tree
              CurrentRecoverability = None
              Freshness = CheckpointFreshness.None
              Warnings =
                if itemState = LiveWorkState.Active then
                    [ ContinuityWarning.NoDurableCheckpoint workItemId
                      match tree with
                      | WorkingTreeState.Dirty(paths, _) -> ContinuityWarning.UncommittedWorkWithoutCheckpoint(workItemId, paths)
                      | _ -> () ]
                else
                    [] }
        | Some recorded ->
            let local = localPosition recorded.Recorded observations
            let recoverability = currentRecoverability recorded.Recorded observations
            let git = DurableLocation.git recorded.Recorded.Location

            { Checkpoint = Some recorded
              Local = Some local
              WorkingTree = tree
              CurrentRecoverability = Some recoverability
              Freshness = freshnessOf local tree recoverability
              Warnings =
                if itemState = LiveWorkState.Complete || itemState = LiveWorkState.Abandoned then
                    []
                else
                    warningsFor workItemId $"{git.Remote.Name}/{git.RemoteBranch}" local tree recoverability }
