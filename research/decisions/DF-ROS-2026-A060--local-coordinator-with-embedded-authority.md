---
id: DF-ROS-2026-A060
title: Local agent coordination uses one controller with embedded authority and artifact handoffs
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-10
updated: 2026-10-10
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A028]
related_documents:
  - DF-ROS-2026-A059
  - DF-ROS-2026-A042
  - docs/local-agent-coordination.md
  - docs/execution-runtime.md
tags: [ecir, agents, local, authority, handoff]
confidence: medium
derived_from: [DF-ROS-2026-A059, DF-ROS-2026-A042]
provenance:
  contributions:
    EXE-20261010T091205536Z-2fa1bdc8:
      operations: [created, modified]
      at: 2026-10-10T12:33:04.114Z
      last: 2026-10-10T13:27:46.349Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Propose embedded local controller and artifact handoffs without required hosted services"
---

# Context

The user requires one agent handing work to other agents with no external
services. Existing work groups organize member work; execute-group starts a
member but explicitly does not launch agents. ExecutionService already
supports parent execution identity, worktrees, role launchers and legal-action
checks. ECIR supplies immutable source/decision evidence and native staging,
but its host-authorized dispatch path is not connected.

# Proposed decision

Implement one local Praxis controller, not a mandatory hosted approval
service. The coordinating agent proposes assignments. Deterministic controller
code verifies scope, allocates bounded workers, persists journals, observes
results, invokes independent validators and serializes integration through the
existing native kernels. Workers receive artifact packets and return artifact
results; conversation memory is not the continuity boundary.

Embed the EcirHostAuthority and administration adapters in this local
controller. Host here means the trusted execution boundary, not a remote server
or always-on daemon. Signing/policy/journal access remains outside worker and
coordinating-model authority, with actual OS restrictions when qualifying a
real run. An operator delegates explicit scope locally; new decisions outside
that delegation require local resolution. This does not grant agents blanket
approval or treat same-model review as independently authenticated authorization.

No required Fides, Arca, GitHub API, hosted queue, database or cloud deployment
sits in the loop. Optional publication/remote-model adapters state their network
dependencies. Strict offline qualification includes an installed local model
backend and validators, runs with network denied, and cannot silently fall back.

Add an explicit local checkpoint profile as a future governed change.
Local commit/journal durability is distinct from the existing remote-verified
checkpoint contract; do not disable its current requirements implicitly.

# Alternatives and consequences

- Hosted approval/coordination: adds deployment dependencies the user excludes.
- Unstructured prompts and shared mutable checkout: loses scope identity,
  creates overlapping writes and makes failure/retry ambiguous.
- New group/work lifecycle: duplicates native evidence and transition rules.
- Assuming process spawn is exactly once: cannot safely distinguish a process
  that started before its acknowledgement was lost. Persist attempt identity,
  reconcile unknown outcomes and require idempotency or refusal.

A local controller reduces infrastructure, not the required verification or
containment. Worktree isolation alone is not a credential/write boundary.
Local recovery evidence does not survive disk loss and is not remote backup.
The first implementation uses bounded parallelism and serialized integration;
batch size and concurrency are adjusted only from measured results.

# Validation and status

RQ-ROS-2026-A028 supplies falsifiable acceptance criteria and issues #220/#226
track implementation. Qualify one coordinator plus two actual workers under
outbound-network denial, with independent frozen acceptance and crash recovery.
Deterministic fixtures prove mechanics only. Existing host tests do not prove
this new local loop or actual local model quality.

This decision remains draft; no execution gate or accepted checkpoint policy
is changed here. Rollback is retaining the existing ECIR refusal and stopping
new local dispatch. Existing groups and member histories remain authoritative.

## Isolated advisory implementation

`handoff explain` inspects typed packet proposals locally and emits text or
`praxis.local-handoff-explanation/1` JSON. It accepts no authority or scheduler
port and has no work-state/process/network effects. Every inspected proposal
retains authority-unobserved and adapter-unqualified blocking reasons; exit zero
means inspection succeeded, not execution authorization. The supplied packet
set is not evidence of full frozen blueprint coverage. This advisory projection
can be replaced without migrating execution state. The protected controller,
local checkpoint profile and actual offline worker loop remain proposed.


## Isolated dispatch persistence proposal

The prototype uses `praxis.local-dispatch-reservation/1` and
`praxis.local-dispatch-event/1` in a provisioned host directory outside the
repository. An immutable reservation binds the packet and controller-session
incarnation; exclusive contiguous event files distinguish intent, observed
start, confirmed no-start and observed exit. Ambiguous starts never return to a
launchable state. These files grant no worker authority and update no native
work state. Actual OS observation/protection and policy/repository locks remain
requirements on the future adapter. File flush and process-interruption tests
do not qualify metadata persistence across power loss. See
[the journal prototype](../../docs/local-dispatch-journal.md). This remains a
draft persistent shape and does not replace remote-verified checkpoints.
