---
id: RP-ROS-2026-A013
title: Provider-neutral adaptive development telemetry implementation and handoff
research_area: repository-operating-system
discipline:
  - software-architecture
  - software-metrics
  - research-systems
author_agent: openai-codex
version: 1.1.0
status: accepted
confidence: high
completion: complete
priority: high
related_projects:
  - Repository Operating System
related_documents:
  - DF-ROS-2026-A010
  - EV-ROS-2026-A011
  - EV-ROS-2026-A014
  - JR-ROS-2026-A012
  - docs/development-telemetry.md
supersedes: []
superseded_by: []
tags: [telemetry, provider-neutral, execution, provenance, handoff]
keywords: [capability-discovery, normalized-metrics, raw-telemetry, multi-agent, research-development]
created: 2026-09-05
updated: 2026-09-05
---

# Research State Snapshot

- **Theory Version:** not established; no theory record changed.
- **Knowledge Base Version:** ROS `1.2.1` plus telemetry schema `1.0.0`.
- **Highest Confidence Areas:** ROS lifecycle integration, Git/clock collection, structural validation, and tested aggregation behavior.
- **Lowest Confidence Areas:** semantic comparability across live provider billing and token implementations.
- **Largest Remaining Unknown:** how real provider streams behave across upgrades and long multi-agent sessions.
- **Active Research Streams:** none required to complete this bounded mission; provider pilots are recommended.
- **Recently Invalidated Ideas:** one universal provider shape, dirty-baseline attribution, broad-prefix unknown-field matching, and unversioned cost calculations.
- **Priority Changes:** pilot and calibrate before central publication or cross-provider benchmarking.

# Executive Summary

ROS now makes execution telemetry part of the work protocol rather than a prompt-only afterthought. Each execution has stable identity, capability state, normalized measurements, bounded sanitized raw snapshots, lifecycle events, repository/scope/quality context, and evidence links. Provider-specific translation stays at the edge. `work begin` creates the record and Git/clock baseline; `work complete` finalizes all active executions and validation rejects structurally untrustworthy records. `EV-ROS-2026-A011` and `DF-ROS-2026-A010` support the architecture.

Confidence is High for the local implementation and simulated adapter behaviors: 24 focused telemetry tests and the full 84-Node/7-Python suite pass. Confidence is Medium-to-Low for provider-to-provider comparisons without matched live pilots because token, cache, cost, routing, and cumulative scopes differ.

# Original Objective

Inspect ROS end to end and implement a comprehensive, provider-neutral, adaptive telemetry capability spanning normal execution, capability discovery, normalized/raw retention, major agent ecosystems, deterministic repository metrics, work/R&D facts, multi-agent aggregation, privacy, validation, tests, migration, and durable findings.

Success required implementation in the repository, not a proposed design, plus an adversarial review and reconstructable handoff.

# Scope

Included the canonical agent contract, CLI work lifecycle, bootstrap snapshots and manifests, runtime adapters, schema/registry, segmented storage, Git/clock derivation, evidence linkage, classifications, quality/scope data, validation, provider routers, documentation, migration notes, tests, decision/evidence/journal records, and this REP.

Central publication, vendor hook installation, signed records, remote retention, and portfolio analytics were explicitly excluded. Scope grew during review to include portable execution IDs, ignored internal Git paths, qualitative estimate confidence, calculated-cost pricing provenance, and raw-file tamper validation.

# Repository Context

The starting point was branch `main` at `aa06e6be9da363db2da33308634bbf5268b759e1`, with pre-existing Project Administration Hub work preserved. Repository archaeology found the dependency-free Node CLI to be the single reliable lifecycle, evidence, Git, and validation seam. Bootstrap manifests freeze that behavior into consumers. Existing semantic work events were unsuitable for high-volume raw telemetry, and historical completed items lacked execution records that could be reconstructed honestly. See `EV-ROS-2026-A011`.

# Current Understanding

