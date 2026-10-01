# Praxis

Praxis is Echelon Foundry's repository operating system. Its package identity, kept for compatibility with existing installations, is `@echelon-foundry/repository-operating-system`.

Repository initialization, verification, diagnostics, and upgrade tooling that
makes research, engineering, decisions, and handoffs durable without relying on
conversation history or tribal knowledge.

Distributed under the [MIT License](LICENSE).

The cross-cutting Aegis, Forma, and Folio application requirements are in [`requirements/SHARED-APPLICATION-FOUNDATIONS.md`](requirements/SHARED-APPLICATION-FOUNDATIONS.md) and apply to native runtime, web UI, and future printable/report surfaces as specified there.

## What it provides

Installing Praxis into a repository gives it a governed operating environment:

- a **work protocol** with legal `begin`/`block`/`resume`/`complete`
  transitions, configurable completion evidence, and durable attribution
  events;
- **canonical research records** — journals, execution packages, theories,
  evidence — with lifecycle, supersession, identifier and confidence rules,
  and generated registries projected from them;
- **adaptive execution telemetry** that distinguishes unavailable from zero and
  observed from derived;
- **agent identity and provenance**:
  - every execution, work event, and captured item records an explicit
    agent, human, or automation actor;
  - requirements and other canonical records accumulate an append-only,
    execution-keyed contributor history, with lineage kept separate from
    authorship;
  - validation enforces this for new work without rewriting legacy history
    (see [`docs/agent-provenance.md`](docs/agent-provenance.md));
- **validation** that fails on malformed front matter, invalid or duplicate
  IDs, broken references, nonreciprocal supersession, and stale registries;
- a **CI workflow** that runs all of the above.

## Quick start

The preferred installation path is now the native GitHub Release distribution:

```bash
curl -fsSL https://raw.githubusercontent.com/kemiller2002/praxis/main/scripts/install-native.sh | sh

praxis init
praxis status
praxis verify
```

The native bundle is self-contained. A consuming machine does not need Node.js,
npm, or a machine-wide .NET runtime. With .NET 10 installed, the same CLI is
also a global tool: `dotnet tool install -g EchelonFoundry.Praxis`. The
established `ros` command remains a compatibility alias of `praxis`. npm is no
longer a distribution channel: the npm package was retired (`DF-ROS-2026-A044`,
`DF-ROS-2026-A049`).

To install the Echelon engineering toolchain, including Ordo:

```bash
echelon setup
```

Once installed, the repository can also run its own lifecycle through the
`./praxis` launcher `init` leaves behind (older installations may also have
`./ros`, a compatibility alias of `./praxis`):

```bash
./praxis verify   # is the installation intact?
./praxis doctor   # if not: what is wrong, and the command that fixes it
./praxis init     # heal: restore anything tool-owned that is missing
./praxis upgrade  # update to this CLI's version
```

## New in 3.4.0

- **Agent-readable Doctor:** `echelon doctor --json` emits a versioned schema with health, commands, native tools, repository components, npm packages, findings, and remediation.
- **Full inventory:** `echelon inventory` distinguishes native Echelon tools, repository-installed lifecycle components, and physically installed `@echelon-foundry/*` npm packages.
- **Stable finding codes:** agents can react to `ECHELON-DOC-xxx` codes instead of scraping diagnostic prose.
- **Stale alias detection:** `ordo`/`sde` and `praxis`/`ros` must report the version that is actually active.
- **Published schemas:** Doctor and inventory JSON schema v1 ship under `schemas/`.
- **Opt-in release awareness:** `echelon doctor --updates` compares active Ordo/Praxis versions with latest stable releases without making network access part of normal Doctor health.

## New in 3.3.0

- **Expanded Echelon Doctor:** `echelon doctor` now reports machine/install health, active and side-by-side tool versions, command aliases, repository toolchain requirements, and repository-level Ordo/Praxis validation.
- **Safe mechanical repair:** `echelon doctor --fix` can restore missing command wrappers or activate/reinstall an exact pinned/active Ordo or Praxis version without rewriting repository state.
- **Verbose diagnostics:** `echelon doctor --verbose` exposes the effective Echelon paths and activation targets for debugging.
- **Future tool discovery:** Doctor lists additional directories under the Echelon tools root without pretending to validate capabilities it does not yet understand.
- **Meaningful exit status:** warning-only environments remain exit 0; broken toolchain or repository invariants return exit 1.

## New in 3.2.0

