---
id: RQ-ROS-2026-A022
title: Durable work checkpoints and agent continuity — an executor session is disposable; repository state plus Praxis state is durable
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-29
updated: 2026-09-29
research_area: repository-operating-system
priority: high
depends_on: [RQ-ROS-2026-A021, RQ-ROS-2026-A020]
evidence_ids: []
related_documents:
  - DF-ROS-2026-A042
  - RQ-ROS-2026-A021
  - DF-ROS-2026-A041
  - DF-ROS-2026-A036
  - docs/work-protocol.md
  - docs/remote-protocol.md
tags: [continuity, checkpoints, durability, handoff, git, remote-execution]
derived_from: [RQ-ROS-2026-A021]
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
      reason: "Canonical requirements for durable work checkpoints and agent continuity (PRAXIS-CONT-00)"
---

# Requirement

**An executor session is disposable. Repository state plus Praxis state is
durable.** A second agent, human, or automation running on another machine
MUST be able to continue meaningful work after the original executor
disappears, without access to that executor's filesystem, process state, or
conversation history. No meaningful completed work may exist only in an
executor's local environment.

Praxis MUST provide a first-class, typed, verified **durable checkpoint**:
evidence, recorded by Praxis from its own observation of the repository and
its remote, that a named commit of a work item's implementation is
retrievable from a durability target. Git is the first durability provider.

The design is `DF-ROS-2026-A042`. Work items `PRAXIS-CONT-00` through
`PRAXIS-CONT-10` deliver it.

## Normative requirements

Each requirement has a stable ID. "MUST" is normative.

### Core continuity

- **CONT-001** Meaningful Git-backed work MUST be recoverable by another
  executor without the previous executor's local filesystem, process state,
  or conversation.
- **CONT-002** Local modifications, staging state, stash state, local
  commits, editor state, transcripts, and local telemetry alone MUST NOT be
  treated as durable checkpoints.
- **CONT-003** A durable Git checkpoint MUST require a remotely retrievable
  commit.
- **CONT-004** A checkpoint is evidence about recoverability, never a work
  lifecycle state. `ready`, `active`, `blocked`, and `complete` remain the
  only work lifecycle states.
- **CONT-005** Checkpoint history MUST be auditable and append-only; a newer
  checkpoint MUST NOT erase an earlier one.
- **CONT-006** Existing repositories and historical records without
  checkpoints MUST remain readable; a missing checkpoint means "no
  checkpoint recorded", never "invalid record".

### Domain model

- **CONT-010** The checkpoint MUST be a typed concept, not a bag of nullable
  strings, capturing: work item, execution, repository identity, local
  branch, local commit, durability target (remote identity), remote branch,
  remotely verified commit, optional telemetry step, completed-work summary,
  next intended action, occurrence time, and verification result.
- **CONT-011** Types MUST distinguish a local commit from a verified remote
  commit, a candidate from a verified checkpoint, verification at creation
  from a later observation of current recoverability, and known from
  unknown from unavailable.
- **CONT-012** The durability concept MUST NOT be "just a SHA"; Git is the
  first provider and the model MUST leave room for others.
- **CONT-013** Domain code MUST be pure: it receives typed observations and
  decides legality. It never runs Git, touches the filesystem, or uses the
  network.

### Checkpoint creation

- **CONT-020** `work checkpoint` MUST locate the work item, require it to be
  active, identify the caller's own execution, observe Git (branch, HEAD,
  upstream), independently inspect the remote, confirm the candidate commit
  is remotely recoverable, verify invariants, append a `work.checkpointed`
  event, update the latest-checkpoint projection atomically, and report the
  result in deterministic human and JSON forms.
- **CONT-021** `work checkpoint` MUST NOT commit, stage, push, merge, rebase,
  reset, stash, force-push, discard modifications, or change branches.
- **CONT-022** At creation Praxis MUST require
  `local HEAD == checkpoint commit == remote branch HEAD` exactly.
- **CONT-023** Praxis MUST reject, with a distinct cause and a safe
  (non-destructive) remediation, each of: not a Git repository; Git
  unavailable; no HEAD; detached HEAD; no branch; no upstream; remote
  missing; remote unreachable; remote branch missing; local branch ahead;
  remote branch ahead; diverged histories; candidate not remotely visible;
  malformed Git response; meaningful uncommitted changes; unknown Git state;
  invalid arguments; inactive or missing work item; missing execution;
  invalid telemetry step; state persistence failure. Remote-unavailable or
  unknown state MUST never become success.
- **CONT-024** Meaningful uncommitted changes MUST reject a checkpoint.
  Transient, tool-owned, and configured ignored paths MUST NOT cause a false
  rejection, and pre-existing baseline dirty paths MUST NOT be attributed to
  the checkpoint or cause a false rejection.
- **CONT-025** A summary MUST be non-blank. A next intended action MUST be
  non-blank; a final checkpoint MAY name a terminal next action such as
  "Run final completion transition". A summary is never proof that tests
  passed.
- **CONT-026** An optional `--step` MUST name a started step of the caller's
  execution, and that execution MUST belong to the work item. Checkpoints
  MUST NOT require synthetic steps.

