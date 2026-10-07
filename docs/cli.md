# CLI reference

The canonical public interface is the `praxis` command (compatibility alias
`ros`, also kept as a scaffolded project's `./ros`), a self-contained F#
binary. Install it from the native bundle (see
[`native-installation.md`](native-installation.md)) or as the .NET global tool
`EchelonFoundry.Praxis`; see [`installation.md`](installation.md).

```bash
praxis <command>
```

All five lifecycle commands, and the repository commands below them, are the
same F# CLI. No lifecycle decision is made in a launcher: launchers
(`praxis`, a project's `./praxis`, and the `ros`/`./ros` compatibility aliases)
only start it, and no Node.js or npm is involved (`DF-ROS-2026-A049`).

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
telemetry counts, upstream synchronization freshness, and the installation.

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
praxis sync status [--json] | sync check [--start] [--json]
praxis work <capture|list|ready|show|start|resume|block|complete|reconcile|checkpoint|continue|update|attach|context|...>
praxis add "..."
praxis step <plan|begin|resume|complete|block|abandon|record|availability|checkpoint|link|list|show>
praxis telemetry <show|summary|finalize|record|ingest|classify|start|adapters|validate>
praxis reconcile --envelope FILE
praxis inbox <list|show|claim|derive|complete|release|reject|recover>
praxis adapter <call|publish>
praxis provenance <identity|record|show|audit>
praxis plan <analyze|simulate|compare|explain|replay|freshness|groups|explain-group>
```

Run `praxis --help` for the full argument list, and see
[`work-protocol.md`](work-protocol.md),
[`development-telemetry.md`](development-telemetry.md),
[`work-adapter-contract.md`](work-adapter-contract.md) and
[`agent-provenance.md`](agent-provenance.md) for what they mean.

### `sync status`, `sync check`

```
praxis sync status [--json]
praxis sync check [--start] [--json]
```

`sync check` performs the configured fetch-only upstream check and records its
state in per-worktree Git metadata. `--start` resets the session's starting
upstream commit and is used once at agent startup. A routine check retains that
baseline. The default policy is `origin/main` with a 30-minute maximum age,
configured at `workProtocol.upstreamSync` in `ros.json`.

`sync status` is local and read-only: it never fetches. Both commands report
freshness, HEAD and start/current upstream commits, ahead/behind counts,
incoming/local/overlap paths, whether integration is required, and whether
final validation is safe against the fetched snapshot. A failed fetch exits
`1` and does not reset the last-success clock; invalid arguments exit `2`.
Disabled policy exits `0` and reports `outcome: "disabled"`.

The check never pulls, merges, rebases, switches, stashes, resets, discards,
commits, pushes, or edits working files. Agents integrate an advanced upstream
deliberately at a safe boundary and rerun affected validation. “Current” means
the latest successfully fetched snapshot within the configured window, not a
guarantee that the remote cannot move after the check. See [Upstream
synchronization and bounded drift](work-protocol.md#upstream-synchronization-and-bounded-drift).

### `repository identity`, `instance`

`praxis repository identity [set ...]` shows or records the repository's
stable identity (`ros.json` `repository.identity`: provider, provider
repository ID, `owner/repo` locator), from which canonical work-item
identities (`owner/repo:ID`, structurally `{repositoryId, repository,
localId}`) are derived. `praxis instance [show|init|projection|register]`
shows, creates, projects and optionally registers the Praxis instance
identity (`.praxis/instance.json`); `init` and `upgrade` create it when it is
missing. See [`identity.md`](identity.md) for the verification states, the
clone/template rules, the legacy migration rule and the `workProtocol.branchPolicy`
setting.

### `reconcile --envelope`

Validates one runtime-free envelope, dispatches its ordered work requests through the native work/evidence rules, imports optional execution steps into canonical telemetry, commits only the resulting canonical paths, and creates `praxis-reconcile/<transaction-id>`. Exit `0` means applied or an already-checkpointed replay, `1` means an interrupted transaction remains pending and is safe to retry, and `2` means the envelope was rejected without canonical mutation. `inbox list` inventories pending envelope/document inputs. See [`fallback-reconciliation.md`](fallback-reconciliation.md).

`praxis inbox` runs the input-document lifecycle (DER-08..10): `list [--json]` reports pending inputs and every claim; `claim PATH` moves a pending input into `.praxis/processing/<claim-id>/`; `derive CLAIM-ID --kind {requirement|decision|constraint|evidence|risk|question|reference} (--id ARTIFACT-ID|--path PATH) --summary TEXT [--locator TEXT]` records what the input yielded; `complete CLAIM-ID [--no-derivations REASON]` archives it to `.praxis/processed/` only once every derived change is committed in HEAD (and every derived artifact names the claim in `derived_from`); `release CLAIM-ID --reason TEXT` returns it to the inbox; `reject PATH|CLAIM-ID --reason TEXT` moves it to `.praxis/rejected/documents/`; `recover` finishes any interrupted operation (every mutating command runs it first). Exit `0` success, `2` refused, `1` storage failure that is safe to retry. See [`fallback-reconciliation.md`](fallback-reconciliation.md) ("Input documents").

`praxis telemetry adapters` lists the ingest adapters. Besides production's
catalog it includes the F#-only `anthropic-claude-session`, which derives
session metrics (repeated and governance reads, time to first code change,
active time, requests, tool calls, compactions, tokens) from a Claude Code
transcript: `praxis telemetry ingest ID --adapter anthropic-claude-session
--input SESSION.jsonl`. Platform-reported cost is recorded with `praxis telemetry
record ID --metric cost.execution_total --value N --currency USD --quality
observed`; `praxis plan` reads both (see
[`development-telemetry.md`](development-telemetry.md) and
[`planning.md`](planning.md)).

### `plan`

```
praxis plan analyze   [--json]
praxis plan simulate  [--for baseline|speed|balanced|cost|max-parallel] [--max-concurrency N]
                      [--budget AMOUNT [--currency CODE]] [--deadline 4h|90m] [--details] [--json]
praxis plan compare   [--max-concurrency N] [--json]
praxis plan explain   ID [--json]
praxis plan replay    [--record] [--details] [--json]
praxis plan freshness --plan FILE [--json]
praxis plan groups    [--json]
praxis plan explain-group GROUP-ID [--json]
praxis plan simulate --groups [--max-concurrency N] [--json]
praxis plan compare  --groups [--max-concurrency N] [--json]
     common: [--observations FILE] [--observe-ci] [--config FILE] [--as-of TIMESTAMP]
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
group exists and what would change it (`DF-ROS-2026-A047`). `--observe-ci`
reads GitHub check runs (through `gh`) for checkpoints that wait on CI; it is
the only network read and is off by default, and a source it cannot read is
reported unavailable. `replay --record` appends the replay result to
`.ros/planning/calibration.jsonl` (once per work state and planner version);
it is the only planning write. See [`planning.md`](planning.md).

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

### `work checkpoint`, `work checkpoint show`, `work continue`

```
praxis work checkpoint --id ID --occurred-at TIMESTAMP --summary TEXT --next-action TEXT
                       [--step STEP-ID] [--execution EXE-ID] [--json] [IDENTITY]
praxis work checkpoint show ID [--json] [--offline]
praxis work continue --id ID --occurred-at TIMESTAMP [--json] [IDENTITY]
praxis work context [ID] [--text] [--offline]
praxis work block ... [--unrecoverable-reason TEXT]
praxis work abandon --id ID [--id ID]* --occurred-at TIMESTAMP --reason TEXT [IDENTITY]
praxis status [--json] [--verbose] [--offline]
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
ros work group cost GROUP-ID [--json]
ros telemetry ingest GEX-ID --input FILE [--adapter NAME] [--json]
ros work group list [--status STATUS] [--member ID] [--repository NAME] [--config FILE] [--json]
ros work group show GROUP-ID [--config FILE] [--json]
ros work group add    --group GROUP-ID --member ID --occurred-at TIMESTAMP [--config FILE]
                      [--reason TEXT] [--dry-run] [--json] [IDENTITY]
ros work group remove --group GROUP-ID --member ID --occurred-at TIMESTAMP [--allow-empty]
                      [--reason TEXT] [--dry-run] [--json] [IDENTITY]
ros work group checkpoint --group GROUP-ID --occurred-at TIMESTAMP --summary TEXT
                      --next-action TEXT [--decision TEXT]* [--dry-run] [--json] [IDENTITY]
ros work group create --group GROUP-ID --member ID|OWNER/REPO:ID [--member ...]* --occurred-at TIMESTAMP
                      [--kind KIND] [--origin ORIGIN] [--shared-context TEXT]*
                      [--architecture-note TEXT]* [--execution-repository NAME]
                      [--cross-repository] [--home-repository OWNER/REPO]
                      [--dependency CONSUMER=PRODUCER[@complete|merged|released:TAG]]*
                      [--config FILE] [--reason TEXT] [--dry-run] [--json] [IDENTITY]
ros work group link   --group GROUP-ECHELON-AREA-SEQ --home OWNER/REPO --member ID
                      --occurred-at TIMESTAMP [--dry-run] [--json] [IDENTITY]
```

Durable execution groups (PRX-GRP-073, phase two; `PRAXIS-GROUP-01..05`),
recorded in `.ros/work/groups.json`. Group commands write that file and
nothing else: membership never changes a member's lifecycle state, evidence,
attribution, telemetry or checkpoints. `plan` merges stored groups into
`grouping.groups`, so a stored group is read exactly as the same declaration
in planner configuration; a group that `--config` also declares keeps its
configured form. `validate` checks stored groups (readable records, unique
IDs, recorded members, no repeated member, a history that begins with the
creation, and no empty group without an explicit empty removal) and
re-validates every stored group checkpoint (local commit equals the recorded
remote commit, summary and next action present, each member under one
standing, and every referenced member checkpoint is one that member itself
recorded).

**`work group create`** declares a group. Group IDs are
`GROUP-<AREA>-<SEQUENCE>` in upper case. Every member must be a recorded work
item (queue or live context) that is not `complete` or `abandoned`, named
once, and must execute in the group's repository (`--execution-repository`,
default this repository) unless `--cross-repository` is given; an item's
repository comes from the planner's own rule (`Grouping.executionLocation`):
its `grouping.executionRepositories` entry in `--config`, else an unknown
external repository when its description names one, else this repository. `--kind` and `--origin` take the planner's codes (default
origin `human-declared`). The creation is recorded with the resolved actor,
`--occurred-at` and `--reason`. Every problem is reported together and nothing
is written: invalid arguments exit `2`, refusals (unknown, terminal or
foreign-repository member, an existing group with a different declaration)
exit `1`. Repeating a `create` whose declaration is identical to the recorded
one succeeds unchanged (below). `--dry-run` shows the group without writing it.

