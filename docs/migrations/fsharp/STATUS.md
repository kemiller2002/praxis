# ROS F# migration status

Last updated: 2026-09-08. Production authority remains Node.

| Category | Current state |
|---|---|
| Baseline | clean SHA tagged and pushed; active continuation branch `migration/ros-work-context-plan` started from `d0a5902` |
| Discovery | complete at T1; 28 baseline execution/config/platform units plus six executable test units; MIG-06 added one inventoried adapter |
| Semantic decomposition | complete at T2; map and six feature manifests added |
| Architecture | accepted for staged shadowing in `DF-ROS-2026-A027`; challenged before treatment |
| F# implementation | MIG-03/04 and MIG-06 complete; MIG-07 includes typed transition decisions, pure item and ordered context planning, explicit context JSON contracts, and shadow `ros-fs work plan`/`work context-plan` diagnostics |
| Persistence migration | MIG-05 complete: bounded registry, live-work, and backlog recovery units plus atomic per-execution telemetry files and recoverable execution/context linking; no cross-store/global transaction claim |
| Git migration | MIG-06 complete: F# owns the typed status contract; production work/telemetry effects share one contract-compatible Node adapter pending the distribution decision |
| Work migration | MIG-07 decision, item/context orchestration-plan, and typed evidence-observation sub-slices complete; Node still owns effect execution, backlog promotion, telemetry execution, and every state-changing command |
| Production command switch | not authorized and not attempted |
| Legacy removal | none; Python validator remains an oracle; layout generator is only a deprecation candidate |
| Distribution | repository-local .NET 10 shadow only; consumer decision unresolved |
| Verification | complete gate passes: 104 Node, 7 Python, 51 F#, and 17 differential/smoke tests (179 total); final SDE/ROS closeout checks remain for the active slice |
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
