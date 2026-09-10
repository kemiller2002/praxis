# ROS F# migration status

Last updated: 2026-09-10 (post command-surface parity inventory re-run, `EV-ROS-2026-A045`, superseding `EV-ROS-2026-A043`). Production authority remains Node.

| Category | Current state |
|---|---|
| Baseline | clean SHA tagged and pushed; active continuation branch `migration/ros-work-verified-context` started from `ff47c69` |
| Discovery | complete at T1; 28 baseline execution/config/platform units plus six executable test units; MIG-06 added one inventoried adapter |
| Semantic decomposition | complete at T2; map and six feature manifests added |
| Architecture | accepted for staged shadowing in `DF-ROS-2026-A027`; challenged before treatment |
| F# implementation | MIG-03/04 and MIG-06 complete; MIG-07 includes typed live/backlog decisions, pure item/context/promotion planning, post-plan batch evidence verification, telemetry execution-ID result-feedback, explicit contracts, and shadow planning diagnostics |
| Persistence migration | MIG-05 complete: bounded registry, live-work, and backlog recovery units plus atomic per-execution telemetry files and recoverable execution/context linking; no cross-store/global transaction claim |
| Git migration | MIG-06 complete: F# owns the typed status contract; production work/telemetry effects share one contract-compatible Node adapter pending the distribution decision |
| Work migration | MIG-07 decision, item/context/backlog-promotion planning, typed evidence-observation, telemetry execution-ID resolution, work-attribution validation (`work validate`, mirroring production `workFindings`), backlog-queue validation (`work backlog-validate`, mirroring production `queueFindings`), and eight real work-lifecycle effects (`work backlog-transition`, `work capture`, `work update`, `work attach`, `work start`, `work resume`, `work block`, `work complete`) sub-slices complete — every backlog-only Node command and all four live-work transitions now have real F# effect parity. MIG-08's first two increments add two real telemetry-producer commands (`telemetry adapters`, `telemetry show`) and real telemetry lifecycle bookkeeping (`recordTelemetryLifecycle`, now recorded by `work resume`/`work block`); Node still owns every write-path telemetry-producer command, `telemetry summary`, and both adapter commands |
| Production command switch | not authorized and not attempted; `DF-ROS-2026-A028` opens the decision track as a three-phase plan (full command-surface effect parity, consumer distribution evidence, then the switch decision itself). Phase A's backlog-layer and live-work-layer scope is complete: all four backlog-only commands (`ready`/`block`/`abandon`, `add`/`captureWork`, `update`/`findOrCreateQueueEntry`, `attachFileUnlocked`) and all four live-work transitions (`begin`/`resume`/`block`/`complete`, including real telemetry execution creation via `startExecution` and finalization via a new `FileTelemetryFinalizationRepository`) are real effects under their respective recovery journals. Phase A has now opened MIG-08 too: `EV-ROS-2026-A044`'s inventory of `tools/ros_telemetry.mjs` explicitly declined to recommend a first slice, only that one was needed; increment 1 chose the smallest one — `telemetry adapters` (a static catalog dump) and `telemetry show` (a real, lock-free read of `.ros/telemetry/executions/*.json`) — with zero new write-path, lock, identity, or adapter complexity. Increment 2 closed a real gap increments 6-8 had left open, honestly documented at the time: `recordTelemetryLifecycle`'s within-execution "blocked"/"resumed" event bookkeeping, so `time.blocked_ms` now computes a real nonzero value at finalization instead of always zero. Everything else left in Phase A (`telemetry summary`'s aggregation, every write-path telemetry-producer command, both adapter commands, `resume`'s still-open `parentExecutionId` linkage) remains its own future MIG-08 scoping choice. `EV-ROS-2026-A043`'s inventory has now been re-run as `EV-ROS-2026-A045`: 13 of the same 26 rows now read "full parity" (up from 3), but its acceptance criterion (every row) is not yet met |
| Legacy removal | none; Python validator remains an oracle; layout generator is only a deprecation candidate |
| Distribution | repository-local .NET 10 shadow only; consumer decision unresolved |
| Verification | complete gate passes: 104 Node, 7 Python, 206 F#, and 91 differential/smoke tests (408 total); zero-warning build; a direct four-tier compliance sweep (project references, per-file opens, no invented Tier 3 decisions) found no violations |
| Research | experiment A020 and hypotheses A021–A026 preregistered; `EV-ROS-2026-A045` re-runs the command-surface parity inventory (`EV-ROS-2026-A043`, now superseded) |

T6 closed WI-0011 and finalized `EXE-20260907T203141590Z-54f547f8`. The final
push state is recorded in the handoff report and journal after the closeout
commit; production authority remains unchanged.

## Classification summary

- **To F# core/commands:** artifact rules/projection and read-only Git status now
  shadow-owned; later work, execution, telemetry, evidence, configured stable
  policy, initialization/upgrade, and release-plan decisions.
- **Retained thin adapters:** launchers, npm acquisition, HTTP, TypeScript UI,
  provider mappings, hub-to-spoke process boundary, package task glue.
- **Retained platform declarations:** GitHub YAML, profile manifests, schemas,
  metric catalog, UI/compiler configuration, SDE inputs.
- **Deprecated only after proof:** independent Python artifact CLI and legacy
  Python layout generator.
- **Deferred:** project-administration F# ownership and production distribution.
- **Blocked for missing requirements:** Time Entry semantics.

This file is updated after T3–T6; it must not be used as evidence that a planned
slice has completed.
