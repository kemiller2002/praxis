---
id: DF-ROS-2026-A028
title: Preconditions and phased plan for redirecting ./ros to the F# solution
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-09
updated: 2026-09-09
author_agent: claude-sonnet-5
supporting_evidence:
  - EV-ROS-2026-A043
related_documents:
  - DF-ROS-2026-A027
  - docs/migrations/fsharp/ARCHITECTURE.md
  - docs/migrations/fsharp/ROADMAP.md
  - docs/migrations/fsharp/STATUS.md
supersedes: []
superseded_by: []
tags: [architecture, fsharp, migration, distribution, sde, decision]
confidence: high
---

# Context

The user asked, in this session, to update `./ros` to use only the F#
solution from now on. `DF-ROS-2026-A027` (staged F# application boundary)
already anticipated this request and deliberately withheld authority for it:
"A later accepted decision, not this record alone, is required to redirect
`./ros` or remove a legacy path." This record is that later decision. Given
the choice between (a) redirecting `./ros` immediately, (b) dispatching only
the handful of already-proven commands to F# while keeping Node for the
rest, or (c) starting the governed decision process the roadmap already
calls for, the user chose (c).

`EV-ROS-2026-A043` is the factual precondition for this decision: a
command-by-command inventory of `./ros` (Node) against `ros-fs` (F#). Its
headline finding is that no Node command which writes durable repository
state (`add`, every `work` transition, every `telemetry` producer command,
both `adapter` commands) has any F# equivalent today — only artifact
registry projection (`registry build`/`check`) has full, differential-tested
parity; everything else F# exposes is an explicitly non-persisting shadow
diagnostic. Consumer distribution evidence (install/startup/size/
update/offline/integrity/rollback across macOS/Linux/Windows) required by
`DF-ROS-2026-A027` also does not exist yet.

# Decision

`./ros` is **not** redirected by this record. Doing so today would delete
this repository's own ability to run its governed work protocol — including
the commands used to produce this very record — for no compensating benefit,
which fails the Engineering Standards' proportionality requirement and the
Agent Operating Manual's bar against expanding blast radius without
evidence.

Instead, this record opens the authority-switch decision track as a named,
phased plan with explicit acceptance criteria, so it can be pursued
incrementally by future migration slices rather than attempted as one
unbounded change:

## Phase A — full command-surface effect parity (extends MIG-07/MIG-08)

For every "No F# equivalent" or "Read-only diagnostic only" row in
`EV-ROS-2026-A043`'s inventory, build the typed decision (where one does not
already exist), a real effect handler composed from the already-built
persistence ports (`WorkStateTransaction`, `BacklogStateTransaction`,
per-execution telemetry atomicity — all MIG-05), and a Node/F# differential
proof, following the same construction method already used for every prior
slice. This explicitly includes the new-execution creation effect the
telemetry-resolution slice already named as its own open boundary
(`ResolvedTelemetryOutcome.PendingNewExecution`), and new domain modeling for
backlog capture/update/attach and telemetry producer commands that have no
F# model at all yet. Acceptance: every row in a re-run of
`EV-ROS-2026-A043`'s inventory reads "full parity," each proven by a
differential test exercising the real effect, not only the decision.

## Phase B — consumer distribution evidence (resolves MIG-01's deferred half)

Gather the exact evidence `DF-ROS-2026-A027` named as blocking: installation
story, startup time, binary size, offline/update behavior, integrity
verification, and rollback, for whichever distribution shape is chosen
(framework-dependent `.NET 10`, self-contained single-file, or an accepted
alternative) on macOS, Linux, and Windows. Acceptance: a dedicated decision
record accepting a specific consumer distribution shape with that evidence
attached.

## Phase C — the actual switch decision

Only once Phase A and Phase B are both accepted may a further decision
redirect `./ros`'s dispatch (or any starter profile) to the F# binary. That
decision must itself carry: the full-parity evidence from Phase A, the
distribution evidence from Phase B, an explicit rollback plan (this record's
Reversibility section defines the floor for it), and a stated cutover
mechanism (flag/env var staged rollout, or a hard cutover with a pinned
rollback tag) — consistent with `DF-ROS-2026-A027`'s requirement that a
switch be its own accepted decision, not inferred from this one.

No phase is authorized to skip its acceptance criterion because a later
phase looks tractable; each is independently gated.

# Alternatives

- **Redirect `./ros` now:** rejected. `EV-ROS-2026-A043` shows this deletes
  every state-changing command with no replacement, breaking the
  repository's own governed workflow immediately and irreversibly-feeling
  (every subsequent `./ros work ...` invocation would fail).
- **Dispatch only already-proven commands (`registry build`/`check`, maybe
  `validate`/`git status`) to F# now, keep Node for the rest:** not rejected
  outright — this remains a legitimate, smaller interim step available to a
  future session — but it is a separate, narrower engineering change from
  "start the distribution decision" that the user did not select this
  round, so it is named here as an option for Phase A's early increments
  rather than decided now.
- **Treat MIG-07/MIG-08's existing "deferred" status as sufficient and take
  no new decision:** rejected. The user's explicit request is a materially
  new authorization question distinct from ordinary roadmap sequencing, and
  `DF-ROS-2026-A027` already requires a dedicated decision for exactly this
  question.
- **Commit to a specific distribution shape (e.g., self-contained
  single-file) now:** rejected as premature. No comparative evidence across
  shapes has been gathered yet; that comparison is Phase B's job.

# Consequences

Nothing about `./ros`'s current behavior changes. Node remains the sole
production authority for every command that mutates repository state. This
record's cost is the decision-tracking overhead itself (this record plus
`EV-ROS-2026-A043`) against the benefit of an explicit, evidenced,
incrementally-gated path to the switch the user asked for, instead of either
silently ignoring the request or silently executing a breaking change.

Phase A is large — comparable in scope to the whole of MIG-07 plus MIG-08 —
and is not committed to any timeline by this record. `ROADMAP.md` and
`STATUS.md` are updated alongside this record to reference it, so a future
session resuming the migration finds this decision before re-deriving it.

# Reversibility and validation

This record makes no code change and is trivially reversible (supersede or
amend it). The floor it sets for Phase C's own rollback plan: `./ros` must
remain restorable to invoking Node by reverting a single, isolated dispatch
change — no canonical data format, schema, or file layout may be changed as
part of any redirect in a way Node cannot also read, until Node is formally
retired by its own accepted decision (out of scope for all three phases
above). Validation for this record is `./ros validate` and
`./ros registry check` passing unchanged, since it touches no source that
either check inspects beyond the new canonical records themselves.
