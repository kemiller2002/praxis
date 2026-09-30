---
id: EV-ROS-2026-A059
title: "Work-group planner baseline on Praxis: no pre-existing cohort, a captured phase-two cohort, and frozen predictions for EX-ROS-2026-A021"
status: review
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-30
updated: 2026-09-30
research_area: repository-operating-system
evidence_type: primary
supports: [DF-ROS-2026-A047, HY-ROS-2026-A027]
related_documents:
  - EX-ROS-2026-A021
  - DF-ROS-2026-A047
  - requirements/PLANNING-WORK-GROUPS.md
  - docs/planning.md
tags: [planning, grouping, shadow-experiment, baseline]
confidence: medium
provenance:
  contributions:
    EXE-20260930T102847967Z-f42c2262:
      operations: [created]
      at: 2026-09-30T11:17:11.207Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "WI-0064: evidence-based work groups and the frozen grouping experiment"
---

# Work-group planner baseline

## Setup (verified)

`./ros plan analyze|compare|groups --json` with the default configuration,
and `plan groups|explain-group GROUP-PRAXIS-WORK-GROUP-001|simulate
--groups|compare --groups --json` with
`research/experiments/EX-ROS-2026-A021-baseline/planner-config.json`, all
`--as-of 2026-09-30T11:13:41.000Z`, at commit
`2d0ec5c0b8b1b4f5de3f70ab4af480de78db798c` on `claude/plan-work-groups`
(work-state fingerprint `231cfac400a74b55...`). Every command ran twice and
produced byte-identical documents. A SHA-256 over every file under `.ros/`
was identical before and after all runs, and `git status` showed no change
outside the output directory. The documents and their SHA-256 sums are in
`research/experiments/EX-ROS-2026-A021-baseline/`. No recommendation was
executed.

## Finding 1: the repository had no cohort to test grouping on

Before `PRAXIS-GROUP-01..05` were captured, the inventory held 15
nonterminal items (8 captured, 1 ready, 2 active, 1 blocked, 1 awaiting
evidence, 2 stale-state candidates). The default configuration recommended
two groups, both pairs below the preferred size of 3..10:
`ATTR-COMPLETE-BASE-REF-SWEEP` + `ATTR-RECONCILE-SYMLINK-SUBMODULE` (high:
two shared tags and both cite `DF-ROS-2026-A040`) and `PRAXIS-STATE-MERGE-01`
+ `WORK-CAPTURE-ID-COLLISION` (medium: one shared tag). No set of 5..10
related open items existed, so PRX-GRP-081's cohort could not be drawn from
existing work without padding it with unrelated items, which PRX-GRP-031
forbids. The five phase-two commands of PRX-GRP-073 were captured as the
cohort instead (see `EX-ROS-2026-A021`, "Cohort").

## Finding 2: the planner recommends the cohort on its own evidence

With the default configuration (no declaration), `plan groups` placed
`PRAXIS-GROUP-01..05` in one `high`-affinity group: two shared explicit tags,
a shared requirement `PRX-GRP-073` (derived), inferred dependencies on
`PRAXIS-GROUP-01`, and inferred ID-family and title signals it labels
`inferred` with low confidence. It ordered `01` first and recommended one
sequential agent.

## Finding 3: one broad tag attached a looser item to a strong core

The same default run also admitted `WI-0064` (the grouping planner itself)
to that group through a single `medium` signal, the shared tag `cli`, which
links it to every member and so satisfies the two-thirds cohesion rule. The
relation is real but weaker than the core's. The experiment pins its cohort
through a human declaration, which outranks inference; the observation is
evidence for a future refinement (for example requiring an admitted member to
reach the group's median level, or treating `cli`-like tags as generic),
not a reason to tune the rule on one case.

## Finding 4: scheduling facts for the cohort (frozen predictions)

All 10 member pairs are `conflict` (declared shared paths) on top of the
shared Praxis state files, so the planner predicts a safe concurrency of 1 in
both arms; high affinity and parallel safety diverge exactly as PRX-GRP-062
requires. Critical path `PRAXIS-GROUP-01 -> 02 -> 05`. Each member up to
about 9 min (medium confidence), so either arm up to about 45 min. Control:
5 executions and context acquisitions; grouped: 1 and 1.

## Finding 5: what the planner cannot predict

Grouped and independent durations are the same estimate: the planner counts
context acquisitions but has no measurement of what one costs, so context
reuse stays `unknown` (PRX-GRP-061). Monetary cost is unavailable (0 of 160
sampled executions carry usable cost evidence). Per-item duration evidence
remains the weak signal `EV-ROS-2026-A058` described (median productive time
2 minutes, interquartile range 0-9 minutes).

## Finding 6: stale state persists

`GH-84` and `GH-90` are still stale-state candidates, as in
`EV-ROS-2026-A058` one day earlier. That the owner has not yet acted on them
is neither confirmation nor refutation of those findings; it is recorded for
`HY-ROS-2026-A027`.

## Limitations

The cohort and its tags were written by the agent that built the planner and
designed the experiment. Declared paths in the experiment configuration are
the author's prediction of the change surface, labelled explicit because they
are declared, not because they were observed.
