EX-ROS-2026-A025: retrospective replay of Rehearsal 5's merges against a
reconstructed integration contract. See
research/experiments/EX-ROS-2026-A025--r5-integration-contract-replay.md for
the derivation, metrics, results and biases. Non-causal.

Inputs
  inputs/contract.base.json   reconstructed praxis.integration-contract/1
                              (stream refs are filled in per run)
  inputs/approvals.json       the five "coordinator-approved" commits as
                              recorded approvals of the shell-files interface
  inputs/merges.tsv           every merge up to the cutoff: parents, stream
                              side, target side, rule, branch check, and the
                              merge-tree conflict recomputation

Scripts (bash, git, jq and the built Praxis CLI; read-only on R5)
  derive-merges.sh R5 CUTOFF > inputs/merges.tsv
  replay.sh R5 PRAXIS_DLL outputs                  -> merge-checks.{primary,s1}.jsonl
  analyze.sh {primary|s1}                          -> summary.*.json, merges.*.tsv
  reconcile.sh R5 CUTOFF PRAXIS_DLL {primary|s1} > outputs/reconcile.*.jsonl

Run used
  R5 = a local copy of kemiller2002/indy-init-rehearsal-005 with every
  r5/* branch fetched and the remote URL disabled
  CUTOFF = e086147bc93142c1d97917f2f2fd3a3d08f66dfc
  PRAXIS_DLL = src/Praxis.Cli/bin/Release/net10.0/praxis.dll built from the
  EII-B-001 commit that added `praxis streams check`

Outputs
  outputs/merge-checks.*.jsonl     one `praxis streams check --json` report per merge
  outputs/summary.*.json           metrics and per-merge rows
  outputs/merges.*.tsv             per-merge table
  outputs/reconcile.*.jsonl        one row per reconciled commit
  outputs/reconcile-summary.*.json outcomes per reconcile commit
