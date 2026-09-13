# ROS Central Service and Integration Contract Upgrade — Current System Inventory

Captured at the baseline commit documented in `BASELINE.md`
(`ae9f709816d0384bd1a198376e79f4fe33c0574e`). This is an inventory, not a
design document: it records what exists today, its purpose, its
consumers, and whether it is expected to be touched by the migration. No
component is replaced, moved, or modified as part of producing this
document, per the migration specification's explicit instruction not to
begin replacing components during inventory.

Fields follow the specification's required schema: Name, Purpose, Inputs,
Outputs, Side effects, Consumers, Tests, Replacement planned, Migration
phase, Compatibility requirement.

## 1. F# solution (`Ros.slnx`)

### 1.1 `Ros.Domain`

- **Purpose**: pure semantic model — Tier 1 of the four-tier architecture
  (`.sde/architecture/FOUR-TIER-ARCHITECTURE.md`). No I/O, no framework
  dependencies.
- **Inputs**: in-memory values only (records/DUs constructed by callers).
- **Outputs**: validated domain values, decision results (e.g.
  `BacklogQueueFinding list`), pure projections.
- **Side effects**: none.
- **Modules**: `Artifacts/{Model,Policy,Projection}`, `Git/Model`,
  `Telemetry/{Capability,ChangeSummary,ExecutionLink,Identity,Summary,
  TelemetryAdapters,TelemetryValidation,Validation}`,
  `Work/{AdapterContract,Attachment,Attribution,Backlog,Capture,
  ContextPlan,Identity,Model,PathFilter,Plan,QueuePresentation,
  QueueValidation,TelemetryResolution,Update,WorkListView}`.
- **Consumers**: `Ros.Application`, `Ros.Contracts`, `Ros.Tests`.
- **Tests**: `Ros.Tests.dll` unit suite (376 tests at baseline), plus
  differential tests that exercise domain logic indirectly through the CLI.
- **Replacement planned**: No. This is the existing four-tier semantic
  core the new `Ros.Domain`-adjacent central concepts (Project
  Administration, Activity/ExternalEvent state) extend, not replace.
  New central-domain types are additive modules alongside the existing
  `Work`/`Artifacts`/`Git`/`Telemetry` ones.
- **Migration phase**: Referenced starting Phase 3 (Central domain) as
  the architectural precedent; not itself modified until a specific work
  item requires an additive module.
- **Compatibility requirement**: No existing module's public shape may
  change without a corresponding test update proving equivalence; new
  central modules must not introduce dependencies from `Ros.Domain` on
  anything outside `FSharp.Core`/`System.*`, matching the existing
  pattern.

### 1.2 `Ros.Contracts`

- **Purpose**: JSON serialization boundary — translates domain values
  to/from wire format for the CLI and web layers.
- **Inputs**: domain values (for serialize), raw JSON (for deserialize).
- **Outputs**: JSON strings/objects; parsed domain values.
- **Side effects**: none (pure transformation).
- **Modules**: `Artifacts/RegistryJson`, `Cli/FindingJson`,
  `Git/StatusJson`, `JsonRendering`,
  `Work/{AttributionJson,BacklogJson,BacklogTransitionJson,
  ContextPlanJson,DecisionJson,PlanJson,QueueValidationJson,
  TelemetryValidationJson}`.
- **Consumers**: `Ros.Cli`, `Ros.Application`, web servers (indirectly,
  via CLI JSON output), `Ros.Tests`.
- **Tests**: covered by `Ros.Tests.dll` and the Node differential suite
  (which diffs real CLI JSON output against frozen golden literals).
- **Replacement planned**: No. This is the direct architectural precedent
  for the new `Ros.Integration` package's own `serializeActivity`/
  `deserializeActivity` contract — the new package follows the same
  "explicit serialize/deserialize functions, golden-file tested" pattern
  already proven here, but lives in a separate, dependency-free assembly.
- **Migration phase**: Phase 1 (Integration contract) reuses this
  pattern; `Ros.Contracts` itself is not modified.
- **Compatibility requirement**: Existing golden-master JSON literals in
  the Node differential suite must not change in place; any wire-format
  change requires an explicit version bump and adapter, matching the
  spec's compatibility policy now being formalized for the new package.

### 1.3 `Ros.Application`

- **Purpose**: orchestration/operations — Tier 3, coordinates domain
  logic and infrastructure repositories to perform a unit of work (e.g.
  "begin work item", "record telemetry execution").
- **Inputs**: CLI command parameters, repository interfaces.
- **Outputs**: updated domain state, operation results.
- **Side effects**: delegates all actual I/O to `Ros.Infrastructure`.
- **Modules**: `Artifacts/Operations`, `Git/Operations`,
  `Telemetry/Operations`, `Work/Operations`.
