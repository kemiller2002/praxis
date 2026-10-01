---
id: HY-ROS-2026-A028
title: One shared reasoning context for strongly related work reduces repeated work or improves architectural consistency
research_area: repository-operating-system
status: supported
confidence: low
created: 2026-09-30
supporting_evidence: [EV-ROS-2026-A063]
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

Supported at low confidence by EV-ROS-2026-A063.

The original A021 execution and its R2 replication show the same directional
architectural result for this high-affinity cohort: one shared reasoning
context produced a more unified cross-item model, while independent execution
retained advantages in some localized implementation details. R2 also directly
measured substantially lower platform cost and elapsed time for grouped
execution.

This is not evidence that waterfall development is generally superior. The
supported claim is narrower: when work items are tightly coupled through shared
domain invariants and infrastructure, preserving one reasoning context can
reduce repeated context acquisition and cross-item architectural drift.

Confidence remains low because both runs use the same repository, baseline and
feature family, and R2 control had a stale-checkout protocol deviation. The
next useful tests are a high-affinity cohort in another subsystem/repository
and a deliberately low-affinity cohort.
