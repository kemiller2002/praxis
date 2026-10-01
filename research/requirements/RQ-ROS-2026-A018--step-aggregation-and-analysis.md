---
id: RQ-ROS-2026-A018
title: Step aggregation is non-overlapping and analysis ready
status: verified
version: 1.0.0
owners: [repository-governance]
created: 2026-09-27
updated: 2026-09-27
research_area: repository-operating-system
priority: high
related_documents: [DF-ROS-2026-A010, DF-ROS-2026-A037]
tags: [steps, aggregation, metrics, research]
provenance:
  contributions:
    EXE-20260927T013711437Z-b3dfcbfc:
      operations: [created, modified]
      at: 2026-09-27T01:55:35.035Z
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

Execution and work-item summaries MUST aggregate step telemetry only where units, scopes, quality, derivation, and overlap make addition valid. Parent and child steps MUST NOT double-count one observation. Cumulative counters MUST NOT be summed as deltas.

Stored evidence MUST support future derivation of step, requirement, defect, test, transition, rework, implementation, and work-item efficiency metrics without prematurely storing every research ratio as a canonical field.

## Acceptance criteria

- Each canonical measurement contributes at most once to execution/work-item totals.
- Query output keeps quality and non-aggregatable measurements visible.
- Step count and grouped usage/cost are inspectable.
