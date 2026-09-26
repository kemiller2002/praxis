---
id: RQ-ROS-2026-A013
title: Contributions from other Echelon systems are keyed by their own execution
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
      at: 2026-09-26T08:05:33.046Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Cross-system Echelon provenance upgrade (work item FEAT-ECHELON-PROVENANCE)"
---

# Requirement

Praxis MUST accept a contribution keyed by another Echelon system's execution as `EXT-<system>.<run-id>`, where `<system>` is that system's Echelon registry id and `<run-id>` is the run it recorded. Such a key is a valid execution key for an agent contribution. Praxis MUST carry these contributions verbatim, MUST NOT cross-check them against local execution records, and MUST report them as informational (audit only), never as an error or a warning.

## Rationale

An artifact that crossed a system boundary still has to say which run of which system produced each contribution. Praxis `EXE-` ids are only minted by `ros work begin`; Dokimos snapshots, Vigila operations, CI runs, and ROS worker attempts have their own run identities, and inventing an `EXE-` id for them would be fabrication.

## Acceptance criteria

- `EXT-dokimos.snapshot-1` is valid; `EXT-dokimos` and `EXT-Dokimos.run` are not.
- An agent contribution keyed `EXT-...` passes structural validation; one keyed `CTB-...` does not.
- `./ros validate` emits no error or warning for a foreign execution; `./ros provenance audit` lists it as informational.
- `provenance show/audit --json` report the foreign key as the contribution's execution, so metrics join across systems.

## Verification

- ProvenanceInterchangeTests: foreign executions: an agent may be keyed by EXT-<system>.<run>, which is informational, never an error
- tests/provenance-interchange.test.mjs: foreign execution keys name their system and reject what they cannot carry