The smallest coherent architecture is an execution envelope linked to, but separate from, work items. A work item may own multiple sequential, parallel, provider-mixed, resumed, parent, or subagent executions. A registry defines comparable metrics and aggregation; adapters map what they understand while sanitized raw data preserves what they do not. Capability state describes availability independently of numeric measurement, so unavailable never becomes zero. Provider facts, ROS-derived facts, estimates, and cooperative reports retain distinct provenance.

# Key Discoveries

- The existing work transition CLI was sufficient to guarantee start/finalization without adding a daemon (`EV-ROS-2026-A011`, `DF-ROS-2026-A010`).
- Current provider surfaces overlap but do not share one stable shape or scope; adapter-neutral storage is more durable than adopting any vendor or OTel payload as canonical (`EV-ROS-2026-A011`).
- A dirty starting worktree makes execution-specific Git deltas scientifically unidentifiable; recording unavailability is more trustworthy than attributing the entire diff (`DF-ROS-2026-A010`).
- Exact mapped-leaf matching is necessary: treating a whole provider object as mapped would silently hide a field added tomorrow.
- Calculated cost without a versioned pricing source is not reproducible, and an arbitrary numeric confidence creates false precision.
- ROS context, queue, event, telemetry, and generated registry files must be excluded from product-development deltas to avoid observer-induced inflation.
- Raw-payload retention and snapshot idempotency are separate concerns; disabling raw values must not make retries double-count.
- Session identity must be namespaced by provider and runtime, and OTel aliases/timestamp encodings require normalization before comparison.
- Time-to-first-token, request failures, permission friction, baseline dirtiness, binary/rename changes, and optional memory/CPU were added because they can explain latency, reliability, attribution quality, and resource cost without collecting content.

# Evidence Registry

- `EV-ROS-2026-A011`: repository archaeology and eight official documentation/source surfaces across Codex, Claude Code, Gemini CLI, and GitHub Copilot.
- `JR-ROS-2026-A012`: chronological implementation, baseline, corrections, and adversarial-review observations.
- `EV-ROS-2026-A014`: iterative architectural challenge, alternative comparison, concurrency/retention/provenance revisions, accepted weaknesses, and reopening criteria.
- Executable evidence: `tests/telemetry.test.mjs` (24 focused cases) and the complete repository suite (84 Node plus 7 Python tests).
- Execution evidence: `.ros/telemetry/executions/EXE-20260905T030921723Z-964f55fb.json` and `.ros/telemetry/executions/EXE-20260905T150039043Z-1e221f2a.json`; Git-delta metrics are explicitly unavailable because both executions began on already dirty trees.

# Hypothesis Registry

No formal `HY-` record was warranted. Three implementation assertions were tested directly: unknown fields survive unchanged adapter code; capability absence is distinct from zero; and repeated session-cumulative values aggregate once per session. All passed their focused tests. General cross-provider comparability remains an untested research hypothesis and must not be inferred from shared metric names.

# Failed Assumptions

- A fixed normalized schema alone could adapt to provider evolution: rejected because new fields would be lost.
- Raw-only storage was sufficient: rejected because stable comparison and aggregation need explicit semantics.
- Broad mapped prefixes were safe: falsified by a nested future-field test.
- Any Git diff after task start belonged to the task: rejected for dirty baselines and ROS-internal changes.
- Provider-estimated cost justified a numeric `0.8` confidence: rejected as manufactured precision; categorical confidence is now supported.
- A sanitized ingestion path alone protected stored raw data: falsified by direct-file tampering; validation now detects sensitive keys.

# Open Questions

1. Which real provider field semantics remain stable across two or more versions?
2. How much record growth occurs in long-running multi-agent repositories?
3. Which token/cache/cost measures can be compared after controlling for task, model routing, and session scope?
4. What signing, reconciliation, retention, deletion, and authorization contract should central publication use?

# Recommended Next Research

