---
id: EV-ROS-2026-A060
title: "PR #92 pre-merge regression fence: semantic conflict matrix, baselines, and the reconciliation manifest"
status: review
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-30
updated: 2026-09-30
research_area: repository-operating-system
evidence_type: test-result
supports: []
related_documents:
  - docs/work-protocol.md
  - docs/remote-agent-contract.md
  - DF-ROS-2026-A042
  - DF-ROS-2026-A044
  - DF-ROS-2026-A045
tags: [pr92, reconciliation, regression, characterization, praxis-rename]
confidence: high
provenance:
  contributions:
    EXE-20260930T162437557Z-40b62e08:
      operations: [created]
      at: 2026-09-30T17:08:11.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PR92-PREMERGE-REGRESSION-FENCE: semantic conflict matrix, baselines and reconciliation manifest for PR #92"
      evidence: [tests/Ros.Tests/PremergeFence.fs]
---

# PR #92 pre-merge regression fence

Work item `PRAXIS-PR92-PREMERGE-REGRESSION-FENCE` (this branch,
`test/pr92-premerge-main-characterization`) and its PR #92-side companion
`PRAXIS-PR92-PREMERGE-RENAME-FENCE` (branch
`claude/praxis-fsharp-only-cleanup-f97n8u`). Nothing here reconciles the two
branches. This record is the handoff for whoever does.

## 1. Authoritative refs (resolved 2026-09-30 with Git)

| Ref | SHA |
|---|---|
| `main` (`MAIN_SHA`) | `fb6a0f9d6c8e25e979be436346fd803184e306a0` |
| PR #92 head before this work (`PR92_SHA`) | `3cf2bd37b8a729ac683e6062013b7f2b31e10a5b` |
| merge base (`MERGE_BASE_SHA`) | `9ace9d9acf711e1013347b5ed8bb756bcaf298cf` |
| PR #92 head after this work | `a10be1f` (tests `6cabfc5`, Praxis state `a10be1f`) |

- PR #92 is **21 commits ahead and 120 behind** `main`. The prompt that
  started this work said 105 behind; `main` gained #126 and more since then.
- Paths changed since the merge base: main **266**, PR #92 **347**. **59**
  paths changed on both sides (4 of them under `.ros/`).
- Of the 59, only these are production source files: `src/Ros.Cli/{Program,Lifecycle,RemoteCommands}.fs`,
  `Ros.Cli.fsproj`, `Ros.Domain.fsproj`, `Ros.Infrastructure.fsproj`,
  `Ros.Domain/Telemetry/TelemetryValidation.fs`,
  `Ros.Infrastructure/Git/GitRepository.fs`,
  `Ros.Infrastructure/Lifecycle/Payload.fs`. The rest are workflows,
  launchers, docs, registries, schemas and tests.
- The overlap count understates the risk. Most of the losses below would
  happen with **no textual conflict**, for three reasons: PR #92 deletes
  whole files that main extended (every Node test and `package.json`);
  PR #92 renames the assembly that main's test harness names; and the two
  branches reuse the same record IDs for different documents in
  different file names.

## 2. Baselines (run before any test was added)

Environment: .NET SDK 10.0.112 (distro package; the Microsoft installer host
is blocked by network policy), Node 22, Python 3. Main and PR #92 both
reference `EchelonFoundry.Aegis.Core 1.0.0`, which needs FSharp.Core
>= 10.1.400, but SDK 10.0.112 bundles 10.0.112, so every build fails with
NU1605. Every build here therefore ran with the MSBuild property
`FSharpCoreImplicitPackageVersion=10.1.400`, set as an environment
variable. No file was changed for this. CI's `setup-dotnet 10.0.x` gets a
newer SDK and is unaffected. **This is an environment limitation, not a
branch defect.**