- **Consumers**: `Ros.Cli`.
- **Tests**: `Ros.Tests.dll`, Node differential suite (end-to-end through
  the CLI).
- **Replacement planned**: No. New central orchestration (activity
  ingestion, outbox processing) is new, additive code following this same
  tier, not a replacement of existing operations.
- **Migration phase**: Phase 3–5 add new operation modules for Central;
  existing modules unchanged.
- **Compatibility requirement**: Existing CLI command behavior (inputs,
  outputs, exit codes) must remain byte-identical per the differential
  test suite.

### 1.4 `Ros.Infrastructure`

- **Purpose**: Tier 4 — file-based repositories and effects (reading and
  writing `.ros/` state, git operations, JSON canonicalization, registry
  locking/transactions).
- **Inputs**: repository root path, domain values to persist.
- **Outputs**: files under `.ros/`, `research/`, `registries/`.
- **Side effects**: filesystem writes, git commands, file locks.
- **Modules**: `Artifacts/{FileArtifactRepository,FrontMatter,
  RegistryLock,RegistryTransaction}`, `Git/GitRepository`,
  `Json/CanonicalJson`,
  `Work/{BacklogStateTransaction,FileAdapterRepository,
  FileBacklogQueueRepository,FileEventLogRepository,
  FileEvidenceRepository,FileMetricRegistryRepository,
  FileTelemetryExecutionRepository,FileTelemetryFinalizationRepository,
  FileTelemetryQueryRepository,FileTelemetryStateRepository,
  FileTelemetryValidationRepository,FileWorkConfigRepository,
  FileWorkContextRepository,FileWorkListRepository,WorkStateTransaction}`.
- **Consumers**: `Ros.Application`, `Ros.Cli`.
- **Tests**: `Ros.Tests.dll` (includes real-filesystem round-trip tests,
  e.g. `FileBacklogQueueRepository.readItems`).
- **Replacement planned**: No, for existing repository-local state. A new
  `Ros.Persistence` project is additive, for Central-only state (e.g. a
  managed database for organizations/projects/activities/outbox), and
  must not replace or wrap the existing file-based repositories used by
  local ROS commands.
- **Migration phase**: Phase 4 (Central host) introduces `Ros.Persistence`
  as a new, separate project; this project is untouched.
- **Compatibility requirement**: File formats under `.ros/` and
  `research/` must not change shape without an explicit migration path;
  local commands must keep working with zero dependency on any new
  Central persistence layer, per the spec's "local repos stay
  independent" rule.

### 1.5 `Ros.Cli`

- **Purpose**: the actual host/dispatcher — `src/Ros.Cli/Program.fs`
  (2486 lines), the `ros-fs` executable that parses CLI arguments and
  dispatches to `Ros.Application` operations.
- **Inputs**: command-line arguments, environment.
- **Outputs**: stdout/stderr JSON or text, process exit codes.
- **Side effects**: everything the underlying operations do; this is the
  entry point.
- **Consumers**: `./ros` launcher script (execs `dotnet .../ros-fs.dll`
  directly per `DF-ROS-2026-A030`), CI (`ros-validation.yml`), the Node
  differential test suite (which shells out to the real CLI and diffs
  output), the two Node HTTP servers (`ros_server.mjs`,
  `ros_hub_server.mjs`, which shell out to the CLI for state changes).
- **Tests**: F# differential/CLI suite (187 tests at baseline, comparing
  real CLI output against frozen golden-master literals), Python
  `test_ros_cli.py` (7 tests).
- **Replacement planned**: No. `Ros.Host` (the new Central executable) is
  a **separate, new executable**, not a replacement of `Ros.Cli` — the
  spec is explicit that repository-local ROS commands (`validate`,
  `build`, `test`, SDE verification) must keep working standalone.
- **Migration phase**: Phase 4 introduces `Ros.Host` alongside, not
  instead of, `Ros.Cli`.
- **Compatibility requirement**: Every existing CLI subcommand, flag, and
  JSON output shape must remain unchanged; command renames require
  aliases per the spec's forbidden-behaviors list.

## 2. Node.js tooling (`tools/`)

### 2.1 `ros_cli.mjs` / `ros_cli.py`

- **Purpose**: legacy/parallel Node and Python implementations of ROS CLI
  logic, retained for differential testing against the F# CLI (the
  migration from Node/Python to F# for the *local* CLI already happened
  in this repository's own prior history — see
  `docs/migrations/fsharp/`).
