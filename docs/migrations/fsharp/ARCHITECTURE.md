# ROS F# migration architecture

`DF-ROS-2026-A027` is the decision authority. This document is the concise
implementation map.

## Current semantic system

```text
npm/bootstrap + launchers
        |
Node CLI kernel -------- Node telemetry kernel
  |       |                        |
  |       +-- work/context/events  +-- execution records/metrics/providers
  +-- artifacts/registries/adapters/Git
        |
Node HTTP + TypeScript UI     project-administration hub
        |
GitHub workflow declarations and npm publication
```

Semantic decisions are concentrated but not isolated: Node/Python/schema
artifact rules duplicate; work and telemetry duplicate Git discovery; bootstrap
constructs domain state; UI duplicates vocabularies; release YAML mixes policy
and platform mechanics.

## Target composition

```text
platform adapters / Ros.Cli
            |
       Ros.Application  <----- Ros.Infrastructure
            |
         Ros.Domain

Ros.Contracts is the explicit versioned boundary vocabulary used at edges.
```

- Domain contains pure stable types/decisions and no host dependencies.
- Application contains capability handlers, projections, validation, and small
  effect-port records/functions.
- Infrastructure implements filesystem/front-matter/Git/process effects.
- CLI composes dependencies and maps typed outcomes to public output/exit.
- Contracts preserves deliberate V1 shapes and unknown extensions; default F#
  serialization never becomes an accidental external contract.
- Code is feature-local inside those boundaries (`Artifacts` first), rather
  than one file per former script or one giant program.

## Implemented artifact slice

`Ros.Domain.Artifacts` owns the artifact vocabulary, identifier/kind policy,
reference checks, and registry projection. `Ros.Application.Artifacts` owns
the validation/check/build use cases and explicit `Failed` versus
`Indeterminate` effect outcomes. `Ros.Infrastructure.Artifacts` owns confined
filesystem discovery, the compatibility front-matter parser, and one-file
atomic registry replacement. `Ros.Contracts` owns deliberate JSON renderers;
the CLI only maps typed outcomes to command output and exits.

