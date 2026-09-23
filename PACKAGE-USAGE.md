# Package distribution and release

How this package is distributed, which entry points it exposes, and how a
release is published.

**Installing ROS into a repository is documented elsewhere.** Start at
[`docs/installation.md`](docs/installation.md) for the canonical interface, and
[`docs/cli.md`](docs/cli.md) for the command reference. This file covers the
package itself.

## Entry points

| Executable | Status | Purpose |
|---|---|---|
| `ros` | **Recommended** | The canonical lifecycle interface: `init`, `status`, `verify`, `upgrade`, `doctor`. |
| `ros-fs` | Supported | Runs the F# CLI directly for a project that depends on this package without installing a scaffold. |
| `ros-bootstrap` | Legacy compatibility | The original `init`/`verify` installer. Still published and unchanged; superseded by `ros`. |

```bash
npx --package=@echelon-foundry/repository-operating-system ros init
npx --package=@echelon-foundry/repository-operating-system@<version> ros status
```

## Distribution model

```
npm / npx
    |
    v
tiny Node bootstrap (bin/ros.mjs -> lib/lifecycle-launcher.mjs)
    |
    v
self-contained F# binary, fetched from the version's GitHub Release
    |
    v
F# domain and application core
```

The launcher detects the platform, resolves the right binary, downloads it once
per version and platform, verifies its SHA-256 against the release's
`checksums.txt`, caches it under `~/.cache/ros-fs/<version>/<platform>/`
(override with `ROS_FS_CACHE_DIR`), and executes it. Later runs of the same
version work entirely offline. A corrupt or tampered download is never
executed.

Inside a source checkout, or in CI after `npm run build:fsharp`, the launcher
runs the freshly built assembly instead of downloading anything;
`ROS_FS_DLL_PATH_OVERRIDE` points it at a specific build.

