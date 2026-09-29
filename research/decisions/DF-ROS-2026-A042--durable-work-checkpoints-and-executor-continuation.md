---
id: DF-ROS-2026-A042
title: Durable work checkpoints are verified Git-remote evidence recorded as work.checkpointed events, with executor continuation as a new execution rather than a lifecycle transition
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-29
updated: 2026-09-29
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A022]
supporting_evidence: []
related_documents:
  - RQ-ROS-2026-A022
  - RQ-ROS-2026-A021
  - DF-ROS-2026-A041
  - DF-ROS-2026-A036
  - DF-ROS-2026-A040
  - docs/work-protocol.md
  - docs/remote-protocol.md
supersedes: []
superseded_by: []
tags: [continuity, checkpoints, durability, git, remote-execution, handoff]
confidence: medium
provenance:
  contributions:
    EXE-20260929T063242003Z-c2cc74a2:
      operations: [created]
      at: 2026-09-29T06:34:39.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Architecture decision for durable checkpoints and executor continuation (PRAXIS-CONT-00)"
derived_from: [RQ-ROS-2026-A022]
---

# Context

Praxis already records who did what (`DF-ROS-2026-A036`), repairs missing
attribution from Git evidence (`DF-ROS-2026-A040`), and lets an agent with
no local runtime act through typed remote requests (`DF-ROS-2026-A041`).
None of these answers the continuity question: *if the executor disappears
now, can someone else continue from something that is known to exist
outside that executor?* Handoffs so far were prose in block reasons and
chat. `RQ-ROS-2026-A022` requires a typed, verified answer.

# Decision

## 1. A checkpoint is verified evidence, not a lifecycle state

The work lifecycle stays `ready -> active -> blocked -> active`,
`active -> complete`. A **durable checkpoint** is a separate fact about an
active work item: Praxis itself observed, at time T, that commit C of branch
B was the exact head of remote branch R on remote M, with no meaningful
uncommitted work in the tree, and accepted it on behalf of execution E.

- `Ros.Domain.Work.Checkpoint` models it. A `VerifiedCheckpoint` can only be
  built by the domain's verification function from typed Git observations,
  so an unverified checkpoint cannot be recorded.
- The location is a `DurableLocation` union whose only case today is
  `GitRemoteBranch`. The verification records its `mechanism`
  (`git-remote-observation`). Another durability provider is a new case, not
  a reinterpretation of a SHA.
- Local commit and verified remote commit are separate fields even though
  they must be equal at creation, so history states what was observed on
  each side.

## 2. Exact equality at creation

Creation requires `local HEAD == candidate == remote branch head`, observed
with `git ls-remote` (the remote itself, not a possibly stale
remote-tracking ref). Ahead, behind, diverged, missing, unreachable,
detached, unborn, and no-upstream states are each a distinct rejection with
a non-destructive remedy. Praxis never commits, stages, pushes, fetches,
merges, rebases, resets, stashes, or switches branches for the executor.

## 3. Meaningful-path semantics

"Uncommitted work" and "work after the checkpoint" use the existing
`workProtocol.meaningfulPaths`/`ignoredPaths` configuration, so Praxis-owned
state (`.ros/**`), registries, and installation bookkeeping never count.
Paths recorded in the context's `baselineDirtyPaths` are never attributed to
a checkpoint and never cause a false rejection; they are reported as
excluded. Consequences:

- Recording a checkpoint changes only Praxis state, so committing and
  pushing that state afterwards does not "move past" the checkpoint.
  Freshness compares the checkpoint commit with HEAD by ancestry plus a
  tree diff restricted to meaningful paths, and reports the raw commit count
  separately.
- The same rule decides whether a work item made a meaningful Git mutation
  (from the earliest execution's recorded start commit, plus dirty
  meaningful paths), which is what exempts no-change work from the
  completion guard.

## 4. Persistence: event history plus latest projection

A successful checkpoint appends one `work.checkpointed` event to
`.ros/events/events.jsonl` (content-addressed `eventId`, like every work
event) and writes the same checkpoint as `latestCheckpoint` on the work
item in `.ros/context/current.json`, in one `work-state` journal
transaction under the existing `work-protocol` lock. The event's `eventId`
is the checkpoint's identity. History is never duplicated into the context.
A context item without `latestCheckpoint` is simply an item with no
checkpoint. The change is additive: no schema version changes and no
migration is required.

