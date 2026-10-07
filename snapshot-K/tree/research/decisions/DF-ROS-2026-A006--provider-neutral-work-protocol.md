---
id: DF-ROS-2026-A006
title: Provider-neutral repository work protocol
status: accepted
version: 1.0.0
confidence: high
created: 2026-08-16
updated: 2026-08-16
owners: [repository-governance]
related_documents: [docs/work-protocol.md, schemas/work-protocol.schema.json]
supersedes: []
superseded_by: []
tags: [work-protocol, attribution, adapters]
---

# Decision

ROS will own a small, versioned semantic protocol for attributable repository work while leaving canonical work-item and portfolio state to an external provider-neutral project-management adapter.

The stable local transition core is `begin`, `block`, `resume`, and `complete`. Repository-local workflow names map to common semantic states. Completion validates evidence configured by work type. Small append-only events record work attribution and provide idempotency keys for publication.

## Context and evidence

The implementation mission requires meaningful mutations to retain attributable intent without turning ROS into a centralized work database. Existing ROS architecture favors deterministic, dependency-free local tooling and generated views. The implemented boundary keeps repository evidence available even when an external service disappears.

## Alternatives

- Commit-message-only attribution was rejected because it is not a sufficient machine-readable association.
- A central ROS work database was rejected because it violates segregation of duties.
- A full event-sourced workflow engine was rejected as premature complexity.
- Vendor-specific GitHub, Jira, or Linear integration was deferred behind the adapter boundary.

## Consequences

Repositories gain deterministic local enforcement and can pin protocol versions. Active context permits iterative work; completion creates durable path attribution. The initial file adapter proves idempotent publication but is not a production remote integration. Authentication, authorization, explicit unknown outcomes, review transitions, and global aggregation remain deferred.

## Reversibility and validation

The event schema is versioned and additive within protocol 1.x. Breaking semantics require protocol 2.0 and migration documentation. Validation is covered by executable tests for success, missing attribution, missing evidence, block/resume, multiple items, mechanical work, research conclusions, retry idempotency, and external write failure.
