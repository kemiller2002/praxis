---
id: RQ-ROS-2026-A009
title: Provenance survives integration and export boundaries
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
tags: [provenance, integration, export, echelon]
provenance:
  contributions:
    EXE-20260925T193942361Z-84dc9b22:
      operations: [created]
      at: 2026-09-25T20:16:21.656Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from the agent identity and provenance objective (work item FEAT-AGENT-PROVENANCE)"
---

# Requirement

Provenance MUST travel with the data it describes. Published work events and `artifact.contributed` events MUST keep their actor. Generated registries MUST project each artifact's provenance. An artifact file MUST keep its contributors when moved, and a receiving repository without the originating executions MUST report that as a warning, not an error. Praxis MUST NOT depend on Ordo, Vigila, Aegis, Dokimos, Percepta, EDF, or any Git host to use provenance.

## Rationale

An artifact passed from one Echelon system to another should retain its origin rather than have it stripped at the boundary.

## Acceptance criteria

- `./ros adapter publish` exports events with their actor verbatim.
- `schemas/provenance-actor.schema.json` and `schemas/artifact-provenance.schema.json` define the shapes other systems can adopt.

## Verification

- ProvenanceEffectTests: adapter publication exports the attributed event verbatim; generated registries carry artifact provenance
- ProvenanceTests: policy: execution cross-check warns on unknown executions
