---
id: HY-ROS-2026-A028
title: One shared reasoning context for strongly related work reduces repeated work or improves architectural consistency
research_area: repository-operating-system
status: supported
confidence: low
created: 2026-09-30
supporting_evidence: [EV-ROS-2026-A060]
contradicting_evidence: []
related_theories: []
related_documents:
  - requirements/PLANNING-WORK-GROUPS.md
  - EX-ROS-2026-A021
  - EV-ROS-2026-A059
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

## Current assessment

Supported at low confidence by one run (`EV-ROS-2026-A060`): the grouped
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