| Check | main `fb6a0f9` | PR #92 `3cf2bd3` |
|---|---|---|
| restore + Release build | pass, 0 warnings | pass, 0 warnings |
| F# `Ros.Tests` | **792/792** | **949/949** |
| F# `Site.Tests` | n/a (Node site tests) | **101/101** |
| Node `npm test` suites | 104/104 | n/a (Node removed) |
| Node `test:fsharp` differentials | 257/257 | n/a |
| Node inbox + site (not in any npm script) | 109/109 | n/a |
| Python oracle `unittest` | 7/7 | 7/7 |
| `architecture check` | n/a (command absent) | pass |
| `registry check` | pass | pass |
| `validate` | pass | pass |
| `--version` | `ros-fs 3.6.0` | `praxis 3.4.0` (`./ros` identical) |

No baseline failures, skips or flakes were observed on either branch.

## 3. After the fence was added

| Branch | Result |
|---|---|
| `test/pr92-premerge-main-characterization` (main + 37 fence tests) | F# **829/829**. `validate` passes, also with `ROS_BASE_REF=origin/main` |
| PR #92 (+7 tests) | F# **956/956**. `architecture check`, `registry check` and `validate` (`PRAXIS_BASE_REF=3cf2bd3`) pass |
| **Differential:** main's 37 fence tests run against PR #92's code in a throwaway worktree | **33 fail, 4 pass**. The 4 that pass cover behavior both branches share: npm retirement, native checksums, and the two base-era persist "any other refusal" cases. If PR #92's side of the merge wins, the fence fails. |

## 4. Semantic conflict matrix

Risk scale: LOW, MEDIUM, HIGH, CRITICAL.

### 4.1 CLI entry point and command dispatch (CRITICAL)
- **Files, main:** `src/Ros.Cli/Program.fs`, `Ros.Cli.fsproj` (compile items `CheckpointCommands`, `ExecutionCommands`, `InstallationCommands`, `PlanCommands`; `PackAsTool`), `Ros.Domain.fsproj`, `Ros.Infrastructure.fsproj`, `Ros.Application.fsproj`, `Ros.Contracts.fsproj`.
- **Files, PR #92:** `Program.fs` (`architecture`, `web serve`, `hub`; `EnvironmentAliases` applied at start-up), `Ros.Cli.fsproj` (`AssemblyName=praxis`; `ArchitectureCommands`, `WebHttp`, `CliProcess`, `WebInterface`, `Hub`), `Ros.Domain.fsproj` (`ImplementationLanguagePolicy`, `EnvironmentAliases`).
- **Main added:** the dispatch branches `plan …`, `execution …`, `installation …` (including `list|status|history`), `work checkpoint`, `work checkpoint show`, `work continue`, `work abandon`, `status --offline`, and `work context --text|--offline`. The durability guards on `complete`/`block` (`--unrecoverable-reason`). `init`/`upgrade` self-register with Project Administration.
- **PR #92 added:** `praxis` as the program name in version, help and repair hints; `architecture check`; `web serve`; `hub`; PRAXIS_ environment aliasing.
- **Tests on main:** `PlanningCliTests`, `CheckpointCliTests`, `ContinuationCliTests` and `WorkAbandonTests` go through the real CLI. Nothing drove `execution`, `installation` or self-registration through dispatch.
- **Tests on PR #92:** `PraxisNamingTests` and the `*CliTests` ports.
- **Gaps (now closed):** command-surface reachability, help grammar, and init self-registration.
- **If main wins:** the product name reverts to `ros-fs` and `architecture`, `web` and `hub` disappear. `PraxisNamingTests` fails.
- **If PR #92 wins:** the build breaks (the new modules are no longer compiled), or it compiles and the `plan`, `execution`, `installation`, `checkpoint`, `continue` and `abandon` dispatch lines are simply gone.
- **Required tests:** `PremergeCommandSurfaceTests` (added) and `PraxisNamingTests` (existing and added).

### 4.2 Version source and packaging (CRITICAL)
- **Files, main:** `Directory.Build.props` reads `package.json` (3.6.0); `Ros.Cli.fsproj` `PackAsTool`/`ToolCommandName=praxis`/`PackageId=EchelonFoundry.Praxis`; `scripts/praxis-release-bump.sh` (Node, `package.json`).
- **Files, PR #92:** `Directory.Build.props` reads `release.json` (**3.4.0**). `package.json` is deleted.
- **If main wins:** the version source is a file PR #92 deleted, so the build fails.
- **If PR #92 wins:** the version silently **regresses to 3.4.0**. `v3.4.0` is already released, and the .NET global tool packaging is lost.
- **Tests:** PR #92's `PraxisNamingTests`, `StatusValidateCliTests` and `CliGolden` assert "version = release.json". The added fence asserts "version = whichever declaration file exists, and it is ≥ 3.6.0", and checks the tool packaging properties.

