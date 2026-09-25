---
id: RQ-ROS-2026-A008
title: Provenance crosses integration boundaries without being stripped
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-identity-and-provenance.md
tags: [integration, ordo, echelon, export]
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

# Provenance crosses integration boundaries without being stripped

1. Provenance MUST use one canonical serialization in JSON (events, backlog items, execution records, registries, handoffs) and YAML front matter (canonical artifacts).
2. Event publication (`adapter publish`) MUST carry each event's `actor` unchanged.
3. Generated registries MUST project artifact provenance so downstream systems can consume it without parsing Markdown.
4. `ordo handoff` MUST record the producing actor and execution (`producedBy`).
5. Praxis MUST NOT depend on ROS, Ordo, Vigila, Aegis, Dokimos, Percepta, EDF tooling, or any other Echelon system to record or validate provenance.

## Verification

- `provenance-cli.test.mjs`: published events and handoffs keep actor and execution; the requirements registry projects contributions.
- `ProvenanceTests.fs`: handoff `producedBy` is present when known and optional otherwise.
