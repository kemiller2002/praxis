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
