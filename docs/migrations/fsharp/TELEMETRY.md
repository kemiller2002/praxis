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
| T3 first vertical slice complete | 2026-09-08T06:14:34Z | MIG-03/04 typed artifact shadow, fixture parity, and rejection tests complete |
| T4 implementation complete | 2026-09-08T06:18:00Z | slice committed; root CI/publish gate provisions .NET; migration docs updated |
| T5 verification complete | 2026-09-08T06:23:23Z | full unrestricted suite, TypeScript builds, and documented self-review complete |
| T6 final evidence | 2026-09-08T06:29:33.997Z | WI-0011 complete; execution finalized; generated Git/test/LOC metrics captured |

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

## T3 measurements

| Metric | Value/capability | Provenance |
|---|---|---|
| F# production projects | 5 | `Ros.slnx` project graph |
| F# shadow commands | 3 capability groups: artifact validate, registry build, registry check | `Ros.Cli.Program` |
| F# test cases | 8 passed, 0 failed | `npm run test:fsharp` output |
| Node-driven F# differential cases | 3 passed, 0 failed | same command; fixture bytes, current-repository smoke, usage rejection |
| F# build attempts after slice implementation | 5 invoked: 4 compiler successes, 1 compiler failure; two later Node differential failures repaired | command output: one test syntax issue, one Node strict-mode test issue, and one assertion-shape issue; no production semantic failure |
| compiler warnings/errors on successful build | 0 / 0 | .NET build output |
| baseline canonical inputs in F# fixture build | unchanged hashes | F# typed test |
| new F# external process call sites | 0 in Domain/Application; CLI uses no shell/Git/network operation | source review |
| unavailable provider token/cost/model measurements | unchanged from T0 | environment did not expose observations |

## Running execution summary before finalization

| Metric | Observed aggregate | Interpretation |
|---|---:|---|
| test executions | 4 | baseline, fixture preregistration, listener-restricted complete run, unrestricted complete run |
| tests passed / failed | 272 / 14 | the 14 failures are retained as sandbox loopback restrictions; unrestricted final run itself was 105 passed / 0 failed |
| build executions / failures | 9 / 1 | includes baseline, F# slice attempts, and final TypeScript builds; one early F# test syntax failure was repaired |
| agent self-corrections | 4 | D010–D013 correction categories recorded in the journal |
| provider token/cost values | unavailable or unknown | not inferred from these engineering aggregates |

## T6 mechanically derived observations

| Metric | Value | Notes |
|---|---:|---|
| execution calendar/wall span | 35,872,407 ms | one finalized execution; this is elapsed span, not active agent time |
| commits created in execution range | 4 | as captured at work finalization |
| files added / modified / deleted | 78 / 5 / 0 | Git-derived at finalization |
| lines added / deleted | 4,624 / 4 | Git-derived at finalization; not a success metric |
| tests added / modified / removed | 32 / 0 / 0 | Git-derived classification |
| ending dirty files | 0 | at finalization, before closeout metadata commit |

The final closeout commit and push are recorded by Git after this telemetry
snapshot; they do not retroactively alter the finalized execution record.

The final evidence record will add mechanically derived commits/files/LOC,
build/test attempts, failures, repair loops, defect counts, migration counts,
external-call/rule-site observations, and every available checkpoint value.
