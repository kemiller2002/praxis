---
id: EV-ROS-2026-A046
title: F# shadow CLI versus production ./ros command-surface parity inventory (second re-run)
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
  - EV-ROS-2026-A044
  - EV-ROS-2026-A045
  - docs/migrations/fsharp/ARCHITECTURE.md
  - docs/migrations/fsharp/ROADMAP.md
  - docs/migrations/fsharp/STATUS.md
  - docs/migrations/fsharp/TRACEABILITY.md
supersedes:
  - EV-ROS-2026-A045
superseded_by: []
tags: [fsharp, migration, parity, cli, distribution, sde]
confidence: high
---

# Evidence summary

`EV-ROS-2026-A045` re-ran `EV-ROS-2026-A043`'s command-surface inventory
after Phase A's backlog/live-work increments and MIG-08's first two
telemetry-producer increments landed, finding 13 of 26 rows at "Full
parity." Its own "what a redirect still needs" section named the
remaining MIG-08 write-path/adapter commands as the next thing to close,
per `EV-ROS-2026-A044`'s scope. All of MIG-08's own remaining increments
have since shipped: `telemetry summary`, `telemetry finalize`, `telemetry
record`, `telemetry ingest` (every adapter name `TelemetryAdapters.all`
recognizes), `telemetry classify`, `telemetry start` (including its own
`--execution-id`/identity-override flags), `adapter call`, and `adapter
publish`. This record re-collects the same 26-row inventory, using the
same method, to measure exactly how far that closed the gap and to state
precisely what remains.

**Headline finding: MIG-08's entire own scope is now real. 21 of the same
26 rows read "Full parity" (up from 13), 1 reads "Read-only diagnostic
only" (`validate`, unchanged), and 4 read "No F# equivalent" — but every
one of those 4 is a pure read-only view (`work`/`work list`, `work show`,
`work context`, `status`) that was never part of MIG-07 or MIG-08's own
scope, not a telemetry-producer or adapter command.** This inventory's
own acceptance bar ("every row reading full parity") is still not met,
but the reason has changed: what remains is no longer MIG-08 work at
all, and distribution evidence remains entirely uncollected regardless.

# Method

Identical to `EV-ROS-2026-A043`/`EV-ROS-2026-A045`: both CLI entry points
read in full, not sampled — Node's `tools/ros_cli.mjs` `main`/dispatch
function (every `args[0] === "..."` branch, including nested `args[1]`
checks) and F#'s `src/Ros.Cli/Program.fs` `dispatch` match expression.
The same three-way classification is used, unchanged:

- **No F# equivalent** — no command with matching semantics exists in
  `ros-fs` at all.
