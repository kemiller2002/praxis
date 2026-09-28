# Feature Manifest — Bootstrap and distribution

## Purpose

Route changes to native acquisition, profile materialization, installation
attribution and installation-integrity verification. The canonical entry
point is the F# CLI's lifecycle interface: `init`, `status`, `verify`,
`upgrade` and `doctor` (`Ros.Domain.Lifecycle`, `Ros.Application.Lifecycle`,
`Ros.Infrastructure.Lifecycle`), installed as the `praxis` command (with the `ros`
compatibility alias) by the native installers. The npm package, its Node launchers and
`ros-bootstrap` are retired (`DF-ROS-2026-A042`).

## Ownership

- State, including presentation state: `starter/*/manifest.json` (including
  each entry's declared `ownership`/`integration`), `release.json` (version
  and package identity), the embedded scaffold payload, the installed
  `.echelon/ros.json` manifest, the legacy `.ros/installation.json` snapshot,
  and the per-user native installation root (`~/.echelon/tools/praxis/<version>/`,
  override `ECHELON_HOME`).
- Transitions / commands / messages: the F# lifecycle commands
  (`src/Ros.Cli/Lifecycle.fs`); `scripts/install-native.sh`/`.ps1` (download,
  checksum, install, optionally activate); `bin/praxis-native.sh`/`.cmd` (bundle
  launchers); the scaffolded `./praxis` (`starter/greenfield/praxis`, plus
  `praxis.cmd`/`praxis.ps1`, with `ros`/`ros.cmd`/`ros.ps1` as aliases that
  exec it), which only resolves the pinned version, installs it
  side by side (`--no-activate`) when missing, and execs it.
- Invariants and guards: every lifecycle decision is made in F#. The
  installers never activate or run a bundle whose SHA-256 does not match
  `native-checksums.txt`; an unsupported platform fails with a named message.
  The scaffold never contains a Node/JavaScript/TypeScript artifact
  (`RQ-ROS-2026-A022`, `ImplementationLanguagePolicyTests`).
- Capabilities / authority: caller selects profile/target; the manifest is
  authoritative for declared materialization.
- Important effects and effect contracts: filesystem creation/copy/render in
  the lifecycle; the installers and the scaffolded launcher are the only
  network effects (one bundle + checksum download per version install).

## Interfaces

- Inbound: `praxis` (alias `ros`) and the lifecycle commands/options documented in
  `docs/cli.md`; `install-native.sh [--version] [--install-base]
  [--no-activate]`, `install-native.ps1 [-Version] [-InstallBase]
  [-NoActivate]`; a project's `./praxis ARGS` (or `./ros ARGS`).
- Outbound: installed files/directories/modes, installation attribution JSON,
  stdout/stderr and process exit status.

## Tests and verification

- `tests/Ros.Tests/LifecycleTests.fs` (pure ownership, planning, migration,
  diagnosis rules, embedded payload equals the manifests' sources) and the
  end-to-end lifecycle tests in `tests/Ros.Tests` (real CLI against throwaway
  repositories, from a checkout and from the embedded payload alone).
- `.github/workflows/praxis-validation.yml` `lifecycle` job: init/verify/doctor
  on Linux, Windows and macOS.
- `.github/workflows/native-release.yml`: builds every bundle and smoke-tests
  the installer and the installed `praxis`/`ros`/`echelon` commands.

## Dependencies

- Allowed direct dependencies: the .NET BCL in the CLI; POSIX `sh`, `curl`,
  `tar`, `sha256sum`/`shasum` in the POSIX installer and launcher; Windows
  PowerShell in the Windows installer and launcher.
- Required composition context: a writable target; network access only to
  install a version.

## Modification boundaries

- Normal: `src/**/Lifecycle/`, `starter/`, `scripts/install-native.*`,
  `bin/praxis-native.*`, `release.json`, and lifecycle tests.
- Escalation required: changes to installed contracts, overwrite policy,
  acquisition/runtime support, profile composition, or the release artifact
  contract (bundle names, checksum file, tag scheme) that
  `native-release.yml` and the installers share.

## Local agent instructions

- `AGENTS.md`, `PACKAGE-USAGE.md`, and `BOOTSTRAP.md`.

## Maintenance

- Owner: repository-governance
- Last checked against implementation: 2026-09-28 (`DF-ROS-2026-A042`: npm
  distribution, `bin/*.mjs`, `lib/*.mjs` and the Node scaffold launcher
  removed; `release.json` replaces `package.json`; native bundles no longer
  carry a `package/` directory because the binary embeds its payload).
- Known gaps: initialization writes related state without a multi-file
  transaction; upgrading stops managing a retired scaffold file but leaves it
  on disk; cross-platform launcher behaviour is exercised in CI, not locally.
