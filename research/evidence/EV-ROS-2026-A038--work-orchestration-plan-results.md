---
id: EV-ROS-2026-A038
title: F# work orchestration plan results
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
  - EV-ROS-2026-A033
  - EV-ROS-2026-A037
supersedes: []
superseded_by: []
tags: [fsharp, work, orchestration, state, telemetry, differential-testing]
confidence: high
---

# Result

F# now plans one live-work transition as an application unit. Given a typed
current item, requested action, configured target local state, clock value,
evidence observations, Git paths, repository/protocol identity, and telemetry
capability, it returns either a full typed rejection or:

- the updated item projection;
- the semantic event payload before effect-generated identity/publication;
- ordered telemetry intents for execution recovery, lifecycle recording, and
  finalization.

The planner composes the existing transition decision rather than restating its
matrix. It preserves custom local-state mappings, retains an existing block
reason unless a new block replaces it, replaces evidence only on completion,
sets completion time only on completion, and emits changed paths only for the
completion event.

# Verification

- Four F# tests cover completion projection, ordered telemetry intents,
  rejection without a plan, and configured local-state mapping.
- A Node-driven differential executes all five legal production edges and
  compares normalized item and event projections field-for-field with
  `ros-fs work plan` using the same observed timestamp and Git paths.
- A rejection CLI test proves missing-evidence obligations survive the public
  JSON contract.
- The complete repository gate passes 104 Node, 7 Python, 44 F#, and 14
  Node-driven differential/smoke tests: 169 total with zero failures. The final
  .NET build reports zero warnings and zero errors.

# Boundary

This result supports a pure orchestration plan, not a production state-changing
command. The caller still owns evidence-path I/O, clock/Git observations,
execution and event identity, locks, telemetry effects, context/event rendering,
and the recovery journal. Backlog promotion and multi-item context updates are
not modeled by this sub-slice. Installed ROS remains Node because the consumer
distribution decision is unresolved.
