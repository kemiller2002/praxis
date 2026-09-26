---
id: RQ-ROS-2026-A017
title: Provenance never carries credentials
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-26
updated: 2026-09-26
research_area: repository-operating-system
priority: critical
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
      operations: [created]
      at: 2026-09-26T08:05:35.182Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Cross-system Echelon provenance upgrade (work item FEAT-ECHELON-PROVENANCE)"
---

# Requirement

Provenance (actors, contribution entries, lineage, and every other field of an interchange block) MUST NOT contain API keys, access tokens, private keys, or other authentication material. A receiving implementation MUST reject a block containing a recognisable credential as malformed rather than store or forward it. Execution and session identifiers identify a run; they are not credentials.

## Rationale

Provenance is copied into artifacts, registries, events, and other systems' storage, and is published. Any secret that enters it leaks everywhere it travels.

## Acceptance criteria

- The F# codec and the JavaScript reference library share a credential tripwire (GitHub, OpenAI/Anthropic-style, AWS, Slack tokens, bearer tokens, JWTs, private-key blocks).
- Fixtures `credential-in-actor`, `credential-in-reason`, and `credential-in-unsupported-major` are malformed.

## Verification

- ProvenanceInterchangeTests: credential-like values are refused wherever they appear in a provenance block
- tests/fixtures/provenance-interchange/cases.json credential cases
