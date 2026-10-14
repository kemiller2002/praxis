---
id: EV-ROS-2026-A058
title: "Advisory planner shadow experiment 1: Praxis's own nonterminal work and a no-hindsight replay of 155 executions"
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-29
updated: 2026-09-29
research_area: repository-operating-system
evidence_type: primary
supports: [DF-ROS-2026-A046]
related_documents:
  - DF-ROS-2026-A046
  - requirements/PLANNING-OPTIMIZATION.md
  - docs/planning.md
tags: [planning, telemetry, shadow-experiment, replay]
confidence: medium
provenance:
  contributions:
    EXE-20260929T172554288Z-0f639674:
      operations: [created]
      at: 2026-09-29T18:08:50.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PLAN-01: advisory planner and its first shadow experiment"
---

# Planner shadow experiment 1

## Setup (verified)

`./ros plan analyze|compare|replay --json --as-of 2026-09-29T18:30:00.000Z`
against this repository at commit `b86217b3dda140e95b6bbaaae755c9485432cac0`
(branch `claude/praxis-planning-optimization-v6dacf`, work-state fingerprint
`722ec04711aee027...`), with default configuration and no supplied
observations. Git evidence came from `origin/main`. The same commands were run
twice per document and produced byte-identical output. A SHA-256 of every file
under `.ros/` before and after the runs was identical. No recommendation was
executed (PRX-PLAN-161).

## Inventory (PRX-PLAN-010..013)

189 items: 172 terminal (171 complete, 1 abandoned) and 17 nonterminal:
9 captured, 1 ready, 2 active, 1 blocked, 1 awaiting evidence,
3 stale-state candidates. Two live-only items (`GH-84`,
`CI-LATEST-ON-VERSION-BUMP`) were present; 63 queue rows still say `ready`
while their live item is complete, and resolve to complete through the
effective-state rule.

## State freshness (PRX-PLAN-020..022): three stale-state candidates

- `PRAXIS-REMOTE-16` is recorded active, but its verified checkpoint commit
  `ca4c5617` is an ancestor of `origin/main` (merged via PR #107).
  Recommendation: reconcile and complete, do not reimplement. Its checkpoint
  says only CI confirmation remains, so remaining work is estimated at up to
  2 minutes against up to 9 minutes from scratch (verification-remaining
  basis, medium confidence).
- `GH-84` is blocked on "human review and merge of PR #85 and the one-time
  Pages setting": PR #85 is merged on `origin/main`; the Pages setting cannot
  be observed. Partially resolved blocker, low confidence.
- `GH-90` is blocked on `PRAXIS-REMOTE-11` (now complete) and
  `PRAXIS-REMOTE-12` (still ready). Partially resolved blocker.

The planner therefore reported planning confidence **low** and put a serial
reconciliation wave 0 ahead of implementation work.

## Dependencies

`PRAXIS-REMOTE-12`'s free-text "Depends on: PRAXIS-REMOTE-11" and
`PRAXIS-REMOTE-16`'s "Depends on: PRAXIS-REMOTE-03" resolved as satisfied.
Critical path: `PRAXIS-REMOTE-12 -> GH-90`. A first version inferred a false
dependency of `PRAXIS-CONT-12` on `PRAXIS-REMOTE-16/17` from the narrative
word "after"; inference was narrowed to explicit dependency phrases and a
regression test added.

## Strategies (PRX-PLAN-090..102)

| Strategy | Availability | Expected duration | Peak | Risk pairs | Frontier |
| --- | --- | --- | --- | --- | --- |
| baseline | available | up to ~28 min | 1 | 0 | yes |
| speed | available | up to ~20 min | 2 | 1 | yes |
| balanced | available | up to ~20 min | 2 | 1 | yes |
| cost | unavailable: 0 of 155 sampled executions contain usable cost evidence | unknown | - | - | no |
| max-parallel | available | up to ~20 min | 2 | 1 | yes |

Only one pair of runnable items could run in parallel, and only by accepting
the elevated shared-Praxis-state risk (PRX-PLAN-082); `WI-READY` has no scope
evidence, so its overlap is unknown and it is never co-scheduled. A third
executor saves nothing (diminishing returns flagged at 3).

## Duration evidence and replay (PRX-PLAN-060..063, 150..152)

155 finalized executions carry `time.wall_ms`; 6 active executions were
excluded. Productive time (wall minus blocked) is heavily skewed: P25 under a
minute, median 2 min, P75 9 min, P90 about 27 min, maximum about 10 h.
`development` (114 executions) has a median of 1 min. Many executions evidently
begin shortly before completion, so execution wall time understates effort in
this repository.

No-hindsight replay (each execution predicted only from executions finalized
before it started): 147 of 155 predictable; 92 of 147 (62.6%) actual durations
fell inside the predicted interquartile range; median absolute error 2.7 min;
median relative error 90.7%. Per class, `development` 75/108 within range,
`architecture-design` 0/5 (median absolute error 238 min). Observed peak
execution overlap: 2 (baseline models 1). Backlog creation order matched actual
start order for 9,336 of 9,397 comparable pairs (99.4%), which mostly reflects
items being captured immediately before they start.

## Assessment

- The planner met the section-24 acceptance criteria on real state: captured,
  ready, active and blocked work distinguished; live-only items kept; stale
  state surfaced separately; a verified checkpoint lowered a remaining-work
  estimate; no monetary cost invented; baseline, speed and balanced compared;
  cost reported unavailable; concurrency conservative; every recommendation
  explained; repeated runs deterministic; no mutation.
- Duration predictions are weak (90.7% median relative error). They should
  not drive decisions until executions are started when work starts and cost
  telemetry exists. The stale-state findings, by contrast, are
  evidence-backed and immediately actionable.
- Not yet measured: recommendation versus what actually happens
  (PRX-PLAN-162); `praxis plan freshness --plan` against a saved plan provides
  the mechanism.
