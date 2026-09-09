---
id: EV-ROS-2026-A041
title: Typed backlog transition and promotion results
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-09
updated: 2026-09-09
research_area: repository-operating-system
evidence_type: primary
related_documents:
  - DF-ROS-2026-A006
  - DF-ROS-2026-A008
  - DF-ROS-2026-A027
  - JR-ROS-2026-A019
  - EV-ROS-2026-A040
supersedes: []
superseded_by: []
tags: [fsharp, work, backlog, promotion, state, differential-testing]
confidence: high
---

# Result

F# now owns a pure typed model for the repository-local backlog lifecycle:
captured, ready, blocked, and abandoned, with seven characterized legal edges.
Queue state changes are distinct from `Start`, which emits a live-work
promotion plan and deliberately leaves the queue record ready.

Optional reason fields use explicit `Keep`, `Clear`, and `Set` operations.
This is necessary because production clears block reason when returning to
ready, sets it when blocking, but preserves it when abandoning a blocked item.
Batch promotion rejects any local queue item that is not ready, while allowing
an ID absent from the queue to preserve direct external-authority work.

# Verification

- Four typed tests cover the full transition matrix, exact effects, empty
  versus whitespace block reasons, and complete batch preflight.
- A production differential executes every one of the 16 state/action pairs
  and compares allowed/rejected outcomes and projected queue fields.
- A second differential proves batch rejection and direct-ID allowance, and
  confirms successful start does not change the queue record from ready.
- The focused gate passes 55 F# tests and all 10 work differentials. After the
  registry build, the complete gate passed 104 Node, 7 Python, 55 F#, and 19
  differential/smoke tests: 185 total with zero failures and a warning-free
  F# build.

# Repair evidence

The first differential found that the initial model cleared a prior block
reason on abandonment. Production preserves it. Replacing ambiguous optional
final values with explicit field operations corrected the model, and the same
full matrix then passed. This is a semantic defect detected by old/new
comparison rather than compilation.

No queue, context, event, or telemetry writer moved to F#. The existing Node
commands and bounded queue/context recovery units remain the production path.
