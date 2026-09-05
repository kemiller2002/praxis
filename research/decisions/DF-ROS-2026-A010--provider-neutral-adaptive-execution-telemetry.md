---
id: DF-ROS-2026-A010
title: Provider-neutral adaptive execution telemetry
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-05
updated: 2026-09-05
supersedes: []
superseded_by: []
supporting_evidence:
  - EV-ROS-2026-A011
related_documents: [docs/development-telemetry.md, docs/work-protocol.md, schemas/execution-telemetry.schema.json, telemetry/metrics.json]
tags: [telemetry, work-protocol, provider-neutral, provenance, architecture]
confidence: high
---

# Decision

ROS will treat development telemetry as versioned execution evidence attached to its existing work lifecycle. `work begin` creates one segmented execution record and a deterministic Git/clock baseline; `work complete` finalizes every active execution before the completed work context is written. Multiple executions, providers, sessions, parents, and subagents may link to one work item.

The core record has two layers:

1. normalized measurements whose semantics, unit, scope, aggregation, quality, and provenance ROS understands; and
2. bounded, sanitized raw snapshots that retain legitimate provider fields without requiring the core schema to understand them.

The normalized vocabulary is a data registry, not a closed provider payload. Capability discovery explicitly represents `supported-observed`, `supported-unavailable`, `unsupported`, `unknown`, `derived`, and `estimated`. A numeric measurement separately records `observed`, `derived`, or `estimated`; zero is valid only as a measurement, never as the encoding of unavailable data.

Provider adapters remain at the ingestion edge. The first adapters accept Codex JSON events, Claude Code status-line/hook/OpenTelemetry JSON, Gemini hook/OpenTelemetry JSON, Copilot hook/OpenTelemetry JSON, and a provider-neutral generic envelope. Unknown fields survive adapter changes and are surfaced for later normalization.

# Context and evidence

Repository archaeology found one executable work seam: the dependency-free Node CLI owns transitions, evidence checks, Git attribution, validation, and the bootstrap snapshot. Existing `.ros/events` are small publication-oriented semantic events, not a suitable high-volume metric document. There was no execution daemon, runtime hook, session model, cost/token record, capability registry, or provider telemetry contract. Root `AGENTS.md` was the only canonical startup router; Claude, Gemini, and Copilot-specific discovery files were absent. `EV-ROS-2026-A011` records both those findings and current official provider surfaces.

# Alternatives

- **Add token fields to work items:** rejected because one work item may span agents/sessions/providers, cumulative session totals would be double-counted, and future fields would be lost.
- **Store everything in `.ros/events/events.jsonl`:** rejected because raw snapshots have different retention, privacy, validation, and growth characteristics from small immutable semantic work events.
- **Adopt one provider or OpenTelemetry shape as canonical:** rejected because provider coverage and metric semantics differ, OTel exporters evolve, and some useful runtime snapshots are not OTel.
- **Raw telemetry only:** rejected because it preserves data but prevents stable comparison and reliable aggregation.
- **Normalized schema only:** rejected because every new provider field would be discarded or would break ingestion.
- **Attribute Git deltas across a dirty starting tree:** rejected as scientifically misleading; ROS records those execution-change metrics as unavailable instead.
- **Install vendor hooks automatically:** rejected because hooks execute with developer privileges, can conflict with project policy, and require provider-specific security review.

# Consequences

New work automatically inherits execution identity, baseline/finalization, and validation without a prompt reminder. Runtime detail still depends on an adapter, hook, API, structured output, or explicit fact report. Historical work with no telemetry links remains valid. The default record is larger than the old context but is segmented and raw snapshots are capped. Dirty baselines sacrifice execution-level line/file attribution to preserve trustworthiness. Cross-provider comparisons must honor scope and semantic differences rather than treating similarly named counters as equivalent.

The schema and metric registry are additive within telemetry 1.x. Breaking identity, unit, measurement-quality, or aggregation meaning requires a new major schema and migration. Central publication, signing, reconciliation, retention, and portfolio analytics remain deferred behind the external adapter/central-system boundary established by `DF-ROS-2026-A006` and `DF-ROS-2026-A007`.

# Reversibility and validation

Telemetry can be disabled only through explicit configuration with a recorded reason; doing so does not rewrite historical records. Individual adapters are replaceable without changing normalized records. Tests cover lifecycle, full/partial/new/removed provider fields, zero/unavailable, derived/estimated quality, multi-provider/subagent aggregation, privacy filtering, classification/R&D facts, duplicate IDs, schema versioning, Git derivation, and finalization. Revisit when real-provider pilots reveal incompatible semantics, record growth becomes material, or a stable cross-runtime OTel ingestion profile can replace adapter-specific mappings.
