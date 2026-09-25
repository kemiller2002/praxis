---
id: RQ-ROS-2026-A002
title: Every execution has its own identity, separate from the actor's stable identity
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-provenance.md
tags: [provenance, execution, identity, telemetry]
provenance:
  contributions:
    EXE-20260925T193942361Z-84dc9b22:
      operations: [created]
      at: 2026-09-25T20:16:18.438Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from the agent identity and provenance objective (work item FEAT-AGENT-PROVENANCE)"
---

# Requirement

Each run of an agent, human, or automation under the work protocol MUST have its own execution identity, the `EXE-...` telemetry execution ID. Its record MUST store `identity.actorKind` alongside the discovered provider, model, runtime, session, and agent ID. Two executions of the same agent MUST remain distinct, and everything produced in one execution MUST be traceable to it.

## Rationale

Separating the stable actor from the individual run lets analysis attribute work to both the agent and the specific run, without conflating either.

## Acceptance criteria

- `work begin`, `resume`, and `complete` and `telemetry start` write `identity.actorKind` when they create an execution.
- Execution records written before `actorKind` existed are projected from the discovery mechanism they preserved (`provenance.sources`), or else as `unknown`.
- Contributions are keyed by execution, so two runs of the same agent never merge.

## Verification

- ProvenanceTests: two executions of the same agent share a stable identity but remain distinct contributions; legacy execution records without actorKind project from their recorded discovery mechanism
- tests/work-start-fsharp-differential.test.mjs and tests/telemetry-start-fsharp-differential.test.mjs (identity.actorKind)
