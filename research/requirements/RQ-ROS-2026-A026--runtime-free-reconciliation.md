---
id: RQ-ROS-2026-A026
title: Runtime-free envelopes reconcile into canonical work and execution state
status: implemented
version: 1.0.0
owners: [repository-governance]
created: 2026-09-27
updated: 2026-09-27
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A051
  - RQ-ROS-2026-A019
  - requirements/PRAXIS-DUAL-ENTRY-RECONCILIATION.md
  - protocol/praxis-envelope-v1.schema.json
  - docs/fallback-reconciliation.md
tags: [fallback, reconciliation, offline, telemetry, provenance, instance-identity]
provenance:
  contributions:
    EXE-20260927T145842499Z-f1b40c22:
      operations: [created, modified]
      at: 2026-09-28T01:07:43.000Z
      last: 2026-09-28T02:12:55Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Runtime-free canonical reconciliation requirement"
    EXE-20260928T122823444Z-e1fc1859:
      operations: [modified]
      at: 2026-09-28T12:46:05Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Update fallback decision reference after resolving the canonical ID collision with main"
      evidence: [DF-ROS-2026-A051]
    EXE-20260928T134154662Z-5c00e767:
      operations: [migrated]
      at: 2026-10-01T06:26:26Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Renumber after latest main independently assigned RQ-ROS-2026-A022"
---

# Requirement

An agent that cannot execute Praxis MUST be able to submit the approved JSON envelope as a second entry path into the same canonical work, event, execution, step, evidence, identity, and telemetry model. The envelope is untrusted input, never a second authority.

Reconciliation MUST use the native work transition and evidence rules; preserve ordered requests, actor and instance identity, execution/step boundaries, normalized measurement epistemic status, sanitized provider-specific raw fields, and evidence relationships; and keep historical repositories and envelopes without steps valid.

The operation MUST be deterministic, single-work-item scoped, idempotent, serialized per checkout, and crash recoverable. It MUST reject branch, base-ancestry, instance, identity, timestamp, lifecycle, evidence, duplicate, raw-retention-policy, path-redirection, and contradictory-measurement failures before canonical mutation. Completion MUST NOT leave a supplied or previously linked execution active. An operational interruption after preparation MUST remain pending and retryable rather than being quarantined as invalid.

Accepted canonical state MUST be committed as an exact path set, receive a deterministic transaction checkpoint, and become durable before accepted input is removed. Rejected input MUST remain inspectable with machine-readable findings and MUST NOT partially mutate canonical state. Independently installed Praxis instances MUST remain authoritative and usable without any central service.

## Acceptance criteria

- `reconcile --envelope FILE` supports ordered start/begin, block, resume, and complete requests and rejects unknown request types.
- The claimed branch equals the work item and observed branch; the base commit exists and is an ancestor of observed HEAD.
- Completion evidence is checked with the native evidence repository before any canonical write.
- Optional fallback execution steps become canonical execution telemetry without copying values into rollups; zero and unavailable remain distinct, and sanitized raw provider/pricing fields remain preserved within repository retention limits.
- Completion rejects unresolved supplied steps and refuses to infer finalization for a previously linked active native execution.
- A journal can resume before or after canonical writes or commit without dispatching a transition twice.
- Applied status requires both an applied receipt and `praxis-reconcile/<transaction-id>`; a conflicting existing tag fails closed.
- Accepted input is deleted only after the checkpoint exists. A tracked input is removed by a subsequent exact-path cleanup commit. Rejected input is quarantined. Replaying an applied transaction is a no-op.
- CI uses a reconciler built from trusted base code when it has authority to push a same-repository candidate branch; untrusted candidate code is not executed at the privileged boundary.
- Old execution records and fallback JSON without `execution` remain valid.
