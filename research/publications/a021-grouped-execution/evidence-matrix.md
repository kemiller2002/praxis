# Evidence matrix

Every substantive manuscript claim maps to a committed artifact. The
authoritative, machine-readable form is **`data/evidence-index.json`** (one
entry per `% CLAIM: Cxx` marker in `manuscript/paper.tex`, with source paths,
pinned commits, locators, extraction method, classification and caveats).
`scripts/check_manuscript.py` fails if a marker is missing from the index or an
index claim is unused. This file is the human-readable summary.

Classification: **observed** (measured or counted, or verified in code/probes),
**evaluated** (evaluator or rubric judgment grounded in code/tests),
**inferred** (author interpretation). An inferred mechanism is never stated as
an observed fact.

| Claim (paper wording, abridged) | IDs | Primary evidence | Status / caution |
| --- | --- | --- | --- |
| Treatment = cohort session with mandated committed cross-item analysis vs. uninstructed per-item fresh sessions, serial on one branch | C01, C02 | A021 protocol (frozen `370e0a4`); `EX-ROS-2026-A021-harness/prompts/*.txt` at `bc5a8d8`; T01; AD-01 | Bundle; no shared-context-only attribution |
| Common baseline, same items/criteria, arms unmerged | C03 | T27; protocol Isolation | Baseline SHA withheld in paper (anonymity) |
| A021 protocol frozen before arms | C04 | T29 | R2 had no frozen protocol |
| A021 evaluations not effectively blind | C06 | T02; AD-02; `evaluation.txt` lines 26-35; EV-A067 §3; erratum E7 | Fatal to "blinded A021"; only code-re-verified facts used |
| R2 evaluation blind by self-report; cross-vendor | C07 | R2 `findings.json`; T09 | Mapping technically recoverable |
| R2 = internal close (operational) replication; not preregistered; prompts not archived; designer knew interim A021 | C08, C09 | AD-07; T07; T08; R2 run record `063e6b5` | Never "independent" |
| A021 worker-only cost and summed active time | C11, C12 | `data/metrics.json` (`sessions.json` platform usage at `bc5a8d8`); CF-03 | 111.4 min, not 112 (CF-03) |
| R2 worker-only cost and summed active time | C13, C14 | `data/metrics.json` (R2 run record §4); CF-05, CF-06 | 52.3% / 44.4% primary |
| R2 58.72% (incl. orchestrator), 61.08% (common-start span); A021 span incl. permission block | C16 | CF-05, CF-06; AD-03; T03, T04 | Secondary only; spans ≠ summed time |
| S1: A021 without failed attempts 46.4% / 30.7% | C17 | `build_tables.py` excluded-sessions macros | Direction unchanged |
| Repeated-context counts lower for grouped (lower bounds) | C18, C19 | transcript metrics files; CF-01, CF-08; T15 | Partly entailed by design |
| No compactions | C20 | transcript metrics; EV-A064 | A021 independent count is a lower bound |
| Architecture A021 6/8 (4 behavioral); R2 4/8 (3 behavioral) | C21, C22 | `data/architecture-findings.json`; `architecture-findings.md`; AD-05 | Rubric post-unblinding, one AI auditor, no IRR |
| Independent better on D4 in both; D7 mixed in both; grouped P2 gap in both | C23, C24 | architecture findings D4, D7, LC-01; erratum E9 | Equal prominence required |
| Independent lost update observed by probe in both | C25 | LC-04; probe table | Audit probe after unblinding |
| Stale-start sensitivity (A021 item 5; R2 items 2, 3) | C26, C27, C36 | architecture-findings "Stale-start sensitivity"; T05; EV-A066 | All stale starts hit per-item sessions |
| Evaluator counts inflated ("three stores" etc.) | C28 | erratum E8; falsification attempts | — |
| Tests added: independent more in both | C30 | metrics.json; eval 1; R2 findings | Test count ≠ quality |
| R2 confirmed defects 2 / 2, each in one item | C31 | R2 findings.json; POST-UNBLINDING mapping | — |
| A021 defect evidence: no eval-1 tally; kit labels 0 / 2 | C32 | eval 1; kit findings | Not blind |
| Both grouped checkpoints fail to reject blank decisions | C33 | LC-05, LC-06; erratum E10 | — |
| Local correctness mixed; trade-off, not "beneficial" | C34 | T12; AD-09; erratum E11 | Inferred synthesis |
| Recurrence matrix (direction only) | C35 | `manuscript/tables/replication-matrix.tex` (generated) | Mechanical rule |
| Deviations, failed attempts, orchestrator, salt, evaluator model, role overlap, restarts | C37-C42 | validity-audit register; T03, T18, T19, T22, T23 | — |
| Affinity follow-up blocked, not run | C43 | EV-A072; EX-A022 | Feasibility only |
| Mechanism candidates, novelty, implications | C44-C47 | T21; related-work.md; architecture findings | Inferred, low confidence |
| n = 1 per cell; correlated resample | C48 | T06 | No inference |
| Workspace contamination: hypothesis, protocol, cohort labels in baseline and item texts | C52 | baseline tree; acceptance-criteria.txt; prompts | Demand characteristics, both arms, major |
| I's create/add asymmetry follows the acceptance criteria | C53 | acceptance-criteria.txt; D2 findings; P2 | Consistency ≠ conformance ≠ correctness |
| G lock-contention failures; lock gaps | C54 | LC-08; D6 notes | Reported next to I's lost update |
| Preregistered hypothesis rule; predictions; uncollected measures; skipped controls | C55, C56 | HY-A028; protocol; T12, T24 | Hypothesis at best weakly supported |
| I implementer later did kit work; R2 evaluator could reach main | C57 | CF-07; EV-A066; T09 | Role contamination |
| Rubric, analysis plan and sensitivity analyses post hoc (6 → 8 dimensions) | C58 | analysis-plan.md history; architecture-findings.md | Exploratory |
| Treatment-outcome overlap | C59 | grouped prompt; T01, T08 | Major construct threat |
| Per-session overhead in the resource gap | C60 | T14, T15; harness note | Not separable |
| Run-to-run variance caveat | C61 | bai2026tokens (search-index); per-session rows | Ratios are observations |
| Reproducibility pipeline, corrections, artifact contents | C49-C51 | scripts; EV-A074; metric-conflicts.json; artifact manifest | Artifact still being finalised |

## Retired rows (superseded by the audits)

- "R2 evaluator remained blind to mapping while evaluating — Direct": now a
  self-report (T09).
- "R2 grouped cost reduction 58.72% / elapsed 61.08%" as headline: secondary,
  defined figures only (AD-03).
- "Original platform cost unavailable": false (CF-02, E1).
- "Grouped has one store/join rule/classification/error style" as blind
  evaluator findings: re-verified structural facts only; classification and
  grammar differences do not replicate in R2.
