---
id: EV-ROS-2026-A036
title: Telemetry execution-link recovery results
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-08
updated: 2026-09-08
research_area: repository-operating-system
evidence_type: primary
related_documents:
  - DF-ROS-2026-A010
  - DF-ROS-2026-A027
  - JR-ROS-2026-A019
  - EV-ROS-2026-A035
supersedes: []
superseded_by: []
tags: [fsharp, node, telemetry, persistence, recovery, idempotency, migration]
confidence: high
---

# Result

Telemetry persistence is now modeled according to its actual stores rather
than forced into another two-file transaction. Each execution JSON record was
already an atomically replaced source of truth guarded by an execution-specific
lease. The unsafe seam was creation of an execution record followed by a
separate direct context-backlink write.

Production now composes telemetry creation and backlinking while holding
`work-protocol`; backlink changes are committed through the existing
event/context recovery journal. If interruption leaves exactly one eligible
execution detached, a retry adopts that record instead of creating duplicate
evidence. Multiple candidates reject automatic guessing and can be repaired by
rerunning with the exact existing `--execution-id`.

F# now owns the pure execution-link decision: start new, recover one, reject a
requested-ID conflict, or reject ambiguity. The telemetry core rejects any
attempt to perform its former uncomposed context attachment before it writes an
execution file.

# Verification

- Six typed F# tests cover start, active/finalized recovery, linked/other-work
  exclusion, requested-ID selection/conflict, and deterministic ambiguous-ID
  ordering.
- Four production tests cover direct-start recovery, work-begin retry recovery,
  ambiguity plus explicit selection, and proof that the uncomposed attachment
  guard writes no execution file.
- The focused gates pass 40 F# and 60 Node work/telemetry tests with zero
  failures. The successful .NET build reports zero warnings and zero errors.
- The complete repository gate passes 100 Node, 7 Python, 40 F#, and 10
  differential/smoke tests: 157 total with zero failures.

# Completion boundary

Together with `EV-ROS-2026-A030`, A033–A035, this completes MIG-05's bounded
persistence/recovery work for generated registries, live work, backlog, and
telemetry linkage. It does not make multiple semantic stores globally atomic,
introduce a generic transaction framework, or move production commands to F#.

An unkeyed `telemetry start` whose process loses its response after every
durable write and journal cleanup still has an externally unknown outcome. A
caller needing retry identity must supply `--execution-id`; otherwise it must
inspect current links before intentionally starting another execution. This is
a command-idempotency constraint, not hidden as success.
