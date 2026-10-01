# Repository Semantic Map

This map routes work to Praxis semantic authorities and their local manifests. It
does not define behavior; the linked decisions, contracts, source symbols, and
tests do.

| Semantic area / feature | Purpose | Location | Manifest | Notes |
|---|---|---|---|---|
| Artifact management | Validate canonical research artifacts and project deterministic registries. | `research/`, `registries/`, `src/**/Artifacts/` | `docs/features/artifact-management/manifest.md` | First F# migration slice. |
| Work lifecycle | Capture local obligations and enforce repository work transitions/evidence. | `.ros/work/`, `.ros/context/`, `src/**/Work/` | `docs/features/work-lifecycle/manifest.md` | Canonical live state remains repository-local JSON. |
| Execution telemetry | Observe execution identity, lifecycle, metrics, provenance, and provider extensions. | `.ros/telemetry/`, `telemetry/`, `src/**/Telemetry/`, `src/Praxis.Infrastructure/Work/FileTelemetry*` | `docs/features/execution-telemetry/manifest.md` | Unknown and unavailable are distinct from zero. |
| Agent identity and provenance | Attribute work, events, and canonical records to explicit agent/human/automation actors and executions. | `src/**/Provenance/`, `research/requirements/`, `.ros/events/`, `docs/agent-provenance.md` | `docs/features/agent-provenance/manifest.md` | Provenance, not authentication; legacy records are never back-filled. |
| Bootstrap and distribution | Materialize Praxis profiles and verify installed package files. | `src/**/Lifecycle/`, `starter/`, `release.json`, `bin/`, `scripts/install-native.*` | `docs/features/bootstrap-distribution/manifest.md` | Native releases are the only acquisition edge (`DF-ROS-2026-A049`). |
| Project administration | Aggregate repository-local work by invoking each repository's public Praxis surface. | `src/Praxis.Cli/Hub.fs`, `web-hub/styles.css`, `.ros/hub/` | `docs/features/project-administration/manifest.md` | Separate bounded context; not a work-state authority. |
| Automation and release | Declare CI validation and native release orchestration. | `.github/workflows/`, `release.json` | `docs/features/automation/manifest.md` | Workflows host the F# CLI; external actions' own runtimes are not repository code. |
| Local repository web interface | Present and invoke one repository's work capabilities. | `src/Praxis.Cli/WebInterface.fs` (`praxis web serve`), `web/styles.css` | `docs/features/work-lifecycle/manifest.md` | UI is an adapter over work semantics. |
| Implementation-language policy | Keep repository-owned code F#/.NET only. | `src/**/Architecture/`, `ros.json` `implementationPolicy` | `research/requirements/RQ-ROS-2026-A024--fsharp-dotnet-only-repository.md` | Enforced by `./praxis architecture check` and `./praxis validate`. |
| F# migration | Move stable Praxis semantic authority into typed vertical slices under comparative verification. | `docs/migrations/fsharp/`, `src/`, `tests/` | `docs/migrations/fsharp/README.md` | Migration status and traceability only; not a second domain authority. |

## Repository-wide composition

- Composition/root entry point: `praxis` (a shell launcher; `ros` is its compatibility alias) runs the F# CLI,
  `src/Praxis.Cli/Program.fs`.
- Shared contracts: `ros.json`, `schemas/`, `telemetry/metrics.json`, and
  accepted `research/decisions/` records.
- Architecture checks: `tests/Praxis.Tests/ArchitectureTests.fs` (project
  references) and `./praxis architecture check` (F#/.NET-only repository,
  `RQ-ROS-2026-A024`).
- Boundary checks: the F# test suite (`tests/Praxis.Tests`), `./praxis registry
  check` and `./praxis validate`.

## Areas without separate manifests

| Area | Reason a separate manifest is not needed |
|---|---|
| Governance and research method | `docs/00-governance/README.md` is already the canonical compact router. |
| SDE methodology bundle | `.sde/README.md` and `.sde/MANIFEST.json` are an installed governed input; project code must not duplicate or edit its rules. |
| Shared persistence helper | `src/Praxis.Infrastructure/Work/*Transaction.fs` are effect implementations used by work and telemetry rather than an independent semantic area. |
| Shared Git process adapter | `src/Praxis.Infrastructure/Git/GitRepository.fs` implements the effect boundary for the F# Git observation contract; work and telemetry own their caller policies. |
