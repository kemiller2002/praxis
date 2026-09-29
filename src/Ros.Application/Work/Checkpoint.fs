namespace Ros.Application.Work

open Ros.Application.Git
open Ros.Domain.Git
open Ros.Domain.Work

/// Repository facts that continuity decisions share: which paths are
/// meaningful and which dirty paths predate the work.
type ContinuityPolicy =
    { PathFilter: PathFilterConfig
      BaselineDirtyPaths: string list }

/// Gathers the typed observations the pure checkpoint decisions need,
/// through the `GitDurability` port, in dependency order: a read that the
/// previous answer made meaningless is not made (no upstream, no remote
/// read). Nothing here decides legality and nothing here writes.
[<RequireQualifiedAccess>]
module CheckpointObservation =
    let private headCommit head =
        match head with
        | GitRead.Observed(HeadState.OnBranch(_, commit))
        | GitRead.Observed(HeadState.Detached commit) -> Some commit
        | _ -> None

    /// The observations `CheckpointVerification.verify` needs for a candidate.
    let candidate (git: GitDurability) (policy: ContinuityPolicy) (itemState: LiveWorkState option) (execution: ExecutionObservation) =
        let head = git.Head()

        let upstream =
            match head with
            | GitRead.Observed(HeadState.OnBranch(branch, _)) -> Some(git.Upstream branch)
            | _ -> None

        let remoteBranch =
            match upstream with
            | Some(GitRead.Observed(UpstreamState.Tracking(remote, branch))) -> Some(git.RemoteBranch remote branch)
            | _ -> None

        let localToRemote =
            match headCommit head, remoteBranch with
            | Some local, Some(RemoteBranchObservation.At remote) when local <> remote -> Some(git.Relation local remote)
            | _ -> None

        { WorkItemState = itemState
          Execution = execution
          Head = head
          Upstream = upstream
          RemoteBranch = remoteBranch
          LocalToRemote = localToRemote
          WorkingTree = git.Status()
          PathFilter = policy.PathFilter
          BaselineDirtyPaths = policy.BaselineDirtyPaths }

    /// The recorded remote, as it is configured now. A remote that has been
    /// removed or renamed cannot be read, which is reported as unreachable,
    /// never as success.
    let private recordedRemote (git: GitDurability) (location: GitDurableLocation) =
        match git.Remote location.Remote.Name with
        | GitRead.Observed(Some remote) -> Ok remote
        | GitRead.Observed None ->
            Error(
                RemoteBranchObservation.Unreachable
                    { Operation = "git remote"
                      Reason = GitUnavailableReason.CommandFailed
                      Message = $"remote '{location.Remote.Name}' is no longer configured"
                      ExitCode = None }
            )
        | GitRead.Unavailable failure -> Error(RemoteBranchObservation.Unavailable failure)

    /// What is true now about the repository relative to one checkpoint (or,
    /// with none, only the working tree).
    let recoverability (git: GitDurability) (policy: ContinuityPolicy) (checkpoint: RecordedCheckpoint option) =
        let head = git.Head()
        let status = git.Status()

        let empty =
            { Head = head
              WorkingTree = status
              PathFilter = policy.PathFilter
              BaselineDirtyPaths = policy.BaselineDirtyPaths
              HeadRelation = None
              HeadDiff = None
              RemoteBranch = None
              RemoteRelation = None
              RemoteDiff = None }

        match checkpoint with
        | None -> empty
        | Some recorded ->
            let location = DurableLocation.git recorded.Recorded.Location
            let checkpointCommit = recorded.Recorded.Commit

            let headRelation, headDiff =
                match headCommit head with
                | Some local when local <> checkpointCommit ->
                    let relation = git.Relation checkpointCommit local

                    match relation with
                    | CommitRelationObservation.Related(CommitRelation.Behind _) ->
                        Some relation, Some(git.ChangedPaths checkpointCommit local)
                    | _ -> Some relation, None
                | _ -> None, None

            let remoteBranch =
                match recordedRemote git location with
                | Ok remote -> git.RemoteBranch remote location.RemoteBranch
                | Error observation -> observation

            let remoteRelation, remoteDiff =
                match remoteBranch with
                | RemoteBranchObservation.At remoteHead when remoteHead <> checkpointCommit ->
                    let relation = git.Relation checkpointCommit remoteHead

                    match relation with
                    | CommitRelationObservation.Related(CommitRelation.Behind _) ->
                        Some relation, Some(git.ChangedPaths checkpointCommit remoteHead)
                    | _ -> Some relation, None
                | _ -> None, None

            { empty with
                HeadRelation = headRelation
                HeadDiff = headDiff
                RemoteBranch = Some remoteBranch
                RemoteRelation = remoteRelation
                RemoteDiff = remoteDiff }

    /// Whether the work item changed the repository meaningfully since its
    /// recorded start commit.
    let mutation (git: GitDurability) (policy: ContinuityPolicy) (startCommit: CommitId option) (tree: WorkingTreeState) =
        let startToHead =
            match startCommit, git.Head() with
            | None, _ -> None
            | Some _, GitRead.Unavailable failure -> Some(GitRead.Unavailable failure)
            | Some start, head ->
                match headCommit head with
                | Some current when current = start -> Some(GitRead.Observed [])
                | Some current -> Some(git.ChangedPaths start current)
                | None -> Some(GitRead.Observed [])

        WorkMutation.observe policy.PathFilter tree startToHead

