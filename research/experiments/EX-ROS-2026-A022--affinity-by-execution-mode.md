---
id: EX-ROS-2026-A022
title: Work affinity by execution mode: grouped versus independent 2x2 replication
research_area: repository-operating-system
status: proposed
created: 2026-10-01
tests_hypotheses:
  - HY-ROS-2026-A028
  - HY-ROS-2026-A029
inputs:
  - EV-ROS-2026-A064
  - EV-ROS-2026-A070
outputs: []
related_theories: []
related_documents:
  - requirements/PLANNING-WORK-GROUPS.md
  - HY-ROS-2026-A028
  - HY-ROS-2026-A029
tags: [planning, grouping, affinity, experiment, replication, falsification]
provenance:
  contributions:
    EXE-20261001T160615840Z-adece80d:
      operations: [created]
      at: 2026-10-01T16:06:17.000Z
      actor:
        kind: agent
        id: openai/chatgpt
        provider: openai
        model: gpt-5.6-sol
        runtime: chatgpt
      reason: "Created the pre-registered 2x2 high/low-affinity by grouped/independent falsification experiment."
      evidence: [EV-ROS-2026-A070]
---

# Experiment

## Research question

Does grouped execution help because tightly related work can reuse one
reasoning context, or does grouping simply reduce setup overhead regardless of
cohesion?

This experiment crosses two independent variables:

| | Grouped execution | Independent execution |
| --- | --- | --- |
| High-affinity cohort | H-G | H-I |
| Low-affinity cohort | L-G | L-I |

The primary scientific question is the interaction: whether the grouping
benefit is materially larger for high-affinity work than for low-affinity
work.

## Why this is the next test

`EX-ROS-2026-A021` and its R2 execution both used the same Praxis feature
family. Both found the same directional pattern: grouped execution produced a
more unified cross-item architecture and substantially less repeated reasoning,
while independent execution retained advantages in some local implementation
details. R2 measured 58.72% lower platform cost and 61.08% lower elapsed time
for grouped execution, with two confirmed acceptance defects in each arm
(`EV-ROS-2026-A070`).

Those results do not distinguish a cohesion mechanism from a generic
one-session-versus-many-sessions setup-cost effect. A low-affinity negative
control is therefore required.

## Target-repository gate

The experiment must run in a repository other than `kemiller2002/praxis`.
Prefer an Echelon Foundry application/library repository that already uses
Praxis and has enough real, ready work to form both cohorts.

Before selecting a target:

1. freeze one baseline commit `B`;
2. inventory all ready work at `B` without creating tasks for the experiment;
3. require at least five real high-affinity items and five real low-affinity
   items;
4. reject the repository if either cohort would need padding or newly invented
   obligations;
5. record the repository, `B`, Praxis version, model/runtime and selection
   evidence before any arm starts.

Using both cohorts from the same non-Praxis repository is preferred because it
holds repository conventions, language, tooling and CI constant. If no
repository can supply both cohorts, this experiment does not run; do not
silently substitute two repositories and confound repository with affinity.

## Cohort size and matching

Each cohort contains exactly five work items unless the frozen target
repository has a compelling natural cohort of six; the same item count must be
used for both cohorts.

The two cohorts should be matched as closely as the available real work allows
on:

- planner-estimated implementation effort and confidence;
- number and specificity of acceptance criteria;
- expected production-file count;
- test burden;
- priority and lifecycle readiness.

Matching is recorded before execution and never tuned after observing an arm.

## Pre-registered affinity classification

Affinity is classified from repository evidence before execution.

### High-affinity cohort

The cohort must be rated `high` by the planner and form one connected
architectural problem. Every member must connect to the cohort through at least
two independent signals among:

- explicit dependency or gating edges;
- shared production paths or modules;
- shared domain state, invariants, API, storage or serialization decisions;
- shared tests or acceptance fixtures;
- documented historical co-change;
- shared architecture/feature tags that correspond to actual implementation
  overlap rather than only naming similarity.

At least one signal for each member must be structural or semantic, not merely
a tag.

### Low-affinity cohort

The cohort must be rated `low` or `none` overall and deliberately minimize
shared reasoning. It must have:

- no explicit dependency edge between cohort members;
- no shared domain invariant or common storage/API decision;
- no predicted shared production file;
- no common bounded feature that requires one architectural choice;
- planner evidence that at least two members could safely execute concurrently.

Shared repository instructions, build tooling and ordinary test harnesses do
not make work high-affinity.

### Frozen classification

Save the planner outputs and a human-readable affinity rationale, hash them,
and freeze them before branches are created. The evaluator may challenge the
classification after the blind evaluation, but the cohorts may not be changed.

## Arms

Create four branches from the same baseline `B`:

- `experiment/a022-hg`
- `experiment/a022-hi`
- `experiment/a022-lg`
- `experiment/a022-li`

The branch names are visible only to the orchestrator and implementers. Blind
evaluation artifacts use neutral cohort and arm names.

All arms use the same provider, model identifier, runtime version, repository
instructions, environment and acceptance criteria. Record any unavoidable
drift.

## Grouped-arm procedure

One fresh session handles all members of that cohort.

Before production mutation it must:

1. read all five work items and acceptance criteria;
2. read the frozen affinity/planner evidence;
3. write a cohort analysis covering shared invariants, reusable abstractions,
   dependencies, conflicts, order, common tests, compatibility and risks;
4. commit that analysis before code.

It then implements items incrementally, preserving each item's lifecycle,
acceptance criteria, evidence, commits and checkpoints. Shared infrastructure
must be explicitly identified and attributed.

The grouped session must not read either independent arm.

## Independent-arm procedure

Use one fresh session per work item, serially on one branch in a frozen order.
Each session receives only its item's requirements plus normal repository
state, including earlier committed implementations. It gets no private context
from earlier sessions and must not read either grouped arm.

