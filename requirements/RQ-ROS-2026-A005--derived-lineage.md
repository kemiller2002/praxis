---
id: RQ-ROS-2026-A005
title: Derivation lineage is recorded separately from authorship
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-identity-and-provenance.md
tags: [lineage, derivation, artifacts]
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

# Derivation lineage is recorded separately from authorship

1. When an actor creates a record based on another record, the new record MUST name its sources in the existing `derived_from` reference field, which Praxis already validates as resolvable references.
2. The new record's `created` contribution MUST name the deriving actor. Lineage MUST NOT transfer, merge, or imply authorship of the source record.
3. Provenance projections (`provenance show`, `provenance summary`) MUST report lineage alongside, and distinct from, contributors.

## Verification

- `provenance-cli.test.mjs`: a requirement created by one agent and derived from another agent's requirement.
- `ProvenanceTests.fs`: summary reports derived records separately from contributors.
