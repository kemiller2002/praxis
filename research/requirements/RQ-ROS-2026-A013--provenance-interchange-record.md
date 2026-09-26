---
id: RQ-ROS-2026-A013
title: Provenance crosses system boundaries as a versioned, lossless interchange record
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-26
updated: 2026-09-26
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A036
  - DF-ROS-2026-A037
  - RQ-ROS-2026-A004
  - RQ-ROS-2026-A009
  - docs/agent-provenance.md
  - docs/echelon-provenance-architecture.md
tags: [provenance, integration, versioning, echelon, schema]
provenance:
  contributions:
    EXE-20260926T204846052Z-8cef0d1e:
      operations: [created]
      at: 2026-09-26T20:50:06.518Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Cross-system provenance upgrade (work item FEAT-ECHELON-PROVENANCE)"
derived_from: [RQ-ROS-2026-A009]
---

# Requirement

When Praxis provenance leaves an artifact's front matter for another system, it MUST travel as the provenance interchange record defined by `schemas/provenance-record.schema.json`. The record MUST contain:

- `contract: praxis.provenance-record`;
- a semantic `version`;
- the unchanged execution-keyed `contributions` of RQ-ROS-2026-A004, with the unchanged actor of RQ-ROS-2026-A001;
- lineage as `derivedFrom`;
- optionally, verbatim lineage snapshots as `sources`.

The record is not a second identity model.

A conforming consumer:

1. MUST interpret any minor or patch version of a supported major version, and MUST preserve fields it does not model, at every level.
2. MUST carry a record in an unsupported major version verbatim, and MUST NOT modify or extend it.
3. MUST reject a malformed record. It MUST NOT silently treat it as absent.
4. MUST read a bare, unversioned `{"contributions": ...}` block, as projected into registries, as version 1.

Identity values, reasons, evidence, and references MUST NOT carry credentials.

Praxis MUST publish conformance fixtures that any system can run its own codec against, and MUST test its own codec against them.

## Rationale

- **Why an explicit envelope.** Downstream systems otherwise have to guess which shape they received and which version it is. Guessing is how provenance is dropped or corrupted at a boundary.
- **Why a versioned contract.** Consumers must not assume that today's shape is permanent.
- **Why reuse the artifact shape.** A new shape would fork the identity model.

## Acceptance criteria

- `schemas/provenance-record.schema.json` references the existing contribution and actor schemas rather than redefining them.
- `schemas/conformance/provenance-record/manifest.json` lists valid, unversioned, unsupported-version, and invalid cases, successor pairs, and an end-to-end chain.
- `ProvenanceRecordJson` reads, validates, appends, and derives records losslessly.
- `./ros provenance check-record --path FILE [--json]` validates a record.
- Credential-shaped values are refused in the actor, reason, evidence, subject, and lineage fields.

## Verification

- ProvenanceRecordTests: every shared conformance case reads as its manifest expects
- ProvenanceRecordTests: appending preserves unknown fields and every other contributor verbatim
- ProvenanceRecordTests: a legacy unversioned registry block is read, and gains the envelope only when extended
- ProvenanceRecordTests: contract versions are parsed strictly and only the supported major is interpreted
- ProvenanceRecordTests: identity never carries credentials, in actors, reasons, or evidence
