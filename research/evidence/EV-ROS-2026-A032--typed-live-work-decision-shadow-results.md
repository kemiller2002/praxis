---
id: EV-ROS-2026-A032
title: Typed live-work decision shadow results
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
  - EV-ROS-2026-A031
supersedes: []
superseded_by: []
tags: [fsharp, work-lifecycle, state-machine, evidence, differential-testing, migration]
confidence: high
---

# Result

The F# shadow now owns a pure decision model for the characterized live-work
lifecycle. `Ready`, `Active`, `Blocked`, and `Complete` are closed states;
`Begin`, `Block`, `Resume`, and `Complete` are closed requests. Exactly five
edges are legal. Illegal transitions, absent block reasons, and missing
configured evidence types are distinct typed rejections.

The model deliberately makes no external effects. `ros-fs work decide` exposes
an explicit JSON diagnostic contract and exit status, but does not write work
state or assert that evidence paths exist.

# Compatibility evidence

- Four focused work-domain tests pass, including exhaustive rejection and both
  positive and negative obligation paths.
- A Node-driven differential invokes the real production `transition` function
  and the F# CLI for all 16 state/action pairs; allowed/rejected outcomes and
  allowed target states agree.
- Separate differentials agree on missing evidence and on the legacy distinction
  between an empty block reason (rejected) and whitespace (accepted).
- The complete F# gate currently passes 25 typed tests and 10 Node-driven
  differential/smoke tests with zero failures.
- The complete repository gate passes 89 Node, 7 Python, 25 F#, and 10
  Node-driven differential/smoke tests (131 total, zero failures).
- SDE v1.1.1 integrity verification passes; its five pre-existing structural
  review warnings remain visible.

# Boundaries

Node remains authoritative for backlog state, configured state mapping,
evidence-path existence, Git attribution, clocks, events, telemetry, locks,
context/queue persistence, and every state-changing command. MIG-07 remains in
progress until those responsibilities migrate in safe vertical slices after
general work-state recovery is established under MIG-05.
