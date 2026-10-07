---
id: DF-ROS-2026-A040
title: Post-hoc attribution reconciliation as an append-only, Git-evidenced, content-bound event
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-27
updated: 2026-09-27
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A020]
related_documents:
  - RQ-ROS-2026-A020
  - DF-ROS-2026-A033
  - DF-ROS-2026-A035
  - DF-ROS-2026-A036
  - docs/work-protocol.md
  - https://github.com/kemiller2002/praxis/issues/80
supersedes: []
superseded_by: []
tags: [attribution, reconciliation, work-protocol, provenance, git-evidence, validation]
confidence: high
provenance:
  contributions:
    EXE-20260927T011826712Z-9911b752:
      operations: [created]
      at: 2026-09-27T01:40:41.353Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Architecture decision for post-hoc attribution reconciliation (work item GH-80)"
derived_from: [RQ-ROS-2026-A020]
---
# Decision

Committed meaningful changes that lack work-item attribution are repaired by
`./ros work reconcile`, which appends one `work.attribution.reconciled` event
to `.ros/events/events.jsonl`:

1. **A new event type, not an existing one.** Reconciliation never edits a
   `work.completed` event, never transitions the work item, and never writes
   work context. The event carries `attribution: "post-hoc"` so recovered
   attribution stays distinguishable from contemporaneous attribution forever.
   It reuses the generic `paths` field that every event-log consumer already
   treats as durable file attribution, and deliberately uses `gitEvidence`
   rather than `evidence`, which on work events means completion-evidence
   files.
2. **Git is the evidence; users do not list files.** Selected commits
   (`--commit`, two-dot `--range`) must resolve unambiguously and be in the
   history of `HEAD`; a range's base must be an ancestor of its head. Each
   commit's own changes against its single parent (`diff-tree --raw -M`)
   determine the paths: both sides of a rename, the destination of a copy.
   Merge commits and shallow-clone boundaries are rejected because their
   changes cannot be assigned unambiguously. `--path` can only narrow.
3. **Three identities.** The event records the reconciliation actor
   (resolved exactly as for work transitions, `DF-ROS-2026-A036`) and, per
   commit, Git's author and committer. The actor is never the change author.
4. **Claims are `(commit, path)` pairs.** Re-reconciling the same change to
   the same work item is a no-op; to another work item it is a conflict that
   fails closed. Paths a contemporaneous event already attributes are skipped,
   not re-claimed.
5. **Content-bound validation.** A reconciled path counts as attributed only
   while its current content is a state the recorded evidence produced (a
   recorded blob, or absence). Reconciling a change never pre-authorizes a
   later change to the same path.
6. **Self-validating records.** Validation re-derives each reconciliation
   event's content hash and checks that it names an existing, non-abandoned
   work item, an actor, and a reason, is marked post-hoc, is not dated before
   its own commits, justifies every path by a recorded non-merge change, and
   does not claim a change another event gave to a different work item. An
   invalid event attributes nothing and is itself a `work_reconciliation`
   error.
7. **Uncommitted changes are out of scope.** The working tree has no author
   and no immutable identity; commit first, or begin the work item.
8. **Layering.** Policy is Domain (`WorkReconciliation`,
   `ReconciliationValidation`, `ReconciliationCoverage`); evidence gathering is
   Application over a read-only `GitHistory` port; Git processes, event
   persistence, and the lock are Infrastructure; the command is a separate CLI
   module (`DF-ROS-2026-A035`). The feature is F#-only: the Node library is
   frozen (`DF-ROS-2026-A033`), and because the event uses `paths`, Node's own
   attribution reader still recognizes reconciled paths.

# Why

Issue #80 records a real case where committed work could only be made
attributable by touching files again. That rewrites history to satisfy a
check. Recording the recovery as its own evidence-backed event keeps the
history true: the work happened without attribution, and attribution was
reconciled later, by a named actor, for a stated reason, from verifiable
commits.

Content binding answers the strongest objection to any post-hoc mechanism:
that it makes validation easier to satisfy rather than more truthful. Without
it, reconciling one old commit would silently attribute every future edit to
the same paths.

# Alternatives rejected

- **Appending paths to the work item's completion event or context.** Rewrites
  history and erases the post-hoc fact.
- **Accepting user-listed paths.** Unverifiable; allows attributing arbitrary
  changes.
- **Suppressing findings (an allow-list).** Hides the gap instead of recording
  who closed it and why.
- **Diffing a range as a whole (`BASE..HEAD` tree diff).** Loses per-commit
  author identity and hides intermediate changes; per-commit changes are both
  more precise and more reviewable.
- **Requiring recorded commits to stay reachable at validation time.**
  Squash merges and rebases would invalidate honest reconciliations; blob-level
  content binding keeps the guarantee without depending on commit reachability.

# Consequences

- A reconciled path that later changes again needs new attribution.
- Contemporaneous attribution remains path-based, as before.
- Reconciliation cannot judge whether a commit semantically belongs to a work
  item; the recorded reason, actor, and evidence make a wrong reconciliation
  reviewable and detectable rather than impossible.
- Symbolic links and submodules are not matched by content and fail closed.
- `work complete` with `ROS_BASE_REF` set still sweeps committed-range paths
  into its completion event (pre-existing behavior kept for Node parity); that
  is a separate follow-up.

# Revisit when

- Pull-request-level evidence (a PR's commits) is needed as a selector.
- Contemporaneous attribution moves from paths to content.
