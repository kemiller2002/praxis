# ROS F# migration telemetry

Canonical raw execution evidence is
`.ros/telemetry/executions/EXE-20260907T203141590Z-54f547f8.json`. This document
is the human projection; it never converts unavailable observations to zero.

## Checkpoints

| Checkpoint | UTC timestamp | State/evidence |
|---|---|---|
| T0 experiment start | 2026-09-07T20:31:41.590Z | WI-0011 execution started; clean Git baseline; experiment question/classification recorded |
| T1 instrumentation/bootstrap complete | 2026-09-08T00:08:50Z | authorities read; capability states inspected; baseline tests/builds recorded |
| T2 semantic foundation established | 2026-09-08T00:19:38Z | inventory/decomposition frozen; SDE map/manifests; hypotheses/experiment/architecture decision authored |
| T3 first vertical slice complete | pending | artifact shadow capability and local tests |
| T4 implementation complete | pending | docs/traceability/CI and committed slices |
| T5 verification complete | pending | heterogeneous gates and review |
| T6 final evidence | pending | work completion, registries, clean/pushed handoff |

## Starting measurements

| Metric | Value/capability | Provenance |
|---|---|---|
| baseline tests | 91 passed, 0 failed, one execution | Node/Python test output |
| baseline builds | 2 passed, 0 failed | TypeScript compiler exits |
| starting dirty files | 0 | ROS Git baseline |
| subagents spawned | 3 | orchestrator report |
| parallel execution peak | 4 | orchestrator report |
| approval requests/denials | 1 / 0 | tool interaction report |
| provider/runtime/session | openai / codex / recorded session ID | whitelisted runtime identity |
| model and model version | unavailable | runtime did not expose them |
| input/output/reasoning/cached tokens | supported-unavailable | runtime family capability; no ingested observation |
| cache-read/tool-token/current-context details | unknown | not mapped or reported |
| input/output/cache/tool/total cost | unknown | no billing/pricing observation |
| context window/utilization | supported-unavailable or unknown per raw execution | runtime capability record |
| tool-call totals/categories | unknown unless explicitly recorded later | no trustworthy aggregate exposed at T0 |
| Declared Context Surface/CER/Discovery Expansion baseline | missing | repository had no project SDE map/manifests at T0; not reconstructed retrospectively |

The final evidence record will add mechanically derived commits/files/LOC,
build/test attempts, failures, repair loops, defect counts, migration counts,
external-call/rule-site observations, and every available checkpoint value.
