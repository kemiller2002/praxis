---
id: EV-ROS-2026-A031
title: Typed Git provenance shadow results
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-08
updated: 2026-09-08
research_area: repository-operating-system
evidence_type: primary
related_documents:
  - DF-ROS-2026-A027
  - JR-ROS-2026-A019
  - EV-ROS-2026-A030
supersedes: []
superseded_by: []
tags: [fsharp, git, provenance, boundary, differential-testing, migration]
confidence: high
---

# Result

The F# shadow now observes Git status through a typed boundary whose outcomes
are `Clean`, `Changed`, or `Unavailable`. A missing Git executable, a directory
outside a repository, a failed command, or malformed porcelain output cannot
be represented as a clean working tree. Tracked changes preserve independent
index and work-tree deltas; rename and copy entries preserve both destination
and original paths.

The explicit versioned JSON contract reports the semantic outcome, changes,
failure reason/exit code, and command provenance. `ros-fs git status --json`
returns exit 0 for observed clean/changed state and exit 1 for unavailable.

# Characterized legacy boundary

`tools/ros_cli.mjs` and `tools/ros_telemetry.mjs` each invoke
`git status --porcelain=v1 -z`. Both catch command failure and return an empty
path array. The work caller uses that array for baseline attribution,
completion event paths, and enforcement; telemetry uses its array for dirty
state and metrics. Those production callers were not changed in this sub-slice
because each requires separate transition/telemetry compatibility evidence.

# Verification

- The .NET solution builds with zero warnings and zero errors.
- 21 F# architecture/unit/integration tests pass, including parser rejection,
  real clean/changed repositories, non-repository state, and missing Git.
- Three Node-driven Git differential tests pass for clean state, changed paths
  and statuses with rename origin, and unavailable non-repository state.
- A live repository CLI smoke test returned the exact current changed set.
- The complete repository gate passes: 89 Node, 7 Python, 21 F#, and 7
  Node-driven F# differential/smoke tests (124 total, zero failures).
- SDE v1.1.1 integrity verification passes for all 18 managed files. Its five
  structural review warnings are pre-existing large-file signals and were not
  suppressed.

# Boundary and limitation

This evidence supports the read-only shadow seam, not production replacement.
`./ros` remains Node-owned, the two production Git helpers remain duplicated,
and no work or telemetry state transition consumes the F# result yet. MIG-06
therefore remains in progress.
