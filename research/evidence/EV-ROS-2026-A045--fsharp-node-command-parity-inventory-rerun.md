---
id: EV-ROS-2026-A045
title: F# shadow CLI versus production ./ros command-surface parity inventory (re-run)
status: superseded
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
  - EV-ROS-2026-A044
  - EV-ROS-2026-A046
  - docs/migrations/fsharp/ARCHITECTURE.md
  - docs/migrations/fsharp/ROADMAP.md
  - docs/migrations/fsharp/STATUS.md
  - docs/migrations/fsharp/TRACEABILITY.md
supersedes:
  - EV-ROS-2026-A043
superseded_by:
  - EV-ROS-2026-A046
tags: [fsharp, migration, parity, cli, distribution, sde]
confidence: high
---

# Evidence summary

`EV-ROS-2026-A043` inventoried the command surface before Phase A's
work-lifecycle and MIG-08 increments shipped, and its own "what a redirect
needs" section named this exact re-run as a future step once Phase 1 was
"materially further along." Ten real-effect increments have since landed
(`work backlog-transition`, `work capture`, `work update`, `work attach`,
`work start`, `work resume`, `work block`, `work complete`, `telemetry
adapters`/`telemetry show`, and telemetry lifecycle bookkeeping). This
record re-collects the same command-by-command inventory, using the same
method, to measure exactly how far that closed the gap.

**Headline finding: the entire backlog-only and live-work command surface
now reads "Full parity," plus two telemetry read commands — but the
inventory's own acceptance bar ("every row reading full parity") is still
not met.** Thirteen of the same twenty-six rows `EV-ROS-2026-A043`
inventoried now read Full parity (up from three). Thirteen remain "No F#
equivalent" (twelve rows) or "Read-only diagnostic only" (one row, `validate`):
every write-path telemetry-producer command, both adapter commands, three
read-only views (`work`/`work list`, `work show`, `work context`),
`status`, and Node's own unified `validate` (which aggregates checks F#
now exposes as three separate commands rather than one matching surface).
Distribution evidence (`EV-ROS-2026-A043`'s items 3-4) remains entirely
uncollected regardless.

# Method

Identical to `EV-ROS-2026-A043`: both CLI entry points read in full, not
sampled — Node's `tools/ros_cli.mjs` `main`/dispatch function (every
`args[0] === "..."` branch, including nested `args[1]` checks) and F#'s
`src/Ros.Cli/Program.fs` `dispatch` match expression. The same three-way
classification is used, unchanged:

- **No F# equivalent** — no command with matching semantics exists in
  `ros-fs` at all.
