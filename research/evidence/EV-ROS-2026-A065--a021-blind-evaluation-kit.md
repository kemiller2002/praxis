---
id: EV-ROS-2026-A065
title: "EX-ROS-2026-A021 blind evaluation kit: method, owner procedure and tooling validation"
status: draft
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-30
updated: 2026-09-30
research_area: repository-operating-system
evidence_type: primary
supports: [EX-ROS-2026-A021]
related_documents:
  - EX-ROS-2026-A021
  - requirements/PLANNING-WORK-GROUPS.md
  - research/experiments/EX-ROS-2026-A021-evaluation-kit/evaluator-prompt.txt
  - research/experiments/EX-ROS-2026-A021-evaluation-kit/prepare-blind-bundle.sh
tags: [planning, grouping, experiment, evaluation, blinding]
confidence: medium
provenance:
  contributions:
    EXE-20260930T144206630Z-1e149d71:
      operations: [created]
      at: 2026-09-30T14:42:12.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-A021-EVAL-KIT: blind evaluation kit for EX-ROS-2026-A021"
    EXE-20260930T230548914Z-bb6a60fd:
      operations: [migrated]
      at: 2026-09-30T23:06:18.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PLAN-08: renumbered from EV-ROS-2026-A060..A062 on experiment/a021-evaluation-kit, which collided with main's EV-ROS-2026-A060; content unchanged apart from the IDs"
---

# EX-ROS-2026-A021 blind evaluation kit

The method for the independent, blind evaluation of experiment `EX-ROS-2026-A021`
(grouped versus independent execution of `PRAXIS-GROUP-01..05`), as required by
PRX-GRP-084 and the protocol's "Evaluation" section in
[`EX-ROS-2026-A021`](../experiments/EX-ROS-2026-A021--grouped-versus-independent-execution.md).

This kit lives on its own branch, `experiment/a021-evaluation-kit`, created from the arms'
start commit `8b4ffa3`. Neither arm branch contains it, so no implementing session could see
it, and its presence does not tell one arm from the other.

## Contents

| File | For | Purpose |
| --- | --- | --- |
| this record (`EV-ROS-2026-A065`) | owner and evaluator | The evaluation method, the owner procedure, and the validation of the tooling. |
| `research/experiments/EX-ROS-2026-A021-evaluation-kit/evaluator-prompt.txt` | the evaluator | The complete brief: paste it as the first message of a fresh evaluator session. |
| `research/experiments/EX-ROS-2026-A021-evaluation-kit/prepare-blind-bundle.sh` | the evaluator | Builds the blind `arm-X` / `arm-Y` bundle and seals the mapping. |
| `EV-ROS-2026-A066` | the owner, **after** evaluation | Per-session notes that name an arm. The evaluator is told not to read it. |
| `EV-ROS-2026-A067` and `research/experiments/EX-ROS-2026-A021-evaluation-kit/output/findings.json` | the owner | The evaluator's report and findings (created by the evaluation). |

## Owner procedure

1. **Wait until both arms are finished.** That means control items 01–05 are committed, checkpointed
   and completed on `experiment/a021-control`, and the grouped session has finished on
   `experiment/a021-grouped`. Do not start earlier: the bundle is a snapshot.
2. **Start a fresh session** that implemented nothing, on the same model where possible, with this
   repository. Paste `evaluator-prompt.txt` as its first message. The session:
   1. builds the bundle;
   2. builds and tests both arms;
   3. checks every acceptance criterion with one shared fixture script;
   4. documents the cross-item architecture with citations;
   5. commits its report as `research/evidence/EV-ROS-2026-A067--a021-blind-evaluation-report.md` and
      `findings.json` under `research/experiments/EX-ROS-2026-A021-evaluation-kit/output/` on this branch, and pushes.

   It does not open a pull request or merge.
3. **Record the mapping commitment** (a SHA-256 hash) that the evaluator prints and repeats in
   its report.
4. **Unblind after the report is pushed.** The sealed mapping file is lost when the evaluator's
   container is reclaimed, so unblind by content instead. The report quotes the last line of each
   arm's `diffstat.txt`. Compare them with:

   ```
   for b in experiment/a021-control experiment/a021-grouped; do
     echo "$b: $(git diff --stat 8b4ffa3 origin/$b -- . ':(exclude).ros' \
       ':(exclude)research/experiments/EX-ROS-2026-A021-control' \
       ':(exclude)research/experiments/EX-ROS-2026-A021-grouped' | tail -1)"
   done
   ```

   Record the resulting mapping in the experiment's evidence record, which also cites
   `EV-ROS-2026-A066` and the other arm-naming session notes. Only then
   compare telemetry (`EX-ROS-2026-A021-control/metrics/*.json` and the grouped arm's metrics)
   against the frozen predictions.

## What the prep script does

- Fetches both arm branches into temporary refs (removed again at the end) and takes the start
  commit as their merge-base (override with `BASE=`).
- Assigns the arms to `arm-X` / `arm-Y` using `/dev/urandom`.
- Exports for each arm:
  - `tree/`: `git archive` of the tip, buildable, without `.git`;
  - `patches/`: `git format-patch` from the start commit;
  - `diffstat.txt` and `commit-subjects.txt`.

  The patches, diffstat and subjects exclude `.ros/` and both arms' experiment folders. Metrics
  folders are removed from the trees.
- Scrubs arm identifiers from everything exported: branch names, arm folder names, `control-0N` /
  `grouped-0N` labels, "control/grouped arm", Claude session links, and `Claude-Session:` trailers.
  The trees keep a scrubbed `.ros/` only because some tests read repository state.
- Exports identical shared inputs from the start commit:
  - the requirements;
  - the frozen protocol;
  - the five work items with their acceptance criteria;
  - the planner baseline.
- Writes `sealed/mapping.json` (branches, tip commits, start commit, random salt; read-only) and
  prints only its SHA-256.

## Validation performed (2026-09-30)

The script was run with the control branch standing in for both arms (the grouped branch was not
read, per that session's instructions), with `BASE=8b4ffa3`:

- the bundle held the control patches for GROUP-01..04, which were the items finished at the time;
- a search found no remaining `a021-control`, `a021-grouped`, `A021-control`, `control-0N` or
  session ID in the arm material. The shared protocol names both branches, which is expected and
  does not reveal the mapping;
- the temporary refs were removed and the source repository was left clean;
- a scrubbed tree built with 0 warnings and 0 errors and passed 821/821 tests without Git history.

The script has **not** yet run with the grouped branch as input.

## Known limits of blinding

- Blinding depends on the evaluator following instructions: it has repository access and could
  read the arm branches or `EV-ROS-2026-A066`. The report must state whether it thinks it learned
  the mapping.
- Commit structure may reveal the arm: the grouped arm's analysis-first commits and its "shared
  group infrastructure" labels.
- The grouped arm's `group-analysis.md` is in its excluded experiment folder, so the evaluator
  does not assess it. Assess it separately after unblinding if it is wanted.
- The number of executions recorded under the scrubbed `.ros/telemetry/` may differ between arms.
  The brief tells the evaluator not to assess `.ros/`.
