# Repository Semantic Map

This map routes work to ROS semantic authorities and their local manifests. It
does not define behavior; the linked decisions, contracts, source symbols, and
tests do.

| Semantic area / feature | Purpose | Location | Manifest | Notes |
|---|---|---|---|---|
| Artifact management | Validate canonical research artifacts and project deterministic registries. | `research/`, `registries/`, `src/**/Artifacts/` | `docs/features/artifact-management/manifest.md` | First F# migration slice. |
| Work lifecycle | Capture local obligations and enforce repository work transitions/evidence. | `.ros/work/`, `.ros/context/`, `src/**/Work/` | `docs/features/work-lifecycle/manifest.md` | Canonical live state remains repository-local JSON. |
| Execution telemetry | Observe execution identity, lifecycle, metrics, provenance, and provider extensions. | `.ros/telemetry/`, `telemetry/`, `src/**/Telemetry/`, `src/Ros.Infrastructure/Work/FileTelemetry*` | `docs/features/execution-telemetry/manifest.md` | Unknown and unavailable are distinct from zero. |
| Agent identity and provenance | Attribute work, events, and canonical records to explicit agent/human/automation actors and executions. | `src/**/Provenance/`, `research/requirements/`, `.ros/events/`, `docs/agent-provenance.md` | `docs/features/agent-provenance/manifest.md` | Provenance, not authentication; legacy records are never back-filled. |
| Bootstrap and distribution | Materialize ROS profiles and verify installed package files. | `src/**/Lifecycle/`, `starter/`, `release.json`, `bin/`, `scripts/install-native.*` | `docs/features/bootstrap-distribution/manifest.md` | Native releases are the only acquisition edge (`DF-ROS-2026-A041`). |
| Project administration | Aggregate repository-local work by invoking each repository's public ROS surface. | `src/Ros.Cli/Hub.fs`, `web-hub/styles.css`, `.ros/hub/` | `docs/features/project-administration/manifest.md` | Separate bounded context; not a work-state authority. |
| Automation and release | Declare CI validation and native release orchestration. | `.github/workflows/`, `release.json` | `docs/features/automation/manifest.md` | Workflows host the F# CLI; external actions' own runtimes are not repository code. |
| Local repository web interface | Present and invoke one repository's work capabilities. | `src/Ros.Cli/WebInterface.fs` (`ros web serve`), `web/styles.css` | `docs/features/work-lifecycle/manifest.md` | UI is an adapter over work semantics. |
| Implementation-language policy | Keep repository-owned code F#/.NET only. | `src/**/Architecture/`, `ros.json` `implementationPolicy` | `research/requirements/RQ-ROS-2026-A021--fsharp-dotnet-only-repository.md` | Enforced by `./ros architecture check` and `./ros validate`. |
| F# migration | Move stable ROS semantic authority into typed vertical slices under comparative verification. | `docs/migrations/fsharp/`, `src/`, `tests/` | `docs/migrations/fsharp/README.md` | Migration status and traceability only; not a second domain authority. |

## Repository-wide composition

- Composition/root entry point: `ros` (a shell launcher) runs the F# CLI,
  `src/Ros.Cli/Program.fs`.
- Shared contracts: `ros.json`, `schemas/`, `telemetry/metrics.json`, and
  accepted `research/decisions/` records.
- Architecture checks: `tests/Ros.Tests/ArchitectureTests.fs` (project
  references) and `./ros architecture check` (F#/.NET-only repository,
  `RQ-ROS-2026-A021`).
- Boundary checks: the F# test suite (`tests/Ros.Tests`), `./ros registry
  check` and `./ros validate`.

## Areas without separate manifests

| Area | Reason a separate manifest is not needed |
|---|---|
| Governance and research method | `docs/00-governance/README.md` is already the canonical compact router. |
| SDE methodology bundle | `.sde/README.md` and `.sde/MANIFEST.json` are an installed governed input; project code must not duplicate or edit its rules. |
| Shared persistence helper | `src/Ros.Infrastructure/Work/*Transaction.fs` are effect implementations used by work and telemetry rather than an independent semantic area. |
| Shared Git process adapter | `src/Ros.Infrastructure/Git/GitRepository.fs` implements the effect boundary for the F# Git observation contract; work and telemetry own their caller policies. |
| Legacy Python layout generator | `setup_ros_layout.py` is an uncalled compatibility candidate, not current semantic authority. |
