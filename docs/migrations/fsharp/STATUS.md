# ROS F# migration status

Last updated: 2026-09-08. Production authority remains Node.

| Category | Current state |
|---|---|
| Baseline | clean SHA tagged and pushed; branch `migration/ros-fsharp-application` |
| Discovery | complete at T1; 28 execution/config/platform units plus six executable test units |
| Semantic decomposition | complete at T2; map and six feature manifests added |
| Architecture | accepted for staged shadowing in `DF-ROS-2026-A027`; challenged before treatment |
| F# implementation | MIG-03/04 complete: typed artifact policy/projection, filesystem port, explicit JSON codecs, and shadow CLI commands |
| Production command switch | not authorized and not attempted |
| Legacy removal | none; Python validator remains an oracle; layout generator is only a deprecation candidate |
| Distribution | repository-local .NET 10 shadow only; consumer decision unresolved |
| Verification | baseline 91 tests plus two TypeScript builds passed; F# solution builds with zero warnings/errors; eight F# tests and three Node-driven differential/smoke tests pass, including deliberate rejection and indeterminate-write paths |
| Research | experiment A020 and hypotheses A021–A026 preregistered |

T6 closed WI-0011 and finalized `EXE-20260907T203141590Z-54f547f8`. The final
push state is recorded in the handoff report and journal after the closeout
commit; production authority remains unchanged.

## Classification summary

- **To F# core/commands:** artifact rules/projection now shadow-owned; later
  work, execution, telemetry, evidence, configured stable policy, Git
  provenance, initialization/upgrade, and release-plan decisions.
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
