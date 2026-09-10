# Feature Manifest — Work lifecycle

## Purpose

Route changes to the repository-local backlog, live work lifecycle, evidence
obligations, attachments, events, and the local HTTP presentation adapter.

## Ownership

- State, including presentation state: `.ros/work/queue.json` owns captured
  backlog state; `.ros/context/current.json` owns live work state after start;
  `.ros/events/events.jsonl` records semantic events; decisions
  `DF-ROS-2026-A006`, `DF-ROS-2026-A007`, and `DF-ROS-2026-A008`.
- Transitions / commands / messages: `tools/ros_cli.mjs` symbols
  `captureWork`, `backlogTransition`, `startWork`, `transition`, `blockWork`,
  `updateWork`, and `attachFile`.
- Invariants and guards: `BACKLOG_TRANSITIONS`, `TRANSITIONS`,
  `SEMANTIC_STATES`, `WORK_ID_RE`, completion-evidence checks, and
  `effectiveStatus` in `tools/ros_cli.mjs`; typed transition and orchestration
  planning in `Ros.Domain.Work`.
- Capabilities / authority: `contextView` returns allowed actions and required
  evidence; file-adapter scopes are checked by `validateAdapterRequest`.
- Important effects and effect contracts: atomic JSON/text and a local
  work-protocol lock through `tools/ros_persistence.mjs`; separate versioned,
  hash-preconditioned event/context and backlog queue/projection recovery
  journals, plus typed Git evidence through the contract-compatible
  `tools/ros_git.mjs` process adapter. The shadow F# application observes
  completion evidence through a typed filesystem port after semantic guards,
  and observes real candidate telemetry executions through
  `Ros.Infrastructure.Work.FileTelemetryStateRepository` when resolving
  execution IDs. It also observes real Git status and configured
  `ros.json` meaningful/ignored path patterns by default when planning a
  context, through `Ros.Domain.Work.PathFilter` and
  `Ros.Infrastructure.Work.FileWorkConfigRepository`. A read-only
  `ros-fs work validate` diagnostic mirrors production `workFindings`
  (the work-attribution contributor to Node's `validate`), reading the
  same event log through `Ros.Infrastructure.Work.FileEventLogRepository`
  and the same `enforceAttribution` flag through
  `Ros.Infrastructure.Work.FileWorkConfigRepository.readEnforceAttribution`.
  A read-only `ros-fs work backlog-validate` diagnostic mirrors production
  `queueFindings` (the backlog-queue contributor to Node's `validate`),
  reading the raw `.ros/work/queue.json` rows through
  `Ros.Infrastructure.Work.FileBacklogQueueRepository`. `ros-fs work
  backlog-transition` (`DF-ROS-2026-A028` Phase A's first increment) is a
  real effect, not a diagnostic: it writes `.ros/work/queue.json` and
  `.ros/work/queue.md` for the backlog-only `ready`/`block`/`abandon`
  actions, under the same `work-protocol` file lock and `backlog-state`
  recovery journal production's own writer uses, via
  `FileBacklogQueueRepository.applyStateChange` (JSON-node surgery
  preserving every unmodeled field) and `Ros.Domain.Work.QueuePresentation`
  (the markdown projection). It excludes `start`, production's own separate,
  telemetry-entangled promotion effect. `ros-fs work capture` (Phase A's
  second increment) is the same kind of real effect for production
  `add`/`captureWork`: title/priority validation, explicit-id validation
  against both the queue and the live context, and collision-avoiding
  sequential ID generation over `nextSeq`, via
  `Ros.Domain.Work.WorkCapture` and
  `FileBacklogQueueRepository.captureItem` (which also synthesizes
  production's own default document when `queue.json` does not exist yet).
  It excludes `--file` attachment, a separate, larger effect. `ros-fs work
  update` (Phase A's third increment) is the same kind of real effect for
  production `update`/`findOrCreateQueueEntry`: per-field `Keep`/`Set`
  changes for only the fields explicitly provided, and the same
  context-only-id upsert (with production's exact minimal-record defaults)
  via `Ros.Domain.Work.WorkUpdate` and
  `FileBacklogQueueRepository.applyUpdate`, which shares its
  parse-or-synthesize/commit machinery with `captureItem`. It also
  excludes `--file` attachment. `ros-fs work attach` (Phase A's fourth and
  final backlog-only increment) is the real effect for production
  `attachFileUnlocked`: it is the first real effect to touch file bytes, not
  just JSON/text, writing each attachment as a plain, non-transactional file
  before the queue commit (reproducing production's own non-atomicity between
  the two writes rather than improving on it), computing the next attachment
  sequence as one past the highest existing sequence (missing/malformed `seq`
  counts as zero, not the count), and sanitizing the stored filename with a
  ported `sanitizeFileComponent` (basename, trim, collapse disallowed
  characters to `_`, `file` fallback only when that leaves nothing). Multiple
  `--file` flags commit as that many separate `work-protocol` lock cycles, one
  per file, matching production's own per-file loop, via
  `Ros.Domain.Work.WorkAttachment` and
  `FileBacklogQueueRepository.applyAttachment`/`readAttachmentSequences`,
  which share `findOrAppendItem` (extracted from `applyUpdate`, behavior
  unchanged) with the other two item-touching effects. With this increment,
  every backlog-only Node command (`add`, `update`, `attach`,
  `ready`/`block`/`abandon`) has real F# effect parity. `ros-fs work start`
  (Phase A's fifth increment) is the first real effect outside the backlog
  layer: production `startWork`/`transitionUnlocked`'s `begin` action,
  including a real telemetry execution creation when no recoverable
  candidate exists (production `startExecution`). It writes
  `.ros/context/current.json` and appends `.ros/events/events.jsonl`
  through `Ros.Infrastructure.Work.FileWorkContextRepository.applyContextPlan`
  (JSON-node surgery, preserving fields the typed `LiveWorkItem` does not
  model, such as a research item's `conclusion`) under the shared
  `work-state` recovery journal (MIG-05), composing the pure decision layer
  that already existed (`WorkContextPlanning.plan` and
  `TelemetryPlanResolution.resolveContext`) with two new real ports:
  `Ros.Infrastructure.Work.FileTelemetryExecutionRepository.createExecution`
  (identity discovery purely from whitelisted environment variables via
  `Ros.Domain.Telemetry.Identity`, a real Git baseline snapshot, the full
  metric registry, and capability seeding via `Ros.Domain.Telemetry.Capability`)
  and `Ros.Infrastructure.Json.CanonicalJson` (the shared SHA-256 hashing
  primitive behind both the event log's `eventId` and the execution
  record's `measurementId`). It excludes `resume`/`block`/`complete` (which
  additionally need `finalizeWorkExecutions`/`recordTelemetryLifecycle`),
  explicit identity/execution-id CLI overrides, and every telemetry-producer
  command. `ros-fs work resume` (Phase A's sixth increment) is production
  `transitionUnlocked`'s `resume` action, reusing increment 5's effect
  infrastructure verbatim (a `resolveContextTelemetryWithCreation` helper
  factored out of `runWorkStart`, no new Domain/Infrastructure code): it
  never creates a new context item, never observes Git, and links every
  currently-active candidate execution or creates one when none is active.
  It excludes `recordTelemetryLifecycle`'s "resumed" bookkeeping (closed by
  a later MIG-08 increment, in the execution-telemetry manifest -- see its
  own "Known gaps").
  `resume`'s `parentExecutionId` linkage on the new-execution path, once
  scoped as a further candidate slice, turned out on manual smoke-testing
  against real Node to be a confirmed production defect rather than a
  missing feature: production computes `parentExecutionId: prior?.
  executionId ?? null` but `startExecution`'s `discoverIdentity(options.
  identity ?? options)` always picks the truthy `options.identity` sibling
  over the `options` object that value was set on, silently discarding it
  (real, unpatched Node always writes `null`). Every other real effect in
  this manifest reproduces a confirmed production quirk once found; this
  one is corrected instead, since Node is being deprecated rather than
  patched: `runWorkResume` now supplies the work item's most recently
  created execution (`Ros.Infrastructure.Work.FileTelemetryQueryRepository.
  readLatestExecutionId`, new) as `CreateExecutionRequest.
  ParentExecutionId` (new field, threaded into the pre-existing `Identity.
  discover`/`IdentityInputs.ParentExecutionId`, which already worked
  correctly -- the defect is purely in how production's own JavaScript
  assembles the options object) whenever its rare no-active-candidate path
  must create one; every other action still supplies `None`, matching
  production's own CLI. `tests/work-resume-parent-execution-fsharp-
  differential.test.mjs` documents this as a deliberate, known divergence:
  it runs both real Node (confirming the defect persists) and the F# CLI
  (confirming the corrected linkage) side by side, rather than asserting
  byte-for-byte parity.
  `ros-fs work block` (Phase A's seventh increment) mirrors production
  `blockWork`: the one command that splits requested ids between a
  not-yet-started backlog item and an already-live context item, applying
  each real effect (the same backlog-transition effect and the same
  live-work effect as the other increments) under one held `work-protocol`
  lock, matching production's own combined command exactly. `--reason` is
  checked by the existing decision layer rather than eagerly at the CLI, so
  an item in the wrong state gets the illegal-transition rejection first,
  matching production's own check order. This slice's own differential
  caught a real bug in `applyItem` (never wrote `blockReason` onto a
  live-context item at all, from increment 5 onward) and fixed it: the
  field is now written whenever set, for every action, matching production's
  own never-cleared persistence. `ros-fs work complete` (Phase A's eighth
  increment) closes the live-work family: production
  `transitionUnlocked`'s `complete` action, including real evidence-path
  verification against the filesystem (`WorkOperations.planVerifiedContext`/
  `Ros.Infrastructure.Work.FileEvidenceRepository`, checked only after the
  frozen decision layer accepts the required evidence types), per-work-type
  required evidence read from `ros.json`'s `workProtocol.completionEvidence`
  (`FileWorkConfigRepository.readCompletionEvidence`), and unconditional
  telemetry-execution finalization via a new
  `Ros.Infrastructure.Work.FileTelemetryFinalizationRepository.finalizeWorkExecutions`
  (called directly per completing id, since the shared telemetry-resolution
  pipeline discards the `FinalizeExecutions` intent signal it computes).
  Finalization computes a real `git diff`-derived clean-baseline change
  summary (`Ros.Domain.Telemetry.ChangeSummary`'s `ChangeSummaryParser`/
  `ChangeClassification`/`BlockedDuration`, reduced to the fields
  production's own `finalizeExecution` reads) or one of production's exact
  three unavailable reasons when the stored starting snapshot was not clean.
  A research item's `--conclusion` (defaulting to `"inconclusive"`) is
  written through a new `applyContextPlanWithConclusions`. It excludes
  `options.input`/adapter-ingestion (unreachable from any current CLI path)
  and explicit `--identity-*`/`--execution-id` overrides. With this
  increment, every live-work transition (`begin`/`resume`/`block`/`complete`)
  has real F# effect parity.
  `ros-fs adapter call --store FILE --request FILE` (tracked as part of
  `DF-ROS-2026-A028` Phase A/MIG-08's remaining-scope inventory, though it
  belongs to this manifest's own `DF-ROS-2026-A007`, not telemetry) is
  production's dependency-free file-based conformance adapter for the
  external work-system contract (`docs/work-adapter-contract.md`): unlike
  every other real effect in this manifest, it touches no telemetry
  execution, work context, or lock at all, operating entirely on a
  caller-named standalone JSON store. Work items and events inside that
  store are caller-defined, open-ended JSON, so a new pure
  `Ros.Domain.Work.AdapterContract.decide` inspects only the handful of
  fields production itself reads (a work item's `state`, an event's
  `eventId`), mirroring `callFileAdapter`/`validateAdapterRequest`'s exact
  branch order: protocol-version and operation-support checks before the
  store is ever loaded (so a malformed request never creates or touches
  it), then a cached `requestId` replay, then repository authorization,
  then `simulateOutcome: "unknown"` fault injection regardless of
  operation or scope, then each operation's own forbidden/not-found/
  conflict checks. `Ros.Infrastructure.Work.FileAdapterRepository.call`
  performs the actual read/mutate/write: a `transitionWorkItem` success
  mutates only `state`/`updatedBy` on the existing work-item node in
  place, preserving every other caller-defined field verbatim; a
  `publishRepositoryEvent` success appends its event only when the
  `eventId` is not already present; every result, success or not, is
  cached under its `requestId` and the whole store rewritten exactly once
  per non-replayed call.
  `ros-fs adapter publish --target FILE` is the paired event-republish
  command: it reads every event from `.ros/events/events.jsonl`, appends
  whichever are not already present (by `eventId`) in a caller-named
  `--target` destination JSONL file (unrelated to `adapter call`'s own
  store), then refreshes `.ros/publications.json`'s receipt for every
  source event -- even an already-published one -- with a fresh
  `publishedAt` on every call, matching production's own unconditional
  per-call refresh (a real, non-obvious quirk verified against real Node
  before writing any test: a duplicate-only republish still rewrites
  every receipt). `Ros.Infrastructure.Work.FileAdapterRepository.publish`
  (new, alongside `call` in the same module) creates the target's parent
  directory unconditionally before appending -- even with zero events,
  which creates the directory but no destination file -- and a
  directory-creation or append failure propagates before either the
  destination or the receipts file is touched, matching production's own
  thrown, uncaught error.

  `ros-fs work context [ID]` (Phase A's ninth increment, prompted by
  `EV-ROS-2026-A046`'s finding that this row -- along with `work`/`work
  list`, `work show`, and `status` -- was never assigned to MIG-07 or
  MIG-08's scope at all) is the first pure read-only command surface view:
  production's own `contextView` reads `.ros/context/current.json`,
  optionally filtered to one work item, computing two fields fresh on
  every call rather than storing them -- `allowedActions` (reusing the
  existing `Ros.Domain.Work.WorkTransition.allowedActions`, matching
  production's own `TRANSITIONS[item.semanticState]` table exactly,
  including its empty-array fallback for a semantic state the table does
  not recognize) and `requiredEvidenceForCompletion` (reusing the
  existing `Ros.Infrastructure.Work.FileWorkConfigRepository.
  readCompletionEvidence`, the same per-type/default lookup `work
  complete`'s evidence check already used). No new decision logic was
  needed at all -- both pieces of domain logic already existed from
  earlier increments; this was purely new read-path wiring. A requested
  ID with no matching item rejects with production's exact message rather
  than an empty view. Requires no lock at all, unlike every mutation
  command above.

  `ros-fs work`/`ros-fs work list`/`ros-fs work show [ID]` (Phase A's tenth
  increment, the second of `EV-ROS-2026-A046`'s four unassigned rows) are
  production's `mergedWorkView`/`showWork`: the merged backlog-queue/
  live-context projection at its full fidelity, wider than
  `Ros.Domain.Work.QueuePresentation.mergedRows`'s five-field `queue.md`-only
  projection (which this increment leaves untouched). A new
  `Ros.Domain.Work.WorkListView.mergedRows` reuses
  `QueuePresentation.effectiveStatus` for the shared status-precedence rule
  and adds a new `BacklogTransition.allowedActions` lookup table (mirroring
  production's own `BACKLOG_TRANSITIONS`, independent of `BacklogTransition.
  decide`'s legality checks) for the `backlogActions` field a backlog-only
  id exposes. It reproduces one subtle production distinction exactly:
  `description`/`priority`/attachment `contentType` are always present
  (`null` when absent) while `blockedReason` is genuinely omitted from the
  JSON when neither the live item nor the backlog record names one --
  production's own object literal leaves it `undefined`, which
  `JSON.stringify` drops, never coercing it to `null`.
  `Ros.Infrastructure.Work.FileWorkListRepository` reads the live context
  through the same typed `Ros.Contracts.Work.WorkContextPlanContract.
  parseJson` every write-side effect already uses, and reads `queue.json`
  wide enough for the new fields via a new `QueueItemDetail` parse
  (distinct from `FileBacklogQueueRepository.readItems`'s narrower
  `BacklogQueueItemRecord`, which `QueueValidation` still needs unchanged).
  `work show` additionally reads the detail markdown file
  (`.ros/work/items/{id}.md`, `null` when absent) and rejects an unknown id
  with production's exact message. `--tag`/`--status` filtering is not yet
  ported (a known gap, matching this slice's read-only scope). `status`
  remains its own future increment, and depends on the `validate` command
  unification this manifest has not yet scoped.

## Interfaces

- Inbound: `./ros add`, `./ros work ...`, `./ros adapter ...`, and HTTP routes
  in `tools/ros_server.mjs`.
- Outbound: versioned work/context/event/adapter JSON, queue Markdown,
  attachments, CLI JSON/text, and HTTP JSON/bytes.

## Tests and verification

- Local behavior tests: `tests/work-protocol.test.mjs` and
  `tests/ros-server.test.mjs`; the typed Git seam is exercised by
  `tests/Ros.Tests/GitTests.fs` and `tests/git-fsharp-differential.test.mjs`.
- Shadow orchestration tests: `tests/Ros.Tests/WorkPlanTests.fs` and the legal
  item/event projection differential in `tests/work-fsharp-differential.test.mjs`.
- Telemetry execution-ID resolution tests: `tests/Ros.Tests/TelemetryResolutionTests.fs`
  and `tests/work-telemetry-fsharp-differential.test.mjs`.
- Git observed/meaningful-path composition tests: `tests/Ros.Tests/PathFilterTests.fs`,
  the base-comparison cases in `tests/Ros.Tests/GitTests.fs`, and
  `tests/work-git-paths-fsharp-differential.test.mjs`.
- Work-attribution validation tests: `tests/Ros.Tests/WorkAttributionTests.fs`
  and `tests/work-attribution-fsharp-differential.test.mjs`.
- Backlog-queue validation tests: `tests/Ros.Tests/QueueValidationTests.fs`
  and `tests/work-backlog-validate-fsharp-differential.test.mjs`.
- Backlog-transition real-effect tests: `tests/Ros.Tests/QueuePresentationTests.fs`,
  `tests/Ros.Tests/BacklogTransitionEffectTests.fs`, and
  `tests/work-backlog-transition-fsharp-differential.test.mjs`.
- Work-capture real-effect tests: `tests/Ros.Tests/WorkCaptureTests.fs`,
  `tests/Ros.Tests/WorkCaptureEffectTests.fs`, and
  `tests/work-capture-fsharp-differential.test.mjs`.
- Work-update real-effect tests: `tests/Ros.Tests/WorkUpdateTests.fs`,
  `tests/Ros.Tests/WorkUpdateEffectTests.fs`, and
  `tests/work-update-fsharp-differential.test.mjs`.
- Work-attach real-effect tests: `tests/Ros.Tests/WorkAttachmentTests.fs`,
  `tests/Ros.Tests/WorkAttachmentEffectTests.fs`, and
  `tests/work-attach-fsharp-differential.test.mjs`.
- Work-start real-effect tests: `tests/Ros.Tests/TelemetryIdentityTests.fs`,
  `tests/Ros.Tests/WorkContextEffectTests.fs`, and
  `tests/work-start-fsharp-differential.test.mjs`.
- Work-resume real-effect tests: `tests/work-resume-fsharp-differential.test.mjs`
  (reuses `work start`'s typed/infrastructure tests, since no new
  Domain/Infrastructure code was added).
- Work-block real-effect tests: `tests/work-block-fsharp-differential.test.mjs`,
  plus the `blockReason`-persistence regression test in
  `tests/Ros.Tests/WorkContextEffectTests.fs`.
- Work-complete real-effect tests: `tests/Ros.Tests/ChangeSummaryTests.fs`
  (change-summary parsing/aggregation and blocked-duration computation) and
  `tests/work-complete-fsharp-differential.test.mjs`.
- Adapter-call real-effect tests: `tests/Ros.Tests/AdapterContractTests.fs`
  (every pure `decide` branch: protocol mismatch, unsupported operation,
  cached replay, repository unauthorized, fault injection, and each
  operation's forbidden/not-found/conflict/success case),
  `tests/Ros.Tests/AdapterCallEffectTests.fs` (the JSON store
  read/mutate/write around those decisions, including the
  missing-field/protocol-mismatch untouched-store paths and the
  transition/publish mutation-persistence paths), and
  `tests/adapter-call-fsharp-differential.test.mjs` (the same
  read/transition/idempotent-retry/conflict/enforcement/publish-dedup/
  missing-field/missing-file scenarios byte-for-byte against production's
  own CLI).
- Adapter-publish real-effect tests:
  `tests/Ros.Tests/AdapterPublishEffectTests.fs` (fresh-destination
  append, eventId dedup across repeated calls, the unconditional per-call
  receipt refresh, the zero-event directory-creation-without-a-file
  quirk, and a directory-creation failure leaving neither file touched)
  and `tests/adapter-publish-fsharp-differential.test.mjs` (a real
  `work begin` + `adapter publish` cycle covering append/dedup/
  receipt-count byte-for-byte against production, the write-failure
  rejection, the missing-`--target` message, and the zero-event case).
- `work resume` `parentExecutionId` correction tests:
  `tests/Ros.Tests/TelemetryQueryTests.fs` (`readLatestExecutionId`'s
  chronological-latest resolution and its none-for-unknown-item case) and
  `tests/Ros.Tests/WorkContextEffectTests.fs` (`createExecution` threading
  a supplied `ParentExecutionId` into the created record's identity, and
  leaving it absent by default); `tests/work-resume-parent-execution-
  fsharp-differential.test.mjs` documents the divergence explicitly,
  running both real Node (confirming the defect persists: `null`) and the
  F# CLI (confirming the corrected linkage) side by side, rather than
  asserting byte-for-byte parity.
- Work-context real-effect tests: `tests/Ros.Tests/WorkContextViewTests.fs`
  (every item's `allowedActions`/`requiredEvidenceForCompletion` computed
  fresh, ID filtering preserving unmodeled fields such as a research
  item's `conclusion`, the not-in-context rejection, and the
  synthesized-empty-view case when no context file exists yet) and
  `tests/work-context-fsharp-differential.test.mjs` (a real
  `add`/`work ready`/`work start`/`work block` cycle driving the actual
  `ros` CLI wrapper directly, covering the no-ID view, ID filtering, and
  the unknown-ID rejection byte-for-byte against production).
- Work-list/work-show real-effect tests: `tests/Ros.Tests/WorkListViewTests.fs`
  (backlog-only `backlogActions`, live-only `liveWorkItem` with a
  defaulted title, a blocked live item overriding both `status` and
  `blockedReason` over a stale backlog record, the genuine
  `blockedReason` omission case, attachment pass-through including a null
  content type, and ordinal id ordering) and
  `tests/work-list-fsharp-differential.test.mjs` (a real `add`/`work
  ready`/`work start`/`work block`/attachment cycle driving the actual
  `ros` CLI wrapper directly, covering `work list`, bare `work`, `work
  show` across backlog-only/live/blocked/attachment items, and the
  unknown-ID rejection byte-for-byte against production).
- Unified-validate real-effect tests: `tests/Ros.Tests/FindingContractTests.fs`
  (the three repair-message branches, including the `work_items` one added
  for this increment) and `tests/validate-unified-fsharp-differential.test.mjs`
  (a clean bootstrap in both `--json` and text form; a combined
  backlog-status/disabled-telemetry scenario; a stale registry sorted
  together with other findings; and a malformed artifact plus an
  unattributed Git change rendered in both output forms — all
  byte-for-byte identical to production's own `validate`).
- Boundary/contract tests: `schemas/work-protocol.schema.json`,
  `schemas/work-adapter-*.schema.json`, and JSON CLI assertions in tests.
- Integration/live verification: `./ros status`, `./ros work context ID`,
  `./ros work list`, `./ros work show ID`, and `./ros validate`.

## Dependencies

- Allowed direct dependencies: execution telemetry lifecycle, filesystem
  persistence, Git evidence, clock/ID generation, and configured file adapters.
- Required composition context: `ros.json`, repository root, and
  `telemetry/metrics.json` when telemetry is enabled.

## Modification boundaries

- Normal: `tools/ros_cli.mjs`, `tools/ros_server.mjs`, `web/`, work schemas,
  work docs, and their tests.
- Escalation required: state mappings, legal transitions, evidence obligations,
  or authoritative-store changes because installed repositories depend on them.

## Local agent instructions

- `AGENTS.md`, `docs/work-protocol.md`, and `docs/work-backlog-guide.md`.

## Maintenance

- Owner: repository-governance
- Last checked against implementation: 2026-09-10 (unified `validate` increment)
- Known gaps: backlog and live work intentionally remain separate recovery
  units; the F# planner now owns pure whole-context/multi-item and
  backlog-promotion plans, post-plan evidence observation, telemetry
  execution-ID result-feedback (recover/reject/bulk-link/finalize), all four
  real backlog-only effects (`ready`/`block`/`abandon` transitions,
  `add`/`captureWork`, `update`/`findOrCreateQueueEntry`, and
  `attachFileUnlocked`, including real binary file writes and filename
  sanitization/sequencing — every backlog-only Node command now has real F#
  parity), and all four real live-work effects (`begin`/`work start`,
  `resume`/`work resume`, `block`/`work block`, and `complete`/`work
  complete`, including real telemetry execution creation and finalization —
  every live-work transition now has real F# parity too). `resume`/`block`
  now also record `recordTelemetryLifecycle`'s within-execution
  "resumed"/"blocked" bookkeeping (`FileTelemetryFinalizationRepository.
  recordLifecycle`, in the execution-telemetry manifest), so
  `time.blocked_ms` computes a real nonzero value on finalization instead of
  always zero. `adapter call` (`AdapterContract.decide`/
  `FileAdapterRepository.call`) and `adapter publish`
  (`FileAdapterRepository.publish`) are now real effects too — the
  external work-system adapter conformance test and its paired
  event-republish command, independent of every telemetry
  execution/context/lock this manifest otherwise owns. `resume`'s
  `parentExecutionId` linkage is also now correct — production's own
  computed value turned out to be silently discarded by a confirmed
  `discoverIdentity` object-merge defect (real Node always writes
  `null`), so this port implements the evidently-intended behavior
  instead, since Node is being deprecated rather than patched; a
  dedicated differential test documents the divergence explicitly.
  `work context [ID]` is now a real read-only effect too — reusing
  already-existing `WorkTransition.allowedActions`/`readCompletionEvidence`
  domain logic, requiring no lock. `work`/`work list`/`work show [ID]` are
  now real read-only effects as well, via a new
  `Ros.Domain.Work.WorkListView.mergedRows` (the full-fidelity merge
  production's own `mergedRows` computes, wider than
  `QueuePresentation`'s five-field markdown-only projection) and a new
  `Ros.Infrastructure.Work.FileWorkListRepository`; `--tag`/`--status`
  filtering is not yet ported. `ros-fs validate [--json]` is now a real,
  unified effect too: production's own top-level `validate(root)`
  combines artifact findings, registry staleness, `workFindings`,
  `queueFindings`, and `telemetryFindings` (execution-telemetry
  manifest) into one sorted array, and every one of those five
  contributors already had a real F# equivalent from earlier increments
  — this closes purely CLI-layer composition (`Ros.Cli.Program.
  runValidateUnified`), reusing each contributor's existing decision
  logic unchanged and adding the one missing repair-message branch
  (`field = "work_items"`) to the shared `Ros.Contracts.Cli.
  FindingContract`. Confirmed against real Node across a combined
  scenario (a backlog-status violation, a disabled-telemetry violation,
  a stale registry, a malformed artifact, and an unattributed Git
  change, all present at once) producing a byte-identical sorted finding
  list in both `--json` and text form. Still remaining: every
  telemetry-producer command (in the execution-telemetry manifest — all
  now real, per MIG-08's own closure) has no bearing here; what's left
  in this manifest's own scope is `status`, the one remaining pure
  read-only view `EV-ROS-2026-A046` found never assigned to
  MIG-07/MIG-08 — its own dependency on the `validate` unification is
  now resolved. Evidence containment has no current authority; production
  behavior accepts absolute existing paths. Production remains Node-owned
  pending those slices and the distribution decision.
