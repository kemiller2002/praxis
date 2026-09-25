---
id: RQ-ROS-2026-A009
title: Provenance is queryable for attribution metrics
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-identity-and-provenance.md
tags: [metrics, analytics]
provenance:
  contributions:
    - operation: "created"
      at: "2026-09-25T18:16:43.440Z"
      actor:
        kind: "agent"
        id: "claude-code"
        provider: "anthropic"
        model: "unknown"
        runtime: "claude-code"
        executionId: "EXE-20260925T174822557Z-7d2ee77c"
        sessionId: "0cb6dd40-ee60-5a05-8f85-35e2927960fe"
        assurance: "self-reported"
      reason: "formal requirement for agent identity and provenance (WI-0070)"
---

# Provenance is queryable for attribution metrics

1. `provenance show TARGET` MUST report a record's creator, contributors, involvement, lineage, and contributions.
2. `provenance summary` MUST report, per actor: records created and modified, operations by record kind, providers, models, and executions; and across records: human corrections of agent work, agent-to-agent revisions, human-approved agent work, derived records, and modification hotspots.
3. The model MUST make later analyses (defects, rework, cost, duration, and outcomes by agent, model, provider, or execution) possible by joining on `executionId` with telemetry, without Praxis owning an analytics subsystem.

## Verification

- `ProvenanceTests.fs`: summary aggregation.
- `provenance-cli.test.mjs`: summary over a multi-agent, human-approved requirement.