- **Read-only diagnostic only** — an F# command exists with a related name
  and decides/plans the same semantic question, but performs no filesystem
  write (or, for `validate`, performs a write-free but narrower-scoped
  check than Node's single command aggregates).
- **Full parity** — the F# command performs the same real, persisted effect
  Node's command performs, verified by an existing Node/F# differential
  test.

Collected against commit `9c6d5fd45375b0f6444c68213cfa36350108141e` on
branch `claude/node-to-fsharp-conversion-h9nyzn` (not yet merged to
`main` at collection time; PR #31 open). Runtimes: Node `v22.22.2`; .NET
SDK `10.0.111`.

# Command inventory

| Node command (`./ros ...`) | Durable effect | F# command (`ros-fs ...`) | Classification |
|---|---|---|---|
| `add TITLE ...` | writes `.ros/work/queue.json`/`.md` | `work capture --title ... --occurred-at ...` | **Full parity** (unchanged; differential-tested) |
| `work` / `work list` (no ids) | read-only (queue + context merged view) | none | No F# equivalent |
| `work ready [ID]` (no ids: read view; with ids: mutate) | writes queue on mutate form | `work backlog-transition --action ready --id ID --occurred-at ...` (mutate form only) | **Full parity** for the mutate form (unchanged); the no-id read view remains No F# equivalent |
| `work show ID` | read-only | none | No F# equivalent |
| `work start ID` | promotes queue -> live context, begins telemetry | `work start --id ID --occurred-at ...` | **Full parity** (unchanged; differential-tested) |
| `work update ID ...` | writes queue | `work update --id ID --occurred-at ...` | **Full parity** (unchanged; differential-tested) |
| `work attach ID ...` | writes queue + attachment bytes | `work attach --id ID --occurred-at ... --file PATH` | **Full parity** (unchanged; differential-tested) |
| `work abandon ID --reason` | writes queue | `work backlog-transition --action abandon --id ID --occurred-at ...` | **Full parity** (unchanged; differential-tested) |
| `work block ID --reason` | writes context/queue + events + telemetry lifecycle | `work block --id ID --occurred-at ... --reason ...` | **Full parity** (unchanged; differential-tested) |
| `work begin/resume/complete/done ID ...` | writes context + events + telemetry executions | `work start` (begin), `work resume`, `work complete` | **Full parity** for `begin`/`resume`/`complete` (unchanged; differential-tested, including `resume`'s deliberate `parentExecutionId` divergence from a confirmed production defect). Node's `done` is a pure `ACTION_ALIASES` spelling this CLI does not additionally wire as a second verb (a naming gap, not a behavioral one) |
| `work context [ID]` | read-only | none | No F# equivalent |
| `telemetry start ID ...` | writes an execution record (recover-or-create, no state transition) | `telemetry start ID [--execution-id ID] [--classification NAME]* [--classification-rationale TEXT] [--provider ...]... [--quiet]` | **Full parity**, including `--execution-id` and all eleven identity-override flags (differential-tested by driving the actual `ros` CLI wrapper directly) |
| `telemetry ingest ...` | writes an execution record | `telemetry ingest [TARGET] --input FILE [--adapter NAME]` | **Full parity** for every adapter name production itself recognizes (`generic`, `openai-codex`, `anthropic-claude-statusline`, all three hook adapters, all four OTel adapters — ten of ten in `TelemetryAdapters.all`; differential-tested per adapter) |
| `telemetry classify ...` | writes an execution record | `telemetry classify [TARGET] --classification NAME [...]` | **Full parity** (differential-tested) |
| `telemetry record ...` | writes an execution record | `telemetry record [TARGET] --metric ID --value VALUE` | **Full parity** (differential-tested) |
| `telemetry finalize ID` | writes an execution record | `telemetry finalize [TARGET]` | **Full parity** for the finalize mutation itself (differential-tested); `--input` (adapter ingestion at finalize time) remains deliberately unported and rejected outright, its own narrower gap within this row |
| `telemetry show [ID]` | read-only | `telemetry show [TARGET]` | **Full parity** (unchanged; differential-tested against production's own exported `showTelemetry`) |
| `telemetry summary/summarize [ID]` | read-only (aggregation) | `telemetry summary [ID]` | **Full parity** (differential-tested) |
| `telemetry adapters ...` | read-only | `telemetry adapters` | **Full parity** (unchanged; differential-tested against production's own exported `TELEMETRY_ADAPTERS`) |
| `adapter call ...` | invokes a configured file adapter | `adapter call --store FILE --request FILE` | **Full parity** (differential-tested) |
| `adapter publish ...` | writes `.ros/publications.json` | `adapter publish --target FILE` | **Full parity** (differential-tested) |
| `validate [--json]` | read-only (aggregates artifact + work-attribution + backlog-queue + telemetry findings in one command) | `artifacts validate [--json]`, `work validate [--json]`, `work backlog-validate [--json]` (three separate commands, no telemetry-findings equivalent at all) | Read-only diagnostic only — unchanged: no single F# command unifies these into one `validate` surface, and `telemetryFindings` has no F# port at all |
| `status` | read-only | none | No F# equivalent |
| `registry build [--dry-run]` | writes registry JSON | `registry build [--dry-run]` | **Full parity** (unchanged) |
| `registry check` | read-only | `registry check` | **Full parity** (unchanged) |
| *(no direct Node command)* | — | `git status [--json]` | Full parity as a standalone observation (unchanged) |

The 26 rows above match `EV-ROS-2026-A043`/`EV-ROS-2026-A045`'s own row
count and granularity exactly (including keeping
`begin`/`resume`/`complete`/`done` as the single combined row production's
own dispatch treats as one branch), so the counts below are directly
comparable to both prior records. The same one additional row noted in
`EV-ROS-2026-A045` still exists in the F# CLI with no Node counterpart at
all, not part of that count: `work decide`, `work plan`,
`work context-plan`, `work backlog-decide`, and `work
backlog-promotion-plan` remain useful read-only decision diagnostics for
exercising the pure decision layer directly.

# Change since `EV-ROS-2026-A045`

- **Rows newly reading Full parity (8 of the 26, up from 13 to 21):**
  `telemetry start` (now including its own `--execution-id`/identity
  flags, previously excluded even conceptually since the row itself was
  "No F# equivalent" at the last inventory), `telemetry ingest` (every
  adapter name), `telemetry classify`, `telemetry record`, `telemetry
  finalize`, `telemetry summary`, `adapter call`, `adapter publish`.
- **Rows unchanged (No F# equivalent, 4):** `work`/`work list`, `work
  show`, `work context`, `status` — every one a pure read-only view, none
  ever assigned to MIG-07 or MIG-08's own scope.
- **Rows unchanged (Read-only diagnostic only, 1):** `validate` — its
  constituent coverage is unchanged since the last inventory
  (work-attribution and backlog-queue validation already had dedicated
  F# diagnostics); no single F# command unifies them and telemetry
  findings still has no F# port.

# What remains, and what it means

Unlike the prior two inventories, what remains today is **not** MIG-08
scope. MIG-08's own remaining-scope list (`EV-ROS-2026-A044`'s inventory,
narrowed by `EV-ROS-2026-A045`) is now empty: every write-path
telemetry-producer command, every adapter name, and both adapter
commands have real F# effects. What this inventory still marks
incomplete is:

1. **Four pure read-only views** (`work`/`work list`, `work show`, `work
   context`, `status`) — never named as MIG-07 or MIG-08 scope in any
   prior record. Porting them would be a new, separately-scoped read-path
   slice, not a continuation of MIG-08.
2. **`validate`'s single-command unification**, plus a `telemetryFindings`
   port — also outside MIG-07/MIG-08's stated scope; `work
   validate`/`work backlog-validate`/`artifacts validate` already cover
   the constituent checks that do have F# diagnostics.
3. **Consumer distribution evidence** per `DF-ROS-2026-A027`'s own stated
   blocker — entirely uncollected; nothing in this round's progress
   touches this. This remains the actual blocker for `DF-ROS-2026-A028`
   Phase B, independent of command-surface parity.
4. **A rollback plan** for the redirect itself — unchanged, not started.

This record's own acceptance bar ("every row reading full parity") is
**not yet met**: 21 of 26 rows do, 5 do not (4 "No F# equivalent" plus 1
"Read-only diagnostic only"). But none of the 5 remaining gaps is
telemetry-producer or adapter work — the category this inventory has
tracked closing since `EV-ROS-2026-A044` opened MIG-08. Whether porting
the four read-only views and unifying `validate` is worth scoping as a
new slice, versus treating distribution evidence (item 3) as the actual
gating question for `DF-ROS-2026-A028`, is a decision this record does
not make.

# Limitations

- Same as `EV-ROS-2026-A043`/`EV-ROS-2026-A045`: a command-surface
  census, not a line-by-line semantic diff of every flag/option;
  per-command differential tests remain the source of truth for
  behavioral equivalence. `docs/migrations/fsharp/STATUS.md` is the
  authoritative, currently-maintained test count (104 Node, 7 Python, 326
  F# unit, and 159 differential/smoke tests as of this record) rather
  than a figure restated here that would drift out of date on the next
  slice.
- `telemetry finalize`'s "Full parity" classification covers the finalize
  mutation itself; its `--input` flag (adapter ingestion performed at
  finalize time rather than via a separate `telemetry ingest` call) is a
  narrower, still-unported sub-path within that one row — noted in the
  row itself rather than tracked as a fractional classification, matching
  this inventory's three-way method.
- Does not re-examine HTTP (`tools/ros_server.mjs`), the hub
  (`tools/ros_hub_cli.mjs`), or bootstrap/init -- unchanged, still out of
  scope.

# Reproduction

```bash
grep -n 'args\[0\] ===' tools/ros_cli.mjs
grep -n '| "work" ::\|| \[ "\|"artifacts"\|"registry"\|"git"\|"telemetry"\|"adapter"' src/Ros.Cli/Program.fs
```
