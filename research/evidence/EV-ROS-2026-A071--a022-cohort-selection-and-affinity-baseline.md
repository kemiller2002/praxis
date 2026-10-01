---
id: EV-ROS-2026-A071
title: "EX-ROS-2026-A022 frozen baseline, cohort selection and pre-execution affinity analysis"
status: draft
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-01
updated: 2026-10-01
research_area: repository-operating-system
evidence_type: primary
supports: []
contradicts: []
related_documents:
  - EX-ROS-2026-A022
  - HY-ROS-2026-A029
  - HY-ROS-2026-A028
  - EV-ROS-2026-A059
  - research/experiments/EX-ROS-2026-A022-setup/README.txt
tags: [planning, grouping, affinity, cohort, baseline, experiment]
confidence: medium
provenance:
  contributions:
    EXE-20261001T105134505Z-4886c699:
      operations: [created]
      at: 2026-10-01T11:03:42.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "EXP-A022-DESIGN: frozen baseline, cohort selection and pre-execution affinity analysis"
derived_from: [EV-ROS-2026-A059]
---

# Evidence

## Claim

Before any experimental condition ran, `EX-ROS-2026-A022` froze a baseline
commit and two five-item cohorts whose affinity was classified by a
pre-registered, reproducible rule over pre-execution observations: one
cohort classifies HIGH and one LOW. This record is the selection evidence
for the experiment (as `EV-ROS-2026-A059` was for `EX-ROS-2026-A021`). It
supports no hypothesis by itself.

ID note: `EV-ROS-2026-A070` is left unused for the A021-R2 post-unblinding
record, which on branch `experiment/a021-r2-post-unblind` collides with the
existing `EV-ROS-2026-A063` and needs renumbering.

## Observations

1. **Baseline.** `f806f817e4656e00550313d839b59ceea87a682d` = main `f68565f`
   plus two Praxis-state-only commits (cohort items captured, given explicit
   acceptance criteria and triaged; durable checkpoint). CI green (run
   36851089199). Details: `research/experiments/EX-ROS-2026-A022-setup/baseline.txt`.
2. **Inventory.** As in A021, the backlog had no pre-existing open cohort of
   five or more items in one architectural area; 62 of 66 rows marked ready
   were complete in the live context. Full inventory, sources and rejected
   candidates: `.../cohorts.txt`.
3. **High-affinity cohort** (requirements/EXECUTION-ORCHESTRATION.md,
   local control plane; captured at the baseline by the designer along
   requirement-ID boundaries): `PRAXIS-CTL-02`, `-03`, `-04`, `-05`, `-06`.
4. **Low-affinity cohort** (five subsystems; four pre-existing items and
   GitHub #56): `ACTOR-KIND-ANCHOR`, `ATTR-RECONCILE-SYMLINK-SUBMODULE`,
   `PRAXIS-CN-GL-KINDS`, `PRAXIS-PLAN-ERROR-HISTORY`, `PRAXIS-STATE-MERGE-01`.
5. **Affinity model.** Twelve observable dimensions per pair (concepts,
   durable state, invariants, files, public surfaces, producer/consumer,
   ordering, validation rules, error model, transaction boundary, tests,
   assumption propagation), five of them "strong"; generic elements
   excluded; no composite score. Classification thresholds were written
   before the first computation (`.../affinity/model.txt`).
6. **First computation** (commit `525b7ad`): the initial six-item high
   cohort classified NEITHER because `PRAXIS-CTL-01` had no strong dimension
   with three members; it was removed by rule, and the low cohort reduced to
   five by the pre-stated size-parity rule (smallest stated scope:
   `PRAXIS-TELEMETRY-CLASSIFY-VOCAB`). `.../affinity/selection-log.txt`.
7. **Frozen result.** High: every pair has at least one strong dimension,
   median 7 of 12 dimensions, 9 of 10 pairs with two or more strong
   dimensions -> HIGH. Low: no pair has more than one strong dimension, 1 of
   10 pairs has one (a shared event store), median 0 -> LOW.
   `.../affinity/affinity-matrix.txt`.
8. **Planner corroboration.** Default configuration: one control-plane group
   (affinity medium) for the high cohort; no two low items grouped. With
   declared areas: high cohort affinity high; but `ACTOR-KIND-ANCHOR` and
   `PRAXIS-CN-GL-KINDS` are paired at high through one shared declared file,
   a discrepancy with the primary matrix caused by the planner's
   any-path-overlap-is-high signal. Recorded, selection unchanged.
   `.../planner/README.txt`.
9. **Rejected pairs show the matrix discriminates:** the excluded
   `WORK-CAPTURE-ID-COLLISION` x `PRAXIS-STATE-MERGE-01` pair has three
   strong dimensions and `ATTR-COMPLETE-BASE-REF-SWEEP` x
   `ATTR-RECONCILE-SYMLINK-SUBMODULE` two.

## Limitations

- Observations and relations are a designer's judgment from descriptions and
  baseline code; they are auditable and frozen, not objective.
- The high cohort's items and criteria were written by the designer; the
  low cohort's are mostly pre-existing.
- Novelty, size and complexity are not equal across cohorts (criteria 27 vs
  26; five medium items vs a mix), and partly overlap with affinity.
- The planner reads metadata the designer wrote for the new items.

## Reproduction

    python3 research/experiments/EX-ROS-2026-A022-setup/affinity/compute_affinity.py --check
    python3 research/experiments/EX-ROS-2026-A022-setup/criteria/export_criteria.py --check
    (cd research/experiments/EX-ROS-2026-A022-setup && sha256sum -c SHA256SUMS)
