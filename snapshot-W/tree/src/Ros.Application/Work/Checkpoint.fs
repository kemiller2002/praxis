namespace Ros.Application.Work

open Ros.Application.Git
open Ros.Domain.Git
open Ros.Domain.Work

/// Commit-level history reads the ownership rule needs (PRAXIS-CONT-12).
type GitCommitHistory =
    { /// Each commit reachable from the second but not the first, with its
      /// own changes (none for a merge).
      Changes: CommitId -> CommitId -> GitRead<CommitChange list>
      /// The commits reachable from the second but not the first.
      Reachable: CommitId -> CommitId -> GitRead<string list> }

/// Recorded evidence of which commits other work items own: their durable
/// checkpoints, and the history reads that relate them to a range.
type OwnershipEvidence =
    { History: GitCommitHistory
      Claims: CheckpointClaim list }

/// Repository facts that continuity decisions share: which paths are
/// meaningful, which dirty paths predate the work, and (when available)
/// which commits other work items' checkpoints already own.
type ContinuityPolicy =
    { PathFilter: PathFilterConfig
      BaselineDirtyPaths: string list
      Ownership: OwnershipEvidence option }

/// A work item's own changes between two commits (PRAXIS-CONT-12). A raw
/// difference on a shared branch also contains other items' checkpointed
/// work; this removes the commits another item's checkpoint already owns.
/// Without ownership evidence, or when no other item's checkpoint covers any
/// commit in the range, it is exactly the raw difference; if the commit
/// history cannot be read it falls back to the raw difference, which claims
/// more rather than less.
[<RequireQualifiedAccess>]
module CheckpointOwnership =
    let changedPaths (git: GitDurability) (policy: ContinuityPolicy) (workItemId: string) (left: CommitId) (right: CommitId) : GitRead<string list> =
        let raw () = git.ChangedPaths left right

        match policy.Ownership with
        | None -> raw ()
        | Some evidence ->
            match evidence.Claims |> List.filter (fun claim -> claim.WorkItemId <> workItemId) with
            | [] -> raw ()
            | others ->
                match evidence.History.Changes left right with
                | GitRead.Unavailable _ -> raw ()
                | GitRead.Observed changes ->
                    let inRange = changes |> List.map (fun change -> change.Commit) |> Set.ofList

                    let coverage =
                        others
                        |> List.choose (fun claim ->
                            match CommitId.tryParse claim.Commit with
                            | None -> None
                            | Some commit ->
                                match evidence.History.Reachable left commit with
                                | GitRead.Observed commits -> Some(claim.WorkItemId, commits |> List.filter inRange.Contains |> Set.ofList)
                                | GitRead.Unavailable _ -> None)

                    let covering (commit: string) =
                        coverage |> List.filter (fun (_, commits) -> commits.Contains commit) |> List.map fst |> List.distinct

                    if changes |> List.forall (fun change -> (covering change.Commit).IsEmpty) then
                        raw ()
                    else
                        GitRead.Observed(CommitOwnership.ownPaths policy.PathFilter others covering changes)

    /// The port as seen by one work item: differences are its own changes.
    let scope (git: GitDurability) (policy: ContinuityPolicy) (workItemId: string) : GitDurability =
        { git with ChangedPaths = changedPaths git policy workItemId }

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
            // No commit exists at all, so nothing can have been committed.
            | None, GitRead.Observed(HeadState.Unborn _) -> Some(GitRead.Observed [])
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

    /// `verify`'s durability check alone, for a checkpoint that is not one
    /// work item's (a group checkpoint): the same observations and the same
    /// Git rules, with no work item and no execution.
    let verifyDurableLocation (git: GitDurability) (policy: ContinuityPolicy) (candidate: CheckpointCandidate) =
        CheckpointObservation.candidate git policy None ExecutionObservation.NoneActive
        |> CheckpointVerification.durableLocation candidate

    let assess (git: GitDurability) (policy: ContinuityPolicy) (workItemId: string) (itemState: LiveWorkState) (checkpoint: RecordedCheckpoint option) =
        CheckpointObservation.recoverability (CheckpointOwnership.scope git policy workItemId) policy checkpoint
        |> CheckpointAssessment.assess workItemId itemState checkpoint

    let decideCompletion (git: GitDurability) (policy: ContinuityPolicy) (workItemId: string) (checkpoint: RecordedCheckpoint option) (startCommit: CommitId option) =
        let assessment = assess git policy workItemId LiveWorkState.Active checkpoint
        let mutation = CheckpointObservation.mutation (CheckpointOwnership.scope git policy workItemId) policy startCommit assessment.WorkingTree
        CompletionGuard.decide mutation assessment, assessment

    let decideBlock (git: GitDurability) (policy: ContinuityPolicy) (workItemId: string) (checkpoint: RecordedCheckpoint option) (startCommit: CommitId option) (reason: string option) =
        let assessment = assess git policy workItemId LiveWorkState.Active checkpoint
        let mutation = CheckpointObservation.mutation (CheckpointOwnership.scope git policy workItemId) policy startCommit assessment.WorkingTree
        BlockGuard.decide mutation assessment reason, assessment

    /// The meaningful paths a checkpoint makes durable: what this item's own
    /// commits changed from its previous checkpoint (or, for the first, its
    /// start commit) to the checkpoint commit; commits another item's
    /// checkpoint already owns are not claimed (PRAXIS-CONT-12). Recorded on
    /// the event so path attribution survives completing work after
    /// committing it. Baseline paths are never claimed. An unknown start or
    /// an unreadable difference claims nothing.
    let attributablePaths (git: GitDurability) (policy: ContinuityPolicy) (since: CommitId option) (checkpoint: Checkpoint) =
        match since with
        | None -> []
        | Some start when start = checkpoint.Commit -> []
        | Some start ->
            match CheckpointOwnership.changedPaths git policy checkpoint.WorkItemId start checkpoint.Commit with
            | GitRead.Observed paths ->
                PathFilter.meaningfulPaths policy.PathFilter paths
                |> List.filter (fun path -> not (List.contains path policy.BaselineDirtyPaths))
                |> List.distinct
                |> List.sortWith (fun left right -> System.String.CompareOrdinal(left, right))
            | GitRead.Unavailable _ -> []