- **Read-only diagnostic only** — an F# command exists with a related name
  and decides/plans the same semantic question, but performs no filesystem
  write (or, for `validate`, performs a write-free but narrower-scoped
  check than Node's single command aggregates).
- **Full parity** — the F# command performs the same real, persisted effect
  Node's command performs, verified by an existing Node/F# differential
  test.

Collected against commit `d446018cde413f456ff9ef11ff3a1d5fb4b7cf43` (`main`,
after PR #16 squash-merged; branch `claude/node-to-fsharp-conversion-h9nyzn`
matches it exactly). Runtimes: Node `v22.22.2`; .NET SDK `10.0.111`.

# Command inventory

| Node command (`./ros ...`) | Durable effect | F# command (`ros-fs ...`) | Classification |
|---|---|---|---|
| `add TITLE ...` | writes `.ros/work/queue.json`/`.md` | `work capture --title ... --occurred-at ...` | **Full parity** (increment 2; differential-tested) |
| `work` / `work list` (no ids) | read-only (queue + context merged view) | none | No F# equivalent |
| `work ready [ID]` (no ids: read view; with ids: mutate) | writes queue on mutate form | `work backlog-transition --action ready --id ID --occurred-at ...` (mutate form only) | **Full parity** for the mutate form (increment 1); the no-id read view remains No F# equivalent |
| `work show ID` | read-only | none | No F# equivalent |
| `work start ID` | promotes queue -> live context, begins telemetry | `work start --id ID --occurred-at ...` | **Full parity** (increment 5; differential-tested) |
| `work update ID ...` | writes queue | `work update --id ID --occurred-at ...` | **Full parity** (increment 3; differential-tested) |
| `work attach ID ...` | writes queue + attachment bytes | `work attach --id ID --occurred-at ... --file PATH` | **Full parity** (increment 4; differential-tested) |
| `work abandon ID --reason` | writes queue | `work backlog-transition --action abandon --id ID --occurred-at ...` | **Full parity** (increment 1; differential-tested) |
| `work block ID --reason` | writes context/queue + events + telemetry lifecycle | `work block --id ID --occurred-at ... --reason ...` | **Full parity** (increment 7, plus lifecycle bookkeeping in MIG-08 increment 2; differential-tested) |
| `work begin/resume/complete/done ID ...` | writes context + events + telemetry executions | `work start` (begin), `work resume`, `work complete` | **Full parity** for `begin`/`resume`/`complete` (increments 5/6/8, plus lifecycle bookkeeping for `resume`/`block` in MIG-08 increment 2; all differential-tested). Node's `done` is a pure `ACTION_ALIASES` spelling of the identical `complete` effect; this CLI does not additionally wire it as a second verb spelling (a naming gap, not a behavioral one) |
| `work context [ID]` | read-only | none (context is a CLI input to `work context-plan`, not something F# reads on its own outside the telemetry-candidate port) | No F# equivalent |
| `telemetry start ID ...` | writes an execution record | none | No F# equivalent |
| `telemetry ingest ...` | writes an execution record | none | No F# equivalent |
| `telemetry classify ...` | writes an execution record | none | No F# equivalent |
| `telemetry record ...` | writes an execution record | none | No F# equivalent |
| `telemetry finalize ID` | writes an execution record (including the only `--input`/adapter-ingestion path) | none (`work complete` calls the same underlying finalize effect internally, but no CLI command exposes it directly against an arbitrary target) | No F# equivalent |
| `telemetry show [ID]` | read-only | `telemetry show [TARGET]` | **Full parity** (MIG-08 increment 1; differential-tested against production's own exported `showTelemetry`) |
| `telemetry summary/summarize [ID]` | read-only (aggregation) | none | No F# equivalent |
| `telemetry adapters ...` | read-only | `telemetry adapters` | **Full parity** (MIG-08 increment 1; differential-tested against production's own exported `TELEMETRY_ADAPTERS`) |
| `adapter call ...` | invokes a configured file adapter | none | No F# equivalent |
| `adapter publish ...` | writes `.ros/publications.json` | none | No F# equivalent |
| `validate [--json]` | read-only (aggregates artifact + work-attribution + backlog-queue + telemetry findings in one command) | `artifacts validate [--json]`, `work validate [--json]`, `work backlog-validate [--json]` (three separate commands, no telemetry-findings equivalent at all) | Read-only diagnostic only — the constituent checks have more F# coverage than at the last inventory (work-attribution and backlog-queue validation now have dedicated, differential-tested F# commands), but no single F# command unifies them into one `validate` surface the way Node's does, and `telemetryFindings` has no F# port at all |
| `status` | read-only | none | No F# equivalent |
| `registry build [--dry-run]` | writes registry JSON | `registry build [--dry-run]` | **Full parity** (unchanged since MIG-04/05) |
| `registry check` | read-only | `registry check` | **Full parity** (unchanged) |
| *(no direct Node command)* | — | `git status [--json]` | Full parity as a standalone observation (unchanged since MIG-06) |

The 26 rows above match `EV-ROS-2026-A043`'s own row count and granularity
exactly (including keeping `begin`/`resume`/`complete`/`done` as the single
combined row production's own dispatch treats as one branch), so the counts
below are directly comparable to that record's. One additional row exists
in the F# CLI with no Node counterpart at all, not part of that count:
`work decide`, `work plan`, `work context-plan`, `work backlog-decide`, and
`work backlog-promotion-plan` are read-only decision diagnostics that
remain useful for exercising the pure decision layer directly, but are no
longer the primary path to any real effect now that increments 1-8 exist —
kept, not superseded, since nothing requires removing them.

# Change since `EV-ROS-2026-A043`

- **Rows newly reading Full parity (10 of the 26, up from 3):** `add`,
  `work ready` (mutate form), `work start`, `work update`, `work attach`,
  `work abandon`, `work block`, `work begin/resume/complete/done`,
  `telemetry show`, `telemetry adapters`.
- **Rows unchanged (No F# equivalent, 12):** every write-path
  telemetry-producer command (`start`/`ingest`/`classify`/`record`/
  `finalize`), `telemetry summary`, both `adapter` commands, `work`/`work
  list`, `work show`, `work context`, `status`.
- **Rows unchanged (Read-only diagnostic only, 1):** `validate` -- though its
  constituent coverage grew (work-attribution and backlog-queue validation
  are now real, differential-tested F# diagnostics; they were not called
  out as separate rows in `EV-ROS-2026-A043` since it inventoried
  commands, not validate's internal contributors).

# What a redirect still needs that does not exist yet

Unchanged from `EV-ROS-2026-A043`, narrowed to what remains after this
round:

1. **State-changing effect handlers in F#** for the thirteen remaining
   "No F# equivalent"/"Read-only diagnostic only" rows -- almost entirely
   telemetry-producer and adapter commands now (MIG-08's own remaining
   scope), plus the handful of pure read-only views (`work list`, `work
   show`, `work context`, `status`) that were never part of MIG-07/MIG-08
   and have no named slice at all yet.
2. **`telemetry ingest`/`classify`/`record`/`finalize`/`summary` and both
   `adapter ...` commands** -- MIG-08's own remaining scope, per
   `EV-ROS-2026-A044`.
3. **Consumer distribution evidence** per `DF-ROS-2026-A027`'s own stated
   blocker -- entirely uncollected; nothing in Phase 1's progress touches
   this.
4. **A rollback plan** for the redirect itself -- unchanged, not started.

This record's own acceptance bar for Phase 1 ("every row reading full
parity") is **not yet met**: thirteen of twenty-six rows do, thirteen do
not.

# Limitations

- Same as `EV-ROS-2026-A043`: a command-surface census, not a
  line-by-line semantic diff of every flag/option; per-command
  differential tests remain the source of truth for behavioral
  equivalence. `docs/migrations/fsharp/STATUS.md` is the authoritative,
  currently-maintained count (104 Node, 7 Python, 206 F# unit, and 91
  differential/smoke tests as of this record) rather than a figure
  restated here that would drift out of date on the next slice.
- Does not re-examine HTTP (`tools/ros_server.mjs`), the hub
  (`tools/ros_hub_cli.mjs`), or bootstrap/init -- unchanged, still out of
  scope.

# Reproduction

```bash
grep -n 'args\[0\] ===' tools/ros_cli.mjs
grep -n '| "work" ::\|| \[ "\|"artifacts"\|"registry"\|"git"\|"telemetry"' src/Ros.Cli/Program.fs
```
