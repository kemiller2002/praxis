# ROS Work Protocol 1.0

ROS owns the versioned protocol, legal transitions, repository validation, and adapter contract. A consuming repository owns code and evidence. An external project-management service owns work-item truth, prioritization, and portfolio state. Disagreement is reported; no layer silently overwrites another.

## Local protocol

```bash
./ros work begin FEAT-142 --type feature
./ros work context FEAT-142
./ros telemetry show FEAT-142
./ros work block FEAT-142 --reason "waiting for fixture"
./ros work resume FEAT-142
./ros work complete FEAT-142 \
  --evidence implementation=src/feature.js \
  --evidence tests=tests/feature.test.js
./ros validate
./ros validate --json
./ros status
```

The legal semantic core is `ready -> active -> blocked -> active` and `active -> complete`. Local states may be supplied with `--local-state`; `ros.json` maps repository states to the shared semantic vocabulary. Research completion accepts an independent `--conclusion`, including `inconclusive`.

Beginning work automatically starts a segmented execution record under `.ros/telemetry/executions/`; completing work automatically finalizes all active records. Block/resume transitions preserve interruption intervals. Runtime adapters can ingest token, cost, context, agent, tool, and provider-specific observations without changing the work-state protocol. `./ros validate` checks telemetry structure and finalization alongside work attribution. See [`development-telemetry.md`](development-telemetry.md).

For substantial execution, record meaningful plan units with `./ros step begin` and `./ros step complete`. Steps sit inside the active execution; they do not replace work items or requirements. Use nesting only when a parent genuinely contains a subtask. Do not manufacture historical steps for work completed before the capability existed, and do not create command-by-command noise. Work completion fails while a step is planned, active, or blocked, so crash recovery remains explicit.

`work context` is the normal agent entry point. It reports current state, legal next actions, and evidence required for completion. `status` combines compact work state with repository validation and recommended next actions. Validation errors include deterministic repair guidance; `validate --json` provides a stable structured result for agents and CI consumers.

`.ros/context/current.json` is local work context. Its `actor` field is the last actor to transition work. It is not provenance and is never used to attribute anything. `.ros/events/events.jsonl` contains small immutable, idempotently identified semantic events and durable file attribution. Each work event carries `actor`, the structured identity of the process that performed the transition (`kind`, `id`, and `provider`/`model`/`runtime` when applicable). ROS resolves it the same way it resolves the execution's identity and never inherits it from stored context. `./ros provenance record` appends `artifact.contributed` events. Each one records the actor, the execution, the operation, the reason, the evidence, lineage, and the before/after content digests of the attributed artifact. See `agent-provenance.md`. `.ros/telemetry/executions/` contains per-execution observations linked from work context and events. These files do not replace the external work item.

Completion validates configured evidence types and paths before changing state. `./ros validate` rejects meaningful dirty paths when enforcement is enabled and neither active context nor a completed event attributes them. Committed changes that were made without an active work item are repaired with `./ros work reconcile` (see "Post-hoc attribution reconciliation" below), never by touching files. CI is the authoritative enforcement boundary; hooks are optional convenience.

When the Praxis executable genuinely cannot run, an agent may write the approved runtime-free envelope and let `./ros reconcile --envelope FILE` enter the same planners and canonical stores. The envelope is proposed input, not state; do not edit `.ros` files directly. See [`fallback-reconciliation.md`](fallback-reconciliation.md). This is distinct from `work reconcile`, which repairs Git attribution after work was committed without an active work item.

Deterministic housekeeping may use the configured `mechanical` work type. It still requires an explicit work-item identity and event, but the default profile does not require implementation/test evidence for that type.

## Post-hoc attribution reconciliation

Attribution is supposed to be established *while* work happens: begin a work
item, change files, complete it. Sometimes that does not happen. An agent or
a person commits meaningful changes while no work item is active, and a later
`./ros validate` (in CI, with `ROS_BASE_REF` set) reports them:

```text
ERROR src/report.fs:work_items: meaningful change has no active or completed work-item attribution
```

`./ros work reconcile` repairs that gap after the fact, **using Git history as
evidence**, and records that it did so. It is recovery, not a substitute for
the protocol: the reconciled work was *not* attributed when it was done, and
the record says so permanently.

### When to use it

- Meaningful changes are already **committed**, they belong to a real work
  item (or to a real external issue you have captured with `./ros add` /
  `work capture`), and validation reports them as unattributed.
- The commits are in the history of your current `HEAD`.

The work item may be in any lifecycle state except abandoned (active,
blocked, complete, or a captured/ready backlog item). Reconciliation never
transitions it: a completed item stays completed, and its `work.completed`
event is not touched.

### When not to use it

