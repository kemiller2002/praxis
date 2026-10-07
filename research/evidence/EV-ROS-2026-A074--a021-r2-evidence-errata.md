---
id: EV-ROS-2026-A074
title: Errata for the A021 and R2 evidence records found during the publication audit
status: review
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-07
updated: 2026-10-07
research_area: repository-operating-system
evidence_type: secondary
supports: []
related_documents:
  - EX-ROS-2026-A021
  - EV-ROS-2026-A064
  - EV-ROS-2026-A066
  - EV-ROS-2026-A070
  - HY-ROS-2026-A028
  - research/experiments/EX-ROS-2026-A021-R2-blind/output/POST-UNBLINDING-METRICS.txt
  - research/publications/a021-grouped-execution/data/metric-conflicts.json
  - research/publications/a021-grouped-execution/validity-audit.md
  - research/publications/a021-grouped-execution/architecture-findings.md
tags: [planning, grouping, replication, erratum, publication]
confidence: high
derived_from: [EV-ROS-2026-A064, EV-ROS-2026-A070]
provenance:
  contributions:
    EXE-20261007T140703062Z-100aa587:
      operations: [created]
      at: 2026-10-07T14:34:38.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Errata for A021/R2 evidence found during the publication audit (WI-0074)"
---

# Evidence

## Claim

Several statements in the A021 and R2 evidence records are factually wrong or
are stated without a definition they need. The original records are **not
edited**. This record lists each correction, the evidence for it, and how the
publication package (`research/publications/a021-grouped-execution/`) uses the
corrected value. Every numeric correction is recomputed by
`scripts/extract_metrics.py` and checked by `scripts/verify_metrics.py`
against pinned commits; the conflict identifiers (CF-nn) refer to
`data/metric-conflicts.json`, and threat identifiers (Tnn) to `data/threats.json`.

## Corrections

| # | Record and statement | Correction | Evidence |
| --- | --- | --- | --- |
| E1 | `POST-UNBLINDING-METRICS.txt` and `EV-ROS-2026-A070`: original A021 "platform cost was unavailable" | False. Platform cost was recorded for every A021 session (`EX-ROS-2026-A021-harness/sessions.json`, `platformUsage.cost_usd`): grouped $9.476795, control workers $21.514808. Only Praxis's own `cost.*` telemetry was unavailable. | CF-02 |
| E2 | `EV-ROS-2026-A070` and `POST-UNBLINDING-METRICS.txt`: A021 resource totals 114/237 requests, 110,627/176,304 output tokens, 23,872,205/32,211,852 cache-read tokens, presented next to R2 platform figures | These are transcript-script sums, not platform usage. The control figures omit item 04 attempts 1 and 2 entirely and are lower bounds ("at least" in EV-A064 was dropped). Platform figures: output 112,377 / 244,630; cache-read 24,507,715 / 45,074,840. | CF-01, CF-08 |
| E3 | `EV-ROS-2026-A070`, `POST-UNBLINDING-METRICS.txt`: R2 grouped cost reduction 58.72% | The control total ($26.0492768) includes the control-arm orchestrator session ($3.4929240); the grouped arm had no orchestrator. Workers only: $10.7536464 vs $22.5563528, a 52.33% reduction. 58.72% is correct only under the "including the arm's orchestrator" definition. | CF-05, T03; R2 run record at `063e6b5` |
| E4 | `EV-ROS-2026-A070`, `POST-UNBLINDING-METRICS.txt`: R2 elapsed reduction 61.08% | The control "elapsed" (2h23m35s) is a wall-clock span from the grouped session's creation to the control orchestrator's last update, including inter-worker gaps and a tail after the last worker. Summed worker session time: 3,353 s vs 6,033 s, a 44.42% reduction. The two definitions must not be compared with A021's summed "active session time". | CF-06, T04 |
| E5 | `EV-ROS-2026-A064` and the A021 protocol Results: control "112 min" of session time | Summing the session records gives 6,682 s (111 min 22 s) when attempt 2 ends at its `blockedAt`. The source of the extra minute is not recorded. | CF-03 |
| E6 | `EV-ROS-2026-A064` threats and `EX-ROS-2026-A021-harness/sessions.json` deviation 2: "items 02-04 started from the correct heads" | Control item 02 also started stale (recorded in `EV-ROS-2026-A066`). | T05 |
| E7 | `EV-ROS-2026-A064` and the A021 protocol: the blind evaluations | The A021 evaluator saw full commit history on `experiment/a021-arm-x`, including the subject "…EX-ROS-2026-A021 grouped-arm session metrics" (`99592467`) and a first commit titled as the group analysis; it reports reading commit messages. The kit evaluator (`EV-ROS-2026-A067`) reports knowing the mapping before reading code. Neither A021 evaluation was effectively blind. Code-level structural findings remain checkable independently. | T02 |
| E8 | `EV-ROS-2026-A064`: control "three stores", "two group-ID grammars", "three member classifications" | Counting is inflated: the third "store" is the pre-existing event log; the checkpoint grammar is a strict superset of the create grammar and comes from the stale-start item 05; the grouped arm also has two classifications (admission and progress), sharing one between show and checkpoint. | `architecture-findings.md`, items 6–8 |
| E9 | R2 evaluation (`findings.json`) and `POST-UNBLINDING-METRICS.txt`: the grouped arm (N) enforces execution-repository compatibility | Only in part. N's member facts default every member to the local repository and ignore the planner's external-repository inference, so N accepts an item the planner treats as external (probe P-series); the control arm (M) refuses it. This repeats the A021 grouped-arm gap. | `architecture-findings.md`, item 1 |
| E10 | `EV-ROS-2026-A064`: the A021 grouped arm's gaps; omissions in both A021 evaluations | The A021 grouped arm stores a blank `--decision` verbatim (`src/Ros.Domain/Work/WorkGroups.fs` at `5face88`); the control arm rejects it. | `architecture-findings.md`, item 9 |
| E11 | `EV-ROS-2026-A064` classification "Grouping was beneficial for this high-affinity cohort" ("clearly beneficial") | Inconsistent with `HY-ROS-2026-A028`'s own weakening condition ("loses quality"): local quality was mixed. The publication reports the outcome as a trade-off, not as "clearly beneficial". | T12 |
| E12 | `EV-ROS-2026-A070`: "preserving comparable acceptance quality"; R2 as reproducing the A021 pattern | Confirmed defects were tied (2/2), but the architecture audit finds R2 reproduces the grouped advantage on three behavioral dimensions only (admission rule, error/exit contract, locked mutation); classification and ID grammar do not replicate, and the independent advantages (checkpoint validation, baseline-rule reuse) replicate too. | `architecture-findings.md` |

## Interpretation

None of the corrections reverses a direction: in both executions the grouped
arm used fewer resources under every like-for-like definition and was more
unified on admission, error contract and mutation safety, while the
independent arm was better on checkpoint validation and reuse of existing
rules. The corrections reduce the magnitudes that may be quoted, remove the
word "blinded" from the original A021 evaluations, and narrow the replication
claim. `HY-ROS-2026-A028` confidence remains low.

## Limitations

These corrections come from an audit by the same organisation that ran the
experiment. Two items (E3, E4) depend on the R2 run record that exists only on
`claude/a021-r2-orchestration-e0gty2` at `063e6b5`; if that branch is deleted
the correction can no longer be re-derived from the repository alone.
