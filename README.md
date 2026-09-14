# Repository Operating System

**`@echelon-foundry/repository-operating-system`**

Repository initialization, verification, diagnostics, and upgrade tooling that
makes research, engineering, decisions, and handoffs durable without relying on
conversation history or tribal knowledge.

Distributed under the [MIT License](LICENSE).

## What it provides

Installing ROS into a repository gives it a governed operating environment:

- a **work protocol** with legal `begin`/`block`/`resume`/`complete`
  transitions, configurable completion evidence, and durable attribution
  events;
- **canonical research records** — journals, execution packages, theories,
  evidence — with lifecycle, supersession, identifier and confidence rules,
  and generated registries projected from them;
- **adaptive execution telemetry** that distinguishes unavailable from zero and
  observed from derived;
- **validation** that fails on malformed front matter, invalid or duplicate
  IDs, broken references, nonreciprocal supersession, and stale registries;
- a **CI workflow** that runs all of the above.

## Quick start

```bash
cd /path/to/your/repository

# Initialize, or bring an existing installation up to date.
npx --package=@echelon-foundry/repository-operating-system ros init

# Confirm what is installed.
npx --package=@echelon-foundry/repository-operating-system ros status

# Verify the repository.
npx --package=@echelon-foundry/repository-operating-system ros verify
```

Nothing happens during `npm install`. The package has no install lifecycle
scripts and never mutates a repository as a side effect of being downloaded.

## Commands

| Command | Writes? | Purpose |
|---|---|---|
| `ros init` | yes | Bring the repository into a valid installed state. Idempotent. |
| `ros status` | no | Report installation, validation and work state. |
| `ros verify` | no | Check that the capability is correctly installed. |
| `ros upgrade` | yes | Migrate an existing installation to this CLI's version. |
| `ros doctor` | no | Diagnose problems and explain how to fix them. |

Plus `ros --help` (and `ros <command> --help`) and `ros --version`.

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
ros init --dry-run          # calculate and report the whole plan; change nothing
ros init --check            # change nothing; exit 3 if any change would be needed
ros upgrade --dry-run --json
```

### Machine-readable mode

```bash
ros status --json
ros verify --json
ros doctor --json
ros init --dry-run --json
ros upgrade --dry-run --json
```

With `--json`, stdout carries one JSON document and nothing else; diagnostics
go to stderr. Schemas are in [`docs/cli.md`](docs/cli.md#machine-readable-output).

### CI usage

```bash
npx --package=@echelon-foundry/repository-operating-system ros verify --strict
```

Exit `0` means valid, `3` means verification failed. Other nonzero codes mean
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
| `./ros` | The repository's own launcher for this CLI, pinned to the version in `ros.json`. |

`.echelon/` is the shared Echelon Foundry root. Each tool owns its own manifest
there and they coexist cleanly.

## File ownership

Every managed file is classified, and the classification decides what the tool
may do to it:

| Ownership | Meaning |
|---|---|
| **tool-owned** | Controlled by the tool; replaced on upgrade when unmodified, blocks when edited locally. |
| **generated** | Derived from the repository's own artifacts; seeded once, then owned by `ros registry build`. |
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
`ros-bootstrap verify` executables still ship and behave exactly as before.
They are supported for existing users, not a second recommended path — use
`ros init` and `ros verify` for new work. A repository installed by
`ros-bootstrap` keeps working untouched; `ros status` reports it as
`upgrade-required`, and `ros upgrade` adopts the manifest while leaving the
legacy snapshot in place.

## Supported platforms

`linux/x64`, `linux/arm64`, `darwin/x64`, `darwin/arm64`, `win32/x64`.

Node.js 20 or newer is needed for the launcher. No .NET installation is
required: the CLI ships as a self-contained binary, fetched and checksum-verified
on first use of a given version and platform, then cached under
`~/.cache/ros-fs/<version>/<platform>/` (override with `ROS_FS_CACHE_DIR`) and
run offline thereafter.

The package declares no npm `os` or `cpu` restriction on purpose: one package
serves every platform and the launcher selects the right binary at run time. An
unsupported platform fails with a message naming the gap.

## How it is built

```
npm / npx
    |
    v
tiny Node bootstrap (bin/ros.mjs, lib/lifecycle-launcher.mjs)
    |
    v
F# CLI (src/Ros.Cli)
    |
    v
