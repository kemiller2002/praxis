# Installation

## Quick start

```bash
cd /path/to/your/repository

# Install, or bring an existing installation up to date.
npx --package=@echelon-foundry/repository-operating-system ros init

# Confirm what is installed.
npx --package=@echelon-foundry/repository-operating-system ros status

# Check it is correct.
npx --package=@echelon-foundry/repository-operating-system ros verify
```

Nothing is installed by `npm install` itself. The package has no `preinstall`,
`install` or `postinstall` script and never mutates a repository as a side
effect of being downloaded; initialization only happens when you run `init`.

## Prerequisites

- Node.js 20 or newer, for the launcher.
- Network access on first use of a given version and platform, so the launcher
  can fetch and cache the CLI binary. Later runs of the same version work
  offline from `~/.cache/ros-fs/<version>/<platform>/` (override the location
  with `ROS_FS_CACHE_DIR`).
- No .NET installation is required: the published binary is self-contained.

Supported platforms: `linux/x64`, `linux/arm64`, `darwin/x64`, `darwin/arm64`,
`win32/x64`. The package deliberately declares no npm `os` or `cpu`
restriction, because one package serves every platform and the launcher picks
the right binary at run time. An unsupported platform fails with a message
naming the gap rather than failing obscurely.

## What `init` means

`init` means **bring this repository into a valid installed state**. It does
not mean "copy some files".

It is safe, repeatable and idempotent. Running it a second time against an
unchanged repository writes nothing at all — not "writes the same bytes
again", but plans zero changes. You can prove that yourself:

```bash
npx --package=@echelon-foundry/repository-operating-system ros init
npx --package=@echelon-foundry/repository-operating-system ros init   # "no changes needed"
```

The sequence is always the same, whatever state the repository starts in:

```
inspect -> determine desired state -> calculate transition
        -> validate transition -> execute -> verify
```

### What it may create

- The tool-owned scaffold for the chosen profile: governance documents, the
  framework and standards, JSON schemas, templates, the `ros` launcher, and
  the CI workflow.
- `ros.json`, the repository's configuration, seeded from a template.
- `.echelon/ros.json`, the installation manifest.
- Empty registries and `.gitkeep` placeholders.
- On a **first** install only, the legacy `.ros/installation.json` snapshot
  plus the installation's work attribution (`.ros/context/current.json`,
  `.ros/events/events.jsonl`, `.ros/work/queue.json`, `.ros/work/queue.md`), so
  a repository installed by `ros init` is indistinguishable from one installed
  by the older `ros-bootstrap init`.

### What it will not overwrite

- A **user-owned** file that already exists — including `README.md`,
  `.gitignore`, `.gitattributes`, `.editorconfig`, `CLAUDE.md`, `GEMINI.md` and
  `.github/copilot-instructions.md`. Yours is kept, and the manifest records
  the content you actually have.
- A **shared** file that already exists, such as `ros.json`,
  `PROJECT-CHARTER.md`, `HANDOFF.md` or anything under `context/`. These are
  seeded once and then belong to you.
- A **generated** file that already exists, such as anything under
  `registries/`. Its real content comes from your own artifacts via
  `ros registry build`; copying the package's empty seed over a populated
  registry would destroy data.
- A **tool-owned** file you have edited locally. That stops the command rather
  than silently discarding your change.

### What happens when there is a conflict

`init` calculates the whole plan and validates it before writing anything. If
the plan has a blocking conflict, nothing is written and the command exits `4`
naming each conflict and its remedy:

| Conflict | When | What to do |
|---|---|---|
| `locally-modified-tool-file` | A tool-owned file was edited after installation. | Revert it, or move the change into a user-owned file. |
| `unmanaged-file-in-the-way` | A different file already sits at a tool-owned path, with no installation recording it. | Move or delete it, or accept that the capability is not installed there. |
| `migration-precondition-failed` | An upgrade step's precondition does not hold. | Resolve the named precondition first. |

There is no force or merge flag. A managed collision is resolved explicitly,
never by a flag that discards work.

## File ownership

Every managed file carries one of four classifications. The classification —
not the fact that `init` once created the file — decides what the tool may
later do to it.

