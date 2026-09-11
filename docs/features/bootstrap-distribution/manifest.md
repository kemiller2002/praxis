# Feature Manifest — Bootstrap and distribution

## Purpose

Route changes to npm acquisition, profile materialization, installation
attribution, installation-integrity verification, and (since
`DF-ROS-2026-A029`/`DF-ROS-2026-A032`) on-demand acquisition of the
compiled F# CLI binary, both for this package's own `ros-fs` bin entry and
for the scaffolded project's own `./ros`, which is that same launcher by
default.

## Ownership

- State, including presentation state: `starter/*/manifest.json`, `package.json`,
  package contents, installed `.ros/installation.json`, and the per-user
  `ros-fs` binary cache (`~/.cache/ros-fs/`, override `ROS_FS_CACHE_DIR`).
- Transitions / commands / messages: `bin/ros-bootstrap.mjs` delegates to
  `lib/bootstrap.mjs` symbols `initializeProject`, `verifyProject`, and `main`.
  `bin/ros-fs.mjs` delegates to `lib/ros-fs-launcher.mjs`'s `run`.
- Invariants and guards: safe destination checks, collision preflight,
  preserve-existing policy, profile/manifest validation, checksums, file modes,
  and verification findings in `lib/bootstrap.mjs`. `ros-fs-launcher.mjs`
  never executes or caches a downloaded binary whose SHA-256 does not match
  its release's `checksums.txt` entry; a platform with no known RID mapping
  fails with a named, actionable message rather than guessing.
- Capabilities / authority: caller selects profile/target/force; manifest is
  authoritative for declared package materialization. Per `DF-ROS-2026-A032`,
  the scaffolded project's `./ros` is this same F# launcher, replacing Node,
  in both starter profiles. Per `DF-ROS-2026-A033`, the `greenfield` profile
  no longer scaffolds Node's implementation at all; `project-administration`
  still includes `tools/ros_cli.mjs` and companions, but only as that
  profile's own web/hub servers' in-process internal dependency, not as a
  CLI or a rollback path.
- Important effects and effect contracts: filesystem creation/copy/render,
  cleanup of newly written declared files after failure, and no network/Git
  semantics inside the bootstrap implementation. `ros-fs-launcher.mjs` is the
  one exception to "no network": it performs exactly one HTTPS fetch pair
  (checksums, then the binary) per (package version, platform) cache miss,
  and none at all on a cache hit.

## Interfaces

- Inbound: npm `ros-bootstrap` binary and `init`/`verify` CLI arguments; npm
  `ros-fs` binary and whatever arguments it forwards verbatim to the
  downloaded F# CLI.
- Outbound: installed files/directories/modes, installation attribution JSON,
  stdout/stderr, and process exit status. `ros-fs` additionally writes the
  cached binary under `ROS_FS_CACHE_DIR`/`~/.cache/ros-fs/` and inherits
  stdio/exit status from the binary it execs.

## Tests and verification

- Local behavior tests: `tests/npm-bootstrap.test.mjs`,
  `tests/ros-fs-launcher.test.mjs`.
- Boundary/contract tests: both profile manifests, npm pack inventory, checksum
  verification, and collision/rollback assertions. `ros-fs-launcher.mjs`'s
  RID mapping, checksum parsing, download/cache/exec cycle (against a real
  local HTTP server), cache-hit no-network-reuse, checksum-mismatch rejection
  (never cached), and missing-checksum-entry error path.
- Integration/live verification: clean temporary installation followed by the
  installed `./ros registry check` and `./ros validate` commands. For
  `ros-fs`: a real self-contained `linux-x64` binary served from a local
  HTTP server, fetched/cached/executed through `bin/ros-fs.mjs` end to end
  (`--version`), then re-invoked with the release URL pointed at an
  unreachable address to confirm the cache-hit path needs no network,
  running a real `validate` against this repository's own state.

## Dependencies

- Allowed direct dependencies: Node filesystem/path/crypto, package metadata,
  profile manifests, and source templates. `ros-fs-launcher.mjs` additionally
  uses Node's built-in `fetch`/`node:stream` (no new npm dependency) and the
  release artifacts `.github/workflows/publish.yml` builds and attaches to
  a stable version's GitHub Release.
- Required composition context: npm package layout and a writable target;
  for `ros-fs`, network access on a cache miss only.

## Modification boundaries

- Normal: `bin/`, `lib/`, `starter/`, package metadata, and bootstrap tests.
- Escalation required: changes to installed contracts, overwrite policy,
  acquisition/runtime support, profile composition, or the `ros-fs` release
  artifact contract (RID set, checksum format, release tag scheme) that
  `publish.yml` and `lib/ros-fs-launcher.mjs` both depend on.

## Local agent instructions

- `AGENTS.md`, `PACKAGE-USAGE.md`, and `BOOTSTRAP.md`.

## Maintenance

- Owner: repository-governance
- Last checked against implementation: 2026-09-11 (DF-ROS-2026-A033: the
  `greenfield` starter profile no longer scaffolds
  `tools/ros_cli.mjs`/`ros_git.mjs`/`ros_telemetry.mjs`/`ros_persistence.mjs`
  at all -- discovered, while attempting `DF-ROS-2026-A032` Phase 2's
  planned deletion of Node's source, that `tools/ros_server.mjs` still
  imports these modules in-process for the separate, permanently
  out-of-scope web UI feature, making wholesale deletion unsafe. The
  `project-administration` profile keeps them scaffolded, since that
  profile's own `ros_server.mjs`/`ros_hub_cli.mjs`/`ros_hub_server.mjs`
  depend on them; there they are documented purely as that profile's
  internal library dependency, never again as a CLI rollback path. The
  differential test suite's own Node-comparison mechanism was converted
  from a live oracle -- importing Node's functions directly, or spawning
  `node tools/ros_cli.mjs` against a bootstrapped fixture -- to golden-master
  literals captured once from Node's real behavior, so the test suite no
  longer executes Node as a CLI at all.)
- Last checked against implementation: 2026-09-11 (DF-ROS-2026-A032,
  superseding DF-ROS-2026-A031: the scaffolded `./ros` itself is now the
  F# launcher by default, not merely an additive `ros-fs` alongside it.
  A031's own build had already found and reverted this exact redesign once
  as too risky without further work -- doing it safely required first
  fixing every differential/smoke test whose Node-comparison side spawned
  a bootstrapped `./ros` (now F#) to invoke `node tools/ros_cli.mjs`
  directly instead, and pre-seeding a delegating fake binary in the
  launcher's cache for the two tests that need the real launcher mechanism
  itself (a real npm-exec install, and `ros-hub create`'s shell-out to a
  spoke's `./ros`))
- Known gaps: initialization writes related state without a multi-file
  transaction; the root lockfile version is stale relative to `package.json`;
  bootstrap prints validation as a next step but does not execute it.
  `ros-fs`'s cross-platform startup timing (macOS/Windows) is buildability-only,
  not independently measured (`EV-ROS-2026-A048`); `osx-x64` is not built at
  all; a `@main`-tagged snapshot version has no matching GitHub Release, so
  the launcher only works against stable version installs. Node's source
  remains in this repository and in the `project-administration` starter
  profile only, as `DF-ROS-2026-A033` describes; it is not scheduled for
  deletion since `tools/ros_server.mjs`/`ros_hub_cli.mjs` depend on it
  in-process and both are permanently out of this migration's scope.
