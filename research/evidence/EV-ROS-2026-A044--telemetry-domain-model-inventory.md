---
id: EV-ROS-2026-A044
title: ROS telemetry domain model inventory and MIG-08 scope correction
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-09
updated: 2026-09-09
research_area: repository-operating-system
evidence_type: primary
supports:
  - DF-ROS-2026-A028
related_documents:
  - DF-ROS-2026-A027
  - research/journals/JR-ROS-2026-A019--fsharp-application-migration.md
  - docs/migrations/fsharp/ROADMAP.md
supersedes: []
superseded_by: []
tags: [fsharp, migration, telemetry, mig-08, scope, sde]
confidence: high
---

# Evidence summary

The migration journal's own "highest-value next step" line after the
telemetry-execution-ID-resolution slice read: "Design the new-execution
creation effect (clock/ID generation under the work-protocol capability)."
That parenthetical understates the work by roughly two orders of magnitude.
This record is a full inventory of `tools/ros_telemetry.mjs` (1516 lines,
read in its entirety), collected specifically to correct that estimate
before anyone budgets a "quick" slice against it.

**Headline finding: creating a single new execution record requires most of
MIG-08's deferred scope, not a standalone clock/ID step.** `startExecution`
alone composes identity discovery, a Git baseline snapshot, metric-registry
loading, and initial capability-status computation — four of MIG-08's five
named responsibilities (`lifecycle, identity, provenance, metric/capability
semantics, aggregation, unknown/raw preservation`) — before a record can be
written at all.

# Method

Read `tools/ros_telemetry.mjs` in full via a dedicated Explore agent tasked
with reporting exact line numbers, function names, and the precise rules
behind the project's "unknown is not zero" capability philosophy, rather
than summarizing from memory or a partial read.

# Domain model inventory

## Execution record lifecycle

States: `active`, `finalized` only (no cancelled/aborted state). Top-level
schema (`startExecution`, lines 528-569): `schemaVersion, executionId,
workItemId, status, startedAt, finalizedAt, identity, provenance,
classification, capabilities, metrics, rawTelemetry, events, repository,
scope, qualitySignals, links`.

## Identity discovery (`discoverIdentity`, lines 349-393)

Fields: `provider, model, modelVersion, runtime, runtimeVersion, sessionId,
conversationId, runId, agentId, subagentId, parentExecutionId,
orchestration`, plus a `discoverySource` moved into `provenance.sources`
rather than stored on the identity object itself. Entirely env-var/options
driven (no file probes): explicit options first, then `ROS_TELEMETRY_*` env
vars, then a hardcoded whitelist of provider-session env vars (`CODEX_
SESSION_ID`/`CODEX_THREAD_ID` -> openai/codex, `CLAUDE_CODE_SESSION_ID` ->
anthropic/claude-code, `GEMINI_SESSION_ID` -> google/gemini-cli,
`COPILOT_SESSION_ID` -> github/copilot, `GITHUB_ACTIONS=true` ->
github/github-actions, `OLLAMA_HOST` -> local/ollama). Later merged
(non-destructively) by provider adapters when real runtime telemetry is
ingested.

## Git snapshot (`gitSnapshot`, lines 201-215; `cleanBaselineChanges`, lines 242-330)

`gitSnapshot` reuses `tools/ros_git.mjs`'s `observeGitStatus` for the
starting dirty-file baseline (does not reimplement status parsing) plus two
extra plumbing calls (`branch --show-current`, `rev-parse HEAD`).
`cleanBaselineChanges` is a separate, heavier finalize-time function with its
own direct `git diff --name-status --find-renames`, `git diff --numstat
--find-renames`, `git ls-files --others --exclude-standard -z`, and
conditional `git rev-list --count` calls, producing commit counts,
added/modified/deleted/renamed counts, test/documentation file
classification, per-extension histograms, and line-added/deleted stats
(including reading untracked files directly for their line counts). It
requires a clean, available starting snapshot with a known commit or returns
an explicit `available: false` with a named reason.

## Metric registry and capability computation (lines 146-464)

`loadMetricRegistry` validates `telemetry/metrics.json` (schema version,
unique IDs, allowed `unit`/`aggregation` per metric). `initialCapabilities`
seeds one capability entry per registered metric using a hardcoded
per-runtime capability table (`RUNTIME_CAPABILITIES`, lines 395-400) crossed
with whether the metric is `ros-derived` (Git-backed) or not. `addMetric` /
`normalizeMetric` validate and record actual measurements, and `upsertCapability`
maintains a bounded history of capability-status changes.

