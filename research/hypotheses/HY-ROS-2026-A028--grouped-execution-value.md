---
id: HY-ROS-2026-A028
title: One shared reasoning context for strongly related work reduces repeated work or improves architectural consistency
research_area: repository-operating-system
status: supported
confidence: low
created: 2026-09-30
supporting_evidence: [EV-ROS-2026-A063, EV-ROS-2026-A064]
contradicting_evidence: []
related_theories: []
related_documents:
  - requirements/PLANNING-WORK-GROUPS.md
  - EX-ROS-2026-A021
  - EV-ROS-2026-A059
  - EV-ROS-2026-A063
  - EV-ROS-2026-A064
supersedes: []
superseded_by: []
tags: [planning, grouping, context, experiment]
provenance:
  contributions:
    EXE-20260930T102847967Z-f42c2262:
      operations: [created]
      at: 2026-09-30T11:17:09.632Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "WI-0064: evidence-based work groups and the frozen grouping experiment"
    EXE-20260930T114749635Z-ba7301df:
      operations: [modified]
      at: 2026-09-30T17:18:12.991Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PLAN-EXP-01: results of the grouping experiment EX-ROS-2026-A021"
    EXE-20260930T172030686Z-71d403ef:
      operations: [modified]
      at: 2026-09-30T17:20:33.432Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PLAN-06: renumber the A021 results record to EV-ROS-2026-A064 and relate it to the parallel evaluation kit and EV-ROS-2026-A063"
    EXE-20260930T175751373Z-7eb9c757:
      operations: [modified]
      at: 2026-09-30T17:57:56.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PLAN-07: merge-conflict resolution combining the completed A021 result (EV-ROS-2026-A064) with the owner's PGEI mechanism (EV-ROS-2026-A063)"
---

# Hypothesis

## Statement

Hypothesis B of PRX-GRP-080 (grouping value): executing strongly related work
items in one shared reasoning context reduces repeated work, improves
architectural consistency, or both, compared with independent fresh
executions of the same items from the same baseline.

## Predictions and measures

Measured in `EX-ROS-2026-A021` on one cohort with high planner affinity.
Repeated work: repeated reads of the same files and of `AGENTS.md` and
architecture documents, repeated searches and module exploration, repeated
test discovery, time to first productive change, tokens (input, output,
cached), tool calls, model requests, compactions. Consistency: evaluator
findings on duplicated abstractions, conflicting API or storage patterns,
design revisions and rework, recorded as evidence rather than a score
(PRX-GRP-084..085). Quality must not fall: acceptance criteria passed,
regressions, defects.

## Falsification

Weakened if the grouped arm repeats no less work than the control arm, shows
no fewer architectural inconsistencies, or loses quality; or if context
pressure (compactions, forgotten requirements, late inconsistent decisions)
outweighs the reuse. One experiment cannot establish causation
(PRX-GRP-087); the outcome is classified, not declared universal.

## Proposed mechanism: Plan Globally, Execute Incrementally

`EV-ROS-2026-A063` records a preliminary mechanism suggested by the first
A021 execution data: for high-affinity work, reason about the whole cohort and
its architecture before production mutation, then implement, test, commit and
validate the individual obligations incrementally.

This is called **Plan Globally, Execute Incrementally (PGEI)**. It is not a
claim for classic waterfall. Feedback remains continuous and evidence that
invalidates an architectural assumption causes replanning. The distinction is
between sharing the expensive architectural/context acquisition across related
work and treating every work item as an independent reasoning problem.

The mechanism predicts two opposing costs:

- **context reacquisition** generally falls as sufficiently related work is
  grouped into one reasoning context;
- **context pressure** eventually rises as a cohort becomes too large,
  increasing compaction, forgotten constraints or inconsistent late
  decisions.

Coordination and rework add a third cost. The useful scheduler therefore seeks
a cohesion-dependent **reasoning cohort**, not a fixed batch size:

`total(group) = reacquisition + context pressure + coordination/rework`

The optimum may be one item for unrelated work, several items for tightly
coupled work, or a shared-analysis/separate-execution hybrid between those
extremes.

## Additional predictions

If PGEI explains the effect rather than simple batching alone:

1. a high-affinity grouped arm should spend less effort rediscovering the same
   modules, requirements and architectural facts;
2. it should converge on fewer conflicting storage/API abstractions without
   reducing acceptance-criteria or regression quality;
3. independent execution should remain competitive or superior for
   low-affinity work where context cannot be reused;
4. increasing group size should eventually show context-pressure costs,
   yielding a measurable cohesion/size threshold;
5. shared reasoning must not erase per-item lifecycle, attribution,
   checkpoints, acceptance criteria or evidence.

These predictions make the mechanism falsifiable separately from the broad
claim that grouping sometimes helps.

## Current assessment

Supported at low confidence by one run (`EV-ROS-2026-A064`): the grouped
execution repeated less context (AGENTS.md 1 versus 5 reads, governance
documents 6 versus 15, one cold start), cost $9.48 against $21.51, and
produced one consistent model where independent executions produced several;
independent executions were more faithful to individual criteria and tested
more broadly. No context pressure at five members. Replication with a
loosely related cohort and another repository is needed before any default.

Before the experiment: The planner can count context acquisitions (five
independent against one grouped for the proposed cohort) but has no
measurement of what an acquisition costs, so every saving is unknown until
the experiment runs.

`EV-ROS-2026-A063` was an interim reading taken after three of five control
sessions; the completed run in `EV-ROS-2026-A064` is consistent with it. The
PGEI mechanism above remains a proposed explanation: the run shows reduced
reacquisition, not yet the cohesion/size threshold or the low-affinity
predictions, which need the replication cohorts.
