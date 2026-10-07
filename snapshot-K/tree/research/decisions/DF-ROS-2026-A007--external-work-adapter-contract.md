---
id: DF-ROS-2026-A007
title: External work-system adapter contract
status: accepted
version: 1.0.0
confidence: high
created: 2026-08-16
updated: 2026-08-16
owners: [repository-governance]
related_documents: [docs/work-adapter-contract.md, schemas/work-adapter-request.schema.json, schemas/work-adapter-result.schema.json]
supersedes: []
superseded_by: []
tags: [work-protocol, adapters, project-management]
---

# Decision

ROS will integrate with external work systems through normalized, versioned request and result envelopes. The initial stable operations are `getWorkItem`, `transitionWorkItem`, and `publishRepositoryEvent`.

Every request carries a durable request ID, repository identity, principal, protocol version, and authorization scopes. Every result distinguishes `success`, `failure`, and `unknown`. Repeated request IDs return the first recorded result, while semantic event IDs prevent duplicate publication.

## Rationale and alternatives

The work protocol needs external state without importing project-management ownership into ROS. A vendor SDK in core was rejected because it would couple protocol semantics to one product. Treating timeouts as failures or successes was rejected because either choice can duplicate or conceal remote effects. A generalized query API was deferred because no concrete consumer yet establishes its stable semantics.

## Consequences

Adapters can be replaced without changing repository evidence or ROS transitions. Production implementations must authenticate outside caller-controlled content and derive scopes from trusted credentials. The included file adapter is only a deterministic conformance double; it is not the canonical project-management store.

## Validation and evolution

Tests cover reads, transitions, authorization, repository identity, optimistic state conflict, protocol mismatch, retry idempotency, event deduplication, and unknown outcomes. Compatible operations or fields may be added within 1.x. Breaking envelope, outcome, or idempotency semantics require a new major protocol and migration.
