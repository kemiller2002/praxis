---
id: EV-ROS-2026-A040
title: Typed multi-item work context planning results
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
  - EV-ROS-2026-A039
supersedes: []
superseded_by: []
tags: [fsharp, work, context, planning, state, differential-testing]
confidence: high
---

# Result

F# work orchestration now plans an ordered set of live-context transitions as
one pure outcome. Existing context order is retained; new items are legal only
for `begin` and append in request order; events preserve request order; the
first begin captures started-at and baseline paths; and any empty selection,
invalid ID, missing item, or rejected item transition rejects the whole plan.

The shadow `work context-plan` command reads an explicit context snapshot
through a versioned decoder and emits a normalized planning view. It does not
write context, events, or telemetry and is not a lossless persistence codec.

# Verification

- Five new typed tests cover order, append behavior, first-begin baseline,
  later-item atomic rejection, absent-item policy, empty/invalid selections,
  and unknown semantic-state rejection.
- Two new production differentials match multi-item begin projections and a
  later illegal transition that leaves the production context file unchanged.
- The focused gate passes 51 F# tests and all 8 work differentials. After the
  canonical registries were rebuilt, the complete gate passed 104 Node, 7
  Python, 51 F#, and 17 differential/smoke tests: 179 total with zero failures
  and a zero-warning, zero-error F# build.

# Boundary discovery

Production writes context/events only after all selected items pass, but its
telemetry calls occur inside the item loop. A later rejection may therefore
leave telemetry created for an earlier item even though context/events remain
unchanged. Existing detached-execution recovery constrains that failure shape,
but it is not full transactionality. A future state-changing F# handler must
freeze a complete plan before executing telemetry intents.

Node remains the production writer. Backlog promotion, effect execution,
evidence-containment policy, and the consumer distribution decision remain
open.