Run matched, content-disabled pilots for each provider adapter against version-pinned runtime fixtures. Preserve raw fixtures, verify units and cumulative scope, measure record growth, and revise mappings only from official semantics plus observed payloads. Stop cross-provider comparison if routing identity, scope, or billing provenance cannot be matched.

# Research Backlog

1. Version-pinned live Codex, Claude Code, Gemini CLI, and Copilot fixtures.
2. Test/build result adapters for machine-readable TAP/JUnit/compiler formats.
3. Record-size and retention study over thousands of executions.
4. Publication-envelope signing, repository identity, idempotency, and reconciliation design.
5. Matched-task study of telemetry overhead and behavior bias.

# Suggested Specialized Research Agents

No specialized agent was required for this bounded implementation. Future pilots would benefit from independent provider-runtime integrators plus a software-metrics reviewer; same-provider agents should not be labeled independent replication.

# Parallel Research Opportunities

Provider fixture collection, build/test adapter design, storage-growth measurement, and central-publication threat modeling can proceed independently if they write separate artifacts. Cross-provider synthesis must wait for the versioned fixtures and matched-scope review.

# Risks

- Provider schemas or undocumented semantics can change despite adapter tests.
- Similar metric names can conceal different scopes, cache rules, or reasoning-token treatment.
- Agent-reported R&D and correction signals may be incomplete or behaviorally biased.
- Raw telemetry can still contain unexpected sensitive values under novel, innocuous-looking keys; content capture must remain disabled upstream.
- Segmented repository records can grow materially; no rotation policy exists yet.
- Local files are not signed or centrally reconciled, so tamper evidence is limited to normal Git history and validation.

# Cross-Discipline Opportunities

Software measurement supplies construct-validity and denominator discipline; observability supplies source/scope conventions; provenance systems supply lineage; privacy engineering supplies minimization and retention; research methodology supplies preregistration and falsification; accounting/FinOps can later review cost semantics without allowing ROS to make legal or tax conclusions.

# Knowledge Relationships

`provider/runtime surface -> adapter capability observation -> normalized measurement + sanitized raw snapshot -> execution -> work item -> code/evidence/result`

`EV-ROS-2026-A011 -> DF-ROS-2026-A010 -> telemetry schema/registry/CLI -> tests -> RP-ROS-2026-A013`

# Theory Impact Assessment

- **Affected Theory Records:** none.
- **Affected Engineering Principles:** deterministic collection over self-report; provider-neutral core with adapters at boundaries; explicit uncertainty and provenance.
- **New Principle Candidates:** an observability system must preserve unknown fields without treating them as comparable; measurement preconditions belong in capability state.
- **Deprecated Principles:** none canonically.
- **Confidence Changes:** confidence in local lifecycle enforceability rose from Low to High after executable tests; provider comparison remains Low pending pilots.
- **Predictions Created:** new leaf fields will ingest without failure and surface as unknown; dirty baselines will never produce task-attributed line/file deltas; latest-per-session totals will not double-count shared sessions.
- **Predictions Invalidated:** none after the final focused suite.
- **Required Theory Registry Updates:** none; evaluate principle candidates after real-provider replication.

# Research Quality Metrics

- **Primary sources:** 8 official provider documentation/source pages, recorded in `EV-ROS-2026-A011`.
- **Independent source families:** 4 provider ecosystems plus direct repository evidence.
- **Counterexamples reviewed:** 17 material adversarial findings that produced fixes across repeated review cycles, including persistence races, retention/provenance gaps, aggregation ambiguity, runtime-policy drift, and lock failure-path cleanup.
- **Competing viewpoints reviewed:** 6 architectural alternatives in `DF-ROS-2026-A010`.
- **Formal hypotheses tested:** 0; 3 implementation assertions tested in executable cases.
- **Failed formal hypotheses:** 0; 6 informal assumptions were rejected and retained above.
- **Research completeness:** complete for all 20 mission completion criteria; live provider calibration and central publication were outside scope.
- **Confidence gain:** local architecture/implementation Low -> High; cross-provider analytics unchanged at Low.
- **Open questions reduced:** repository placement, lifecycle, compatibility, privacy boundary, and aggregation resolved; 4 follow-up questions remain.

