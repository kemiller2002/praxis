# Distribution and release

How Praxis (the Echelon Foundry Repository Operating System) is distributed,
which entry points it exposes, and how a release is published.

**Installing Praxis into a repository is documented elsewhere.** Start at
[`docs/installation.md`](docs/installation.md) for the canonical interface,
[`docs/native-installation.md`](docs/native-installation.md) for installing the
commands, and [`docs/cli.md`](docs/cli.md) for the command reference.

Praxis is distributed two ways (`DF-ROS-2026-A044`). **npm is no longer a
distribution channel:** the repository has no `package.json` at all
(`DF-ROS-2026-A049`), and nothing is published to npm. Versions of
`@echelon-foundry/repository-operating-system` already on npm stay
installable but receive no updates.

## Channels

| Channel | For | Install | Built by |
|---|---|---|---|
| Native bundle (GitHub Releases) | any machine, CI, cloud agents; no Node.js or .NET needed | `scripts/install-native.sh` / `install-native.ps1`, `echelon install praxis`, or `scripts/praxis-bootstrap.sh` (attestation-verified, used by `praxis-remote.yml`) | `native-release.yml` |
| .NET global tool (NuGet) | developers with .NET 10 | `dotnet tool install -g EchelonFoundry.Praxis` | `native-release.yml` |
| `ros-fs-<platform>` binaries (GitHub Releases) | the legacy `./ros` launcher of a project scaffolded before the Praxis rename, which downloads the one for its pinned version | automatic, on first use | `ros-fs-assets.yml`, called by `native-release.yml` |

All three run the same F# CLI (`src/Praxis.Cli`), and each carries the scaffold
it installs compiled into the assembly, so none of them needs anything else at
run time. The legacy `ros-fs-<platform>` binaries are the same `praxis` binary
shipped under the legacy name so those projects keep working
(`DF-ROS-2026-A050`).

