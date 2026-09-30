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

The legal semantic core is `ready -> active -> blocked -> active` and `active -> complete`. Durable checkpoints and `work continue` (below) add evidence and a new execution; they are not lifecycle states. Local states may be supplied with `--local-state`; `ros.json` maps repository states to the shared semantic vocabulary. Research completion accepts an independent `--conclusion`, including `inconclusive` (the default). Any other work type records a `--conclusion` only when one is supplied; it is never accepted and then dropped.

Beginning work automatically starts a segmented execution record under `.ros/telemetry/executions/`; completing work automatically finalizes all active records. Block/resume transitions preserve interruption intervals. Runtime adapters can ingest token, cost, context, agent, tool, and provider-specific observations without changing the work-state protocol. `./ros validate` checks telemetry structure and finalization alongside work attribution. See [`development-telemetry.md`](development-telemetry.md).

`work context` is the normal agent entry point. It reports current state, legal next actions, and evidence required for completion. `status` combines compact work state with repository validation and recommended next actions. Validation errors include deterministic repair guidance; `validate --json` provides a stable structured result for agents and CI consumers.

`.ros/context/current.json` is local work context. Its `actor` field is the last actor to transition work. It is not provenance and is never used to attribute anything. `.ros/events/events.jsonl` contains small immutable, idempotently identified semantic events and durable file attribution. Each work event carries `actor`, the structured identity of the process that performed the transition (`kind`, `id`, and `provider`/`model`/`runtime` when applicable). ROS resolves it the same way it resolves the execution's identity and never inherits it from stored context. `./ros provenance record` appends `artifact.contributed` events. Each one records the actor, the execution, the operation, the reason, the evidence, lineage, and the before/after content digests of the attributed artifact. See `agent-provenance.md`. `.ros/telemetry/executions/` contains per-execution observations linked from work context and events. These files do not replace the external work item.

Completion validates configured evidence types and paths before changing state. `./ros validate` rejects meaningful dirty paths when enforcement is enabled and neither active context nor a completed event attributes them. Committed changes that were made without an active work item are repaired with `./ros work reconcile` (see "Post-hoc attribution reconciliation" below), never by touching files. CI is the authoritative enforcement boundary; hooks are optional convenience.

Deterministic housekeeping may use the configured `mechanical` work type. It still requires an explicit work-item identity and event, but the default profile does not require implementation/test evidence for that type.

## Durable checkpoints and continuity

**An executor session is disposable. Repository state and Praxis state are
the continuity boundary.** No meaningful completed work may exist only in an
executor's local environment. Requirement `RQ-ROS-2026-A022`; design
`DF-ROS-2026-A042`.

### Five different things

| Term | What it is | Durable? |
|---|---|---|
| commit | a Git object in one checkout | no: it dies with the checkout |
| pushed commit | a commit a remote holds | yes, but nobody verified it or recorded what it means |
| verified durable checkpoint | `work checkpoint`'s record that HEAD == the checkpoint commit == the upstream remote branch head, with no meaningful uncommitted work, plus what was completed and what comes next | yes, and Praxis verified it itself |
| historical checkpoint | that record, as a `work.checkpointed` event | an immutable fact about time T; never rewritten |
| currently recoverable checkpoint | a historical checkpoint the remote still carries *now* | observed at read time (`work context`, `status`) |

Telemetry has its own distinction. **Execution-level** telemetry is
attributed to an execution as a whole. **Step-level** telemetry is
attributed to a step of that execution. **Unavailable historical step
attribution** is the truthful state of usage recorded before step tracking
was adopted. New observability is effective-current: adopting steps midway
never restarts work, and earlier usage is never guessed into steps. See
"Effective-current step telemetry" in
[`development-telemetry.md`](development-telemetry.md).

Staging, stashes, local commits, editor state, transcripts and local
telemetry are never checkpoints. A checkpoint is evidence about
recoverability, never a lifecycle state: `ready`, `active`, `blocked` and
`complete` are unchanged.

### Recording a checkpoint

```bash
git commit -am "Implement capability boundary"   # you decide what is coherent
git push                                           # Praxis never pushes for you
./ros work checkpoint --id FEAT-142 --occurred-at "$(date -u +%Y-%m-%dT%H:%M:%S.000Z)" \
  --summary "Implemented capability boundary" \
  --next-action "Implement Rust consumer fixture" [--step STEP-ID] [--json]
git add .ros && git commit -m "praxis: checkpoint FEAT-142" && git push
```

Praxis locates the active work item and resolves **your own** execution
(`--execution EXE-...` when several could be yours). It then reads the
branch, HEAD and upstream, and reads the remote branch head from the remote
itself (`git ls-remote`, with prompts disabled and a bounded timeout). It
accepts the checkpoint only when all three are the same commit and no
meaningful uncommitted change exists. It never commits, stages, pushes,
fetches, merges, rebases, resets, stashes, force-pushes, discards or
switches branches.

Each refusal has a stable code and a non-destructive remedy:

- `blank-summary`, `blank-next-action` (exit 2)
- `work-item-not-found`, `work-item-not-active`
- `missing-execution`, `ambiguous-execution`, `execution-refused`, `invalid-step`
- `not-git-repository`, `git-unavailable`, `malformed-git-response`, `unknown-git-state`
- `no-head`, `detached-head`, `no-upstream`
- `remote-missing`, `remote-unreachable`, `remote-branch-missing`
- `local-ahead`, `remote-ahead`, `diverged`, `not-remotely-visible`
- `uncommitted-changes`
- `persistence-failed`

An unreachable or unknown remote is never success.

