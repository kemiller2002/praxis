# REL-PRAXIS-001: Atomic work ownership and verified completion

Priority: P0
Owner system: Praxis
Intake: Praxis document ingestion
Dependencies: Ordo contract REL-ORDO-001; Dokimos evidence REL-DOKIMOS-001

## Problem
Concurrent agents can collide on work-item ownership, mutate stale queue state, and mark completion without independently verifiable evidence.

## Requirements
- PRX-REL-001: Implement an atomic claim operation with work-item ID, claimant ID, expected revision, unique claim token, lease expiration, and fencing generation.
- PRX-REL-002: Guarantee at most one successful active claim for a given work item and generation, including concurrent claim attempts and retries.
- PRX-REL-003: Reject stale writes using revision comparison and fencing tokens; return structured conflict responses that cause refresh/replan rather than overwrite.
- PRX-REL-004: Define durable state transitions for unclaimed, claimed, running, awaiting-verification, completed, failed, and expired; transitions must be idempotent.
- PRX-REL-005: Support lease renewal, crash recovery, and expiry without allowing an expired worker to publish a late completion.
- PRX-REL-006: Completion must reference an immutable commit, requirement IDs, exact verification revision, validation outcomes, and an evidence digest.
- PRX-REL-007: Refuse completion if mandatory evidence is missing, stale, or fails Dokimos verification; make error codes actionable.
- PRX-REL-008: Ingest only Ordo contracts with supported schema versions and valid dependency/decision references; fail fast before agent execution.
- PRX-REL-009: Emit compact per-work-item telemetry for claim collisions, retries, queue wait, tokens, tool calls, test executions, rework, and final verification cost.
- PRX-REL-010: Provide deterministic race tests (two claims, lease expiry, stale completion, replay, concurrent reconciliation), failure injection, and backward-compatible migration of existing work records.
- PRX-REL-011: Support batched cohesive work without sharing mutable ownership; each slice retains independently auditable completion evidence.
- PRX-REL-012: Keep the existing .praxis intake workflow; do not create GitHub Issues as intake records.

## Acceptance
Two concurrent claimants cannot both obtain a valid active claim. Stale agents cannot complete work. Replayed transitions do not duplicate effects. An unverified completion is rejected. Existing queues migrate without lost records. Per-verified-requirement cost is computable.

## Delivery
Implement in narrow slices, fast tests per slice, integration tests at tranche boundary. Record baseline and before/after measurements; do not assert savings without data.