The slice's commands are `ros-fs artifacts validate [--json]`, `ros-fs
registry build [--dry-run]`, and `ros-fs registry check`. It intentionally
does not claim the broader Node `validate` contract, which additionally owns
work, telemetry, and stale-registry checks. F# registry check is artifact-only
until those semantic areas have their own slices.

## Artifact projection persistence seam

MIG-05 begins with a deliberately narrow cross-runtime lease for the generated
artifact registries. Node and F# use the same SHA-256-named `.ros/locks` lease
and a versioned `.ros/transactions/artifact-registries.json` write set. A normal
build locks, recovers a pending generated-registry transaction, plans all
changes, durably records the set, atomically replaces each registry, and removes
the record. Recovery replays only the eight configured registry paths; malformed
or unknown transaction data is rejected as indeterminate rather than followed.

This is safe because canonical Markdown remains source of truth and registry
projection is idempotent. It is not a generic transaction implementation, does
not version canonical inputs, and must not be reused for work or telemetry state
without their own recovery/authority design.

## Git provenance seam

MIG-06 adds a read-only F# boundary for repository status. The domain
result is closed over `Clean`, `Changed`, and `Unavailable`; a failure can no
longer be represented as an empty change list. Tracked status preserves separate
index and work-tree deltas, while rename/copy changes retain both destination
and origin paths. Infrastructure alone invokes `git status --porcelain=v1 -z`,
the application exposes the observation use case, Contracts owns the versioned
JSON shape, and the CLI maps unavailable to a non-zero exit.

Production work and telemetry consumers now share `tools/ros_git.mjs`, whose
versioned observation shape is byte-for-byte compatible with the F# contract.
Work uses destination paths for rename attribution, permits only the documented
greenfield non-repository case, and rejects other unavailable status before
completion effects. Telemetry preserves unavailable ending state instead of
emitting a measured zero. The two former fail-open Git helpers were removed.

The Node adapter remains the installed process implementation because MIG-01
did not authorize a .NET consumer dependency. F# therefore owns the typed
semantic contract while differential tests constrain the compatibility
implementation; a production runtime switch still belongs to the distribution
slice. No custom Git behavior replaces the Git executable.

## Live-work decision seam

MIG-07 begins with the pure semantic core of the live-work lifecycle. Four
states (`Ready`, `Active`, `Blocked`, `Complete`) and four requested actions
encode only the five characterized legal edges. The decision also rejects a
missing block reason and missing configured evidence types. These decisions
have no filesystem, Git, clock, telemetry, event, or persistence effects.

`ros-fs work decide` is a shadow diagnostic surface with an explicit JSON
result. It is not a state-changing command and does not claim that evidence
paths exist. Application/effect orchestration and comparative persisted
context/event behavior remain required before a production switch.

The next MIG-07 sub-slice adds a pure orchestration plan. Given a typed current
item, requested action, configured target local state, observed clock, evidence,
Git paths, repository/protocol identity, and telemetry capability, it returns
either the full rejection or a planned item projection, semantic event, and
ordered telemetry intents. Five legal-edge differentials match the production
Node item/event projections field-for-field. The planner does not generate
event/execution IDs, inspect evidence paths, acquire locks, or write state.

This separation makes the use-case decision independently testable without
creating a second source of truth. Whole-context planning, evidence-path
capabilities, backlog promotion, and effect execution remain MIG-07 work. The
framework-dependent shadow is not called by installed ROS pending distribution.

The evidence-capability sub-slice composes after a successful pure plan. A typed
port observes each supplied evidence reference as `Present`, `Missing`, or
`Unavailable`; the filesystem adapter uses repository-root resolution while
preserving the legacy acceptance of files, directories, and absolute paths.
Missing and unavailable evidence remain ordered, distinct rejection issues.
Repository containment is not imposed without an authority and is recorded as
an open policy question. Whole-context/backlog effects still remain.

The context-planning sub-slice lifts the item plan over an ordered selection of
work IDs. It retains existing context order, appends newly begun items in
request order, emits events in request order, computes first-begin metadata,
and returns no plan if any ID or transition is rejected. Its explicit context
JSON decoder rejects unknown semantic states while ignoring unrelated fields;
the output is a planning view, not a lossless persistence codec.

This sequencing also exposes a production boundary defect: Node delays the
event/context transaction until every item succeeds, but performs telemetry
effects inside the item loop. A later rejection can therefore leave earlier
telemetry evidence detached even though context/events were not written. The
existing execution-link recovery can repair a single detached record; a future
F# effect handler must validate and freeze the whole plan before executing any
telemetry intent. This slice intentionally does not switch the writer.

The backlog-planning sub-slice keeps queue transitions separate from live-work
promotion. Four closed queue states and four actions encode the seven observed
legal edges. State-change effects use explicit `Keep`, `Clear`, or `Set`
operations for optional reasons, because production abandonment preserves a
prior block reason. `Start` instead returns `PromoteToLiveWork`; it does not
invent a fifth queue state or mutate the authoritative queue record.

Batch promotion preflights every captured queue item as ready while permitting
an ID absent from the local queue, preserving direct external-authority work.
The resulting plan is input to the already typed live-context planner. Queue
and live-context writes remain separate bounded recovery units, and the shadow
does not attempt a cross-store transaction or production switch.

The verified-context composition applies evidence observation only after the
entire ordered context plan succeeds, and only for completion. It reuses the
same present/missing/unavailable boundary as the item command, observes the
command evidence list once in caller order, and returns no plan when any issue
exists. This removes duplicate evidence-policy implementations without moving
filesystem knowledge into Domain.

The telemetry-resolution sub-slice composes a plan's abstract `TelemetryIntent`
list with an observed candidate-execution read model (`TelemetryItemState`),
freezing the final item/event `telemetryExecutionIds` projection whenever no
intent requires creating a new execution record. Begin's single
recover-or-start decision reuses the existing `ExecutionLinkRecovery` contract
unchanged; resume is a distinct bulk-link decision — production appends every
currently active execution for the work item regardless of prior link state
and never rejects on multiple candidates, unlike begin's single-candidate
ambiguity guard. Finalize similarly re-scans every active candidate rather
than only linked ones, matching production's orphan-recovery behavior on
completion. When resolution reaches an intent that would require a new
execution, it halts and reports `PendingNewExecution` rather than guessing an
ID — the still-open boundary below.

`Ros.Infrastructure.Work.FileTelemetryStateRepository` gives the shadow CLI a
real observation of candidate executions, reading the same
`.ros/telemetry/executions/*.json` records production's
`showTelemetry`/`loadExecutions` read, in the same lexicographic order. `ros-fs
work plan --resolve-telemetry` uses it by default; explicit `--candidate`
flags remain available only to force a scenario the current repository state
does not contain (controlled testing). This closes the same class of gap
`FileEvidenceRepository` and `ProcessGitRepository` already closed for
evidence and Git status: the shadow surface observes real repository state
rather than only a synthetic simulation of it.

Creating a genuinely new execution record is still a clock/ID-generation
effect this migration phase does not perform, so
`ResolvedTelemetryOutcome.PendingNewExecution` names the exact work item
needing that effect instead of the frozen projection. A future state-changing
handler must perform that creation, feed the resulting ID back through the
same `TelemetryItemState` port, and only then render the event/context write
set through the existing work-state recovery port.

`ObservedGitPaths`/`MeaningfulChangedPaths` on `work context-plan` now default
to a real observation, closing the gap the previous paragraph in this
document once named. `Ros.Domain.Work.PathFilter` mirrors production's
`globMatch`/`meaningfulPaths` (`tools/ros_cli.mjs`) character-for-character —
escaping only the same metacharacter set Node's manual escape does (a generic
regex-escape would also escape `*` itself and characters Node leaves literal,
breaking the `**`/`*` distinction) — and `Ros.Infrastructure.Work.FileWorkConfigRepository`
reads `ros.json`'s `workProtocol.meaningfulPaths`/`.ignoredPaths` with the
same field-by-field defaults. `Ros.Domain.Git.GitBaseComparisonOutcome` and
`ProcessGitRepository.createBaseComparison` add the optional `$ROS_BASE_REF`
committed-range extension: an unresolvable ref is a silent no-op (matching
production's own tolerance of an absent CI base ref in nested fixtures),
while a resolvable ref whose diff itself fails is a hard error, never folded
into a silently empty path list. The CLI gates real observation on the same
condition production gates `gitPaths` on (completion, or a repository's first
begin) so an irrelevant action never risks a spurious Git failure; explicit
`--observed-git-path`/`--path` flags remain available to force a scenario
outside the current repository state, exactly as `--candidate` does for
telemetry.

`ros-fs work validate [--json]` is a read-only diagnostic mirroring
production's `workFindings` (`tools/ros_cli.mjs`), the work-attribution
contributor to Node's monolithic `validate` findings pipeline (alongside
artifact, registry, queue, and telemetry findings the F# CLI already exposes
or does not yet mirror as separate diagnostics). `Ros.Domain.Work.Attribution`
is the pure decision: when `workProtocol.enforceAttribution` is off, no
Git observation is ever attempted, matching Node's short-circuit before
`gitPaths` is called; otherwise every real observed Git path is filtered to
the meaningful set via the same `PathFilter`, excluded when already present
in the context's `baselineDirtyPaths`, and excluded again when either the
path appears in `.ros/events/events.jsonl` (`FileEventLogRepository.readAttributedPaths`)
or any work item in `.ros/context/current.json` is `active`/`blocked`. A
real Git failure while enforcement is on renders the same single synthetic
`.git` finding production does — `cannot verify work attribution because
{operation} is unavailable: {reason}` — never a silent empty result. This
closes one more Node/F# parity gap without adding new state-changing
capability: the command only reads `ros.json`, `.ros/context/current.json`,
`.ros/events/events.jsonl`, and real Git status.

`ros-fs work backlog-validate [--json]` is the same kind of read-only
diagnostic for production's `queueFindings` (`tools/ros_cli.mjs`), the
backlog-queue contributor to Node's `validate` findings pipeline.
`Ros.Domain.Work.BacklogQueueValidation` validates the raw
`.ros/work/queue.json` rows exactly as production does — duplicate ids
(flagged on the second occurrence, not the first), ids failing the shared
`WorkItemId.isValid` pattern, statuses outside production's four literal
values, and priorities outside its three — over unparsed `string` fields
rather than an already-validated `BacklogState`/priority enum, since
production reports an unrecognized value as a finding instead of throwing.
`Ros.Infrastructure.Work.FileBacklogQueueRepository.readItems` reads the
file, defaulting to an empty list exactly like production's `loadQueue`
default when the file is absent. `BacklogQueueFinding` is a new type with
the same `Path`/`Field`/`Message` shape as `ArtifactFinding` and
`WorkAttributionFinding` rather than a shared one: those two names are
already shipped with their own contracts, and unifying them retroactively
risked the same field-resolution ambiguity a shared name already caused
twice this migration (F# resolves an unannotated record field access to
the most recently declared type with that field in scope) for a purely
cosmetic gain. The new file is deliberately compiled last in
`Ros.Domain.fsproj` so its `Id`/`Path`/`Field`/`Message` fields can never
become the "most recent" declaration shadowing an earlier file's own
unannotated field access.

## Phase A: the first real state-changing effect (`work backlog-transition`)

`DF-ROS-2026-A028` names Phase A ("full command-surface effect parity") as
the precondition for ever redirecting `./ros`. `ros-fs work backlog-transition
--id ID --action {ready|block|abandon} --occurred-at TIMESTAMP [--reason
TEXT]` is Phase A's first increment: a genuine, differential-proven write to
`.ros/work/queue.json` and `.ros/work/queue.md`, mirroring production
`backlogTransition`/`backlogTransitionUnlocked` (`tools/ros_cli.mjs`) exactly
— not a shadow diagnostic. It deliberately excludes `start`: production's own
`backlogTransitionUnlocked` never handles that action either (`startWork` is
a separate exported function delegating into the full live-work
`transitionUnlocked`, which also touches telemetry) — a materially larger,
telemetry-entangled effect this slice does not attempt.

The pure decision is `Ros.Domain.Work.BacklogTransition.decide` (already
built and tested for the read-only `work backlog-decide` diagnostic); this
slice is what finally *executes* that decision. Two design choices make the
execution safe:

- **JSON surgery over a typed rewrite.** `queue.json` items carry fields no
  F# type models today (`attachments`, `description`, `sourceReference`,
  arbitrary future fields). A typed parse-mutate-reserialize round trip would
  silently drop anything the type doesn't know about — invisible data loss
  on every write, not a bug that fails loudly. `Ros.Infrastructure.Work.FileBacklogQueueRepository.applyStateChange`
  instead parses `queue.json` as a mutable `System.Text.Json.Nodes.JsonNode`
  tree, mutates only `status`/`blockedReason`/`abandonedReason`/`updatedAt`
  on the one matching item (`BacklogFieldChange.Set`/`Clear`/`Keep`, mapped
  to indexer-set/`Remove`/no-op), and reserializes the *whole* tree — so
  every other item, and every field on the mutated item this migration
  doesn't model, survives byte-for-byte. `JsonSerializerOptions` is
  configured with `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`
  (the same choice `JsonRendering.renderIndented` already made for contract
  output) so an apostrophe or quote in a title is never HTML-safe-escaped
  the way .NET's default encoder would — production's own `JSON.stringify`
  never does either, and a real differential test with `It's a "quoted"
  title` as a fixture title proves the byte match, not just the common case.
- **Markdown regeneration needs typed rows, not the whole document.**
  `queue.md`'s table only ever needs `id`/`title`/`tags`/`priority` from the
  queue plus `id`/`semanticState` from live work items in
  `.ros/context/current.json` — so `Ros.Domain.Work.QueuePresentation`
  (`effectiveStatus`/`mergedRows`/`renderMarkdown`) is a small, fully typed,
  pure port of production's own `effectiveStatus`/`mergedRows`/`renderQueueMarkdown`,
  built from rows the infrastructure layer extracts from the same mutated
  `JsonNode` tree, without needing a complete queue-item type either.

Both writes commit through the *existing* MIG-05 `BacklogStateTransaction`
contract (`prepare` writes the recovery journal, `recover` applies it and
deletes the journal on success — the same two-call sequence production's own
`commitBacklogState` uses), under the same generic `RegistryLock.acquire
root "work-protocol"` file lock production's `withFileLock` uses (proven
format-compatible since MIG-04/05), after recovering any pending work-state
*and* backlog-state transaction first — mirroring `withWorkProtocol`'s exact
guard order. No new persistence primitive was needed: this slice is proof
that MIG-05's ports were built for exactly this moment.

An unknown id, an illegal transition, and a missing `--reason` on `block`
are rejected with production's exact error-message text (`'{id}' is not a
captured local work item`, `cannot {action} backlog item '{id}' from
'{status}'`, `block requires --reason`) before any file is touched — proven
by a real differential test that also exercises Node's own error path and
asserts the state files are byte-identical afterward (i.e. untouched, not
partially written).

## Phase A, increment 2: `work capture`

`ros-fs work capture --title TITLE --occurred-at TIMESTAMP [--id ID]
[--priority {high|medium|low}] [--description TEXT] [--tag TAG]*
[--actor NAME] [--source NAME] [--source-reference REF]` mirrors production
`add`/`captureWork`/`captureWorkUnlocked`/`nextQueueId` (`tools/ros_cli.mjs`),
deliberately excluding `--file` attachment: `attachFileUnlocked` copies
binary content and is a separate, larger effect this slice does not attempt
(an item this command creates always starts with `attachments: []`,
matching production's own initial state for an item created without
`--file`).

The pure decision is new: `Ros.Domain.Work.WorkCapture.plan` — title
trimming and emptiness, priority validation, explicit-id validation against
*both* the queue and the live context (in that order, matching production's
own two separate checks), and collision-avoiding sequential ID generation
(`WI-%04d` seeded from the persisted `nextSeq`, advancing past any
collision) when no explicit id is given. It deliberately checks a
generated id's uniqueness against the queue only, never the live context
— reproducing a real production asymmetry (`nextQueueId` only ever
consults `queue.items`) rather than "fixing" it, since a differential
test must prove behavior, not a nicer version of it.

`Ros.Infrastructure.Work.FileBacklogQueueRepository.captureItem` extends the
same `JsonNode`-surgery approach `applyStateChange` established: an
*existing* `queue.json` is parsed and appended to (every prior item and
field surviving untouched, same as a transition); an *absent* one is
synthesized first, matching production's `loadQueue` default —
`{schemaVersion: "1.0.0", repository: <resolved>, nextSeq: 1, items: []}`
— including reading `ros.json`'s `repository.id ?? name ?? path.basename(root)`
chain (`FileWorkConfigRepository.readRepositoryId`, new) for the `repository`
field a from-scratch document needs. The new item is then written as a
`JsonObject` in production's own literal key order (`id, title, description,
tags, priority, status, attachments, createdAt, updatedAt, createdBy,
source, sourceReference`) so a byte comparison against a real Node capture
is meaningful, not coincidental. `queue.md` is regenerated exactly as it is
for a transition, and both writes commit through the same
`BacklogStateTransaction`/`RegistryLock` pair.

Real differential tests cover an auto-generated id (including an
apostrophe-and-quotes title, proving the escaping choice still holds), a
completely fresh (missing) `queue.json`, an explicit id that never advances
`nextSeq`, and three rejection paths (empty title, invalid priority, and a
duplicate explicit id) — each proving the state files are byte-identical to
what production itself produces (modulo the clock-derived
`createdAt`/`updatedAt`), not merely that the CLI printed something
plausible.

## Phase A, increment 3: `work update`

`ros-fs work update --id ID --occurred-at TIMESTAMP [--title TEXT]
[--description TEXT] [--priority {high|medium|low}] [--tag TAG]*` mirrors
production `update`/`updateWorkUnlocked`/`findOrCreateQueueEntry`
(`tools/ros_cli.mjs`), again excluding `--file` attachment. It shares
`captureItem`'s infrastructure rather than duplicating it: both
`FileBacklogQueueRepository.captureItem` and the new `applyUpdate` now
call a shared `loadOrCreateQueueNode`/`commitQueue` pair, extracted from
`captureItem`'s original body without changing its behavior (its own
tests still pass unchanged) — proof the two effects really do share one
underlying shape (parse-or-synthesize, mutate, extract rows, regenerate
`queue.md`, commit through `BacklogStateTransaction`), not just similar
prose.

The pure decision, `Ros.Domain.Work.WorkUpdate.plan`, is a genuine
production behavior most of this migration hasn't needed yet: **partial
field update**. Node's `updateWorkUnlocked` only ever touches a field when
the caller's option for it is not `undefined` — an omitted `--title`
leaves the title alone, not blank. Modeling this with `Option<'T>` inputs
mapping to `Keep`/`Set` outputs (`WorkTitleChange`, `WorkDescriptionChange`,
`WorkTagsChange`, `WorkPriorityChange` — four small unions rather than one
shared type, since `BacklogFieldChange`'s existing `Keep`/`Clear`/`Set of
string` shape doesn't fit a list-valued field or a doubly-optional
description) keeps "nothing said, nothing changed" explicit in the type
rather than encoded as a sentinel value. The CLI layer reproduces one more
real subtlety in how production reaches this: `--tag`'s own *presence*,
not its value, decides whether tags change at all (`rest.includes("--tag")`
in production, `arguments |> List.contains "--tag"` here) — a `work update`
with no `--tag` flag at all can never clear or replace tags, but one with a
single valueless `--tag` still counts as "tags provided" and would replace
them with whatever `tagOptions` collects (here, none).

The other production behavior this slice reproduces deliberately, not
fixes: `findOrCreateQueueEntry` upserts a *minimal* default record
(`title = id`, `priority = "medium"`, `attachments: []`, ...) for an id
found only in the live context, then updates apply on top of that fresh
record in the same call — never on top of whatever richer record a
caller might expect. The infrastructure layer re-derives "is this id
already in the queue" directly from the live `items` array at write time
rather than trusting the plan's own `UpsertNew` flag, which is sound only
because the surrounding `work-protocol` lock rules out a same-process race
between the decision and the write — an assumption every real effect this
migration has built shares, not a new one.

## Phase A, increment 4: `work attach` — every backlog-only effect now has parity

`ros-fs work attach --id ID --occurred-at TIMESTAMP --file PATH[=NAME]
[--file PATH[=NAME]]*` mirrors production `attachFileUnlocked`/
`sanitizeFileComponent`/`findOrCreateQueueEntry` (`tools/ros_cli.mjs`). This
is the fourth Phase A increment and closes out the backlog-only command
surface: `add`, `update`, `attach`, and `ready`/`block`/`abandon` all now
have real, differential-proven F# effects.

Two things make this slice larger than the prior three, both handled
deliberately:

- **It is the first real effect to touch bytes, not just JSON/text.**
  Production reads the source file, computes a sanitized on-disk name,
  writes it under `.ros/work/attachments/{id}/{seq}-{sanitized}`, *then*
  commits the queue/markdown update — and does so as a **plain,
  non-transactional write**, not through `BacklogStateTransaction`. This
  slice reproduces that exact ordering and exact non-atomicity: a crash
  between the file write and the queue commit leaves the same kind of
  orphaned attachment file production's own design already accepts. That is
  not a gap this migration introduces, and not one it silently closes
  either — matching the boundary is the correct fidelity, not a shortcut.
  `Ros.Domain.Work.WorkAttachment.sanitizeFileComponent` ports production's
  `sanitizeFileComponent` exactly: the last POSIX path segment (`path.basename`
  on this platform), trimmed, every run of characters outside
  `[A-Za-z0-9._-]` collapsed to one `_` — falling back to `"file"` only when
  basename-then-trim leaves *nothing at all* (not, as a first draft of this
  slice's own tests wrongly assumed, whenever the whole name is disallowed
  characters: `sanitizeFileComponent("???")` is `"_"`, a valid non-empty
  result, not `"file"` — production's own `cleaned || "file"` only fires on
  an empty string).
- **Multiple `--file` flags do not commit as one batch.** Production's own
  `work attach` CLI handler loops calling the fully-locked, fully-committing
  `attachFile` once per file (`for (const file of files) attachFile(...)`),
  so N files means N separate lock-acquire/recover/commit cycles, not one.
  `runWorkAttach` reproduces this exactly via `attachOneFile`, a
  single-file lock-to-commit cycle the CLI loops over — matching not just
  the end state but the same partial-progress behavior if a later file in
  the batch fails (earlier ones stay committed).

The sequence-number and record-shape decision, `Ros.Domain.Work.WorkAttachment.plan`,
mirrors `attachFileUnlocked`'s `item.attachments.reduce((max, entry) =>
Math.max(max, entry.seq ?? 0), 0) + 1` precisely: the next sequence is one
past the *highest* existing sequence, not the count, and a missing or
malformed `seq` on an existing record counts as zero rather than being
skipped (`FileBacklogQueueRepository.readAttachmentSequences` reproduces
the same `?? 0` fallback when reading real records). `contentType` is
always `None`: production's own `work attach` CLI path never threads one
through either (only the separate, out-of-scope HTTP upload path in
`ros_server.mjs` ever does).

`FileBacklogQueueRepository.applyAttachment` extends the same
`findOrAppendItem`/`commitQueue` shape the prior two effects share — a
`findOrAppendItem` helper was extracted from `applyUpdate`'s original body
(behavior unchanged, its own tests still pass) so all three item-touching
effects (`applyUpdate`, `applyAttachment`, and `captureItem`'s own append)
go through the same small set of primitives rather than three parallel
copies of "find or upsert, mutate, commit."

## Phase A, increment 5: `work start` — the first live-work, telemetry-creating effect

`ros-fs work start --id ID [--id ID]* --occurred-at TIMESTAMP [--type TYPE]
[--actor NAME] [--classification NAME]*` mirrors production
`startWork`/`transitionUnlocked` (`tools/ros_cli.mjs`) for the `begin`
action only. This is the first Phase A increment outside the backlog layer:
it writes `.ros/context/current.json` and appends `.ros/events/
events.jsonl`, and — when telemetry is enabled and no recoverable execution
already exists — creates a brand-new `.ros/telemetry/executions/EXE-*.json`
record, production's own `startExecution`. Closing this boundary was named
as its own open item by the telemetry-resolution slice
(`TelemetryEffect.CreateExecution`, `ResolvedTelemetryOutcome.PendingNewExecution`)
and by `EV-ROS-2026-A043`'s inventory.

The pure decision layer for this whole surface already existed before this
slice: `WorkContextPlanning.plan` (new-item creation, transition legality,
baseline-dirty-path bookkeeping) composed with `TelemetryPlanResolution.
resolveContext` (recovering a detached candidate execution, or halting on
`PendingNewExecution`) — both built during MIG-07-WORK-CONTEXT and
MIG-07-TELEMETRY-RESOLUTION as read-only shadow diagnostics. This increment
adds only the effect layer those decisions were always missing:

- **`Ros.Infrastructure.Work.FileTelemetryExecutionRepository.createExecution`**
  is the real `startExecution` port: identity discovery purely from
  whitelisted environment variables (`Ros.Domain.Telemetry.Identity`,
  matching production's provider/runtime cascade and independent
  `sessionId`/`conversationId`/`runId` fallback chains exactly), a real Git
  baseline snapshot (branch, commit, dirty paths filtered by telemetry's own
  `ignoredPaths` default — which, unlike the work-protocol default, also
  ignores `registries/**`), the full metric registry read from
  `telemetry/metrics.json`, capability seeding for every registered metric
  (`Ros.Domain.Telemetry.Capability.initial`, mirroring production's
  `ros-derived`/runtime-known/`unknown` classification), and the one
  baseline metric production always records at creation
  (`git.baseline_dirty_files`) — including its content-addressed
  `measurementId`, a SHA-256 digest of the metric's own fields with keys
  sorted, and the capability history/omission bookkeeping a later merge
  produces (`Capability.upsert`, capped at `maxCapabilityHistoryEntries`).
  A capability `initialCapabilities` seeded and no observation ever merged
  into is written as the same 5-key object production's own map produces —
  `lastAssessedAt`/`recordedAt`/`history`/`historyOmitted` do not exist on
  it at all, not merely hold default/empty values; a `Capability.Touched`
  flag carries that distinction into the JSON writer. Excluded from this
  port: every explicit override option no current CLI command supplies (an
  execution ID, identity overrides, `startedAt`, requirement/experiment/PR
  links, an initial `scope`) — a future telemetry-producer-command
  increment can extend it without changing this shape.
- **`Ros.Infrastructure.Work.FileWorkContextRepository.applyContextPlan`**
  is the real `transitionUnlocked` + `commitWorkState` port for an
  already-telemetry-resolved plan: JSON-node surgery on
  `.ros/context/current.json` (preserving every field this slice's typed
  `LiveWorkItem` does not model — a research item's `conclusion`, for
  instance — exactly the same discipline `FileBacklogQueueRepository`
  already established for `queue.json`), and a real event-log append with
  production's own content-addressed `eventId`: a SHA-256 digest of the
  event's JSON in the *exact* key-insertion order production's object
  literal plus spread produces (no key sorting — `eventId` is computed over
  `JSON.stringify`'s own insertion-order text, unlike `measurementId`'s
  sorted digest), with an absent `reason` dropped from the hash input
  entirely rather than hashed as `null` (matching `JSON.stringify` silently
  omitting an `undefined`-valued key). Both writes commit through
  `WorkStateTransaction` (MIG-05's `work-state` recovery journal), which
  already existed but had never been exercised by a real effect before this
  slice. `telemetryExecutionIds` is written onto an item only when
  non-empty, matching production's own conditional `??= []` (never set at
  all when telemetry is disabled).
- The CLI orchestration (`runWorkStart`) is the first place a real effect
  and a `PendingNewExecution` halt compose: on that outcome it calls
  `createExecution` for the named work item, then re-runs
  `resolveContextTelemetry` against freshly re-observed candidates (a real
  effect boundary re-reading disk state, not a second pure pass) — bounded
  to one attempt per requested work item so a persistent failure cannot
  loop forever. It also reproduces `startWork`'s own backlog guard: an id
  already captured in the queue must be `ready` (`"cannot start backlog
  item '{id}' from '{status}'; mark it ready first"`, or `"...: it was
  abandoned"` for that specific status), checked before any write, under
  the same `work-protocol` lock and `work-state`/`backlog-state` recovery
  order production's own `withWorkProtocol` uses.

Deliberately excluded from this increment, each a separate later slice:
`resume`/`block`/`complete` (the same effect infrastructure, but `complete`
additionally needs `finalizeWorkExecutions`, and `block`/`resume` need
`recordTelemetryLifecycle`'s within-execution "blocked"/"resumed" event
bookkeeping — not reachable from `begin` and not yet ported at the time;
closed later by increment 10); explicit
`--identity-*`/`--execution-id`/`--conclusion` CLI overrides; and every
telemetry-producer command (`telemetry start/ingest/classify/record/
finalize`) and both `adapter` commands, none of which have any F# model at
all yet.

## Phase A, increment 6: `work resume`

`ros-fs work resume --id ID [--id ID]* --occurred-at TIMESTAMP [--actor NAME]`
mirrors production `transition(root, "resume", ids, options)`
(`tools/ros_cli.mjs`). It is the second live-work effect and reuses
increment 5's effect infrastructure directly rather than adding any new
Domain or Infrastructure code: the same `WorkContextPlanning.plan` (with
`WorkAction.Resume`), the same telemetry-resolution-with-execution-creation
loop (now factored out of `runWorkStart` into a shared
`resolveContextTelemetryWithCreation`), and the same
`FileWorkContextRepository.applyContextPlan` commit. This is the shape the
`begin`/`resume`/`block`/`complete` family was always going to share; `work
resume` is the proof that sharing works without new plumbing.

Two things distinguish `resume` from `begin` and are reproduced exactly:
`resume` never creates a new context item (an id absent from context is
`work item '{id}' is not in repository context`, not upserted), and
`resume` never observes Git (production's own `gitPaths` call is gated on
`action === "complete" || (action === "begin" && !context.startedAt)` —
`resume` satisfies neither). Telemetry-wise, `resume`'s
`EnsureActiveExecution` intent runs through `TelemetryResolution`'s
`linkAllActive` path: every currently-active candidate execution gets
linked (regardless of prior link state) when one exists, or a brand-new
execution is created via the same `FileTelemetryExecutionRepository.
createExecution` increment 5 built when none is active — both proven by a
real Node differential, including the "no active execution at all" case
(a context item blocked directly from `ready`, never begun, which no
current CLI command can produce and which the differential seeds directly
as fixture state, matching this project's own established pattern for
exercising a real but not-yet-CLI-reachable branch).

Deliberately excluded at the time, the same as increment 5:
`recordTelemetryLifecycle`'s "resumed" within-execution event bookkeeping
(closed later by increment 10), and production's `parentExecutionId`
linkage on the new-execution path (`identity.parentExecutionId` on the
freshly created record is always `null`, where production sets it to the
work item's most recent prior execution id when one exists — still open) —
both real, narrow, and honestly-scoped gaps rather than silent ones.

## Phase A, increment 7: `work block`

`ros-fs work block --id ID [--id ID]* --occurred-at TIMESTAMP [--reason TEXT]
[--actor NAME]` mirrors production `blockWork` (`tools/ros_cli.mjs`) — the
one live-work-adjacent command that is not purely one or the other:
production's own `blockWork` splits its requested ids into backlog-only
items (captured but never started) and live-context items, applying the
matching real effect to each *under one held `work-protocol` lock*, and
returns a single mixed-shape array (raw backlog queue items alongside raw
live-context items). This increment reproduces the split, the combined
lock hold, and the mixed output exactly: backlog ids go through the same
`applyBacklogTransition` (a small helper extracted from `runBacklogTransitionEffect`,
reused verbatim) `work backlog-transition` already used for `block`; context
ids go through `work start`/`work resume`'s effect infrastructure
unchanged (`WorkContextPlanning.plan` with `WorkAction.Block`,
`resolveContextTelemetryWithCreation`, `FileWorkContextRepository.applyContextPlan`)
— `RecordBlocked` never halts on `PendingNewExecution`, so no new
execution-creation logic was needed here either.

One correctness fix came out of this slice, caught by the differential
test rather than assumed correct: `FileWorkContextRepository.applyItem`
never wrote `blockReason` onto a live-context item at all. Production sets
`item.blockReason = options.reason` during `block` and — notably — never
clears it on any later transition, including `resume`; it persists as a
stale field for the rest of the item's life. `applyItem` now writes it
whenever `Item.BlockReason` is `Some`, for every action, matching that
persistence exactly. This shipped as part of increments 5/6 undetected
until a real live-context `work block` differential existed to catch it —
a reminder that "no new Domain/Infrastructure code" (true of increment 6)
does not mean "no new coverage of existing code."

The other correctness detail this slice got right the first time by
reading production closely rather than assuming: `--reason` is **not**
checked eagerly. Production's own check order is transition-legality
first, then the missing-reason rejection — `backlogTransitionUnlocked`
and `transitionUnlocked` both throw the illegal-transition error before
ever reaching `if (!options.reason) throw "block requires --reason"`. A
CLI that required `--reason` up front would report the wrong error for an
item in the wrong state (e.g. still `captured`, never `ready`). This CLI
instead always threads `reason: string option` through to the existing
decision layer (`BacklogTransitionRejection.BlockReasonRequired` /
`TransitionRejection.BlockReasonRequired`, both already modeled and
ordered correctly by the pure `decide` functions), so the two rejections
surface in the same order production's own does.

## Phase A, increment 8: `work complete` — every live-work transition now has parity

`ros-fs work complete --id ID [--id ID]* --occurred-at TIMESTAMP [--evidence
TYPE=PATH]* [--conclusion TEXT] [--actor NAME]` mirrors production
`transition(root, "complete", ids, options)` (`tools/ros_cli.mjs`'s
`transitionUnlocked` `complete` branch, plus `tools/ros_telemetry.mjs`'s
`finalizeExecution`/`finalizeWorkExecutions`). It closes the live-work
family: `begin`/`resume`/`block`/`complete` all now commit through real F#
effects.

`complete` reuses increment 5/6's context-effect shape (`WorkContextPlanning.plan`
composed via a request built the same way, `resolveContextTelemetryWithCreation`,
a `FileWorkContextRepository` commit) but adds two things neither `begin` nor
`resume` needed:

- **Evidence-path verification.** The frozen decision layer already rejects
  missing required evidence *types* (`TransitionRejection.MissingEvidence`).
  Production additionally checks that every *provided* evidence path exists
  on disk (`fs.existsSync(path.resolve(root, entry.path))`), throwing on the
  first missing one. This CLI calls `WorkOperations.planVerifiedContext`
  (pre-existing, unused until now) instead of `WorkContextPlanning.plan`
  directly — it runs the same semantic plan first, then, only for
  `Action = Complete` and only once the plan succeeds, observes each
  provided path through `FileEvidenceRepository.create root` (also
  pre-existing). Required evidence types are read per work-type from
  `ros.json`'s `workProtocol.completionEvidence` (`FileWorkConfigRepository.
  readCompletionEvidence`, new), matching production's own
  `config.evidence[item.type] ?? config.evidence.default` fallback — the
  default ROS scaffold requires `implementation`+`tests` for most types but
  overrides `research` to require `research-record` alone, so this is a real
  per-type config read, not a hardcoded set.
- **Unconditional telemetry finalization.** Production's `complete` branch
  always calls `finalizeWorkExecutions` once the item's own execution is
  ensured, before the context/events commit. The shared `TelemetryPlanResolution`
  pipeline this migration already built discards the `FinalizeExecutions`
  intent it computes (a known, documented architectural gap — see
  `resolveContext`/`resolvePlan`), so rather than plumbing a new signal
  through the already-tested, shared `Ros.Application`/`Ros.Domain` layers,
  the CLI calls the new `FileTelemetryFinalizationRepository.
  finalizeWorkExecutions root id` directly for each completing id, in the
  same position production uses it, gated on the same `telemetryEnabled`
  flag production gates the whole block on.

`FileTelemetryFinalizationRepository.finalizeWorkExecutions` is the new real
effect: it re-reads each of the work item's currently-`active` execution
records (scanning `.ros/telemetry/executions/*.json`, matching production's
`loadExecutions().filter(status === "active")`), and for each, under that
execution's own per-execution lock, mutates it in place —

- `time.wall_ms`/`time.blocked_ms` metrics, unconditionally (`BlockedDuration.compute`,
  a new pure port of production's `blockedDuration`, folding the execution's
  own recorded lifecycle events; at the time this always evaluated to zero,
  since this migration had not yet ported `recordTelemetryLifecycle`'s
  "blocked"/"resumed" event writes — a correct answer for the data this
  migration produced then, not a stub. Increment 10 closed that gap, so
  `time.blocked_ms` now computes a real nonzero value whenever a completed
  execution was actually blocked and resumed);
- an ending Git snapshot and `git.ending_dirty_files` metric, or a
  `supported-unavailable` capability when Git itself is unavailable;
- a real **clean-baseline change summary**, computed by three new
  `ProcessGitRepository` reads (`git diff --name-status`, `git diff
  --numstat`, and untracked-file listing, each against the execution's own
  *stored* starting commit — never re-observed) parsed and aggregated by a
  new `Ros.Domain.Telemetry.ChangeSummary` module (`ChangeSummaryParser`,
  `ChangeClassification.isTestFile`/`isDocumentation`), reduced to exactly
  the fields production's own `finalizeExecution` reads (`filesByType` and
  per-file `paths` are computed by production but never consumed by that one
  caller, so this migration never computes them either — a deliberate,
  documented scope reduction, not a silent omission). When the stored start
  snapshot fails production's own "clean baseline" precondition
  (`available && commit && !dirty`), the summary is `{available: false,
  reason}` with one of production's exact three reasons
  (`git-unavailable`/`starting-commit-unavailable`/`preexisting-dirty-worktree`),
  and each of the twelve git-change metrics gets a `supported-unavailable`
  capability instead of a measurement — both branches proven byte-for-byte
  against production by a real Node/F# differential exercising a real `git`
  repository, not synthetic fixture data.

A research item's `--conclusion` (defaulting to `"inconclusive"`, matching
production's `options.conclusion ?? "inconclusive"`) is written through a
new `FileWorkContextRepository.applyContextPlanWithConclusions`, which
generalizes the existing `applyContextPlan` (kept as a zero-conclusion
convenience wrapper for the other three transitions) and extends `applyItem`
to also persist `completedAt` and, for a research item, `conclusion` —
neither of which any prior increment's `applyItem` wrote at all, since no
earlier transition needed them.

Deliberately excluded, matching every prior increment's honesty standard:
`options.input`/adapter-ingestion (`finalizeExecution`'s `adaptInput`/
`ingestAdapted`/`snapshotId` paths, gated on an option no current CLI command
threads through to a work-completion finalize — only the separate,
out-of-scope `telemetry finalize` command uses `--input`), and explicit
`--identity-*`/`--execution-id` overrides (consistent with every other real
effect in this migration).

## Phase A, increment 9: `work context` — the first pure read-only command surface view

`EV-ROS-2026-A046`'s re-run of the command-surface parity inventory,
collected once MIG-08 closed entirely, found four rows still reading "No
F# equivalent" — but flagged all four (`work`/`work list`, `work show`,
`work context`, `status`) as never having been assigned to MIG-07 or
MIG-08's own scope in the first place, rather than a gap either migration
left open. This increment closes the smallest of the four.

`ros-fs work context [ID]` mirrors production's own `contextView`
(`tools/ros_cli.mjs`) exactly: a read of `.ros/context/current.json`,
optionally filtered to one work item by ID, with two fields computed
fresh on every call rather than persisted. Unlike every increment above,
neither piece of domain logic needed to be written — both already
existed from earlier work:

- **`allowedActions`** reuses `Ros.Domain.Work.WorkTransition.
  allowedActions` (built for the live-work transition decision layer
  itself, long before this increment), matching production's own
  `TRANSITIONS[item.semanticState]` lookup table exactly field-for-field
  — including its behavior for a semantic state the table does not
  recognize: production's `TRANSITIONS[unknownState]` is `undefined`,
  spread through `[...(undefined ?? [])]` to an empty array; the F# port
  matches this via `parseSemanticState value |> Option.map
  WorkTransition.allowedActions |> Option.defaultValue []`, then sorts
  the result (`List.sort`) to match production's own `.sort()` — though
  in practice every declared state's action list already happens to
  enumerate alphabetically, so the sort is a defensive correctness
  guarantee rather than an observed reordering.
- **`requiredEvidenceForCompletion`** reuses `Ros.Infrastructure.Work.
  FileWorkConfigRepository.readCompletionEvidence` (built for `work
  complete`'s own evidence-type validation), the same per-item-type,
  falling back to `default`, lookup production's own `config.
  evidence[item.type] ?? config.evidence.default` performs. One accepted
  limitation carries over from reusing this existing, `Set`-based read
  rather than writing a second, order-preserving one: `Set.toList`
  always returns values in alphabetical order, while production's own
  array preserves whatever order `ros.json` declared. This repository's
  own `ros.json` happens to declare every evidence list already in
  alphabetical order (`["implementation", "tests"]`, `["research-record"]`,
  `[]`), so the divergence does not manifest here, but a hypothetical
  consuming repository declaring a different order would see it
  normalized.

Every unmodeled field on each item — a research item's `conclusion`, for
instance — is preserved verbatim via JSON-node surgery (`item.
DeepClone()` then two new keys assigned), matching production's own
`{...item, allowedActions, requiredEvidenceForCompletion}` object spread:
both new keys are appended after every pre-existing one, exactly as
JavaScript's spread-then-assign does. A requested ID matching no context
item rejects with production's exact message (`work item '{id}' is not
in repository context`) rather than returning an empty view. Unlike
every mutation command in this manifest, `work context` acquires no lock
at all — there is nothing to serialize against a pure read.

Confirmed against real Node by driving the actual `ros` CLI wrapper
directly (not just the exported function), covering a mix of active and
blocked items with real telemetry executions attached (the no-ID view),
filtering to one item by ID, and the unknown-ID rejection — all
byte-for-byte identical once timestamps and randomly-generated execution
IDs are normalized out. `work`/`work list`, `work show`, and `status`
remain their own future increments.

## Phase A, increment 10: `work`/`work list`/`work show` — the merged backlog/live view

The second of `EV-ROS-2026-A046`'s four unassigned rows. Production's own
`mergedWorkView`/`showWork` (`tools/ros_cli.mjs`) compute a wider view
than `work context`'s: they merge the backlog queue (`.ros/work/queue.json`)
with the live context, at full fidelity — `description`, `blockedReason`,
`backlogActions`, `attachments`, and a nested `liveWorkItem` summary — not
just the five columns `queue.md` itself renders.

A pre-existing `Ros.Domain.Work.QueuePresentation.mergedRows` already
computed this merge, but only at the narrower five-field
(`Id`/`Title`/`Tags`/`Priority`/`Status`) fidelity `queue.md` rendering
needs; widening it in place would have risked every existing caller
(`FileBacklogQueueRepository`'s `applyStateChange`/`captureItem`/
`applyUpdate`/`applyAttachment`, all committing `queue.md` on every real
backlog effect). Instead, a new `Ros.Domain.Work.WorkListView` module adds
a second, wider `mergedRows` at the `work`/`work list`/`work show`
fidelity, reusing `QueuePresentation.effectiveStatus` for the one
status-precedence rule both share rather than duplicating it. It also
adds `BacklogTransition.allowedActions` (mirroring production's own
`BACKLOG_TRANSITIONS` lookup table) as a new, independent function
alongside `BacklogTransition.decide` — the table names which actions a
backlog-only item exposes as `backlogActions`, a different question from
`decide`'s own per-request legality check.

One subtle production distinction required getting the JSON-presence
rules exactly right, not just the values: `description`, `priority`, and
an attachment's `contentType` are always-present fields (`null` when the
source has no value), while `blockedReason` is genuinely *absent* from
the JSON when neither the live item (if blocked) nor the backlog record
names one. Production's own object literal computes `blockedReason:
contextItem?.semanticState === "blocked" ? contextItem.blockReason :
queueItem?.blockedReason` — when both operands are `undefined`, the field
itself is `undefined`, and `JSON.stringify` drops an `undefined`-valued
key entirely rather than writing `null` for it. The F# port matches this
by conditionally assigning the `blockedReason` key on the output
`JsonObject` only when `row.BlockedReason` is `Some`, never assigning
(not even to `null`) when it is `None` — unlike every other optional
field in this row, which always assigns (`null` or the value).

`Ros.Infrastructure.Work.FileWorkListRepository` is the new read-side
port. It reads the live context through the exact same typed parse every
write-side effect already depends on
(`Ros.Contracts.Work.WorkContextPlanContract.parseJson`), rather than
introducing a second context parser. For `queue.json`, it introduces a
new `QueueItemDetail` shape (title, description, tags, priority, status,
blockedReason, attachments) deliberately kept separate from
`FileBacklogQueueRepository.readItems`'s existing, narrower
`BacklogQueueItemRecord` (id/status/priority only) — that type still
serves `Ros.Domain.Work.QueueValidation`'s findings, unrelated to this
view, and widening it would have been a needless coupling between two
independent read paths. `work show`'s one addition over `work list` is
the detail markdown file (`.ros/work/items/{id}.md`, read as `Some text`
when present, `None`/`null` otherwise) and production's exact
unknown-id rejection message.

Confirmed against real Node by driving the actual `ros` CLI wrapper
directly across a deliberately varied fixture — a backlog-only captured
item, a ready item promoted to an active live item, a blocked live item
(backlog record's own stale `blockedReason` intentionally left in place,
to confirm the live item wins), and a ready item carrying a real file
attachment — for `work list`, bare `work`, `work show` on each of the
four items, and the unknown-ID rejection, all byte-for-byte identical
once timestamps are normalized out. `--tag`/`--status` filtering is not
yet ported (`work context` did not need it; this view's CLI wiring
mirrors that same read-only-view scope). `status` is now the one
remaining row `EV-ROS-2026-A046` found unassigned, and it depends on the
`validate` command unification (`workFindings` + `queueFindings` under
one production-shaped result), which has not yet been scoped.

## Phase A / MIG-08, increment 9: `telemetry adapters` and `telemetry show` — the first telemetry-producer commands

With the live-work family closed, Phase A's remaining scope is every
`telemetry ...` producer command and both `adapter ...` commands —
`EV-ROS-2026-A044` (a full inventory of `tools/ros_telemetry.mjs`) grounds
why this is MIG-08-sized rather than a quick follow-on: creating a single
new execution record already needs identity discovery, a Git baseline
snapshot, metric-registry loading, and initial capability seeding, and
that record's own text explicitly declines to recommend which slice to
attempt first — only that MIG-08 "almost certainly" needs its own smaller
first vertical slice. This increment is that choice: the two commands with
zero new write-path, lock, identity, or adapter complexity —
`ros-fs telemetry adapters` and `ros-fs telemetry show [TARGET]` — mirroring
production `tools/ros_telemetry.mjs`'s `TELEMETRY_ADAPTERS` constant and
`showTelemetry` exactly.

`telemetry adapters` prints a hardcoded provider-adapter catalog
(`Ros.Domain.Telemetry.TelemetryAdapters.all`, a direct port of the
`TELEMETRY_ADAPTERS` array) — no file I/O, no domain logic at all, just a
literal list ported for traceability's sake.

`telemetry show [TARGET]` is a real read against
`.ros/telemetry/executions/*.json` — the same files `work start`/`work
complete` already produce — through a new
`Ros.Infrastructure.Work.FileTelemetryQueryRepository`
(`readAll`/`readByWorkItemId`/`readByExecutionId`), with no lock and no
write. `TARGET` is positional, matching production's own
`telemetryTarget(args)` (`args[2]`, undefined when it starts with `--`),
not a `--` flag like every other command this migration has ported so far:

- no target → every execution record, in ascending filename order
  (`readAll`, mirroring `loadExecutions`);
- an `EXE-`-prefixed target → `resolveExecution`'s exact-id branch:
  reject with production's exact message when nothing matches, otherwise
  resolve the (in practice singular) match;
- any other target → filter by `workItemId` (`readByWorkItemId`), where an
  empty result is **not** a rejection — production's own `showTelemetry`
  never throws for this branch, unlike the `EXE-` one.

Deliberately excluded at the time, and explicitly out of THIS increment's
scope rather than assumed unnecessary: `telemetry summary`'s aggregation
(`summarizeTelemetry` — four aggregation strategies plus interval-merged
timing summaries, real new domain logic, not a trivial read — closed later
by increment 11); every write-path telemetry-producer command (`start`,
`ingest`, `classify`, `record`, `finalize` — the last of which is also the
one place `--input`/adapter-ingestion is reachable at all, deliberately
excluded from `work complete` in increment 8, still open); and both
`adapter call`/`adapter publish` commands (the work-adapter contract, a
different concern from telemetry provider adapters, still open). Each
remains its own future increment, chosen the same deliberate way this one
was.

## Phase A / MIG-08, increment 10: telemetry lifecycle bookkeeping — closing a real gap in `work block`/`work resume`

Increments 5-8 documented, honestly, that `work resume`/`work block` never
ported production's `recordTelemetryLifecycle` (`tools/ros_telemetry.mjs`):
the "resumed"/"blocked" bookkeeping it writes into an execution's OWN
`events` array (distinct from `.ros/events/events.jsonl`, the context-level
event log those two commands already write correctly). Its absence meant
`time.blocked_ms` -- computed at finalization by `BlockedDuration.compute`,
already ported in increment 8 -- always evaluated to zero: a correct
answer for the data this migration produced at the time, not a stub, but
incomplete relative to production. This increment closes that gap.

`FileTelemetryFinalizationRepository.recordLifecycle` (a new function
alongside `finalizeWorkExecutions` in the same module, since both mutate an
existing execution record in place under its own per-execution lock) mirrors
`recordTelemetryLifecycle` exactly: for every currently-active execution
linked to a work item, it appends a `work.blocked`/`work.resumed` event
(deduped by a `TEVT-` id -- but unlike every other `TEVT-`/`MEAS-` id this
migration has built so far from a 2-14-key digest, this one is a full
4-key object including a nested 3-key `source`, still hand-built in
alphabetical key order to match production's own recursive `stable()`
sort) plus its paired `agent.interruptions`/`agent.resumes` metric
(reusing `FileTelemetryExecutionRepository.derivedMetricNode`, the same
"derived"-quality metric/capability shape increment 8's finalization
already established).

`work resume`/`work block` (`src/Ros.Cli/Program.fs`) now call
`recordLifecycle` for every requested id -- `resumed`/`blocked`
respectively, `block` also threading its `--reason` through -- BEFORE
`resolveContextTelemetryWithCreation` runs, matching production's own exact
ordering: `recordTelemetryLifecycle` reads whichever executions are
*currently* active, and only afterward does either command decide whether
a new execution needs linking or creating. A brand-new execution `resume`
itself creates (when none was active) therefore never receives a "resumed"
event for the transition that created it, exactly like production. For
`work block`, this reordering is observationally a no-op today (a
`Block`-only telemetry intent never triggers new-execution creation, per
`WorkTransitionPlanning.telemetryIntents`), but the same ordering is kept
for both commands rather than relying on that fact staying true.

Deliberately excluded, matching `finalizeOne`'s own established scope
reduction: `ensureRecordDefaults`' legacy-record backfill (every record
this migration's own writers produce already carries the full shape it
would otherwise backfill, so there is nothing to default).

## Phase A / MIG-08, increment 11: `telemetry summary` — real aggregation, not just a read

Increment 9 explicitly deferred `telemetry summary`'s aggregation
(`summarizeTelemetry`) as real new domain logic rather than a trivial read.
This increment ports it: four real aggregation strategies plus an
interval-sweep timing summary, over every metric recorded across every
execution matching a work-item filter (or every execution when none is
given) -- the largest read-only telemetry command by far, but still no
lock and no write.

The pure math lives in a new `Ros.Domain.Telemetry.Summary` module,
deliberately separated from the JSON parsing/grouping (in
`FileTelemetryQueryRepository.readSummaryExecutions`, new) so the
aggregation itself is unit-testable without touching a filesystem:

- **`TimingSummary.compute`** mirrors production `timingSummary` exactly:
  an interval-sweep union over every fully-finalized execution's
  `[startedAt, finalizedAt]` span reports `calendarSpanMs` (the union's
  total extent) against `totalExecutionWallMs` (the naive sum, double-
  counting overlaps) and their difference as `overlappingExecutionMs` --
  all three explicitly nulled whenever any execution in the set is still
  active, since an unfinished execution's true end time is unknown.
- **`MetricAggregation.compute`** mirrors the four real strategies inside
  `summarizeTelemetry`: `sum` (with `time.wall_ms` alone carrying a
  fixed explanatory note about double-counting overlaps), `maximum`,
  `latest-per-session` (sums only the latest-`collectedAt` sample per
  unique `provider`/`runtime`/`sessionId` triple, or per execution id when
  no session is set -- a real per-session dedup, not a plain sum), and
  `none` (never aggregates, just a fixed note) -- falling back to a plain
  "latest measurement wins" default for any other/missing aggregation
  string, matching production's own unguarded `else` branch.
- **`TelemetrySummary.summarize`** groups every `(id, unit, currency,
  dimensions)` tuple across every execution (preserving file/array
  encounter order, since `latest-per-session`'s tie-breaking depends on
  it), resolves each group's aggregation strategy from the metric
  registry when the id is known there, else falls back to the group's
  own stored `aggregation` field (matching production's own `definition
  ?.aggregation ?? values[0].item.aggregation`), and sorts the final
  metrics array by id.

`FileTelemetryQueryRepository.readSummaryExecutions` does the necessary
JSON extraction: `identity.provider`/`.runtime`/`.sessionId` (each
defaulting the same way production's own `?? "unknown"`/absent-key
optional chaining does), `startedAt`/`finalizedAt`, and every `metrics[]`
entry's `id`/`unit`/`currency`/`value`/`collectedAt`/`aggregation`. One
narrow, documented reduction: a measurement's `dimensions` object is used
as a grouping key via its own (not canonically key-sorted) JSON text,
rather than production's `stable()`-sorted key -- every metric this
migration itself ever writes always has empty `dimensions`, so this only
risks diverging from production's own grouping for a non-empty
`dimensions` object written by an external tool with non-canonical field
order, which no current effect produces.

`telemetry summary`'s own `TARGET` argument is positional like `telemetry
show`'s, but unlike `show` it is used purely as a work-item-id filter --
production's own `summarizeTelemetry(root, workItemId)` never
special-cases an `EXE-`-prefixed value the way `showTelemetry` does, so
passing an execution id here silently filters to nothing (matching that
real quirk rather than "improving" on it).

## Phase A / MIG-08, increment 12: `telemetry finalize` — the first write-path telemetry-producer command

Increments 9-11 covered every read-only telemetry command; this increment
opens the write-path list left open since increment 9, starting with
`telemetry finalize [TARGET] [--quiet]` -- production's own manual
finalization entry point, layered entirely on the same
`FileTelemetryFinalizationRepository.finalizeOne` mutation that `work
complete` already exercises (shipped, tested, and unchanged since the
work-lifecycle slice), so this increment adds only target *resolution*,
never a second finalization code path to keep in sync.

`FileTelemetryFinalizationRepository.resolveFinalizeTarget` ports
production's `resolveExecution` exactly, including a real quirk `telemetry
show` does *not* share: a non-`EXE-`-prefixed target matches by
`workItemId` regardless of the matched execution's status (`show`'s
equivalent branch is state-blind too, but callers like `finalizeExecution`
never pass `activeOnly`, so this is production's real behavior, not an
oversight to fix). The three branches, matching `resolveExecution` exactly:

- An `EXE-`-prefixed target matches by exact `executionId`.
- Any other target matches by `workItemId`, any status.
- No target reads `.ros/context/current.json`, filters to work items whose
  `semanticState` is `active` or `blocked`, and requires exactly one match
  -- zero or several both reject with production's exact message,
  `"telemetry target is ambiguous; provide a work-item or execution ID"`.

Every branch then sorts its candidates by `startedAt` ascending and takes
the last (most recent); an empty candidate set after that rejects with
production's exact `"telemetry execution '{target}' was not found"`
(`"current"` substituted for a missing target, matching production's own
`?? "current"`).

`finalizeTarget` then reproduces production's exact pre-lock fast path: if
the resolved execution's file already reads `status: "finalized"`, it is
returned completely untouched -- no lock acquired at all. This is a real,
intentional race-tolerant behavior in production (a second concurrent
finalize call should not block on or re-mutate a call that already won),
reproduced deliberately rather than "improved" into always taking the
lock. Otherwise, `finalizeOne` (already lock-guarded, already
double-checking "finalized" under its own lock) runs unchanged.

One deliberate scope cut: production's `--input` flag drives real adapter
ingestion (`adaptInput`/`ingestAdapted`) before finalization, folding
externally-supplied telemetry into the execution. This CLI does not port
adapter ingestion at all yet (it is its own future MIG-08 slice, shared
with `adapter call`/`adapter publish`), so `--input` is rejected outright
with `"telemetry finalize --input is not yet supported by this CLI"`
(exit code 2) rather than silently accepted and ignored -- a loud gap, not
a quiet one. `--quiet` (suppressing the printed record, matching
production's own flag) is a one-line addition alongside it.

`resolveFinalizeTarget` reads execution records directly off disk via a
small private `readAllExecutionRecords` helper rather than reusing
`FileTelemetryQueryRepository.readAll`: the finalization module compiles
before the query module in `Ros.Infrastructure.fsproj`, and duplicating
this small helper (following this codebase's own established convention
of per-module helper duplication, already evidenced by both modules'
independent private `stringField` implementations) was judged lower-risk
than reordering the compile list for a single call site.

## Phase A / MIG-08, increment 13: `telemetry record` — the second write-path telemetry-producer command

Increment 12 shipped `telemetry finalize`; this increment ports production's
other directly CLI-reachable write-path effect, `telemetry record [TARGET]
--metric ID --value VALUE`, production's `recordTelemetryMetric` layered on
`normalizeMetric`/`addMetric` (`tools/ros_telemetry.mjs`) -- the one command
that lets a caller append an arbitrary registered metric to an execution
without going through `work start`/`work complete`'s own automatic
lifecycle.

Target resolution reuses `finalize`'s own resolver, generalized to accept
production's `activeOnly` option (`resolveExecutionTarget`, replacing the
former `resolveFinalizeTarget` as its `activeOnly = false` case): `record`
passes `activeOnly = true`, matching production's own `withExecutionLock`
call, so a target whose only match is not currently `"active"` -- including
one finalized between the pre-lock resolve and the lock actually being
acquired, a real race production itself guards against by re-resolving
under the lock -- is rejected with production's exact `"... was not found
or is already finalized"` message (reproduced here by re-running the same
resolver a second time immediately after acquiring the lock, exactly where
production's own `withExecutionLock` re-resolves).

The normalization/write-path itself (`recordMetric`, new in
`FileTelemetryFinalizationRepository`) ports every field a CLI caller can
actually reach: the metric id must exist in `telemetry/metrics.json` (else
production's exact `"unknown normalized metric '...'"` message) and the
value must be finite (else `"metric '...' requires a finite numeric
value"`) -- both checked at the same point production's does, *inside* the
lock, after the race re-resolve, not before, so a target-resolution
rejection always wins over an input-validation one when both would apply.
`--unit`/`--currency` override the registry's own defaults; `--quality`/
`--scope`/`--source-type`/`--source-name`/`--mechanism` default exactly as
production's CLI does (`"observed"`/`"execution"`/`"agent-report"`/
`"ros-telemetry-cli"`/`"explicit-metric-record"`); `--pricing-source`/
`--pricing-version` build a pricing object only when at least one is given;
`--collected-at` defaults to the real current time. `--confidence` mirrors
production's own permissive parsing exactly via a small `MetricConfidence`
union (`NoConfidence`/`NumericConfidence`/`TextConfidence`): omitted is
`null`, text that parses as a finite number is stored numerically, anything
else is stored as the literal text -- matching production's `Number.
isFinite(Number(confidenceOption)) ? Number(confidenceOption) :
confidenceOption` exactly, including that a malformed `--value` similarly
becomes `NaN` rather than a CLI-level parse error, so it reaches the same
finite-value rejection point production's own `Number(rawValue)` does.
`dimensions` stays `{}` and `aggregation` stays the registry's own value
throughout, since no CLI flag can override either one -- production's
`normalizeMetric` allows both, but nothing reachable from `telemetry
record` ever supplies a non-default value for them.

The `measurementId` is a SHA-256 digest of the metric's own fields with
keys sorted (mirroring production's `stable()` + `digest()`, and this
migration's own `derivedMetricNode` from the `work complete`/`finalize`
slices), so an identical repeated call -- same id, value, quality,
`collectedAt`, and every other digested field -- is deduplicated by content
rather than appended twice. The capability upsert (and the record's
rewrite to disk) still happens unconditionally either way, exactly
matching production's own `addMetric`, which never conditions the
capability write on whether the metric itself was a duplicate. One
subtle, deliberately-reproduced quirk: the CLI prints `record.metrics.
at(-1)` from the *returned, already-mutated* record -- if the call was a
content-duplicate of a metric that is not already the array's last entry,
the printed metric is whatever else happens to be last, not the one just
"recorded". This was confirmed by hand against real Node before being
written up here, since it is easy to assume the print always reflects the
call just made.

## Phase A / MIG-08, increment 14: `telemetry ingest` — the third write-path telemetry-producer command, generic adapter only

Increments 12-13 shipped two directly CLI-typed write-path commands
(`finalize`, `record`); this increment ports production's largest
remaining directly-reachable effect, `telemetry ingest [TARGET] --input
FILE [--adapter NAME]` (`ingestTelemetry`/`adaptInput`/`ingestAdapted`),
restricted to the `generic` adapter -- the one with zero provider-specific
field mapping, matching increment 9's own "smallest real slice first"
choice. Every other adapter name production itself recognizes
(`openai-codex`, `anthropic-claude-*`, `google-gemini-*`,
`github-copilot-*`, `otel-json`) is its own future MIG-08 slice; the CLI
rejects them outright (exit 2) rather than silently treating them as
generic, while a name production itself would not recognize gets
production's own exact error.

Target resolution reuses `record`'s own `activeOnly` resolver, but adapter
validation and adaptation happen *before* it -- mirroring production's own
`ingestTelemetry`, which calls `adaptInput` (throwing on an unknown
adapter) ahead of `withExecutionLock`. `adaptGeneric` (new) is a direct
field-by-field passthrough of the parsed input JSON -- `identity`,
`capabilities`, `metrics`, `events`, `classification`, `scope`,
`qualitySignals`, `links`, and `raw` (falling back to `providerTelemetry`,
then `{}`) are honored verbatim, since the generic adapter maps nothing.

The real complexity lives in `ingestAdaptedGeneric` (new), which mirrors
`ingestAdapted` field-for-field:

- **Snapshot identity and dedup.** A caller-supplied `snapshotId` wins;
  otherwise production computes it *before* `ingestAdapted` even runs,
  inside `ingestTelemetry` itself, as a SHA-256 digest of `{adapter,
  input}` over the *original, un-adapted* input -- not `adapted.raw` as
  `ingestAdapted`'s own (in practice unreachable) fallback formula would
  suggest. Getting this backwards was the one real bug this increment's
  own manual smoke-testing against real Node caught before any test was
  written: the two formulas produce different digests whenever the input
  JSON carries fields outside `raw`, and only re-deriving production's
  real call order (`ingestTelemetry` sets `adapted.snapshotId` first)
  resolved it. An execution that already carries this `snapshotId` --
  in `rawTelemetry` or the ingestion-event log -- returns untouched, no
  further mutation attempted.
- **The declared-unavailable consistency guard**: a metric reported with a
  value while every one of its declared capabilities says
  `supported-unavailable`/`unsupported` rejects the whole snapshot before
  any mutation, citing the same snapshotId.
- **Identity merge** (`mergeObject` semantics: skips `null`/absent
  incoming values) versus **classification/scope merge** (plain
  object-spread semantics: an explicit incoming `null` overwrites) --
  two genuinely different merge rules production itself keeps distinct,
  reproduced as two separate functions rather than one parameterized by a
  boolean, since conflating them risks silently drifting one back into
  the other on a future edit.
- **Metric normalization** generalizes `record`'s own narrow, CLI-typed
  path: an ingested metric is an arbitrary JSON object, so `unit`,
  `currency`, `quality`, `confidence`, `scope`, `aggregation`,
  `dimensions`, `pricing`, `measurementId`, `source`, and `collectedAt`
  are all honored verbatim when the metric item itself supplies them,
  falling back to the registry/defaults chain exactly as production's
  field-by-field `??` does.
- **Capability upsert** is now keyed by the pair `(metricId,
  providerField)`, not `metricId` alone -- the `Capability` domain type
  (`Ros.Domain.Telemetry`) gained an optional `ProviderField` alongside
  making `MetricId` itself optional, since production's own unknown-field
  discovery capability (below) carries a `providerField` and *no*
  `metricId` at all. `Capability.upsert` also gained an independent
  `newLastAssessedAt` parameter (every pre-existing call site passes the
  same value twice, preserving its exact prior behavior) since a
  directly-declared ingest capability can assert a `lastAssessedAt`
  distinct from its own `discoveredAt`, unlike every other capability
  write this migration produces.
- **Raw payload handling**: `sanitizeRaw` (new, general `JsonNode`
  recursion) redacts any key matching either of production's sensitive-key
  patterns and truncates an over-long string leaf, always rebuilding via
  fresh nodes since a `JsonNode` already attached elsewhere cannot be
  reattached; `leafPaths` (new) collects every leaf's path (array indices
  normalized to `[]`) for unmapped-field discovery. The byte-budget
  retention policy checks, in production's own order, whether raw
  telemetry is disabled by config, whether this single snapshot exceeds
  `maxRawPayloadBytes`, whether the execution has already reached
  `maxRawSnapshotsPerExecution`, and whether adding this snapshot would
  exceed `maxRawBytesPerExecution` -- four new `Ros.Infrastructure.Work.
  FileWorkConfigRepository` readers, matching production's own defaults.
  Every discovered (unmapped) field gets its own `unknown`-status,
  `providerField`-keyed capability, whether or not the raw snapshot itself
  was actually retained.
- **The three derived quality metrics** (`telemetry.redactions`,
  `telemetry.unknown_fields`, `telemetry.raw_snapshots_omitted`) are
  synthesized as ordinary metric-normalization inputs (a small
  `syntheticMetricNode` helper) and routed through the same
  `normalizeAndAppendIngestedMetric` path every other ingested metric
  uses, rather than a separate write path -- one mutation function to
  keep correct, not two.
- **Provenance sources** accumulate across every ingest call, deduped by a
  content digest of each source object (mirroring production's own
  `[...new Map(sources.map(s => [digest(s), s])).values()]`).

A new general `CanonicalJson.stabilize`/`contentDigest` (in
`Ros.Infrastructure.Json`) mirrors production's `stable()` for genuinely
arbitrary, caller-supplied JSON -- a raw payload, an ingested event or
quality signal, a metric's own `dimensions`/`pricing` -- where the key set
and nesting depth are not known in advance, unlike every prior
content-addressed digest in this migration (an event's `eventId`, a
derived metric's `measurementId`), which could hand-build an
already-sorted flat object literal because their own field set was fixed
and shallow.

`--input`'s file (or `-` for stdin) is read and byte-checked at the CLI
layer (`Ros.Cli.Program`), matching this migration's own established split
of disk I/O away from the pure effect functions; a single JSON value is
tried first, falling back to JSON Lines (one value per non-blank line) --
production's own `readTelemetryInput` contract exactly, reproduced as
`FileTelemetryFinalizationRepository.parseIngestInput`.

## Phase A / MIG-08, increment 15: `telemetry classify` — a thin wrapper over generic ingest

`telemetry classify --classification NAME [...] [--rationale TEXT]
[--evidence-link LINK]* [--rd-context FILE]` is production's own thin
wrapper over `ingestTelemetry`: it builds a synthetic ingest whose only
real content is a constructed `classification` object and an explicitly
empty `raw: {}`, then calls the same `ingestTelemetry(root, target,
input)` `telemetry ingest` already does, with no `adapter` override
(always `"generic"`). Since increment 14 already ported the entire
generic-ingest mutation path, this increment is almost entirely a CLI
wrapper: `FileTelemetryFinalizationRepository.classifyTarget` (new)
builds the `classification` object (`types`, `rationale`, `evidence`,
`rd`) and the synthetic input, then delegates to `ingestTarget`
unchanged -- no second mutation path.

The one genuinely distinct piece of production behavior is
`classification`'s own `snapshotId`: `` `classification-${Date.now()}` ``
-- a **real-clock millisecond timestamp**, not a content digest like
every other ingest's default. This means a `telemetry classify` call is
essentially never deduplicated the way a repeated `telemetry ingest`
snapshot would be: each call gets its own event and its own
`rawTelemetry` entry (an empty `{}` payload, since `raw: {}` is always
explicit), reproduced here via `DateTimeOffset.UtcNow.
ToUnixTimeMilliseconds()` rather than the content-addressed formula
`ingestTarget`'s own fallback would otherwise compute (moot regardless,
since `classifyTarget` always supplies an explicit `snapshotId` up
front, the same way a caller-supplied one on `telemetry ingest` would).

`--rd-context`'s file (or `-` for stdin) is read exactly like `telemetry
ingest`'s own `--input` -- same byte-limit check, same JSON/JSON-Lines
parsing via `parseIngestInput` -- reused rather than duplicated, since
production's own `readTelemetryInput` call is identical in both
commands. Checking for at least one `--classification` happens before
that read, matching production's own check order exactly (an absent or
malformed `--rd-context` file never masks the "no classification"
rejection).

## Phase A / MIG-08, increment 16: `telemetry start` — the first command that recovers-or-creates telemetry outside a work transition

`telemetry start WORKITEMID [--classification NAME]* [--classification-rationale
TEXT] [--quiet]` is production's own manual telemetry-attach command:
unlike `work start`/`work resume`, it has no state transition of its own
to plan -- it recovers or creates a telemetry execution for a work item
that is already `active` or `blocked`, links it into
`telemetryExecutionIds`, and returns. Production rejects both "the work
item is not in context" and "the work item is not active/blocked" with
the exact same message (`work item '{id}' must be active or blocked
before starting telemetry`), reproduced here from three separate F# code
paths (missing context file, item not found, wrong `semanticState`) that
all resolve to the identical string.

Unlike `work start`/`resume`, whose telemetry resolution is
transition-plan-shaped (`WorkContextPlanning`/`resolveContextTelemetry`,
consumed by `resolveContextTelemetryWithCreation` in `Ros.Cli.Program`),
`telemetry start` has no plan to build. `FileTelemetryFinalizationRepository.
startTarget` (new) therefore calls the same low-level pure
`ExecutionLinkRecovery.decide` directly against
`FileTelemetryStateRepository.readCandidates`, then performs a narrow,
self-contained read-modify-write of `.ros/context/current.json` --
deliberately bypassing `FileWorkContextRepository`'s only public API
(`applyContextPlan`/`applyContextPlanWithConclusions`), which is shaped
for a transition's item/event/telemetry plan, not a bare link-in-place.
`resolveOrCreateExecution` mirrors production's `recoverOrStartExecution`
exactly: filter candidates by work item, `Active` status, and exclusion
of already-linked ids; `Recover` an unambiguous detached candidate;
`RejectAmbiguous` on more than one (with production's exact `; rerun with
--execution-id one of: ...` message); otherwise create a new execution
via `FileTelemetryExecutionRepository.createExecution` (bounded to a
single attempt, since there is no plan-resolution retry loop here).

`classificationRationale` (`tools/ros_telemetry.mjs`'s `rationale:
options.classificationRationale ?? null`) is a real, previously-unported
production field: `CreateExecutionRequest` gained one new optional field
for it, `None` at both pre-existing call sites (`work start`/`resume`
never supply one, matching production's own CLI).

This increment deliberately excludes `--execution-id` and the eleven
identity-override flags (`--provider`, `--model`, `--model-version`,
`--runtime`, `--runtime-version`, `--session`, `--conversation`, `--run`,
`--agent`, `--subagent`, `--parent-execution`) that production's own
`telemetry start` exposes via `telemetryIdentityOptions`. Unlike prior
increments' exclusions (flags no current CLI command exposed at all),
these are real, reachable production behavior -- supporting them requires
extending `createExecution` itself to accept an explicit execution id and
identity-override layering, judged a separately-scoped future slice. Each
is rejected as a loud gap (exit code 2, `telemetry start FLAG is not yet
supported by this CLI`) rather than silently ignored.

`telemetry start` commits via a plain context-file write plus
`RegistryLock.acquire root "work-protocol"` (matching production's
`withWorkProtocol` lock and its `recoverWorkStateTransaction`/
`recoverBacklogStateTransaction` recovery calls) -- but, matching
production's own `renderedEventLog(root, [])`, it never appends to
`.ros/events/events.jsonl`: no event log entry is written by this
command, unlike `work start`/`resume`/`complete`.

## Phase A / MIG-08, increment 17: `adapter call` — the external work-system adapter conformance test

`adapter call --store FILE --request FILE` is production's dependency-free
file-based conformance adapter for the external work-system contract
(`DF-ROS-2026-A007`, `docs/work-adapter-contract.md`,
`schemas/work-adapter-request.schema.json`/`work-adapter-result.schema.json`)
-- a test double exercising the same `getWorkItem`/`transitionWorkItem`/
`publishRepositoryEvent` request/result contract a real production
adapter must satisfy, without this repository choosing a project-
management vendor. Unlike every other increment so far, this one is not
telemetry: it belongs to the work-lifecycle feature (`docs/features/
work-lifecycle/manifest.md`), and touches no telemetry execution, work
context, or lock at all -- production's own `callFileAdapter` operates
entirely on a caller-named, standalone JSON store file with no
`work-protocol` lease.

Work items and events inside that store are caller-defined, open-ended
JSON (production reads and writes only a handful of fields: a work
item's `state`/`updatedBy`, an event's `eventId`). Mirroring this
migration's established split between a pure decision and its
Infrastructure effect, `Ros.Domain.Work.AdapterContract.decide` (new)
is a pure function from a `DecisionInput` (the primitive fields
production actually inspects) to an `AdapterDecision` union mirroring
`callFileAdapter`'s exact branch order: protocol-version mismatch and
unsupported-operation checks first (production's own
`validateAdapterRequest`, which never touches the store file at all for
these two), then -- only once the store is loaded -- a cached
`requestId` replay (no mutation, matching production's own early
return), then repository authorization, then `simulateOutcome:
"unknown"` fault injection regardless of operation or scope, then each
operation's own forbidden/not-found/conflict checks. `firstMissingField`
mirrors production's `validateAdapterRequest`'s first loop -- a missing
required field is a thrown `Error` in production, surfaced identically
here as `Result.Error` before the store is ever touched.

`Ros.Infrastructure.Work.FileAdapterRepository.call` (new) performs the
actual JSON store read/mutate/write around that decision: a
`transitionWorkItem` success mutates only `state`/`updatedBy` on the
existing work item node in place, preserving every other caller-defined
field verbatim; a `publishRepositoryEvent` success appends the event
only when its `eventId` is not already present, matching production's
own de-duplication; every result (success, failure, or unknown alike) is
cached under its `requestId` and the whole store is rewritten, exactly
once, per non-replayed call. Exit codes match production's own CLI
mapping: 0 success, 1 failure, 2 unknown.

`adapter publish` (the paired `.ros/publications.json`-writing command)
remains Node-only, its own future scoping choice.

## Phase A / MIG-08, increment 18: `adapter publish` — the paired event-republish command

`adapter publish --target FILE` is production's own event-republish
command, paired with increment 17's `adapter call` but otherwise
unrelated to it: it reads every event from `.ros/events/events.jsonl`,
appends whichever are not already present (by `eventId`) in `target`
(a caller-named destination JSONL file, unrelated to the adapter store),
then refreshes `.ros/publications.json`'s receipt for every source event
-- even an already-published one -- with a fresh `publishedAt` timestamp,
matching production's own unconditional per-call refresh (the receipt's
`publishedAt` changes on every republish, not just when a new event is
appended). This is a real, non-obvious quirk verified against real Node
before writing any test: a duplicate-only republish still rewrites every
receipt.

`Ros.Infrastructure.Work.FileAdapterRepository.publish` (new, alongside
`call` in the same module) mirrors production's own read-order exactly:
source events, then the destination's existing events (for the
`known` eventId set), then creates the destination's parent directory
before appending -- a failure there (e.g. a path segment already exists
as a non-directory file) propagates as `Error` before either the
destination or `.ros/publications.json` is touched at all, matching
production's own thrown, uncaught error and its real quirk that a
zero-event publish still creates the target directory and an empty
`{}` `publications.json`, just never a destination file (an empty
append loop creates nothing). Appended events are written one compact
JSON line each, verbatim, matching production's own `JSON.stringify`.

## `work resume`'s `parentExecutionId`: a corrected, not replicated, production defect

While scoping `resume`'s `parentExecutionId` linkage as a candidate next
slice, manual smoke-testing against real Node revealed it is not actually
a missing F# feature at all: **production's own implementation is dead
code.** `transitionUnlocked`'s `resume` branch computes
`parentExecutionId: prior?.executionId ?? null` (the work item's most
recently created execution, regardless of status) and passes it into
`recoverOrStartExecution`/`startExecution` as a option sibling to
`identity: options.telemetryIdentity`. But `startExecution` builds
identity via `discoverIdentity(options.identity ?? options)`, and
`options.identity` (`telemetryIdentityOptions(args)`) is *always* a
truthy object -- even with every field `undefined` -- so it unconditionally
wins the `??` over the sibling `options` object `parentExecutionId` was
actually set on. Production's own computed value is silently discarded
every time; confirmed against real, unpatched Node: the created record's
`identity.parentExecutionId` is always `null`, never the prior execution.

Every other established increment in this migration has deliberately
*reproduced* a real production quirk once confirmed, on the reasoning
that production is the authority pending the switch decision. This one
is treated differently, on the user's explicit instruction: Node is
being deprecated rather than patched, so there is no value in
replicating a bug in the very code intended to replace it. `runWorkResume`
now supplies the work item's most recently created execution
(`FileTelemetryQueryRepository.readLatestExecutionId`, new) as
`CreateExecutionRequest.ParentExecutionId` (also new, threaded into
`Identity.discover` via `IdentityInputs.ParentExecutionId`, which already
existed and already worked correctly -- the defect is purely in how
production's own JavaScript assembles the options object, not in the
identity-discovery logic itself) whenever `resolveContextTelemetryWithCreation`
halts on `PendingNewExecution` during a resume; every other action
(`begin`/`block`/`complete`) still supplies `None`, matching production's
own CLI, which never threads a `parentExecutionId` through those paths
at all.

`tests/work-resume-parent-execution-fsharp-differential.test.mjs`
documents this as a deliberate, known divergence rather than claiming
parity: it runs both real Node (confirming `parentExecutionId` stays
`null`, the confirmed defect) and the F# CLI (confirming it correctly
links to the prior execution) side by side, so the divergence stays
visible and intentional rather than silently drifting.

## Phase A / MIG-08, increment 19: `telemetry ingest --adapter openai-codex` — the first real provider-specific field mapping

Every write-path telemetry-producer increment so far ported the `generic`
adapter only -- the one with zero provider-specific field mapping.
`openai-codex` is the first of the nine real adapter names
(`TELEMETRY_ADAPTERS`) to get its own mapping ported: production's
`adaptOpenAICodex` translates a Codex `exec --json` event stream (or a
single event object) into normalized token/context metrics.

The port revealed a design win worth calling out: the generic ingest
pipeline shipped in an earlier increment (`ingestAdaptedGeneric` --
snapshot dedup, identity merge, capability upsert, metric normalization,
raw redaction/retention, quality-signal handling) turns out to be
entirely adapter-agnostic once an `AdaptedSnapshot` value exists. Porting
a new adapter therefore means writing only the *adaptation* function
(raw input to `AdaptedSnapshot`) and adding one dispatch arm in
`ingestTarget`; the entire downstream mutation pipeline is reused
unchanged, matching production's own `adaptInput`/`ingestAdapted` split
exactly.

`adaptOpenAICodex` (new, alongside `adaptGeneric` in the same module)
mirrors production field-for-field: `completed` entries (`type ===
"turn.completed"` or any `usage` object at all) each declare a fixed
capability for all six usage fields (`input_tokens`/`output_tokens`/
`cached_input_tokens`/`cache_write_input_tokens`/
`reasoning_output_tokens`/`total_tokens`), present or not, but a metric
only for the fields actually present -- `context.window_size` is the one
metric with deliberately no paired capability declaration in the adapter
itself, a real quirk reproduced exactly (though the shared
metric-recording pipeline still upserts a capability for it regardless,
since that step applies uniformly to every recorded metric independent
of the adapter). `identity.model` resolves from the *last* record
(searched in reverse) carrying a truthy `server_model` or `model`,
regardless of whether that record was itself "completed" -- confirmed
against real Node with a two-record fixture where the identity-bearing
record is not itself a turn.

Every other real adapter name (`anthropic-claude-statusline`,
`anthropic-claude-hook`, `anthropic-claude-otel`, `google-gemini-hook`,
`google-gemini-otel`, `github-copilot-hook`, `github-copilot-otel`,
`otel-json`) remains its own future MIG-08 slice, still rejected outright
(exit 2) exactly as before.

## Phase A / MIG-08, increment 20: the three hook adapters — capabilities derived from what fired, not a fixed declaration

`anthropic-claude-hook`, `google-gemini-hook`, and `github-copilot-hook`
are three CLI-visible adapter names that all resolve to production's
single `adaptHook` function, parameterized only by an identity
provider/runtime pair (`anthropic`/`claude-code`, `google`/`gemini-cli`,
`github`/`copilot` respectively) — the same one-function/three-name
shape production itself uses (`adaptInput`'s own three `if` branches
each call `adaptHook` with different `identityDefaults`). The F# port
mirrors this with a single `adaptHook` taking `identityProvider`/
`identityRuntime` as plain string parameters and a three-way match in
`ingestTarget`'s dispatch, rather than three near-duplicate functions.

Unlike `adaptOpenAICodex`'s fixed six-field capability declaration
(increment 19, above), `adaptHook`'s capabilities are derived strictly
1:1 from whichever metrics actually fired for a given payload — there is
no "recognized but unavailable" declaration at all in this adapter. The
hook payload's `hook_event_name` (falling back to `hookEventName`, then
`event`) is matched, case-insensitively, against five independent
(non-exclusive — more than one can match the same payload) precompiled
regex patterns: `posttooluse|aftertool|toolresult`, `subagentstart`,
`postcompact`, `permissionrequest`, `permissiondenied`. A tool-use match
always contributes two metrics together — `tool.calls` and a
`toolCategoryMetric`-bucketed metric (`tool_name` matched against 11
substring patterns — shell/file-read/file-write/search/web/repository/
test/build/deploy/database/api — first match wins, falling back to
`tool.external_service_calls`) — plus a third, conditional
`tool.failures` metric only when the payload's own `error` field is
truthy, its `tool_response.error` is truthy, or `success` is explicitly
`false`. Every other pattern match contributes exactly one metric
(`agent.subagents_spawned`, `context.compactions`,
`agent.approvals_requested`, `agent.approvals_denied`). Regardless of how
many of the five patterns match, exactly one `runtime.{hookName}` event
is emitted per call (the hook name lowercased and non-alphanumeric runs
collapsed to `-`), matching production's own single-event-per-call
shape.

Identity resolves the same way for all three adapter names: `sessionId`
from `session_id`, `model` from `model.id` when `model` is an object or
`model` itself when it is a string, `agentId` from `agent_type` — each
`null` when absent, matching production's own `??`-chained field
resolution exactly. Confirmed against real Node with differential
fixtures covering all three adapter names and four of the five hook-name
patterns (`PostToolUse` with and without an error, `SubagentStart`,
`PermissionDenied`), each producing byte-identical identity, capability,
metric, and event sets.

## Phase A / MIG-08, increment 21: `anthropic-claude-statusline` — a single snapshot, not an event stream

`anthropic-claude-statusline` is the fourth real adapter name ported, and
the first whose input is a single snapshot object rather than an event
stream or per-record collection — there is no iteration at all in
`adaptClaudeStatusline`, unlike `adaptOpenAICodex`'s per-entry loop or
`adaptHook`'s per-payload pattern matching.

Its field mapping falls into two groups, matching production's own two
separate loops. Three top-level fields — `context.window_size`
(`context_window.context_window_size`), `context.utilization`
(`context_window.used_percentage / 100`), and `cost.session_cumulative`
(`cost.total_cost_usd`) — each always get a capability declaration
(present or not), and, when present, a metric carrying an explicit
`quality` ("observed" for the first two, "estimated" for cost) and a
`confidence` key that is always present (`"medium"` when estimated,
JSON `null` otherwise) — unlike every metric in every other adapter in
this migration, where `confidence` (like every other optional extra) is
simply absent when not applicable. `cost.session_cumulative` is also the
one field whose *capability* status becomes `"estimated"` rather than
`"supported-observed"` when present — a real quirk, since a statusline
snapshot cannot directly observe session cost, only estimate it — and
the one metric whose extras include `currency: "USD"`, gated on the
metric id's own `cost.` prefix rather than a fixed adapter-wide flag.

Four `current_usage` token fields (`input_tokens`/`output_tokens`/
`cache_creation_input_tokens`/`cache_read_input_tokens`, mapped to
`context.current_input_tokens`/`context.current_output_tokens`/
`context.current_cache_write_tokens`/`context.current_cache_read_tokens`)
form the second group: each always gets a plain capability declaration
(present or not, no `"estimated"` special case), and, when present, a
metric with no `quality`/`confidence`/`currency` extras at all — a
narrower shape than the first group's metrics, again matching
production's own second, simpler loop exactly.

This adapter emits no events of its own at all — the returned `events`
array is always empty, so only the shared ingest pipeline's own
`telemetry.snapshot.ingested` bookkeeping event appears in the stored
record. `collectedAt` is also never overridden by an input timestamp
field (unlike `adaptHook`'s `input.timestamp ?? collectedAt`), since a
statusline snapshot carries no per-event timestamp to prefer. Confirmed
against real Node with differential fixtures covering a fully-populated
snapshot, a snapshot with nothing present (every capability
`supported-unavailable`, zero metrics), and the `cost.session_cumulative`
`"estimated"`-status quirk in isolation — all producing byte-identical
identity, capability, metric, and event sets. Notably, the shared
metric-recording pipeline (`normalizeAndAppendIngestedMetric`) normalizes
every stored metric to the same full field set regardless of which
extras the adapter itself supplied, so this adapter's narrower raw shape
for the `current_usage` fields converges with the richer shape from the
top-level fields once persisted — parity holds end-to-end even though
the two groups' raw shapes differ.

## Phase A / MIG-08, increment 22: the OTel adapter family — the last real adapter mapping, and the first with no fixed schema at all

`anthropic-claude-otel`, `google-gemini-otel`, `github-copilot-otel`, and
`otel-json` are four CLI-visible adapter names that all resolve to
production's single `adaptOtel` function — the same one-function/
multi-name shape as `adaptHook`, but with one more name and, unlike
every prior adapter (each keyed to one specific provider's JSON shape),
no fixed field schema of its own at all. Every field `adaptOtel` reads
is looked up via a shared `firstValue` helper across a per-record set of
"nested candidates" — the record itself, `attributes`,
`resource.attributes`, `body`, `dataPoint.attributes`, checked in that
fixed order, first candidate to carry any of the requested field names
wins — matching the range of real shapes OTLP JSON exports and Gemini
CLI's own metric events actually carry without needing a name apiece.
`otel-json` is the one adapter name with no identity seed at all
(production's own `identityDefaults = {}` default resolves both
`provider` and `runtime` to `"unknown"`); the F# port passes `"unknown"`/
`"unknown"` explicitly at its own dispatch arm rather than threading an
optional-parameter default through `adaptOtel`, keeping every call site
uniform.

Input shape resolves the same way production's own `Array.isArray(input)
? input : input.records ?? input.events ?? [input]` does: an array of
records, an object carrying a `records` or `events` array, or (falling
through both) the single object itself treated as one record.
`identity.provider`/`runtime`/`model`/`sessionId` are *mutable across the
whole record stream* — each record can overwrite them with its own
discovered value, and the last record to carry one wins, unlike every
other adapter in this migration where identity resolves once from a
fixed vantage point (the last record in reverse, or the input object
directly). Metrics fall into several independently-triggered branches
per record: six "direct" field mappings (token/cache/timing fields,
each always carrying a `dimensions.event` key -- populated with the
record's own `name` field when present, an empty object otherwise, never
omitted, unlike every other adapter's optional `dimensions`); a
name-and-type-keyed token-usage mapping (`gemini_cli.token.usage`/
`gen_ai.client.token.usage`, mapping five possible `type` values to
their own metric ids); `model.requests`/`model.request_failures` for
API-request-shaped records; `tool.calls`/`tool.failures` for
tool-result-shaped records; and three Gemini-CLI-specific single-metric
mappings (`agent.turns`, `context.compactions`,
`runtime.memory_peak_bytes` gated on `memory_type === "rss"`). Like
`adaptHook`, capabilities are declared 1:1 from whichever metrics
actually fired, each keyed to the metric's own recorded `source` object
(which can itself differ across metrics from different records, since
`provider`/`runtime` can change mid-stream) — there is no "recognized
but unavailable" declaration at all.

`runtimeTimestamp` — production's own heuristic for telling apart an
already-ISO-8601 timestamp string (passed through verbatim), a numeric
epoch value in nanoseconds/milliseconds/seconds (told apart purely by
magnitude thresholds and converted to a millisecond-precision ISO
string), and anything else (falling back to the ingest-time
`collectedAt`) — is ported for the realistic timestamp shapes real OTel
exports carry. One corner is deliberately left unreproduced, mirroring
`hasTruthyField`'s own documented zero-is-falsy gap: an exotic non-ISO
date string `Date.parse` would still accept (e.g. `"January 1, 2026"`)
is treated as non-timestamp text here rather than parsed, since no
realistic OTel export emits timestamps in that shape.

This closes MIG-08's telemetry-ingest adapter inventory: every name in
`Ros.Domain.Telemetry.TelemetryAdapters.all` now dispatches to a real
adaptation function in `ingestTarget`, so its "not yet supported by this
CLI" rejection branch became permanently unreachable and was retired
along with the `supportedAdapters` allowlist that guarded it. Confirmed
against real Node with differential fixtures covering a record exercising
every branch at once (nested `resource.attributes` provider discovery,
a numeric nanosecond timestamp, direct fields, the token-usage type
mapping, a failed API request, a tool result with a string `"false"`
success, and all three Gemini-CLI-specific metrics), the `records`-object
input-wrapping shape, and each of the four adapter names' own identity
seeding — all producing byte-identical identity, capability, metric, and
event sets.

## Phase A / MIG-08, increment 23: `telemetry start`'s `--execution-id` and identity-override flags — the last piece of MIG-08's own scope

`telemetry start`'s eighth increment shipped the command itself but
deliberately excluded and loudly rejected (exit 2) two things production's
own CLI exposes only here: `--execution-id` and the eleven identity-override
flags (`--provider`/`--model`/`--model-version`/`--runtime`/
`--runtime-version`/`--session`/`--conversation`/`--run`/`--agent`/
`--subagent`/`--parent-execution`). This increment supports both, closing
MIG-08's own command-surface scope entirely.

`--execution-id` needed no new decision logic at all. `Ros.Domain.
Telemetry.ExecutionLink.ExecutionLinkRecovery.decide`'s pure
`RequestedExecutionId` handling — already shipped, and already covered by
`TelemetryTests.fs` — was simply threaded through from the CLI for the
first time: `resolveOrCreateExecution` (`FileTelemetryFinalizationRepository`)
now accepts a `requestedExecutionId: string option` parameter and passes
it straight into the `ExecutionLinkRequest` it already built, rather than
hardcoding `None`. A matching detached candidate is recovered; a
non-matching id with other detached candidates present rejects with
production's own exact message (`detached telemetry execution must be
linked before creating '{requested}' for '{workItemId}'; rerun with
--execution-id {firstCandidate}` — `firstCandidate` taken from
`ExecutionLinkRecovery.decide`'s own already-sorted `RejectDetachedConflict`
list, matching production's own `candidates[0]` after its identical
`.sort()`); with no detached candidates at all, the requested id flows
through unchanged to become the newly created execution's own id.

The eleven identity flags reuse `Ros.Domain.Telemetry.Identity.discover`'s
already-complete override handling, built earlier in this migration and
never missing a single field (confirmed by re-reading it before writing
any new code) — the actual gap was purely in the CLI-to-repository
wiring, not the domain logic. `FileTelemetryExecutionRepository.
CreateExecutionRequest` gained two new fields: `ExecutionId: string
option` (used verbatim instead of a freshly generated id, mirroring
production's own `options.executionId ?? executionId()`) and
`IdentityOverrides: IdentityInputs` (replacing the previous standalone
`ParentExecutionId: string option` field, which is folded into this one
as one of the same eleven slots). A new private `mergeIdentityOverrides`
overlays the eleven explicit-override fields from the caller's value onto
the real environment-derived `IdentityInputs`, mirroring production's own
`discoverIdentity(options.identity ?? options)` exactly: an override field
wins when present, otherwise the environment-derived field (and each
field's own further environment-variable fallback chain inside `Identity.
discover`) still applies. The two pre-existing call sites (`work start`/
`resume`/`block`/`complete`, via `Ros.Cli.Program.
resolveContextTelemetryWithCreation`, and `telemetry start`'s own
`resolveOrCreateExecution`) both updated to the new field shape, each
still supplying only `ParentExecutionId` (or nothing) on `IdentityOverrides`
— no behavior change for any of them.

Confirmed against real Node by driving the actual `ros` CLI wrapper
directly (not just the exported function) with differential fixtures
covering: `--execution-id` recovering a matching detached candidate;
`--execution-id` rejecting a non-matching id with production's exact
rerun message when other candidates exist; `--execution-id` becoming a
freshly created execution's own id when no candidate exists at all; and
all eleven identity flags supplied together producing an identity object
byte-identical to production's own. This closes MIG-08's own
command-surface scope entirely — every command and adapter name
production's own CLI exposes for execution/telemetry now has real F#
effect parity.

## Phase A / MIG-08, increment 24: `telemetryFindings` — the largest single validator in this migration

Outside MIG-08's own command-surface scope, but a direct prerequisite for
the not-yet-scoped `validate` command unification (work-lifecycle
manifest): production's `validate(root)` combines artifact findings,
registry staleness, `workFindings`, `queueFindings`, and
`telemetryFindings` into one sorted array. The first four contributors
already had real F# equivalents (`ArtifactPolicy.validate`,
`ArtifactOperations.checkRegistries`, `Ros.Domain.Work.Attribution`,
`Ros.Domain.Work.QueueValidation`); `telemetryFindings` did not, and at
roughly 210 lines of dense, independent checks in production, it is
larger than any other single function ported in this migration —
comparable to the entire 14-increment telemetry-ingest adapter family
combined.

Every other validator in this migration follows the same shape:
Infrastructure parses raw JSON into a small, loosely-typed record
(`BacklogQueueItemRecord`, three plain fields), and Domain decides over
that record, checking a raw string against a known set rather than a
parsed enum — `QueueValidation`'s own doc comment states why explicitly:
"production reports an unrecognized status... as a finding rather than
rejecting parse." `telemetryFindings` follows the identical principle at
much greater depth, because the record it validates is much deeper:
11-field identity, capabilities with nested source and history, metrics
with nested source and pricing, quality signals, raw telemetry snapshots,
events, repository/links objects, and classification. A first draft of
this port kept the checking logic operating directly on `JsonNode`
inside `Ros.Infrastructure` end to end — faithful to production's own
permissive `?.`/`typeof` idiom, but a genuine violation of
`SDE-DOCTRINE-003` (Four-Tier Architecture): Tier 2 ("what legal change
may happen", including guards and invariants) is where a validation
decision belongs, and Tier 4 ("what actually happened") should only
report structural facts about raw data, never decide legality itself.
The shipped version corrects this: a wide family of plain `Parsed*`/
`Raw*` types (`Ros.Domain.Telemetry.Validation`) crosses the boundary —
no `JsonNode` reaches `Ros.Domain` at all — and `Ros.Domain.Telemetry.
TelemetryValidation` (Tier 2) makes every actual legality decision over
those types, while `Ros.Infrastructure.Work.
FileTelemetryValidationRepository` (Tier 4) does nothing but parse JSON
into them and report what it structurally found.

Two closed-union types carry real information the plain `option` types
this migration otherwise favors cannot: `FieldPresence<'a>`
(`KeyAbsent`/`KeyPresent of 'a option`) distinguishes a JSON key that is
genuinely absent from one that is present but unparseable or explicitly
`null` — needed because production's own JS uses two different
truthiness gates for this, and they disagree on `null`. Most "present but
invalid" checks (`lastAssessedAt`, `recordedAt`, `history`,
`historyOmitted`, `payloadBytes`) use `value !== undefined`, where an
explicit `null` counts as present (and, since `null` never satisfies any
of those fields' validity checks, always fires the finding). Metric
`confidence` alone uses the narrower `value !== null && value !==
undefined`, where an explicit `null` is treated exactly like an absent
key. A first pass at this port missed the distinction — confirmed
directly against real Node before any test existed: a genuine completed
real `work start` execution (whose own `confidence: null` field
production itself writes for every non-estimated metric) produced a
spurious finding until `FieldPresence` parsing was corrected to map
explicit `null` to `KeyAbsent` for this one field only. `IdentityField`
(`FieldAbsent`/`FieldNull`/`FieldString of string`/`FieldOtherInvalid`)
models each of the eleven identity fields' exact three-way shape the
same way. The config/registry early-exit sequence — production's own
three sequential early `return`s (malformed config, disabled-with-no-
reason, malformed registry) — is a small closed state machine,
`TelemetryValidationOutcome`, rather than a chain of independently
re-checked booleans.

One deliberate simplification: production's own registry loader
(`loadMetricRegistry`) throws on the first invalid entry, so this port's
`RawMetricRegistry`/`validateRegistry` reproduces that exact early-exit
behavior rather than collecting every registry-level problem — matching
production, this is a config-authoring error path, not a per-execution
one.

Confirmed against real Node across a wide scenario sweep before any test
was written, each byte-for-byte identical: a clean real `work start`; a
fully completed, evidence-satisfied, real `work complete` (real
change-summary metrics, zero findings); disabled telemetry with no
reason; a malformed metric registry; a deliberately broken hand-written
record combining a non-portable executionId, a filename mismatch, an
empty workItemId, a non-string identity field, an invalid status, a
duplicate classification type, an invalid capability timestamp, a
negative metric value, and a ROS-derived/quality mismatch, all at once;
capability history recorded out of chronological order; an unrecognized
quality-signal detector; an unredacted sensitive raw-payload field; a
telemetry execution not linked back from its own work item; and a
completed work item whose linked execution was never finalized. This
increment does not itself unify `validate` — it only makes the last
missing contributor to that eventual command real.

## Phase A, increment 11: unified `validate` — every contributor was already real

With `telemetryFindings` shipped, all five of production's own
`validate(root)` contributors (`tools/ros_cli.mjs`) already had a real F#
equivalent: `ArtifactPolicy.validate` (artifact semantic + parse
findings), `ArtifactOperations.checkRegistries` (registry staleness),
`Ros.Domain.Work.Attribution` (`workFindings`),
`Ros.Domain.Work.QueueValidation` (`queueFindings`), and
`Ros.Domain.Telemetry.TelemetryValidation` (`telemetryFindings`).
Unifying them into one `ros-fs validate [--json]` command matching
production's exact combined, sorted output is therefore pure
orchestration — no contributor's own decision changes — and lives at the
CLI composition layer (`Ros.Cli.Program.runValidateUnified`) rather than
introducing a new cross-feature Application module, consistent with how
several other multi-repository CLI commands in this migration already
compose directly at the dispatch layer (`work validate` itself already
composes config, context, and Git observation inline).

Two small pieces of genuine logic were needed, not just composition.
First, `ArtifactOperations.checkRegistries`'s own result already bundles
the same parse findings `ArtifactOperations.validate` separately
returns (`loaded.ParseFindings @ stale`), so using it verbatim would
double-count them; the unified command filters its result down to only
the `"registry is stale"` messages before merging. Second, production's
own `findingRecord` computes a finding's repair hint from three
branches — a stale-registry message, a `field === "work_items"` finding,
or the generic default — but the pre-existing `Ros.Contracts.Cli.
FindingContract.repair` (built for `artifacts validate` alone, which
never produces a `work_items`-fielded finding) only implemented the
first and third. This increment adds the missing branch; it is a safe,
backward-compatible addition, since no artifact-only finding could ever
have exercised it before.

Sorting mirrors production's own `findings.sort((a, b) => [a.path,
a.field, a.message].join("\0").localeCompare(...))` with ordinal string
comparison over the same joined tuple, rather than replicating Node's
locale-aware `localeCompare` collation — a deliberate, documented
simplification, since every path/field/message this repository's own
contributors produce is plain ASCII, where ordinal and locale-aware
comparison agree.

Confirmed against real Node across a combined scenario exercising all
five contributors and the sort together — a backlog-item invalid
status, disabled telemetry with no reason, a stale registry, a
malformed artifact (bad identifier, wrong filename), and an
unattributed Git change, all present in the same repository at once —
producing a byte-identical sorted finding list in both `--json` and
plain-text form (including the text form's per-finding `ERROR .../ REPAIR
...` rendering and final error count). `status` is now unblocked: its
own dependency on this unification is resolved, and it remains the one
row `EV-ROS-2026-A046` found never assigned to MIG-07/MIG-08's scope.

## Work-state recovery seam

The second MIG-05 sub-slice defines a bounded `work-state` recovery journal for
the final two live-transition projections, in their characterized order:
`.ros/events/events.jsonl` then `.ros/context/current.json`. Each declared write
stores the before-content SHA-256, intended after-content SHA-256, and complete
replacement content. Recovery preflights both targets before writing: only the
recorded before state or already-applied after state is legal. Divergence is an
`Indeterminate` outcome and leaves the journal and both targets untouched.

Production Node live-work transitions now use this record while holding the
existing `work-protocol` lease. Before making a new transition they recover any
compatible pending record; normal completion leaves no journal. The F# and
Node implementations accept the same JSON shape, hashes, ordered targets, and
divergence rules. This is shared recovery infrastructure, not an authority
switch: Node still decides and performs every state-changing work command.

Telemetry execution files and backlog queue/projection files remain explicitly
outside this two-file unit. Telemetry effects currently precede journal
preparation, so backlink/finalization validation detects—but does not
automatically repair—an interruption in that earlier interval. Backlog and
telemetry require bounded store-specific recovery designs.

## Backlog-state recovery seam

The third MIG-05 sub-slice gives the captured backlog its own bounded recovery
unit: authoritative `.ros/work/queue.json` followed by derived
`.ros/work/queue.md`. Node and F# share a versioned, hash-preconditioned journal
contract. Production capture, update, attachment-reference, and backlog
transition operations now hold `work-protocol` across the complete
read/modify/write cycle, and live transitions acquire that same capability.

The unit deliberately excludes live context/events, telemetry, and attachment
bytes. Attachment content is written before its queue reference, preserving the
existing failure direction: interruption can leave an orphan but not a missing
referenced file. The Markdown file remains a rebuildable projection and is
regenerated on backlog changes, not claimed as independent authority.

## Telemetry-link recovery seam

Telemetry execution JSON differs from the multi-file projections: each record
is already atomically replaced under an execution-specific lease. MIG-05
therefore keeps that boundary and addresses the cross-store creation seam.
Execution creation and context linking are composed under `work-protocol`, and
the backlink is written through the existing work-state recovery record.

The typed F# decision distinguishes starting a new record, recovering exactly
one detached active/finalized record, requested-ID conflict, and ambiguous
detached evidence. Production uses the same behavior. An exact existing
`--execution-id` is the repair/idempotency key; ambiguous recovery never chooses
silently. Provider mapping and metric semantics remain MIG-08 work.

## State architecture

| Lifecycle | Closed state now | Authority | Migration treatment |
|---|---|---|---|
| Backlog | captured, ready, blocked, abandoned | queue + work kernel | later typed transition slice |
| Live work | ready, active, blocked, complete; configured local mapping | context + work kernel | later typed transition/evidence slice |
| Execution | active, finalized | telemetry kernel | later typed lifecycle slice |
| Metric capability | supported-observed, supported-unavailable, unsupported, unknown, derived, estimated | telemetry kernel/catalog | typed stable cases plus open provider fields later |
| Artifact lifecycle | kind-specific status vocabularies | artifact policy | first slice validates without inventing a generic state machine |
| Publication | pending plus success/failure/unknown adapter outcome | events/receipts/adapters | defer until retry/acknowledgement contract exists |

For every state-changing future slice, model legal transitions, guards,
required evidence/capability, requested effects, rejection/unknown outcomes,
retry semantics, and version checks before moving the writer.

## Effect boundaries

Filesystem, Git, process execution, clock, environment, HTTP, GitHub, npm,
provider input, secrets, and persistence are host effects. Domain decisions
receive typed observations and return typed requests/outcomes. External process
exit is not automatically semantic success.

## Sources of truth

- canonical research/governance: Markdown records;
- registries: deterministic disposable projection;
- captured backlog: `.ros/work/queue.json`;
- live/completed work: `.ros/context/current.json`, with events as chronology;
- execution facts: per-execution versioned JSON;
- metric meanings: `telemetry/metrics.json`;
- configuration: versioned `ros.json`;
- project membership: current hub registry, in its separate bounded context;
- release version: committed package metadata plus explicit registry observation.

No database, event store, Project Administration authority, or Time Entry model
is introduced by this migration.

## Retained adapters

Node/npm bootstrap, tiny launchers, Node HTTP servers, TypeScript clients,
provider adapters, GitHub Actions YAML, npm publication, profile manifests, and
external tools remain where they are genuinely platform-specific. Retention is
not permanent approval for domain logic inside them; the inventory identifies
which decisions move in later slices.

## Distribution boundary

The first F# surface is framework-dependent `.NET 10` and repository-local.
It is not added to starter profiles or selected by `./ros`. A production
distribution decision requires explicit macOS/Linux/Windows installation,
startup, size, offline/update, checksum, version-selection, and rollback data.

## Architecture fitness rules

1. `Ros.Domain` may reference only FSharp.Core/runtime primitives.
2. `Ros.Application` may reference Domain/Contracts, never CLI or
   Infrastructure.
3. Infrastructure and CLI point inward; composition happens only at the edge.
4. Public bytes and diagnostics are produced by explicit codecs/renderers.
5. A feature slice includes semantics, handler, effect boundary, CLI, tests,
   telemetry/traceability, compatibility, and rollback.
6. Architecture verification must include a demonstrated rejection path.
7. A registry-file replacement is atomic per file; the bounded replay record
   recovers an incomplete generated-registry set but is not a general-purpose
   transaction facility.
