---
id: RQ-ROS-2026-A014
title: One contribution vocabulary expresses discovery, measurement, transformation, remediation, validation, and resolution
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-26
updated: 2026-09-26
research_area: repository-operating-system
priority: high
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
      at: 2026-09-26T08:05:33.575Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Cross-system Echelon provenance upgrade (work item FEAT-ECHELON-PROVENANCE)"
---

# Requirement

Praxis MUST provide, in its single contribution vocabulary, the role operations `discovered`, `measured`, `transformed`, `remediated`, `validated`, and `resolved` alongside `created`, `modified`, `reviewed`, `approved`, `superseded`, and `migrated`. Only `created` establishes authorship. Measuring, discovering, reviewing, approving, and validating MUST NOT count as modifying the record.

## Rationale

Vigila, Aegis, Dokimos, and the other Echelon systems must distinguish the actor who found a problem, the system that generated a record, the actor who measured something, who fixed it, who confirmed the fix, and who closed it. Without shared operations each system would invent its own vocabulary: a schema fork.

## Acceptance criteria

- `provenance record --operation` accepts every role operation.
- `Involvement` lists a `measured` or `validated` contributor as neither originator nor modifier; a `remediated` contributor is a modifier.
- The artifact and interchange schemas publish the vocabulary.

## Verification

- ProvenanceInterchangeTests: role operations never transfer authorship, and measuring or validating is not modifying
- ProvenanceInterchangeTests: a downstream role history written into front matter reads back identically (downstream -> Praxis artifact)
