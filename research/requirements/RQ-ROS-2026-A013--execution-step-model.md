---
id: RQ-ROS-2026-A013
title: Executions contain ordered, attributable steps
status: verified
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-27
updated: 2026-09-27
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A037
  - docs/development-telemetry.md
tags: [telemetry, execution, steps, provenance]
provenance:
  contributions:
    EXE-20260927T013711437Z-b3dfcbfc:
      operations: [created, modified]
      at: 2026-09-27T01:55:33.976Z
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

Praxis MUST let an agent create ordered steps within its active execution without a centrally predefined plan. Each step MUST have a stable identifier, execution and work-item identity, sibling order, original name, optional description, open classification, status, timestamps, and the execution actor/provider/model/runtime context that was actually available.

Steps MAY nest through `parentStepId`. Flat use MUST remain simple, sibling order MUST be stable, and unknown or custom classifications MUST remain representable. Completed history MUST NOT be rewritten except by a future explicit correction operation that preserves provenance.

## Acceptance criteria

- An agent can plan or begin a named step while executing.
- Step identifiers and ordering serialize deterministically.
- A nested step preserves its parent and sibling order.
- Existing executions with no `steps` field remain valid.
