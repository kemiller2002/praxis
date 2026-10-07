---
id: EV-ROS-2026-A034
title: Production work-state recovery integration results
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
  - EV-ROS-2026-A033
supersedes: []
superseded_by: []
tags: [fsharp, node, work-lifecycle, persistence, recovery, differential-testing, migration]
confidence: high
---

# Result

Production Node work transitions now prepare and recover the same versioned,
hash-preconditioned `work-state` journal as the F# infrastructure shadow while
holding the existing `work-protocol` lease. The bounded unit contains the
event-log replacement followed by the current-context replacement. Recovery is
attempted before every live transition and refuses to overwrite a target whose
content matches neither the recorded before nor after hash.

This closes the previously characterized crash window between the final event
and context writes without transferring semantic authority to F#. Node still
owns the state-changing work command, telemetry effects, event construction,
and context projection. The shared journal is a cross-runtime compatibility
contract, not a second source of truth.

# Verification

- Three new production tests pass: a normal transition leaves no journal, a
  partially applied F#-compatible journal is completed before a new
  transition, and divergent state rejects recovery before either target is
  written.
- A new F# test constructs a Node-shaped JSON journal directly and proves the
  F# recovery implementation accepts and completes it.
- The complete repository gate passes 92 Node, 7 Python, 31 F#, and 10
  Node-driven differential/smoke tests: 140 total with zero failures.
- The successful .NET build reports zero warnings and zero errors.
- The focused production work/telemetry gate passes 52 tests; expected error
  output belongs to asserted rejection paths.

# Boundary and remaining risk

Telemetry starts/finalization happen before the event/context transaction is
prepared. An interruption in that interval can therefore leave telemetry
ahead of live context; the existing backlink/finalization validation detects
that state, but this slice does not make telemetry and work state one atomic
unit. Backlog queue JSON and its Markdown projection are also outside this
transaction. Both need their own bounded recovery analysis rather than reuse
of the live-work journal.

The F# application still does not acquire the production work lease or execute
state-changing work commands. Production authority remains Node.
