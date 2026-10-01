---
id: RQ-ROS-2026-A014
title: Step lifecycle is explicit and state directed
status: verified
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-27
updated: 2026-09-27
research_area: repository-operating-system
priority: high
related_documents: [DF-ROS-2026-A037, RQ-ROS-2026-A013]
tags: [telemetry, steps, lifecycle, validation]
provenance:
  contributions:
    EXE-20260927T013711437Z-b3dfcbfc:
      operations: [created, modified]
      at: 2026-09-27T01:55:34.188Z
      last: 2026-09-27T07:23:55Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "First-class step-level execution telemetry requirements and architecture"
---

# Requirement

Praxis MUST model `planned`, `active`, `completed`, `blocked`, and `abandoned` step states with an explicit transition matrix. Illegal, conflicting, duplicate, cross-execution, or time-regressing transitions MUST be rejected without mutation.

Without an explicit concurrency protocol, active steps MUST form one ancestor path: a child may execute while its ancestors remain active, but unrelated active siblings may not. An execution MUST NOT finalize while any step is planned, active, or blocked. Crash recovery MUST leave unresolved state visible rather than silently repairing it.

## Acceptance criteria

- Begin, complete, block, resume, and abandon follow the documented transition matrix.
- Timestamps cannot precede execution or step start times.
- Execution finalization rejects unresolved steps.
- Validation detects contradictory stored state.
