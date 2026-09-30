---
id: EV-ROS-2026-A064
title: "Grouping experiment EX-ROS-2026-A021: one grouped execution versus five independent executions of the work-group commands"
status: review
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-30
updated: 2026-09-30
research_area: repository-operating-system
evidence_type: primary
supports: [HY-ROS-2026-A028]
related_documents:
  - EX-ROS-2026-A021
  - EV-ROS-2026-A059
  - HY-ROS-2026-A027
  - HY-ROS-2026-A028
  - DF-ROS-2026-A047
  - requirements/PLANNING-WORK-GROUPS.md
tags: [planning, grouping, experiment, context, evaluation]
confidence: low
provenance:
  contributions:
    EXE-20260930T114749635Z-ba7301df:
      operations: [created]
      at: 2026-09-30T17:18:10.317Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PLAN-EXP-01: results of the grouping experiment EX-ROS-2026-A021"
    EXE-20260930T172030686Z-71d403ef:
      operations: [modified]
      at: 2026-09-30T17:20:31.714Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PLAN-06: renumber the A021 results record to EV-ROS-2026-A064 and relate it to the parallel evaluation kit and EV-ROS-2026-A063"
    EXE-20260930T230548914Z-bb6a60fd:
      operations: [modified]
      at: 2026-09-30T23:06:18.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PLAN-08: refer to the evaluation kit by its renumbered IDs and record the second blind evaluation's outcome (EV-ROS-2026-A067)"
derived_from: [EX-ROS-2026-A021]
---

# Grouping experiment results

## What ran (verified)

Both arms started from `8b4ffa3` (main `fb6a0f9` plus one identical harness
commit). Cohort: `PRAXIS-GROUP-01..05`, the `praxis work group
create|show|add|remove|checkpoint` commands. Every session was a Claude Code
remote session on `claude-opus-5-5` (configured and last-served model
identical for all nine sessions). Session IDs, prompts, start commits and
platform usage are in `research/experiments/EX-ROS-2026-A021-harness/`.

- **Grouped arm** (`experiment/a021-grouped`, head `5face88`): one session,
  11:50-12:51 UTC. It wrote and committed the group analysis
  (`9e8fc2e`) before any code, then implemented 01..05 in order.
