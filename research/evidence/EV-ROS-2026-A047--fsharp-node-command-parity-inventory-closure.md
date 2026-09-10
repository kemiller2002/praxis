---
id: EV-ROS-2026-A047
title: F# shadow CLI versus production ./ros command-surface parity inventory (closure)
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-10
updated: 2026-09-10
research_area: repository-operating-system
evidence_type: primary
supports:
  - DF-ROS-2026-A028
related_documents:
  - DF-ROS-2026-A027
  - EV-ROS-2026-A043
  - EV-ROS-2026-A045
  - EV-ROS-2026-A046
  - docs/migrations/fsharp/ARCHITECTURE.md
  - docs/migrations/fsharp/ROADMAP.md
  - docs/migrations/fsharp/STATUS.md
  - docs/migrations/fsharp/TRACEABILITY.md
supersedes:
  - EV-ROS-2026-A046
superseded_by: []
tags: [fsharp, migration, parity, cli, distribution, sde]
confidence: high
---

# Evidence summary

`EV-ROS-2026-A046` found 21 of the same 26 command-surface rows at "Full
parity," with the remaining 5 split between 4 pure read-only views
(`work`/`work list`, `work show`, `work context`, `status`) and `validate`'s
own single-command unification (plus a still-unported `telemetryFindings`
contributor). Since that record, WI-0040 (`work context`), WI-0041
(`work`/`work list`/`work show`), WI-0042 (`telemetryFindings`), WI-0043
(unified `validate`), and WI-0044 (`status`) have all shipped and merged
(PRs #34–#37). This record re-runs the same 26-row inventory a third time to
confirm what closing them changed.

**Headline finding: all 26 rows now read "Full parity." `DF-ROS-2026-A028`
Phase A's own acceptance criterion — "every row in a re-run of
`EV-ROS-2026-A043`'s inventory reads 'full parity'" — is met for the first
time.** This closes Phase A. It does not authorize Phase C: Phase B
(consumer distribution evidence) remains entirely uncollected, and Phase C
requires both phases accepted plus its own new decision record carrying a
rollback plan and cutover mechanism, none of which this record supplies.

# Method

Identical to `EV-ROS-2026-A043`/`EV-ROS-2026-A045`/`EV-ROS-2026-A046`: both
CLI entry points read in full — Node's `tools/ros_cli.mjs` `main` dispatch
and F#'s `src/Ros.Cli/Program.fs` `dispatch` match expression — using the
same three-way classification (No F# equivalent / Read-only diagnostic only
/ Full parity), against the same 26 rows at the same granularity so counts
remain directly comparable across all four records in this series.

Collected on branch `claude/node-to-fsharp-conversion-h9nyzn` at commit
`e54f191` (merged to `main`; PR #37). Runtimes: Node `v22.22.2`; .NET SDK
`10.0.111`.

# Change since `EV-ROS-2026-A046`

- **`work` / `work list` (no ids):** No F# equivalent -> **Full parity**.
  `ros-fs work` / `work list` (`FileWorkListRepository.readListView`) ports
  production's `mergedWorkView` at full fidelity; differential-tested.
- **`work show ID`:** No F# equivalent -> **Full parity**. `ros-fs work show
  ID` (`FileWorkListRepository.readShowView`) ports production's `showWork`;
  differential-tested.
- **`work context [ID]`:** No F# equivalent -> **Full parity**. `ros-fs work
  context` (`FileWorkContextRepository.readContextView`); differential-tested.
- **`validate [--json]`:** Read-only diagnostic only -> **Full parity**.
  `ros-fs validate` (`Ros.Cli.Program.runValidateUnified`) now composes all
  five of production's own contributors (artifact findings, registry
  staleness, `workFindings`, `queueFindings`, `telemetryFindings`) into one
  command, matching production's exact sorted output in both `--json` and
  text form; differential-tested.
- **`status`:** No F# equivalent -> **Full parity**. `ros-fs status`
  (`Ros.Cli.Program.runStatus`) composes `contextView`, the unified
  `validate` findings, and `showTelemetry`'s execution read; differential-tested.

No other row changed; the 21 already reading "Full parity" in
`EV-ROS-2026-A046` are unaffected.

# Command inventory

All 26 rows previously enumerated in `EV-ROS-2026-A046` now read **Full
parity**, each backed by an existing Node/F# differential test. The row set,
granularity, and per-row Node/F# command mapping are unchanged from that
record; only the classification column changed, as described above. See
`EV-ROS-2026-A046` for the full per-row table; it is not reproduced here to
avoid drift between two copies of the same inventory.

The one non-counted extra row noted in prior inventories is also unchanged:
`work decide`, `work plan`, `work context-plan`, `work backlog-decide`, and
`work backlog-promotion-plan` remain useful read-only decision diagnostics
with no Node counterpart at all.

# What this closes, and what it does not

This record closes `DF-ROS-2026-A028` Phase A. It does **not**:

1. Collect any Phase B distribution evidence (installation story, startup
   time, binary size, offline/update behavior, integrity verification,
   rollback, across macOS/Linux/Windows) — still entirely uncollected.
2. Produce a rollback plan or cutover mechanism for an actual redirect —
   still not started.
3. Authorize any change to `./ros`'s dispatch, in this repository or in
   `starter/greenfield/ros`. `DF-ROS-2026-A028` is explicit that Phase C
   requires both Phase A and Phase B accepted, plus its own new decision
   record; this record supplies only the Phase A half.

Given Phase A's closure, the practical, currently-authorized use of the
F# shadow CLI in this repository is as a verification/cross-check tool for
its own already-ported read-only commands (`status`, `validate`, `telemetry
validate`, `work`/`work show`/`work context`, `artifacts validate`,
`registry check`, `git status`) — never as a substitute authority for
`./ros` itself, and never for state-mutating operations, which remain
Node's alone until a Phase C decision exists. `AGENTS.md` is updated
alongside this record to state that explicitly for agents working in this
repository.

# Limitations

Same as the prior three records in this series: a command-surface census,
not a line-by-line semantic diff of every flag/option; per-command
differential tests remain the source of truth for behavioral equivalence.
Does not re-examine HTTP (`tools/ros_server.mjs`), the hub
(`tools/ros_hub_cli.mjs`), or bootstrap/init — unchanged, still out of
scope.

# Reproduction

```bash
grep -n 'args\[0\] ===' tools/ros_cli.mjs
grep -n '| "work" ::\|| \[ "\|"artifacts"\|"registry"\|"git"\|"telemetry"\|"adapter"' src/Ros.Cli/Program.fs
```
