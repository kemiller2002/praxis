---
id: RQ-ROS-2026-0863
title: Decision records carry review dates, evidence as-of dates, monitored assumptions and invalidation conditions, and Praxis reports overdue decisions and citations of superseded decisions
status: accepted
version: 0.2.0
owners: [repository-governance]
created: 2026-11-26
updated: 2026-11-27
research_area: repository-operating-system
priority: medium
related_documents:
  - RQ-ROS-2026-6C17
  - RQ-ROS-2026-6D60
  - framework/protocols/SUPERSESSION.md
  - framework/standards/CONFIDENCE.md
tags: [decisions, freshness, assumptions, invalidation, supersession, consistency]
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

# Decision currency: freshness, assumptions and invalidation

**Status: accepted** (owner, 2026-11-27). **Not implemented.** Proposed under WI-0118; acceptance recorded under
WI-0119. The owner confirmed in session the direction posted on praxis PR #314: "Owner approves the three
proposed decision lifecycle, currency and cross-repository propagation requirements for ratification. Preserve
governance and validation gates." Acceptance does not implement anything; implementation, its tests and the
validation requirements below remain separate work.

## Origin

Strategic decisions approved on research evidence depend on assumptions (prices, partner availability,
productivity) and on evidence with a date. Decision records written as prose ("Trigger" or "Reassessment"
sections, as in existing `DF` records) are not checked, so a decision can outlive its evidence silently, and an
active requirement can keep citing a decision that has been superseded. Nothing here is domain-specific.

## What Praxis already provides

- Reciprocal supersession, validated; superseded records stay discoverable.
- Confidence labels (`framework/standards/CONFIDENCE.md`).
- Prose triggers in decision bodies (for example `DF-ECHELON-FOUNDRY-FND-2026-0001` in a governed repository).
- Clarity's decision record (outside Praxis) defines triggers, failure detection and review dates in prose.

**Gap:** no machine-readable review date, evidence date, assumption or invalidation condition; no report of
overdue decisions; no warning when an active record cites a superseded or withdrawn decision.

## Requirements

1. **PRX-DCR-001 Review date.** A `DF` record MAY carry `review_by` (date) and `review_cycle`. *Acceptance:*
   `praxis validate` reports (warning) an `accepted` decision whose `review_by` is in the past. Automated. P1.
2. **PRX-DCR-002 Evidence as-of.** A `DF` record MAY carry `evidence_as_of` (date). Where a cited `EV` record
   has a later `updated` date, validate notes it (info). Automated. P2.
3. **PRX-DCR-003 Monitored assumptions.** A `DF` record MAY list `assumptions`, each with id, statement, owner,
   monitoring signal, threshold and state (`holding`, `breached`, `unknown`). A `breached` assumption on an
   `accepted` decision is reported (warning) until the decision is reviewed, superseded or withdrawn. Automated.
   P1.
4. **PRX-DCR-004 Invalidation conditions.** A `DF` record MAY list `invalidated_if`: observations that, if they
   occur, mean the decision's thesis no longer holds. Recording one as observed (with evidence) requires the
   decision to be reviewed; validate reports an observed invalidation on an unreviewed `accepted` decision
   (error). Automated. P1.
5. **PRX-DCR-005 Superseded-citation check.** Validate reports (warning) an `accepted`, `implemented` or
   `verified` record whose reference fields cite a `superseded` or `withdrawn` decision, naming the successor
   when one exists. Automated. P1.
6. **PRX-DCR-006 Currency report.** `praxis status` lists decisions that are overdue, have breached assumptions
   or observed invalidations. Automated. P2.
7. **PRX-DCR-007 No silent change.** Recording a breach or invalidation never changes a decision's status or
   authority stage by itself (see `RQ-ROS-2026-6C17` PRX-DAS-003). Automated. P0.

All fields are optional and additive.

## Acceptance for this record

Accepted by the owner on 2026-11-27, including the items this section previously named for owner
acceptance. Implementation is a separate work item.

## Validation requirements

- Pure, date-injected validation functions (no wall-clock reads in the domain) with tests for each finding,
  including a decision exactly on its review date.
- Every existing record in this repository validates unchanged.
