# Repository Semantic Map

This map routes work to ROS semantic authorities and their local manifests. It
does not define behavior; the linked decisions, contracts, source symbols, and
tests do.

| Semantic area / feature | Purpose | Location | Manifest | Notes |
|---|---|---|---|---|
| Artifact management | Validate canonical research artifacts and project deterministic registries. | `research/`, `registries/`, `tools/ros_cli.mjs` | `docs/features/artifact-management/manifest.md` | First F# migration slice. |
| Work lifecycle | Capture local obligations and enforce repository work transitions/evidence. | `.ros/work/`, `.ros/context/`, `tools/ros_cli.mjs` | `docs/features/work-lifecycle/manifest.md` | Canonical live state remains repository-local JSON. |
| Agent identity and provenance | Record and validate who (agent, human, automation) and which execution produced each record; accumulate contributors. | `src/*/Provenance/`, `src/Ros.Cli/ProvenanceCommands.fs`, `requirements/` | `docs/features/provenance/manifest.md` | Self-reported provenance, not attestation. |
| Execution telemetry | Observe execution identity, lifecycle, metrics, provenance, and provider extensions. | `.ros/telemetry/`, `telemetry/`, `tools/ros_telemetry.mjs` | `docs/features/execution-telemetry/manifest.md` | Unknown and unavailable are distinct from zero. |
| Bootstrap and distribution | Materialize ROS profiles and verify installed package files. | `bin/`, `lib/`, `starter/`, `package.json` | `docs/features/bootstrap-distribution/manifest.md` | npm remains the acquisition edge during migration. |
| Project administration | Aggregate repository-local work by invoking each repository's public ROS surface. | `tools/ros_hub_*`, `web-hub/`, `.ros/hub/` | `docs/features/project-administration/manifest.md` | Separate bounded context; not a work-state authority. |
| Automation and release | Declare CI validation and npm publication orchestration. | `.github/workflows/`, `package.json` | `docs/features/automation/manifest.md` | GitHub and npm mechanics remain platform declarations. |
| Local repository web interface | Present and invoke one repository's work capabilities. | `tools/ros_server.mjs`, `web/` | `docs/features/work-lifecycle/manifest.md` | UI is an adapter over work semantics. |
| F# migration | Move stable ROS semantic authority into typed vertical slices under comparative verification. | `docs/migrations/fsharp/`, `src/`, `tests/` | `docs/migrations/fsharp/README.md` | Migration status and traceability only; not a second domain authority. |

## Repository-wide composition

- Composition/root entry point: `ros` delegates to `tools/ros_cli.mjs`; during
  coexistence the shadow F# composition root is documented in
  `docs/migrations/fsharp/README.md`.
- Shared contracts: `ros.json`, `schemas/`, `telemetry/metrics.json`, and
  accepted `research/decisions/` records.
- Architecture checks: no project-owned architecture check existed at T0; the
  F# migration adds one before a production authority switch.
- Boundary checks: `npm test`, `./ros registry check`, `./ros validate`, and
  the compatibility gates documented in `docs/migrations/fsharp/README.md`.

## Areas without separate manifests

| Area | Reason a separate manifest is not needed |
|---|---|
| Governance and research method | `docs/00-governance/README.md` is already the canonical compact router. |
| SDE methodology bundle | `.sde/README.md` and `.sde/MANIFEST.json` are an installed governed input; project code must not duplicate or edit its rules. |
| Shared persistence helper | `tools/ros_persistence.mjs` is an effect implementation used by work and telemetry rather than an independent semantic area. |
| Shared Git process adapter | `tools/ros_git.mjs` implements the installed effect boundary for the F# Git observation contract; work and telemetry own their caller policies. |
| Legacy Python layout generator | `setup_ros_layout.py` is an uncalled compatibility candidate, not current semantic authority. |
