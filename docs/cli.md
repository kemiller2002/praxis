# CLI reference

The canonical public interface is the `praxis` command (`ros` is kept as an
alias and as a scaffolded project's `./ros`). Install it from the native
bundle or as the .NET global tool `EchelonFoundry.Praxis`; see
[`installation.md`](installation.md).

```bash
praxis <command>
```

All five lifecycle commands, and the repository commands below them, are the
same F# CLI. No lifecycle decision is made in a launcher.

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
| `-h`, `--help` | Show help. `ros <command> --help` shows that command's help. |
| `-V`, `--version` | Print `ros-fs <version>`, the Praxis release version (from `package.json`, the single version source). |
| `--root PATH` | Repository to act on. Defaults to the current directory. |
| `--package-root PATH` | Install from this scaffold directory instead of the one compiled into the binary. Rarely needed — see [Where the scaffold comes from](installation.md#where-the-scaffold-comes-from). |
| `--json` | Emit machine-readable JSON on stdout. |
| `--verbose` | Emit extra detail. |

## `init`

```
ros init [--profile NAME] [--project NAME] [--dry-run] [--check] [--json] [--verbose]
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
ros status [--json] [--verbose]
```

Read-only. Prints a JSON document covering work items, validation findings,
telemetry counts and the installation.

The output is JSON with or without `--json`. This command emitted JSON before
the lifecycle interface existed and consumers depend on that, so the default
was left alone; `--json` is accepted so scripts can be explicit. `--verbose`
adds the full `installation.managedArtifacts` list.

## `verify`

```
ros verify [--strict] [--json] [--verbose]
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
ros upgrade [--dry-run] [--check] [--json] [--verbose]
```

Resolves the ordered chain of migrations from the installed configuration
version to this CLI's, checks each step's precondition, then applies them and
reconciles tool-owned files. See [`upgrading.md`](upgrading.md).

## `doctor`

```
ros doctor [--strict] [--json] [--verbose]
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
      "remedy": "Run 'ros init' to restore the missing tool-owned artifact." }
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
ros validate [--json]
ros registry build [--dry-run] | registry check
ros git status [--json]
ros work <capture|list|ready|show|start|resume|block|complete|reconcile|checkpoint|continue|update|attach|context|...>
ros add "..."
ros telemetry <show|summary|finalize|record|ingest|classify|start|adapters|validate>
ros adapter <call|publish>
ros provenance <identity|record|show|audit>
ros plan <analyze|simulate|compare|explain|replay|freshness|groups|explain-group>
```

Run `ros --help` for the full argument list, and see
[`work-protocol.md`](work-protocol.md),
[`development-telemetry.md`](development-telemetry.md),
[`work-adapter-contract.md`](work-adapter-contract.md) and
[`agent-provenance.md`](agent-provenance.md) for what they mean.

### `plan`

```
ros plan analyze   [--json]
ros plan simulate  [--for baseline|speed|balanced|cost|max-parallel] [--max-concurrency N]
                   [--budget AMOUNT [--currency CODE]] [--deadline 4h|90m] [--details] [--json]
ros plan compare   [--max-concurrency N] [--json]
ros plan explain   ID [--json]
ros plan replay    [--details] [--json]
ros plan freshness --plan FILE [--json]
ros plan groups    [--json]
ros plan explain-group GROUP-ID [--json]
ros plan simulate --groups [--max-concurrency N] [--json]
ros plan compare  --groups [--max-concurrency N] [--json]
     common: [--observations FILE] [--config FILE] [--as-of TIMESTAMP]
```

The advisory planner: read-only, deterministic, and never changes work state
(`DF-ROS-2026-A046`). It classifies every queue and live-context item, finds
stale state from Git and supplied evidence, and recommends execution waves
under an explicit strategy and risk policy, with a reason for every entry.
Unknown durations and costs stay unknown; without cost telemetry the `cost`
strategy is unavailable and `--budget` cannot be evaluated. `--json` documents
use the versioned `praxis.plan/1.0.0` schema. `freshness` exits `3` when the
saved plan is stale. `groups` recommends evidence-based work groups (items to
reason about together, with the evidence, collision risk and recommended
execution for each) without changing any item; `explain-group` answers why a
group exists and what would change it (`DF-ROS-2026-A047`). See
[`planning.md`](planning.md).

### `work reconcile`

```
ros work reconcile --id ID --occurred-at TIMESTAMP --reason TEXT
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

### `work checkpoint`, `work checkpoint show`, `work continue`

```
ros work checkpoint --id ID --occurred-at TIMESTAMP --summary TEXT --next-action TEXT
                    [--step STEP-ID] [--execution EXE-ID] [--json] [IDENTITY]
ros work checkpoint show ID [--json] [--offline]
ros work continue --id ID --occurred-at TIMESTAMP [--json] [IDENTITY]
ros work context [ID] [--text] [--offline]
ros work block ... [--unrecoverable-reason TEXT]
ros work abandon --id ID [--id ID]* --occurred-at TIMESTAMP --reason TEXT [IDENTITY]
ros status [--json] [--verbose] [--offline]
```

**`work checkpoint`** records a verified durable checkpoint. The remote itself
must show that local HEAD, the checkpoint commit, and the upstream branch head
are the same commit, with no meaningful uncommitted work. It never commits,
pushes or stashes.

- `--json` prints `status` (`recorded`, `rejected` or `failed`), `checkpoint`
  (the recorded fact), the attributed `paths`, and `rejections[{code,
  message, remedy}]`.
- Exit codes: `0` recorded; `1` refused or not persisted; `2` argument
  errors, including a blank summary or next action.

**`work checkpoint show`** prints the latest checkpoint, the separately
observed current state (freshness, current recoverability, local HEAD,
working tree), warnings, non-destructive recovery steps, and the full
history.

**`work continue`** lets a successor take over active work whose executor
disappeared. The successor gets a new execution whose parent is the
predecessor, and a `work.continued` event is recorded. The command refuses
dirty checkouts, the caller's own run, and non-active work.

**Additive output.** `work context` and `status` gain an additive
`continuity` block. `--offline` never contacts a remote. Each block carries
`telemetry.executions[]`, the item's executions with their telemetry
segmentation (`execution-level`, `step-level` or `step-level-adopted`),
`stepTrackingStartedAt` and any execution-scoped period before it; `--text`
prints them under `TELEMETRY SEGMENTATION`. See "Effective-current step
telemetry" in [`development-telemetry.md`](development-telemetry.md).

**Guards.** Where `workProtocol.continuity.requireDurableCheckpoint` is set:

- `work complete` requires a current, re-verified checkpoint for meaningful
  Git-backed work;
- `work block` after un-checkpointed work needs a checkpoint or
  `--unrecoverable-reason`.

See "Durable checkpoints and continuity" in [`work-protocol.md`](work-protocol.md).

### `work group`

```
ros work group show GROUP-ID [--config FILE] [--json]
ros work group create --id GROUP-ID --member ID [--member ID]* --occurred-at TIMESTAMP
                      [--kind KIND] [--execution-repository NAME] [--cross-repository]
                      [--shared-context TEXT]* [--architecture-note TEXT]* [--reason TEXT]
                      [--config FILE] [--dry-run] [--json] [IDENTITY]
ros work group add --id GROUP-ID --member ID --occurred-at TIMESTAMP [--reason TEXT]
                   [--config FILE] [--dry-run] [--json] [IDENTITY]
```

Durable, human-declared execution groups (PRX-GRP-073, phase two), stored in
`.ros/work/groups.json` and read by every `plan` command exactly as planner
configuration's `grouping.groups` (a `--config` file declaring the same group
ID shadows the stored one for that run). A group records membership only:
no command in this family changes a member's lifecycle state, evidence,
attribution or telemetry, or writes any file but `groups.json`.

**`work group create`** declares a group. IDs look like
`GROUP-<AREA>-<SEQUENCE>`. It refuses an existing group ID, unknown work items,
terminal (`complete` or `abandoned`) items, and an item that executes in
another repository (its `grouping.executionRepositories` entry in `--config`)
unless `--cross-repository` is given. The group's execution repository
defaults to this repository. The creator's identity is recorded with a
`created` history entry. `--dry-run` decides and reports without writing.
`validate` checks stored groups (unknown or repeated members, duplicate IDs,
malformed records); a member that completes after joining is partial
completion, not a finding.

**`work group add`** adds one member under the same admission rule as
`create` (known, not terminal, same execution repository unless the group is
cross-repository) and refuses an item that is already a member or a group that
does not exist. It appends a `member-added` history entry with the actor and
optional reason; the item itself is untouched. `validate` reports members that
the membership history does not explain.

**`work group show`** is read-only. It prints the stored declaration and, for
each member, its own recorded state (live context, else backlog) and the
planner's reading of it (`planningState`, `status`), with partial-completion
progress (`1 of 3 complete; blocked: ...`), which blocked members gate which
others (`gatedBy`/`gates`, from hard dependencies, including those in
`--config`), the execution repository and the architecture notes. A member
the planner cannot see is reported `unknown`. An unknown group exits `1`.

Exit codes: `0` success; `2` argument errors (missing flags, invalid IDs,
unknown kind, no members); `1` refusals and persistence failures. With
`--json` every command prints one document
`{ "command", "schemaVersion": 1, "groupId", "status", ... }` where `status` is
`created`, `added`, `shown`, `dry-run`, `rejected` (with `rejections[]` of `code`, `message`,
`remedy`) or `failed` (with `failure`).

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
--actor-kind agent|human|automation|unknown|x-...   (env ROS_ACTOR_KIND)
--agent ID | --actor ID                              (env ROS_ACTOR; stable identity)
--provider P --model M --model-version V --runtime R --runtime-version V
--session S --conversation C --run R --subagent ID   (env ROS_TELEMETRY_*)
```

An invalid `--actor-kind` is an argument error (exit `2`).

| Command | Purpose |
|---|---|
| `provenance identity [--json]` | who this process is recorded as, how that was determined, and the active executions |
| `provenance record --path PATH\|--id ID --operation OP [--reason T] [--evidence REF]* [--derived-from REF]* [--execution EXE] [--occurred-at TS] [--json]` | attribute a contribution to an artifact's front matter and append an `artifact.contributed` event; identity is inherited from the active execution; idempotent |
| `provenance show ID\|PATH [--json]` | contributors, involvement label, lineage (sources and derivatives), legacy-declared authors, and events |
| `provenance audit [--json]` | coverage, per-actor summaries, flattened contribution facts for metrics, and every finding including informational ones; exits `1` on errors |

`validate` reports provenance errors (which fail validation) and provenance
warnings (which do not). In `--json`, warnings carry `"severity":"warning"`,
and `valid` reflects errors only.

## Execution and installation commands

`praxis execution ...` runs Ordo's execution contract: envelopes, worktree
per execution, the step ledger and receipts, mutation boundaries, evaluator
identity and legal actions. See [`execution-runtime.md`](execution-runtime.md).

`praxis installation register|remove|verify|reconcile|list|status|history`
registers installations with Project Administration's inventory. See
[`installation-registration.md`](installation-registration.md).

`praxis` is the canonical command. The native release installs `praxis` and
`ros`, the .NET global tool installs `praxis`, and `./praxis` runs this
checkout. The npm package is no longer published (`DF-ROS-2026-A044`). Every
name runs the same F# CLI.