F# domain and application core (src/Ros.Domain, src/Ros.Application)
```

The Node launcher only detects the platform, locates the CLI binary, tells it
where the package's own files are, forwards arguments and stdio, and returns
the exit code. Every lifecycle decision — what to install, what the repository's
state means, whether an installation is valid, which migrations apply, what is
stale — is made in F#. Planning is pure and separate from execution:
`inspect -> desired state -> transition -> validate -> execute -> verify`.

## Development

```bash
npm run build:fsharp        # dotnet build Ros.slnx --configuration Release
npm test                    # node + python suites, including the packed artifact
npm run test:fsharp         # F# unit tests and the differential suites
npm run test:all            # everything
```

Requires the .NET 10 SDK and Node.js 20+.

Inside this source checkout, `./ros` runs the locally built CLI directly:

```bash
./ros validate
./ros registry check
./ros status
```

### Packaging

```bash
npm run pack:inspect        # npm pack --dry-run: review the file list
npm pack                    # produce the real tarball
```

`tests/lifecycle-package.test.mjs` packs the artifact, extracts it the way
`npx` would, and runs every documented command against throwaway repositories —
`dotnet test` passing is not treated as evidence that npm distribution works.

### Release

Publishing is CI-driven ([`.github/workflows/publish.yml`](.github/workflows/publish.yml)),
never a local developer machine. Every push to `main` publishes a `main`-tagged
snapshot; a stable release happens only when `package.json`'s committed version
changes, which also builds the self-contained binaries and creates the matching
GitHub Release. Before tagging:

```bash
npm run release:check
```

`package.json`'s version is the single authoritative version source: the F#
build reads it (see [`Directory.Build.props`](Directory.Build.props)) so
`ros --version` can never drift from the released package version.

See [`PACKAGE-USAGE.md`](PACKAGE-USAGE.md) for publication and trusted-publishing
setup.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `ros init` exits `4` naming a tool-owned file | You edited a file the tool owns. Revert it, or move the change into a user-owned file. |
| `ros verify` exits `3` | Run `ros doctor` — it names each problem and the command that fixes it. |
| `no prebuilt binary for <platform>/<arch>` | That platform is not supported. Build from source in a checkout with `npm run build:fsharp`. |
| `version ... is a main-branch snapshot` | A `@main` snapshot has no GitHub Release and therefore no binary. Install a stable version. |
| `this CLI cannot see its own packaged scaffold` (exit `6`) | `init`/`upgrade` need the package's files. Run through `npx --package=...` rather than invoking a cached binary directly, or pass `--package-root`. |
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
generated views, rebuilt with `./ros registry build`.

### Work protocol

Provider-neutral work context, legal transitions, configurable completion
evidence, durable attribution events, and idempotent file-adapter publication.
A repository-local backlog (`ros add`, `ros work list|ready|show|start`) lets
work be captured before it has an externally assigned ID, and graduates into
the same protocol via `work start`.

See [`docs/work-protocol.md`](docs/work-protocol.md) and, for a UI over the same
backlog, [`docs/web-interface.md`](docs/web-interface.md) (`npm run web`).

External project-management products integrate through the normalized
[work adapter contract](docs/work-adapter-contract.md); they are not embedded
in ROS.

### Adaptive execution telemetry

Every `work begin` starts a provider-neutral execution record, and
`work complete` finalizes it with deterministic clock and Git measurements where
attribution is trustworthy. Runtime adapters can add tokens, costs, context,
agent activity, scope discovery and raw provider fields without changing the
core model.

```bash
./ros telemetry show WORK-ID
./ros telemetry ingest WORK-ID --adapter openai-codex --input events.jsonl
./ros telemetry summary WORK-ID
```

See [`docs/development-telemetry.md`](docs/development-telemetry.md).

### Central aggregation and reporting

The default reporting project is `project-administration`. ROS ships an
installable profile for it:

```bash
npx --package=@echelon-foundry/repository-operating-system ros init \
  --profile project-administration \
  --project "Project Administration"
```

That installs a hub with its own registry of other ROS repositories, a CLI and
web UI to create work items in any of them, and a read-only aggregated view. It
does not include ingestion, reconciliation, access control, retention, or
reporting beyond that view — see
[`docs/project-administration-hub.md`](docs/project-administration-hub.md).

The ownership boundary is:

| Owner | Responsibility |
|---|---|
| ROS | Generic work protocol, legal transitions, validation, installation behavior, and adapter contract |
| Central reporting repository | Project administration, repository registration, portfolio data, ingestion, reconciliation, reporting rules, access control, retention, and operations |
| Contributing repository | Implementation, evidence, local workflow mapping, and repository-specific instructions |

Do not add central project-administration policy to the reusable ROS package.
Move a rule into ROS only when it is intended to apply to every ROS-controlled
repository.

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
| [`AGENTS.md`](AGENTS.md) | The agent contract for working inside a ROS repository |
| [`docs/00-governance/`](docs/00-governance/README.md) | Governance, operating manual, engineering standards, REP specification |
| [`docs/work-protocol.md`](docs/work-protocol.md) | Work transitions, evidence, attribution |
| [`docs/development-telemetry.md`](docs/development-telemetry.md) | Telemetry schema, privacy boundary, provider integrations |
| [`PACKAGE-USAGE.md`](PACKAGE-USAGE.md) | Publication, release gate, trusted publishing |
| [`docs/migrations/fsharp/`](docs/migrations/fsharp/README.md) | The F# migration's staged architecture and command-surface status |
