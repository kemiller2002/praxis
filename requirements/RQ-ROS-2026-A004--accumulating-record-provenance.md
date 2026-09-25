---
id: RQ-ROS-2026-A004
title: Requirements, canonical artifacts, and backlog items accumulate contributor history
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-identity-and-provenance.md
tags: [requirements, artifacts, backlog, contributors]
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

# Requirements, canonical artifacts, and backlog items accumulate contributor history

1. Requirements, decisions, evidence, hypotheses, experiments, journals, research packages, theories, missions, and backlog items MUST support a `provenance` block (`praxis.provenance/1`) holding an ordered list of contributions, each with `operation` (`created`, `modified`, `reviewed`, `approved`), `at`, `actor`, and optional `workItem`, `reason`, `evidence`, and `basis` (the prior state operated on).
2. Provenance MUST be append-only: recording a contribution MUST preserve every earlier contribution byte-for-byte, and a retried identical contribution MUST NOT be duplicated. A malformed existing block MUST be refused rather than overwritten.
3. `created` MUST be recorded at most once and first. A new record defaults to `created`; any existing record, including a legacy record with no provenance, defaults to `modified`, so a later contributor never claims prior authorship. The last modifier MUST NOT be treated as the original author.
4. Backlog capture MUST record `created`; backlog update, attachment, and state transitions MUST record `modified` with a reason, automatically, from the resolved actor.
5. Canonical artifacts MUST gain contributions through `provenance record PATH`, which inherits the current actor and single active work item, and edits only by inserting lines.
6. Praxis MUST distinguish agent-created, human-created, agent-created and human-approved, human-created and agent-modified, and agent-created and another-agent-modified records.

## Verification

- `ProvenanceTests.fs`: append/idempotence/preservation, involvement combinations, front-matter append properties.
- `provenance-cli.test.mjs`: backlog accumulation and requirement creation, revision, approval.