**`work group add`** adds one member by the same join rule as `create`
(recorded, non-terminal, same execution repository unless the group is
cross-repository) and refuses a group that is not recorded. Adding a current
member succeeds unchanged. The history entry records who added it (resolved
actor), when and why. The member's own record is untouched.

**`work group remove`** removes one member, whatever its state, and refuses
an item that was never a member; removing a member whose removal is already
recorded succeeds unchanged. Removing the last member is refused unless
`--allow-empty` is given; the removal is then recorded as `explicitEmpty` and
`validate` accepts the empty group. The history entry records who removed it.
The item's lifecycle state, evidence and attribution are untouched.

**`work group checkpoint`** records a group checkpoint (PRX-GRP-044) after an
architectural or implementation milestone: the completed, active, blocked,
remaining and abandoned members, shared decisions (`--decision`, repeatable),
summary and next action, and the branch and commit. It requires the same
durable-checkpoint verification as `work checkpoint` (local HEAD equals its
upstream remote head, read from the remote itself, and no meaningful
uncommitted change; the same rejection codes) and the same ownership rule: at
least one member must be active (`no-active-member`) and at least one active
member's execution must resolve to the caller, as `work checkpoint` resolves
it (`no-own-execution`). It is refused otherwise, with exit `1`. It
references each member's own latest durable checkpoint by ID and commit and
never writes, replaces or supersedes one; it records no paths and no
execution, so no member claims another's changes (PRX-GRP-043). `work group
show` prints the latest group checkpoint for later member executions.

**`work group show`** is read-only (no lock, no write): the group's
declaration, each member's own recorded state and planning state (from the
planner's analysis; `--config` is passed through), partial-completion
progress (`k of n complete`; `progress.complete` is true only when every
member completed on its own evidence), the work items each member still waits
on, blocked members and the open members they gate, shared context,
architecture notes and history, the derived group status (`groupStatus`), and
every member that was removed while open (`removedOpen`, with its removal
reason). An unknown group exits `1`.

**`work group list`** is read-only (PRX-GRP-110): one row per group, sorted by
ID, with its kind, origin, home and execution repository, `crossRepository`,
member count, derived `groupStatus`, progress, removed-open members, execution
mode and latest group checkpoint time. `--status`, `--member` and
`--repository` filter the rows (all given filters must hold); `--config` is
passed to the planner.

**Group status is derived, never stored** (PRX-GRP-103, PRX-GRP-116). In
order of precedence: `complete` (every current member completed on its own
evidence), `unknown` (some member's state is not known), `partially-complete`
(some member completed), `blocked` (every open member is blocked or waits on a
blocked prerequisite), `active` (some member is active), `not-started`. There
is no `work group complete`: no command or flag can mark a group done. A
member removed while open never counts toward `k of n` and is reported
`removed-open`; an abandoned member is reported `abandoned`, never complete.

**Idempotency** (PRX-GRP-114). A repeated request whose end state already
holds (`create` of an identical declaration, `add` of a current member,
`remove` of an already-removed member) exits `0` with `"status": "unchanged"`
and `"changed": false`, and appends no history. Every mutation document
carries `changed`. A request that conflicts with recorded state is refused
with exit `1`. Concurrent mutations are serialized by the store lock.

**Audit** (PRX-GRP-113). Every change appends one history entry with the
operation, member, time, resolved actor, reason, explicit-empty flag, the
caller's active execution (`executionId`, when it has exactly one) and the
member's state when it joined or left (`memberState`). History is
append-only: `validate` refuses a store whose history for a group recorded at
`HEAD` (or at `$ROS_BASE_REF`, when set) was rewritten, reordered or truncated,
or whose group vanished. Group commands write only the group store, so no
`work.group.*` event is appended to `.ros/events` (PRX-GRP-115 outranks that
PRX-GRP-113 SHOULD; the group history is the audit trail).

**Cross-repository groups** (PRX-GRP-100..109). A group whose members live in
several repositories (a portfolio sweep: one brief, one pull request per
repository, one consolidated status table) is created with
`--cross-repository` and an ID in the reserved area `GROUP-ECHELON-<AREA>-<SEQ>`;
a repository-local group may not use that area. Its **home** is the repository
where `create` ran (`homeRepository`, from `--home-repository` or the
checkout's `origin` remote, `owner/repo`); only the home holds the group record,
and the home never changes. Members of other repositories are named
`owner/repo:WORK-ID` (a bare ID is a home item; a qualified name of the home is
stored bare). Each member is governed only in its own repository: its own
queue entry, executions, evidence, checkpoints, pull request and completion.

- **Observation, never copying.** The home reads a member repository's Praxis
  state read-only from a local clone at a fetched ref (`git show REF:.ros/...`),
  configured in planner configuration under
  `grouping.crossRepository.repositories` (`{"owner/repo": {"path": "../repo",
  "ref": "origin/main"}}`; `ref` defaults to `origin/HEAD`). Planner
  configuration is `--config FILE`, else the `planner` object of `ros.json`.
  Each observation records repository, ref, commit, `observedAt`, how current
  the ref was (`sourceAsOf`: its reflog, else its commit time) and the method
  (`git-ref`). Nothing is fetched, written, committed or pushed in another
  repository. An observation older than `maxObservationAgeMinutes` (default
  1440) is `stale`; a repository that is not configured, missing, a clone of a
  different repository, without Praxis, with state this version cannot parse,
  or denying access makes its members `unknown` with the reason (`unreachable`,
  `praxis-not-installed`, `unsupported-schema`, `access-denied`). A stale or
  unknown member is never complete; the rest of the view still renders.
- **Joining.** A qualified member is checked by observation: a terminal or
  unrecorded item is refused; an unobservable one joins with
  `verified: false` and a warning (`verifications` in the group record).
- **Order.** `--dependency CONSUMER=PRODUCER[@MILESTONE]` records an edge whose
  producer must reach `complete` (default), `merged` (its latest checkpoint
  commit is reachable from its observed ref) or `released:TAG` (the tag exists
  there). `show` reports each edge `satisfied`, `waiting` or `unknown`, and each
  consumer's planning state; it never changes the consumer's lifecycle state.
  Edges must join members and may not form a cycle, across repositories.
- **References.** `work group link`, run in a member's own repository, records
  the only group data that repository holds: an immutable reference on its
  item (group ID and home). Repeating it is `unchanged`; another home is
  refused. A membership whose member repository holds no reference, or a
  reference the observed home does not list, is `unlinked`: shown by `show`
  and reported by `validate` as a warning.
- **Status table.** `show` is the consolidated sweep table: per member its
  repository, observed state, ref and commit, latest checkpoint, staleness and
  link; progress per repository and overall (`k of n complete`); order edges.
  In a member repository, `show` of a group homed elsewhere prints that
  repository's references and whether the home lists them. Group checkpoints
  in the home record `memberObservations` (labelled observations, never
  verifications of another repository's remote). The planner reports a
  qualified member as `executes-elsewhere` with the repository to act in and
  never schedules it into this checkout (PRX-GRP-108).

**Grouped execution** (PRX-GRP-117, 130..132, 136..138).

```
ros plan execute-group GROUP-ID --occurred-at TIMESTAMP [--member ID]
                       [--mode grouped|independent --reason TEXT]
                       [--independent-member ID --reason TEXT]* [--type TYPE]
                       [--config FILE] [--dry-run] [--json] [IDENTITY]
