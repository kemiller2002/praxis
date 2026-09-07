---
id: EV-ROS-2026-A014
title: Iterative telemetry architecture challenge and reliability revisions
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-05
updated: 2026-09-05
supersedes: []
superseded_by: []
research_area: repository-operating-system
evidence_type: primary
supports:
  - DF-ROS-2026-A010
related_documents: [docs/development-telemetry.md, tools/ros_persistence.mjs, tools/ros_telemetry.mjs, tests/telemetry.test.mjs]
tags: [telemetry, adversarial-review, concurrency, persistence, provenance, storage]
confidence: high
---

# Objective and method

After the first provider-neutral telemetry implementation passed, `WI-0007` challenged it again rather than treating test success as completion. The review covered architecture, schema and event semantics, provider boundaries, persistence, Git integration, synchronization, aggregation, validation, tests, bootstrap packaging, migration, documentation, operational burden, and downstream publication readiness. Each material finding was converted into an executable regression before closeout.

# Challenge cycle 1: persistence and concurrent writers

## Argument against the design

Execution records and context used direct whole-file writes and unlocked read-modify-write updates. Two provider callbacks could read the same execution revision and allow the last writer to erase the other. Parallel `telemetry start` operations could similarly lose work-item backlinks. An interrupted write could leave malformed JSON. The architecture claimed multi-agent representation but did not mechanically support concurrent writers in one local checkout.

The first attempt introduced a shared persistence helper. The focused suite then disproved bootstrap completeness: consumer fixtures could not import the helper because both starter manifests and the npm file list froze an explicit file set. The lock file also appeared as a dirty development path, making a clean baseline look contaminated.

## Alternatives and decision

- An append-only measurement/event journal would minimize write contention and preserve every mutation, but would require a new materialization/index/recovery contract, complicate validation and Git review, and create a larger migration for scale not yet observed.
- SQLite would provide transactions but add a binary mutable artifact and runtime dependency, weakening transparent Git diffs and the current dependency-free bootstrap.
- One raw file per callback would reduce contention on the execution envelope but create high file-count and cross-file finalization/reconciliation burden.
- Same-directory atomic replacement plus short, resource-scoped local locks preserves the current readable per-execution contract with substantially less migration and administration.

ROS adopted the fourth option. JSON and derived queue Markdown now use atomic replacement. Work-context transitions share a local lock, execution creation uses an identity-index lock, and mutations use per-execution locks. Lock files are transient, ignored by Git and metric attribution, reclaim abandoned process owners, and use ownership tokens so an older writer cannot unlink a successor's lock. Starter manifests and npm packaging include the shared helper.

## Evidence

The new parallel test starts eight executions against one work item and launches twelve provider callbacks against one execution. All nine execution links, twelve raw snapshots, twelve normalized measurements, and twelve ingestion events survive. The existing clean-baseline Git test also passes with lock paths excluded.

# Challenge cycle 2: provenance, retention, and finalization races

## Argument against the revised design

The current capability projection overwrote the evidence that a provider had changed from observed to unavailable. Provider timestamps were incorrectly assumed to arrive in ROS order. Raw retention was capped per snapshot but not per execution, so a long hook-driven session could grow without bound. A final provider envelope and a competing finalize operation could race, allowing the no-input finalizer to close the record first and discard the final usage envelope.

## Alternatives and decision

- Retain every capability assessment as a separate normalized event: strongest history, but duplicates high-frequency same-state observations and increases record/write cost without equivalent analytical value.
- Preserve only the latest capability: simplest, but insufficient to audit provider field removal or adapter drift.
- Preserve a current projection plus bounded state-change history, keeping source assessment time separate from ROS recording order: retains material transitions with explicit loss accounting and bounded administration.

ROS adopted the third option. Capability entries now retain prior states, reason, source, assessment time, and ROS recording order. History keeps the first and most recent transitions under a configurable cap and records `historyOmitted` for collapsed intermediate states. This explicitly accepts lossy compression only after the configured boundary.

Raw payload retention now has per-input, per-snapshot, per-execution count, and per-execution byte budgets. Budget exhaustion omits values but preserves normalized measurements, field names, redaction counts, idempotency, and an omission reason through `telemetry.raw_snapshots_omitted`. Final-envelope ingestion and finalization now execute under the same record lock; an explicitly supplied final envelope may be appended idempotently even if another finalizer won the race.

Tests cover observed-to-unavailable history, delayed provider timestamps, bounded/flapping capability history, snapshot-count and byte budgets, retained metrics/field discovery after raw omission, and competing finalization.

# Challenge cycle 3: aggregation semantics and structural invariants

## Argument against the revised design

Summing per-execution wall time can be mistaken for elapsed work-item time when agents overlap. Repeated passing-test results can be mistaken for unique tests. Validation checked context-to-execution links but not the reverse, so a crash between two atomic files could strand a record silently. It also allowed duplicate capability identities and measurement IDs, which could inflate aggregation after manual edits or faulty import.

ROS added a work-item timing projection separating calendar span, total execution wall time, and overlap, and clarified repeated-test semantics in the metric registry. Incomplete timing summaries remain null rather than presenting partial effort as a complete total. Validation now rejects missing execution backlinks, duplicate capability identities, duplicate measurement IDs, invalid transition history, retention-budget violations, and mismatched raw byte counts.

# Challenge cycle 4: configuration contract parity

## Argument against the revised design

Passing schema fixtures did not prove that normal CLI operations would reject an invalid local retention policy. ROS deliberately avoids a runtime JSON Schema dependency, so a repository could set a zero or nonsensical history/raw limit, pass that value into ingestion, and fail later or retain data contrary to the documented contract. This exposed hidden duplication between the declarative schema and the dependency-free operational validator.