- **Praxis native distribution:** self-contained GitHub Release bundles for macOS, Linux, and Windows; no Node.js, npm, or machine-wide .NET runtime is required.
- **Echelon bootstrap:** `echelon setup`, `install`, `upgrade`, and `doctor` manage the native Ordo/Praxis toolchain.
- **Repository toolchain pinning:** `.echelon/toolchain.json` can require exact Ordo and Praxis releases.
- **Compatibility aliases:** `praxis` is the preferred native command while `ros` remains supported.
- **Immutable installs:** native versions live side-by-side and activation changes without rewriting an existing version directory.

## New in 3.1.4

- **Legacy research-package compatibility:** established `RP-...-YYYY-NNN` and `REP-...-YYYY-NNN` package identifiers remain valid, including their historical exact `ID.md` filenames. New artifacts should still use the current canonical identifier format. The compatibility rule is intentionally limited to research packages and does not widen evidence, hypothesis, theory, experiment, decision, concept, glossary, or mission IDs.

## New in 3.1.3

- **Launcher version authority:** installed repositories now launch the version recorded in `.echelon/ros.json`; `ros.json.rosVersion` is only the legacy fallback. An upgrade therefore cannot leave a current installation using an obsolete or snapshot launcher pin.
- **Portable distributed documentation:** consumer copies of `docs/work-protocol.md` no longer contain source-repository-relative links that break after installation.
- **Customizable validation integration:** `.github/workflows/ros-validation.yml` is now seeded as shared integration state, so repositories can wrap or strengthen ROS validation without blocking later upgrades.

## New in 3.1.2

- **Real legacy adoption:** `ros upgrade` now reads the file hashes and profile recorded in `.ros/installation.json`, so repositories installed by older ROS releases can be adopted safely. Untouched legacy tool-owned files can advance, local edits still block, and the original installation profile is preserved.

## New in 3.1.1

- **Cross-capability `AGENTS.md` ownership:** ROS now seeds `AGENTS.md` as a shared file so sibling Echelon capabilities such as Visual Engineering and Communication Engineering can maintain their own marked regions without making the ROS installation invalid. Running `ros init` with 3.1.1 preserves the current file bytes and updates the installation record to shared ownership.

## New in 3.1

- **Executable Ordo observation:** ingest versioned `ordo.resolution-observation` v2 records without taking over Ordo's semantic authority.
- **Retrospective outcomes:** record separate semantic and operational assessments with evidence, method, time, and limitations.
- **Effective-current projections:** derive current repository views from immutable observation history instead of rewriting prior records.
- **Scoped negative knowledge:** retain search target, scope, method, state, coverage, exclusions, errors, and `searched-not-found` without claiming global absence.
- **Unknown-effect safety:** preserve `unknown` external effects and reconciliation/retry facts without collapsing uncertainty into failure.
- **Revision-bound handoff:** emit structured facts, assumptions, unknowns, obligations, legal-next-action descriptions, and supersession state.
- Versioned schemas and usage are documented in [Ordo Observation and Handoff](docs/ordo-observation.md).

## New in 3.0

- The **standard lifecycle interface** — `init`, `status`, `verify`, `upgrade`,
  `doctor` — as the `ros` executable, implemented in F#.
- **Self-contained:** the CLI carries the scaffold it installs, so a repository
  can heal and upgrade itself with no package on disk and no network.
- An explicit **file-ownership model** (tool-owned, generated, user-owned,
  shared) that decides what may be replaced and what is never touched.
- A versioned **installation manifest** at `.echelon/ros.json`, and **sequential
  migrations** rather than delete-and-recopy upgrades.
- **Machine-readable output** (`--json`) and a documented exit-code contract for
  CI and agents.