### 4.3 Test harness assembly name (HIGH)
- **Main:** `PraxisCli.cli` (`CheckpointCliTests.fs:55`) and `WorkReconciliationEffectTests.fs:18` hardcode `ros-fs.dll`. They are used by CheckpointCli, CheckpointGuard, Continuation, RecoveryProof, TelemetrySegmentation, CommitOwnership, WorkAbandon and PlanningCli.
- **PR #92:** the assembly is renamed to `praxis.dll`.
- **Failure:** after the merge, every one of those main suites fails. The failure is loud, not silent, but it hides whether their behavior survived.
- **Fix at reconciliation:** point `PraxisCli.cli` at `praxis.dll`, or resolve it the way `PremergeFence.cliAssembly` does. The fence harness already resolves either name.

### 4.4 Lifecycle: init, upgrade, payload (HIGH)
- **Files, main:** `Lifecycle.fs` (`status --offline` help and continuity text), `Payload.fs` (comments only); starter `ros.json` gets `workProtocol.continuity.requireDurableCheckpoint: true` (greenfield and project-administration); starter `HANDOFF.md` gets a continuity section.
- **Files, PR #92:**
  - `Lifecycle.fs`/`Payload.fs`/`Installation.fs`/`Planning.fs`/`Model.fs`: praxis naming, a `MoveManagedFile` upgrade step, the `replaces` manifest key, and `readTargetVersion` (stable pin) removed.
  - The scaffold gains `praxis`, `praxis.cmd` and `praxis.ps1`; `ros*` become aliases; `ros-validation.yml` becomes `praxis-validation.yml`; `tools/ros_fs_launcher.mjs` and the Node/web payload are deleted.
- **Observed differential:** fresh init on main writes `ros`, `tools/`, `ros-validation.yml` and continuity `{requireDurableCheckpoint: true}`. On PR #92 it writes `praxis*`/`ros*`, `praxis-validation.yml` and **no continuity setting**.
- **If main wins:** the Praxis launchers, the workflow rename and move-on-upgrade are lost.
- **If PR #92 wins:** new installations no longer enforce durable checkpoints, `--offline` help text disappears, and the Checkpoint/Execution/Installation/Plan compile items go missing.
- **Tests:**
  - Main: `CheckpointGuardTests` "starter: a new installation enforces durable checkpoints…" (runs through the `ros-fs.dll` harness).
  - Added fence: "a new installation … enforces durable checkpoints", which works with either assembly name.
  - PR #92: `PraxisNamingTests` (fresh, ROS-era upgrade, customized workflow), `LifecycleCliTests`, `InstalledRepositoryTests`.
  - Added on PR #92: the retained persisted names; a customized `ros` launcher blocks upgrade with exit 5 and nothing changes; the legacy `tools/ros_fs_launcher.mjs` is left in place; ROS-era work is resumed via `PRAXIS_*`.

### 4.5 Remote execution protocol and executor (CRITICAL)
- **Files, main:**
  - `RemoteCommands.fs`: the GH-113 ownership guard, and `describe` gains `requiresExecution`/`introducedIn`.
  - `Remote/Protocol.fs`, `Execution.fs`, `RemoteJson.fs` and the schema: protocol **1.3**, `work.checkpoint`, `work.continue`, and `work.block.unrecoverableReason`.
