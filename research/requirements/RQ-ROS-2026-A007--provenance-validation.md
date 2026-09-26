---
id: RQ-ROS-2026-A007
title: Validation enforces provenance for new work and stays compatible with legacy repositories
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-provenance.md
tags: [provenance, validation, legacy, migration]
provenance:
  contributions:
    EXE-20260925T193942361Z-84dc9b22:
      operations: [created, modified]
      at: 2026-09-25T20:16:20.772Z
      last: 2026-09-25T20:23:34.384Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from the agent identity and provenance objective (work item FEAT-AGENT-PROVENANCE)"
---

# Requirement

`./ros validate` MUST report malformed or contradictory provenance as errors. Under the `ros.json` `provenance` policy (`enforce`, `requiredFrom`, `requireOriginator`), it MUST also report new canonical artifacts without provenance and unattributed modifications of new artifacts as errors. It MUST report non-blocking concerns as warnings that do not fail validation, and legacy observations as informational findings in `./ros provenance audit` only. A repository without the policy MUST validate as before.

## Rationale

New agent work must not silently lose attribution, but a repository with years of history must not become unusable, and history must never be invented to satisfy a validator.

## Acceptance criteria

- The severity table in docs/agent-provenance.md is implemented exactly.
- An unattributed modification is detected from Git, as content changed since the base revision without any change to recorded contributions, and not only from dates.
- `validate --json` emits warnings with `"severity":"warning"`, and `valid` reflects errors only.
- Migration is a deliberate no-op: legacy artifacts stay unattributed until a real contribution is recorded, and self-declared legacy author fields are reported as unverified, never converted.

## Verification

- ProvenanceTests: policy tests (new, legacy, stale modification, execution cross-check, malformed); change detection: content changed since base without a new contribution is unattributed
- ProvenanceEffectTests: the base-revision reader returns an artifact as committed
- ProvenanceEffectTests: policy configuration: absent is not enforced; enforced without a date is an error