**The exact "unknown is not zero" rule set**, confirmed line-by-line rather
than paraphrased from doctrine: `unknown` = no runtime mapping exists at all
for this metric, or a raw field was seen that isn't a normalized metric;
`supported-unavailable` = the runtime/Git is known capable of this metric
but no value has been observed yet for this execution (also forced at
finalize time when the ending Git snapshot or clean-baseline diff is
unavailable); `supported-observed` = an actual measurement was normalized
and recorded; `derived`/`estimated` mirror the metric's own declared
quality; `unsupported` is a valid enum value the file never itself assigns
(reserved for an adapter/caller's explicit declaration) but which the
validator checks for consistency, and which `ingestAdapted` refuses to
accept a fresh measurement against (you cannot record a value for something
just declared absent in the same snapshot).

## All thirteen exported functions

`loadMetricRegistry`, `loadExecutions`, `startExecution`, `ingestTelemetry`,
`recordTelemetryMetric`, `recordTelemetryLifecycle`, `finalizeExecution`,
`finalizeWorkExecutions`, `showTelemetry`, `summarizeTelemetry`,
`telemetryFindings`, `readTelemetryInput`, `configuredTelemetry` — plus an
internal adapter layer (`adaptOpenAICodex`, `adaptClaudeStatusline`,
`adaptOtel`, `adaptHook`, `adaptGeneric`/`adaptInput`) mapping four
provider-specific payload shapes into the normalized schema.

## Finalize guards

No separate state-machine module; guards live directly in `resolveExecution`
(rejects ingesting into a non-active execution when `activeOnly` is
requested) and `finalizeExecution` (a no-op re-finalize when no new input is
given; a still-permitted final adapter ingest into an already-finalized
record when input is given, behind double-checked locking against a race
with a fresh finalize). `requireFinalization` is enforced only by the
validator (`telemetryFindings`), not by a write-time guard.

## Raw telemetry sanitization

Two regexes (`RAW_REDACTED_KEY` for whole-key matches, `RAW_SENSITIVE_SEGMENT`
for compound-key matches) drive unconditional redaction of sensitive-looking
fields (`authorization, password, secret, credential, *_token, prompt(s),
messages, content, tool_input/response, file paths, email`, etc.) to a
literal `[REDACTED_BY_ROS]` marker, plus truncation of any string over 2048
characters, applied before a payload is ever persisted. A separate,
independently configured retention limit decides whether the
already-sanitized payload is stored at all.

## Aggregation (`summarizeTelemetry`/`timingSummary`, lines 1178-1275)

Four aggregation strategies (`sum`, `maximum`, `latest-per-session` — sums
only the latest measurement per distinct provider session, avoiding
double-counting repeated snapshots of the same session — and `latest`),
plus `none` (unaggregated). `timingSummary` merges execution time spans
(interval-sweep union) to report `calendarSpanMs`/`overlappingExecutionMs`,
explicitly nulling those fields whenever any execution in the set is still
active, since an unfinished execution's true end time is unknown.

# What this means for MIG-08 scoping

Building a "create a new execution" effect that a future F# handler could
safely call — the boundary `ResolvedTelemetryOutcome.PendingNewExecution`
names — is not separable from most of MIG-08's stated scope: it needs
identity discovery, a Git baseline snapshot, metric-registry loading, and
initial capability seeding, at minimum, before a schema-valid record can be
written. A partial implementation (e.g., a record with a fabricated or
empty `identity`/`capabilities` block) would violate the project's own
"never invent a metric" and boundary-preservation rules and would not
actually be readable correctly by Node's own `showTelemetry`/`telemetryFindings`
consumers. This is why `DF-ROS-2026-A028`'s Phase A already describes new-
execution creation as absorbed into "all of MIG-08" rather than as a
free-standing task — this record grounds that framing in the actual code
rather than an estimate.

MIG-08, when it is attempted, needs its own architecture challenge (per
`DF-ROS-2026-A027`'s own precedent of an "architecture challenge before
treatment") and almost certainly its own smaller first vertical slice
inside it — this inventory is exactly the kind of discovery output that
precedent requires before that slice is chosen, not a recommendation of
which slice to pick.

# Limitations

This is a structural/behavioral inventory, not a differential-tested
port — no F# code changed as part of this record. It does not cover the
telemetry HTTP/adapter wire contracts beyond what feeds this file, or
`tools/ros_hub_*` aggregation across repositories.
