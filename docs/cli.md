# CLI reference

The canonical public interface is the `praxis` command (compatibility alias `ros`), a
self-contained F# binary installed by the native installers (see
[`native-installation.md`](native-installation.md)).

```bash
praxis <command>
```

All five lifecycle commands, and the repository commands below them, are the
same F# CLI. Launchers (`praxis`, a project's `./praxis`, and the `ros`/`./ros`
compatibility aliases) only start it; no
decision is made anywhere else, and no Node.js or npm is involved
(`DF-ROS-2026-A049`).

## Lifecycle commands

| Command | Writes? | Purpose |
|---|---|---|
| `init` | yes | Bring the repository into a valid installed state. Idempotent. |
| `status` | no | Report installation, validation and work state. |
| `verify` | no | Check that the capability is correctly installed. |
| `upgrade` | yes | Migrate an existing installation to this CLI's version. |
| `doctor` | no | Diagnose problems and explain how to fix them. |

## Global options

| Option | Meaning |
|---|---|
| `-h`, `--help` | Show help. `praxis <command> --help` shows that command's help. |
| `-V`, `--version` | Print `praxis <version>`, where the version is the release version (`release.json`). |
| `--root PATH` | Repository to act on. Defaults to the current directory. |
| `--package-root PATH` | Install from this scaffold directory instead of the one compiled into the binary. Rarely needed — see [Where the scaffold comes from](installation.md#where-the-scaffold-comes-from). |
| `--json` | Emit machine-readable JSON on stdout. |
| `--verbose` | Emit extra detail. |

## `init`

```
praxis init [--profile NAME] [--project NAME] [--dry-run] [--check] [--json] [--verbose]
```

Inspects the repository, determines the installed state, calculates the
changes needed, validates them, and applies them. See
[`installation.md`](installation.md) for exactly what it may create, what it
will not overwrite, and how ownership works.

| Option | Meaning |
|---|---|
| `--profile NAME` | Starter profile to install. Default `greenfield`; `project-administration` is also available. |
| `--project NAME` | Display name for a new installation. Derived from the repository folder when omitted. |
| `--dry-run` | Calculate and report the full plan; change nothing. |
| `--check` | Change nothing, and exit `3` if any change would be needed. |

## `status`

```
praxis status [--json] [--verbose]
```

Read-only. Prints a JSON document covering work items, validation findings,
telemetry counts and the installation.

The output is JSON with or without `--json`. This command emitted JSON before
the lifecycle interface existed and consumers depend on that, so the default
was left alone; `--json` is accepted so scripts can be explicit. `--verbose`
adds the full `installation.managedArtifacts` list.

## `verify`

```
praxis verify [--strict] [--json] [--verbose]
```

Read-only. Checks that every tool-owned artifact the installation manifest
records is present and unmodified, that `ros.json` is present and parseable,
and that the manifest schema is one this CLI supports.

Generated, shared and user-owned files are checked for presence, not content:
the repository, or a generator the repository runs, is what makes them
current. See [`installation.md`](installation.md#file-ownership).

`--strict` also fails on warnings — an available upgrade, a legacy
installation with no manifest, or a seeded file that has been deleted. A
strict pass always implies a non-strict pass.

## `upgrade`

```
praxis upgrade [--dry-run] [--check] [--json] [--verbose]
```

Resolves the ordered chain of migrations from the installed configuration
version to this CLI's, checks each step's precondition, then applies them and
reconciles tool-owned files. See [`upgrading.md`](upgrading.md).

## `doctor`

```
praxis doctor [--strict] [--json] [--verbose]
```

Read-only. Reports every problem it can detect, each with the reason and,
where one exists, the command that fixes it. Findings are classified:

| Severity | Meaning |
|---|---|
| `error` | The installation is not usable as recorded. |
| `warning` | Usable, but something should be attended to. |
| `information` | An expected state worth knowing, such as a shared file you have edited. |

`doctor` exits `3` when any error is present, or — with `--strict` — when any
warning is.

## Exit codes

These are a public contract. Changing a value is a breaking change.

| Code | Meaning |
|---|---|
| `0` | Success. |
| `1` | Internal failure. |
| `2` | Invalid arguments. |
| `3` | Verification failed, or `--check` found pending work. |
| `4` | Incompatible installation (blocked `init`, unsupported configuration version). |
| `5` | Migration blocked. |
| `6` | Prerequisite or environment failure. |
| `7` | Unsupported platform. |

The repository commands below predate this table and keep their historical
codes: `0` success, `1` validation or operation failure, `2` unusable
arguments.

## Machine-readable output

`--json` writes one JSON document to stdout and nothing else. Diagnostics and
decorative text go to stderr. Every document carries `schemaVersion`, which is
the version of the document shape, not of the tool; a field may be added
without bumping it, and no field is removed or repurposed without bumping it.

### `status --json`

Every key that existed before the lifecycle interface is unchanged. One key
was added:

```jsonc
{
  "repository": "...", "protocolVersion": "...", "validation": "passed",
  "findingCount": 0, "workItems": [], "telemetry": {}, "nextActions": [],
  "installation": {
    "schemaVersion": 1,
    "tool": "ros",
    "package": "@echelon-foundry/repository-operating-system",
    "cliVersion": "3.0.0",
    "installedVersion": "3.0.0",     // null when nothing is installed
    "configurationVersion": 1,        // null when there is no manifest
    "profile": "greenfield",
    "managedArtifactCount": 82,
    "state": "installed",             // not-installed | installed | upgrade-required | invalid
    "verified": true,
    "upgradeAvailable": null,         // the available version when state is upgrade-required
    "managedArtifacts": []            // --verbose only
  }
}
```

### `verify --json`

```jsonc
{
  "command": "verify",
  "schemaVersion": 1,
  "valid": true,
  "strict": false,
  "installation": { /* as above, without managedArtifacts */ },
  "failures": [
    { "severity": "error", "code": "managed-artifact-missing",
      "message": "managed artifact is missing",
      "path": "framework/REP-SPECIFICATION.md",
      "remedy": "Run 'praxis init' to restore the missing tool-owned artifact." }
  ]
}
```

### `doctor --json`

Same shape, plus `healthy`, `errorCount`, `warningCount` and a `diagnoses`
array holding every finding at every severity.

### `init --json` and `upgrade --json`

Identical shape whether or not the plan was executed, so an agent parses one
document either way:

```jsonc
{
  "command": "init",
  "schemaVersion": 1,
  "package": "@echelon-foundry/repository-operating-system",
  "cliVersion": "3.0.0",
  "dryRun": true,
  "applied": false,
  "changesRequired": true,
  "migrations": [ { "fromVersion": 0, "toVersion": 1, "description": "..." } ],
  "changes": [
    { "kind": "create-file", "path": "AGENTS.md",
      "description": "create AGENTS.md (tool-owned)", "ownership": "tool-owned" }
  ],
  "conflicts": [
    { "code": "locally-modified-tool-file", "path": "...", "message": "...",
      "remedy": "...", "blocking": true }
  ],
  "preserved": ["README.md"],
  "manifest": { /* the manifest the repository would carry */ }
}
```

`kind` is one of `create-directory`, `create-file`, `update-managed-file`,
`update-configuration`, `register-integration`, `run-migration`.

## CI usage

```bash
# Fail the build if the repository is not installed and current.
praxis verify --strict

# Fail the build if init would change anything.
praxis init --check

# Machine-readable, for a step that parses the result.
praxis verify --json
```

Exit code `0` means the assertion held; `3` means it did not. Any other
nonzero code is a different failure — see the table above — and should not be
treated as "verification failed".

## Agent usage

Every command is non-interactive and never prompts, so no invocation can hang
waiting for input. There is no "force" flag: an operation that would destroy a
local change stops and reports the conflict instead of asking for approval or
assuming it.

For an agent driving this tool:

- Use `--json` for every command you parse. stdout is the document; read
  diagnostics from stderr.
- Use `--dry-run --json` before `init` or `upgrade` to see the whole plan,
  including `conflicts`, without writing.
- Branch on the exit code, not on the human text.
- `changesRequired: false` is the signal that the repository is already in the
  desired state.
- When `conflicts` is non-empty, each entry carries a `remedy`; none of them
  can be resolved by re-running the same command.

## Repository commands

The same executable carries the repository's artifact, work and telemetry
commands. These predate the lifecycle interface and are unchanged:

```
praxis validate [--json]
praxis registry build [--dry-run] | registry check
praxis git status [--json]
praxis work <capture|list|ready|show|start|resume|block|complete|reconcile|update|attach|context|...>
praxis add "..."
praxis telemetry <show|summary|finalize|record|ingest|classify|start|adapters|validate>
praxis adapter <call|publish>
praxis provenance <identity|record|show|audit>
```

Run `praxis --help` for the full argument list, and see
[`work-protocol.md`](work-protocol.md),
[`development-telemetry.md`](development-telemetry.md),
[`work-adapter-contract.md`](work-adapter-contract.md) and
[`agent-provenance.md`](agent-provenance.md) for what they mean.

### `work reconcile`

```
praxis work reconcile --id ID --occurred-at TIMESTAMP --reason TEXT
                   (--commit REV | --range BASE..HEAD) [--commit REV]* [--range BASE..HEAD]*
                   [--path PATH]* [--dry-run] [--json] [IDENTITY]
```

Post-hoc, Git-evidenced attribution of committed meaningful changes that were
made without an active work item. Git establishes the paths (added, modified,
deleted, both sides of a rename); `--path` can only narrow them. It appends one
`work.attribution.reconciled` event marked `"attribution":"post-hoc"`, recording
the reconciliation actor separately from each commit's Git author and
committer, and never changes existing events or work state. Idempotent per
`(commit, path)`; a change already reconciled to another work item is a
conflict. Ambiguous or unverifiable evidence (unknown or ambiguous revision, a
commit outside `HEAD`'s history, a merge, an empty or unrelated range, a
shallow boundary, unavailable Git) is rejected with exit `1` and nothing is
recorded; argument errors exit `2`. `--dry-run` shows the assessment without
recording. See "Post-hoc attribution reconciliation" in
[`work-protocol.md`](work-protocol.md) for when to use it and when not to.

### `remote execute`

```
praxis remote execute --request FILE [--grant read|mutate|complete|reconcile]*
                      [--output FILE] [--timeout-seconds N]
```

`remote execute` runs one `praxis.remote` request on behalf of an agent that
has no local Praxis runtime. It is the executor side of the protocol and is
run by a trusted adapter, not by the requester.

- **Same rules as local commands.** The request is carried out by the same
  command implementation as the local CLI.
- **Grants.** `--grant` states what the transport allows. It is intersected
  with the repository's `ros.json` setting `remote.capabilities`. When that
  setting is absent, only reads are allowed.
- **Idempotent retries.** A mutation is bound to `repository.expectedSha`
  and journalled under `.ros/remote/requests/`, so retrying the same
  request ID replays the recorded outcome instead of running again.
- **Output.** The command prints the response JSON.
- **Exit codes:**
  - `0` when the outcome is `succeeded`
  - `1` for any other outcome
  - `2` for argument errors

See [`remote-protocol.md`](remote-protocol.md) for the full protocol.

### Identity flags and provenance commands

Every work transition (`work start|begin|resume|block|complete|done`), `work reconcile`, `add`
or `work capture`, and `telemetry start` accepts the same identity
declaration. Each flag overrides the whitelisted environment:

```
--actor-kind agent|human|automation|unknown|x-...   (env PRAXIS_ACTOR_KIND)
--agent ID | --actor ID                              (env PRAXIS_ACTOR; stable identity)
--provider P --model M --model-version V --runtime R --runtime-version V
--session S --conversation C --run R --subagent ID   (env PRAXIS_TELEMETRY_*)
```

The legacy `ROS_*` names of these variables still work; the `PRAXIS_*` name
wins when both are set. An invalid `--actor-kind` is an argument error (exit `2`).

| Command | Purpose |
|---|---|
| `provenance identity [--json]` | who this process is recorded as, how that was determined, and the active executions |
| `provenance record --path PATH\|--id ID --operation OP [--reason T] [--evidence REF]* [--derived-from REF]* [--execution EXE] [--occurred-at TS] [--json]` | attribute a contribution to an artifact's front matter and append an `artifact.contributed` event; identity is inherited from the active execution; idempotent |
| `provenance show ID\|PATH [--json]` | contributors, involvement label, lineage (sources and derivatives), legacy-declared authors, and events |
| `provenance audit [--json]` | coverage, per-actor summaries, flattened contribution facts for metrics, and every finding including informational ones; exits `1` on errors |

`validate` reports provenance errors (which fail validation) and provenance
warnings (which do not). In `--json`, warnings carry `"severity":"warning"`,
and `valid` reflects errors only.