- **Inputs/Outputs**: same shape as `Ros.Cli` for the commands they cover.
- **Side effects**: same class as `Ros.Cli` (filesystem, git).
- **Consumers**: differential test suite (compares Node/Python output
  against F# output as the reference-vs-candidate pair), `ros_server.mjs`
  / `ros_hub_server.mjs` (still shell out to `ros_cli.mjs`, not the F#
  binary, as of this baseline — confirmed by their route handlers).
- **Tests**: Node (`node --test`) suite, Python `unittest` suite.
- **Replacement planned**: Already substantially superseded by
  `Ros.Cli`; full retirement is governed by the existing
  `docs/migrations/fsharp/STATUS.md`/`ROADMAP.md`, which predates and is
  independent of this Central migration. This inventory does not change
  that plan's timeline. Not in scope for Phase 0–11 of the Central
  migration.
- **Migration phase**: N/A to this migration.
- **Compatibility requirement**: No change; this migration does not touch
  the Node/F# CLI parity effort.

### 2.2 `ros_server.mjs`

- **Purpose**: single-repository local HTTP API used by `web/` (a
  browser UI for the work-protocol backlog).
- **Inputs**: HTTP requests.
- **Outputs**: JSON responses; delegates mutations to `ros_cli.mjs`.
- **Routes** (confirmed from source): `GET /api/work`,
  `GET /api/work/ready`, `GET /api/work/:id`, `POST /api/work`,
  `POST /api/work/:id/ready`, `POST /api/work/:id/block`,
  `POST /api/work/:id/abandon`, `POST /api/work/:id/update`,
  `POST /api/work/:id/attachments`, `POST /api/work/:id/start`,
  `POST /api/work/:id/resume`, `POST /api/work/:id/complete`,
  `GET /api/validate`, `GET /api/status`.
- **Side effects**: shells out to `ros_cli.mjs`, which performs the
  filesystem/git side effects.
- **Consumers**: `web/app.ts` (the single-repo web UI).
- **Tests**: Node `ros-server` test file (part of the 55-test Node
  suite).
- **Replacement planned**: No. This is a single-repository local tool,
  orthogonal to the new Central HTTP host. Not superseded by
  `Ros.Host`'s `/integration/v1/*` endpoints, which serve a categorically
  different purpose (external inbound integration, not local UI
  backing).
- **Migration phase**: Not scheduled for replacement in Phases 0–11.
- **Compatibility requirement**: Unaffected by this migration; must
  continue working with zero dependency on Central.

### 2.3 `ros_hub_server.mjs`

- **Purpose**: **multi-repository** local HTTP API backing `web-hub/` —
  registers repositories and aggregates work-protocol state across them.
  This is directly relevant prior art for the new spec's "Project
  Administration" / repository-mapping requirement.
- **Routes** (confirmed from source): `GET /api/repos`,
  `POST /api/repos`, `DELETE /api/repos/:id`, `GET /api/work`
  (aggregated across all registered repos), `POST /api/repos/:id/work`.
- **Side effects**: reads a local repo registry (outside any single
  repo's `.ros/`), shells out to each registered repo's CLI.
- **Consumers**: `web-hub/app.ts`.
- **Tests**: Node `ros-hub` test file (part of the 55-test Node suite).
- **Replacement planned**: **Not replaced, but explicitly superseded in
  intent** by the new central Project Administration domain
  (`Organization → Project → RepositoryAssignment`) starting Phase 7.
  This existing tool solves a narrower, local-machine-only version of
  the same problem (repository registration + cross-repo aggregation)
  without any organizational/project modeling, authentication, or
  durable central storage. The Central host does not need to delete or
  break this tool — it continues to serve local multi-repo aggregation
  on a single machine — but the new Project Administration model should
  be designed with awareness that this precedent exists, and later
  phases should evaluate whether `ros_hub_server.mjs`'s repo registry
  can eventually delegate to (not be forcibly replaced by) Central data,
  per the spec's "compatibility before replacement" principle. No
  deletion is planned in this document.
- **Migration phase**: Referenced as design precedent starting Phase 7
  (Project Administration); no code change to this file scheduled in
  Phases 0–11.
- **Compatibility requirement**: Must keep working standalone (no
  Central dependency) per the spec's "local repos stay independent"
  rule; any future integration is additive and optional.

### 2.4 Other `tools/` files

- `http_body.mjs`, `ros_git.mjs`, `ros_persistence.mjs`,
  `ros_telemetry.mjs`, `__init__.py`: shared helpers used by the above.
  No direct relevance to the Central migration; not scheduled for
  replacement.

## 3. GitHub Actions workflows (`.github/workflows/`)

### 3.1 `ros-validation.yml`

- **Purpose**: CI gate — runs the full validation suite (`npm run
  test:all`, `./ros validate`, `./ros registry check`) on every push/PR.
- **Consumers**: GitHub branch protection / PR merge gating.
- **Replacement planned**: No — explicitly forbidden by the spec
  ("do not replace `ros-validation.yml`").
- **Migration phase**: N/A — remains as-is through all phases.
- **Compatibility requirement**: Must continue passing at every commit,
  including every migration-phase commit.

### 3.2 `publish.yml`

- **Purpose**: npm package + GitHub Release publishing for the
  `@echelon-foundry/repository-operating-system` package (stable vs.
  `@main` snapshot version detection, 5-RID binary builds, npm dist-tags).
- **Replacement planned**: No. Unrelated to the new NuGet package
  publishing pipeline (`integration-package-ci.yml` /
  `publish-integration-package.yml`), which is entirely new and
  additive.
- **Migration phase**: New workflows added in Phase 2 (WI-6) and Phase 6
  (WI-13) sit alongside this one, scoped to different paths
  (`src/Ros.Integration/**`) so they never trigger on the same changes.
- **Compatibility requirement**: Unaffected; must not be modified by this
  migration.

## 4. Governance and schema surfaces

### 4.1 `AGENTS.md`, `.sde/`

- **Purpose**: canonical ROS/SDE governance — work-protocol lifecycle,
  decision/evidence record standards, the four-tier architecture
  doctrine, semantic authority rules.
- **Replacement planned**: No. The spec requires *adding* three new
  permanent rules ("Receiver-Owned Integration Contracts", "Compatibility
  Before Replacement", "Complexity Requires Evidence") to this governance
  surface, not replacing any existing rule.
- **Migration phase**: Addition is not tied to a specific numbered work
  item in the spec; recommended placement and timing are addressed in
  `MIGRATION-PLAN.md`.
- **Compatibility requirement**: All existing SDE rules (explicit
  semantic authority, legal state transitions, bounded reasoning scope,
  mechanical-first execution, boundary validation, behavioral
  verification, defect classification, contemporaneous telemetry,
  explicit stop conditions) must continue to govern both existing ROS
  and all new Central code without exception.

### 4.2 `schemas/*.schema.json`

- **Purpose**: JSON Schema definitions for artifact metadata, evidence,
  execution telemetry, experiments, hypotheses, journals, missions,
  reps, theories, work-adapter request/result, work-protocol.
- **Replacement planned**: No. The new integration contract
  (`ActivityObservation` etc.) is a **new, separate** schema surface
  (versioned independently, living with `Ros.Integration` and its golden
  fixtures under `tests/contracts/`), not an extension of these existing
  schemas, which describe repository-local SDE artifacts, a different
  concern from inbound external activity data.
- **Migration phase**: Phase 1 introduces new schema/contract artifacts
  alongside these, not in place of them.
- **Compatibility requirement**: Unaffected.

### 4.3 `starter/` bootstrap profiles (`greenfield`,
`project-administration`)

- **Purpose**: bootstrap templates for new repositories adopting ROS.
- **Note**: the existing `project-administration` starter profile name is
  a naming coincidence worth flagging — it predates and is conceptually
  narrower than the new central "Project Administration" domain
  (organization/project/repository/work-item modeling). No rename or
  merge is planned; this is noted here only to avoid future confusion
  between the two.
- **Replacement planned**: No.
- **Migration phase**: N/A.
- **Compatibility requirement**: Unaffected.

## 5. Summary table

| Component | Replacement planned | Migration phase touching it | Compatibility requirement |
|---|---|---|---|
| `Ros.Domain` | No (additive modules only) | Phase 3+ | Existing modules unchanged |
| `Ros.Contracts` | No | Phase 1 (pattern reused) | Golden literals unchanged |
| `Ros.Application` | No (additive modules only) | Phase 3–5 | Existing CLI behavior unchanged |
| `Ros.Infrastructure` | No | Phase 4 (new sibling project) | File formats unchanged |
| `Ros.Cli` | No (new sibling executable) | Phase 4 (Ros.Host added) | All commands/flags/output unchanged |
| `ros_cli.mjs`/`.py` | Governed by pre-existing, unrelated migration | N/A to this migration | No change |
| `ros_server.mjs` | No | N/A | No change |
| `ros_hub_server.mjs` | No (superseded in intent, not deleted) | Phase 7 (design precedent) | Must keep working standalone |
| `ros-validation.yml` | No (forbidden to replace) | N/A | Must keep passing every commit |
| `publish.yml` | No | N/A | Unaffected |
| `AGENTS.md`/`.sde/` | No (additive rules only) | Unscheduled — see Migration Plan | All existing rules remain in force |
| `schemas/*.schema.json` | No | N/A (new, separate schema surface) | Unaffected |
| `starter/project-administration` | No | N/A (name collision noted only) | Unaffected |

No component in this inventory has been modified, moved, or deleted in
the course of producing it.
