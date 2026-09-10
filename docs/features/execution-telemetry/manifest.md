# Feature Manifest — Execution telemetry

## Purpose

Route changes to provider-neutral execution evidence, adaptive metric
capabilities, lifecycle capture, classification, and aggregation.

## Ownership

- State, including presentation state: `.ros/telemetry/executions/*.json`,
  linked from `.ros/context/current.json`; semantic decision
  `DF-ROS-2026-A010` and metric authority `telemetry/metrics.json`.
- Transitions / commands / messages: `tools/ros_telemetry.mjs` symbols
  `startExecution`, `ingestTelemetry`, `recordTelemetryMetric`,
  `recordTelemetryLifecycle`, `finalizeExecution`, and `summarizeTelemetry`.
- Invariants and guards: `telemetryFindings`, `loadMetricRegistry`, capability
  statuses, measurement quality/source/unit/scope/aggregation checks, raw-data
  redaction and retention limits.
- Capabilities / authority: runtime/provider observations may report supported,
  unavailable, unsupported, or unknown fields; ROS-derived and estimated
  values require distinct provenance.
- Important effects and effect contracts: per-execution atomic files and locks;
  execution/context linking composed under `work-protocol` and the recoverable
  work-state journal; typed Git observation through `tools/ros_git.mjs`, plus
  clock/environment discovery; bounded sanitized provider input.
