# PRAXIS-INTERNAL-NAMESPACES: internal `Ros.*` → `Praxis.*` rename manifest

Work item: `PRAXIS-INTERNAL-NAMESPACES`. Decision context:
[`DF-ROS-2026-A050`](../../research/decisions/DF-ROS-2026-A050--praxis-canonical-name-ros-compatibility.md),
which renamed the public product to Praxis and deferred the internal
implementation names ("not a user interface; migrating them is a separate,
larger change tracked on its own").

The public ROS → Praxis rename was already complete. This migration changes
**internal implementation identity only**: source directories, project
files, F# namespaces and modules, test-assembly identity, the solution file,
and purely internal MSBuild/resource names. It changes no CLI behaviour, no
persisted state and no wire or file format.

## Rule

A name is renamed only when it is an implementation identity that nothing
outside one build of this repository can observe. Anything an installed
project, an earlier release, a pinned workflow caller, a persisted record or
the historical record can observe keeps its `ros` spelling.

## Rename manifest

| Current name | Target name | Type | Files affected | External compatibility risk | Decision | Test protecting the decision |
|---|---|---|---|---|---|---|
| `Ros.Domain` | `Praxis.Domain` | project, assembly, namespace root | `src/Ros.Domain/**`, every `open`/qualified reference | none: no serialized output names a .NET namespace or assembly (checked: no `FullName`/`AssemblyQualifiedName`/type-name serialization in `src/`) | rename | `ArchitectureTests`, `InternalNamingTests` |
| `Ros.Contracts` | `Praxis.Contracts` | project, assembly, namespace root | `src/Ros.Contracts/**` and references | none (as above) | rename | `ArchitectureTests`, `InternalNamingTests` |
| `Ros.Application` | `Praxis.Application` | project, assembly, namespace root | `src/Ros.Application/**` and references | none (as above) | rename | `ArchitectureTests`, `InternalNamingTests` |
| `Ros.Infrastructure` | `Praxis.Infrastructure` | project, assembly, namespace root | `src/Ros.Infrastructure/**` and references | none (as above) | rename | `ArchitectureTests`, `InternalNamingTests` |
| `Ros.Cli` | `Praxis.Cli` | project and namespace root (assembly is already `praxis`) | `src/Ros.Cli/**` and references | none: `AssemblyName` stays `praxis`, so `praxis.dll`, the `praxis` tool command and `EchelonFoundry.Praxis` are unchanged | rename project/namespace; **keep assembly `praxis`** | `InternalNamingGuardTests` (CLI identity), `PraxisNamingTests` |
| `Ros.Tests` | `Praxis.Tests` | test project, assembly, namespace | `tests/Ros.Tests/**`, CI, docs | none: the test assembly is not shipped | rename | CI runs `tests/Praxis.Tests/bin/Release/net10.0/Praxis.Tests.dll` |
| `Ros.slnx` | `Praxis.slnx` | solution | root, CI, docs, launcher hint, test root discovery | none: no released artifact or installed project references the solution | rename | `InternalNamingGuardTests` (workflow paths), `InternalNamingTests` |
| `src/Ros.Domain/` | `src/Praxis.Domain/` | directory (`git mv`) | — | none | rename | build |
| `src/Ros.Contracts/` | `src/Praxis.Contracts/` | directory (`git mv`) | — | none | rename | build |
| `src/Ros.Application/` | `src/Praxis.Application/` | directory (`git mv`) | — | none | rename | build |
| `src/Ros.Infrastructure/` | `src/Praxis.Infrastructure/` | directory (`git mv`) | — | none | rename | build |
| `src/Ros.Cli/` | `src/Praxis.Cli/` | directory (`git mv`) | `./praxis` launcher default DLL path, workflows | low: workflows that build a *pinned older* ref (`foundations-verify.yml`, `ros-fs-assets.yml`) must still find `src/Ros.Cli` | rename; pinned-ref workflows probe both paths | `InternalNamingGuardTests` (pinned-ref fallback) |
| `tests/Ros.Tests/` | `tests/Praxis.Tests/` | directory (`git mv`) | CI, docs | none | rename | CI |
| `RosRepositoryRoot` | `PraxisRepositoryRoot` | MSBuild property | `Directory.Build.props`, `Praxis.Infrastructure.fsproj`, `Site.Tests/WorkflowTests.fs` | none: defined and consumed only inside this repository's MSBuild evaluation | rename | build; `Site.Tests` embedded-payload test |
| `RosPackageVersion` | `PraxisPackageVersion` | MSBuild property | `Directory.Build.props` | none: only feeds `Version`/`InformationalVersion` | rename | `PraxisNamingTests` (`--version`), release smoke tests |
| `ros.payload/` | `praxis.payload/` | embedded-resource logical-name prefix | `Praxis.Infrastructure.fsproj`, `Lifecycle/Payload.fs` | none: written by the fsproj and read only by `Payload.fs` in the same assembly; the prefix is stripped before any path is used, so no manifest, installation record or output contains it | rename atomically (fsproj + lookup) | `LifecycleTests` (embedded payload matches the manifest), `InternalNamingTests` |

## Preserved (not renamed)

| Category | Names | Why |
|---|---|---|
| B. Public compatibility | `./ros`, `ros`, `ros.ps1`, `ros.cmd`, `ros-hub`, `ROS_*` variables, `ros-fs-<rid>` release assets and `ros-fs-assets.yml`, the legacy `ros-fs.dll` fallback in `foundations-verify.yml` | aliases and assets existing installations and scripts depend on (DF-ROS-2026-A050, DF-ROS-2026-A044) |
| C. Persisted state | `.ros/`, `ros.json`, `.echelon/ros.json` and its `"tool": "ros"`, `rosVersion`, `ROS-INSTALL-*` | read by every installed project and earlier release |
| D. Historical identity | `DF-ROS-*`, `RQ-ROS-*`, `EV-ROS-*`, `HY-ROS-*`, `EX-ROS-*`, `JR-ROS-*`, `RP-ROS-*`; research records, experiment inputs/outputs (e.g. `EX-ROS-2026-A021-*` cohort and planner-config JSON naming `src/Ros.*`), `.ros/` events and telemetry naming old paths, `docs/migrations/fsharp/*`, `docs/upgrades/SDE-1.3.0-*`, `registries/*` generated from those records | immutable history: they describe what was true when written |
| E. Serialized/versioned contracts | `schemas/ros-*.schema.json`, `collector: "ros"`, `ros-clock`, JSON property names | persisted/wire contracts; no evidence they are internal |
| Test fixtures | `tests/Praxis.Tests/Fixtures/claude-session-transcript.jsonl` (a recorded command line naming `Ros.slnx`) | recorded input data; the adapter test asserts how it is parsed, not what it names |
| Resolved separately | Python oracle `tools/ros_cli.py` / `tests/test_ros_cli.py` | kept by this migration because `EV-*` evidence cites `tests/test_ros_cli.py` as a `source_uri`. That citation is historical text that no validator resolves, so WI-0066 later retired the oracle outright (its one uncovered case, supersession reciprocity, now has an F# test); the file remains in Git history |

## Permanent invariant

`InternalNamingTests` (in `tests/Praxis.Tests`) fails when current
implementation artifacts reintroduce `src/Ros.*`, `tests/Ros.Tests`,
`Ros.slnx`, `namespace`/`module`/`open Ros.*`, a `ProjectReference` to a
`Ros.*` project, `RosRepositoryRoot`/`RosPackageVersion` or the
`ros.payload/` prefix. It scans only current implementation surfaces
(`src/`, `tests/` excluding recorded fixtures, `site-tools/`, project and
solution files, launchers, workflows that build the current checkout, and
current developer documentation), and allows each preserved category above
explicitly, with its reason, rather than banning the string `ros`.