Paths excluded by `meaningfulPaths`/`ignoredPaths` (Praxis state, registries,
installation bookkeeping) never block a checkpoint. Paths in the context's
`baselineDirtyPaths` are neither claimed nor a false refusal. Committing the
Praxis state after a checkpoint does not "move past" it: freshness compares
meaningful paths only.

The event records the actor, the execution, the optional step, the full
checkpoint, and the meaningful `paths` this item's own commits changed since
its previous checkpoint (or its start commit). Those paths keep
contemporaneous path attribution intact when work is committed before `work
complete`. The context keeps only `latestCheckpoint`; history lives in the
event log and is never rewritten.

**Other items' work is never claimed (PRAXIS-CONT-12).** On a branch that
several work items share, or after merging the default branch, the commits
after a checkpoint include other items' work. A commit belongs to *other*
items when every meaningful path it changed is already claimed by an item
whose recorded evidence contains it: a `work.checkpointed` event whose commit
descends from it, or a Git-evidenced `work.attribution.reconciled` event that
names it. Merge commits carry no change of their own. Such commits are not
attributed to the checkpointing item, do not make its checkpoint stale, and
do not stop it from completing or blocking. The evidence is recorded Praxis
claims and Git ancestry only, never authors, messages or timing. Anything no
other item has claimed still counts as the item's own work, as does a commit
that mixes claimed and unclaimed paths, so un-checkpointed work is never
excused. When no other item's evidence covers any commit in the range, or the
commit history cannot be read, the result is the plain difference, as
before. Work done before Praxis recorded checkpoints has no such evidence;
record it with `work reconcile` so later checkpoints stop claiming it.

### Reading continuity

`./ros work context ID` adds a `continuity` block, and `--text` renders it for
people. `./ros work checkpoint show ID` adds the full history. `./ros status`
adds `continuity.warnings`. `--offline` never contacts a remote and reports
its state as unknown. The block reports:

- the historical checkpoint (`checkpoint.status` is always `verified`, with
  its commit, branch, remote, remote branch, time, execution, step, summary
  and next action);
- `checkpoint.currentRecoverability`, observed now: `at-remote-head`,
  `contained-without-meaningful-change`, `remote-advanced`,
  `remote-moved-ancestry-unknown`, `not-contained`,
  `remote-branch-missing`, `remote-unreachable` or `unknown`;
- `freshness`: `none`, `current`, `commits-after-checkpoint`,
  `uncommitted-changes-after-checkpoint`, `remote-unavailable`,
  `remote-moved`, `checkpoint-no-longer-currently-verifiable`,
  `head-diverged-from-checkpoint` or `unknown`;
- warnings stated as observed facts;
- recovery steps derived from the checkpoint. For a dirty checkout they
  stop; they never reset.

### Recovery boundaries (when to checkpoint)

Checkpoint at coherent recovery boundaries:

- after a meaningful implementation slice, or a material implementation
  step;
- before a risky or disruptive change;
- before switching work items or repositories;
- before an intentional handoff;
- when context exhaustion or process termination looks possible;
- before blocking after new work;
- before completing Git-backed work.

Never on a timer, never per file edit, and never with a meaningless commit.
Research and analysis steps that change nothing need no checkpoint. A
checkpoint summary is not evidence that tests passed; record results as
telemetry or completion evidence.

### Guards

With `"workProtocol": {"continuity": {"requireDurableCheckpoint": true}}`
(the default for new installations):

- **`work complete`** of an item that changed the repository meaningfully
  (since its first execution's start commit, or with dirty meaningful paths)
  or that has any checkpoint requires all of the following:
  - the latest checkpoint is HEAD, or differs from it only by commits of
    Praxis state;
  - the remote re-verifies it at completion time;
  - no meaningful uncommitted change remains.

  Work that changed nothing completes as before, and no commit is ever
  required. Outside a Git repository the guard does not apply.
- **`work block`** after work that no checkpoint covers requires a checkpoint
  first, or `--unrecoverable-reason TEXT`. Recorded truthfully, that reason
  becomes `continuity: {status: "not-remotely-recoverable", reason, ...}` on
  the `work.blocked` event and is shown to successors. A reason given when
  there is no new work is not recorded.

Repositories without the setting keep their completion semantics and still
see continuity warnings.

### Taking over: `work continue`

When the executor of **active** work is gone, a successor continues it
without the blocked/resume cycle:

```bash
git fetch origin && git switch --track origin/<branch>   # a clean checkout
./ros work context FEAT-142 --text                        # checkpoint, next action, freshness
./ros work continue --id FEAT-142 --occurred-at "$(date -u +%Y-%m-%dT%H:%M:%S.000Z)"
```

Continuation is refused in these cases:

- for blocked, ready or complete work;
- for the caller's own run;
- for a process with no declared identity;
- for a checkout with meaningful uncommitted changes, which are never
  overwritten;
- for a checkout that does not contain the checkpoint (the recovery steps
  are printed).

Otherwise the successor gets a **new execution**, whose `parentExecutionId`
is the predecessor, and a `work.continued` event records the predecessor as
`interrupted` (`dispositionSource: observed-by-successor`). The predecessor's
execution record is never edited or re-identified. When the work later
completes, finalization closes every active execution; "finalized" means
closed, not successful, and the `work.continued` event remains the record of
the interruption.

The output also includes the checkpoint, the completed work, the next
action, the completion evidence still required, the test measurements every
execution recorded (with their evidence quality), and the predecessor's
steps. Every executor, whether an agent from any provider, a human or
automation, keeps its own identity.

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