- F# migration (`DF-ROS-2026-A028` Phase A, MIG-08): `EV-ROS-2026-A044`
  inventories this file's full scope and explains why a "quick" slice was
  never realistic (creating one new execution record alone composes
  identity discovery, a Git baseline snapshot, metric-registry loading, and
  initial capability seeding). That identity/Git/registry/capability
  machinery, plus a real clean-baseline change summary and finalization,
  already shipped as part of `work start`/`work complete`
  (`Ros.Infrastructure.Work.FileTelemetryExecutionRepository`/
  `FileTelemetryFinalizationRepository`, in the work-lifecycle manifest) --
  MIG-08's own first increment is the two read-only `telemetry ...`
  commands with zero further new complexity: `telemetry adapters`
  (`Ros.Domain.Telemetry.TelemetryAdapters`, a direct port of the
  `TELEMETRY_ADAPTERS` catalog) and `telemetry show`
  (`Ros.Infrastructure.Work.FileTelemetryQueryRepository`, a lock-free read
  of `.ros/telemetry/executions/*.json`). A follow-on slice closed a real
  gap left open by `work start`/`work complete`: `recordTelemetryLifecycle`
  (`FileTelemetryFinalizationRepository.recordLifecycle`) now appends the
  real `work.blocked`/`work.resumed` events -- deduped by production's own
  sorted-key digest, matching its `TEVT-` `eventId` convention exactly --
  plus their paired `agent.interruptions`/`agent.resumes` metrics, to every
  currently-active execution linked to a work item, called from `work
  block`/`work resume` (in the work-lifecycle manifest) before those
  commands resolve or create any new telemetry execution, matching
  production's own ordering. `time.blocked_ms` (computed at finalization by
  `BlockedDuration.compute`, shipped in the `work complete` increment) now
  computes a real nonzero value instead of always zero. A third increment
  ports `telemetry summary`'s aggregation (`summarizeTelemetry`/
  `timingSummary`): a new pure `Ros.Domain.Telemetry.Summary` module
  computes four real aggregation strategies (`sum`, `maximum`,
  `latest-per-session` -- a real per-session dedup, not a plain sum -- and
  `none`, falling back to a plain "latest wins" default) and an
  interval-sweep timing summary (calendar span vs. total wall time vs.
  overlap), fed by `FileTelemetryQueryRepository.readSummaryExecutions`'s
  JSON extraction/grouping. A fourth increment ships the first write-path
  telemetry-producer command, `telemetry finalize [TARGET]`
  (`FileTelemetryFinalizationRepository.resolveFinalizeTarget`/
  `finalizeTarget`): target resolution ports production's
  `resolveExecution` exactly (an `EXE-`-prefixed target matches by exact
  execution id; any other target matches by workItemId regardless of
  status; no target requires exactly one active/blocked work item, else
  production's exact ambiguity message), then reuses the already-tested
  `finalizeOne` mutation unchanged -- no second finalization code path --
  reproducing production's real pre-lock fast path (an already-finalized
  resolved execution returns untouched, no lock acquired). `--input` (real
  adapter ingestion) is deliberately not ported and rejected outright
  rather than silently ignored. A fifth increment ships the second
  write-path telemetry-producer command, `telemetry record [TARGET]
  --metric ID --value VALUE` (`FileTelemetryFinalizationRepository.
  resolveExecutionTarget`, generalized from `finalize`'s own resolver
  with production's own `activeOnly` option, plus `recordMetric`, new):
  a target whose only match is not currently active is rejected
  (including a race re-checked immediately after the lock, matching
  production's own re-resolve), the metric id must be registered and the
  value must be finite (validated inside the lock, at the same point
  production's `normalizeMetric` does), and a content-addressed
  `measurementId` deduplicates an identical repeated call while the
  capability upsert and record rewrite still happen unconditionally,
  matching production's own `addMetric` exactly. A sixth increment ships
  the third write-path telemetry-producer command, `telemetry ingest
  [TARGET] --input FILE`, restricted to the `generic` adapter (every
  other real adapter name is rejected outright, its own future slice;
  a genuinely unknown name gets production's own exact error):
  `FileTelemetryFinalizationRepository.ingestTarget`/
  `ingestAdaptedGeneric`/`adaptGeneric` port `ingestTelemetry`/
  `ingestAdapted` field-for-field -- snapshot dedup keyed by a digest of
  the original un-adapted input, the declared-unavailable consistency
  guard, identity merge (skip-null) versus classification/scope merge
  (explicit-null-overwrites) kept as two distinct functions, general
  metric normalization honoring every field an ingested metric supplies,
  capability upsert now keyed by `(metricId, providerField)` rather than
  `metricId` alone (`Ros.Domain.Telemetry.Capability` gained an optional
  `ProviderField`, and `MetricId` itself is now optional), raw-payload
  redaction/truncation plus the four-branch byte-budget retention policy
  (new `FileWorkConfigRepository` raw-telemetry config readers), the
  three derived quality metrics routed through the same metric-
  normalization path, and provenance-source dedup by content digest. A
  new general `CanonicalJson.stabilize`/`contentDigest`
  (`Ros.Infrastructure.Json`) mirrors production's `stable()` for
  arbitrary caller-supplied JSON. A seventh increment ships `telemetry
  classify --classification NAME [...] [--rationale TEXT]
  [--evidence-link LINK]* [--rd-context FILE]`, a thin wrapper over the
  sixth increment's generic-ingest path
  (`FileTelemetryFinalizationRepository.classifyTarget`, new): builds a
  synthetic ingest whose only content is a constructed `classification`
  object and an explicit `raw: {}`, using a real-clock
  `` `classification-${Date.now()}` `` snapshotId (not content-addressed,
  so a repeated call is never deduplicated the way a normal ingest
  snapshot would be), then delegates to `ingestTarget` unchanged;
  `--rd-context` reuses `telemetry ingest`'s own `--input`
  reading/parsing exactly. An eighth increment ships `telemetry start
  WORKITEMID [--classification NAME]* [--classification-rationale TEXT]
  [--quiet]`, the fourth write-path telemetry-producer command and the
  first with no state transition of its own to plan
  (`FileTelemetryFinalizationRepository.resolveOrCreateExecution`/
  `startTarget`, new): rejects a work item that is missing from context
  or not active/blocked with production's identical message from either
  cause; calls the same low-level pure `Ros.Domain.Telemetry.
  ExecutionLinkRecovery.decide` `work start`/`resume`'s own telemetry
  resolution uses, recovering an unambiguous detached (unlinked, active)
  candidate, rejecting on more than one with production's exact `; rerun
  with --execution-id one of: ...` message, else creating a new execution
  via `FileTelemetryExecutionRepository.createExecution` (bounded to a
  single attempt); links the resolved id into `telemetryExecutionIds`
  (guarded against a duplicate) via a narrow direct
  `.ros/context/current.json` read-modify-write, deliberately bypassing
  the transition-plan-shaped `FileWorkContextRepository.applyContextPlan`
  -- but, matching production's own `renderedEventLog(root, [])`, never
  appends to `events.jsonl`. `CreateExecutionRequest` gained a real,
  previously-unported `ClassificationRationale` field
  (production's `startExecution`'s `options.classificationRationale ??
  null`). `--execution-id` and the eleven identity-override flags
  production's own CLI exposes (`telemetryIdentityOptions`) were
  initially excluded and loudly rejected (exit 2); a later increment
  (below) closes that gap. The `adapter call`/`adapter publish` commands
  (owned by the work-lifecycle manifest's `DF-ROS-2026-A007`, not
  telemetry; both are now real effects there) remain out of this
  manifest's scope.

  A ninth increment ships `telemetry ingest --adapter openai-codex`, the
  first real provider-specific field mapping ported beyond `generic`:
  `FileTelemetryFinalizationRepository.adaptOpenAICodex` (new) mirrors
  production's own `adaptOpenAICodex` field-for-field, mapping a Codex
  `exec --json` event stream (or a single event object) to normalized
  token/context metrics. This port confirmed a design payoff from the
  generic-ingest increment: the shared mutation pipeline
  (`ingestAdaptedGeneric` -- snapshot dedup, identity merge, capability
  upsert, metric normalization, raw redaction/retention) is fully
  adapter-agnostic once an `AdaptedSnapshot` value exists, so a new
  adapter needs only its own adaptation function plus one dispatch arm in
  `ingestTarget`, reusing everything downstream unchanged. `completed`
  entries (`type === "turn.completed"` or any `usage` object at all) each
  declare a fixed capability for all six usage fields
  (`input_tokens`/`output_tokens`/`cached_input_tokens`/
  `cache_write_input_tokens`/`reasoning_output_tokens`/`total_tokens`),
  present or not, but a metric only for the fields actually present, in
  production's own per-entry per-field order; `context.window_size` is
  the one metric with deliberately no paired capability declaration in
  the adapter itself, a real quirk reproduced exactly (the shared
  metric-recording pipeline still upserts a capability for it regardless,
  since that step applies uniformly to every recorded metric).
  `identity.model` resolves from the *last* record (searched in reverse)
  carrying a truthy `server_model` or `model`, regardless of whether that
  record was itself "completed" -- confirmed against real Node with a
  fixture where the identity-bearing record is not itself a turn.

  A tenth increment ships the three hook adapters --
  `anthropic-claude-hook`, `google-gemini-hook`, `github-copilot-hook` --
  all resolving to one shared `FileTelemetryFinalizationRepository.adaptHook`
  function parameterized only by identity provider/runtime, matching
  production's own one-function/three-name shape. Unlike
  `adaptOpenAICodex`'s fixed six-field capability declaration, `adaptHook`'s
  capabilities are derived strictly 1:1 from whichever metrics actually
  fired: `hook_event_name` (falling back to `hookEventName`/`event`) is
  matched, case-insensitively, against five independent (non-exclusive)
  precompiled regex patterns. PostToolUse/AfterTool/ToolResult contributes
  `tool.calls`, a `toolCategoryMetric`-bucketed metric (11 substring-matched
  categories, falling back to `tool.external_service_calls`), and a
  conditional `tool.failures` when `error`/`tool_response.error` is truthy
  or `success` is explicitly `false`; SubagentStart, PostCompact,
  PermissionRequest, and PermissionDenied each contribute exactly one
  metric of their own. Exactly one `runtime.{hookName}` event is emitted
  per call regardless of how many patterns matched.

  An eleventh increment ships `anthropic-claude-statusline` via
  `FileTelemetryFinalizationRepository.adaptClaudeStatusline`, the first
  real adapter whose input is a single snapshot object rather than an
  event stream -- there is no per-record iteration at all. Three
  top-level fields (`context.window_size`, `context.utilization` --
  computed as `used_percentage / 100` -- and `cost.session_cumulative`)
  and four `current_usage` token fields each always get a capability
  declaration, present or not, matching `adaptOpenAICodex`'s
  fixed-declaration style rather than `adaptHook`'s derived-from-fired
  style. `cost.session_cumulative` is the one field whose present-value
  capability status becomes `"estimated"` rather than
  `"supported-observed"` -- a real quirk, since a statusline snapshot
  cannot directly observe session cost, only estimate it -- and whose
  metric alone carries a `currency: "USD"` extra and an always-present
  `confidence` key (`"medium"` when estimated, JSON `null` otherwise);
  the four `current_usage` metrics carry no such extras at all. This
  adapter emits no events of its own, and `collectedAt` is never
  overridden by an input timestamp field.

  A twelfth and final adapter-mapping increment ships the OTel adapter
  family -- `anthropic-claude-otel`, `google-gemini-otel`,
  `github-copilot-otel`, `otel-json` -- all resolving to one shared
  `FileTelemetryFinalizationRepository.adaptOtel`, the first adapter with
  no fixed field schema at all. Every field is looked up via a shared
  `firstValue` helper across a per-record set of nested candidates (the
  record itself, `attributes`, `resource.attributes`, `body`,
  `dataPoint.attributes`, in that fixed order), matching the range of
  shapes real OTLP JSON exports and Gemini CLI's own metric events
  actually carry. Unlike every other adapter, `identity.provider`/
  `runtime`/`model`/`sessionId` can be overwritten by any record across
  the whole stream, with the last discovered value winning; `otel-json`
  is the one adapter name with no identity seed, resolving both fields to
  `"unknown"`. Metrics come from six direct field mappings (always
  carrying a `dimensions.event` key, populated or empty, never omitted),
  a name-and-type-keyed token-usage mapping, request/tool-result mappings
  (a string `"false"` or boolean `false` success field triggers a
  failure metric), and three Gemini-CLI-specific single-metric mappings.
  Like `adaptHook`, capabilities are derived 1:1 from whichever metrics
  actually fired. `runtimeTimestamp` (production's own heuristic for
  telling an ISO string apart from a numeric nanosecond/millisecond/
  second epoch value by magnitude) is ported for realistic OTel export
  timestamp shapes. This closes MIG-08's telemetry-ingest adapter
  inventory -- every name in `Ros.Domain.Telemetry.TelemetryAdapters.all`
  now dispatches to a real F# effect, so `ingestTarget`'s "not yet
  supported by this CLI" rejection became permanently unreachable and was
  retired along with the allowlist that guarded it.

  A thirteenth and final increment closes the last piece of MIG-08's own
  scope: `telemetry start`'s own `--execution-id` and its eleven
  identity-override flags, excluded since the eighth increment above.
  `--execution-id` needed no new decision logic at all --
  `Ros.Domain.Telemetry.ExecutionLinkRecovery.decide`'s pure
  `RequestedExecutionId` handling, already shipped and already tested,
  was simply threaded through from the CLI for the first time: a match
  among detached candidates recovers that execution; a non-match with
  other candidates present rejects with production's exact `; rerun with
  --execution-id ...` message (naming the first candidate in sorted
  order); no candidates at all lets the requested id become the newly
  created execution's own id. The eleven identity flags reuse
  `Ros.Domain.Telemetry.Identity.discover`'s already-complete override
  handling, built earlier in this migration and never missing a field --
  the actual gap was purely in the CLI-to-repository wiring.
  `FileTelemetryExecutionRepository.CreateExecutionRequest` gained
  `ExecutionId`/`IdentityOverrides` fields (the latter replacing the
  previous standalone `ParentExecutionId` field, folded in as one of the
  same eleven slots), and `createExecution` now merges the full override
  set over environment-discovered identity via a new
  `mergeIdentityOverrides`, mirroring production's own
  `discoverIdentity(options.identity ?? options)` exactly. This closes
  MIG-08's own command-surface scope entirely.

## Interfaces

- Inbound: automatic work lifecycle calls and `./ros telemetry
  start|ingest|record|classify|finalize|show|summary|adapters`.
- Outbound: execution records, normalized metrics, provider-specific raw
  extensions, capabilities, lifecycle events, and summary JSON.

## Tests and verification

- Local behavior tests: `tests/telemetry.test.mjs` and telemetry-related work
  tests.
- Boundary/contract tests: `schemas/execution-telemetry.schema.json`,
  `telemetry/metrics.json`, adapter fixtures, and unknown/zero/unavailable tests.
- Integration/live verification: `./ros telemetry show|summary` and
  `./ros validate`.
- F# telemetry-read real-effect tests: `tests/Ros.Tests/TelemetryQueryTests.fs`
  and `tests/telemetry-show-fsharp-differential.test.mjs` (imports
  production's own `showTelemetry`/`TELEMETRY_ADAPTERS` directly from
  `tools/ros_telemetry.mjs` rather than reimplementing them).
- F# telemetry-lifecycle real-effect tests:
  `tests/Ros.Tests/TelemetryLifecycleTests.fs` and
  `tests/work-telemetry-lifecycle-fsharp-differential.test.mjs` (a real
  `work block`/`work resume`/`work complete` cycle proving the lifecycle
  events, their metrics, and a real nonzero `time.blocked_ms`).
- F# telemetry-summary real-effect tests:
  `tests/Ros.Tests/TelemetrySummaryTests.fs` (every `TimingSummary`/
  `MetricAggregation` branch) and
  `tests/telemetry-summary-fsharp-differential.test.mjs` (a real
  `work start`/`work complete` cycle with cross-checked deterministic
  metrics, work-item filtering, an empty summary, and hand-written fixture
  executions proving `latest-per-session`/`none` byte-for-byte against
  production).
- F# telemetry-finalize real-effect tests:
  `tests/Ros.Tests/TelemetryFinalizeTargetTests.fs` (every
  `resolveFinalizeTarget` branch: `EXE-` exact match, workItemId match
  regardless of status, single/zero/multiple active-item resolution,
  unknown-target rejection, already-finalized untouched) and
  `tests/telemetry-finalize-fsharp-differential.test.mjs` (a real
  `work start` + `telemetry finalize` cycle with cross-checked
  deterministic metrics, workItemId-latest-regardless-of-status and
  `EXE-`-prefixed resolution over hand-written fixture executions, both
  ambiguity variants and the not-found rejection with production's exact
  messages, the already-finalized byte-identical-file fast path, and an
  F#-only assertion that `--input` is rejected with exit code 2).
- F# telemetry-record real-effect tests:
  `tests/Ros.Tests/TelemetryRecordMetricTests.fs` (every resolution
  branch, unknown-metric/non-finite-value rejections, content-addressed
  dedup, a fresh capability creation and an existing-capability-to-history
  upsert, and unit/currency/confidence overrides) and
  `tests/telemetry-record-fsharp-differential.test.mjs` (a real
  `work start` + `telemetry record` cycle byte-identical to production's
  own `recordTelemetryMetric`, workItemId-restricted-to-active and
  `EXE-`-prefixed resolution over hand-written fixtures, both ambiguity
  variants and the not-active rejection with production's exact
  messages, content-addressed dedup, a real registry-seeded capability
  transition, and unit/currency/confidence overrides cross-checked
  byte-for-byte against production).
- F# telemetry-ingest real-effect tests:
  `tests/Ros.Tests/TelemetryIngestTests.fs` (identity/metric/event
  merging with redaction and derived metrics, snapshot dedup,
  declared-capability upsert-into-history, the declared-unavailable
  conflict, classification/scope/links merge semantics, quality-signal
  dedup, every resolution/adapter rejection, all three raw-retention
  branches, and `parseIngestInput`'s JSON/JSON-Lines/empty/malformed
  handling) and `tests/telemetry-ingest-fsharp-differential.test.mjs`
  (the same real-effect surface byte-for-byte against production's own
  `ingestTelemetry`, including snapshotId/eventId digest equality
  independent of executionId or wall-clock time, the
  declared-unavailable rejection's shared snapshotId, config-driven
  retention behavior, and stdin (`-`) input).
- F# telemetry-classify real-effect tests:
  `tests/Ros.Tests/TelemetryClassifyTests.fs` (the classification shape,
  the empty-classification-list rejection, an rd-context merge,
  non-dedup across repeated calls, and EXE-prefixed target resolution)
  and `tests/telemetry-classify-fsharp-differential.test.mjs`
  (reproducing production's own CLI-dispatch construction, since it has
  no standalone exported function, covering the same classification
  shape byte-for-byte, `--rd-context` file merging, the
  empty-classification rejection, and non-dedup across two real calls).
- F# telemetry-start real-effect tests:
  `tests/Ros.Tests/TelemetryStartTests.fs` (fresh creation with no
  candidate, detached-execution recovery, new-execution-when-only-
  candidate-already-linked, ambiguity rejection with production's exact
  message, the not-active-or-blocked rejection for both a missing and a
  wrong-state item, and the disabled-with-no-candidate no-op) and
  `tests/telemetry-start-fsharp-differential.test.mjs` (the same seven
  scenarios end-to-end against the real `ros` CLI, plus an F#-only
  assertion that `--execution-id` is rejected with exit code 2).
- F# telemetry-ingest openai-codex real-effect tests:
  `tests/Ros.Tests/TelemetryIngestOpenAICodexTests.fs` (the fixed
  six-field capability declaration with presence-gated metrics, the
  context.window_size metric-without-adapter-capability quirk, per-entry
  turnIndex assignment, reverse-order identity.model resolution including
  a non-completed identity-bearing record, the completed-entry filter,
  and single-object input wrapping) and
  `tests/telemetry-ingest-openai-codex-fsharp-differential.test.mjs`
  (identity/capabilities/metrics/events compared structurally against
  production's own `ingestTelemetry`/`adaptOpenAICodex`, the reverse-order
  identity resolution, and unknown-field discovery).
- F# telemetry-ingest hook-adapter real-effect tests:
  `tests/Ros.Tests/TelemetryIngestHookTests.fs` (the PostToolUse
  tool-use/tool-category/identity mapping, the truthy-error and no-error
  `tool.failures` branches, SubagentStart's `agent.subagents_spawned`/
  `agentId`, PostCompact/PermissionRequest/PermissionDenied's own distinct
  metrics, and the single-event-per-call invariant) and
  `tests/telemetry-ingest-hook-fsharp-differential.test.mjs` (all three
  adapter names -- PostToolUse-with-error, SubagentStart,
  PermissionDenied -- plus the no-error PostToolUse case, compared
  byte-for-byte against production's own `ingestTelemetry`/`adaptHook`).
- F# telemetry-ingest claude-statusline real-effect tests:
  `tests/Ros.Tests/TelemetryIngestClaudeStatuslineTests.fs` (the
  fully-populated field mapping including the utilization division and
  full identity resolution, the `cost.session_cumulative`
  `"estimated"`-status quirk, the all-absent case with every capability
  `supported-unavailable` and zero metrics, and the no-events invariant)
  and `tests/telemetry-ingest-claude-statusline-fsharp-differential.test.mjs`
  (a fully-populated snapshot, the all-absent case, and the
  estimated-status quirk in isolation, compared byte-for-byte against
  production's own `ingestTelemetry`/`adaptClaudeStatusline`).
- F# telemetry-ingest OTel-adapter-family real-effect tests:
  `tests/Ros.Tests/TelemetryIngestOtelTests.fs` (a direct-field mapping
  with its derived capability, nested `resource.attributes` provider/
  model/session discovery, the api-request success/failure branch, the
  tool-result string-`"false"` failure branch, the three
  Gemini-CLI-specific metrics together, the bare `otel-json` adapter's
  `records`-object unwrapping and unknown/unknown identity seed, and the
  no-events invariant) and
  `tests/telemetry-ingest-otel-fsharp-differential.test.mjs` (a record
  exercising every branch at once -- including a numeric nanosecond
  timestamp -- the `records`-object wrapping shape, and each of the
  other two OTel adapter names' own identity seeding, byte-for-byte
  against production's own `ingestTelemetry`/`adaptOtel`).
- F# telemetry-start identity/execution-id real-effect tests:
  `tests/Ros.Tests/TelemetryStartTests.fs` (`--execution-id` recovering a
  matching detached candidate, rejecting a non-matching one with
  production's exact rerun message, becoming a freshly created
  execution's own id with no candidates present, and all eleven identity
  flags threading into the created record's identity) and
  `tests/Ros.Tests/WorkContextEffectTests.fs` (`createExecution`'s own
  `ExecutionId` override in isolation); a real differential
  (`tests/telemetry-start-fsharp-differential.test.mjs`) drives the
  actual `ros` CLI wrapper directly, not just the exported function,
  covering all four scenarios byte-for-byte against production.

## Dependencies

- Allowed direct dependencies: filesystem persistence, repository Git state,
  clock/random identity, environment metadata, and provider edge adapters.
- Required composition context: active work item for automatic lifecycle and
  `ros.json` telemetry policy.

## Modification boundaries

- Normal: `tools/ros_telemetry.mjs`, metric registry, telemetry schema/docs,
  and telemetry tests.
- Escalation required: metric meaning, quality/capability semantics, redaction,
  retention, identity, or aggregation contract changes.

## Local agent instructions

- `AGENTS.md` and `docs/development-telemetry.md`.

## Maintenance

- Owner: repository-governance
- Last checked against implementation: 2026-09-10
- Known gaps: live model/token/cost fields depend on runtime adapters; whole
  execution records are rewritten on each update; unkeyed response loss after
  complete journal cleanup requires inspection before intentional retry;
  cross-host coordination and signing are deferred. The installed Git adapter
  matches the F# contract but remains Node until distribution is authorized.
  F# parity is now real but partial: execution creation/finalization
  (via `work start`/`work complete`), lifecycle bookkeeping (via `work
  block`/`work resume`), all three read-only `telemetry
  adapters`/`telemetry show`/`telemetry summary` commands, `telemetry
  finalize` (excluding `--input` adapter ingestion), `telemetry record`,
  `telemetry ingest` (every adapter name in `TelemetryAdapters.all` now
  has a real mapping: `generic`, `openai-codex`,
  `anthropic-claude-statusline`, the three hook adapters --
  `anthropic-claude-hook`/`google-gemini-hook`/`github-copilot-hook` --
  and the four OTel adapters -- `anthropic-claude-otel`/
  `google-gemini-otel`/`github-copilot-otel`/`otel-json`), `telemetry
  classify`, and `telemetry start` (including its own `--execution-id`
  and eleven identity-override flags) are real effects -- MIG-08's own
  command-surface scope is now completely real. `adapter call`/`adapter
  publish` are owned by the work-lifecycle manifest, not this one -- see
  its own Known gaps
  for their status.