- **Control arm** (`experiment/a021-control`, head `8ea9be2`): one fresh
  session per item, serially, 11:50-16:42 UTC. Items 01-03 and 05 took one
  session each. Item 04 took three: attempt 1 ended idle without pushing
  anything, attempt 2 pushed the implementation (`881a34d`) and then stopped
  at a permission prompt for `./ros work` commands, and attempt 3 (with the
  owner's approval pre-granted) started, checkpointed and completed it.
- **Evaluator**: one separate session that implemented nothing, given the two
  arms as `experiment/a021-arm-x` and `-arm-y` with each arm's own experiment
  directory removed. The mapping (X = grouped, Y = control) was drawn at
  random, sealed, and unsealed only after the evaluation was committed
  (`758724b`). The full evaluation is in
  `research/experiments/EX-ROS-2026-A021-evaluation/evaluation.txt`.

## Resources and context overhead (PRX-GRP-085)

Platform usage comes from the session records. Transcript metrics come from
the harness script (`session_metrics.py`) that each session ran on its own
transcript. The control arm's script figures miss item 04's attempts 1 and 2
and the last minutes of control-05, so they are lower bounds.

| Measure | Grouped | Control |
| --- | --- | --- |
| Sessions (executions) | 1 | 7 (5 items, 2 failed attempts) |
| Cost, platform-reported | $9.48 | $21.51 |
| Output tokens | 112,377 | 244,630 |
| Cache-read tokens | 24.5 M | 45.1 M |
| Active session time | 61 min | 112 min (sum of sessions) |
| Elapsed, first start to last finish | 61 min | 4 h 52 min (includes orchestration gaps and a 2 h permission block) |
| Reads of AGENTS.md | 1 | 5 (one per measured session) |
| Reads of governance and planning documents | 6 | 15 |
| Time to first code change | 9.6 min, once, including the analysis | 4.6, 5.6, 6.4 and 4.8 min in the four sessions that started from scratch |
| Model requests, script | 114 | at least 237 |
| File reads / searches, script | 52 / 53 | at least 134 / 159 |
| Compactions, context resets | 0 | 0 |
| Peak context in one session | 319 k of 1 M tokens | 263 k (control-05) |
| Merge conflicts | 0 | 1 (control-05, `Program.fs`) |
| Tests at the end | 811 pass | 829 pass |

Costs are the platform's estimates. Praxis's own `cost.*` telemetry still
records none. No context-pressure indicator appeared in the grouped arm.

## Quality (evaluator findings, PRX-GRP-084)

The evaluator judged, with confirmed behaviour and file and line evidence:

- **Architectural consistency.** The grouped arm has *one model* of a declared group:
  - one store (`.ros/work/groups.json`, holding the declaration, history and checkpoints);
  - one member-join rule, applied at create and at add;
  - one member classification, shared by show and checkpoint;
  - one rejection type and error style, and one JSON envelope;
  - one CLI mutation pipeline.

  The control arm has *several partial models*:
  - three stores (declarations, a membership ledger, checkpoint events);
  - two join rules: create skips the repository check that add applies (confirmed);
  - two group-ID grammars;
  - three member classifications;
  - four rejection types with inconsistent exit codes (confirmed);
  - three JSON envelopes and two CLI parsers.

  The evaluator ranked this the most important difference, because every
  later verb would have to be changed in two or three places.
- **Individual criteria and reuse.** Here the control arm was ahead:
  - It reused the planner's own `grouping.groups` parser and its
    `executionLocation` rule. The grouped arm's `add` accepts an item the
    planner considers external (confirmed), and it has a second declaration
    parser.
  - Its group checkpoint applies the full `work checkpoint` ownership rule
    and is re-validated by `validate`.
  - Its `show` reports blocking from the whole planner graph.
  - It added 37 tests against 19.

  The evaluator judged each of the grouped arm's gaps local and small to close.
- **Attribution.** The grouped arm labelled its shared infrastructure as such
  and attributed it to PRAXIS-GROUP-01 (PRX-GRP-043), as asked. The control
  arm attributed per item and named its refactors. It also recorded one
  mis-attribution of item 04's paths itself, in new backlog item WI-0065.
- **Cross-item decisions.** The grouped arm made the store, join-rule,
  classification and checkpoint-shape decisions once and reused them.
  In the control arm, item 03 added a second store. Item 05 began on its own
  model and was reconciled after a merge. That start was on a checkout
  without items 01-04 (see threats).
- **Other issues.**
  - Grouped arm: it committed a live experiment group to `groups.json`, and
    that group's note points to a file that no longer exists.
  - Control arm: an ID clash takes down every planner command, group
    mutations take no lock, and one text label is wrong.

## Predicted against observed (PRX-GRP-086/087)

| Frozen prediction | Observed |
| --- | --- |
| Affinity `high`; one sequential agent recommended | Supported by the evaluator: the five items are one feature, and the arm that reasoned about them together produced one model. |
| Dependency order `01` first; critical path `01 -> 02 -> 05` | Held. Both arms built 01's store first. The grouped arm's 05 reused 02's classification. The control arm's 05, begun without 01's store, had to be reconciled with it. |
| All member pairs `conflict`; safe concurrency 1 | Held. The only concurrent-edit event (control-05 against 01-04) produced a merge conflict in `Program.fs`. |
| Executions / context acquisitions 5 / 5 against 1 / 1 | 7 / 7 against 1 / 1. Independent execution also carried two failed attempts. |
| Each member up to about 9 min; either arm up to about 45 min | Missed. The grouped arm took 61 min, and the control arm 112 min of session time. As predicted by the frozen note, the history-based duration model underestimates implementation work (here by about 1.4 to 2.5 times the upper bound). |
| Context-reuse value unknown | Measured: 4 fewer AGENTS.md reads, 9 fewer governance reads, one cold start instead of four or more, and 56 % lower platform cost. |
| Cost unavailable | Still unavailable to the planner. The platform reported cost outside Praxis. |
| No context-pressure evidence expected at 5 members | None observed (0 compactions, 32 % of the context window at peak). |

## Classification (PRX-GRP-087)

**Grouping was beneficial for this high-affinity cohort.** It saved repeated
context and cost, and it produced a markedly more consistent architecture. The
independent executions met individual criteria more faithfully and tested more
broadly. No context pressure appeared at five members. This is one cohort and
one run per arm, so no causal or general claim follows (PRX-GRP-087). It is
evidence for "grouping clearly beneficial for high-affinity work", qualified
by the individual-criterion finding.

## Relation to other A021 records

- A separate blind evaluation kit was prepared in parallel
  (`EV-ROS-2026-A065`: evaluator prompt, blinding script and shared
  acceptance-criteria fixture; `EV-ROS-2026-A066`: control-02 session notes).
  These were numbered A060..A062 on their branch and renumbered because
  `EV-ROS-2026-A060` already existed on main (`PRAXIS-PLAN-08`).
- The evaluation summarized here used this protocol's own brief
  (`EX-ROS-2026-A021-harness/prompts/evaluator.txt`) and is one independent
  evaluation, not the kit's.
- The kit's evaluator then ran as a second, independent evaluation
  (`EV-ROS-2026-A067`, findings in
  `EX-ROS-2026-A021-evaluation-kit/output/findings.json`). It replicated these
  quality findings: the grouped arm kept one store, one join rule and one
  checkpoint shape but duplicated the planner's parser, ignored its
  external-repository inference and checkpointed without the ownership
  checks; the control arm reused more and verified checkpoints more fully.
  It added one defect this evaluation did not report: the control arm's
  create, add and remove write the store without a lock, so concurrent runs
  that all exit 0 lose writes. Its blinding was compromised (an unscrubbed
  "grouped-arm" commit subject and "control branch" in a doc), which it
  reported before reading any code; read its qualitative judgements with
  that caveat.
- `EV-ROS-2026-A063` (preliminary, draft) recorded the early
  context-reacquisition signal from the grouped arm and control items 01-03.
  The final figures here supersede its numbers, not its interpretation.

## Threats to validity

- **One cohort, one run per arm.** The cohort was authored by the agent that
  designed the experiment, and it extends the grouping feature itself.
- **Control-05 started from the wrong commit.** Its session was created from
  `experiment/a021-control` (head `8c175d5`), but its first commit
  (`8d435cb`) has the arm start `8b4ffa3` as its parent. The cause is not
  known; items 02-04 started from the correct heads. Some of the control
  arm's item-05 divergence and its merge conflict are therefore an
  orchestration artifact rather than a property of independent execution.
  The consistency finding holds without item 05, since item 03 alone added a
  second store and a second join rule.
- **Item 04 took three sessions** because of an ended-without-push attempt
  and a permission prompt that needed the owner. Their cost counts against
  the control arm. That is a real overhead of many sessions here, but it is
  environment-specific.
- **Metrics are partial.** Transcript metrics are self-computed by a
  deterministic script and miss two control attempts. Costs are
  platform-reported estimates.
- **One evaluator.** A single evaluator produced the quality findings, and
  it used this protocol's brief rather than the parallel kit's.
- **Blinding was partial.** Commit messages and committed Praxis state name
  experiment labels, and the evaluator reports reading them without using
  them.

## Recommendation for the next planning iteration

1. Keep grouping advisory (`DF-ROS-2026-A047`). For high-affinity groups, the
   planner's "one sequential agent" recommendation is supported by this run.
   Do not make grouped execution a default on one run.
2. Strengthen grouped-execution guidance (PRX-GRP-040) with the two things
   the grouped arm missed:
   - an explicit inventory of existing rules and parsers to reuse (here
     `grouping.groups` parsing and `executionLocation`);
   - a final per-criterion verification pass for every member.
3. Make context overhead observable in Praxis itself. Promote
   `session_metrics.py` to a telemetry adapter, and record platform cost as
   `cost.execution_total`, so reuse and cost stop being unknown to the planner.
4. Replace or down-weight the history-based duration model for
   implementation work. It missed the upper bound in both arms.
5. Replicate before generalizing: first with a loosely related cohort, to
   test the cohesion-threshold classes, then in another repository.
6. The owner decides how phase two proceeds. The evaluator's comparison
   suggests arm X (grouped) as the base, with the control arm's parser reuse,
   `executionLocation` join rule and checkpoint re-validation ported into it.
   Neither arm is merged by this experiment.