- **To make validation pass for work you cannot honestly assign.** If you do
  not know which work item a change belongs to, find out or capture the
  obligation first. Never invent a work item to absorb changes.
- **Instead of beginning work.** For work you are about to do, run
  `./ros work begin`. Reconciliation is only for work that already happened.
- **For uncommitted changes.** The working tree is not evidence: it has no
  author, no immutable identity, and can change underneath you. Commit the
  changes (the commit is the evidence), then reconcile that commit; or, if the
  work is still in progress, begin the work item.
- **Never touch, rewrite, or recommit files just to manufacture
  attribution.** That destroys the real history of who changed what and when.
  Reconciliation exists so that nobody has to.

### How it works

```bash
# Preview: what would be attributed, from which commits, and why.
./ros work reconcile --id GH-80 --reason "committed while no work item was active" \
  --commit 3f2a9c1 --occurred-at "$(date -u +%Y-%m-%dT%H:%M:%S.000Z)" --dry-run

# Record it.
./ros work reconcile --id GH-80 --reason "committed while no work item was active" \
  --commit 3f2a9c1 --occurred-at "$(date -u +%Y-%m-%dT%H:%M:%S.000Z)"

# Several commits: repeat --commit, or give a BASE..HEAD range.
./ros work reconcile --id GH-80 --reason "three commits made before GH-80 was begun" \
  --range 1a2b3c4..9d8e7f6 --occurred-at "$(date -u +%Y-%m-%dT%H:%M:%S.000Z)"

# Narrow to specific paths (can only remove paths Git proves; never add).
./ros work reconcile --id GH-80 --reason "only the parser change is GH-80" \
  --commit 3f2a9c1 --path src/parser.fs --occurred-at "$(date -u +%Y-%m-%dT%H:%M:%S.000Z)"
```

You never list files by hand: Git establishes them. For every selected commit
ROS reads the commit's own changes against its single parent
(`git diff-tree --raw -M`) and assesses each touched path:

| Change | Paths attributed |
|---|---|
| added, modified, type-changed | the path |
| deleted | the deleted path |
| renamed | **both** the old and the new path |
| copied | the new path (the source is unchanged) |

Each path is then `reconciled`, `already attributed` (a contemporaneous event
already names it), `already reconciled` (this work item already reconciled
this exact commit's change), `not meaningful` (excluded by the same
`meaningfulPaths`/`ignoredPaths` configuration validation uses), or
`not selected` (outside `--path`). Only `reconciled` paths are recorded.

The command **fails closed**, recording nothing, when the evidence cannot be
established reliably:

- the work item does not exist, or was abandoned (reconciliation never creates
  a work item);
- `--reason` or Git evidence is missing, or `--occurred-at` predates a selected
  commit (a reconciliation is dated when it happens, never backdated);
- a revision does not resolve, or is ambiguous (a short SHA with several
  matches, a name that is both a branch and a tag);
- a commit is not in the history of `HEAD` (another branch, or history that a
  rebase discarded);
- a range is empty, uses `...`, or its BASE is not an ancestor of its HEAD
  (which would sweep in unrelated history);
- a commit is a merge (its changes cannot be assigned to one line of work
  unambiguously; reconcile the commits it merged instead);
- a commit is a shallow-clone boundary (its parent, and so its changes, are
  unknown; fetch full history first);
- a `--path` is not changed by any selected commit;
- a selected change is already reconciled to a **different** work item
  (conflicts are reported, never overwritten);
- Git is unavailable.

Exit codes follow the CLI contract: `0` reconciled, nothing to reconcile, or a
successful `--dry-run`; `1` rejected; `2` invalid arguments. `--json` prints
the full assessment (`status` is `reconciled`, `planned`, or
`nothing-to-reconcile`).

### What is recorded

One `work.attribution.reconciled` event is appended to
`.ros/events/events.jsonl`; nothing else changes. The original events,
including any `work.completed` event of the work item, and work context are
never modified. The event records:

- `workItem`: the work item that receives attribution;
- `attribution: "post-hoc"`: this was recovered afterwards, not established
  during the work;
- `paths`: the reconciled paths;
- `reason`: why attribution was missing;
- `occurredAt`: when the reconciliation happened (compare with the commit
  dates to see how late it was);
- `actor`: **who performed the reconciliation**, resolved exactly as for
  every work transition (see `agent-provenance.md`);
- `gitEvidence`: the `HEAD` it was verified against, each `--commit`/`--range`
  selector as given and as resolved, and every justifying commit with its
  parents, subject, **original Git author and committer**, and the exact
  per-path changes (status, path, rename source, and resulting blob id).

`./ros work show ID` lists a work item's reconciliations (with `valid`
reporting whether validation accepts each one), so post-hoc attribution stays
visible next to the item it was reconciled to.