### Persistence and presentation

- **CONT-030** The persisted event MUST reconstruct the checkpoint without a
  transcript. Historical checkpoints live in the append-only event log; the
  work context carries only the latest checkpoint.
- **CONT-031** `work context` MUST show the latest recoverable checkpoint
  and whether the working repository has moved past it.
- **CONT-032** Freshness MUST distinguish at least: `none`, `current`,
  `commits-after-checkpoint`, `uncommitted-changes-after-checkpoint`,
  `remote-unavailable`, `remote-moved`,
  `checkpoint-no-longer-currently-verifiable`, and `unknown`.
- **CONT-033** `status` MUST surface continuity risk from observed facts
  only: active work without a checkpoint, commits after the checkpoint,
  uncommitted meaningful work after it, and a checkpoint no longer at the
  head of its remote branch.
- **CONT-034** The historical fact ("at time T Praxis observed commit C at
  remote branch R and accepted it") and the current observation ("is C
  still recoverable from R now?") MUST be modelled and reported separately.
  A historical event is never rewritten.
- **CONT-035** Machine-readable output MUST expose semantic fields (status,
  commit, branch, remote, remote branch, recorded time, execution, step,
  summary, next action, current recoverability, freshness), never prose to
  scrape.

### Lifecycle guards

- **CONT-040** Meaningful Git-backed work MUST NOT complete unless the
  latest checkpoint exists, equals the current intended HEAD, is still
  verified at its remote branch (re-checked at completion time), and no
  meaningful uncommitted implementation changes remain.
- **CONT-041** Research, review, investigation, administrative, and other
  work that made no meaningful repository change MUST complete without any
  commit or checkpoint. No meaningless commit may ever be required.
- **CONT-042** Blocking after meaningful work since the latest checkpoint
  MUST require either a durable checkpoint first or an explicit, recorded,
  truthful statement that the latest local state is not remotely
  recoverable, and why.
- **CONT-043** Praxis MUST NOT implement a checkpoint timer, per-edit
  checkpoints, or microcommits. Governance states the coherent recovery
  boundaries at which agents checkpoint.

### Continuation

- **CONT-050** `work continue` MUST let a new executor take over an active
  work item without the blocked/resume cycle: locate the item, identify and
  assess the latest checkpoint, refuse to overwrite unrelated local changes,
  create a new execution for the new executor, preserve the prior execution,
  link predecessor lineage, and present the checkpoint, completed work, next
  action, and unresolved evidence obligations.
- **CONT-051** A successor MUST never reuse a predecessor's identity or
  execution. A disappeared execution MUST be represented truthfully: never
  retroactively successful, never re-identified, never claimed by the
  successor.
- **CONT-052** Recovery guidance MUST derive from the recorded checkpoint
  and MUST NOT recommend destructive Git operations against an unknown or
  dirty working tree.
- **CONT-053** Continuation MUST work across executor types (any agent
  provider, human, automation) with each keeping its own identity.

### Remote execution and concurrency

- **CONT-060** The `praxis.remote` protocol MUST expose checkpoint creation
  and continuation, discoverable through `praxis.describe`, executed by the
  same command implementation, and bound by `requestId`/`expectedSha`.
  Checkpoint context is readable through `work.context`.
- **CONT-061** Concurrent writers MUST NOT silently overwrite checkpoint or
  context state: locally through the existing lock and atomic journal,
  remotely through the existing expected-SHA binding. No distributed lock
  service is introduced.

### Validation, compatibility, governance

- **CONT-070** `validate` MUST check checkpoint structure offline: work item
  and execution references, step reference, event integrity, chronology,
  projection derivable from history, conflicting identities, and supported
  schema version. Live remote recoverability is a runtime observation, never
  a prerequisite for reading history.
- **CONT-071** The schema change MUST be additive.
- **CONT-072** Governance (AGENTS.md, operating manual, work protocol,
  telemetry, provenance, CLI and remote documents) and the starter content
  MUST state the continuity rule, the recovery boundaries, and the
  difference between a commit, a pushed commit, a verified durable
  checkpoint, a historical checkpoint, and a currently recoverable
  checkpoint, respecting `.echelon` ownership on install and upgrade.

## Acceptance criteria

The capability is complete only when the **two-clone agent-loss proof**
passes: with a temporary bare remote and two independent clones, executor A
(clone A only) starts a work item, commits and pushes meaningful work, and
records a verified checkpoint; A is then permanently lost; executor B (clone
B only, never reading clone A) fetches, discovers the work item, branch, and
exact checkpoint commit from Praxis state, verifies it against the remote,
continues under a new execution of its own, commits, pushes, checkpoints,
and completes. B's recovered commit MUST equal A's recorded checkpoint
commit; each executor keeps its own attribution; both checkpoints remain in
history; the final checkpoint equals the final remote branch head.

An equivalent contract-level scenario MUST pass for the remote protocol.

## Verification

Per work item, by the tests named in each item's completion evidence; the
two-clone proof is `PRAXIS-CONT-08-RECOVERY`.
