---
id: RQ-ROS-2026-A022
title: Durable work checkpoints and agent continuity — an executor session is disposable; repository state plus Praxis state is durable
status: accepted
version: 1.2.0
owners:
  - repository-governance
created: 2026-09-29
updated: 2026-09-29
research_area: repository-operating-system
priority: high
depends_on: [RQ-ROS-2026-A021, RQ-ROS-2026-A020]
evidence_ids: [EV-ROS-2026-A055]
related_documents:
  - DF-ROS-2026-A042
  - DF-ROS-2026-A043
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
    EXE-20260929T075409917Z-4a1d9b5a:
      operations: [modified]
      at: 2026-09-29T08:02:40.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Implementation status and evidence EV-ROS-2026-A055 (PRAXIS-CONT-10-HARDEN)"
      evidence: [EV-ROS-2026-A055]
    EXE-20260929T101141946Z-cddc93be:
      operations: [modified]
      at: 2026-09-29T10:20:45.774Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Add CONT-080..086 effective-current telemetry requirements dropped from 1.1.0 (PRAXIS-CONT-11-SEGMENTATION)"
    EXE-20261001T112010850Z-0bbe08ef:
      operations: [modified]
      at: 2026-10-01T11:20:30Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Post-merge correction: PR #79 normalized the file's EOF whitespace during integration; record that responsible contribution so base-aware validation remains truthful."
      evidence: [https://github.com/kemiller2002/praxis/actions/runs/36826902106/job/110254499248]
    EXE-20261006T203816433Z-d8de3cb4:
      operations: [modified]
      at: 2026-10-06T20:40:43.996Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-MISC-09: replace citations of deleted Node tests with the F# tests that cover the behaviour; keep the Node result as history"
    EXE-20261006T204120763Z-b0001e2e:
      operations: [modified]
      at: 2026-10-06T20:42:24.471Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-MISC-07: cite the completed cross-provider continuation proof (EV-ROS-2026-A073)"
      evidence: [EV-ROS-2026-A073]
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

### Telemetry segmentation (effective-current)

New observability requirements apply effective-current unless reliable
historical evidence already exists. Historical absence of step data is not
an invalid execution; unknown historical step attribution is not zero step
usage. Praxis prefers truthful incompleteness over reconstructed precision.

- **CONT-080** Execution telemetry MUST be legitimate at two granularities:
  execution-level (unsegmented) and step-level (segmented). An execution
  that began before step tracking was available, required or adopted MUST
  remain valid, and so MUST its work item's completion.
- **CONT-081** Step tracking MUST be adoptable partway through an execution
  without restarting the execution or the work item. Earlier telemetry MUST
  be preserved exactly as recorded, and step attribution MUST apply only
  prospectively from the adoption boundary.
- **CONT-082** Praxis MUST NOT create synthetic historical steps, and MUST
  NOT redistribute or estimate earlier execution-level usage (tokens, cost,
  or any metric) among steps by percentage, duration, change size, or any
  other guess. Where historical step attribution cannot be known, it MUST be
  represented as unavailable, never as zero.
- **CONT-083** `work context` MUST NOT imply that execution activity before
  adoption belongs to the first recorded step. Its human and JSON output
  MUST expose each execution's segmentation and step-tracking boundary.
- **CONT-084** A successor execution MUST start its own telemetry and steps.
  Nothing MUST be appended to its predecessor, and predecessor and successor
  usage MUST remain separately attributable.
- **CONT-085** `validate` MUST accept execution-scoped measurements without
  a step whatever the execution's segmentation, and MUST report a
  step-scoped measurement whose step its own execution never started. No
  validation rule may infer zero usage from missing historical step data.
- **CONT-086** Governance MUST state: "New observability is
  effective-current. Praxis preserves truthful historical gaps rather than
  restarting work or fabricating telemetry," and explain execution-level
  telemetry, step-level telemetry, and unavailable historical step
  attribution.

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
history; the final checkpoint equals the final remote branch head. At least one
scenario MUST show truthful continuation from a predecessor whose telemetry
is not step-segmented, with the successor's step telemetry belonging only
to the successor.

An equivalent contract-level scenario MUST pass for the remote protocol.

## Verification

Per work item, by the tests named in each item's completion evidence; the
two-clone proof is `PRAXIS-CONT-08-RECOVERY`.

## Implementation status (2026-09-29, branch `claude/durable-checkpoints-continuity-ywqs3w`)

The requirements are implemented and tested. The two-clone proof passed, as
recorded in `EV-ROS-2026-A055`.

| Requirements | Delivered by | Tests |
|---|---|---|
| CONT-001..006, 010..013 | PRAXIS-CONT-01, 02 | `CheckpointDomainTests`, `GitDurabilityTests` |
| CONT-020..026 | PRAXIS-CONT-02, 04 | `GitDurabilityTests`, `CheckpointCliTests` |
| CONT-030..035, 070, 071 | PRAXIS-CONT-03, 04 | `CheckpointPersistenceTests`, `CheckpointCliTests` |
| CONT-040..043 | PRAXIS-CONT-05 | `CheckpointGuardTests`, `CheckpointDomainTests` |
| CONT-050..053 | PRAXIS-CONT-06 | `ContinuationCliTests`, `RecoveryProofTests` |
| CONT-060, 061 | PRAXIS-CONT-07 | `RemoteProtocolTests` ("remote 1.3: work.checkpoint maps to the local command ..."), `RemoteExecuteCliTests` ("a successor agent continues in its own execution ...", "a stale request ... refused"); the original Node test `tests/remote-checkpoint.test.mjs` was deleted (historical) with the Node suites (`RQ-ROS-2026-A024`) |
| CONT-072 | PRAXIS-CONT-09 | `CheckpointGuardTests` (starter and upgrade) |
| Acceptance | PRAXIS-CONT-08, PRAXIS-CONT-11 | `RecoveryProofTests` |
| CONT-080..086 | PRAXIS-CONT-11 | `TelemetrySegmentationTests`, `CheckpointPersistenceTests`, `RecoveryProofTests` |

**Remaining.** Protocol 1.3 has not run live through GitHub Actions. That
needs a release containing it, pinned in `.echelon/toolchain.json`.
(Update 2026-10-06: protocol 1.3 `work.continue` and `work.checkpoint` ran
live through GitHub Actions with Praxis 3.6.0 in the cross-provider proof,
runs `36605336068` and `36677659037`; see `EV-ROS-2026-A073`.)
