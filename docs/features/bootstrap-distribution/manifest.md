# Feature Manifest — Bootstrap and distribution

## Purpose

Route changes to npm acquisition, profile materialization, installation
attribution, installation-integrity verification, and (since
`DF-ROS-2026-A029`/`DF-ROS-2026-A031`) on-demand acquisition of the
compiled F# CLI binary, both for this package's own `ros-fs` and for a
`ros-fs` scaffolded into bootstrapped projects.

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
  authoritative for declared package materialization. `ros-fs` is additive
  and optional: it never changes what `ros-bootstrap init` scaffolds, and
  `./ros` (Node) remains the only thing that scaffolded project runs.
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
- Last checked against implementation: 2026-09-11 (DF-ROS-2026-A031: added
  an additive `ros-fs` launcher to the greenfield starter template; a
  near-miss during this change found and reverted an earlier design that
  would have redirected the scaffolded `ros` itself, which would have
  invalidated this whole migration's differential-test methodology --
  every such test spawns a bootstrapped project's `ros` as its Node
  baseline)
- Known gaps: initialization writes related state without a multi-file
  transaction; the root lockfile version is stale relative to `package.json`;
  bootstrap prints validation as a next step but does not execute it.
  `ros-fs`'s cross-platform startup timing (macOS/Windows) is buildability-only,
  not independently measured (`EV-ROS-2026-A048`); `osx-x64` is not built at
  all; a `@main`-tagged snapshot version has no matching GitHub Release, so
  `ros-fs` only works against stable version installs.
