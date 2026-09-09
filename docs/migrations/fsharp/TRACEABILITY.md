# ROS F# migration traceability

This table links current behavior to its authority, planned/new typed owner, and
verification. A planned owner is not authoritative until its slice and switch
decision are accepted.

| Responsibility | Current requirement/authority | SDE constraint | F# authority | Verification/status |
|---|---|---|---|---|
| Canonical Markdown and generated registries | `DF-ROS-2026-A001`, A002; Node/Python behavior | boundary preservation; explicit semantic authority | `Ros.Domain.Artifacts`, `Ros.Application.Artifacts`, `Ros.Contracts.Artifacts` | MIG-03/04 complete; Node/F# byte differential and repeatability pass; Node remains authority |
| Artifact ID/kind/status/confidence/reference validation | schemas, `tools/ros_cli.mjs`, `tools/ros_cli.py` | pure decision; explicit codecs; negative proof | `Ros.Domain.Artifacts` + `Ros.Infrastructure.Artifacts.FrontMatter` | valid/invalid frozen fixtures, malformed front matter, reference/status/ID rejection, and current-repo smoke pass |
| Live-work transition decision and plan | `DF-ROS-2026-A006`; `TRANSITIONS`, completion guards, item/event projection, and telemetry call order in work kernel | legal transitions/obligations explicit; effects planned before execution; rejection proof | `Ros.Domain.Work`, `Ros.Application.Work`, `Ros.Contracts.Work` | exhaustive matrix and guards pass; all five legal Node item/event projections match; ordered multi-item begin and later rejection match; no state effects moved |
| Whole-context transition planning | A006; production `transitionUnlocked` item loop and final event/context transaction | validate complete plan before executing effects; preserve item/event ordering and first-begin baseline | `WorkContextPlanning`, `WorkContextPlanContract`, shadow `work context-plan` | typed empty/invalid/missing/later-transition rejection tests; Node differential matches context order, request event order, new-item append, metadata, and no context write on rejection; production pre-commit telemetry side effect remains open |
| Completion evidence path observation | A006; production `fs.existsSync(path.resolve(root,path))` behavior | distinguish missing from unavailable; inspect only after semantic guards | `WorkEvidenceRepository`, `VerifiedWorkPlanOutcome`, `FileEvidenceRepository` | file, directory, absolute, and missing compatibility differential passes; injected unavailable issue remains distinct; containment policy remains open |
| Batch completion evidence composition | A006; production multi-item completion loop | reject entire semantic context before I/O; preserve missing/unavailable and request order | `WorkOperations.planVerifiedContext`, `VerifiedWorkContextPlanOutcome` | typed tests prove zero evidence calls on semantic rejection/non-completion and one ordered command-level observation on accepted completion; multi-item present/missing differential matches production |
| Backlog triage and promotion semantics | `DF-ROS-2026-A006`, A008; `BACKLOG_TRANSITIONS`, `backlogTransitionUnlocked`, `startWork` | legal transitions and promotion authority explicit; preflight before effects | `BacklogTransition`, `BacklogPromotion`, `BacklogContract` | exhaustive typed matrix and all 16 Node action comparisons pass; `start` is an explicit promotion that does not mutate queue state; batch rejection/direct external ID compatibility pass; Node remains writer |
| Backlog queue/projection persistence | A006/A008; production Node backlog kernel | whole read/modify/write capability; explicit ordered write set; retry preconditions and divergence outcome | shared Node/F# `backlog-state` journal contract; `Ros.Infrastructure.Work.BacklogStateTransaction` is typed implementation | partial replay works in both runtime directions; divergence rejects before writes; eight concurrent production captures retain every entry; Node remains command authority |
| Live transition event/context persistence | A006; production Node transition kernel | explicit write set, retry preconditions, conflict/unknown outcome, lock capability | shared Node/F# `work-state` journal contract; `Ros.Infrastructure.Work.WorkStateTransaction` is the typed implementation | MIG-05 passes normal production commit, Node recovery of a partial F#-compatible record, F# recovery of a Node-shaped record, divergence preflight, reordered/incomplete set, existing journal, and corrupt-journal tests; Node remains command authority |
| Git status/provenance observation | A006; work/telemetry kernels; porcelain-v1 behavior | effects isolated; unavailable distinct from clean; explicit boundary contract | `Ros.Domain.Git`, `Ros.Application.Git`, `Ros.Infrastructure.Git`, `Ros.Contracts.Git`; compatible installed adapter `tools/ros_git.mjs` | MIG-06 complete; exact Node/F# clean/changed/unavailable differential, rename/copy, malformed output, missing tool, and non-repository proofs pass |
| Completion evidence and Git attribution | A006; `ros.json`; work kernel | effects isolated; unavailable distinct from clean | F# Git contract plus compatible Node effect adapter; future Work handler consumes the F# port directly after distribution | work records rename destination; unavailable completion rejects before telemetry finalization and validation emits a structured `.git` finding; greenfield pre-Git compatibility is explicit |
| Execution lifecycle and telemetry | `DF-ROS-2026-A010`; metric catalog | stable closed semantics + open extensions/provenance | future `Ros.Domain.Execution/Metrics` | deferred; Node remains authority |
| Execution creation/context backlink | A010; production telemetry/work kernels | execution file atomic under per-execution lease; context mutation under work capability; retry must not duplicate evidence or guess ambiguity | `Ros.Domain.Telemetry.ExecutionLinkRecovery` and `Ros.Application.Telemetry.TelemetryOperations`; production Node composes the effect | direct and work-begin crash-shape retries adopt one detached record; ambiguity rejects and exact `--execution-id` selects; uncomposed core attachment rejects before writing |
| Provider telemetry mapping | A010 and provider adapters | open boundary, raw preservation | infrastructure adapters emitting typed observation batches | retained Node edge initially |
| Installation/profile composition | `DF-ROS-2026-A003`; profile manifests | platform adapter versus semantic initialization | future initialization/upgrade handler; npm adapter retained | deferred distribution experiment |
| Hub aggregation | `DF-ROS-2026-A009` | separate semantic area and authority | no `Ros.Domain` owner yet | deferred; spoke contract tests retained |
| GitHub validation/release | workflow and package contract | platform declarations stay at edge | future validation/release-plan commands only | deferred hosted evidence |
| Project Administration -> Time Entry | A006/A007/A009 boundary | do not invent/couple absent domain | explicit future integrations only | blocked pending authoritative requirements |

