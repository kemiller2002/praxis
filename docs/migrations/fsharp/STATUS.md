# ROS F# migration status

Last updated: 2026-09-09 (post first Phase A real-effect slice: `work backlog-transition`). Production authority remains Node.

| Category | Current state |
|---|---|
| Baseline | clean SHA tagged and pushed; active continuation branch `migration/ros-work-verified-context` started from `ff47c69` |
| Discovery | complete at T1; 28 baseline execution/config/platform units plus six executable test units; MIG-06 added one inventoried adapter |
| Semantic decomposition | complete at T2; map and six feature manifests added |
| Architecture | accepted for staged shadowing in `DF-ROS-2026-A027`; challenged before treatment |
| F# implementation | MIG-03/04 and MIG-06 complete; MIG-07 includes typed live/backlog decisions, pure item/context/promotion planning, post-plan batch evidence verification, telemetry execution-ID result-feedback, explicit contracts, and shadow planning diagnostics |
| Persistence migration | MIG-05 complete: bounded registry, live-work, and backlog recovery units plus atomic per-execution telemetry files and recoverable execution/context linking; no cross-store/global transaction claim |
| Git migration | MIG-06 complete: F# owns the typed status contract; production work/telemetry effects share one contract-compatible Node adapter pending the distribution decision |
| Work migration | MIG-07 decision, item/context/backlog-promotion planning, typed evidence-observation, telemetry execution-ID resolution, work-attribution validation (`work validate`, mirroring production `workFindings`), and backlog-queue validation (`work backlog-validate`, mirroring production `queueFindings`) sub-slices complete; Node still owns new-execution creation, effect execution, live-work/context persistence, and every telemetry-bearing command |
| Production command switch | not authorized and not attempted; `DF-ROS-2026-A028` opens the decision track as a three-phase plan (full command-surface effect parity, consumer distribution evidence, then the switch decision itself), Phase A now underway: `work backlog-transition` (`ready`/`block`/`abandon`) is the first Node command with a real, differential-proven F# effect — a genuine write to `.ros/work/queue.json`/`queue.md` under the same `work-protocol` lock and `backlog-state` recovery journal production's own writer uses, excluding `start`'s live-work/telemetry promotion. `EV-ROS-2026-A043`'s inventory is not yet re-run (Phase A's acceptance requires every row, not one) |
| Legacy removal | none; Python validator remains an oracle; layout generator is only a deprecation candidate |
| Distribution | repository-local .NET 10 shadow only; consumer decision unresolved |
| Verification | complete gate passes: 104 Node, 7 Python, 110 F#, and 48 differential/smoke tests (269 total); zero-warning build; a direct four-tier compliance sweep (project references, per-file opens, no invented Tier 3 decisions) found no violations |
| Research | experiment A020 and hypotheses A021–A026 preregistered |

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
