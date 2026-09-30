---
id: EV-ROS-2026-A066
title: "EX-ROS-2026-A021 control arm, PRAXIS-GROUP-02: session record and stale-clone deviation (names an arm; read only after unblinding)"
status: draft
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-30
updated: 2026-09-30
research_area: repository-operating-system
evidence_type: primary
supports: [EX-ROS-2026-A021]
related_documents:
  - EX-ROS-2026-A021
  - requirements/PLANNING-WORK-GROUPS.md
  - research/experiments/EX-ROS-2026-A021-evaluation-kit/evaluator-prompt.txt
  - research/experiments/EX-ROS-2026-A021-evaluation-kit/prepare-blind-bundle.sh
tags: [planning, grouping, experiment, control-arm, deviation]
confidence: high
provenance:
  contributions:
    EXE-20260930T144206630Z-1e149d71:
      operations: [created]
      at: 2026-09-30T14:42:13.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-A021-EVAL-KIT: blind evaluation kit for EX-ROS-2026-A021"
    EXE-20260930T230548914Z-bb6a60fd:
      operations: [migrated]
      at: 2026-09-30T23:06:18.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PLAN-08: renumbered from EV-ROS-2026-A060..A062 on experiment/a021-evaluation-kit, which collided with main's EV-ROS-2026-A060; content unchanged apart from the IDs"
---

# Control arm, PRAXIS-GROUP-02: session notes (read only after unblinding)

This note names an arm. The evaluator must not read it.

- **Session:** `https://claude.ai/code/session_01UTrWKh68vzLeWn5wdDZAXD`, configured model
  `claude-opus-5-5`; the model that served each turn was not recorded separately.
- **Branch and commits:** `experiment/a021-control`:
  - `60e3f03` is the implementation plus `EX-ROS-2026-A021-control/metrics/control-02.json`;
  - `a47522e` records the checkpoint and completion;
  - telemetry execution `EXE-20260930T121813963Z-b4690a18`.
- **Result:** `work group show GROUP-ID [--as-of TIMESTAMP] [--json]`:
  - new pure `GroupView` domain projection (`src/Ros.Domain/Planning/GroupView.fs`);
  - `WorkGroupJson.renderView` / `renderNotFound`;
  - read-only CLI dispatch without identity resolution;
  - 5 tests;
  - 808/808 tests passed and `./ros validate` passed.
- **Design choice to weigh in the cross-item assessment:** GROUP-02 added its own
  `DeclaredGroupProgress` instead of reusing the planner's `Grouping.GroupProgress`, because:
  - the planner's version counts abandoned members as complete;
  - `plan groups` excludes completed members (PRX-GRP-090 #7), so it cannot show partial
    completion.

  This is a parallel type and may count as a duplicated abstraction.

## Deviation: stale clone at session start

The container's clone of `experiment/a021-control` was older than the remote. The session ran
`./ros work begin` for PRAXIS-GROUP-02 before GROUP-01's commits (`51086ac`, `9349e48`) were
present locally. It noticed only when a later `git fetch` showed them.

- Discarding the uncommitted start state (including a telemetry execution file) was refused by
  the session's permission policy, so the telemetry was kept.
- The start state was stashed, the branch fast-forwarded, and the two conflicting files
  (`.ros/context/current.json` and `.ros/events/events.jsonl`) were reconciled by hand as a union:
  GROUP-01's entries first, then the GROUP-02 start, which is later in time.
- `./ros validate` passed afterwards.
- A local stash (`group02-begin`) was left in that container; deleting it was also refused. It
  was never pushed.

**Effect on metrics:** control-02's wall-clock time (1,246 s at the metrics snapshot), its
55 model requests and its 37 file reads include the diagnosis and recovery. They overstate this
item's context overhead relative to a clean start. The work-item code itself is unaffected.
Other control sessions may or may not have hit the same stale-clone condition; check their
transcripts.

## Setup noted for reproducibility

- `dotnet-sdk-10.0` needed `apt-get update` before it would install.
- Builds used `-p:FSharpCoreImplicitPackageVersion=10.1.400`, which was not committed.
