---
id: RQ-ROS-2026-6C17
title: Decision records distinguish strategic approval from implementation authorization through an ordered authority-stage ladder with conditional gates that never auto-advance
status: accepted
version: 0.2.0
owners: [repository-governance]
created: 2026-11-26
updated: 2026-11-27
research_area: repository-operating-system
priority: medium
related_documents:
  - RQ-ROS-2026-0863
  - RQ-ROS-2026-6D60
  - framework/protocols/ARTIFACT-LIFECYCLE.md
  - framework/protocols/SUPERSESSION.md
tags: [decisions, authority, approval, authorization, gates, lifecycle]
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

# Decision authority stages and conditional gates

**Status: accepted** (owner, 2026-11-27). **Not implemented.** Proposed under WI-0118; acceptance recorded under
WI-0119. The owner confirmed in session the direction posted on praxis PR #314: "Owner approves the three
proposed decision lifecycle, currency and cross-repository propagation requirements for ratification. Preserve
governance and validation gates." Acceptance does not implement anything; implementation, its tests and the
validation requirements below remain separate work.

## Origin

A repository owner approved a set of strategic decisions in a research repository and, in the same act,
withheld expenditure, contracts and deployment until evidence gates were met. The approval had to propagate to
several Praxis-governed and non-Praxis repositories. Praxis could record each decision only as `accepted`, which
a later reader can take to mean "authorized to build". At least one adopter already added an ad hoc
`approval_stage` front-matter field to keep the stages apart. Nothing in this record is specific to that domain.

## What Praxis already provides

- `DF` lifecycle `draft → review → accepted → superseded | withdrawn` (`framework/protocols/ARTIFACT-LIFECYCLE.md`),
  validated by `praxis validate`.
- Reciprocal, acyclic supersession (`framework/protocols/SUPERSESSION.md`).
- `RQ` lifecycle `draft → proposed → accepted → implemented → verified`.
- Owner-signed run authorization and verified completion for **work** (`RQ-ROS-2026-A032`, `RQ-ROS-2026-A039`).
- Open PR #302 (PRX-LEARN-001) governs **improvement proposals** learned from failures, with human approval and
  regression guards. It does not cover decision authority stages; this record does not duplicate it.

**Gap:** a decision's lifecycle status (`accepted`) says the record is settled; it does not say what the
acceptance authorizes, which conditions remain, or whether implementation is authorized.

## Requirements

Each item: statement · rationale · acceptance · verification · priority.

1. **PRX-DAS-001 Authority stage field.** A `DF` record MAY carry `authority_stage` with one value from the
   ordered ladder `strategically-approved`, `requirements-incorporated`, `evidence-gate-satisfied`,
   `implementation-authorized`, `implemented`, `validated`. *Rationale:* separate what was approved from what
   may be done. *Acceptance:* `praxis validate` reports an unknown value as an error. *Verification:* automated.
   P1.
2. **PRX-DAS-002 Stage history, append-only.** Each stage reached is recorded in `authority_history` with
   date, actor, and evidence or authorizing act. Earlier entries are never rewritten. *Acceptance:* validate
   reports a history whose order contradicts the ladder, or a current stage absent from history. Automated. P1.
3. **PRX-DAS-003 No auto-advance.** No Praxis command advances `authority_stage` as a side effect of another
   transition (work completion, registry build, acceptance). *Acceptance:* tests show work completion leaves a
   linked decision's stage unchanged. Automated. P0.
4. **PRX-DAS-004 Human authorization for authorizing stages.** Advancing to `implementation-authorized` or
   `validated` records an actor of kind `human`; an agent may record `requirements-incorporated` and
   `evidence-gate-satisfied` with evidence, but not authorize. *Rationale:* consistent with PRX-LEARN-001's
   human-approval guard. *Acceptance:* validate reports an agent actor on an authorizing stage. Automated
   (provenance is self-reported, not authenticated; the limit is documented). P1.
5. **PRX-DAS-005 Conditional gates.** A `DF` record MAY list `conditions`, each with an id, statement, kind
   (`evidence`, `approval`, `time`, `external`), state (`open`, `met`, `waived`, `failed`) and, when not `open`,
   the evidence or decision that changed it. `evidence-gate-satisfied` requires every evidence condition to be
   `met` or `waived` with a recorded waiver decision. *Acceptance:* validate reports a stage inconsistent with
   its conditions. Automated. P1.
6. **PRX-DAS-006 Approval scope statement.** An `accepted` record with an `authority_stage` below
   `implementation-authorized` states what it does not authorize in `not_authorized` (for example spending,
   contracts, deployment). *Acceptance:* validate notes (info) its absence. Automated. P2.
7. **PRX-DAS-007 Reporting.** `praxis status` and the decision registry show each decision's authority stage and
   open conditions. *Acceptance:* registry entries include the fields. Automated. P2.

All fields are optional and additive: records without them validate exactly as today.

## Acceptance for this record

Accepted by the owner on 2026-11-27, including the items this section previously named for owner
acceptance. Implementation is a separate work item.

## Validation requirements

- Unit tests over a pure validation function for each finding above, including negative cases.
- A migration check: every existing `DF` record in this repository validates unchanged.
