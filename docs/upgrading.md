# Upgrading

From inside an installed repository, using its own launcher:

```bash
./ros upgrade --dry-run --json   # see exactly what would change
./ros upgrade                    # apply it
```

That works with no npm package on disk and no network, because the CLI carries
its own scaffold — see
[Where the scaffold comes from](installation.md#where-the-scaffold-comes-from).

To upgrade to a version newer than the one the repository is pinned to, run
that version's package instead:

```bash
npx --package=@echelon-foundry/repository-operating-system@<version> ros upgrade
```

## The migration model

An upgrade is never "delete the old files, copy the new ones". It is an
ordered chain of declared configuration-version transitions.

Each installation records a `configurationVersion` in `.echelon/ros.json`.
Each step in the chain knows its source version, its destination version, its
precondition, and the changes it makes. An upgrade from an older version runs
every step between it and the current one, in sequence:

```
0 -> 1 -> 2 -> 3
```

An installation at version 0 with a current CLI at version 3 therefore runs
`0 -> 1`, then `1 -> 2`, then `2 -> 3` — never one arbitrary `0 -> 3` jump.

If a step's precondition fails, the chain stops immediately, **before any
change is written**, and the command exits `5` naming the failing step. The
whole plan is calculated and validated before execution begins, so an upgrade
that cannot complete does not start.

## Supported upgrade paths

| Configuration version | What it is | Path to current |
|---|---|---|
| `0` | A legacy installation made by `ros-bootstrap init`, recorded only in `.ros/installation.json`. | `0 -> 1` |
| `1` | Current. Records `.echelon/ros.json`. | none; already current |
| `> 1` | Installed by a newer release than the CLI you are running. | **refused**, exit `4` |

`ros upgrade` on an already-current installation is a no-op that exits `0`.
`ros upgrade --check` exits `3` when an upgrade is pending and `0` when it is
not, without writing anything.

### The `0 -> 1` migration

Adopts the `.echelon/ros.json` installation manifest. Its precondition is that
a real installation exists to adopt — either `.ros/installation.json` or an
existing `.echelon/ros.json`. For a legacy snapshot, ROS reads its recorded
file hashes as prior ownership evidence and preserves its recorded profile.
That lets an untouched old tool-owned file advance while still blocking a file
that was edited after the legacy install. The legacy snapshot is **left in
place**, not deleted, so `ros-bootstrap verify` keeps working against the same
repository afterwards.

## What upgrade does to your files

Exactly what `init` does, governed by the same ownership rules documented in
[`installation.md`](installation.md#file-ownership):

- **tool-owned** files are replaced when they are byte-identical to what the
  installation recorded.
- a **tool-owned** file you edited locally **blocks the upgrade** rather than
  being overwritten. The command exits `5`, names the file, and changes
  nothing.
- **user-owned**, **shared** and **generated** files are preserved. They appear
  in the plan's `preserved` list so "untouched" is visible rather than implied.

The `preserved` array in `upgrade --dry-run --json` is the authoritative answer
to "what will you leave alone", and it is worth reading before a large upgrade.

## Failure behaviour and recovery

- The plan is calculated and validated first. A blocking conflict means
  nothing is written.
- During execution, each file's new content is written to a staging file
  beside its destination and re-hashed against the hash the plan calculated
  before any destination is replaced. Content that does not match is never
  installed.
- If a write fails, staged files are cleaned up and the command reports the
  failure rather than exiting `0`. Nothing is silently swallowed.
- The installation manifest is written last, so an interrupted upgrade leaves
  a manifest that under-claims rather than one claiming artifacts it never
  wrote. Re-running `ros upgrade` then converges.

Recovery from any partial state is the same command: `ros doctor` to see what
is wrong, then `ros init` or `ros upgrade` to converge.

## Compatibility policy

- Adding a field to a JSON document, or a new command or option, is not a
  breaking change and does not bump a schema version.
- Removing a field, changing what one means, or changing an exit code is a
  breaking change. It requires a new schema or configuration version and a
  migration step.
- `ros-bootstrap init` and `ros-bootstrap verify` remain published and
  behave exactly as they did. They are legacy compatibility, not a second
  recommended path.
- A repository installed by `ros-bootstrap init` keeps working with no action
  from you. `ros status` reports it as `upgrade-required`; adopting the
  manifest with `ros upgrade` is what moves it to `installed`.

## What is proven

The guarantees above are the ones covered by tests in
`tests/lifecycle-package.test.mjs`, which runs against the actual packed npm
artifact rather than the source tree:

- uninstalled → current (`init`)
- current → current is a byte-identical no-op (idempotency)
- legacy (`ros-bootstrap`) → current, including older recorded tool-owned bytes,
  local-edit blocking, profile preservation, and the legacy snapshot left in place
- a configuration version newer than the CLI is refused
- a damaged installation fails `verify` and is explained by `doctor`
- a locally modified tool-owned file blocks `init` and survives
- a user-owned file is never overwritten and is recorded at its own content
- `--dry-run` and `--check` change nothing

Nothing stronger is claimed than what those tests exercise.

## Upgrading the CLI itself

A scaffolded project pins its CLI version in `ros.json`, and its `./ros`
downloads and caches that version's binary. Changing that value is what moves
the project to a new release; `npx --package=...@<version> ros upgrade` does the
same thing for a single run without changing the pin.
