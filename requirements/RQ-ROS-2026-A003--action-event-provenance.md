---
id: RQ-ROS-2026-A003
title: Every recorded action names its actor, execution, operation, target, and time
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-identity-and-provenance.md
tags: [events, work-protocol, provenance]
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

# Every recorded action names its actor, execution, operation, target, and time

1. Every work event (`work.started`, `work.blocked`, `work.resumed`, `work.completed`) MUST carry the resolved `actor`, including an explicitly `unknown` one; no writer path may omit it.
2. The event's `actor` MUST be part of its content hash (`eventId`), so the same transition by a different actor or execution is a different event while a retried identical command stays idempotent.
3. Together with the existing event fields, an event MUST answer: who (actor kind/id), which execution, which provider/model when known, which work item and paths, which operation, when, and which evidence or reason.
4. Installer bookkeeping events MUST be attributed to an `automation` actor (`ros-bootstrap`), identically from the Node and F# installers.

## Verification

- `provenance-cli.test.mjs`: start/block/complete events carry the bound actor; published events preserve it.
- Golden-master differential tests keep every pre-existing event field at parity (`tests/support/provenance-golden.mjs`).
