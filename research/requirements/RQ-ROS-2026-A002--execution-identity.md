---
id: RQ-ROS-2026-A002
title: Every execution has its own identity, separate from the actor's stable identity
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-28
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
    EXE-20260928T090915352Z-fb23943c:
      operations: [modified]
      at: 2026-09-28T10:18:58.429Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Verification references moved from removed Node tests to their F# ports (F#/.NET-only repository cleanup (work item FSHARP-ONLY-REPOSITORY))"
    EXE-20261006T203816433Z-d8de3cb4:
      operations: [modified]
      at: 2026-10-06T20:40:40.078Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-MISC-09: cited test paths moved from tests/Ros.Tests to tests/Praxis.Tests; content unchanged"
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
- tests/Praxis.Tests/WorkLifecycleCliTests.fs and tests/Praxis.Tests/TelemetryCliTests.fs (identity.actorKind)