A repository installed by the retired `ros-bootstrap` keeps working, and
`praxis upgrade` adopts it. See [Compatibility](#compatibility).

## Commands

| Command | Writes? | Purpose |
|---|---|---|
| `praxis init` | yes | Bring the repository into a valid installed state. Idempotent. |
| `praxis status` | no | Report installation, validation and work state. |
| `praxis verify` | no | Check that the capability is correctly installed. |
| `praxis upgrade` | yes | Migrate an existing installation to this CLI's version. |
| `praxis doctor` | no | Diagnose problems and explain how to fix them. |

Plus `praxis --help` (and `praxis <command> --help`) and `praxis --version`.

Full reference, including every option, the JSON schemas and the exit-code
contract: [`docs/cli.md`](docs/cli.md).

### `init` is safe to repeat

`init` means *bring this repository into a valid installed state* — not "copy
some files". It inspects the repository, determines the desired state,
calculates and validates the transition, executes it, then verifies the result.

Running it again against an unchanged repository plans **zero** changes and
writes nothing. It never overwrites a file you own, and a tool-owned file you
edited locally stops the command rather than being discarded.

See [`docs/installation.md`](docs/installation.md) for exactly what it may
create, what it will not overwrite, and what happens on a conflict.

### Dry run and check

```bash
praxis init --dry-run    # calculate and report the whole plan; change nothing
praxis init --check      # change nothing; exit 3 if any change would be needed
praxis upgrade --dry-run --json
```

### Machine-readable mode

```bash
praxis status --json
praxis verify --json
praxis doctor --json
praxis init --dry-run --json
praxis upgrade --dry-run --json
```

With `--json`, stdout carries one JSON document and nothing else; diagnostics
go to stderr. Schemas are in [`docs/cli.md`](docs/cli.md#machine-readable-output).

### CI usage

```bash
praxis verify --strict
```

Install `praxis` in the job first (the native bundle needs no runtime, see
[`docs/installation.md`](docs/installation.md)), or run the repository's own
`./praxis verify --strict`. Exit `0` means valid, `3` means verification failed. Other nonzero codes mean
something else — see the [exit-code contract](docs/cli.md#exit-codes) — and
should not be read as "verification failed".

### Agent usage

Every command is non-interactive and never prompts, so nothing can hang waiting
for input. There is no force flag: an operation that would destroy a local
change reports the conflict instead of assuming approval. Use `--dry-run
--json` to see a full plan before acting, and branch on the exit code rather
than on human-readable text. See [`docs/cli.md`](docs/cli.md#agent-usage).

## Where things live

| Path | What it is |
|---|---|
| `ros.json` | The repository's configuration. Yours to edit. |
| `.echelon/ros.json` | The installation manifest: what is installed, at which version, and which artifacts it manages. |
| `.ros/` | Work context, events, backlog and telemetry. |
| `./praxis` | The repository's own launcher (`./ros` is a compatibility alias). Current installs use `.echelon/ros.json.installedVersion`; legacy installs fall back to `ros.json.rosVersion`. |

`.echelon/` is the shared Echelon Foundry root. Each tool owns its own manifest
there and they coexist cleanly.

## File ownership

Every managed file is classified, and the classification decides what the tool
may do to it:

| Ownership | Meaning |
|---|---|
| **tool-owned** | Controlled by the tool; replaced on upgrade when unmodified, blocks when edited locally. |
| **generated** | Derived from the repository's own artifacts; seeded once, then owned by `praxis registry build`. |
| **user-owned** | Yours. Seeded once if absent, never rewritten. |
| **shared** | Seeded by the tool, then yours. Only a declared migration changes it. |

Details, and how ownership is declared: [`docs/installation.md`](docs/installation.md#file-ownership).

## Upgrade policy

Upgrades are an ordered chain of declared configuration-version migrations
(`0 -> 1 -> 2`), never one arbitrary jump and never a delete-and-recopy. The
whole plan is validated before anything is written; a failed precondition stops
the chain before the first change. User-owned, shared and generated files are
preserved. See [`docs/upgrading.md`](docs/upgrading.md) for supported paths,
failure behaviour, and exactly which guarantees the tests prove.

## Compatibility

Adding a JSON field, command or option is not a breaking change. Removing a
field, changing what one means, or changing an exit code is, and requires a
version bump and a migration step.

**Legacy compatibility.** The older `ros-bootstrap init` and
`ros-bootstrap verify` executables were npm-only and are retired
(`DF-ROS-2026-A044`, `DF-ROS-2026-A049`); versions already on npm stay
installable but receive no updates. Use `praxis init` and `praxis verify` for new work. A repository
installed by `ros-bootstrap` keeps working untouched; `praxis status` reports it
as `upgrade-required`, and `praxis upgrade` adopts the manifest while leaving the
legacy snapshot in place.

## Supported platforms

`linux/x64`, `linux/arm64`, `darwin/x64`, `darwin/arm64`, `win32/x64`.

The CLI ships as a self-contained single-file binary per platform, installed
and checksum-verified by `scripts/install-native.sh` or
`scripts/install-native.ps1` (or `echelon install praxis`). No Node.js, npm or
.NET installation is required. The native bundle is additionally built for
`linux-musl/x64` (Alpine). The .NET global tool runs wherever .NET 10 does.

A project's own `./praxis` runs the version the project pins, installing that
release side by side on first use (under `~/.echelon/tools/praxis/<version>/`,
or `$ECHELON_HOME`) without changing which version your global commands run.
A project scaffolded before the Praxis rename keeps its legacy `./ros`, a small
Node.js 20+ launcher that needs no .NET: it fetches the self-contained
`ros-fs-<platform>` binary for its pinned version from that GitHub Release
(these legacy assets continue to ship with every release), verifies its
checksum on first use, caches it under `~/.cache/ros-fs/<version>/<platform>/`
(override with `ROS_FS_CACHE_DIR`) and runs offline thereafter. An unsupported
platform fails with a message naming the gap.

## How it is built

```
native bundle (praxis wrapper) | .NET global tool | project ./praxis launcher (or the ros / ./ros aliases)
    |
    v
F# CLI (src/Ros.Cli)
    |
    v
F# domain and application core (src/Ros.Domain, src/Ros.Application)
```

Each entry point only locates the CLI, forwards arguments and stdio, and
returns the exit code. The binary carries the scaffold it installs, so it
needs nothing else at run time. Every lifecycle decision — what to install,
what the repository's
state means, whether an installation is valid, which migrations apply, what is
stale — is made in F#. Planning is pure and separate from execution:
`inspect -> desired state -> transition -> validate -> execute -> verify`.

## Development

```bash
dotnet build Ros.slnx --configuration Release
dotnet tests/Ros.Tests/bin/Release/net10.0/Ros.Tests.dll    # F# unit and end-to-end CLI tests
python3 -m unittest discover -s tests                       # Python artifact-validator oracle
```

Requires only the .NET 10 SDK (and Python 3 for the oracle tests). This
repository is F#/.NET only (`RQ-ROS-2026-A024`): it owns no JavaScript,
TypeScript, npm or Node tooling, and `./praxis architecture check` (also part of
`./praxis validate`) fails, naming each path, if any appears.

Inside this source checkout, `./praxis` runs the locally built CLI directly:

```bash
./praxis validate
./praxis architecture check
./praxis registry check
./praxis status
```

### Packaging

```bash
dotnet pack src/Ros.Cli/Ros.Cli.fsproj -c Release -o dist/nuget   # the .NET global tool
```

There is no `package.json` and no npm payload: the scaffold is compiled into
the CLI assembly. `native-release.yml` smoke-tests both the native bundle and
the installed .NET tool — `dotnet test` passing is not treated as evidence that
distribution works.

### Release

Publishing is CI-driven ([`.github/workflows/native-release.yml`](.github/workflows/native-release.yml)),
never a local developer machine. A release happens only for a new
`release.json` version: `native-release.yml` builds the self-contained binaries
for every platform, smoke-tests the installer against them, and publishes the
GitHub Release with the native bundles and the legacy `ros-fs-<platform>`
binaries projects scaffolded before the rename download, and the .NET global
tool is pushed to NuGet. Released assets are immutable. See
[`PACKAGE-USAGE.md`](PACKAGE-USAGE.md).

To cut a release, run the **Release** workflow
([`.github/workflows/release.yml`](.github/workflows/release.yml)) from the
Actions tab on `main` with `patch`, `minor`, `major` or an exact `X.Y.Z`. It
bumps the version as an attributed Praxis work item (`RELEASE-X-Y-Z`, via
[`scripts/praxis-release-bump.sh`](scripts/praxis-release-bump.sh)), pushes it
with a durable checkpoint, then dispatches `native-release.yml`. Before
releasing, run the build, tests, `./praxis architecture check` and
`./praxis validate` shown above.

`release.json`'s version is the single authoritative version source: the F#
build reads it (see [`Directory.Build.props`](Directory.Build.props)) so
`praxis --version` can never drift from the released version.

See [`PACKAGE-USAGE.md`](PACKAGE-USAGE.md) for publication and trusted-publishing
setup.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `praxis init` exits `4` naming a tool-owned file | You edited a file the tool owns. Revert it, or move the change into a user-owned file. |
| `praxis verify` exits `3` | Run `praxis doctor` — it names each problem and the command that fixes it. |
| `Unsupported operating system` / `Unsupported architecture` from the installer | That platform is not supported. Build from source in a checkout with `dotnet build Ros.slnx --configuration Release`. |
| `installed configuration version N is newer than this CLI supports` | The repository was installed by a newer release. Upgrade the CLI. |

## Repository concepts

### Canonical source hierarchy

1. Scientific Research Journals
2. Research Execution Packages
3. Theory Registry
4. Evidence Registry

Reports, websites, presentations, and training materials are derived from
canonical research artifacts. Generated products must not silently replace or
modify canonical records.

Canonical knowledge is stored in Markdown artifacts; JSON registries are
generated views, rebuilt with `./praxis registry build`.

### Work protocol

Provider-neutral work context, legal transitions, configurable completion
evidence, durable attribution events, and idempotent file-adapter publication.
A repository-local backlog (`praxis add`, `praxis work list|ready|show|start`) lets
work be captured before it has an externally assigned ID, and graduates into
the same protocol via `work start`.

**Durable checkpoints and continuity.** An executor session is disposable;
repository and Praxis state are durable. `praxis work checkpoint` records a
verified checkpoint: the exact pushed commit, what was completed, and the
next action, verified against the remote itself. A successor on another
machine continues from it with `praxis work continue`, under its own identity.

```bash
git commit -am "Implement capability boundary" && git push
./praxis work checkpoint --id WORK-ID --occurred-at "$(date -u +%Y-%m-%dT%H:%M:%S.000Z)" \
  --summary "Implemented capability boundary" --next-action "Implement consumer fixture"
./praxis work context WORK-ID --text      # latest recoverable checkpoint and current state
```

See [`docs/work-protocol.md`](docs/work-protocol.md) and, for a UI over the same
backlog, [`docs/web-interface.md`](docs/web-interface.md) (`./praxis web serve`).

External project-management products integrate through the normalized
[work adapter contract](docs/work-adapter-contract.md); they are not embedded
in Praxis.

### Adaptive execution telemetry

Every `work begin` starts a provider-neutral execution record, and
`work complete` finalizes it with deterministic clock and Git measurements where
attribution is trustworthy. Runtime adapters can add tokens, costs, context,
agent activity, scope discovery and raw provider fields without changing the
core model.

```bash
./praxis telemetry show WORK-ID
./praxis telemetry ingest WORK-ID --adapter openai-codex --input events.jsonl
./praxis telemetry summary WORK-ID
```

See [`docs/development-telemetry.md`](docs/development-telemetry.md).

### Central aggregation and reporting

The default reporting project is `project-administration`. Praxis ships an
installable profile for it:

```bash
praxis init \
  --profile project-administration \
  --project "Project Administration"
```

That installs a hub with its own registry of other Praxis repositories, a CLI and
web UI to create work items in any of them, and a read-only aggregated view. It
does not include ingestion, reconciliation, access control, retention, or
reporting beyond that view — see
[`docs/project-administration-hub.md`](docs/project-administration-hub.md).

The ownership boundary is:

| Owner | Responsibility |
|---|---|
| Praxis | Generic work protocol, legal transitions, validation, installation behavior, and adapter contract |
| Central reporting repository | Project administration, repository registration, portfolio data, ingestion, reconciliation, reporting rules, access control, retention, and operations |
| Contributing repository | Implementation, evidence, local workflow mapping, and repository-specific instructions |

Do not add central project-administration policy to the reusable Praxis package.
Move a rule into Praxis only when it is intended to apply to every
Praxis-controlled repository.

### Repository principles

- Preserve provenance.
- Prefer stable identifiers over filenames as references.
- Do not overwrite immutable findings.
- Record what evidence supports each important claim.
- Separate observations, evidence, assumptions, inferences, and conclusions.
- Rebuild generated registries after creating canonical artifacts.
- Leave the repository usable by the next agent.

## Further reading

| Document | Covers |
|---|---|
| [`docs/cli.md`](docs/cli.md) | Every command, option, JSON schema and exit code |
| [`docs/installation.md`](docs/installation.md) | `init` semantics, ownership model, manifest, profiles |
| [`docs/upgrading.md`](docs/upgrading.md) | Migration model, supported paths, guarantees |
| [`AGENTS.md`](AGENTS.md) | The agent contract for working inside a Praxis repository |
| [`docs/00-governance/`](docs/00-governance/README.md) | Governance, operating manual, engineering standards, REP specification |
| [`docs/work-protocol.md`](docs/work-protocol.md) | Work transitions, evidence, attribution |
| [`docs/development-telemetry.md`](docs/development-telemetry.md) | Telemetry schema, privacy boundary, provider integrations |
| [`PACKAGE-USAGE.md`](PACKAGE-USAGE.md) | Publication, release gate, trusted publishing |
| [`docs/migrations/fsharp/`](docs/migrations/fsharp/README.md) | The F# migration's staged architecture and command-surface status |
