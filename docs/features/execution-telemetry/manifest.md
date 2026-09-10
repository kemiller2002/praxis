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
  of `.ros/telemetry/executions/*.json`). `telemetry summary`'s aggregation,
  every write-path telemetry-producer command
  (`start`/`ingest`/`classify`/`record`/`finalize`), and both `adapter
  call`/`adapter publish` commands remain Node-only, each its own future
  scoping choice.

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
  (via `work start`/`work complete`) and the two read-only `telemetry
  adapters`/`telemetry show` commands are real effects; `telemetry
  summary`'s aggregation, every write-path telemetry-producer command, and
  both adapter commands remain Node-only, pending MIG-08's own further
  scoping decisions.
