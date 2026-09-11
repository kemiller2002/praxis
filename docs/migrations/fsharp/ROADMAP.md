# ROS F# migration roadmap

Status values are `complete`, `in progress`, `ready`, `deferred`, and
`blocked`. Completion is evidence-based; adding a project or compiling does not
complete a capability.

## Prioritization rubric

Candidates are scored 1–5 for semantic importance (S), duplication (D), defect
or risk pressure (R), frequency/fan-out (F), testability (T), architectural
leverage for later slices (L), and uncertainty penalty (U). The aid is
`S + D + R + F + T + L - U`; it is ordering evidence, not scientific precision.

| Candidate | S | D | R | F | T | L | U | Aid | Interpretation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| Artifact validation/registries | 4 | 5 | 3 | 4 | 5 | 5 | 1 | 25 | safest end-to-end architecture proof |
| Shared persistence/recovery | 5 | 4 | 5 | 5 | 3 | 5 | 3 | highest systemic safety leverage after proof |
| Git provenance | 4 | 4 | 4 | 5 | 4 | 4 | 2 | removes duplicated fail-open boundary |
| Work lifecycle/evidence | 5 | 4 | 5 | 5 | 4 | 5 | 3 | central state; requires persistence/Git seams |
| Execution/telemetry core | 5 | 4 | 4 | 5 | 4 | 5 | 4 | high value, newer and extension-heavy |
| Bootstrap/upgrade | 4 | 3 | 4 | 3 | 4 | 4 | 3 | controls installed state and distribution |
| Workflow/release policy | 4 | 3 | 4 | 3 | 3 | 3 | 4 | hosted external outcomes limit local evidence |
| HTTP/UI contracts | 2 | 2 | 4 | 3 | 3 | 2 | 3 | secure/contract, not wholesale rewrite |
| Project administration | 3 | 2 | 4 | 2 | 3 | 2 | 5 | separate authority requirements unresolved |
| Time-entry projection | 2 | 1 | 2 | 1 | 1 | 1 | 5 | no current semantic contract; research only |

Dependency ordering overrides a raw score where state safety requires it.

## Vertical slices

