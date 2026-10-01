EX-ROS-2026-A022 -- setup directory (frozen)
============================================

The experiment record is
research/experiments/EX-ROS-2026-A022--cohesion-moderated-grouped-execution.md
and the hypothesis research/hypotheses/HY-ROS-2026-A029--cohesion-moderates-shared-context-value.md.
Everything here is plain text, JSON or a script because every *.md under
research/experiments is parsed as an experiment record.

Read in this order:
  baseline.txt                    frozen baseline SHA and branch creation
  cohorts.txt                     inventory, selected and rejected items, rationale,
                                  scope, dependencies, confounders
  affinity/model.txt              the affinity model and classification rule
  affinity/observations.json      per-item pre-execution observations
  affinity/relations.json         pairwise relations with rationale
  affinity/compute_affinity.py    deterministic matrix computation (--check)
  affinity/affinity-matrix.txt    the matrix (generated; .json beside it)
  affinity/selection-log.txt      every rule-driven change to the cohorts
  affinity/planner-config-areas.json, planner/   planner corroboration
  criteria/cohort-high.txt, cohort-low.txt        frozen items verbatim
                                  (export_criteria.py --check)
  protocol/conditions.txt         the 2x2 design, grouped and independent protocols,
                                  stop conditions
  protocol/start-guard.txt        stale-checkout protection (A021-R2 C2 fix)
  protocol/environment-parity.txt
  protocol/prompts/               every message sent to an implementing session
  protocol/orchestration.txt      the runbook for all four conditions, evaluation, reveal
  measurement/metrics-schema.json what is measured, from where, unknown != 0
  blinding/blinding-protocol.txt  kits, leak review, order of events
  blinding/commitment-reveal.txt  canonical payload, salted commitment, escrow, reveal
  blinding/a022_commitment.py     generate / verify / selftest
  blinding/prepare_blind_kit.py   builds blind arm and kit branches
  blinding/evaluator-instructions.txt, findings-template.json
  readiness-checklist.txt         must pass before execution
  SHA256SUMS                      checksums of every file above

Nothing in this directory may change after the first condition starts,
except by a dated addition recording a deviation.
