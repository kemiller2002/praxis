---
id: DF-ROS-2026-A037
title: Execution-owned step ledger with referenced canonical telemetry
status: accepted
version: 1.0.0
owners: [repository-governance]
created: 2026-09-27
updated: 2026-09-27
research_area: repository-operating-system
decision_type: architecture
supports: []
related_documents:
  - DF-ROS-2026-A010
  - DF-ROS-2026-A036
  - RQ-ROS-2026-A013
  - RQ-ROS-2026-A014
  - RQ-ROS-2026-A015
  - RQ-ROS-2026-A016
  - RQ-ROS-2026-A017
  - RQ-ROS-2026-A018
  - RQ-ROS-2026-A019
  - RQ-ROS-2026-A020
  - requirements/PRAXIS-DUAL-ENTRY-RECONCILIATION.md
supersedes: []
superseded_by: []
tags: [architecture, execution, steps, telemetry, provenance]
confidence: high
derived_from: [DF-ROS-2026-A010, DF-ROS-2026-A036]
provenance:
  contributions:
    EXE-20260927T013711437Z-b3dfcbfc:
      operations: [created, modified]
      at: 2026-09-27T01:55:35.666Z
      last: 2026-09-27T07:23:55Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "First-class step-level execution telemetry requirements and architecture"
---

# Decision

Praxis will extend each execution record with an optional ordered `steps` ledger. A step is execution evidence, not a requirement or work item. It snapshots the execution identity, owns its lifecycle and relationships, and refers to canonical execution telemetry by stable observation identifiers.

Usage and cost observations remain stored exactly once in the execution's `metrics` and sanitized `rawTelemetry` collections. A directly step-attributed metric carries `stepId`; a step keeps reference/checkpoint metadata. Thus existing execution/work-item aggregation sees one canonical observation while step queries can select its subset. A nested parent is grouping-only: measurements and checkpoints belong to leaf steps, and a measured step cannot later acquire children. This prevents overlapping parent/child intervals from entering totals twice.

Nested execution uses one active ancestor path. An active child may coexist only with its active ancestors. Terminal transitions and execution finalization fail closed on unresolved descendants or steps. Checkpoint deltas require two explicitly referenced, compatible cumulative observations and preserve their identifiers.

The F# implementation will use a dedicated `Step` domain module, file repository, and `StepCommands` composition module. Existing execution JSON and the approved fallback envelope receive additive optional fields, so legacy records require no migration.

# Why

- Execution ownership preserves the existing `work item -> execution` authority and actor/session checks.
- One canonical metric copy prevents parent/child and execution/step double counting.
- Explicit checkpoint references make late telemetry and concurrent sessions attributable without guessing from wall-clock windows.
- An optional field preserves historical repositories and lets the first genuine step begin only after the capability exists.
- An open classification string list is more durable than a closed enum while recommended values remain documentable.

# Alternatives rejected

- A separate `.ros/steps` database: it creates a second authority and multi-file transaction boundary.
- Encoding steps as work items: it confuses execution plan evidence with independently governed obligations.
- Assigning measurements to a step by timestamp alone: provider delivery may be late and concurrent activity may overlap.
- Storing parent rollups as observations: nested steps would double count unless every consumer understood the hierarchy.
- Predefining every step centrally: it would erase the agent's actual plan as research evidence.

# Consequences

- Execution records gain additive `steps`, `stepId`, checkpoint, pricing, and relationship schema surfaces.
- Finalization checks step state before it mutates the record.
- A crashed active step stays visible and must be resumed, blocked, or abandoned explicitly.
- Provider adapters remain provider-specific; the step model remains provider-neutral.
- The current fallback reconciler is fail-closed before canonical dispatch. This work can preserve and validate step-aware envelopes, but it cannot claim full native/envelope equivalence until the already-approved reconciliation dispatcher exists.
- Local Praxis instance registration remains an upstream requirement. Step records preserve a nullable instance identity now and reject mismatches when a local authority exists; they do not depend on a registry.

# Revisit when

- Praxis introduces parallel step execution.
- Providers expose cryptographically attributable per-step usage.
- A correction protocol for immutable completed step descriptions is approved.
