---
id: EV-ROS-2026-A037
title: Production Git consumer consolidation results
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
  - DF-ROS-2026-A010
  - DF-ROS-2026-A027
  - JR-ROS-2026-A019
  - EV-ROS-2026-A031
supersedes: []
superseded_by: []
tags: [fsharp, node, git, provenance, telemetry, work, migration]
confidence: high
---

# Result

The two production Git-status implementations were replaced by one installed
process adapter, `tools/ros_git.mjs`. Its versioned observation is exactly
compatible with the F# `Clean`, `Changed`, and `Unavailable` contract, including
status code, independent index/work-tree deltas, rename/copy origin and
destination, failure reason and exit code, and command provenance.

Work attribution now consumes normalized destination paths for rename/copy
records. A non-repository directory remains an explicit compatibility case for
greenfield installation before `git init`; other unavailable status outcomes
are errors. Completion observes Git before telemetry finalization, so failure
leaves the execution active and retryable. Validation preserves the same typed
failure as a structured `.git` finding.

Telemetry consumes the same observation. If ending Git state is unavailable,
it records `git.ending_dirty_files` as `supported-unavailable` and does not emit
a zero metric. If any required diff command is unavailable, the change summary
is unavailable rather than a collection of zero counts.

# Verification

- Exact Node/F# JSON equality passes for clean, changed-with-rename, and
  non-repository observations.
- Missing executable and malformed porcelain rejection tests pass.
- Production work tests prove rename destination attribution and unavailable
  Git rejection before finalization.
- Production telemetry tests prove aggregate/path rename provenance and the
  unavailable-not-zero ending-state contract.
- Greenfield and Project Administration installation tests pass with the new
  adapter present in both profiles and the npm package.
- The complete gate passes 104 Node, 7 Python, 40 F#, and 12 Node-driven
  differential/smoke tests: 163 total, zero failures. The .NET build reports
  zero warnings and zero errors.

# Boundary

MIG-06 is complete as a semantic/effect consolidation slice, not as a runtime
distribution switch. The installed adapter remains Node because invoking the
framework-dependent F# binary would impose an unapproved .NET 10 dependency on
consumers. F# owns the typed contract and infrastructure model; differential
verification constrains the temporary Node-compatible implementation until the
MIG-01 distribution experiment authorizes a production switch.

Git itself remains the mature platform capability. ROS adds typed observation
and caller policy; it does not reimplement repository behavior.
