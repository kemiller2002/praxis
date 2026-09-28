---
id: RQ-ROS-2026-A003
title: Work events and captured work items record the acting actor
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
tags: [provenance, events, work-protocol, attribution]
provenance:
  contributions:
    EXE-20260925T193942361Z-84dc9b22:
      operations: [created]
      at: 2026-09-25T20:16:18.916Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from the agent identity and provenance objective (work item FEAT-AGENT-PROVENANCE)"
    EXE-20260928T090915352Z-fb23943c:
      operations: [modified]
      at: 2026-09-28T10:18:59.082Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Verification references moved from removed Node tests to their F# ports (F#/.NET-only repository cleanup (work item FSHARP-ONLY-REPOSITORY))"
---

# Requirement

Every work-protocol event (`work.started`, `work.blocked`, `work.resumed`, `work.completed`) MUST carry the structured `actor` of the process performing the transition. Every newly captured backlog item MUST record `createdByActor`. The actor MUST be resolved from the current process, never inherited from previously stored state such as the last actor in work context.

## Rationale

Git authorship and free-text actor strings cannot say who performed an action or what kind of actor it was. Inheriting a stored actor would attribute one agent's work to another.

## Acceptance criteria

- The event `actor` is part of the hashed, published event.
- The CLI writes the identical actor recorded by the original golden masters (the Node library that shared them was removed by DF-ROS-2026-A042).
- Legacy events and items without an actor remain valid and are reported only as informational findings.

## Verification

- tests/Ros.Tests/WorkLifecycleCliTests.fs and tests/Ros.Tests/WorkBacklogCliTests.fs (work start, resume, block, capture golden masters)
- ProvenanceTests: events: legacy events without actors are informational; malformed actors are errors