/// Composes observation and decision for each continuity question. Pure
/// functions of the port: the same answers from Git give the same result.
[<RequireQualifiedAccess>]
module CheckpointOperations =
    let verify (git: GitDurability) (policy: ContinuityPolicy) (candidate: CheckpointCandidate) (itemState: LiveWorkState option) (execution: ExecutionObservation) =
        CheckpointObservation.candidate git policy itemState execution
        |> CheckpointVerification.verify candidate

    let assess (git: GitDurability) (policy: ContinuityPolicy) (workItemId: string) (itemState: LiveWorkState) (checkpoint: RecordedCheckpoint option) =
        CheckpointObservation.recoverability git policy checkpoint
        |> CheckpointAssessment.assess workItemId itemState checkpoint

    let decideCompletion (git: GitDurability) (policy: ContinuityPolicy) (workItemId: string) (checkpoint: RecordedCheckpoint option) (startCommit: CommitId option) =
        let assessment = assess git policy workItemId LiveWorkState.Active checkpoint
        let mutation = CheckpointObservation.mutation git policy startCommit assessment.WorkingTree
        CompletionGuard.decide mutation assessment, assessment

    let decideBlock (git: GitDurability) (policy: ContinuityPolicy) (workItemId: string) (checkpoint: RecordedCheckpoint option) (startCommit: CommitId option) (reason: string option) =
        let assessment = assess git policy workItemId LiveWorkState.Active checkpoint
        let mutation = CheckpointObservation.mutation git policy startCommit assessment.WorkingTree
        BlockGuard.decide mutation assessment reason, assessment

    /// The meaningful paths a checkpoint makes durable: what changed from
    /// the item's previous checkpoint (or, for the first, its start commit)
    /// to the checkpoint commit. Recorded on the event so path attribution
    /// survives completing work after committing it. Baseline paths are
    /// never claimed. An unknown start or an unreadable difference claims
    /// nothing.
    let attributablePaths (git: GitDurability) (policy: ContinuityPolicy) (since: CommitId option) (checkpoint: Checkpoint) =
        match since with
        | None -> []
        | Some start when start = checkpoint.Commit -> []
        | Some start ->
            match git.ChangedPaths start checkpoint.Commit with
            | GitRead.Observed paths ->
                PathFilter.meaningfulPaths policy.PathFilter paths
                |> List.filter (fun path -> not (List.contains path policy.BaselineDirtyPaths))
                |> List.distinct
                |> List.sortWith (fun left right -> System.String.CompareOrdinal(left, right))
            | GitRead.Unavailable _ -> []
