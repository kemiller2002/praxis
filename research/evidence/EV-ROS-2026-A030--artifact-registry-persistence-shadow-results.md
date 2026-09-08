---
id: EV-ROS-2026-A030
title: Artifact registry cross-runtime lease and recovery results
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-08
updated: 2026-09-08
research_area: repository-operating-system
evidence_type: primary
related_documents:
  - EV-ROS-2026-A028
  - DF-ROS-2026-A027
  - JR-ROS-2026-A019
supersedes: []
superseded_by: []
tags: [fsharp, persistence, recovery, transaction, lock, migration]
confidence: medium
---

# Result

The artifact-registry projection now coordinates Node and F# writers through
the same `artifact-registries` lease resource and the same versioned pending
write-record shape. Tests establish SHA-256 lock-path compatibility, dead-owner
recovery, live-owner rejection, release ownership-change indeterminacy,
Node/F# stale-lease recovery, and Node/F# replay of the other runtime's pending
transaction. Registry bytes remain covered by the prior golden differential.

The transaction record is restricted to the eight configured generated registry
paths. Its replay is idempotent because it stores complete replacement content
and canonical Markdown remains authoritative. Invalid transaction records stop
as indeterminate; they are never treated as paths to write.

# Boundaries

This is an artifact-projection persistence sub-slice, not a completed generic
transaction system. It supplies neither version/concurrency policy for canonical
Markdown nor recovery semantics for work context, queue, events, telemetry,
adapter stores, or hub state. Those writers remain Node-owned and require their
own characterized follow-up before migration.

# Verification

- 16 F# architecture/unit tests passed.
- 4 Node-driven F# differential/smoke tests passed.
- 89 Node and 7 Python tests passed.
- Successful .NET build reported 0 warnings and 0 errors.
