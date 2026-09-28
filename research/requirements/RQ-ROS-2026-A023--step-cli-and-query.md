---
id: RQ-ROS-2026-A023
title: Step lifecycle and evidence have deterministic CLI inspection
status: verified
version: 1.0.0
owners: [repository-governance]
created: 2026-09-27
updated: 2026-09-27
research_area: repository-operating-system
priority: high
related_documents: [DF-ROS-2026-A037, RQ-ROS-2026-A013, RQ-ROS-2026-A014]
tags: [steps, cli, query, json]
provenance:
  contributions:
    EXE-20260927T013711437Z-b3dfcbfc:
      operations: [created, modified]
      at: 2026-09-27T01:55:35.455Z
      last: 2026-09-27T07:23:55Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "First-class step-level execution telemetry requirements and architecture"
    EXE-20260927T145842499Z-f1b40c22:
      operations: [migrated]
      at: 2026-09-28T01:07:46.000Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Resolve requirement identifier collision introduced by main integration"
    EXE-20260928T122823444Z-e1fc1859:
      operations: [migrated]
      at: 2026-09-28T12:45:21Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Renumber canonical step-telemetry requirement after integrating main's independently assigned RQ-ROS-2026-A021"
derived_from: [RQ-ROS-2026-A020]
---

# Requirement

The F# authority MUST expose commands to create, transition, annotate, list, and show execution steps. Default resolution MUST select only the current actor/session's active execution and active step; explicit identifiers MUST still pass execution and actor ownership checks.

Human and deterministic JSON output MUST show order, status, duration, identity, availability, usage, cost, derivations, evidence, and nesting. Existing commands MUST continue to work.

## Acceptance criteria

- `step list` and `step show` are deterministic.
- Lifecycle commands resolve the active execution and step unambiguously.
- JSON output contains the canonical step representation plus derived summaries.
