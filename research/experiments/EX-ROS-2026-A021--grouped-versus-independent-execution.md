---
id: EX-ROS-2026-A021
title: Grouped versus independent execution of one high-affinity cohort (frozen protocol)
research_area: repository-operating-system
status: proposed
created: 2026-09-30
tests_hypotheses:
  - HY-ROS-2026-A027
  - HY-ROS-2026-A028
inputs:
  - EV-ROS-2026-A059
  - EV-ROS-2026-A058
outputs: []
related_theories: []
related_documents:
  - requirements/PLANNING-WORK-GROUPS.md
  - DF-ROS-2026-A047
  - docs/planning.md
tags: [planning, grouping, experiment, context, protocol]
provenance:
  contributions:
    EXE-20260930T102847967Z-f42c2262:
      operations: [created]
      at: 2026-09-30T11:17:10.436Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "WI-0064: evidence-based work groups and the frozen grouping experiment"
---

# Experiment

## Research question

For one cohort of strongly related work items, does one execution that
reasons about the whole cohort before mutating (grouped arm) repeat less work
and make more consistent architectural decisions than one fresh execution per
item (control arm), without losing quality or suffering context pressure? And
did the read-only planner predict the scheduling facts correctly?

## Hypotheses tested

- `HY-ROS-2026-A027` (A, scheduling value) against the frozen predictions
  below.
- `HY-ROS-2026-A028` (B, grouping value) against the two arms.

## Status and gate

**Protocol frozen; arms not run.** Running the arms needs, in order:

1. the phase-one grouping model (`WI-0064`) merged to `main`;
2. the repository owner's approval of this cohort and of the cost (about
   seven agent sessions: five control, one grouped, one evaluator);
3. the owner triaging `PRAXIS-GROUP-01..05` from `captured` to `ready`
   (their acceptance criteria are already in their descriptions).

Nothing in this protocol may change after an arm starts except to record
what happened. Deviations are recorded, never silently corrected.

## Cohort (PRX-GRP-081)

`GROUP-PRAXIS-WORK-GROUP-001`: `PRAXIS-GROUP-01` (`work group create`),
`-02` (`show`), `-03` (`add`), `-04` (`remove`), `-05` (`checkpoint`), the
phase-two commands PRX-GRP-073 already specifies. Selection evidence is in
`EV-ROS-2026-A059`: at the baseline commit the repository had no pre-existing
open cohort of 5 or more related items (only two cohesive pairs), so no
existing work could be used without padding. These five were captured as
real, specified obligations, not invented to reach a size; the planner then
independently recommended them as one high-affinity group. Their being
authored by the same agent that designed the experiment is a recorded threat
to validity.

Selection rule, should this cohort be rejected: the largest set of 5..10 open,
triaged items the planner rates `high` affinity within one execution
repository, with at least one explicit (non-inferred) signal covering every
member; never pad with lower-affinity items.

## Variables

- Independent: execution mode (one fresh execution per item versus one
  execution for the cohort).
- Held constant: baseline commit, cohort, acceptance criteria, repository
  instructions, provider, model and runtime (Claude Code remote sessions on
  the same model identifier, recorded per session from `get_session`),
  environment, planner version.
- Dependent: the metrics below.

## Method

### Baseline (PRX-GRP-086)

