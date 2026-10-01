---
id: HY-ROS-2026-A029
title: Architectural cohesion moderates the value of one shared reasoning context over independent executions
research_area: repository-operating-system
status: proposed
confidence: very-low
created: 2026-10-01
supporting_evidence: []
contradicting_evidence: []
related_theories: []
related_documents:
  - HY-ROS-2026-A028
  - EX-ROS-2026-A021
  - EX-ROS-2026-A022
  - EV-ROS-2026-A063
  - EV-ROS-2026-A064
  - EV-ROS-2026-A071
  - requirements/PLANNING-WORK-GROUPS.md
supersedes: []
superseded_by: []
tags: [planning, grouping, context, cohesion, affinity, experiment]
provenance:
  contributions:
    EXE-20261001T105134505Z-4886c699:
      operations: [created, modified]
      at: 2026-10-01T11:03:41.000Z
      last: 2026-10-01T11:18:32.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "EXP-A022-DESIGN: cohesion-moderation hypothesis narrowing HY-ROS-2026-A028"
derived_from: [HY-ROS-2026-A028]
---

# Hypothesis

## Statement

**Cohesion moderates the value of shared execution context.** The advantage
of executing a cohort of work items in one continuing reasoning context
(grouped execution) over executing each item in a fresh context (independent
execution), in execution efficiency and in cross-item architectural
consistency, is greater for a cohort whose items are architecturally cohesive
(high affinity: shared state, invariants, contracts, producer/consumer
relations) than for a cohort whose items are architecturally independent
(low affinity), at no material cost in acceptance quality for the
high-affinity cohort.

This is a claim about an interaction (cohort affinity x execution topology),
not a claim that planning everything up front is better than iterating, and
not a claim that grouping helps in general. It narrows `HY-ROS-2026-A028`,
which A021 and its replication supported at low confidence for one
high-affinity cohort only.

## Mechanism

`HY-ROS-2026-A028` ("Plan Globally, Execute Incrementally") proposes that a
shared context saves context reacquisition (reading the same governance
documents, modules and requirements again) and lets cross-item decisions
(one store, one contract, one refusal model) be made once. Both savings
should scale with how much the items actually share: unrelated items have
little common context to reuse and few cross-item decisions to make, while
a single long context still pays context pressure. So the grouped advantage
should shrink, vanish or reverse as affinity falls.

## Predictions

Pre-registered in `EX-ROS-2026-A022` (2x2: high/low affinity x
grouped/independent; affinity defined before execution by the model in
`research/experiments/EX-ROS-2026-A022-setup/affinity/model.txt`):

1. High affinity: grouped execution repeats less context acquisition and
   rework than independent execution and shows fewer cross-item
   architectural inconsistencies, without a material loss in acceptance
   quality.
2. The grouped-over-independent resource advantage (independent/grouped
   ratios of platform cost, output tokens and summed active session time) is
   materially greater for the high-affinity cohort than for the
   low-affinity cohort.
3. The grouped-over-independent consistency advantage is greater for the
   high-affinity cohort. (For low affinity there are few cross-item
   decisions to get right, so a small difference is expected by
   construction; prediction 2 is the stronger test.)
4. Grouped execution is not required to win the low-affinity comparison.

## Evidence that would support it

Outcome A or E of `EX-ROS-2026-A022`: a clear grouped advantage for the
high-affinity cohort with little, none, or a reversed advantage for the
low-affinity cohort.

## Evidence that would contradict it

- Outcome B: a similar grouped advantage at both affinity levels (supports a
  general shared-context effect; weakens cohesion as the moderator).
- Outcome C: no meaningful grouped advantage at either level (weakens this
  hypothesis and `HY-ROS-2026-A028`).
- Outcome D: grouped acceptance quality materially worse despite resource
  savings (weakens the claim of net engineering value).
- A larger grouped advantage for the low-affinity cohort than for the
  high-affinity cohort.

## Tests performed

None yet. `EX-ROS-2026-A022` is designed and frozen; no condition has run.

## Results

None.

## Falsification attempts

None yet.

## Current assessment

Proposed and untested. Prior evidence comes only from high-affinity cohorts
(`EV-ROS-2026-A064`; the A021-R2 replication on branch
`experiment/a021-r2-post-unblind`, not yet merged), so it cannot speak to the
moderation claim. One 2x2 run will be one observation per cell: it can
classify an outcome, not establish a causal law.

## Product implication (future, not adopted)

If supported, the Praxis planner could estimate work-item affinity before
execution and choose an execution topology: one shared reasoning context for
a strongly coupled cohort, independent or parallel contexts for weakly
coupled work, and partition into affinity clusters for an intermediate
cohort. Nothing in Praxis changes on the strength of this hypothesis until
evidence supports it.

## Next experiment

`EX-ROS-2026-A022`.
