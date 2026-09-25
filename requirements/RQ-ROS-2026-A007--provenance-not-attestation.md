---
id: RQ-ROS-2026-A007
title: Recorded identity is provenance, not proof, and admits later attestation
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-identity-and-provenance.md
tags: [attestation, security, trust]
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

# Recorded identity is provenance, not proof, and admits later attestation

1. Every actor MUST carry `assurance`. Praxis-produced actors are `self-reported`; documentation and `identity` output MUST state that a recorded provider or identity does not prove who produced a record.
2. An `assurance` value other than `self-reported` supplied by an integration (for example a CI attestation or signed execution receipt) MUST be preserved verbatim and reported as not verified by this version, so a verifier can be added later without changing the record shape.
3. Praxis MUST NOT treat provenance as authentication or authorization.

## Verification

- `ProvenanceTests.fs`: an unverified declared assurance is preserved and reported as informational.