At the arms' start commit `B` (recorded then), re-run `plan analyze`, `plan
compare`, `plan groups` (default configuration and
`EX-ROS-2026-A021-baseline/planner-config.json`), `plan explain-group
GROUP-PRAXIS-WORK-GROUP-001`, `plan simulate --groups` and `plan compare
--groups` with `--json`, save them under
`research/experiments/EX-ROS-2026-A021-baseline/at-start/`, and run `plan
freshness` against the frozen documents. The frozen predictions below are not
changed by that run; differences are recorded as drift. The planner executes
nothing.

### Isolation (PRX-GRP-083)

Two branches from `B`: `experiment/a021-control` and
`experiment/a021-grouped`. Every execution is a separate remote session (its
own container and workspace) with its own Praxis execution and identity.
Sessions of one arm are told the other arm's branch name only as a branch
they must not fetch, read or check out; the evaluator verifies this from
session event logs. Neither branch is merged before evaluation.

### Control arm

Five sessions, one per item, run one after another in the planner's order
`01, 02, 03, 04, 05` on the control branch. Serial because the planner rates
every member pair `conflict`; each session starts fresh and sees only the
repository (including earlier items' pushed commits, which are repository
state, not private context). Frozen prompt, with `ID` substituted:

> Implement work item ID in this repository on branch
> experiment/a021-control, following AGENTS.md and the Praxis work protocol
> (work start, checkpoint, complete). Its acceptance criteria are in its
> backlog description (`./ros work show ID`). Do not fetch, read or check out
> experiment/a021-grouped. Commit with the item ID as the message prefix,
> push, checkpoint and complete the item. Do not merge.

### Grouped arm

One session on the grouped branch. Frozen prompt:

> Implement work items PRAXIS-GROUP-01 to PRAXIS-GROUP-05 in this repository
> on branch experiment/a021-grouped, following AGENTS.md and the Praxis work
> protocol. First read every item (`./ros work show ID`) and `./ros plan
> explain-group GROUP-PRAXIS-WORK-GROUP-001 --config
> research/experiments/EX-ROS-2026-A021-baseline/planner-config.json`, then,
> before changing any code, write and commit
> research/experiments/EX-ROS-2026-A021-grouped/group-analysis.md covering
> PRX-GRP-040: common architecture, shared invariants, conflicting
> requirements, dependencies, order, reusable abstractions, compatibility,
> common tests, where one design can serve several items, and the risks of
> solving each separately. Then implement each item under its own work
> start/complete, committing with the item ID as prefix; label any shared
> change "shared group infrastructure" and attribute it to one item or record
> it as its own item (PRX-GRP-043). Checkpoint after architectural milestones
> naming completed and remaining members. Do not fetch, read or check out
> experiment/a021-control. Do not merge.

### Evaluation (PRX-GRP-084)

A separate evaluator session that implemented nothing receives the two
branches as patch series exported under neutral names (`arm-X`, `arm-Y`; the
mapping is written by the orchestrator to a file committed only after the
evaluation). It assesses, per item and overall: acceptance criteria, tests,
architectural consistency, duplicated abstractions, unnecessary dependencies,
conflicting design decisions, duplication, compatibility, maintainability,
churn, technical debt, and cross-item architectural consequences (one store
model or several; one member-validation rule or several; one checkpoint
shape), documented as evidence, not a score.

## Metrics (PRX-GRP-085)

Per arm, from Praxis telemetry, Git and the sessions' event logs. A metric a
source cannot supply is recorded `unknown`, never zero.

| Group | Metrics |
| --- | --- |
| Resources | input, output and cached tokens; cost; wall-clock, active and blocked time; tool calls; model requests; executions; commits |
| Work | file reads and writes; searches; builds; test runs; retries; failed approaches; merge conflicts; validation failures |
| Context overhead | time from session start to first code edit; files read more than once (same session and across the arm); reads of AGENTS.md and governance/architecture documents; repeated module exploration; repeated test discovery; repeated architecture reasoning |
| Context pressure (PRX-GRP-041) | compactions; context resets; forgotten or re-read requirements; lost acceptance criteria; inconsistent late decisions; session duration |
| Quality | acceptance criteria passed; regressions; defects found; architectural inconsistencies; duplicated code and abstractions; conflicting API patterns; design revisions; evaluator findings; rework before merge |

## Frozen predictions (PRX-GRP-086; recorded 2026-09-30, commit `2d0ec5c`)

From the documents in `research/experiments/EX-ROS-2026-A021-baseline/`
(SHA-256 in `SHA256SUMS`). These must not be edited after an arm starts.

| Prediction | Planner value |
| --- | --- |
| Group affinity | `high` for the cohort; confidence `high`; the default configuration independently recommends the same five (plus `WI-0064`, attached by one shared `cli` tag) |
| Dependency order / critical path | `PRAXIS-GROUP-01` first; critical path `01 -> 02 -> 05` (up to about 27 min) |
| Collisions | all 10 member pairs `conflict` (shared declared paths `src/Ros.Cli`, `src/Ros.Domain/Work`, `src/Ros.Contracts/Work`; shared tags; Praxis state files) |
| Safe concurrency | 1 in both arms; the cohort may run concurrently with the attribution and work-protocol groups under `accept-elevated` |
| Recommended execution | one sequential agent |
| Duration | each member up to about 9 min (medium confidence); either arm up to about 45 min in total (expected 10 min) |
| Executions / context acquisitions | control 5 / 5; grouped 1 / 1 (4 cold starts avoided) |
| Context-reuse value | unknown: the planner has no measurement of what a context acquisition costs |
| Cost | unavailable: 0 of 160 sampled executions carry usable cost evidence |
| Expected unknowns | cost; cached tokens unless the runtime reports them; repeated reads and time to first edit need event-log analysis, not telemetry; historical co-change |

The duration prediction is expected to be low: `EV-ROS-2026-A058` found that
executions in this repository often begin just before completion, so the
historical median (2 min) understates implementation effort. The prediction
is frozen as the planner stated it; the error is part of what is measured.

## Acceptance criteria

The experiment is complete when both arms have run under this protocol, the
evaluator has reported, the metrics table is filled (with `unknown` where
applicable), predictions are compared with observations, and the outcome is
classified. It succeeds even if grouping loses (PRX-GRP-088).

## Falsification criteria

See `HY-ROS-2026-A027` and `HY-ROS-2026-A028`. Outcome classes
(PRX-GRP-087): grouping clearly beneficial for high-affinity work; beneficial
only above a cohesion threshold; saves context but increases context
pressure; improves architecture but not speed; independent execution better
for loosely related work; insufficient evidence.

## Controls

Same commit, cohort, criteria, instructions, model and environment; no
cross-arm visibility; the evaluator implements nothing and sees neutral arm
names; predictions frozen before either arm starts.

## Checkpoints and instrumentation

Every execution uses `work start`/`checkpoint`/`complete`; the grouped arm
also checkpoints after its analysis commit and after each member. Step
telemetry (`telemetry step start|complete`) marks analysis, each item, and
validation. Session event logs are retained for the context metrics.

## Results

Not run. See "Status and gate".

## Threats to validity

- One cohort, one run per arm: no causal claim (PRX-GRP-087).
- The cohort, its tags and its acceptance criteria were written by the agent
  that designed the experiment, and the cohort extends the grouping feature
  itself, so implementers of both arms meet the same unusually rich context.
- The control arm is serial on one branch, so later items see earlier items'
  code; that is repository state, identical in kind to the grouped arm's.
- Model or runtime drift between sessions; recorded per session.
- `PRAXIS-STATE-MERGE-01`: both arms record Praxis state for the same item
  IDs on separate branches; at most one arm's state can be merged.
- Blinding is partial: code style or commit shape may reveal the arm.

## Replication notes

Replicate with a cohort from another repository or area, and with a
deliberately loosely related cohort, to test the cohesion-threshold classes.

## Conclusion

Pending.

## Registry updates required

On completion: evidence record for the results, hypothesis status updates,
and a follow-up decision on whether phase two (PRX-GRP-073) proceeds.
