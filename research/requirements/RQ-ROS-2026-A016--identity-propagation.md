---
id: RQ-ROS-2026-A016
title: The current actor and execution propagate to tools without re-implementing identity discovery
status: implemented
version: 1.1.0
owners:
  - repository-governance
created: 2026-09-26
updated: 2026-09-26
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A037
  - DF-ROS-2026-A036
  - docs/agent-provenance.md
  - docs/echelon-provenance-architecture.md
tags: [provenance, echelon, interchange]
derived_from: [RQ-ROS-2026-A009]
provenance:
  contributions:
    EXE-20260926T075049280Z-9919ff63:
      operations: [created, modified]
      at: 2026-09-26T08:05:34.633Z
      last: 2026-09-26T08:52:35.463Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Cross-system Echelon provenance upgrade (work item FEAT-ECHELON-PROVENANCE)"
---

# Requirement

A tool launched inside a Praxis execution MUST be able to learn the current execution from `ROS_EXECUTION_ID`, which Praxis treats exactly like an explicit `--execution` assertion (the same guards apply; an explicit flag wins). A downstream Echelon system that needs the current actor MUST take it from an explicit declaration (an execution envelope, CLI flags, or the `ROS_ACTOR_KIND`/`ROS_ACTOR`/`ROS_TELEMETRY_*` variables) or from `ros provenance identity --json` when Praxis is available, and otherwise record `unknown`. It MUST NOT guess identity from ambient signals and MUST NOT depend on Praxis being installed.

## Rationale

Re-implementing runtime detection in every system would fork the identity model and drift. Explicit propagation keeps Praxis authoritative for discovery while leaving every system independently usable.

## Acceptance criteria

- `provenance record` without `--execution` uses `ROS_EXECUTION_ID` when set.
- docs/echelon-provenance-architecture.md documents the propagation contract and the unknown fallback.
- A process launched on behalf of another actor starts from an environment with every identity-bearing variable removed. The canonical list is `tests/fixtures/provenance-interchange/identity-environment.json` (`IDENTITY_ENVIRONMENT_VARIABLES`), pinned against the variables Praxis identity discovery actually reads.

## Verification

- ProvenanceEffectTests: recording inherits the execution's identity (guards unchanged)
- docs/echelon-provenance-architecture.md, Identity propagation
