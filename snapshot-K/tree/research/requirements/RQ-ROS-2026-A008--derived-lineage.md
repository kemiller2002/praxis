---
id: RQ-ROS-2026-A008
title: Derivation lineage is recorded separately from authorship
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
tags: [provenance, lineage, derivation]
provenance:
  contributions:
    EXE-20260925T193942361Z-84dc9b22:
      operations: [created]
      at: 2026-09-25T20:16:21.199Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from the agent identity and provenance objective (work item FEAT-AGENT-PROVENANCE)"
---

# Requirement

When an artifact is derived from another, Praxis MUST record the source in the artifact's `derived_from` reference field (`--derived-from` on `provenance record`). The derived artifact's authorship MUST remain its own contributors. Lineage MAY name foreign, namespaced references from other systems.

## Rationale

When agent B produces something from agent A's work, both facts matter: B authored the result, and it derives from A's artifact.

## Acceptance criteria

- `./ros provenance show` lists lineage sources with each source's own involvement label, and lists derivatives in reverse.
- `artifact.contributed` events carry `derivedFrom`.

## Verification

- ProvenanceTests: lineage is separate from authorship
- ProvenanceEffectTests: derived-from lineage is recorded on the artifact and in the event
