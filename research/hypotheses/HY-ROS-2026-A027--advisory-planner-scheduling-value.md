---
id: HY-ROS-2026-A027
title: The advisory planner makes useful scheduling recommendations without mutating state
research_area: repository-operating-system
status: active
confidence: low
created: 2026-09-30
supporting_evidence: [EV-ROS-2026-A058, EV-ROS-2026-A059, EV-ROS-2026-A060]
contradicting_evidence: []
related_theories: []
related_documents:
  - requirements/PLANNING-WORK-GROUPS.md
  - requirements/PLANNING-OPTIMIZATION.md
  - DF-ROS-2026-A046
  - EX-ROS-2026-A021
supersedes: []
superseded_by: []
tags: [planning, scheduling, experiment]
provenance:
  contributions:
    EXE-20260930T102847967Z-f42c2262:
      operations: [created]
      at: 2026-09-30T11:17:08.787Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "WI-0064: evidence-based work groups and the frozen grouping experiment"
    EXE-20260930T114749635Z-ba7301df:
      operations: [modified]
      at: 2026-09-30T17:18:12.340Z
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

Hypothesis A of PRX-GRP-080 (scheduling value): `praxis plan` makes useful
recommendations about stale work, dependencies, critical path, safe
concurrency and remaining work without mutating any state.

## Predictions and measures

Measured in `EX-ROS-2026-A021` by comparing the frozen baseline documents of
`EV-ROS-2026-A059` with what happens: stale-state findings that the owner
confirms and reconciles; dependency order the implementers actually needed;
whether the predicted critical path (`PRAXIS-GROUP-01 -> PRAXIS-GROUP-02 ->
PRAXIS-GROUP-05`) held; whether members the planner rated `conflict` did
collide when run as independent executions; predicted versus observed
remaining work and duration. Read-only behaviour is measured by hashing
`.ros/` and the Git state around every planner command.

## Falsification

Weakened if the owner rejects most stale-state findings, if implementers need
a different order than the predicted dependency order, if pairs rated
`conflict` merge cleanly while pairs rated `safe` collide, or if predicted
durations miss the observed range for most members. Refuted outright if any
planner command changes repository or Praxis state.

## Current assessment

Active, partly supported (`EV-ROS-2026-A060`). The stale-state findings on
`GH-84` and `GH-90` were acted on (the owner abandoned both); the predicted
dependency order, critical path, `conflict` rating and safe concurrency of one
held in `EX-ROS-2026-A021`; every planner command was read-only. The
duration prediction failed (61 and 112 min observed against an upper bound
of 45), which the falsification criterion names; duration estimation for
implementation work is the part to replace.

Earlier assessment:

Proposed. `EV-ROS-2026-A058` found the stale-state findings correct on this
repository and the duration evidence weak (median productive time about two
minutes because executions often begin just before completion);
`EV-ROS-2026-A059` repeats the read-only check for the grouping commands.