## 5. Historical verification versus current recoverability

The event records the historical fact and is never rewritten. Every read
(`work context`, `work checkpoint show`, `status`, `work continue`, and the
guards) separately observes **current recoverability**: whether the remote
branch head still equals the checkpoint, contains it, has moved past it by
meaningful work, no longer contains it (for example after a force-push), is
missing, or cannot be reached. **Freshness** summarizes local and remote
facts in one value, while the underlying facts stay available as separate
fields.

Reads that would need a missing object never fetch; they report the
ancestry as unknown and recommend `git fetch`.

## 6. Guards

Enforcement is configured by
`workProtocol.continuity.requireDurableCheckpoint` in `ros.json`. It is
`true` in this repository and in newly installed projects, and absent
(so `false`) in existing installations, which keeps the change additive.
Observed continuity warnings are reported regardless.

- **Completion.** When the work item made a meaningful Git mutation, or has
  any checkpoint, completion requires a latest checkpoint whose commit is
  the effective HEAD, re-verified at the remote at completion time, and a
  clean meaningful tree. No-change work completes without any commit.
  A repository that is not a Git repository cannot hold Git-backed work,
  so the guard does not apply. Git being unavailable or unknown state is a
  rejection.
- **Block.** When meaningful work exists after the latest checkpoint (or
  since the work began, without one), blocking requires a checkpoint first
  or `--unrecoverable-reason TEXT`, which is recorded on the `work.blocked`
  event as `continuity: {status: "not-remotely-recoverable", reason}`.

## 7. Continuation is a new execution

`work continue --id ID` is not `resume`: the item stays `active`. It
refuses a checkout with meaningful uncommitted changes or a HEAD that does
not contain the checkpoint (reporting non-destructive recovery steps),
creates a new execution for the caller with `parentExecutionId` naming the
predecessor, and appends a `work.continued` event naming the predecessor
execution, the successor execution, the checkpoint, and the predecessor's
disposition `interrupted` as observed by the successor. The predecessor's
execution record is never edited, re-identified, or marked successful.
When the work item later completes, the existing rule that completion
finalizes every active execution still applies; "finalized" means closed,
and the `work.continued` event remains the truthful record of the
interruption.

## 8. Remote protocol 1.3

`praxis.remote` 1.3 adds `work.checkpoint` (`mutate`) and `work.continue`
(`mutate`), executed by the same command implementation. `work.context`
already returns the continuity block. The adapter's commit of the resulting
Praxis state is a non-meaningful commit on top of the verified commit,
which by section 3 does not move past the checkpoint. Concurrency is the
existing `expectedSha` binding plus non-fast-forward push.

## 9. Validation

`validate` checks, offline: each checkpoint event's integrity (recomputed
`eventId`), work-item and execution references, the execution belonging to
the work item, a step reference that the execution started, chronology
(not before the execution started), and that each context
`latestCheckpoint` equals the latest checkpoint event for that item.
Remote recoverability is never needed to read or validate history.

# Alternatives considered

- **Accept any commit reachable from the remote branch.** Rejected for
  creation: a remotely advanced or diverged branch means there is no
  unambiguous canonical recovery point at that branch location. Reachability
  is reported afterwards as current recoverability.
- **Checkpoint as a lifecycle state.** Rejected: it conflates evidence
  with workflow and breaks the semantic mapping external systems rely on.
- **Automatic commit/push.** Rejected: the executor decides what is a
  coherent recovery point; Praxis verifies it.
- **Timers or per-step automatic checkpoints.** Rejected: produce
  meaningless commits and noise.
- **Writing an "interrupted" marker into the predecessor's execution
  record.** Rejected: nobody records into an execution that is not theirs.
- **Enforce by default in every existing installation.** Rejected for this
  version: it would change completion semantics of in-flight work in
  repositories that never opted in. Warnings are shown instead.

# Consequences

- The standard order for Git-backed work becomes: commit, push,
  `work checkpoint`, `work complete`, then commit and push the Praxis state.
- A shared branch where another work item's commits land means a research
  item may be asked to checkpoint already-pushed HEAD. That requires no new
  commit.
- Continuity no longer depends on any provider's chat history.

# Revisit triggers

- A second durability provider is needed.
- Enforcement should become the default for existing installations.
- Real use shows the meaningful-mutation heuristic misclassifies work.
