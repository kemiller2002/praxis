---
id: EV-ROS-2026-A070
title: A021 R2 blind evaluation, unblinding, and replication comparison
status: review
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-01
updated: 2026-10-01
research_area: repository-operating-system
evidence_type: primary
supports: [HY-ROS-2026-A028]
related_documents:
  - EX-ROS-2026-A021
  - HY-ROS-2026-A028
  - EX-ROS-2026-A022
  - research/experiments/EX-ROS-2026-A021-R2-blind/output/EVALUATION.txt
tags: [planning, grouping, replication, blind-evaluation, context, cost]
confidence: low
derived_from: [EX-ROS-2026-A021]
---

# Evidence

## Claim

The second execution of the A021 high-affinity work-group experiment supports
HY-ROS-2026-A028. The grouped execution again produced the more coherent
cross-item architecture while preserving comparable acceptance quality, and
R2 directly measured substantially lower platform cost and elapsed time.

This evidence does not support a general claim that waterfall development is
superior to iterative development. The supported mechanism is narrower:
strongly related work sharing invariants, storage and command infrastructure
benefits from one reasoning context that can make cross-item decisions once.

## Blind evaluation

The blind evaluation was committed before unblinding on
blind/a021-r2/evaluation at
bf8baece65d70c266bda35e859dd9ab1b0aa27b5.

The evaluator reported two confirmed acceptance defects in each arm. Without
knowing the mapping, it found Arm N stronger in shared admission rules,
repository invariants, locking, one-store persistence and JSON/mutation
cohesion; Arm M was stronger in checkpoint-specific integrity and validation.

## Unblinding

After the blind report was durable, exact source/test blob identity established:
- Arm N = experiment/a021-r2-grouped.
- Arm M = experiment/a021-r2-control.

The hidden salt used for the precommitted mapping hash was not present in the
repository or recoverable prior context, so the evaluator could not recompute
the commitment hash. Mapping identity itself is independently established by
exact implementation blobs.

## R2 resource evidence

Grouped:
- 55m53s elapsed
- $10.7536464 platform cost
- 132,902 output tokens
- 27,911,832 cache-read tokens

Independent/control:
- 2h23m35s elapsed
- $26.0492768 platform cost
- 262,893 output tokens
- 62,960,709 cache-read tokens

Control/grouped ratios:
- cost 2.4224x
- elapsed 2.5693x
- output 1.9781x
- cache-read 2.2557x

Grouped reductions:
- cost 58.72%
- elapsed 61.08%

## Replication comparison

The original A021 blind evaluation found the grouped arm had one store, one
member-validation rule, one classification, one error/JSON style and one
mutation pipeline, while the independent arm fragmented the same feature
across multiple stores, rules, classifications and error/JSON patterns. The
independent arm nevertheless had stronger fidelity in several local details.

R2 repeats that shape:
- grouped again centralizes cross-item invariants and transactional mutation;
- control again has stronger local checkpoint detail;
- confirmed acceptance-defect count is tied at two each.

Original A021 telemetry also showed less repeated work for grouped: 114 versus
237 model requests, 110,627 versus 176,304 output tokens, 52 versus 134 file
reads, 53 versus 159 searches and 16 versus 34 builds.

## Threats

R2 control GROUP-02 started from a stale baseline checkout and only later
merged GROUP-01. This can increase the architectural drift being measured.
GROUP-03 also started stale but merged prior work before implementation.

Both runs use the same repository, baseline and feature family. They are two
executions of one setup, not independent-domain replication.

## Assessment

HY-ROS-2026-A028 is supported at low confidence.

The evidence now contains two executions with the same directional
architectural result plus an R2 direct platform-cost/time result. Confidence
remains low because the cohort/domain is not independent and R2 control had a
stale-checkout deviation.

A different-subsystem high-affinity replication and a low-affinity negative
control are the appropriate next tests.