| Ownership | Definition | `init` / `upgrade` | `verify` |
|---|---|---|---|
| **tool-owned** | Controlled by the tool. | Replaced when byte-identical to what the installation recorded; a local edit blocks instead. | Presence **and** content are checked. |
| **generated** | Derived from authoritative inputs already in the repository. | Seeded once. After that only the generator that owns it (`ros registry build`) rewrites it. | Presence only. |
| **user-owned** | Controlled by the repository. | Seeded once if absent, never rewritten. | Presence only; absence is a warning. |
| **shared** | Seeded by the tool, then edited by the repository. | Seeded once. Only a declared migration may change it, and only when it is unmodified. | Presence only. |

Ownership is declared as data, in the profile's `starter/<profile>/manifest.json`.
An entry may state it explicitly with an `"ownership"` field; otherwise it is
derived, and the derivation is part of the contract:

- `"policy": "preserve-existing"` → **user-owned**
- `"template": true` → **shared**
- otherwise → **tool-owned**

## Where the scaffold comes from

The CLI carries its own copy of everything it installs, compiled into the
executable. That is what lets a project run its own lifecycle:

```bash
cd /path/to/your/repository
./ros verify      # is the installation intact?
./ros init        # heal: restore anything tool-owned that is missing
./ros upgrade     # update to this CLI's version
```

No npm package on disk, no network, and no `--package-root` are needed for any
of those. A project scaffolded by `init` gets a `./ros` launcher that downloads
the binary for the version pinned in its `ros.json`; that binary is
self-sufficient from then on.

The scaffold is resolved in this order:

1. `--package-root PATH`, when given.
2. `ROS_PACKAGE_ROOT`, when set.
3. A package directory found by walking up from the executable, then from the
   working directory — this is what a source checkout and an `npx` invocation
   both hit.
4. The copy compiled into the binary.

A real directory wins so that a source checkout installs the files you are
editing rather than the ones compiled in. Everywhere else, step 4 applies.

The compiled-in copy is exactly the set of files the profile manifests
reference — no more, no less; a test asserts that equality in both directions,
so a manifest entry that was never embedded fails the build's tests rather than
shipping a binary that cannot install itself.

## Installation manifest

`.echelon/ros.json` is the machine-readable record of what is installed. The
presence of arbitrary files is never the source of truth; this file is.

```json
{
  "schemaVersion": 1,
  "tool": "ros",
  "package": "@echelon-foundry/repository-operating-system",
  "installedVersion": "3.0.0",
  "configurationVersion": 1,
  "profile": "greenfield",
  "managedArtifacts": [
    { "path": "AGENTS.md", "ownership": "tool-owned", "sha256": "..." }
  ]
}
```

`schemaVersion` is typed and versioned; a CLI that meets a schema version it
does not support says so rather than guessing. The manifest deliberately holds
**no** timestamp, absolute path, machine identity, credential or secret — which
is also what makes a re-`init` a genuine no-op rather than a file that changes
every run.

`.echelon/` is the shared Echelon Foundry root: each tool owns its own manifest
there and they coexist without a shared global format. The tool never treats
changes under `.echelon/` as meaningful repository change for work attribution.

## Profiles

| Profile | Installs |
|---|---|
| `greenfield` | The default. Governance, framework, schemas, templates, empty registries, the CLI launcher and the CI workflow. |
| `project-administration` | The above plus the hub: a registry of other ROS repositories, a CLI and web UI to create work items in them, and an aggregated read-only view. See [`project-administration-hub.md`](project-administration-hub.md). |

```bash
npx --package=@echelon-foundry/repository-operating-system ros init \
  --profile project-administration \
  --project "Project Administration"
```

`--project` sets the display name. Omit it and the name is derived from the
target folder.

## After installing

```bash
./ros registry check
./ros validate
```

The scaffolded repository can also run its own lifecycle — `./ros verify`,
`./ros init` to heal, `./ros upgrade` to update — without reaching for npx; see
[Where the scaffold comes from](#where-the-scaffold-comes-from).

The scaffolded repository gets its own `./ros`, which runs the same F# CLI
pinned to the version recorded in its `ros.json`. It does not read from, or
link back to, the source checkout that installed it.

## Legacy compatibility

`ros-bootstrap init` and `ros-bootstrap verify` still work exactly as before
and are still published. They are **legacy compatibility**: use `ros init`
and `ros verify` for new work. See [`upgrading.md`](upgrading.md) for how an
existing `ros-bootstrap` installation moves across.
