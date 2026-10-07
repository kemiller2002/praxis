---
id: EV-ROS-2026-A035
title: Backlog state recovery and concurrency results
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
  - EV-ROS-2026-A034
supersedes: []
superseded_by: []
tags: [fsharp, node, backlog, persistence, recovery, concurrency, migration]
confidence: high
---

# Result

The captured-backlog store now has a bounded `backlog-state` recovery contract
covering authoritative `.ros/work/queue.json` followed by its human-readable
`.ros/work/queue.md` projection. Node and F# use the same versioned JSON shape,
ordered targets, full replacement content, and before/after SHA-256
preconditions.

Every production backlog read/modify/write operation now executes while holding
the existing `work-protocol` lease. Live transitions and backlog changes share
that capability, eliminating the previously unguarded queue update race.
Attachments retain their characterized ordering: file content is stored before
the queue gains its reference, so interruption may leave an orphan but cannot
make the queue point at a file that was never written.

# Verification

- Three typed F# tests pass: partial queue/projection replay, whole-set
  divergence preflight, and recovery of a manually serialized Node-shaped
  journal.
- Four production tests pass: normal journal cleanup, recovery of a partially
  applied F#-compatible record, rejection before writes on projection
  divergence, and eight concurrent process captures with every queue item
  retained.
- The focused gates pass 34 F# tests and 32 production work tests with zero
  failures. Expected error output belongs to asserted rejection cases.
- After rebuilding the two stale generated registries, the complete repository
  gate passes 96 Node, 7 Python, 34 F#, and 10 differential/smoke tests: 147
  total with zero failures.
- The successful .NET build reports zero warnings and zero errors.

# Boundary

The journal does not include live context, work events, attachments, or
execution telemetry. `queue.md` remains a derived projection, not a second
authority. Live-work transitions do not regenerate it; its documented contract
remains regeneration on backlog changes. Cross-store atomicity is intentionally
not introduced.

Production command semantics remain Node-owned. F# owns a compatible typed
recovery implementation but does not yet execute backlog commands.
