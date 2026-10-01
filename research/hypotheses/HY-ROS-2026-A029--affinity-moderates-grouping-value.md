---
id: HY-ROS-2026-A029
title: Work affinity moderates the value of shared reasoning context
research_area: repository-operating-system
status: active
confidence: very-low
created: 2026-10-01
supporting_evidence: []
contradicting_evidence: []
related_theories: []
related_documents:
  - HY-ROS-2026-A028
  - EV-ROS-2026-A064
  - EV-ROS-2026-A070
  - EX-ROS-2026-A022
  - requirements/PLANNING-WORK-GROUPS.md
supersedes: []
superseded_by: []
tags: [planning, grouping, affinity, context, experiment]
---

# Hypothesis

## Statement

The benefit of preserving one reasoning context across multiple software work
items is moderated by the affinity of those items. Grouping should provide a
larger reduction in repeated context acquisition and cross-item architectural
drift for a high-affinity cohort than for an otherwise comparable low-affinity
cohort.

This is a mechanism hypothesis, not a claim that larger batches are generally
better. Low-affinity work may gain little from shared context and may instead
pay context-pressure and coordination costs.

## Mechanism

A grouped execution has two competing effects:

1. it amortizes context acquisition when several items share domain concepts,
   invariants, files, tests, APIs, storage, or architectural decisions;
2. it increases the amount of simultaneously relevant material that the agent
   must retain and reconcile.

High-affinity work should have a high ratio of reusable context to unrelated
context. Low-affinity work should have a lower ratio. The useful reasoning
cohort should therefore depend on cohesion rather than a fixed item count.

## Predictions

For matched cohorts executed under the same model, runtime and baseline:

1. the independent/grouped cost and elapsed-time ratios should be larger for
   the high-affinity cohort than for the low-affinity cohort;
2. high-affinity grouped execution should show fewer repeated discovery reads,
   searches and architecture rediscovery than its independent control;
3. the architectural-consistency advantage of grouping should be larger in the
   high-affinity cohort;
4. low-affinity grouping may show little resource benefit, no architecture
   benefit, or measurable context-pressure costs;
5. acceptance quality must remain comparable. Resource savings obtained by
   omitting requirements or weakening verification do not support the
   hypothesis.

## Falsification

The hypothesis is weakened if the low-affinity cohort receives the same or a
larger grouping benefit than the high-affinity cohort on the pre-registered
resource and context-reuse measures, or if the high-affinity grouped arm loses
acceptance quality or accumulates enough context pressure to erase the reuse
benefit.

If both affinity levels benefit similarly, the more plausible mechanism is
general session/setup-cost amortization rather than work cohesion. If neither
benefits, the A021 result may be specific to its feature family or execution
conditions.

## Evidence state

The original A021 run and R2 both support the high-affinity half of the
mechanism, but neither contains a low-affinity control. They therefore do not
yet test this interaction directly. `EX-ROS-2026-A022` is the pre-registered
test.
