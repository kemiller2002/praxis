# Distribution and release

How Praxis is distributed, which entry points it exposes, and how a release is
published.

**Installing Praxis into a repository is documented elsewhere.** Start at
[`docs/installation.md`](docs/installation.md) for the canonical interface,
[`docs/native-installation.md`](docs/native-installation.md) for installing the
commands, and [`docs/cli.md`](docs/cli.md) for the command reference.

## Entry points

The native installers (`scripts/install-native.sh`,
`scripts/install-native.ps1`, or `echelon install praxis`) install:

| Command | Purpose |
|---|---|
| `praxis` | The canonical lifecycle interface: `init`, `status`, `verify`, `upgrade`, `doctor`, plus every work, telemetry and provenance command. |
| `ros` | Compatibility alias for `praxis`. |
| `echelon` | Echelon toolchain bootstrap and Doctor. |

A project that `init` installed also has its own `./praxis` (POSIX shell, plus
`praxis.cmd`/`praxis.ps1` on Windows), which runs the Praxis version the project
pins and installs that release side by side on first use. Its `./ros`,
`ros.cmd` and `ros.ps1` are compatibility aliases that run the same launcher.

The npm package (`@echelon-foundry/repository-operating-system`, with its
`ros`, `ros-fs` and `ros-bootstrap` executables) is retired
(`DF-ROS-2026-A042`); no new versions are published. The package name remains
the installation identity recorded in `.echelon/ros.json`.

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

## Version source

`release.json`'s `version` is the single authoritative version.
[`Directory.Build.props`](Directory.Build.props) reads it at build time and
sets the CLI assembly's informational version from it, the CLI reports that
back (`praxis --version`), and the release workflow reads it from the CLI. The
CLI version and the release version therefore cannot drift.

## Release

[`.github/workflows/native-release.yml`](.github/workflows/native-release.yml)
is the authoritative release path; a local developer machine is not. On a pull
request touching the release inputs it builds, tests and validates, publishes
every platform binary, assembles the bundles and smoke-tests the installer
against them. On a push to `main` that changes `release.json`, the installers
or the native launchers, it additionally creates (or updates) the `vX.Y.Z`
GitHub Release for `release.json`'s version with the bundles and
`native-checksums.txt`.

Before bumping `release.json`, run the same gate locally:

```bash
dotnet build Ros.slnx --configuration Release
dotnet tests/Ros.Tests/bin/Release/net10.0/Ros.Tests.dll
./praxis architecture check
./praxis registry check
./praxis validate
```

The project is distributed under the MIT License.
