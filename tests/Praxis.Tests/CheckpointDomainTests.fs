namespace Praxis.Tests

open Praxis.Domain.Git
open Praxis.Domain.Work

/// Pure decision rules of durable checkpoints (RQ-ROS-2026-A022,
/// PRAXIS-CONT-01-DOMAIN): every observation is a typed value, so each rule
/// is exercised without Git, a filesystem, or a network.
[<RequireQualifiedAccess>]
module CheckpointDomainTests =
    let private sha (digit: char) = CommitId.tryParse (System.String(digit, 40)) |> Option.get
    let private c1 = sha 'a'
    let private c2 = sha 'b'
    let private c3 = sha 'c'
    let private origin = { Name = "origin"; Url = Some "https://example.test/repo.git" }

    let private failure reason message =
        { Operation = "git"; Reason = reason; Message = message; ExitCode = Some 128 }

    let private filter =
        { PathFilterConfig.defaultConfig with IgnoredPatterns = [ ".ros/**"; "registries/**" ] }

    let private candidate =
        { WorkItemId = "PRAXIS-CONT-01"
          Repository = "example"
          Summary = "Implemented capability boundary"
          NextAction = "Implement Rust consumer fixture"
          StepId = None
          OccurredAt = "2026-09-29T10:00:00.000Z" }

    /// A healthy observation: active item, own execution, HEAD on a pushed
    /// branch equal to the remote head, clean tree.
    let private healthy =
        { WorkItemState = Some LiveWorkState.Active
          Execution = ExecutionObservation.Resolved("EXE-A", [ "step-1" ])
          Head = GitRead.Observed(HeadState.OnBranch("feature/x", c1))
          Upstream = Some(GitRead.Observed(UpstreamState.Tracking(origin, "feature/x")))
          RemoteBranch = Some(RemoteBranchObservation.At c1)
          LocalToRemote = None
          WorkingTree = GitStatusObservation.Clean
          PathFilter = filter
          BaselineDirtyPaths = [] }

    let private change path =
        { Status = GitChangeStatus.Tracked(GitDelta.Unmodified, GitDelta.Modified)
          Path = path
          OriginalPath = None }

    let private rejected observations cand =
        match CheckpointVerification.verify cand observations with
        | Ok _ -> failwith "expected a rejection"
        | Error rejections -> rejections |> List.map CheckpointRejection.code

    let private remoteAt relation remoteHead =
        { healthy with
            RemoteBranch = Some(RemoteBranchObservation.At remoteHead)
            LocalToRemote = Some relation }

    let private recorded () =
        match CheckpointVerification.verify candidate healthy with
        | Ok checkpoint -> { CheckpointId = "evt-1"; Recorded = checkpoint }
        | Error rejections -> failwith $"{rejections}"

    let private observations head =
        { Head = GitRead.Observed(HeadState.OnBranch("feature/x", head))
          WorkingTree = GitStatusObservation.Clean
          PathFilter = filter
          BaselineDirtyPaths = []
          HeadRelation = None
          HeadDiff = None
          RemoteBranch = Some(RemoteBranchObservation.At c1)
          RemoteRelation = None
          RemoteDiff = None }

    let private assess checkpoint obs =
        CheckpointAssessment.assess "PRAXIS-CONT-01" LiveWorkState.Active checkpoint obs

    let tests =
        [ { Name = "checkpoint: HEAD == candidate == remote head on a clean tree is a verified checkpoint with typed location"
            Run =
              fun () ->
                  match CheckpointVerification.verify { candidate with StepId = Some "step-1" } healthy with
                  | Error rejections -> failwith $"{rejections}"
                  | Ok checkpoint ->
                      let git = DurableLocation.git checkpoint.Location
                      Assert.equal "EXE-A" checkpoint.ExecutionId
                      Assert.equal (Some "step-1") checkpoint.StepId
                      Assert.equal c1 git.LocalCommit
                      Assert.equal c1 git.RemoteCommit
                      Assert.equal "feature/x" git.Branch
                      Assert.equal "feature/x" git.RemoteBranch
                      Assert.equal "origin" git.Remote.Name
                      Assert.equal VerificationStatus.Verified checkpoint.Verification.Status
                      Assert.equal "git-remote-observation" (DurabilityMechanism.code checkpoint.Verification.Mechanism) }
          { Name = "checkpoint: a missing or inactive work item is refused with a distinct cause"
            Run =
              fun () ->
                  Assert.equal [ "work-item-not-found" ] (rejected { healthy with WorkItemState = None } candidate)
                  Assert.equal [ "work-item-not-active" ] (rejected { healthy with WorkItemState = Some LiveWorkState.Blocked } candidate)
                  Assert.equal [ "work-item-not-active" ] (rejected { healthy with WorkItemState = Some LiveWorkState.Complete } candidate) }
          { Name = "checkpoint: no execution of the caller, or an ambiguous one, is refused"
            Run =
              fun () ->
                  Assert.equal [ "missing-execution" ] (rejected { healthy with Execution = ExecutionObservation.NoneActive } candidate)
                  Assert.equal [ "ambiguous-execution" ] (rejected { healthy with Execution = ExecutionObservation.Ambiguous [ "EXE-1"; "EXE-2" ] } candidate)
                  Assert.equal [ "execution-refused" ] (rejected { healthy with Execution = ExecutionObservation.Refused "not yours" } candidate) }
          { Name = "checkpoint: detached HEAD, unborn HEAD, no upstream and a missing remote are distinct rejections"
            Run =
              fun () ->
                  Assert.equal [ "detached-head" ] (rejected { healthy with Head = GitRead.Observed(HeadState.Detached c1) } candidate)
                  Assert.equal [ "no-head" ] (rejected { healthy with Head = GitRead.Observed(HeadState.Unborn(Some "main")) } candidate)
                  Assert.equal [ "no-upstream" ] (rejected { healthy with Upstream = Some(GitRead.Observed UpstreamState.NoUpstream) } candidate)

                  Assert.equal
                      [ "remote-missing" ]
                      (rejected { healthy with Upstream = Some(GitRead.Observed(UpstreamState.RemoteNotConfigured("gone", "feature/x"))) } candidate) }
          { Name = "checkpoint: an unpushed local commit (local ahead) is refused and never treated as durable"
            Run = fun () -> Assert.equal [ "local-ahead" ] (rejected (remoteAt (CommitRelationObservation.Related(CommitRelation.Ahead 1)) c2) candidate) }
          { Name = "checkpoint: a remote ahead of local, and diverged histories, are refused"
            Run =
              fun () ->
                  Assert.equal [ "remote-ahead" ] (rejected (remoteAt (CommitRelationObservation.Related(CommitRelation.Behind 2)) c2) candidate)
                  Assert.equal [ "diverged" ] (rejected (remoteAt (CommitRelationObservation.Related(CommitRelation.Diverged(1, 1))) c2) candidate) }
          { Name = "checkpoint: a remote head that is not present locally is not remotely visible, not success"
            Run =
              fun () ->
                  Assert.equal [ "not-remotely-visible" ] (rejected (remoteAt (CommitRelationObservation.ObjectMissing c2) c2) candidate)
                  // Contradictory "same" for different heads is never accepted.
                  Assert.equal [ "not-remotely-visible" ] (rejected (remoteAt (CommitRelationObservation.Related CommitRelation.Same) c2) candidate) }
          { Name = "checkpoint: an unreachable remote and a deleted remote branch are refused"
            Run =
              fun () ->
                  Assert.equal
                      [ "remote-unreachable" ]
                      (rejected { healthy with RemoteBranch = Some(RemoteBranchObservation.Unreachable(failure GitUnavailableReason.CommandFailed "could not resolve host")) } candidate)

                  Assert.equal [ "remote-branch-missing" ] (rejected { healthy with RemoteBranch = Some RemoteBranchObservation.Missing } candidate) }
          { Name = "checkpoint: Git unavailable, not a repository, malformed output and unknown state stay distinct"
            Run =
              fun () ->
                  let head reason = { healthy with Head = GitRead.Unavailable(failure reason "x") }
                  Assert.equal [ "git-unavailable" ] (rejected (head GitUnavailableReason.ToolUnavailable) candidate)
                  Assert.equal [ "not-git-repository" ] (rejected (head GitUnavailableReason.NotRepository) candidate)
                  Assert.equal [ "malformed-git-response" ] (rejected (head GitUnavailableReason.MalformedOutput) candidate)
                  Assert.equal [ "unknown-git-state" ] (rejected (head GitUnavailableReason.CommandFailed) candidate) }
          { Name = "checkpoint: meaningful uncommitted changes are refused and named"
            Run =
              fun () ->
                  let dirty = { healthy with WorkingTree = GitStatusObservation.Changed [ change "src/a.fs"; change ".ros/context/current.json" ] }

                  match CheckpointVerification.verify candidate dirty with
                  | Error [ CheckpointRejection.UncommittedChanges paths ] -> Assert.equal [ "src/a.fs" ] paths
                  | other -> failwith $"{other}" }
          { Name = "checkpoint: ignored and Praxis-owned paths never fail a checkpoint"
            Run =
              fun () ->
                  let transient =
                      { healthy with
                          WorkingTree =
                              GitStatusObservation.Changed
                                  [ change ".ros/events/events.jsonl"; change "registries/requirements.json"; change ".echelon/ros.json" ] }

                  Assert.isTrue (CheckpointVerification.verify candidate transient |> Result.isOk) "transient paths rejected" }
          { Name = "checkpoint: a pre-existing baseline dirty path is neither attributed nor a false rejection"
            Run =
              fun () ->
                  let baseline =
                      { healthy with
                          WorkingTree = GitStatusObservation.Changed [ change "prompts/theirs.md" ]
                          BaselineDirtyPaths = [ "prompts/theirs.md" ] }

                  Assert.isTrue (CheckpointVerification.verify candidate baseline |> Result.isOk) "baseline path rejected"
                  let state = WorkingTreeState.observe filter [ "prompts/theirs.md" ] baseline.WorkingTree
                  Assert.equal [ "prompts/theirs.md" ] (WorkingTreeState.excludedBaselinePaths state)
                  Assert.empty (WorkingTreeState.meaningfulPaths state) }
          { Name = "checkpoint: a step never started in the caller's execution is refused; a started one is linked"
            Run =
              fun () ->
                  Assert.equal [ "invalid-step" ] (rejected healthy { candidate with StepId = Some "step-9" })
                  Assert.isTrue (CheckpointVerification.verify { candidate with StepId = Some "step-1" } healthy |> Result.isOk) "valid step rejected" }
          { Name = "checkpoint: blank or whitespace summary and next action are refused together"
            Run =
              fun () ->
                  Assert.equal [ "blank-summary"; "blank-next-action" ] (rejected healthy { candidate with Summary = "  \t"; NextAction = "" })
                  Assert.isTrue (CheckpointRejection.isArgumentError CheckpointRejection.BlankSummary) "blank summary is an argument error" }
          { Name = "checkpoint: independent problems are all reported (remote and tree), never only the first"
            Run =
              fun () ->
                  let both =
                      { (remoteAt (CommitRelationObservation.Related(CommitRelation.Ahead 1)) c2) with
                          WorkingTree = GitStatusObservation.Changed [ change "src/a.fs" ] }

                  Assert.equal [ "local-ahead"; "uncommitted-changes" ] (rejected both candidate) }
          { Name = "checkpoint: every rejection has a remedy that never recommends discarding work"
            Run =
              fun () ->
                  let all =
                      [ CheckpointRejection.UncommittedChanges [ "a" ]
                        CheckpointRejection.Diverged(1, 1, origin, "x")
                        CheckpointRejection.RemoteAhead(1, origin, "x")
                        CheckpointRejection.DetachedHead c1
                        CheckpointRejection.LocalAhead(1, origin, "x") ]

                  for rejection in all do
                      let remedy = CheckpointRejection.remedy rejection
                      Assert.isTrue (remedy.Length > 0) "empty remedy"

                      for forbidden in [ "reset --hard"; "--force"; "push -f"; "git stash"; "checkout --" ] do
                          Assert.isTrue (not (remedy.Contains forbidden)) $"remedy recommends '{forbidden}': {remedy}" }
          { Name = "checkpoint: a stored checkpoint round-trips, and rehydration re-checks every invariant"
            Run =
              fun () ->
                  let checkpoint = (recorded ()).Recorded
                  let stored = Checkpoint.store checkpoint

                  match Checkpoint.rehydrate stored with
                  | Ok back -> Assert.equal checkpoint back
                  | Error problems -> failwith $"{problems}"

                  match Checkpoint.rehydrate { stored with RemoteCommit = String.replicate 40 "b"; Summary = " " } with
                  | Ok _ -> failwith "an unequal commit pair was accepted"
                  | Error problems ->
                      Assert.isTrue (problems |> List.exists (fun p -> p.Contains "differ")) "commit mismatch not reported"
                      Assert.isTrue (problems |> List.exists (fun p -> p.Contains "summary")) "blank summary not reported"

                  match Checkpoint.rehydrate { stored with Commit = "abc123" } with
                  | Ok _ -> failwith "a short SHA was accepted"
                  | Error _ -> () }
          { Name = "commit id: only full lowercase hexadecimal ids parse"
            Run =
              fun () ->
                  Assert.isTrue (CommitId.tryParse "abc").IsNone "short sha parsed"
                  Assert.isTrue (CommitId.tryParse (String.replicate 40 "A")).IsNone "uppercase parsed"
                  Assert.isTrue (CommitId.tryParse "HEAD").IsNone "symbolic parsed"
                  Assert.isTrue (CommitId.tryParse (String.replicate 64 "0")).IsSome "sha-256 rejected" }
          { Name = "freshness: no checkpoint on active work is 'none' with a warning; a checkpoint at HEAD and remote head is current"
            Run =
              fun () ->
                  let none = assess None (observations c1)
                  Assert.equal "none" (CheckpointFreshness.code none.Freshness)
                  Assert.equal [ "no-durable-checkpoint" ] (none.Warnings |> List.map ContinuityWarning.code)
                  let current = assess (Some(recorded ())) (observations c1)
                  Assert.equal "current" (CheckpointFreshness.code current.Freshness)
                  Assert.empty current.Warnings }
          { Name = "freshness: commits after the checkpoint are surfaced with their count"
            Run =
              fun () ->
                  let after =
                      { observations c2 with
                          HeadRelation = Some(CommitRelationObservation.Related(CommitRelation.Behind 3))
                          HeadDiff = Some(GitRead.Observed [ "src/b.fs" ]) }

                  let assessment = assess (Some(recorded ())) after
                  Assert.equal "commits-after-checkpoint" (CheckpointFreshness.code assessment.Freshness)
                  Assert.equal [ "commits-after-checkpoint" ] (assessment.Warnings |> List.map ContinuityWarning.code)

                  match assessment.Warnings with
                  | [ ContinuityWarning.CommitsAfterCheckpoint(_, 3) ] -> ()
                  | other -> failwith $"{other}" }
          { Name = "freshness: commits that only carry Praxis state do not move past the checkpoint"
            Run =
              fun () ->
                  let stateOnly =
                      { observations c2 with
                          HeadRelation = Some(CommitRelationObservation.Related(CommitRelation.Behind 1))
                          HeadDiff = Some(GitRead.Observed [ ".ros/events/events.jsonl"; ".ros/context/current.json" ])
                          RemoteBranch = Some(RemoteBranchObservation.At c2)
                          RemoteRelation = Some(CommitRelationObservation.Related(CommitRelation.Behind 1))
                          RemoteDiff = Some(GitRead.Observed [ ".ros/events/events.jsonl" ]) }

                  let assessment = assess (Some(recorded ())) stateOnly
                  Assert.equal "current" (CheckpointFreshness.code assessment.Freshness)
                  Assert.equal "contained-without-meaningful-change" (CurrentRecoverability.code assessment.CurrentRecoverability.Value) }
          { Name = "freshness: uncommitted meaningful work after the checkpoint is distinct from commits after it"
            Run =
              fun () ->
                  let dirty = { observations c1 with WorkingTree = GitStatusObservation.Changed [ change "src/c.fs" ] }
                  let assessment = assess (Some(recorded ())) dirty
                  Assert.equal "uncommitted-changes-after-checkpoint" (CheckpointFreshness.code assessment.Freshness)
                  Assert.equal [ "uncommitted-work-after-checkpoint" ] (assessment.Warnings |> List.map ContinuityWarning.code) }
          { Name = "freshness: an unreachable remote, a moved remote, a rewritten remote and a deleted branch are distinct"
            Run =
              fun () ->
                  let check expected remote relation diff =
                      let assessment =
                          assess (Some(recorded ())) { observations c1 with RemoteBranch = Some remote; RemoteRelation = relation; RemoteDiff = diff }

                      Assert.equal expected (CheckpointFreshness.code assessment.Freshness)

                  check "remote-unavailable" (RemoteBranchObservation.Unreachable(failure GitUnavailableReason.CommandFailed "offline")) None None
                  check "remote-moved" (RemoteBranchObservation.At c2) (Some(CommitRelationObservation.Related(CommitRelation.Behind 2))) (Some(GitRead.Observed [ "src/x.fs" ]))
                  check "remote-moved" (RemoteBranchObservation.At c2) (Some(CommitRelationObservation.ObjectMissing c2)) None
                  check "checkpoint-no-longer-currently-verifiable" (RemoteBranchObservation.At c3) (Some(CommitRelationObservation.Related(CommitRelation.Diverged(1, 1)))) None
                  check "checkpoint-no-longer-currently-verifiable" RemoteBranchObservation.Missing None None }
          { Name = "history: a later force-push changes current recoverability, never the historical checkpoint"
            Run =
              fun () ->
                  let historical = recorded ()

                  let rewritten =
                      assess
                          (Some historical)
                          { observations c1 with
                              RemoteBranch = Some(RemoteBranchObservation.At c3)
                              RemoteRelation = Some(CommitRelationObservation.Related(CommitRelation.Diverged(2, 1))) }

                  Assert.equal "not-contained" (CurrentRecoverability.code rewritten.CurrentRecoverability.Value)
                  let git = DurableLocation.git rewritten.Checkpoint.Value.Recorded.Location
                  Assert.equal c1 git.RemoteCommit
                  Assert.equal VerificationStatus.Verified rewritten.Checkpoint.Value.Recorded.Verification.Status
                  Assert.equal (Some false) (CurrentRecoverability.isRecoverable rewritten.CurrentRecoverability.Value) }
          { Name = "mutation: no committed or uncommitted meaningful change is 'no change'; an unknown start is never 'no change'"
            Run =
              fun () ->
                  let clean = WorkingTreeState.observe filter [] GitStatusObservation.Clean
                  Assert.equal WorkMutation.NoMeaningfulChange (WorkMutation.observe filter clean (Some(GitRead.Observed [ ".ros/context/current.json" ])))
                  Assert.equal (WorkMutation.MeaningfulChange([ "src/a.fs" ], [])) (WorkMutation.observe filter clean (Some(GitRead.Observed [ "src/a.fs" ])))

                  match WorkMutation.observe filter clean None with
                  | WorkMutation.Unknown _ -> ()
                  | other -> failwith $"{other}"

                  Assert.equal WorkMutation.NotGitRepository (WorkMutation.observe filter WorkingTreeState.NotRepository None) }
          { Name = "completion guard: meaningful work without a checkpoint is refused"
            Run =
              fun () ->
                  match CompletionGuard.decide (WorkMutation.MeaningfulChange([ "src/a.fs" ], [])) (assess None (observations c1)) with
                  | CompletionGuardOutcome.Rejected [ CompletionGuardRejection.NoCheckpoint ] -> ()
                  | other -> failwith $"{other}" }
          { Name = "completion guard: a stale checkpoint (commits after it) is refused"
            Run =
              fun () ->
                  let stale =
                      assess
                          (Some(recorded ()))
                          { observations c2 with
                              HeadRelation = Some(CommitRelationObservation.Related(CommitRelation.Behind 1))
                              HeadDiff = Some(GitRead.Observed [ "src/b.fs" ]) }

                  match CompletionGuard.decide (WorkMutation.MeaningfulChange([ "src/b.fs" ], [])) stale with
                  | CompletionGuardOutcome.Rejected rejections ->
                      Assert.equal [ "checkpoint-not-at-head" ] (rejections |> List.map CompletionGuardRejection.code)
                  | other -> failwith $"{other}" }
          { Name = "completion guard: a final HEAD that is not pushed (remote no longer the checkpoint) is refused"
            Run =
              fun () ->
                  let moved =
                      assess
                          (Some(recorded ()))
                          { observations c1 with
                              RemoteBranch = Some(RemoteBranchObservation.At c3)
                              RemoteRelation = Some(CommitRelationObservation.Related(CommitRelation.Diverged(1, 2))) }

                  match CompletionGuard.decide (WorkMutation.MeaningfulChange([], [])) moved with
                  | CompletionGuardOutcome.Rejected [ CompletionGuardRejection.RemoteNoLongerVerifies _ ] -> ()
                  | other -> failwith $"{other}" }
          { Name = "completion guard: a dirty final state is refused even with a current checkpoint"
            Run =
              fun () ->
                  let dirty = assess (Some(recorded ())) { observations c1 with WorkingTree = GitStatusObservation.Changed [ change "src/z.fs" ] }

                  match CompletionGuard.decide (WorkMutation.MeaningfulChange([], [ "src/z.fs" ])) dirty with
                  | CompletionGuardOutcome.Rejected [ CompletionGuardRejection.UncommittedChanges [ "src/z.fs" ] ] -> ()
                  | other -> failwith $"{other}" }
          { Name = "completion guard: no-change work completes without any commit or checkpoint"
            Run =
              fun () ->
                  match CompletionGuard.decide WorkMutation.NoMeaningfulChange (assess None (observations c1)) with
                  | CompletionGuardOutcome.NotApplicable _ -> ()
                  | other -> failwith $"{other}"

                  match CompletionGuard.decide WorkMutation.NotGitRepository (assess None (observations c1)) with
                  | CompletionGuardOutcome.NotApplicable _ -> ()
                  | other -> failwith $"{other}" }
          { Name = "completion guard: a current, re-verified checkpoint satisfies completion"
            Run =
              fun () ->
                  match CompletionGuard.decide (WorkMutation.MeaningfulChange([ "src/a.fs" ], [])) (assess (Some(recorded ())) (observations c1)) with
                  | CompletionGuardOutcome.Satisfied recordedCheckpoint -> Assert.equal "evt-1" recordedCheckpoint.CheckpointId
                  | other -> failwith $"{other}" }
          { Name = "block guard: no new work blocks normally; new work needs a checkpoint or a recorded unrecoverable reason"
            Run =
              fun () ->
                  let current = assess (Some(recorded ())) (observations c1)
                  Assert.equal BlockGuardOutcome.NoNewWork (BlockGuard.decide WorkMutation.NoMeaningfulChange current None)
                  // A reason offered with no new work is not recorded.
                  Assert.equal BlockGuardOutcome.NoNewWork (BlockGuard.decide WorkMutation.NoMeaningfulChange current (Some "x"))
                  let dirty = assess (Some(recorded ())) { observations c1 with WorkingTree = GitStatusObservation.Changed [ change "src/q.fs" ] }

                  match BlockGuard.decide (WorkMutation.MeaningfulChange([], [ "src/q.fs" ])) dirty None with
                  | BlockGuardOutcome.Rejected _ -> ()
                  | other -> failwith $"{other}"

                  match BlockGuard.decide (WorkMutation.MeaningfulChange([], [ "src/q.fs" ])) dirty (Some "  ") with
                  | BlockGuardOutcome.Rejected _ -> ()
                  | other -> failwith $"blank reason accepted: {other}"

                  match BlockGuard.decide (WorkMutation.MeaningfulChange([], [ "src/q.fs" ])) dirty (Some "disk full; cannot push") with
                  | BlockGuardOutcome.RecordedUnrecoverable(reason, _) -> Assert.equal "disk full; cannot push" reason.Value
                  | other -> failwith $"{other}" }
          { Name = "continuation: a new executor continues active work, naming the checkpoint's execution as interrupted predecessor"
            Run =
              fun () ->
                  let input =
                      { WorkItemId = "PRAXIS-CONT-01"
                        ItemState = Some LiveWorkState.Active
                        Assessment = assess (Some(recorded ())) (observations c1)
                        CallerExecutions = []
                        OtherActiveExecutions = [ "EXE-OTHER"; "EXE-A" ]
                        LatestExecution = Some "EXE-A" }

                  match Continuation.decide input with
                  | Ok plan ->
                      Assert.equal (Some("EXE-A", PredecessorDisposition.Interrupted)) plan.Predecessor
                      Assert.equal [ "EXE-OTHER" ] plan.OtherActiveExecutions
                  | Error rejection -> failwith $"{rejection}" }
          { Name = "continuation: refused for non-active work, for the caller's own run, and over local changes"
            Run =
              fun () ->
                  let baseInput =
                      { WorkItemId = "W-1"
                        ItemState = Some LiveWorkState.Active
                        Assessment = assess (Some(recorded ())) (observations c1)
                        CallerExecutions = []
                        OtherActiveExecutions = [ "EXE-A" ]
                        LatestExecution = Some "EXE-A" }

                  let code input =
                      match Continuation.decide input with
                      | Error rejection -> ContinuationRejection.code rejection
                      | Ok _ -> "ok"

                  Assert.equal "work-item-not-active" (code { baseInput with ItemState = Some LiveWorkState.Blocked })
                  Assert.equal "work-item-not-found" (code { baseInput with ItemState = None })
                  Assert.equal "already-executing" (code { baseInput with CallerExecutions = [ "EXE-MINE" ] })

                  Assert.equal
                      "local-changes"
                      (code { baseInput with Assessment = assess (Some(recorded ())) { observations c1 with WorkingTree = GitStatusObservation.Changed [ change "src/mine.fs" ] } })

                  let elsewhere =
                      assess (Some(recorded ())) { observations c3 with HeadRelation = Some(CommitRelationObservation.ObjectMissing c1) }

                  Assert.equal "checkout-does-not-contain-checkpoint" (code { baseInput with Assessment = elsewhere }) }
          { Name = "continuation: a finalized-only history names the latest execution as an ended predecessor"
            Run =
              fun () ->
                  match
                      Continuation.decide
                          { WorkItemId = "W-1"
                            ItemState = Some LiveWorkState.Active
                            Assessment = assess None (observations c1)
                            CallerExecutions = []
                            OtherActiveExecutions = []
                            LatestExecution = Some "EXE-OLD" }
                  with
                  | Ok plan -> Assert.equal (Some("EXE-OLD", PredecessorDisposition.Ended)) plan.Predecessor
                  | Error rejection -> failwith $"{rejection}" }
          { Name = "recovery: instructions derive from the checkpoint and never reset, stash or discard"
            Run =
              fun () ->
                  let cases =
                      [ assess (Some(recorded ())) (observations c1)
                        assess (Some(recorded ())) { observations c3 with HeadRelation = Some(CommitRelationObservation.ObjectMissing c1) }
                        assess (Some(recorded ())) { observations c1 with WorkingTree = GitStatusObservation.Changed [ change "src/mine.fs" ] }
                        assess None (observations c1) ]

                  for assessment in cases do
                      let steps = RecoveryInstructions.derive assessment
                      Assert.isTrue (not steps.IsEmpty) "no recovery steps"

                      for step in steps do
                          let text = RecoveryStep.render step

                          for forbidden in [ "reset"; "stash"; "--force"; "clean -"; "checkout --" ] do
                              Assert.isTrue (not (text.Contains forbidden)) $"recovery step recommends '{forbidden}': {text}"

                  let fetch = RecoveryInstructions.derive cases[1] |> List.map RecoveryStep.render |> String.concat "\n"
                  Assert.isTrue (fetch.Contains "git fetch origin feature/x") fetch
                  Assert.isTrue (fetch.Contains(String.replicate 40 "a")) "exact checkpoint commit missing"
                  let dirty = RecoveryInstructions.derive cases[2]
                  Assert.equal "stop" (RecoveryStep.kind dirty.Head) } ]
