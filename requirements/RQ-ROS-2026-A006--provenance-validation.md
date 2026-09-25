---
id: RQ-ROS-2026-A006
title: Validation enforces provenance for new work while keeping legacy repositories usable
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-identity-and-provenance.md
tags: [validation, legacy, migration]
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

# Validation enforces provenance for new work while keeping legacy repositories usable

1. `ros.json` `provenance` MAY declare `enforce` and `requiredSince`. Without it, the repository is in legacy mode.
2. Malformed provenance (unknown kind or operation, missing required actor fields, invalid timestamps, duplicate or non-first `created`) MUST be an error in every mode.
3. Under enforcement, a record created at or after `requiredSince` (every record when unset) without provenance MUST be an error, as MUST a new record whose contributions lack a `created` contribution.
4. Records created before the cutoff, and every unattributed record in legacy mode, MUST remain readable and be reported as informational legacy records. A legacy free-form author string (`author_agent`, `createdBy`) MUST be reported but never converted into structured provenance.
5. Honest unknowns on new records (unknown kind, unknown agent id, an agent with no bound execution) MUST be warnings, not silent and not errors.
6. Removing or altering a committed contribution MUST be an error (`provenance-rewritten`).
7. Errors MUST fail `validate`; `provenance validate` MUST report errors, warnings, and informational findings.
8. Praxis MUST NOT rewrite history to migrate: adopting provenance means setting `requiredSince` to the adoption time.

## Verification

- `ProvenanceTests.fs`: every policy grade.
- `provenance-cli.test.mjs`: missing, malformed, rewritten, legacy-artifact, and legacy-versus-new event cases.