```

`plan execute-group` is the only `plan` verb that mutates, and only Praxis
state: it begins the next runnable member of the group in its required order
(or `--member`) through the existing `work begin` transition and records a
**group execution** (`GEX-<timestamp>-<suffix>`) in the group record, with the
actor, start time, repository, member order, mode and its basis, opt-outs, and
each member it began linked to that member's own execution. One execution
context carries the members one after another; each member keeps its own
execution, evidence, checkpoints and completion. It never launches an agent,
creates a branch, selects a provider or model, or changes priorities or
dependencies. A member already begun in the open group execution is
`unchanged`; a member that waits on an unsatisfied prerequisite, is not
`ready`, or belongs to another repository is not begun here (the reason is
given). It refuses (exit `1`) an unknown group, a dependency cycle among
members, a group with nothing runnable in this checkout, and a caller who
already owns an open group execution of another group. A group execution
ends when no runnable member remains.

- **Default.** `plan groups` and `plan explain-group` report each group's
  `groupedExecution` qualification: affinity `high` with no `none`/`unknown`
  member pair, `minimumSize`..`maximumSize` runnable members (default 2..6),
  every runnable member in this checkout, no limiting context pressure, and no
  `captured` member; every failed threshold is explained. A qualifying group
  is recommended `grouped` and `execute-group` defaults to grouped mode; any
  other defaults to `independent`, and grouped mode then needs
  `--mode grouped --reason TEXT`. Settings live under
  `grouping.groupedExecution` (`default`, `minimumSize`, `maximumSize`,
  `compactionLimit` 1, `repeatedReadLimit` 25, `elapsedFactor` 1.5). Setting
  `default` to `advisory` is the rollback (PRX-GRP-138): a configuration change
  only.
- **Gates.** A member begun in grouped mode completes only with committed
  `group-analysis` and `group-verification` evidence (see
  `docs/group-analysis-template.md`); members that execute independently
  complete under the normal policy.
- **Opt-outs** (PRX-GRP-132) need a non-empty `--reason`, are recorded with the
  actor and time in the group history (`opted-out`), are shown by `show`,
  `list` and `explain-group`, and apply only to executions not yet begun. Per
  group: `work group create|add --execution-mode independent --reason TEXT` or
  `execute-group --mode independent --reason TEXT`. Per item:
  `work group create --independent-member ID --reason TEXT`,
  `work group add --member ID --independent-member ID --reason TEXT`, or
  `execute-group --independent-member ID --reason TEXT`: the member stays a
  member but executes in its own fresh context.
- **Fallback** (PRX-GRP-136). When a member begun in grouped mode records
  `context.compactions` of at least `compactionLimit`, `context.repeated_file_reads`
  above `repeatedReadLimit`, elapsed time above `elapsedFactor` times its upper
  estimate, or an explicit `context-pressure` observation names it,
  `execute-group` records `fallback: independent` with the signal and its
  evidence, ends the group execution, and recommends a fresh independent
  execution (or a split) for each remaining member; the next group execution
  runs independently. Members already active or complete are unchanged. A
  metric that was never recorded never triggers it (unknown is not zero).

**Store and documents** (PRX-GRP-112). `.ros/work/groups.json` is written as
`schemaVersion` 2 (additive fields); a version-1 store is read as-is and is
rewritten only by a mutation, its history intact. Every group command prints
exactly one `{command, schemaVersion, status, ...}` document with `--json`;
unknown values are `null`, never `0`.

### `tutela ingest`, `tutela metrics`

```
praxis tutela ingest --input FILE [--collected-at TIMESTAMP] [--json]
praxis tutela metrics [--repository NAME] [--json]
```

`tutela ingest` records a Tutela security assessment (`schemaVersion` 1) as
an append-only observation in `.ros/telemetry/tutela/observations.jsonl`,
with its repository, ref (resolved to a commit when this repository has it),
collection time and source sha256. Re-ingesting the same document is a no-op.
`tutela metrics` derives security metrics per observation and over time
(`praxis.tutela-metrics/1`). Missing measurements are `unknown` with a reason,
never zero, and there is no single score. See
[`tutela-security-metrics.md`](tutela-security-metrics.md).

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
| `provenance acknowledge-unrecorded --path PATH --reason T [--execution EXE] [--occurred-at TS] [--json]` | acknowledge that no Praxis execution recorded the artifact's creation (DF-ROS-2026-A055): records an `origin-unrecorded` contribution under the acknowledging actor, naming no creator; refused when `created` exists; `validate` reports it as a `NOTE` |
| `provenance audit [--json]` | coverage, per-actor summaries, flattened contribution facts for metrics, and every finding including informational ones; exits `1` on errors |
| `step plan --name NAME --occurred-at TS [--description TEXT] [--classification TYPE]* [--parent STEP] [--execution EXE] [--json]` | add a planned step to the current execution |
| `step begin --name NAME --occurred-at TS [...]` | create and activate a step; use `--parent` for a nested child |
| `step begin\|resume --id STEP --occurred-at TS [--execution EXE]` | activate a planned or blocked step |
| `step complete\|block\|abandon [--id STEP] --occurred-at TS [--reason TEXT]` | transition the explicit step or current active leaf; block requires a reason |
| `step record --metric ID --value N --collected-at TS [--id STEP] [--quality observed\|derived\|estimated] [cost provenance flags]` | record one canonical step-attributed normalized measurement |
| `step availability --metric ID --status supported-unavailable\|unsupported\|unknown --reason TEXT --occurred-at TS [--id STEP]` | record measurement availability without inventing a value |
| `step checkpoint --phase begin\|end (--measurement MEAS\|--snapshot SNAP)+ --occurred-at TS [--id STEP]` | preserve source observations and derive compatible cumulative deltas |
| `step link --kind KIND --value VALUE --occurred-at TS [--source SOURCE] [--id STEP]` | relate sourced engineering evidence without duplicating its canonical record |
| `step list [--execution EXE\|--work-item ID] [--json]` | list ordered steps; JSON is deterministic and includes summaries |
| `step show STEP-ID` | show the canonical step, availability, measurements, duration, nesting, and evidence as JSON |

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