Why several native builds when the code is .NET: the IL is portable, but a
build that needs no installed runtime embeds the .NET runtime, which is native
code for one operating system and CPU. Each release therefore carries one
self-contained build per platform (`linux-x64`, `linux-musl-x64` for the
bundle, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`). The .NET tool is
the single portable package for machines that already have .NET 10.

## Entry points

The native installers (`scripts/install-native.sh`,
`scripts/install-native.ps1`, or `echelon install praxis`) install:

| Command | Purpose |
|---|---|
| `praxis` | The canonical lifecycle interface: `init`, `status`, `verify`, `upgrade`, `doctor`, plus every work, telemetry and provenance command. |
| `ros` | Compatibility alias for `praxis`. |
| `echelon` | Echelon toolchain bootstrap and Doctor. |

The .NET global tool installs the `praxis` command.

A project that `init` installed also has its own `./praxis` (POSIX shell, plus
`praxis.cmd`/`praxis.ps1` on Windows), which runs the Praxis version the project
pins and installs that release side by side on first use. Its `./ros`,
`ros.cmd` and `ros.ps1` are compatibility aliases that run the same launcher.

The npm package (`@echelon-foundry/repository-operating-system`, with its
`ros`, `ros-fs` and `ros-bootstrap` executables) is retired
(`DF-ROS-2026-A044`, `DF-ROS-2026-A049`); no new versions are published. The
package name remains the installation identity recorded in `.echelon/ros.json`.

## Distribution model

```
install-native.sh / install-native.ps1   (or a project's ./praxis on first use)
    |  downloads praxis-<platform> bundle + native-checksums.txt, verifies SHA-256
    v
~/.echelon/tools/praxis/<version>/praxis-bin   (self-contained F# binary)
    |
    v
F# domain and application core
```

No Node.js, npm or .NET installation is needed on the consuming machine. The
binary carries the scaffold it installs, compiled in, so a repository's own
`./praxis` can run `init` and `upgrade` with no package on disk and no network.
See [Where the scaffold comes from](docs/installation.md#where-the-scaffold-comes-from).

Bundles: `linux-x64`, `linux-musl-x64`, `linux-arm64`, `osx-x64`,
`osx-arm64`, `win-x64`. An unsupported platform gets a clear error naming the
gap rather than a silent failure.

## Versions

`release.json`'s `version` is the single authoritative version.
[`Directory.Build.props`](Directory.Build.props) reads it at build time and
sets the CLI assembly's informational version from it, the CLI reports that
back (`praxis --version`), and the release workflow reads it from the CLI. The
CLI version and the release version therefore cannot drift.

Only stable `X.Y.Z` versions are released. There are no snapshot releases:
the former npm `main` dist-tag snapshots are retired with npm. A scaffolded
project pins its version in `ros.json`, and no release (and no binary) exists
for a non-stable pin, so its `./praxis` launcher cannot install one. Pin a
released version, or build from source in a checkout to try unreleased
behaviour.

Released assets are immutable. A workflow never replaces an asset a release
already has; publishing a different build requires a new version.

## Cutting a release

[`.github/workflows/native-release.yml`](.github/workflows/native-release.yml)
is the authoritative release path; a local developer machine is not. Run the
**Release** workflow ([`.github/workflows/release.yml`](.github/workflows/release.yml))
from the Actions tab on `main` with `patch`, `minor`, `major` or an exact
`X.Y.Z`. It:

1. runs [`scripts/praxis-release-bump.sh`](scripts/praxis-release-bump.sh),
   which begins work item `RELEASE-X-Y-Z` under the GitHub Actions identity,
   bumps `release.json`, commits and pushes, records a durable checkpoint,
   completes the work item, and pushes the Praxis state;
2. dispatches `native-release.yml`, since a push made with `GITHUB_TOKEN`
   does not start push-triggered workflows.

On a pull request touching the release inputs, `native-release.yml` builds,
tests and validates, publishes every platform binary, assembles the bundles
and smoke-tests the installer against them. On `main` (a push that changes
`release.json`, the installers or the native launchers, or the dispatch
above) it runs the full test suite and validation, and for a version that is
not yet released:

1. builds, smoke-tests, attests and uploads the native bundles and
   `native-checksums.txt` to the `vX.Y.Z` GitHub Release;
2. packs and smoke-tests the .NET tool (this also runs on every pull request),
   and pushes it to NuGet when NuGet publishing is configured (below);
3. calls `ros-fs-assets.yml`, which builds, attests and uploads the five
   legacy `ros-fs-<platform>` binaries and `checksums.txt`.

The script refuses, before changing anything, a dirty tree, a branch that is
not at its upstream head, and a version that is malformed or not newer.

Before releasing, run the same gate locally:

```bash
dotnet build Praxis.slnx --configuration Release
dotnet tests/Praxis.Tests/bin/Release/net10.0/Praxis.Tests.dll
./praxis architecture check
./praxis registry check
./praxis validate
```

### Backfilling a release's `ros-fs` binaries

v3.3.0 through v3.6.0 were released while npm publishing (which used to build
the `ros-fs` binaries) was switched off, so projects pinned to them could not
download their CLI. To add the binaries to such a release, run the **ros-fs
release assets** workflow (`ros-fs-assets.yml`) with its tag, for example
`v3.6.0`. It builds from that tag, checks that the tag's declared version
(`release.json`, or `package.json` for a tag from before the rename) matches,
and does nothing if the release already has `checksums.txt`.

## Configure NuGet trusted publishing

The .NET tool is pushed with NuGet trusted publishing: `NuGet/login`
exchanges the workflow run's OIDC token for a short-lived API key, so no
long-lived key is stored. To turn it on:

1. On nuget.org, sign in as the account that owns the `EchelonFoundry.*`
   packages and add a trusted-publishing policy for repository
   `kemiller2002/praxis`, workflow file `native-release.yml`.
2. In the repository's Actions variables, set `NUGET_USER` to that nuget.org
   account name and `NUGET_PUBLISH_ENABLED` to `true`.

Until both are set, releases still build and smoke-test the tool but do not
push it. A version already on NuGet is skipped.

## Payload contents

There is no separate payload package. The starter scaffold, templates,
schemas, telemetry configuration and the agent documents are compiled into
the CLI assembly, so the native bundle, the .NET tool and the legacy `ros-fs`
binaries all carry the same payload. The lifecycle tests in
`tests/Praxis.Tests` (for example `LifecycleCliTests.fs`) exercise the documented
lifecycle commands against throwaway repositories.

## Legacy compatibility

- A repository installed by the legacy npm `ros-bootstrap init` keeps
  working untouched. `praxis status` reports it as `upgrade-required`, and
  `praxis upgrade` adopts it.
- A scaffolded project's installation manifest (`.echelon/ros.json`) still
  records `@echelon-foundry/repository-operating-system` as its package
  identity. That is the product's installation identity, kept for
  compatibility; it does not mean the project uses npm.
- Projects scaffolded before the Praxis rename keep their `./ros` launcher,
  which downloads `ros-fs` binaries from GitHub Releases, not from npm. A
  newer project's `./ros` is a compatibility alias of its `./praxis`.

The project is distributed under the MIT License.
