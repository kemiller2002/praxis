---
id: RQ-ROS-2026-A020
title: Committed changes made without an active work item can be reconciled post hoc from Git evidence
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-27
updated: 2026-09-27
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A040
  - docs/work-protocol.md
  - https://github.com/kemiller2002/praxis/issues/80
tags: [attribution, reconciliation, work-protocol, provenance, git-evidence]
provenance:
  contributions:
    EXE-20260927T011826712Z-9911b752:
      operations: [created]
      at: 2026-09-27T01:40:40.952Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from GitHub issue #80 (work item GH-80): post-hoc Git-evidenced attribution reconciliation"
      evidence: [https://github.com/kemiller2002/praxis/issues/80]
---

# Requirement

ROS MUST provide an explicit, auditable way to attribute meaningful committed
changes that were made while no work item was active to an existing work item,
after the fact, using Git history as the evidence, without modifying the
changed files. The reconciliation MUST be recorded durably as a distinct
post-hoc attribution event and MUST NOT rewrite or masquerade as the
contemporaneous attribution a work item's own transitions record.

## Rationale

A downstream validation incident (issue #80, already resolved there) showed
that legitimately committed work could only be made "attributable" by touching
files again under a later work item. That destroys the real history of who
changed what and when, and it hides the governance gap rather than recording
it.

## Acceptance criteria

- `./ros work reconcile` establishes the attributed paths from the selected
  commits (`--commit`, `--range BASE..HEAD`); users never enumerate paths, and
  `--path` can only narrow what Git proves.
- Added, modified, deleted, and type-changed paths are attributed; a rename
  attributes both its source and destination.
- Paths excluded by the repository's `meaningfulPaths`/`ignoredPaths` are
  never attributed and never create a reconciliation requirement.
- Reconciliation fails closed, recording nothing, for an unknown or abandoned
  work item, a missing reason, a missing, unresolvable, or ambiguous revision,
  a commit outside the history of `HEAD`, an empty, symmetric, or unrelated
  range, a merge commit, a shallow-clone boundary, a `--path` outside the
  evidence, an `--occurred-at` earlier than the evidence, a change already
  reconciled to a different work item, or unavailable Git.
- The durable `work.attribution.reconciled` event records the work item, the
  paths, `attribution: "post-hoc"`, the reason, when it happened, the
  reconciliation actor, and the Git evidence (HEAD, selectors as given and
  resolved, and each justifying commit with its original author, committer,
  subject, parents, and per-path changes and blob ids).
- The change author, the work item, and the reconciliation actor are recorded
  as three distinct identities and never collapsed.
- Reconciliation is idempotent per `(commit, path)`; repeating it writes
  nothing, and a change can never be reconciled to two work items.
- Existing events and work context are never modified.
- `./ros work validate` and `./ros validate` accept a reconciled path only
  while its current content matches the recorded evidence, keep failing for
  unrelated unattributed paths, and report tampered or internally inconsistent
  reconciliation events as `work_reconciliation` findings that attribute
  nothing.

## Verification

- tests/Ros.Tests/WorkReconciliationTests.fs
- tests/Ros.Tests/WorkReconciliationEffectTests.fs