Three identities stay separate: the change author (Git's author/committer),
the work item, and the reconciliation actor. The reconciliation actor is
never presented as the author of the change.

Re-running the same reconciliation is safe: every `(commit, path)` the work
item already reconciled is reported as `already reconciled`, and when nothing
new remains no event is written. The same change can never be reconciled to
two work items.

### How validation behaves afterwards

`./ros work validate` and `./ros validate` accept a reconciled path only while
**its current content is exactly what the Git evidence recorded** (the
recorded blob, or absence after a deletion or as a rename source). Reconciling
a change never pre-authorizes later edits: change that path again without an
active work item and it is reported again, and the new commit needs its own
attribution. Paths the reconciliation did not cover still fail.

Validation also checks every reconciliation event itself and reports a
`work_reconciliation` finding (and attributes nothing) when an event's
`eventId` no longer matches its content, it names an unknown or abandoned work
item, lacks an actor or reason, is not marked `post-hoc`, is dated before its
own commits, attributes a path its recorded commits do not justify, relies on
a merge commit, or claims a change another event reconciled to a different
work item. Reconciliation events must never be written or edited by hand.

Known limits: attribution remains path-based for contemporaneous events, as
before; reconciliation cannot judge whether a commit *semantically* belongs to
the named work item, so the `reason`, the recorded actor, and the reviewable
evidence are what make a wrong reconciliation detectable; symbolic links and
submodules are not matched by content and so fail closed.

## Local backlog

Beginning a work item with `work begin` requires an ID to already exist. The
local backlog is a cheap, repository-owned staging area for work that has not
been assigned one yet -- captured ideas, discovered obligations, follow-ups --
with its own small lifecycle: `captured -> ready -> {blocked, abandoned}`.

```bash
./ros add "Investigate state payload growth" --tag wasm,state --priority high
./ros work                       # list the unified backlog + in-flight queue
./ros work ready                 # query: items with no blocker
./ros work ready WI-0001         # mutate: captured/blocked -> ready
./ros work show WI-0001
./ros work start WI-0001         # requires ready; delegates to `begin`
./ros work block WI-0001 --reason "waiting on benchmark"
./ros work done WI-0001 --evidence implementation=... --evidence tests=...
./ros work abandon WI-0002 --reason "no longer relevant"
```

Canonical storage is `.ros/work/queue.json`; `.ros/work/queue.md` is a
generated human-readable projection, and `.ros/work/items/<ID>.md` is an
optional free-form detail file `work show` will include when present.

The backlog is **not** a second work-item authority. `work start` requires
`ready`, then delegates directly to the existing `begin` transition above --
from that point the in-flight record in `.ros/context/current.json` is
authoritative, and `work list`/`work show` always prefer its live state over
the backlog's own status field. `work block`/`work ready` on an ID already
being executed dispatch to the existing in-flight transitions, unchanged.
See [`DF-ROS-2026-A008`](https://github.com/kemiller2002/repository-operating-system/blob/main/research/decisions/DF-ROS-2026-A008--repository-local-work-backlog.md)
for why this stays a staging layer rather than repository-owned work-item
authority (that boundary belongs to the external system; see below). For a
worked, example-heavy walkthrough of every command, see
[`work-backlog-guide.md`](https://github.com/kemiller2002/repository-operating-system/blob/main/docs/work-backlog-guide.md).

## Adapter contract

The stable executable interface is `getWorkItem`, `transitionWorkItem`, and `publishRepositoryEvent`. Protocol 1.0 implements a file-backed adapter for conformance tests:

The normalized, testable contract is defined in [`work-adapter-contract.md`](work-adapter-contract.md). Its initial executable operations are `getWorkItem`, `transitionWorkItem`, and `publishRepositoryEvent`; broad listing is deferred.

```bash
./ros adapter publish --target .ros/mock-project-store/events.jsonl
```

Event IDs make retries idempotent. Successful local publication creates `.ros/publications.json` receipts without mutating immutable events. A write error returns failure and creates no success receipt; ROS never treats failure or an unknown remote outcome as success. Production adapters must add authentication, authorization, repository identity checks, version negotiation, retry policy, and explicit `success|failure|unknown` outcomes.

## Adoption and versioning

Initialize a repository with `ros-bootstrap init`, configure `repository` and `workProtocol` in `ros.json`, and call `./ros validate` in CI. Repositories pin a package/protocol version. Breaking semantic or event-schema changes require a new major protocol version; additive evidence types and local mappings are compatible minor changes.

Deferred: remote reads and transitions, signed events, review/approval transitions, commit graph indexing, global aggregation, telemetry publication/retention, and UI. These belong behind the adapter or in the external project-management system—not in ROS core.