Serial execution intentionally holds concurrency constant. This experiment
measures shared reasoning context, not the scheduling advantage of running
low-affinity work in parallel.

If an independent session starts from a stale commit, the deviation is recorded
and the affected arm is not silently repaired or rerun. A replacement run
requires a new replication identifier.

## Arm order

Randomize the order in which the four arms are executed after this protocol,
the baseline and both cohorts are frozen. Commit a cryptographic commitment to
the arm-order mapping before the first implementation starts and reveal it only
after all arms finish.

This limits systematic model/runtime drift from always favoring one condition.

## Blinding

Evaluation uses neutral artifacts with two neutral cohorts:

- cohort Alpha: arm 1 and arm 2;
- cohort Beta: arm 1 and arm 2.

The evaluator is told which two implementations correspond to the same cohort
so requirements can be compared, but is not told:

- which arm is grouped versus independent;
- which cohort was classified high versus low affinity;
- cost, token, session-count or elapsed-time data;
- implementation branch names or session histories.

Commit a salted hash of the complete mapping before evaluation. The mapping is
revealed only after the evaluator's report is durable.

## Blind evaluation

A fresh evaluator that implemented none of the work assesses both pairs using
the same acceptance criteria and symmetric probes.

For each implementation it reports facts and concrete findings about:

- acceptance criteria and regressions;
- correctness and defect evidence;
- duplicated or conflicting abstractions;
- consistency of domain rules, storage, APIs and serialization;
- unnecessary dependencies;
- maintainability and future change surface;
- test coverage and missing adversarial cases;
- rework and cross-item architectural consequences.

The evaluator does not produce an overall numeric quality score. It may report
counts of concrete confirmed defects and tests, but architectural conclusions
must cite specific evidence.

## Primary resource/context measures

For each arm collect, with unavailable values recorded as unavailable rather
than zero:

- platform cost;
- elapsed and active time;
- input, output, cache-read and cache-write tokens;
- model requests and tool calls;
- file reads, repeated file reads and searches;
- governance/architecture reads;
- builds and test runs;
- time to first productive code change;
- retries, failed approaches and merge conflicts;
- compactions/context resets.

For each cohort compute, without treating the result as a quality score:

`grouping resource ratio = independent / grouped`

for cost, elapsed time, output tokens, repeated reads/searches and other
lower-is-better resource measures.

The mechanism prediction is:

`high-affinity grouping ratio > low-affinity grouping ratio`

on the pre-registered context-reuse/resource measures.

Do not choose a metric after seeing results.

## Quality guardrail

Resource savings support the hypotheses only if grouped execution does not show
a material loss in acceptance quality.

Before unblinding, the evaluator identifies confirmed defects and architectural
findings. After unblinding, classify each cohort as:

- grouped comparable or better in acceptance quality;
- mixed/local tradeoff;
- grouped materially worse;
- insufficient evidence.

No aggregate winner or quality score is calculated.

## Primary interpretations

The result is classified using the following pre-registered logic:

1. **Affinity mechanism supported:** high-affinity grouping has a clear
   resource/context-reuse advantage and architectural-cohesion advantage over
   H-I, while the low-affinity advantage is materially smaller or absent,
   without a high-affinity quality loss.
2. **Generic setup-cost mechanism:** H-G and L-G show similar resource
   advantages and affinity does not meaningfully change the architecture
   result.
3. **Context-pressure threshold:** H-G benefits, while L-G is neutral or worse
   and shows more irrelevant-context/compaction/coordination evidence.
4. **Prior result does not replicate:** H-G does not reproduce the A021
   direction under a different repository/subsystem.
5. **Quality tradeoff:** grouped execution is cheaper/faster but materially
   worse on acceptance correctness.
6. **Insufficient evidence:** protocol deviations, missing metrics or unmatched
   cohorts prevent a useful inference.

## Falsification criteria

`HY-ROS-2026-A028` is weakened if H-G does not reduce repeated work or improve
architectural consistency relative to H-I, or if it does so only by losing
quality.

`HY-ROS-2026-A029` is weakened if low-affinity grouping provides the same or
larger benefit than high-affinity grouping on the frozen primary measures
without corresponding cohesion evidence.

A null or contrary result is a successful experiment. Do not alter the
classification rules to preserve the hypothesis.

## Stopping and rerun rules

- Do not stop an arm because early metrics look unfavorable.
- Stop only for safety, unrecoverable infrastructure failure, or a protocol
  violation that invalidates the arm.
- Never silently replace a failed session. Record the failure and either keep
  it as part of the arm or start a separately identified replication.
- Do not merge any implementation arm before blind evaluation and unblinding.

## Required artifacts before execution

The orchestrator must commit:

- baseline SHA and repository identity;
- the ten selected work-item specifications;
- matching table;
- frozen planner outputs for both cohorts;
- affinity rationale;
- arm-order commitment;
- blind-mapping commitment;
- exact grouped and independent prompts;
- metric collection procedure;
- evaluator prompt.

Only after these artifacts are durable may any implementation session start.

## Completion

The experiment is complete only when:

1. all four arms have either completed or have a recorded invalidating
   deviation;
2. blind evaluation is committed;
3. mapping and arm order are unblinded;
4. resource/context metrics are computed from recorded sources;
5. predictions and falsification criteria are evaluated;
6. the result is recorded as evidence and both hypotheses are updated;
7. raw and derived evaluation artifacts remain reproducible from immutable
   SHAs.

## Current status

Protocol drafted and pre-registered in the repository on 2026-10-01. No target
repository, baseline, cohorts, arm mapping or implementation result has been
selected yet. Target selection must satisfy the gate above before this record
can move from planned to running.
