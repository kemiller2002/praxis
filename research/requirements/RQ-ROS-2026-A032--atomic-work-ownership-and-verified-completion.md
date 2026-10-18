---
id: RQ-ROS-2026-A032
title: Atomic work ownership and independently verified completion
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-18
updated: 2026-10-18
research_area: repository-operating-system
priority: critical
related_documents: []
tags: [work-protocol, concurrency, reliability, evidence]
derived_from: [INPUT-695a4e7723d7f795]
provenance:
  contributions:
    EXE-20261018T124842618Z-ce3f5877:
      operations: [created, modified]
      at: 2026-10-18T12:53:00.882Z
      last: 2026-10-18T12:55:12.790Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Canonical ingestion of claimed requirement source"
---

# Atomic work ownership and independently verified completion

This draft canonically ingests REL-PRAXIS-001. Ingestion records the
requirement and does not claim that it is implemented.

## Problem

Concurrent agents can collide on ownership, mutate stale queue state, and mark
completion without independently verifiable evidence.

## Requirements

- **PRX-REL-001:** Implement an atomic claim carrying work-item ID, claimant ID, expected revision, unique claim token, lease expiration, and fencing generation.
- **PRX-REL-002:** Guarantee at most one successful active claim per work item and generation, including concurrent claims and retries.
- **PRX-REL-003:** Reject stale writes using revision comparison and fencing tokens; return structured conflicts that cause refresh/replan rather than overwrite.
- **PRX-REL-004:** Define idempotent durable transitions for unclaimed, claimed, running, awaiting-verification, completed, failed, and expired.
- **PRX-REL-005:** Support lease renewal, crash recovery, and expiry without allowing expired workers to publish late completion.
- **PRX-REL-006:** Completion references an immutable commit, requirement IDs, exact verification revision, validation outcomes, and evidence digest.
- **PRX-REL-007:** Refuse completion when mandatory evidence is missing, stale, or fails Dokimos verification, with actionable error codes.
- **PRX-REL-008:** Ingest only supported Ordo contract versions with valid dependency and decision references; fail before agent execution.
- **PRX-REL-009:** Emit compact per-work-item telemetry for claim collisions, retries, queue wait, tokens, tool calls, tests, rework, and final verification cost.
- **PRX-REL-010:** Provide deterministic race tests for two claims, lease expiry, stale completion, replay, concurrent reconciliation, failure injection, and backward-compatible migration.
- **PRX-REL-011:** Support cohesive batches without shared mutable ownership; every slice retains independently auditable completion evidence.
- **PRX-REL-012:** Preserve the existing `.praxis` intake workflow and do not create GitHub issues as intake records.

## Acceptance criteria

Two concurrent claimants cannot both hold a valid active claim. Stale agents
cannot complete work. Replayed transitions do not duplicate effects.
Unverified completion is rejected. Existing queues migrate without lost
records. Per-verified-requirement cost is computable.

## Delivery and dependencies

Implement in narrow slices with fast tests per slice and integration tests at
tranche boundaries. Record baseline and before/after measures; do not assert
savings without data. Dependencies are Ordo RQ-SDE-2026-0002 (source family
REL-ORDO-001) and REL-DOKIMOS-001.
