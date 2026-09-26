---
id: RQ-ROS-2026-A015
title: No Echelon system silently strips, overwrites, or re-attributes provenance
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
  - RQ-ROS-2026-A004
  - RQ-ROS-2026-A008
  - RQ-ROS-2026-A010
  - docs/echelon-provenance-architecture.md
tags: [provenance, integration, echelon, validation, lineage]
provenance:
  contributions:
    EXE-20260926T204846052Z-8cef0d1e:
      operations: [created]
      at: 2026-09-26T20:50:07.496Z
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

A system that transports, transforms, stores, derives from, or acts upon an artifact carrying Praxis provenance MUST preserve that provenance. It MUST follow these rules.

## Handling rules

| When a system... | It MUST... | It MUST NOT... |
|---|---|---|
| transports a record | carry it verbatim | add a contribution |
| changes only the record's representation | record `migrated` | |
| changes the subject's content | record `modified`, or a documented interchange operation | |
| derives a new subject | start a new record whose own `created` contribution names the deriving actor, list the source in `derivedFrom`, and carry the source record as a snapshot in `sources` whenever it holds that record | merge the source's contributors into the new record |

## Interchange extension operations

The interchange extension operations are:

- `x-handled`: acted on a follow-up;
- `x-resolved`: resolved it;
- `x-remediated`: changed the affected artifact to address a finding;
- `x-validated`: verified a resolution or remediation;
- `x-dismissed`: closed without action.

Systems use these operations rather than inventing synonyms.

## Identity is not authorization or evidence

Recorded identity MUST NOT be used as authorization, as evidence, or as a weight on evidence.

## Checking a successor

A successor record MUST pass the successor check in all of these respects:

- no contribution removed;
- no actor overwritten;
- no `at` changed;
- no operation or evidence removed;
- no reason rewritten;
- no originator changed;
- no lineage reference or snapshot removed or rewritten;
- no field the consumer does not model dropped;
- no major version changed in place;
- an unsupported major carried verbatim.

## Independence

Adopting the contract MUST NOT make any Echelon system depend on Praxis being installed or reachable.

## Rationale

These rules let the whole chain, from agent execution to downstream action, be reconstructed. They do this without pretending that any one actor authored the chain, and without letting whichever system touched the chain last erase the others.

## Acceptance criteria

- `ProvenanceRecordJson.successorProblems` and `./ros provenance check-record --previous FILE` detect each destructive transformation listed above.
- `ProvenanceRecord.chain` reconstructs every contribution across lineage snapshots and keeps each one under its own subject.
- `ProvenanceRecord.derive` refuses a derivation that has no creator.
- The end-to-end fixture chain passes the successor check at every hop. The chain runs: requirement created by agent A, modified by agent B, implemented, measured, security finding, follow-up, handled, resolved, validated.

## Verification

- ProvenanceRecordTests: successor pairs separate preserving from destructive transformations
- ProvenanceRecordTests: derivation authors the new subject and carries sources as lineage, never as authorship
- ProvenanceRecordTests: the cross-system end-to-end chain is reconstructable without a single author
- ProvenanceRecordTests: a downstream record's contributions land in Praxis artifact front matter unchanged
