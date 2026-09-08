---
id: EV-ROS-2026-A039
title: Typed work evidence observation results
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
  - EV-ROS-2026-A038
supersedes: []
superseded_by: []
tags: [fsharp, work, evidence, filesystem, boundary, differential-testing]
confidence: high
---

# Result

F# work planning now composes with a typed evidence-observation port after the
pure transition/type/evidence-kind decision succeeds. Each supplied reference
is observed as present, missing, or filesystem-unavailable. Rejection preserves
every issue in request order and keeps missing distinct from unavailable.

The filesystem adapter resolves paths from the repository root and uses the
platform attribute operation, accepting both files and directories like the
production `fs.existsSync` guard. A shadow `--verify-evidence` option composes
the adapter without adding filesystem knowledge to the domain planner.

# Verification

- Typed tests prove ordered mixed missing/unavailable issues and real
  file/directory/missing/malformed-path observations. Malformed paths become a
  typed unavailable result rather than escaping the boundary as exceptions.
- A production differential matches file, directory, absolute existing path,
  and missing-path completion behavior.
- The complete gate passes 104 Node, 7 Python, 46 F#, and 15 Node-driven
  differential/smoke tests: 172 total with zero failures. The final F# build
  reports zero warnings and zero errors.

# Boundary and open question

Production currently accepts an existing absolute evidence path outside the
repository. No canonical authority discovered in this slice requires evidence
containment, so the shadow preserves that behavior instead of guessing. Whether
completion evidence must be repository-contained requires an explicit policy
decision before a production switch.

This slice does not mutate work state or move production authority. Whole-
context orchestration, backlog promotion, telemetry effects, and distribution
remain open.