## First-slice acceptance linkage

| Acceptance | Requirement source | Planned proof |
|---|---|---|
| Node remains production authority | `DF-ROS-2026-A027` | unchanged `ros`/starter launchers and package manifest diff |
| Canonical inputs never modified | A001 | before/after hashes in isolated fixture differential case |
| Registries deterministic and equivalent | A001/A002/current behavior | Node/Python/F# fixture bytes, F# repeat build, and current-repository smoke |
| Invalid boundary data rejected explicitly | A002/SDE verification method | positive/negative frozen fixture; malformed front matter; status/reference/ID cases |
| Domain depends inward only | SDE-DOCTRINE-003/008 | project-reference architecture check plus intentional rejection |
| Public encoding deliberate | SDE-DOCTRINE-004 | golden JSON bytes and explicit writer tests |
| Partial effect is not hidden | SDE-DOCTRINE-004 | injected second-write failure yields typed `Indeterminate` incomplete outcome |
| Git unavailability is not clean | SDE boundary/verification method; work and telemetry manifests | non-repository and missing-executable tests return `Unavailable`; CLI exits 1; malformed rename record is rejected |
| Rename/copy provenance is retained | characterized porcelain-v1 `-z` contract | parser unit test retains both paths; real-repository differential matches Node/Git bytes semantically |
| Illegal work transitions are rejected | A006 and current Node transition matrix | exhaustive 16 state/action combinations compare F# decisions with the Node transition guard |
| Work obligations precede effects | current Node block/completion guards | pure tests and Node differential reject absent block reason and missing evidence types before any F# effect exists |
| Interrupted live-work persistence is recoverable | A006; MIG-05 safety acceptance | production Node holds `work-protocol`, prepares the shared journal, and recovers before transition; cross-runtime tests exercise both directions and a divergence rejection |
| Concurrent backlog changes do not lose queue entries | A006/A008; persistence safety acceptance | eight separate `ros add` processes are released concurrently; the final queue contains all eight IDs and no pending journal |
| Rollback remains available | migration non-goal/safety | Node gates remain; no authority switch or legacy deletion |

The final migration evidence record `EV-ROS-2026-A028` records the exact
commands, results, and remaining authority boundary.
