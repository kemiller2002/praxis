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

## MIG-06 continuation execution

Canonical raw execution evidence for this continuation is
`.ros/telemetry/executions/EXE-20260908T113835529Z-e8d6b91e.json`.

| Observation | Value/capability | Provenance |
|---|---|---|
| continuation start | 2026-09-08T11:38:35.529Z | ROS work/telemetry transition |
| starting branch/tree | `migration/ros-fsharp-git-provenance`, clean before work records | Git and `./ros work ready` |
| F# tests after Git slice | 21 passed, 0 failed | direct `Ros.Tests.dll` execution |
| Git differential tests | 3 passed, 0 failed | Node test runner over controlled repositories |
| new shadow command | `ros-fs git status [--json]` | CLI smoke output |
| Git process call sites added | 1, Infrastructure only | source inspection |
| compiler attempts | 8: 4 successful, 4 failed | command output; reserved-name, inference, placement, and interpolation repairs followed by narrow and full gates |
| final complete suite | 124 passed, 0 failed | 89 Node + 7 Python + 21 F# + 7 Node-driven F# differential/smoke tests |
| SDE integrity | v1.1.1 verified; 18 managed files; 5 structural review warnings | `sde status` and `sde verify`; warnings are pre-existing large-file review signals |
| provider model/token/cache/cost | unavailable/not captured | runtime exposed no new provider observation; no values inferred |

This continuation used the existing semantic map and the work-lifecycle and
execution-telemetry manifests. Source inspection expanded into both Node Git
helpers because the work manifest declared Git evidence and the telemetry
manifest declared repository discovery; this was expected dependency fan-out,
not an undeclared semantic area.

## MIG-07 continuation execution

Canonical raw execution evidence is
`.ros/telemetry/executions/EXE-20260908T115323101Z-4ccf5eaa.json`.

| Observation | Value/capability | Provenance |
|---|---|---|
| continuation start | 2026-09-08T11:53:23.101Z | ROS work/telemetry transition |
| starting branch/SHA | `migration/ros-fsharp-work-lifecycle` / `803f2b9` | Git baseline captured by ROS |
| typed work tests | 4 new; 25 total F# tests passed | `Ros.Tests.dll` output |
| work differential tests | 3 new; matrix case covers all 16 state/action pairs | Node test runner against real Node transition calls and F# CLI |
| final complete suite | 131 passed, 0 failed | 89 Node + 7 Python + 25 F# + 10 Node-driven differential/smoke tests |
| build executions | 2 passed, 0 failed | narrow and complete gates; successful builds had zero warnings/errors |
| SDE integrity | v1.1.1 verified; 18 managed files; 5 pre-existing structural review warnings | `sde verify` |
| production mutations | none | source diff; `./ros` remains Node |
| provider model/token/cache/cost | unavailable/not captured | no runtime observation; no values inferred |

## MIG-05 work-persistence continuation

Canonical raw execution evidence is
`.ros/telemetry/executions/EXE-20260908T120118252Z-e06c199f.json`.

| Observation | Value/capability | Provenance |
|---|---|---|
| continuation start | 2026-09-08T12:01:18.252Z | ROS work/telemetry transition |
| starting branch/SHA | `migration/ros-fsharp-work-persistence` / `9785eb6` | ROS Git baseline |
| new persistence tests | 5; 30 total F# tests pass | direct typed test execution |
| successful build | 0 warnings, 0 errors | final narrow .NET build |
| failed build attempts | 2 | compiler exposed unconstrained overloads and record-label ambiguity; both repaired |
| final complete suite | 136 passed, 0 failed | 89 Node + 7 Python + 30 F# + 10 Node-driven differential/smoke tests |
| SDE integrity | v1.1.1 verified; 18 managed files; 5 pre-existing structural review warnings | `sde verify` |
| production state writer changes | 0 | source diff; shadow recovery contract only |
| provider model/token/cache/cost | unavailable/not captured | no runtime observation; no values inferred |

## MIG-05 production work-persistence integration

Canonical raw execution evidence is
`.ros/telemetry/executions/EXE-20260908T121300882Z-fe89c3e0.json`.

| Observation | Value/capability | Provenance |
|---|---|---|
| continuation start | 2026-09-08T12:13:00.882Z | ROS work/telemetry transition |
| starting branch/SHA | `migration/ros-work-persistence-integration` / `b3a32cf` | ROS Git baseline |
| new production/cross-runtime tests | 4; 31 total F# and 92 total Node tests pass | focused and complete test output |
| focused work/telemetry tests | 52 passed, 0 failed | Node test runner; expected rejection diagnostics were asserted |
| successful build | 0 warnings, 0 errors | final .NET build |
| execution mistakes | 1 | initial direct test invocation selected an unbuilt Debug output; corrected to Release without a test failure |
| complete suite before closeout | 140 passed, 0 failed | 92 Node + 7 Python + 31 F# + 10 Node-driven differential/smoke tests |
| production state writer changes | Node live-work event/context writer adopts shared recovery journal under existing lock | source and cross-runtime tests |
| provider model/token/cache/cost | unavailable/not captured | no runtime observation; no values inferred |

## MIG-05 backlog-persistence continuation

Canonical raw execution evidence is
`.ros/telemetry/executions/EXE-20260908T122549568Z-b1bcf1e1.json`.

| Observation | Value/capability | Provenance |
|---|---|---|
| continuation start | 2026-09-08T12:25:49.568Z | ROS work/telemetry transition |
| starting branch/SHA | `migration/ros-backlog-persistence` / `10f44ef` | ROS Git baseline |
| new typed/production tests | 3 / 4 | test source and focused runner output |
| focused gates | 34 F# and 32 Node work tests passed, zero failed | direct typed and Node test execution |
| concurrency proof | eight concurrent `ros add` processes; eight final unique queue IDs | production test fixture |
| successful builds before closeout | 1 | .NET build, zero warnings/errors |
| failed builds before closeout | 1 | nominal F# write-record ambiguity in an unannotated existing test helper; repaired with explicit types |
| complete test attempts | 2: first 146 passed/1 failed; second 147 passed/0 failed | first read-only smoke detected stale evidence/journal registries; configured rebuild then unchanged rerun passed |
| final complete suite | 147 passed, 0 failed | 96 Node + 7 Python + 34 F# + 10 Node-driven differential/smoke tests |
| provider model/token/cache/cost | unavailable/not captured | no runtime observation; no values inferred |

## MIG-05 telemetry-link continuation

Canonical raw execution evidence is
`.ros/telemetry/executions/EXE-20260908T140008179Z-0d130adf.json`.

| Observation | Value/capability | Provenance |
|---|---|---|
| continuation start | 2026-09-08T14:00:08.179Z | ROS work/telemetry transition |
| starting branch/SHA | `migration/ros-telemetry-link-recovery` / `b6b1f9b` | ROS Git baseline |
| typed execution-link tests | 6 new; 40 total F# tests pass | direct typed runner |
| production recovery/guard tests | 4 new; focused work/telemetry gate has 60 passing tests | Node test runner |
| successful builds before closeout | 2 | .NET builds, zero warnings/errors |
| failed focused tests before closeout | 1 | no-write guard succeeded but test helper did not model an absent execution directory; helper repaired and unchanged guard reran successfully |
| final complete suite | 157 passed, 0 failed | 100 Node + 7 Python + 40 F# + 10 Node-driven differential/smoke tests |
| provider model/token/cache/cost | unavailable/not captured | no runtime observation; no values inferred |