| Slice | Status | Capability and acceptance | Compatibility/removal plan |
|---|---|---|---|
| MIG-00 baseline, inventory, preregistration, navigation | complete | immutable tag; T0–T2; complete inventory; semantic map/manifests; frozen hypotheses/fixtures | additive only |
| MIG-01 bounded distribution decision | complete | repository-local framework-dependent .NET 10 shadow; production consumer choice explicitly deferred at the time. `DF-ROS-2026-A029` has since accepted a consumer distribution shape (self-contained single-file binaries via GitHub Releases). `DF-ROS-2026-A030` closed Phase C for this repository (`./ros` execs the F# CLI directly). `DF-ROS-2026-A032` (superseding `DF-ROS-2026-A031`) closes it for `ros-bootstrap init`'s scaffolded `./ros` too, by default, using the self-contained-binary mechanism (a bootstrapped repo has no F# source to build from) | Node's own source remains present, untouched, in this repository and the starter template, as the accepted end-state's Phase 2 (actual deletion, named but not attempted) and the interim rollback path |
| MIG-02 architecture-enforced F# skeleton | complete | five production projects; version/help; dependency rules with positive and rejection proof | remove shadow projects to roll back |
| MIG-03 artifact boundary/fixtures | complete | explicit front matter, IDs, kind/status/confidence/reference shapes; path-specific findings; frozen valid/invalid fixtures | Node/Python remain oracles |
| MIG-04 artifact validation and registry projection | complete | `ros-fs artifacts validate`, `registry build/check`; Node/F# byte parity; no canonical mutation; repeated build and partial-write outcome proof | no launcher switch; Python deprecation only after further evidence |
| MIG-05 transactional file persistence/recovery | complete | separate bounded recovery contracts cover generated registries, live event/context, and backlog queue/projection; telemetry execution records remain atomic/per-execution locked while creation/backlink retry adopts detached evidence or rejects ambiguity; cross-runtime, divergence, concurrency, and rejection proofs pass | retain stateful Node writers; each semantic store keeps a bounded contract; no global transaction framework |
| MIG-06 unified Git provenance | complete | F# owns the typed clean/changed/unavailable contract; one compatible installed Node process adapter serves work and telemetry; rename attribution, malformed/missing/non-repository outcomes, pre-effect work rejection, and unavailable-not-zero telemetry pass | retain compatible Node adapter until the MIG-01 distribution decision authorizes an F# runtime switch |
| MIG-07 work lifecycle/evidence | complete | typed live/backlog decisions, pure item/context/promotion plans, batch evidence composition that runs only after complete semantic acceptance, telemetry execution-ID result-feedback that freezes the item/event projection whenever no new execution record is required, real Git observed/meaningful-path composition for `work context-plan`, read-only `work validate`/`work backlog-validate` diagnostics mirroring production `workFindings`/`queueFindings`, and — Phase A (`DF-ROS-2026-A028`) — eight real state-changing effects: `work backlog-transition` (`ready`/`block`/`abandon`), `work capture` (`add`/`captureWork`), `work update` (`update`/`findOrCreateQueueEntry`, including its context-only upsert), `work attach` (`attachFileUnlocked`, including real binary file writes and production's exact filename sanitization/sequencing) — giving every backlog-only Node command genuine F# parity — `work start` (`startWork`/`transitionUnlocked`'s `begin` action, including real telemetry execution creation via `startExecution`), the first live-work and telemetry-creating effect, `work resume` (`transitionUnlocked`'s `resume` action, reusing `work start`'s effect infrastructure verbatim), `work block` (`blockWork`, splitting requested ids between a backlog and a live-context branch under one held lock, matching production's own combined command exactly), and `work complete` (`transitionUnlocked`'s `complete` action, including real evidence-path verification via `WorkOperations.planVerifiedContext`, a real `git diff`-derived clean-baseline change summary, and unconditional telemetry-execution finalization) — giving every live-work transition genuine F# parity. The four backlog effects commit through the shared `work-protocol` lock and `backlog-state` recovery journal (MIG-05) via `JsonNode` surgery (preserving every unmodeled field, matching production's non-HTML-safe JSON escaping) rather than a full typed queue-item model; `work start`/`work resume`/`work block`/`work complete` commit through `work-state` (events + context) via the same JSON-node-surgery discipline, create telemetry executions through a new content-addressed identity/capability/measurement model, and (for `complete`) finalize them through a new `FileTelemetryFinalizationRepository`; see `ARCHITECTURE.md` for the per-slice detail. Exhaustive guards, legal-edge projections, ordered multi-item behavior, all 16 backlog state/action differentials, begin/resume/finalize telemetry differentials, Git differentials, work-attribution/backlog-queue-validation differentials, and effect differentials for all eight real effects pass; persistence ports exist. A ninth increment, prompted by `EV-ROS-2026-A046`'s finding that four read-only rows (`work`/`work list`, `work show`, `work context`, `status`) were never assigned to MIG-07 or MIG-08's scope at all, closes the smallest: `work context [ID]` reuses pre-existing `WorkTransition.allowedActions`/`FileWorkConfigRepository.readCompletionEvidence` domain logic with no new decision code, requiring no lock. A tenth increment closes the second: `work`/`work list`/`work show [ID]` port production's `mergedWorkView`/`showWork` at full fidelity via a new `Ros.Domain.Work.WorkListView.mergedRows` (reusing `QueuePresentation.effectiveStatus`) and a new `BacklogTransition.allowedActions` lookup table, deliberately kept separate from `QueuePresentation`'s narrower `queue.md`-only projection and `FileBacklogQueueRepository.readItems`'s narrower validation-only read; `--tag`/`--status` filtering not yet ported. An eleventh increment unifies `validate`: all five of production's own contributors (artifact findings, registry staleness, `workFindings`, `queueFindings`, `telemetryFindings`) already had a real F# equivalent, so this is pure CLI-layer orchestration (`runValidateUnified`), matching production's exact sorted, combined output in both `--json` and text form. A twelfth increment ports `status` itself: production's own `statusView` is pure composition over `contextView`/the unified `validate` findings/`showTelemetry`'s execution read, all already real, so this adds zero new Domain/Infrastructure code, closing `EV-ROS-2026-A046`'s full four-row inventory (`work`/`work list`, `work show`, `work context`, `status`). `EV-ROS-2026-A047` then re-ran the full 26-row inventory a third time and found every row reading "Full parity," closing `DF-ROS-2026-A028` Phase A entirely | evidence-containment policy and the production/distribution switch remain, each its own future scoping choice; Phase A's closure authorizes neither -- `AGENTS.md` was updated alongside `EV-ROS-2026-A047` to keep agents on `./ros` (Node) for every actual operation |
| MIG-08 execution/telemetry core | complete | lifecycle, identity, provenance, metric/capability semantics, aggregation, unknown/raw preservation; `EV-ROS-2026-A044` inventories the full scope and corrects an earlier estimate that treated new-execution creation as separable from it. Increment 1 (Phase A/MIG-08's first slice, chosen per `EV-ROS-2026-A044`'s own "almost certainly its own smaller first vertical slice" guidance rather than any recommendation it declined to make) ships the two commands with zero new write-path/lock/identity/adapter complexity: `telemetry adapters` (a hardcoded catalog dump, `Ros.Domain.Telemetry.TelemetryAdapters`) and `telemetry show [TARGET]` (a real, lock-free read of `.ros/telemetry/executions/*.json` through a new `Ros.Infrastructure.Work.FileTelemetryQueryRepository`, with `TARGET` positional rather than a `--` flag, matching production's own `telemetryTarget(args)` exactly); both proven by a real Node differential against production's own exported `showTelemetry`/`TELEMETRY_ADAPTERS`. Increment 2 closes a real gap left open since increments 6-8: `recordTelemetryLifecycle` (`Ros.Infrastructure.Work.FileTelemetryFinalizationRepository.recordLifecycle`) now appends the real `work.blocked`/`work.resumed` events plus their `agent.interruptions`/`agent.resumes` metrics to every currently-active execution, called from `work resume`/`work block` before telemetry resolution runs (matching production's own ordering), so `time.blocked_ms` computes a real nonzero value at finalization instead of always zero. Increment 3 ports `telemetry summary`'s real aggregation (`summarizeTelemetry`/`timingSummary`): four aggregation strategies (`sum`/`maximum`/`latest-per-session`/`none`, falling back to a plain "latest wins" default) plus an interval-sweep timing summary, via a new pure `Ros.Domain.Telemetry.Summary` module. Increment 4 ships the first write-path telemetry-producer command, `telemetry finalize [TARGET]`: target resolution ports production's `resolveExecution` exactly (including its real quirk that a non-`EXE-`-prefixed target matches by workItemId regardless of status), then reuses the already-tested `FileTelemetryFinalizationRepository.finalizeOne` mutation unchanged, reproducing production's real pre-lock "already finalized" fast path rather than "fixing" it; `--input` (real adapter ingestion) is deliberately not ported and rejected outright rather than silently ignored. Increment 5 ships the second write-path telemetry-producer command, `telemetry record [TARGET] --metric ID --value VALUE`: target resolution generalizes increment 4's resolver with production's own `activeOnly` option (a target whose only match is not currently active is rejected, including a race re-checked immediately after the lock), then the metric id/value are validated inside the lock at the same point production's `normalizeMetric` does, and a content-addressed `measurementId` (mirroring `derivedMetricNode`) deduplicates an identical repeated call while the capability upsert still happens unconditionally, matching production's own `addMetric` exactly. Increment 6 ships the third write-path telemetry-producer command, `telemetry ingest [TARGET] --input FILE`, restricted to the `generic` adapter (every other real adapter name rejected outright; provider-specific field mapping remains its own future slice): ports `ingestAdapted` field-for-field -- snapshot dedup, the declared-unavailable guard, identity/classification/scope/links/quality-signal merging, general metric normalization, `(metricId, providerField)`-keyed capability upsert (the `Capability` domain type gained an optional `ProviderField`), raw-payload redaction/truncation plus the four-branch byte-budget retention policy, and provenance-source dedup. Increment 7 ships `telemetry classify`, a thin wrapper over increment 6's generic-ingest path: builds a synthetic ingest whose only content is a constructed `classification` object and an explicit `raw: {}`, using a real-clock `` `classification-${Date.now()}` `` snapshotId (not content-addressed, so never deduplicated across repeated calls). Increment 8 ships `telemetry start WORKITEMID`, the fourth write-path telemetry-producer command and the first with no state transition to plan: it recovers an unambiguous detached candidate, rejects on ambiguity with production's exact `; rerun with --execution-id one of: ...` message, or creates a new execution via `FileTelemetryExecutionRepository.createExecution` (which gained a real, previously-unported `ClassificationRationale` field), linking it into `telemetryExecutionIds` via a narrow direct `.ros/context/current.json` read-modify-write rather than `FileWorkContextRepository.applyContextPlan`; `--execution-id` and the eleven identity-override flags production's own CLI exposes are deliberately excluded and loudly rejected (exit 2), scoped as a separate future slice. Increment 9 ships `adapter call --store FILE --request FILE`, the external work-system adapter conformance test (`DF-ROS-2026-A007`) -- not telemetry at all, but tracked as part of this same remaining-scope inventory: a pure `Ros.Domain.Work.AdapterContract.decide` mirrors `callFileAdapter`'s exact branch order (protocol/operation checks before the store is ever touched, then a cached-replay short circuit, then repository authorization, then `simulateOutcome` fault injection, then each operation's forbidden/not-found/conflict checks), with `Ros.Infrastructure.Work.FileAdapterRepository.call` performing the actual JSON store mutation (a `transitionWorkItem` success touches only `state`/`updatedBy` on the existing work-item node, a `publishRepositoryEvent` success dedups by `eventId`). Increment 10 ships `adapter publish --target FILE`, the paired event-republish command: appends every `.ros/events/events.jsonl` event not already present (by `eventId`) in the caller-named `--target` destination, then refreshes `.ros/publications.json`'s receipt for every source event -- even an already-published one -- with a fresh `publishedAt` on every call, a real quirk verified against Node before any test existed; creates the target's parent directory unconditionally, even with zero events. `resume`'s `parentExecutionId` linkage, once scoped as a candidate next slice, turned out on manual smoke-testing to not be a missing feature at all: production's own computed value is silently discarded by a real `discoverIdentity(options.identity ?? options)` object-merge bug (confirmed against real Node), so this migration's F# port implements the evidently-intended behavior instead of replicating the bug -- the one deliberate divergence from production in this migration, justified since Node is being deprecated rather than patched. Increment 11 ships `telemetry ingest --adapter openai-codex`, the first real provider-specific field mapping beyond `generic`: this port confirmed the shared generic-ingest mutation pipeline (`ingestAdaptedGeneric`) is fully adapter-agnostic, so a new adapter needs only its own adaptation function (raw input to `AdaptedSnapshot`) plus one dispatch arm -- `adaptOpenAICodex` declares a fixed capability for all six usage fields per completed entry regardless of presence, a metric only for the ones present, resolves `identity.model` from the last record (searched in reverse) carrying one, and reproduces production's own quirk that `context.window_size` gets a metric but no adapter-level capability declaration (the shared metric pipeline upserts one regardless). Increment 12 ships the three hook adapters -- `anthropic-claude-hook`, `google-gemini-hook`, `github-copilot-hook` -- all resolving to one shared `adaptHook` function parameterized only by identity provider/runtime, matching production's own one-function/three-name shape; unlike `openai-codex`'s fixed declaration, `adaptHook`'s capabilities are derived strictly 1:1 from whichever of its five independent (non-exclusive), precompiled hook-name regex patterns actually match a given payload -- PostToolUse/AfterTool/ToolResult (contributing `tool.calls`, a `toolCategoryMetric`-bucketed metric, and a conditional `tool.failures`), SubagentStart, PostCompact, PermissionRequest, PermissionDenied -- with exactly one `runtime.{hookName}` event emitted per call regardless of how many patterns matched. Increment 13 ships `anthropic-claude-statusline` via `adaptClaudeStatusline`, the first adapter whose input is a single snapshot rather than an event stream: three top-level fields and four `current_usage` token fields each always get a capability declaration (present or not), matching `openai-codex`'s fixed-declaration style; `cost.session_cumulative` is the one field whose present-value capability status becomes `"estimated"` (a statusline snapshot can only estimate session cost, never observe it directly) and whose metric alone carries a `currency`/richer `quality`/`confidence` shape; this adapter emits no events of its own at all. Increment 14 ships the OTel adapter family -- `anthropic-claude-otel`, `google-gemini-otel`, `github-copilot-otel`, `otel-json` -- all resolving to one shared `adaptOtel`, the last real adapter mapping and the first with no fixed field schema at all: every field is looked up across a per-record set of nested candidates (the record itself, `attributes`, `resource.attributes`, `body`, `dataPoint.attributes`), and identity fields can be overwritten record-by-record across the whole stream (last one wins), unlike every other adapter's single-vantage-point resolution; capabilities are derived 1:1 from whichever metrics fired, matching `adaptHook`'s style. This closes MIG-08's telemetry-ingest adapter inventory -- every name in `Ros.Domain.Telemetry.TelemetryAdapters.all` now dispatches to a real F# effect, retiring `ingestTarget`'s now-unreachable "not yet supported" rejection branch. Increment 15 closes the last piece of MIG-08's own scope: `telemetry start`'s own `--execution-id` and its eleven identity-override flags, both rejected outright until now -- `--execution-id` needed no new decision logic (`ExecutionLinkRecovery.decide`'s already-shipped, already-tested `RequestedExecutionId` handling was simply threaded through from the CLI for the first time), and the eleven identity flags reuse `Identity.discover`'s already-complete override handling, with `CreateExecutionRequest` gaining `ExecutionId`/`IdentityOverrides` fields. Increment 24, outside MIG-08's own command-surface scope but a direct prerequisite for the not-yet-scoped `validate` command unification, ports `telemetryFindings` -- the telemetry contributor to production's combined `validate` findings array, and the largest single validator in this migration at ~80 independent checks. Unlike every other validator here, it crosses an unusually wide typed boundary rather than a small loosely-typed record: a new `Ros.Domain.Telemetry.Validation` type family (`FieldPresence`/`IdentityField`/`ParsedItem` closed unions, faithfully distinguishing JS's two different truthiness gates and the 11-field identity shape) plus `Ros.Domain.Telemetry.TelemetryValidation` (every legality decision, pure) and a new `Ros.Infrastructure.Work.FileTelemetryValidationRepository` (JSON-to-typed parsing only, per SDE-DOCTRINE-003's Tier 2/Tier 4 split) | MIG-08's own command-surface scope is now completely real -- every command and adapter name production's own CLI exposes for execution/telemetry has real F# effect parity, and `telemetryFindings` closes the last gap the `validate` command unification needs |
| MIG-09 bootstrap/upgrade | deferred | valid initial state, versioned upgrade/check/rollback; npm materializer thin | retain npm acquisition |
| MIG-10 validation/release workflow thinning | deferred | typed validation/release plans with explicit unknown external outcomes | YAML keeps GitHub/npm effects |
| MIG-11 HTTP/UI hardening and contracts | deferred | loopback/auth policy, sanitized uploads, idempotency, checked capability data | retain Node/TypeScript where useful |
| MIG-12 project administration research | deferred | identity/authority/reconciliation/portability contract | separate bounded context |
| MIG-13 Time Entry research | blocked | allocation/approval/correction/rounding/audit authority required | no implementation without requirements |
| MIG-14 legacy retirement | deferred | all nine removal conditions documented per path | deprecate before delete when external use unknown |

## Next ordered work after this mission

1. Migrate work lifecycle orchestration as a controlled shadow slice now that
   its persistence preconditions exist.
2. Migrate stable execution/telemetry semantics while preserving open provider
   extensions.
3. Run the full consumer distribution experiment before changing bootstrap.
4. Address hub security/locking as separate defects even if its F# migration is
   deferred.

## Authority-switch decision track

`DF-ROS-2026-A028` opens the decision this roadmap's "Production command
switch" row has always deferred, as three independently-gated phases:

1. **Full command-surface effect parity** — real, persisted F# handlers
   (not shadow diagnostics) for every Node command `EV-ROS-2026-A043`
   inventoried as having no F# equivalent, verified by differential proof of
   the real effect. This absorbs the remainder of MIG-07 and all of MIG-08.
2. **Consumer distribution evidence** — the macOS/Linux/Windows
   install/startup/size/update/offline/integrity/rollback evidence
   `DF-ROS-2026-A027` named as blocking, for a specific chosen distribution
   shape.
3. **The switch decision itself** — only once 1 and 2 are both accepted,
   carrying that evidence plus an explicit rollback plan.

Phase 1's backlog-layer scope is complete: `work backlog-transition`
(`ready`/`block`/`abandon`), `work capture` (`add`), `work update` (`update`,
including its context-only-id upsert), and `work attach`
(`attachFileUnlocked`, including real binary file writes and production's
exact filename sanitization/sequencing) are all real, differential-proven
effects — every backlog-only Node command now has genuine F# parity. Phase
1's live-work layer is now complete too: `work start`
(`startWork`/`transitionUnlocked`'s `begin` action, including real
telemetry execution creation via `startExecution`), `work resume`
(`transitionUnlocked`'s `resume` action, reusing `work start`'s effect
infrastructure verbatim), `work block` (`blockWork`, the one command
that splits requested ids between the backlog and live-context layers
under one held lock, matching production's own combined command exactly),
and `work complete` (`transitionUnlocked`'s `complete` action, including
real evidence-path verification against the filesystem, a real
`git diff`-derived clean-baseline change summary, and unconditional
telemetry-execution finalization via a new
`FileTelemetryFinalizationRepository` port of `finalizeWorkExecutions`) are
all real effects — every live-work transition (`begin`/`resume`/`block`/
`complete`) now has genuine F# parity, closing the entire work-lifecycle
command surface. Phase 1 has now opened its MIG-08 scope too:
`EV-ROS-2026-A044` (a full inventory of `tools/ros_telemetry.mjs`)
deliberately declined to recommend which slice to attempt first, only that
one was needed, so this round made that choice — `telemetry adapters` (a
hardcoded catalog dump) and `telemetry show` (a real, lock-free read of
`.ros/telemetry/executions/*.json`), the two commands with zero new
write-path, lock, identity, or adapter complexity. A follow-on round closed
a real gap that increments 6-8 had documented, honestly, as still open:
`recordTelemetryLifecycle`'s within-execution "blocked"/"resumed" event
bookkeeping, so `work resume`/`work block` now record it before telemetry
resolution runs (matching production's own ordering) and `time.blocked_ms`
computes a real nonzero value at finalization instead of always zero. A
third round ported `telemetry summary`'s real aggregation
(`summarizeTelemetry`/`timingSummary`) via a new pure `Ros.Domain.
Telemetry.Summary` module. A fourth round shipped the first write-path
telemetry-producer command, `telemetry finalize [TARGET]`: resolution
ports production's `resolveExecution` exactly, then reuses the
already-tested `FileTelemetryFinalizationRepository.finalizeOne` mutation
unchanged, reproducing production's real pre-lock "already finalized"
fast path rather than "fixing" it; `--input` (real adapter ingestion)
remains deliberately unported and is rejected outright. A fifth round
shipped the second write-path telemetry-producer command, `telemetry
record [TARGET] --metric ID --value VALUE`: resolution generalizes the
fourth round's resolver with production's own `activeOnly` option, then
the metric id/value are validated inside the lock at the same point
production's `normalizeMetric` does, and a content-addressed
`measurementId` deduplicates an identical repeated call while the
capability upsert still happens unconditionally, matching production's
own `addMetric` exactly. A sixth round shipped the third write-path
telemetry-producer command, `telemetry ingest [TARGET] --input FILE`,
restricted to the `generic` adapter (zero provider-specific field
mapping; every other real adapter name is rejected outright, its own
future slice): ports `ingestAdapted` field-for-field -- snapshot dedup
keyed by a digest of the original un-adapted input (a real bug this
round's own manual smoke-testing against real Node caught and fixed
before any test existed, since the naive formula using `adapted.raw`
instead produces a different digest), the declared-unavailable
consistency guard, identity-versus-classification/scope merge semantics
kept as two distinct functions, general metric normalization, capability
upsert now keyed by `(metricId, providerField)` rather than `metricId`
alone (the `Capability` domain type gained an optional `ProviderField`),
raw-payload redaction/truncation plus the four-branch byte-budget
retention policy, and provenance-source dedup. A seventh round shipped
`telemetry classify`, a thin wrapper over the sixth round's generic-ingest
path: it builds a synthetic ingest whose only content is a constructed
`classification` object and an explicit `raw: {}`, using a real-clock
`` `classification-${Date.now()}` `` snapshotId rather than a content
digest, so a repeated call is never deduplicated the way a normal ingest
snapshot would be. An eighth round shipped `telemetry start WORKITEMID`,
the fourth write-path telemetry-producer command and the first with no
state transition of its own to plan: it calls the same low-level pure
`ExecutionLinkRecovery.decide` `work start`/`resume`'s own telemetry
resolution uses, recovering an unambiguous detached candidate, rejecting
ambiguity with production's exact rerun message, or creating a new
execution -- linked into `telemetryExecutionIds` via a narrow direct
context read-modify-write rather than the transition-plan-shaped
`FileWorkContextRepository.applyContextPlan`; `CreateExecutionRequest`
gained a real, previously-unported `ClassificationRationale` field;
`--execution-id` and the eleven identity-override flags production's own
CLI exposes are deliberately excluded and loudly rejected (exit 2), their
own future slice. A ninth round shipped `adapter call --store FILE
--request FILE`, the external work-system adapter conformance test
(`DF-ROS-2026-A007`) -- not telemetry, but tracked in this same
remaining-scope inventory: a pure `Ros.Domain.Work.AdapterContract.decide`
mirrors production's `callFileAdapter`/`validateAdapterRequest` branch
order exactly (protocol-version and operation-support checks before the
store file is ever touched, a cached `requestId` replay, repository
authorization, `simulateOutcome: "unknown"` fault injection, then each
operation's own forbidden/not-found/conflict checks), with a new
`Ros.Infrastructure.Work.FileAdapterRepository.call` performing the
actual JSON read/mutate/write -- a `transitionWorkItem` success mutates
only `state`/`updatedBy` on the existing work-item node, preserving
every other caller-defined field verbatim, and a `publishRepositoryEvent`
success dedups by `eventId`, matching production exactly. A tenth round
shipped `adapter publish --target FILE`, the paired event-republish
command: it reads every `.ros/events/events.jsonl` event, appends
whichever are not already present (by `eventId`) in the caller-named
`--target`, then refreshes `.ros/publications.json`'s receipt for every
source event -- even an already-published one -- with a fresh
`publishedAt` on every call, a real quirk (a duplicate-only republish
still rewrites every receipt) verified against real Node before any test
existed; the target's parent directory is created unconditionally, even
with zero events (creating the directory but no destination file, since
an empty append loop creates nothing); a directory-creation or append
failure propagates before either the destination or the receipts file is
touched. `resume`'s `parentExecutionId` linkage was scoped next as a
candidate slice, but manual smoke-testing against real Node before
implementation revealed there was no actual feature to port: production's
own computed `prior?.executionId ?? null` is silently discarded, since
`startExecution`'s `discoverIdentity(options.identity ?? options)`
unconditionally picks the truthy `options.identity` sibling over the
`options` object `parentExecutionId` was set on -- confirmed against real
Node, whose created records always carry `parentExecutionId: null`. Every
other increment in this migration reproduces a confirmed production
quirk once found; this one is corrected instead, since Node is being
deprecated rather than patched: `runWorkResume` now supplies the work
item's most recently created execution
(`FileTelemetryQueryRepository.readLatestExecutionId`, new) whenever its
rare no-active-candidate path must create one, while every other action
still supplies none, matching production's own CLI. A dedicated
differential test documents this as a known, deliberate divergence
rather than claiming parity: it runs both real Node (confirming the
defect persists) and the F# CLI (confirming the corrected linkage) side
by side. An eleventh round shipped `telemetry ingest --adapter
openai-codex`, the first real provider-specific field mapping beyond
`generic`. This port confirmed a design win from the earlier generic-
ingest increment: its mutation pipeline (`ingestAdaptedGeneric` --
snapshot dedup, identity merge, capability upsert, metric normalization,
raw redaction/retention) is fully adapter-agnostic once an
`AdaptedSnapshot` value exists, so porting a new adapter means writing
only its own adaptation function plus one dispatch arm, reusing
everything downstream unchanged. `adaptOpenAICodex` declares a fixed
capability for all six usage fields per completed entry regardless of
presence, a metric only for the fields actually present, resolves
`identity.model` from the last record (searched in reverse) carrying a
truthy `server_model` or `model` even if that record was not itself a
completed turn, and reproduces production's own real quirk that
`context.window_size` gets a metric but no adapter-level capability
declaration (the shared metric-recording pipeline upserts one
regardless, since that step applies uniformly to every recorded metric).
Everything still missing from Phase 1 (every other non-generic adapter's
field mapping and `telemetry start`'s own excluded flags) remains its
own future MIG-08 scoping choice. `EV-ROS-2026-A043` has been re-run as
`EV-ROS-2026-A045`: thirteen of the same twenty-six rows read "full
parity" as of that record's own collection point (up from three); the
`telemetry summary`, `telemetry finalize`, `telemetry record`,
`telemetry ingest`, `telemetry classify`, `telemetry start`, `adapter
call`, and `adapter publish` increments above each flip one more of the
still-open rows to "full parity" (twenty-one of twenty-six; the
`parentExecutionId` correction and the `openai-codex` adapter mapping
each refine an already-"full parity" or still-partial row rather than
closing an additional whole one), not yet reflected in a new formal re-run, since a small
row delta does not warrant its own evidence record
-- the acceptance criterion is still every row reading "full parity," so
Phase 1 remains open until a future re-run closes the
rest.

`./ros` continues to invoke Node exclusively until Phase 3 is accepted.
