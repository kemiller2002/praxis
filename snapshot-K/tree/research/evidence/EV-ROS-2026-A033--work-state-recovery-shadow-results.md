---
id: EV-ROS-2026-A033
title: Work-state recovery shadow results
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-08
updated: 2026-09-08
research_area: repository-operating-system
evidence_type: primary
related_documents:
  - DF-ROS-2026-A006
  - DF-ROS-2026-A027
  - JR-ROS-2026-A019
  - EV-ROS-2026-A032
supersedes: []
superseded_by: []
tags: [fsharp, work-lifecycle, persistence, recovery, concurrency, migration]
confidence: medium
---

# Result

The F# infrastructure shadow now defines a versioned recovery journal for the
two final live-work transition projections: the immutable event log followed by
the current-context projection. Each write carries its full intended content
and SHA-256 hashes of both the observed before state and intended after state.

Recovery preflights the entire write set. A target may equal only its recorded
before hash or after hash. This supports safe retry after partial application
without overwriting a later third-party change. Conflict, corrupt structure,
wrong/reordered targets, and an unrecovered existing journal are explicit
failed or indeterminate outcomes rather than implicit success.

# Verification

- Five focused recovery tests pass: partial event-then-context replay,
  divergence preflight with no writes, incomplete/reordered write-set rejection,
  pending-journal preservation, and corrupt-journal preservation.
- All 30 current F# tests pass.
- The successful .NET build reports zero warnings and zero errors.
- The complete repository gate passes 89 Node, 7 Python, 30 F#, and 10
  Node-driven differential/smoke tests (136 total, zero failures).
- SDE v1.1.1 integrity and ROS validation pass; five pre-existing structural
  review warnings remain visible.

# Boundary and remaining proof

This is a shadow contract, not a production writer. Correct use requires the
caller to hold the `work-protocol` lease for preparation, application,
completion, and recovery. That capability is not yet composed in F#. Node does
not yet write or recover this journal. Telemetry execution records have their
own locks/bidirectional validation, and backlog queue/Markdown writes form a
different recovery unit. MIG-05 remains in progress pending those integrations
and comparative production evidence.
