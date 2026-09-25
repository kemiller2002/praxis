---
id: RQ-ROS-2026-A010
title: Requirements are formal, identified, validated canonical artifacts
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-identity-and-provenance.md
tags: [requirements, artifacts, registries]
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

# Requirements are formal, identified, validated canonical artifacts

1. Requirements MUST be canonical artifacts with the `RQ` identifier prefix, stored as `requirements/RQ-...--slug.md` with front matter, and projected into `registries/requirements.json`.
2. Requirement status MUST be one of `draft`, `proposed`, `accepted`, `implemented`, `deprecated`, `superseded`, `withdrawn`.
3. Free-form prose already in `requirements/` MUST remain valid and untouched; only `RQ-` files are canonical.
4. A repository with no requirements MUST NOT be reported stale for lacking `registries/requirements.json`.

## Verification

- `ProvenanceTests.fs`: identifier, status, prose coexistence, and optional-registry cases.
