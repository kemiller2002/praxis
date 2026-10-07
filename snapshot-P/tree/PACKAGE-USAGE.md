# Distribution and release

Praxis (the Echelon Foundry Repository Operating System) is distributed two
ways (`DF-ROS-2026-A044`). **npm is no longer a distribution channel:**
`package.json` is private and never published. Versions of
`@echelon-foundry/repository-operating-system` already on npm stay
installable but receive no updates.

## Channels

| Channel | For | Install | Built by |
|---|---|---|---|
| Native bundle (GitHub Releases) | any machine, CI, cloud agents; no Node.js or .NET needed | `scripts/install-native.sh` / `install-native.ps1`, or `scripts/praxis-bootstrap.sh` (attestation-verified, used by `praxis-remote.yml`) | `native-release.yml` |
| .NET global tool (NuGet) | developers with .NET 10 | `dotnet tool install -g EchelonFoundry.Praxis` | `native-release.yml` |
| `ros-fs-<platform>` binaries (GitHub Releases) | a scaffolded project's `./ros` launcher, which downloads the one for its pinned version | automatic, on first use | `ros-fs-assets.yml`, called by `native-release.yml` |

All three run the same F# CLI (`src/Ros.Cli`). The native bundle's `praxis`
wrapper passes the payload it carries with `--package-root`; the .NET tool and
the `ros-fs` binaries use the scaffold compiled into the assembly. None of
them needs anything else at run time.

Why several native builds when the code is .NET: the IL in `ros-fs.dll` is
portable, but a build that needs no installed runtime embeds the .NET runtime,
which is native code for one operating system and CPU. Each release therefore
carries one self-contained build per platform (`linux-x64`, `linux-musl-x64`
for the bundle, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`). The .NET
tool is the single portable package for machines that already have .NET 10.

## Versions

`package.json`'s `version` is the single authoritative version source: the F#
build reads it (see [`Directory.Build.props`](Directory.Build.props)), so
`praxis --version` can never drift from the released version.

Only stable `X.Y.Z` versions are released. There are no snapshot releases:
the former npm `main` dist-tag snapshots are retired with npm. A scaffolded
project pins its version in `ros.json`, and its `./ros` refuses a non-stable
pin before making any network request, because no release (and no binary)
exists for it. Pin a released version, or build from source in a checkout to
try unreleased behaviour.

Released assets are immutable. A workflow never replaces an asset a release
already has; publishing a different build requires a new version.

## Cutting a release

Run the **Release** workflow
([`.github/workflows/release.yml`](.github/workflows/release.yml)) from the
Actions tab on `main` with `patch`, `minor`, `major` or an exact `X.Y.Z`. It:

1. runs [`scripts/praxis-release-bump.sh`](scripts/praxis-release-bump.sh),
   which begins work item `RELEASE-X-Y-Z` under the GitHub Actions identity,
   bumps `package.json` and `package-lock.json`, commits and pushes, records a
   durable checkpoint, completes the work item, and pushes the Praxis state;
2. dispatches `native-release.yml`, since a push made with `GITHUB_TOKEN`
   does not start push-triggered workflows.

`native-release.yml` then runs the full test suite and validation, and for a
version that is not yet released:

1. builds, smoke-tests, attests and uploads the native bundles and
   `native-checksums.txt`;
2. packs and smoke-tests the .NET tool (this also runs on every pull request),
   and pushes it to NuGet when NuGet publishing is configured (below);
3. calls `ros-fs-assets.yml`, which builds, attests and uploads the five
   `ros-fs-<platform>` binaries and `checksums.txt`.

The script refuses, before changing anything, a dirty tree, a branch that is
not at its upstream head, and a version that is malformed or not newer.

Before releasing, locally:

```bash
npm run release:check
```

### Backfilling a release's `ros-fs` binaries

v3.3.0 through v3.6.0 were released while npm publishing (which used to build
the `ros-fs` binaries) was switched off, so projects pinned to them could not
download their CLI. To add the binaries to such a release, run the **ros-fs
release assets** workflow (`ros-fs-assets.yml`) with its tag, for example
`v3.6.0`. It builds from that tag, checks the tag's `package.json` version
matches, and does nothing if the release already has `checksums.txt`.

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

`package.json`'s `files` list still defines the payload the native bundle
carries: `native-release.yml` assembles it with `npm pack --ignore-scripts`
and extracts it into each bundle. It includes the starter scaffold, templates,
schemas, telemetry configuration and the agent documents. `npm run
pack:inspect` prints it. `tests/lifecycle-package.test.mjs` packs and extracts
that payload and runs every documented lifecycle command against throwaway
repositories.

## Legacy compatibility

- A repository installed by the legacy npm `ros-bootstrap init` keeps
  working untouched. `praxis status` reports it as `upgrade-required`, and
  `praxis upgrade` adopts it.
- A scaffolded project's installation manifest (`.echelon/ros.json`) still
  records `@echelon-foundry/repository-operating-system` as its package
  identity. That is the product's installation identity, kept for
  compatibility; it does not mean the project uses npm.
- Existing projects keep their `./ros` launcher, which downloads `ros-fs`
  binaries from GitHub Releases, not from npm.