The binary carries the scaffold it installs, compiled in, so it needs nothing
else from the package at run time: a repository's own `./ros` can run `init` and
`upgrade` with no npm package on disk and no network. See
[Where the scaffold comes from](docs/installation.md#where-the-scaffold-comes-from).

Supported platforms: `linux/x64`, `linux/arm64`, `darwin/x64`, `darwin/arm64`,
`win32/x64`. An unsupported platform gets a clear error naming the gap rather
than a silent failure.

### Snapshot versions

Every push to the canonical `main` branch publishes a unique prerelease such as
`3.0.0-main.42.1` and moves the npm `main` dist-tag. Stable releases and the
`latest` tag remain deliberate release actions.

A snapshot version never has its own GitHub Release, so it never has a matching
binary. Two things follow:

- `ros`/`ros-fs` invoked from a snapshot install checks the version's shape
  before making any network request and fails immediately with a specific
  error, rather than attempting a download that cannot succeed.
- `ros init` and `ros-bootstrap init` pin a scaffolded project's `ros.json` to
  the newest **stable** release rather than the exact snapshot installed
  (`DF-ROS-2026-A034`, via `lib/stable-ros-version.json`, which `publish.yml`
  bundles into each tarball).

Installing via `@main` gets you the newest scaffolding and bootstrap fixes; it
does not give you a preview of unreleased CLI behaviour. Building from source
is still the way to do that.

## Package contents

`package.json`'s `files` field is an explicit allow-list; there is no
`.npmignore`, so publish behaviour has exactly one definition. The tarball
carries the Node launchers, the scaffold for every profile, the governance
documents, schemas, templates, the web and hub assets, the README and the
licence. It does not carry `src/`, `tests/`, build output, or local
configuration.

Review it before releasing:

```bash
npm run pack:inspect      # npm pack --dry-run
npm pack                  # then inspect the real archive
tar -tzf echelon-foundry-repository-operating-system-*.tgz
```

`tests/lifecycle-package.test.mjs` asserts the contents of the real tarball and
then exercises every documented command against it, so a packaging mistake
fails the test suite rather than reaching the registry.

## Version source

`package.json`'s `version` is the single authoritative version.
[`Directory.Build.props`](Directory.Build.props) reads it at build time and
sets the CLI assembly's informational version from it, and the CLI reports that
back. The CLI version and the npm release version therefore cannot drift.

## Publication gate

```bash
npm run release:check
npm publish --dry-run --access public
```

`release:check` runs the full test suite (including the packed-artifact tests),
`npm pack --dry-run`, `./ros registry check` and `./ros validate`.

Then inspect the tarball file list, confirm the version, create an immutable Git
tag, and publish with an npm account authorized for the `@echelon-foundry`
organization scope. The package is distributed under the MIT License; the
tarball must contain `LICENSE`.

Package metadata fixes the publication registry to
`https://registry.npmjs.org/`, declares public access, and links releases to the
current GitHub source repository. Before the first publish, `npm whoami` must
succeed and the authenticated user must have write permission in the
`echelon-foundry` npm organization.

## Automated release

[`.github/workflows/publish.yml`](.github/workflows/publish.yml) is the
authoritative release path; a local developer machine is not. On every push to
`main` it builds F#, runs the full suite, validates the repository, packs and
exercises the artifact, and publishes a `main` snapshot. When the committed
`package.json` version has changed it additionally:

1. builds self-contained binaries for all five supported platforms,
2. writes `checksums.txt`,
3. publishes the stable npm release,
4. creates the matching `vX.Y.Z` GitHub Release carrying those binaries.

The binaries are fetched automatically by the launcher; they are not intended
for manual download.

## Configure trusted publishing

The workflow uses npm trusted publishing and does not require a long-lived
`NPM_TOKEN`. After the first manual publication, configure the package on
npmjs.com with this trusted publisher:

- Provider: GitHub Actions
- GitHub organization or user: `kemiller2002`
- Repository: `praxis`
- Workflow filename: `publish.yml`
- Allowed action: `npm publish`

The workflow requires GitHub-hosted runners and `id-token: write`, and verifies
that an explicit public license is configured before publishing. If the GitHub
repository is transferred, update the package repository metadata, the workflow
repository guard, and the npm trusted-publisher configuration together.

## Legacy compatibility

`ros-bootstrap` is retained unchanged for existing users:

```bash
npx --package=@echelon-foundry/repository-operating-system ros-bootstrap init --target .
npx --package=@echelon-foundry/repository-operating-system ros-bootstrap verify --target .
```

Differences from `ros`, all of them reasons to prefer `ros`:

- `ros-bootstrap init` is not idempotent: it aborts when
  `.ros/installation.json` already exists.
- It has no ownership model beyond `preserve-existing`, no installation-state
  model, no migrations, no `doctor`, no `--json`, and no `--check`.
- `ros-bootstrap verify` compares every managed file against the installed
  snapshot, so it reports drift in files a project is expected to edit. It is a
  diagnostic, not a rule forbidding project evolution; `ros verify` classifies
  by ownership instead and does not report those.

`ros upgrade` migrates a `ros-bootstrap` installation to the current model and
leaves `.ros/installation.json` in place, so both executables keep working
against the same repository. See [`docs/upgrading.md`](docs/upgrading.md).

Installing from GitHub rather than npm also still works, for a commit that has
no published release:

```bash
npx --yes --prefer-online \
  --package=github:kemiller2002/praxis#<commit> \
  ros-bootstrap init --target .
```

A branch name is convenient but not reproducible; prefer a tag or an exact
commit SHA.


## Repository rename and npm publishing

The source repository is now `kemiller2002/praxis`. npm trusted publishing validates the repository identity in GitHub's OIDC claim, so the npm package's Trusted Publisher configuration must name `kemiller2002/praxis` and `.github/workflows/publish.yml`.

Until that external npm setting is updated, the npm publish job is intentionally gated by the GitHub repository variable `NPM_PUBLISH_ENABLED`. Leave it unset or false while native GitHub Release distribution is being adopted. After the npm Trusted Publisher entry is updated, set `NPM_PUBLISH_ENABLED=true` to resume compatibility snapshot and stable npm publication.
