---
id: RQ-ROS-2026-A019
title: Provenance never substitutes for authentication, authorization, or evidence
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
      at: 2026-09-26T08:05:36.256Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Cross-system Echelon provenance upgrade (work item FEAT-ECHELON-PROVENANCE)"
---

# Requirement

Across every Echelon system, a recorded actor answers only "who says they did this". It MUST NOT be treated as authentication (Tutela identity bindings), authorization (Ordo capabilities), or evidence quality (EDF claim weight, Dokimos findings, Aegis severity). A system MUST NOT grant, deny, weight, or verify anything because of the actor's kind, provider, model, or id alone.

## Rationale

Identity is self-reported (RQ-ROS-2026-A010). If it silently became a permission or a credibility signal, a single mislabelled or forged actor would change outcomes. Keeping identity, capability, and evidence separate is what makes provenance safe to collect everywhere.

## Acceptance criteria

- docs/echelon-provenance-architecture.md states the separation for each system.
- Downstream requirements in Ordo, Tutela, and EDF restate it for their own decision points, with tests where the system decides.

## Verification

- docs/echelon-provenance-architecture.md, Identity is not authority
- Downstream repository tests referenced from the architecture inventory
