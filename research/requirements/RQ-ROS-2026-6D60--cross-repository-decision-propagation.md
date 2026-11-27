---
id: RQ-ROS-2026-6D60
title: Decisions taken in one repository propagate to others through implication records with an immutable source reference, a target, a disposition class, acceptance criteria and validation requirements
status: accepted
version: 0.2.0
owners: [repository-governance]
created: 2026-11-26
updated: 2026-11-27
research_area: repository-operating-system
priority: medium
related_documents:
  - RQ-ROS-2026-6C17
  - RQ-ROS-2026-0863
  - RQ-ROS-2026-A037
tags: [decisions, cross-repository, propagation, traceability, requirements]
provenance:
  contributions:
    EXE-20261126T204033635Z-01b9955f:
      operations: [created]
      at: 2026-11-26T20:41:26Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "WI-0118: DAF-agnostic decision lifecycle requirement exposed by an approved multi-repository strategy (proposed)"
    EXE-20261127T024457548Z-5edaf2a9:
      operations: [modified]
      at: 2026-11-27T02:45:12Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "WI-0119: record owner acceptance (2026-11-27, PR #314): status proposed -> accepted; not implemented"
---

# Cross-repository decision propagation and research-to-requirements traceability

**Status: accepted** (owner, 2026-11-27). **Not implemented.** Proposed under WI-0118; acceptance recorded under
WI-0119. The owner confirmed in session the direction posted on praxis PR #314: "Owner approves the three
proposed decision lifecycle, currency and cross-repository propagation requirements for ratification. Preserve
governance and validation gates." Acceptance does not implement anything; implementation, its tests and the
validation requirements below remain separate work.

## Origin

A research repository produced decisions and a register of implications for other repositories (product
requirements, migration tooling, organizational policy, methodology). Some targets are Praxis-governed, some are
not. Each target had to decide, by hand, how to record the implication and what happened to it. There was no
common shape, so it was hard to see which implications were satisfied, deferred or blocked, and on what source
version they rested. Nothing here is domain-specific.

## What Praxis already provides

- `related_documents` and other reference fields, validated for local identifiers.
- Stable requirement identifiers and a deterministic verification manifest (`RQ-ROS-2026-A037`).
- Cross-repository work groups (`praxis work group create --cross-repository`).
- Open PRs #78 and #81 propose a provenance interchange contract between systems; they carry provenance of
  artifacts, not decision implications and their dispositions. This record does not duplicate them and should
  reuse their reference format if they merge.

**Gap:** no record of "decision D in repository A implies change C in repository B", with an immutable source
reference and a disposition, and no check that a record derived from an external decision cites that decision.

## Requirements

1. **PRX-XRP-001 External decision reference.** A record MAY cite an external decision in `source_decisions`
   with repository, identifier, and an immutable revision (commit SHA). *Acceptance:* validate reports a
   reference with no revision or a non-40-hex revision (warning). Automated. P1.
2. **PRX-XRP-002 Implication record.** A target repository MAY record each received implication with: source
   implication id, source decisions, target area, proposed change, disposition class, acceptance criteria,
   validation requirements, and the local records that realize it. Disposition classes: `already-satisfied`,
   `requirements-updated`, `new-requirement-added`, `existing-decision-superseded`, `research-only`,
   `deferred-behind-gate`, `blocked-unavailable-information`, `requires-owner-decision`. *Acceptance:* validate
   reports an unknown class, or a `deferred-behind-gate` implication without a named gate. Automated. P1.
3. **PRX-XRP-003 Realization check.** An implication with disposition `requirements-updated`,
   `new-requirement-added` or `existing-decision-superseded` names at least one local record that resolves.
   Automated. P1.
4. **PRX-XRP-004 Source status propagation is explicit.** A change of the source decision's authority stage (see
   `RQ-ROS-2026-6C17`) does not change a target record; the target records its own stage. Automated (no
   side-effect test). P0.
5. **PRX-XRP-005 Research-to-requirement trace.** A requirement derived from research cites the research by
   repository-relative path or identifier plus revision, not by copying the research text. *Acceptance:* review
   checklist; validate notes (info) a requirement with `source_decisions` but no revision. P2.
6. **PRX-XRP-006 Domain neutrality.** The schema carries no domain vocabulary; domain terms live in the citing
   repositories. Review. P1.

All fields are optional and additive; non-Praxis repositories can follow the same shape in their own format.

## Acceptance for this record

Accepted by the owner on 2026-11-27, including the items this section previously named for owner
acceptance. Implementation is a separate work item.

## Validation requirements

- Pure validation functions with tests for each finding.
- A fixture with one implication of each disposition class.
