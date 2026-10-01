EX-ROS-2026-A022 -- planner corroboration at the frozen baseline
================================================================

Secondary, tool-computed affinity evidence (affinity/model.txt section 7).
The primary affinity evidence is affinity/affinity-matrix.txt.

How these files were produced
-----------------------------
Planner 3.6.0 (this repository's own F# CLI, built from the baseline sources),
run against a clean detached worktree of the baseline so that no later setup
state could leak in:

  git worktree add --detach WT f806f817e4656e00550313d839b59ceea87a682d
  ./praxis --root WT plan groups  --as-of 2026-10-01T11:00:00.000Z [--json]
  ./praxis --root WT plan groups  --as-of 2026-10-01T11:00:00.000Z \
      --config research/experiments/EX-ROS-2026-A022-setup/affinity/planner-config-areas.json [--json]
  ./praxis --root WT plan analyze --as-of 2026-10-01T11:00:00.000Z --json

  plan-groups-default.{json,txt}  default configuration: only the backlog
                                  items' own tags, cited IDs and dependencies
  plan-groups-areas.{json,txt}    plus declared areas = each selected item's
                                  expected files from observations.json
                                  (generic files excluded)
  plan-analyze.json               full analysis snapshot

--as-of pins the planner clock, so a re-run on the same commit reproduces the
logical content.

What the planner says about the frozen cohorts
----------------------------------------------
High cohort (PRAXIS-CTL-02..06):
  default: one group GROUP-...-CONTROL-PLANE-001, affinity medium, confidence
           high (shared 'control-plane' tag, two shared requirement IDs, one
           inferred dependency). The planner group also contains
           PRAXIS-CTL-01, which the primary rule rejected (H1); the planner
           does not know the experiment's selection.
  areas:   the same group, affinity high, confidence high (shared declared
           files src/Ros.Cli/WebHttp.fs, WebInterface.fs, CliProcess.fs,
           src/Ros.Contracts/ControlPlane, docs/web-interface.md).
  Corroboration expected by model.txt: met.

Low cohort (ACTOR-KIND-ANCHOR, ATTR-RECONCILE-SYMLINK-SUBMODULE,
PRAXIS-CN-GL-KINDS, PRAXIS-PLAN-ERROR-HISTORY, PRAXIS-STATE-MERGE-01):
  default: no two members share a group; ACTOR-KIND-ANCHOR,
           PRAXIS-CN-GL-KINDS and PRAXIS-PLAN-ERROR-HISTORY are ungrouped with
           affinity none; ATTR-RECONCILE-SYMLINK-SUBMODULE is grouped with the
           non-cohort ATTR-COMPLETE-BASE-REF-SWEEP and PRAXIS-STATE-MERGE-01
           with the non-cohort ID-collision items. Corroboration expected by
           model.txt: met.
  areas:   ACTOR-KIND-ANCHOR and PRAXIS-CN-GL-KINDS form a two-member group
           rated HIGH, solely because both declare
           src/Ros.Domain/Artifacts/Policy.fs. Corroboration expected by
           model.txt: NOT met for this one pair.

Recorded discrepancy
--------------------
The planner's signal table rates any overlap of declared paths 'high'
(docs/planning.md, "Signals"), however small the overlap and however large
the declared surface. ACTOR-KIND-ANCHOR declares nineteen Domain files (its
audit scope), so it overlaps any item that edits one of them. The primary
matrix records the same overlap (D4 files, one file) and no strong
dimension for that pair. The selection was not changed: the pre-registered
primary rule classifies the low cohort LOW, and the planner is secondary.
The pair is listed as a confounder in cohorts.txt; the evaluator's findings
on that pair are to be read with this in mind, and the discrepancy is a
finding about the planner (coarse path-overlap signal) worth recording in
the results.
