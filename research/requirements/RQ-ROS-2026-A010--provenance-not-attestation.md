---
id: RQ-ROS-2026-A010
title: Recorded identity is provenance, not authentication, and remains open to attestation
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
tags: [provenance, security, attestation, assurance]
provenance:
  contributions:
    EXE-20260925T193942361Z-84dc9b22:
      operations: [created]
      at: 2026-09-25T20:16:22.115Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from the agent identity and provenance objective (work item FEAT-AGENT-PROVENANCE)"
---

# Requirement

Praxis MUST document and label recorded identity as self-reported provenance, not proof of identity. The model MUST allow stronger attestation, such as signed execution records, CI or OIDC identity, GitHub App identity, or external receipts, to be added later without redesign. In particular, unmodeled fields on a contribution MUST survive other contributors' recordings.

## Rationale

A string saying `provider: openai` does not prove that OpenAI produced anything. Overstating assurance would mislead, while designing it out would force a later migration.

## Acceptance criteria

- `provenance identity` reports `assurance: self-reported`.
- Events carry before/after content digests that a future signature can cover.
- No cryptography is implemented now (see DF-ROS-2026-A036).

## Verification

- ProvenanceTests: front-matter writer appends a second contributor and never rewrites the first contributor's entry (unmodeled attestation field preserved)
