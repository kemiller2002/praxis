# Portable ROS package

The package ships a frozen, self-contained Repository Operating System
greenfield profile. A target repository does not read from or link to this
source checkout after installation.

## Install from npm

After a release is published under the organization scope:

```bash
cd /path/to/project
npx --yes \
  --package=@echelon-foundry/repository-operating-system@<version> \
  ros-bootstrap init \
  --target .
```

To install the newest continuously published snapshot from `main`:

```bash
npx --yes \
  --package=@echelon-foundry/repository-operating-system@main \
  ros-bootstrap init \
  --target .
```

Every push to the canonical GitHub `main` branch publishes a unique prerelease version such as `1.1.0-main.42.1` and moves the npm `main` dist-tag. Stable releases and the `latest` tag remain deliberate release actions.

A snapshot version never has its own GitHub Release, so it never has a
matching `ros-fs` binary either. `ros-bootstrap init` handles this
automatically (`DF-ROS-2026-A034`): the scaffolded project's `ros.json`
is pinned to the newest **stable** release instead of the exact snapshot
just installed, and `./ros` reports which version it was pinned to and
why. Installing via `@main` gets you the newest scaffolding and
bootstrap-logic fixes; it does not get you a preview of unreleased F#
CLI behavior through `./ros` — building from source is still the way to
do that.

## Run the F# CLI (`ros-fs`) via npm

The package also exposes `ros-fs`, a launcher for the project's F# CLI
(`DF-ROS-2026-A029`), for any project that depends on this package directly
without going through `ros-bootstrap init`:

```bash
npx --package=@echelon-foundry/repository-operating-system@<version> ros-fs status
```

On first use for a given package version and platform, `ros-fs`
(`bin/ros-fs.mjs`) downloads a self-contained, single-file build of the F#
CLI from that version's GitHub Release, verifies its SHA-256 checksum
against the release's `checksums.txt`, and caches it under
`~/.cache/ros-fs/<version>/<platform>/` (override with `ROS_FS_CACHE_DIR`).
Later invocations of the same version on the same machine run entirely from
that cache, with no network access. Supported platforms: linux/x64,
linux/arm64, darwin/x64, darwin/arm64, win32/x64 — an unsupported platform
gets a clear error naming the gap rather than a silent failure.

This launcher only resolves a release for a **stable** version (the one
tagged `vX.Y.Z` on GitHub); `@main`-tagged prerelease snapshots have no
matching release, so the CLI checks the version's shape before making any
network request and fails immediately with a plain, specific error rather
than attempting (and failing) a download. `ros-bootstrap init` itself warns
at install time when it detects it is scaffolding a snapshot version, since
the resulting project's `./ros` cannot work until it is upgraded to a
stable release. Only already-ported, differential-tested commands are
safe to rely on here —
consult `docs/migrations/fsharp/STATUS.md` for current command-surface
parity.

**A project scaffolded by `ros-bootstrap init` gets this same launcher as
its own `./ros`** (`DF-ROS-2026-A032`, superseding `DF-ROS-2026-A031`'s
earlier additive-only `ros-fs`): F# is that project's sole CLI, using the
identical acquire-verify-cache-exec mechanism (`tools/ros_fs_launcher.mjs`,
reading the target version from that project's own `ros.json`). The
`greenfield` profile no longer scaffolds Node's implementation at all
(`DF-ROS-2026-A033`); the `project-administration` profile still includes
`tools/ros_cli.mjs`/`ros_git.mjs`/`ros_telemetry.mjs`/`ros_persistence.mjs`,
but only as the in-process internal dependency of that profile's own web
and hub servers (`ros_server.mjs`, `ros_hub_cli.mjs`) — it is not a CLI and
is not a supported rollback path.

## Install the latest directly from GitHub

Run inside the target repository:

```bash
cd /path/to/project
npx --yes --prefer-online \
  --package=github:kemiller2002/repository-operating-system#main \
  ros-bootstrap init \
  --target .
```

The project display name is derived from the target folder. Use
`--project "Different Display Name"` to override it. `--prefer-online` directs
npm to check the remote instead of preferring cached package data.

## Reproducible GitHub installation

Before an npm release, or when testing a repository tag:

```bash
cd /path/to/project
npx --yes github:kemiller2002/repository-operating-system#<tag> init \
  --target .
```

For an unpublished development commit, replace `v1.0.0` with an exact commit
SHA. A branch name is convenient but not reproducible.

## Preview and safety

```bash
npx --yes github:kemiller2002/repository-operating-system#<commit> init \
  --target . \
  --dry-run
```

Initialization is additive. Existing project-owned `README.md`,
`.gitattributes`, `.gitignore`, and `.editorconfig` files are preserved and
recorded as unmanaged in the installation manifest. An identical managed file
is adopted. Any differing managed destination stops the command before it
writes files. Version 1.0.0 intentionally has no force or merge flag; managed
collisions require an explicit migration.

## Validate the installed project

```bash
./ros registry check
./ros validate
```

Initialization also installs `.github/workflows/ros-validation.yml`. It runs the self-contained, pinned snapshot from the repository and supplies the pull-request or push base revision for committed-change attribution. No network call to the ROS source repository occurs during validation.

To verify an installed snapshot against its recorded checksums, invoke the
current GitHub package:

```bash
npx --yes --prefer-online \
  --package=github:kemiller2002/repository-operating-system#main \
  ros-bootstrap verify \
  --target .
```

Project-owned files will legitimately change during use. Snapshot verification
is a diagnostic, not a rule forbidding project evolution; accepted changes
should be recorded through normal project governance.

## Upgrade procedure

ROS upgrades are explicit and collision-safe:

1. Pin the desired release tag or commit and run `ros-bootstrap init --dry-run` against a clean temporary repository to inspect the new snapshot.
2. Read the release migration report and compare its protocol version with `ros.json`.
3. In the consuming repository, establish an attributed upgrade work item and preserve project-owned configuration.
4. Replace only files recorded as managed in `.ros/installation.json`; resolve differing managed files as an explicit migration rather than using a force flag.
5. Update `.ros/installation.json`, run `ros-bootstrap verify`, `./ros registry check`, and `./ros validate`, then review the diff before commit.

Minor releases may add compatible commands, evidence types, or schema fields. Breaking transition, event, or configuration semantics require a new protocol major version and a repository migration. The initializer intentionally does not perform silent in-place upgrades.

## Publication gate

Before tagging or publishing:

```bash
npm run release:check
npm publish --dry-run --access public
```

Inspect the tarball file list, confirm the version, create an immutable Git tag,
and publish with an npm account authorized for the `@echelon-foundry`
organization scope. The package is distributed under the MIT License; the
tarball must contain `LICENSE`.

The package metadata fixes the publication registry to `https://registry.npmjs.org/`, declares public access, and links releases to the current GitHub source repository. Before the first publish, `npm whoami` must succeed and the authenticated user must have write permission in the `echelon-foundry` npm organization.

## Configure trusted publishing

The main-snapshot workflow uses npm trusted publishing and does not require a long-lived `NPM_TOKEN`. After the first manual package publication, configure the package on npmjs.com with this trusted publisher:

- Provider: GitHub Actions
- GitHub organization or user: `kemiller2002`
- Repository: `repository-operating-system`
- Workflow filename: `publish.yml`
- Allowed action: `npm publish`

The workflow requires GitHub-hosted runners and `id-token: write`. It verifies that an explicit public license is configured before publishing. If the GitHub repository is transferred, update the package repository metadata, workflow repository guard, and npm trusted-publisher configuration together.
