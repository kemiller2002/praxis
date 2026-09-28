---
id: DF-ROS-2026-A041
title: Journaled exact-path fallback reconciliation with Git checkpoint
status: accepted
version: 1.0.0
owners: [repository-governance]
created: 2026-09-27
updated: 2026-09-27
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A019, RQ-ROS-2026-A022]
related_documents:
  - DF-ROS-2026-A037
  - requirements/PRAXIS-DUAL-ENTRY-RECONCILIATION.md
  - docs/fallback-reconciliation.md
supersedes: []
superseded_by: []
tags: [architecture, fallback, reconciliation, transaction, checkpoint, provenance]
confidence: high
derived_from: [RQ-ROS-2026-A019, RQ-ROS-2026-A022]
provenance:
  contributions:
    EXE-20260927T145842499Z-f1b40c22:
      operations: [created, modified]
      at: 2026-09-28T01:07:44.000Z
      last: 2026-09-28T02:12:55Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Journaled fallback reconciliation architecture"
---

# Decision

Fallback reconciliation will validate and plan every ordered request in an isolated staging root using the existing work planner, evidence repository, and canonical event renderer. Only a fully accepted staged result becomes a journal containing the exact allowlisted canonical paths, their before/after hashes, their complete intended contents, the input path/hash, and the transaction's deterministic tag.

Recovery replays the journal content rather than re-dispatching transitions. It commits only the journal's canonical paths, with a `Praxis-Reconciliation` commit trailer, then creates `praxis-reconcile/<sanitized-transaction-id>` at that commit. The applied receipt is one of the committed paths. Applied status requires both receipt and tag. Accepted input is removed only after the checkpoint exists; tracked input removal is a subsequent exact-path cleanup commit. A retry resumes any pending journal before attempting to parse input again. Canonical and journal paths fail closed when an existing path component is a symbolic link.

Invalid proposed data and indeterminate effects are different outcomes. Validation/planning failures quarantine the envelope as rejected. A filesystem, Git commit, or tag interruption remains pending with its journal intact and never writes a rejected receipt over partially applied state.

An optional fallback execution is rendered into the canonical execution schema. Measurements remain stored once at execution scope with `stepId`; step nodes reference their measurement and raw-snapshot identifiers. Unavailable/unsupported/unknown input creates capability state without a metric. The complete sanitized raw measurement object and explicit raw provider snapshots are retained so additive provider/pricing fields survive normalization, subject to the receiving repository's configured raw-retention policy. Inputs that would exceed that policy fail before canonical mutation.

A completion envelope that supplies execution evidence must contain only terminal steps. Reconciliation also fails closed if completion would leave a previously linked native execution active; the native execution must first be finalized through its owning runtime. The reconciler never invents a terminal observation for execution state it did not own.

Production CLI reconciliation is serialized by the repository lock. The privileged CI apply workflow builds the reconciler from the trusted base commit and treats the candidate checkout only as data; it never executes candidate code. Fork input stays on the read-only validation path.

# Why

- A receipt written after live transitions cannot distinguish a crash from a replay and can apply one request twice.
- A pure staged plan ensures a later invalid transition or evidence path cannot leave an earlier request applied.
- Before/after hashes detect concurrent or out-of-band changes instead of silently overwriting them.
- A Git tag makes completion independently inspectable and binds the transaction to immutable canonical history.
- Exact-path commits avoid absorbing unrelated staged or working-tree changes.
- Trusted-base CI avoids granting unreviewed pull-request code a write token.

# Alternatives rejected

- Sequentially invoking CLI transition handlers against live state: partial application is not safely replayable.
- Treating a receipt file alone as completion: it is not bound to a commit and can be written before or after an interrupted mutation.
- Tagging the pre-reconciliation HEAD: the checkpoint would not contain the accepted canonical state.
- Building and running pull-request code in `pull_request_target`: it exposes repository write authority to untrusted code.
- A second fallback telemetry store: it would make later aggregation and query semantics diverge from native execution.

# Consequences

- Reconciliation requires Git commit/tag capability and a configured committer. An inability to commit is pending, not rejected.
- The transaction allowlist covers work context/events, completion queue/projection updates, canonical execution records, and applied receipts. New canonical request families must deliberately extend the allowlist and staging planner.
- One envelope remains scoped to one work item and branch; distributed multi-checkout coordination is not claimed.
- External registration is optional. Local instance identity remains authoritative.

# Revisit when

- Praxis supports cross-work-item or distributed reconciliation.
- Signed envelopes or signed checkpoint attestations are required.
- A canonical append-only store replaces current JSON projections.