# Research Debt

- **High — Missing evidence:** live versioned provider fixtures; consequence is uncertain adapter drift and comparability.
- **High — Missing experiment:** matched-provider calibration; consequence is risk of misleading efficiency comparisons.
- **Medium — Tool limitation:** no automatic TAP/JUnit/compiler ingestion; explicit structured recording remains necessary.
- **Medium — Storage evidence:** growth/rotation thresholds are unmeasured.
- **Medium — Privacy replication:** sensitive-key filtering cannot replace provider-side content suppression.
- **Low — Missing discipline:** independent FinOps/accounting review before central cost reporting.

# Repository Updates

The durable update spans the canonical agent/governance contract, work CLI and bootstrap packaging, telemetry module, versioned execution schema, metric registry, ROS/starter configurations, provider entry routers, focused tests, development/migration documentation, architecture/current-state/risk context, decision/evidence/journal records, and generated registries. Exact paths are recoverable from Git and listed in the completion report.

# Website Updates

No telemetry UI or website publication was added. The existing Project Administration Hub work was outside this mission and preserved. A telemetry UI should wait until real records establish useful views and privacy/retention rules.

# AI Consumption Notes

Reliable facts: telemetry is per execution; work items may link many executions; zero is a measurement; unavailable is capability state; raw fields are sanitized and bounded; Git deltas require a clean baseline; cumulative session totals aggregate latest-per-session. Caveats: provider field names are adapter knowledge, not universal semantics; agent reports are lower assurance; current local records are unsigned. Useful retrieval terms include `development-telemetry`, `execution-telemetry.schema`, `telemetry/metrics`, `DF-ROS-2026-A010`, and `WI-0003`.

# Handoff Instructions

Begin with `docs/development-telemetry.md`, then inspect `tools/ros_telemetry.mjs`, `telemetry/metrics.json`, `schemas/execution-telemetry.schema.json`, and `tests/telemetry.test.mjs`. Run `npm test`, `./ros registry check`, and `./ros validate`. To add a provider field, first preserve an observed sanitized fixture, confirm official unit/scope/cumulative semantics, add or reuse a metric registry entry, narrow the adapter mapping to exact leaf fields, and add zero/unavailable/version/aggregation tests. Do not enable prompt or tool-content logging solely for telemetry.

# Research Journal

- `JR-ROS-2026-A012`: archaeology, baseline, provider survey, implementation, corrections, and adversarial closeout.

# Appendix

Validation method: dependency-free CLI tests in isolated temporary Git repositories, provider-shaped JSON/JSONL fixtures, duplicate/tamper/schema/configuration failures, clean and dirty Git preconditions, parallel starts/callbacks/finalization, stale-lock recovery, bounded raw/history retention, reverse backlinks, overlapping timing, raw-disabled retries, cross-provider session-ID collisions, OTel timestamps/aliases, repeated shared-session totals, and complete repository regression testing. Baseline suite: 60 Node and 7 Python tests. Final broad suite: 84 Node and 7 Python tests. The sandboxed server suite required localhost permission; with that permission it passed.

# Completion Checklist

- [x] Metadata and research snapshot are complete.
- [x] Every mandatory REP section is present.
- [x] Architectural claims link to evidence and the accepted decision.
- [x] Rejected assumptions and null/unavailable evidence are preserved.
- [x] Theory impact and lack of required theory mutation are explicit.
- [x] Quality metrics state counts, sources, and limitations.
- [x] Research debt is prioritized.
- [x] Repository and website boundaries are explicit.
- [x] Handoff commands and extension procedure are executable.
- [x] Focused and broad tests pass.
- [x] Registry build/check and final `./ros validate` are required closeout gates before handoff.