The materially different alternative was to replace the manual checks with a full JSON Schema runtime. That would make the schema the single validation implementation, but it would add a package/runtime dependency to bootstrap consumers and still require explicit semantic checks for cross-file links and aggregation. ROS instead added narrow runtime validation for every configurable retention bound and converts invalid values into deterministic `ros.json` findings. A regression proves that `maxCapabilityHistoryEntries: 1` is rejected before telemetry mutation. The remaining schema/manual-validator duplication is now explicit and covered at the policy boundary; a full schema runtime remains a future option if breadth or drift defects justify its cost.

# Challenge cycle 5: lock failure-path ownership

## Argument against the revised design

Exclusive lock-file creation prevented two successful owners, but it did not make every acquisition failure safe. If owner-metadata writing failed after creation, the descriptor and empty/partial lock could remain. In addition, two waiters evaluating an abandoned lock could race: one could reclaim it and a later waiter could act on its stale observation unless it compared the current owner data again.

The alternative was a directory-lock package with tested stale-owner semantics. That would add a dependency and would not remove the need for ROS-specific ownership metadata and timeout policy. The local helper was instead hardened to clean up a just-created lock when metadata writing fails and to re-read the observed owner payload immediately before stale removal. A direct regression now covers stale malformed-lock recovery, cleanup when the protected operation throws, and subsequent reacquisition. Atomic filesystem operations still do not provide cross-host leases or durable distributed consensus; those remain outside the stated contract.

# Cross-criteria comparison

| Approach | Correctness and reliability | Simplicity and overhead | Adaptability and downstream use | Auditability and compatibility |
|---|---|---|---|---|
| Current atomic, locked execution envelopes | Strong for local concurrent callbacks; detects cross-file gaps | One small dependency-free helper; whole-record rewrites remain | Raw boundary and stable envelopes remain publication-friendly | Human-readable Git records; additive schema fields preserve 1.0 readers |
| Append-only journal plus materializer | Strongest high-volume ingestion and replay | Highest implementation, indexing, recovery, and operator burden | Best if callback volume becomes large | Excellent event lineage, but requires a new read/migration contract |
| SQLite local store | Strong local transactions | New binary/dependency/backup administration | Good querying; weaker Git-native interchange | Poorer reviewability and a larger publication converter |
| Unlocked direct JSON | Fails concurrent and interrupted writers | Superficially simplest | Becomes unreliable under the already-supported multi-agent model | Weak because loss can be silent |

The current approach best fits demonstrated local scale while keeping a migration path: stable execution IDs, normalized measurement IDs, raw snapshot IDs, source provenance, and per-execution segmentation allow a future journal or central ledger to ingest existing records without redefining their meaning.

# Rejected approaches

- A provider-specific or OTel-native persistence core remains rejected because providers expose different scopes and non-OTel surfaces.
- Automatic vendor-hook installation remains rejected because it executes with project privileges and creates provider-specific policy coupling.
- Unlimited history/raw capture remains rejected because its evidence value does not justify repository growth and privacy risk.
- Dropping all telemetry at the first raw limit remains rejected because normalized data and unknown field names remain useful and low risk.
- Making metrics/events silently lossy at arbitrary limits remains rejected until real volume evidence can establish scientifically defensible compaction rules.
- Replacing the dependency-free manual validator with a JSON Schema runtime remains deferred: it would reduce schema/validator drift, but adds installation/runtime surface for checks already exercised by executable parity tests. Reconsider if schema breadth or drift defects increase.

# Remaining weaknesses intentionally accepted

- Atomic record creation and context backlinking are two file replacements, not a distributed transaction. Reverse-link validation makes a crash gap explicit, but repair is not yet automatic.
- Locks coordinate processes on one local filesystem. They do not claim cross-host or network-filesystem correctness; separate worktrees remain required.
- Backlog capture and attachment mutation use atomic replacement but retain their pre-existing unlocked read-modify-write behavior. They are outside the telemetry execution write path; serialize them if concurrent backlog writers become a supported operating mode.
- Whole-record rewriting can become expensive for thousands of normalized events even though raw payload and capability history are bounded.
- Capability history deliberately collapses intermediate transitions past its limit, retaining the first/recent states and an omission count rather than full provenance.
- The raw redactor is key-based and cannot prove a value under an innocuous new key is safe. Provider-side content suppression remains the primary privacy boundary.
- Adapter semantics are supported by official documentation and simulated fixtures, not version-pinned live-provider recordings.
- Agent-reported R&D, scope, correction, and failed-approach facts remain lower assurance than runtime or deterministic tool signals.

# Verification and diminishing returns

Focused tests reached 24 telemetry cases. The final repository-wide suite passed 84 Node tests and 7 Python tests with localhost permission. An earlier sandboxed broad run failed only the 14 server cases at `listen EPERM`, while all non-server cases passed; that environmental failure had been seen at the original baseline and did not indicate a product regression.

A final review after the lock failure-path regression re-examined provider leakage, persistence ownership, lock recovery, late telemetry, raw growth, high-cardinality fields, aggregation, Git attribution, schema/manual-validator duplication, bootstrap installation, migration, privacy, and central publication. Further in-scope changes were predominantly a new storage subsystem, speculative provider heuristics without fixtures, cosmetic naming, or lossy metric/event compaction without empirical thresholds. Their expected maintenance and migration cost exceeds the demonstrated benefit. This is diminishing returns, not a claim of perfection.

Reopen the architecture if real runs routinely approach retention caps, lock waits/timeouts appear, execution records reach sizes that make writes material, a cross-host writer is required, schema/validator drift causes defects, a stable provider/OTel profile displaces adapters, privacy review finds innocuous-key leakage, or central publication requires signatures, reconciliation, deletion, or globally ordered events.