- **Files, PR #92:** `RemoteCommands.fs` has a one-line comment change. Everything else in this area is untouched.
- **Differential:** `remote describe` reports protocols `1.0–1.3` and 18 operations on main, but `1.0–1.2` and 16 operations on PR #92.
- **Tests on main:** only the domain parts are in F# (`RemoteProtocolTests` 1.3 cases). The end-to-end remote checkpoint/continue path, GH-113 and the takeover are tested **only in Node** (`remote-checkpoint.test.mjs`, `remote-execute.test.mjs`).
- **Tests on PR #92:** `RemoteExecuteCliTests` (a 1.2-era port) asserts `protocolVersions = ["1.0";"1.1";"1.2"]` (`RemoteExecuteCliTests.fs:482`) and deep-compares a describe entry without the new fields (`:492-494`). **These two assertions will fail against the merged code and must be updated to 1.3**, which is intended behavior change.
- **If PR #92 wins:** protocol 1.3, remote checkpoint/continue and GH-113 are lost.
- **Added:** `PremergeRemoteTests` (6 tests, an F# port of the Node suites).

### 4.6 Remote adapter scripts and workflows (HIGH)
- **Main:**
  - `scripts/praxis-remote-persist.sh`: `rate_limited`, same-request branch reuse, existing-PR reporting, conflicting state branch, and the `reused` field.
  - `.github/actions/praxis-remote/action.yml`: job log inside `::stop-commands::`.
  - New `scripts/praxis-remote-inbox.sh` and `.github/workflows/praxis-remote-inbox.yml` (DF-ROS-2026-A045).
- **PR #92:** `praxis-remote-enable.sh` (lookup is `./praxis` first, then `ros`), plus the `RemotePersistScriptTests` port (base-era only).
- **Tests on main:** Node only (`praxis-remote-adapter.test.mjs` additions, `praxis-remote-inbox.test.mjs`).
- **If PR #92's Node removal wins:** every one of these behaviors becomes untested. The inbox relay would have no test on either side.
- **Added:** `PremergeRemoteScriptTests` (20 tests).

### 4.7 Durable continuity: checkpoint, continue, ownership, abandon (HIGH)
- **Main:** `Work/Checkpoint*.fs`, `Continuity.fs`, `Ownership.fs`, `Git/Durability.fs`, `FileCheckpointRepository.fs`, and the CLI commands. PR #92 has none of this.
- **Tests on main:** extensive F# coverage (CheckpointDomain/Persistence/Cli/Guard, Continuation, RecoveryProof, CommitOwnership, WorkAbandon). The CLI-level suites use the `ros-fs.dll` harness (see 4.3).
- **If PR #92 wins** `Program.fs` or the fsproj files: all of it disappears.
- **Guarded by:** the existing suites (once the harness is fixed), plus the fence's command-surface and remote tests.
- **Additional risk:** main makes new installations `requireDurableCheckpoint: true`. PR #92's golden ports (`CliGolden`, `WorkLifecycleCliTests`, …) were written before continuity existed and have no opt-out. Main's Node goldens got one (`tests/legacy-completion.mjs`). **Expect some PR #92 goldens to need that opt-out after the merge.**

### 4.8 Git repository layer (LOW)
- **Main:** durability parsers, `ls-remote`, `ROS_GIT_REMOTE_TIMEOUT_SECONDS`, and commit reachability.
- **PR #92:** only `listRepositoryFiles`.
- The changes don't overlap and both sides keep their behavior. Covered by `GitDurabilityTests` and `CommitOwnershipTests` on main, and `ImplementationLanguagePolicyTests` on PR #92.
- **Post-merge gap:** `GIT_REMOTE_TIMEOUT_SECONDS` is missing from PR #92's `EnvironmentAliases.suffixes`.

### 4.9 Native release and distribution (HIGH)
- **Main:**
  - `native-release.yml` adds a NuGet tool pack with a smoke test expecting `"ros-fs ${VERSION}"`, `echelon-release.json`, and a `ros-fs-assets` job.
  - `release.yml` (one-click) and `ros-fs-assets.yml` are new: legacy `ros-fs-<rid>` and `checksums.txt` for already-scaffolded projects.
  - Everything reads `package.json` through `node`.
- **PR #92:** `native-release.yml` has no Node; the version comes from `./praxis --version`; the binary is `praxis`.
- **Tests:**
  - Main: Node `npm-bootstrap.test.mjs` release checks, and `tests/site/workflow.test.mjs`.
  - PR #92: `Site.Tests/WorkflowTests.fs`, with `releaseWorkflows = [native-release.yml; publish.yml]`, missing the two new workflows.
- **If PR #92 wins:** the NuGet tool, one-click release and legacy assets are lost, and existing scaffolded projects' `./ros` cannot download.
- **If main wins:** the workflows call `node`/`npm` and read a deleted `package.json`, and `ros-fs-assets.yml` expects `dist/publish/*/ros-fs`.
- **Added:** `PremergeReleaseTests` (6 tests).

### 4.10 Node removal versus main's Node-only tests (CRITICAL)
- PR #92 deletes `package.json`, `tools/*.mjs`, `lib/*.mjs` and every `tests/*.mjs`, and forbids repository-owned Node through `architecture check`.
- Main added or extended these Node tests after the merge base:
  - `remote-checkpoint.test.mjs`, `praxis-remote-inbox.test.mjs`, `legacy-completion.mjs`
  - additions to `remote-execute.test.mjs` and `praxis-remote-adapter.test.mjs`
  - `npm-bootstrap.test.mjs` release checks
  - the `work-complete` `--conclusion` differential
- New files survive the merge as orphans and fail `architecture check`. The modified files become modify/delete conflicts.
- **Everything main's Node files protected now has an F# equivalent on this branch** (see section 7). Resolving by deleting the Node files is therefore safe **only after** the F# fence is merged.

### 4.11 Record ID collisions (CRITICAL)
| ID | main | PR #92 |
|---|---|---|
| `DF-ROS-2026-A042` | durable work checkpoints and executor continuation | F#/.NET-only repository |
| `DF-ROS-2026-A043` | effective-current step telemetry segmentation | Praxis canonical name, ROS compatibility |
| `RQ-ROS-2026-A022` | durable checkpoints and agent continuity | F#/.NET-only repository |
| `RQ-ROS-2026-A023` | remote requests without Actions dispatch | Praxis canonical name |

- The file names differ, so Git keeps both files silently.
- `./ros validate` then reports `duplicate '<id>'` through `ArtifactPolicy.validate`, which fails loudly. What it cannot catch is every prose, code and test citation of these IDs that silently points at the wrong decision. The citing files include `EnvironmentAliases.fs`, `Program.fs:2825`, `Lifecycle.fs:615`, `ros`, the starter launchers, `PraxisNamingTests.fs:10`, `ImplementationLanguagePolicyTests.fs:6`, `AGENTS.md` and the workflows.
- **A renumbering decision is required before reconciliation** (section 9).

### 4.12 Telemetry and provenance (MEDIUM)
- **Main:** effective-current step segmentation (`Steps.fs`, `Usage.fs`), and abandoned work needs finalized telemetry.
- **PR #92:** "Praxis-derived (ros-derived)" message text, schema titles, and PRAXIS_ aliasing of `ACTOR*`/`TELEMETRY_*`. Detection is unchanged.
- Main's CLI-level telemetry tests (`cli segmentation`, recovery proof, which cover usage, cost and identity per execution and step) run through the `ros-fs.dll` harness, so they must be re-pointed (4.3).
- **Post-merge test:** telemetry recorded under `PRAXIS_*` identity keeps provider, runtime, execution, step and usage (section 6).

### 4.13 Governance docs, AGENTS.md, README (MEDIUM)
- Both branches rewrote `AGENTS.md`, `docs/work-protocol.md`, `docs/cli.md`, `docs/installation.md`, `docs/upgrading.md`, and others.
- Main added the continuity, inbox, planner and native-distribution sections. PR #92 changed `./ros` to `./praxis` and cites its colliding IDs.
- Text conflicts are expected. The resolution must keep main's content and PR #92's naming.
- No automated test covers prose except `RemotePersistScriptTests` (the operator doc names every failure code) and the fence's inbox-contract check.

## 5. Differential scenarios (same inputs, both baselines)

| Scenario | main | PR #92 | Class |
|---|---|---|---|
| `--version` | `ros-fs 3.6.0` | `praxis 3.4.0` | name: INTENTIONAL; number: **RECONCILIATION RISK** (3.4.0 < 3.6.0) |
| `--help` first line | `ros -- Repository Operating System lifecycle CLI` | `praxis -- Praxis, Echelon Foundry's repository operating system` | INTENTIONAL |
| unknown command | exit 2, global help | exit 2, global help | same |
| `init` (greenfield) files | `ros`, `tools/`, `ros-validation.yml` | `praxis*`, `ros*` (aliases), `praxis-validation.yml` | INTENTIONAL |
| `init` continuity | `requireDurableCheckpoint: true` | absent | **RECONCILIATION RISK** |
| `remote describe` | 1.0–1.3, 18 operations | 1.0–1.2, 16 operations | **RECONCILIATION RISK** |
| `status --json` keys | includes `continuity` | no `continuity` | **RECONCILIATION RISK** |
| `plan`, `execution`, `installation`, `work checkpoint`/`continue`/`abandon` | present | absent (fall through to global help) | **RECONCILIATION RISK** |
| `architecture check`, `web serve`, `hub` | absent | present | **RECONCILIATION RISK** (PR #92 side) |

## 6. Reconciliation manifest

### MUST PASS BEFORE RECONCILIATION
- On `main` plus this branch: `dotnet tests/Ros.Tests/.../Ros.Tests.dll` **829/829**, including every test named `fence …`. Also the Node `npm run test:all` suites (unchanged) and `./ros validate`.
- On PR #92 (`a10be1f`): F# **956/956**, Site **101/101**, `./praxis architecture check`, `registry check`, `validate`, and `test "$(./ros --version)" = "$(./praxis --version)"`.
- This branch's PR must be merged into `main` **before** reconciliation, so the Node-only behavior has F# protection when PR #92 deletes Node.

### MUST PASS AFTER RECONCILIATION (on the combined branch)
1. Every `fence …` test (37) unchanged. The harness resolves `praxis.dll`, and the root is found by `Ros.slnx`.
2. Every `praxis naming: …` test (17), including the 7 added.
3. Main's CLI suites (CheckpointCli, CheckpointGuard, Continuation, RecoveryProof, TelemetrySegmentation, CommitOwnership, WorkAbandon, PlanningCli, WorkReconciliationEffect), after only the harness change in 4.3.
4. `RemoteExecuteCliTests` updated only where protocol 1.3 intentionally changed `describe` (`:482`, `:492-494`).
5. Mandatory post-merge tests that **cannot run before the merge** because one side lacks half the behavior. Each must be written on the combined branch:
   - **P1, remote plus canonical naming.**
     - Setup: a `praxis init` repository with `remote.capabilities` set; a `work.start` → `work.checkpoint` → `work.continue` → `work.complete` sequence via `praxis remote execute`.
     - Expect: all four succeed at protocol 1.3; the generated `AGENTS.md`/`HANDOFF.md` in the installed repository invoke `./praxis`, and `./ros` still runs the same program.
     - Not executable before the merge: PR #92 has no 1.3 operations, and main scaffolds no `praxis` launcher.
   - **P2, continuity plus ROS-era persisted state.**
     - Setup: a ROS-era installation (PR #92's `makeRosEra`) with an item started under `ROS_*` identity and checkpointed; upgrade with `praxis`; a second actor under `PRAXIS_*` identity runs `praxis work continue`.
     - Expect: a new execution whose parent is the predecessor, which is recorded as interrupted; `.ros/` and `rosVersion` are intact; `work complete` passes the durable guard.
     - Not executable before the merge: PR #92 has no `work continue`, and main has no ROS-era upgrade path.
   - **P3, newer commands plus the renamed executable.** Run `./praxis plan analyze --json`, `./praxis execution list --json`, `./praxis installation list --json`, `./praxis remote describe` and `./praxis work checkpoint show X` through the shell launcher, and the same through `./ros`. Expect the same output as the `dotnet praxis.dll` invocation, each reaching its handler. The fence covers the dll invocation; the launchers only exist on PR #92.
   - **P4, telemetry plus the renamed entry point.** Run `work start`, `telemetry step start`, `telemetry record --metric tokens.input/cost.usd --step`, `telemetry step complete`, `telemetry usage --by step` with identity only in `PRAXIS_ACTOR*`/`PRAXIS_TELEMETRY_*`. Expect the execution identity (provider, runtime, session), step attribution, and usage and cost totals exactly as recorded, and provenance naming the same actor. Not executable before the merge because main has no `PRAXIS_*` aliasing.
   - **P5, version and packaging.** Expect `praxis --version` to equal the single declared version, at least 3.6.0; `dotnet pack` to produce `EchelonFoundry.Praxis` with command `praxis`; and the native-release smoke step to expect `praxis ${VERSION}`, not `ros-fs`.
   - **P6, environment aliases.** Expect `PRAXIS_GIT_REMOTE_TIMEOUT_SECONDS` to be honored (an alias entry needs adding).
   - **P7, record IDs.** Expect `praxis validate` to report no `duplicate` finding, and every citation of a renumbered ID to resolve to the intended decision.
6. `./praxis architecture check` passes. No Node test survives; each has an F# port listed in section 7.

### INTENTIONAL DIFFERENCES (keep PR #92's behavior)
- The canonical command is `praxis`, and `--version` prints `praxis <version>`.
- `ros` (and `ros.cmd`, `ros.ps1`, `ros-hub`) are compatibility aliases that exec the praxis launcher. They are never a second implementation.
- Help, usage and repair hints teach `praxis`.
- The validation workflow is `praxis-validation.yml`. Upgrade moves `ros-validation.yml` and keeps local edits.
- `PRAXIS_` is the canonical environment prefix. When both are set, `PRAXIS_*` wins; an empty `PRAXIS_*` counts as unset.
- The assembly is `praxis.dll`, and the source-checkout launcher is `./praxis`.
- No repository-owned Node (`architecture check`). The version source is `release.json`, but its value must become main's version (≥ 3.6.0).
- `architecture check`, `web serve` and `hub` are F# commands.

### INTENTIONALLY RETAINED ROS NAMES (must not be renamed by the merge)
- `.ros/`, `ros.json`, `.echelon/ros.json` (`"tool": "ros"`), `rosVersion`, `ROS-INSTALL-*`.
- Historical `DF-ROS-*`, `RQ-ROS-*`, `EV-ROS-*` and other `*-ROS-*` record IDs, and the `ROS_*` environment variables (still accepted).
- The internal `Ros.*` project and namespace names, deferred to PRAXIS-INTERNAL-NAMESPACES.
- Schema identifiers whose migration is not approved.
- The legacy `ros-fs-<rid>` release assets for already-scaffolded projects, until a decision retires them (section 9).

### MAIN BEHAVIOR THAT MUST SURVIVE (capability → guarding tests)
| Capability | Guard |
|---|---|
| `plan analyze/simulate/compare/explain/explain-group/groups/replay/freshness` | `PlanningCliTests`; fence command surface, help grammar and JSON contract |
| `execution start/show/list/step/evaluate/expand-scope/resolve-effect/transition/cleanup` | `ExecutionGovernanceTests` (in-process); fence command surface and JSON contract |
| `installation register/remove/verify/reconcile/list/status/history` | `InstallationRegistrationTests` (in-process); fence command surface |
| init/upgrade self-registration with Project Administration | fence "init self-registers…" |
| `work checkpoint`, `work checkpoint show`, the durable completion and block guards, `--unrecoverable-reason` | `CheckpointCliTests`, `CheckpointGuardTests`, `CheckpointPersistenceTests`; fence command surface |
| `work continue` and predecessor-interrupted takeover | `ContinuationCliTests`, `RecoveryProofTests`; fence remote handoff |
| shared-branch commit ownership (PRAXIS-CONT-12) | `CommitOwnershipTests` |
| `work abandon` | `WorkAbandonTests`; fence command surface |
| `work context --text/--offline`, `status --offline` with `continuity` | `CheckpointCliTests`; fence command surface |
| effective-current step telemetry and usage attribution | `TelemetrySegmentationTests`, `TelemetryStepsUsageTests` |
| `--conclusion` kept for non-research items (PRAXIS-REMOTE-16) | fence "work complete keeps an explicit --conclusion…" |
| new installations require durable checkpoints | `CheckpointGuardTests`; fence continuity default |
| praxis.remote 1.3: `work.checkpoint`, `work.continue`, `describe` `requiresExecution`/`introducedIn`, 1.2 refusal | `RemoteProtocolTests`; fence remote (3 tests) |
| GH-113 remote completion ownership guard and handoff exemption | fence remote GH-113 (2 tests) |
| remote takeover via block then resume | fence remote takeover |
| inbox relay: byte-exact, branch routing, refusals, only added/modified files, branch-creation push, trust boundary | fence inbox (9 tests) |
| persist: rate-limited classification (push and PR), same-request branch reuse, existing-PR reporting, occupied branch conflict | fence persist (8 tests) |
| action job log inside `::stop-commands::` | fence action |
| npm retired; native checksums; `ros-fs-<rid>` assets; one-click release; .NET global tool; `echelon-release.json` | fence release (6 tests) |
| version ≥ 3.6.0 equals the declared version | fence version |

## 7. Node-to-F# port ledger (main-only Node behavior)

| Node test | F# fence test |
|---|---|
| `remote-checkpoint.test.mjs` (5) | `PremergeRemoteTests` (5) |
| `remote-execute.test.mjs` takeover and 1.3 describe fields | `PremergeRemoteTests` takeover and describe |
| `praxis-remote-inbox.test.mjs` (10) | `PremergeRemoteScriptTests` inbox (9; the contract and target checks are merged into one) |
| `praxis-remote-adapter.test.mjs` main additions (9) | `PremergeRemoteScriptTests` persist and action (9) |
| `npm-bootstrap.test.mjs` release checks (2) | `PremergeReleaseTests` (6) |
| `work-complete-fsharp-differential` `--conclusion` | `PremergeCommandSurfaceTests` |
| `tests/legacy-completion.mjs` | none needed; it is a test-only opt-out helper (see 4.7) |

## 8. Defects and limitations found (not fixed; no production change was made)
- **Environment:** the distro SDK 10.0.112 cannot restore either branch without `FSharpCoreImplicitPackageVersion=10.1.400` (NU1605 from `EchelonFoundry.Aegis.Core`). This is present on both untouched branches, and CI is unaffected.
- **Main:** `tests/praxis-remote-inbox.test.mjs` is in no npm script, so CI never runs it. It passed when run manually (part of the 109/109). Its F# port runs in CI.
- **PR #92:** `release.json` declares 3.4.0 while main is at 3.6.0. This is a known consequence of the divergence, not a defect on PR #92 alone.
- **PR #92:** upgrade leaves a ROS-era `tools/ros_fs_launcher.mjs` as an unmanaged orphan. The new test characterizes this; whether upgrade should remove it is an open decision.

## 9. Unresolved behavioral decisions (need an owner decision before or during reconciliation)
1. **Renumber the colliding records.** Proposal: keep main's numbering, since it was released and is cited by shipped docs and events, and renumber PR #92's to `DF-ROS-2026-A048`/`A049` and `RQ-ROS-2026-A024`/`A025` (confirm these are free at the time), then rewrite every citation in section 4.11.
2. **Legacy `ros-fs-<rid>` assets.** Keep building them, from the `praxis` assembly and renamed, for projects scaffolded before the rename, or retire them with a decision record. The fence currently requires them.
3. **Version source.** Accept `release.json`, set it to main's current version, and port `praxis-release-bump.sh`, `release.yml` and `ros-fs-assets.yml` off Node and `package.json`.
4. **Stable-version pin.** PR #92 removed `Payload.readTargetVersion` (`lib/stable-ros-version.json`). Confirm this is intended.
5. **Orphaned ROS-era helper files after upgrade**: remove them, or keep them (current behavior).
6. **Continuity opt-out for PR #92's golden ports** once `requireDurableCheckpoint` defaults to true.

## 10. Conclusion
The HIGH and CRITICAL areas above now each have behavioral protection on the branch where they can run. The remaining post-merge obligations (P1–P7) are specified above. Reconciliation should not start until decision 1 (ID renumbering) is made and this branch's test-only PR has merged into `main`.
