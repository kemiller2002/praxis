---
id: EV-ROS-2026-A043
title: F# shadow CLI versus production ./ros command-surface parity inventory
status: superseded
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-09
updated: 2026-09-10
research_area: repository-operating-system
evidence_type: primary
supports:
  - DF-ROS-2026-A028
related_documents:
  - DF-ROS-2026-A027
  - EV-ROS-2026-A018
  - docs/migrations/fsharp/ARCHITECTURE.md
  - docs/migrations/fsharp/ROADMAP.md
  - docs/migrations/fsharp/STATUS.md
supersedes: []
superseded_by:
  - EV-ROS-2026-A045
tags: [fsharp, migration, parity, cli, distribution, sde]
confidence: high
---

# Evidence summary

The user asked, in-session, to redirect `./ros` to use only the F# solution.
`DF-ROS-2026-A027` already states that redirecting `./ros` requires "a later
accepted decision, not this record alone." This record is that decision's
factual precondition: a direct, command-by-command inventory of every
`./ros` (Node, `tools/ros_cli.mjs`) subcommand against the current F# shadow
CLI (`ros-fs`, `src/Ros.Cli/Program.fs`), collected by reading both
dispatchers in full rather than sampling.

**Headline finding: zero Node commands that mutate durable repository state
have any F# equivalent today.** The F# CLI's only production-shape
capability is artifact registry projection (`registry build`/`check`, which
writes real disposable registry JSON, byte-identical to Node's). Every other
F# command is an explicitly non-persisting "shadow diagnostic" — it decides
or plans in memory and prints JSON; it never writes `.ros/context/`,
`.ros/work/`, `.ros/telemetry/`, or `.ros/events/`. Redirecting `./ros` to
`ros-fs` today would delete the ability to run `./ros add`, every
`./ros work ...` transition, every `./ros telemetry ...` command, and both
`./ros adapter ...` commands — the commands this repository's own governed
work protocol (`AGENTS.md`'s Work Protocol section) requires for every
tracked task, including the ones used to record this very evidence record.

# Method

Both CLI entry points were read in full, not sampled:

- Node: every `args[0] === "..."` (and nested `args[1]`) branch in
  `tools/ros_cli.mjs`'s `main`/dispatch function.
- F#: every pattern in the `dispatch` match expression in
  `src/Ros.Cli/Program.fs`.

For each Node command, its F# counterpart (if any) was classified as one of:

- **No F# equivalent** — no command with matching semantics exists in
  `ros-fs` at all.
- **Read-only diagnostic only** — an F# command exists with a related name
  and decides/plans the same semantic question, but performs no filesystem
  write and takes its "current state" as explicit CLI flags rather than
  reading `.ros/` itself (except where a real Infrastructure-backed
  observation port already exists, noted per row).
- **Full parity** — the F# command performs the same real, persisted effect
  Node's command performs, verified by an existing Node/F# differential
  test.

Collected against commit `869139a989c80282cd5490746d71498c11a76431` (branch
`claude/node-to-fsharp-conversion-h9nyzn`, based on `main` at `2159287`).
Runtimes: Node `v22.22.2`; .NET SDK `10.0.111`.

# Command inventory

| Node command (`./ros ...`) | Durable effect | F# command (`ros-fs ...`) | Classification |
|---|---|---|---|
| `add TITLE ...` | writes `.ros/work/queue.json`/`.md` | none | No F# equivalent |
| `work` / `work list` | read-only (queue + context) | none | No F# equivalent |
| `work ready [ID]` | writes queue on mutate form | none | No F# equivalent |
| `work show ID` | read-only | none | No F# equivalent |
| `work start ID` | promotes queue -> live context | `work backlog-promotion-plan` | Read-only diagnostic only (plans the promotion; does not write the queue or context) |
| `work update ID ...` | writes queue | none | No F# equivalent |
| `work attach ID ...` | writes queue + attachment bytes | none | No F# equivalent |
| `work abandon ID --reason` | writes queue | `work backlog-decide` | Read-only diagnostic only (decides legality; does not write) |
| `work block ID --reason` | writes context + events + telemetry | `work decide`, `work plan` | Read-only diagnostic only |
| `work begin/resume/complete/done ID ...` | writes context + events + telemetry executions | `work decide`, `work plan`, `work context-plan` (`--resolve-telemetry` for execution IDs) | Read-only diagnostic only |
| `work context [ID]` | read-only | none (context is a CLI input to `work context-plan`, not something F# reads on its own outside the telemetry-candidate port) | No F# equivalent |
| `telemetry start ID ...` | writes an execution record | none | No F# equivalent |
| `telemetry ingest ...` | writes an execution record | none | No F# equivalent |
| `telemetry classify ...` | writes an execution record | none | No F# equivalent |
| `telemetry record ...` | writes an execution record | none | No F# equivalent |
| `telemetry finalize ID` | writes an execution record | none | No F# equivalent |
| `telemetry show [ID]` | read-only | none | No F# equivalent |
| `telemetry summary/summarize [ID]` | read-only (aggregation) | none | No F# equivalent |
| `telemetry adapters ...` | read-only | none | No F# equivalent |
| `adapter call ...` | invokes a configured file adapter | none | No F# equivalent |
| `adapter publish ...` | writes `.ros/publications.json` | none | No F# equivalent |
| `validate [--json]` | read-only | `artifacts validate [--json]` | Read-only diagnostic only — Node's `validate` additionally checks work, telemetry, and stale-registry state; F#'s is artifact-only (documented in `ARCHITECTURE.md`'s "Implemented artifact slice" section) |
| `status` | read-only | none | No F# equivalent |
| `registry build [--dry-run]` | writes registry JSON | `registry build [--dry-run]` | **Full parity** (MIG-04/05; Node/F# byte-identical, differential-tested) |
| `registry check` | read-only | `registry check` | **Full parity**, with the same artifact-only scope note as `validate` |
| *(no direct Node command)* | — | `git status [--json]` | Full parity as a standalone observation (MIG-06; Node has no public `git status` command, only internal callers, but the underlying `observeGitStatus` is differential-tested byte-for-byte) |

Every "read-only diagnostic only" F# command is deliberately documented that
way in `docs/migrations/fsharp/ARCHITECTURE.md` (e.g., "a shadow diagnostic
surface... not a state-changing command") — this record does not allege a
defect in those commands; it measures the distance from here to a full
command-surface switch.

# What a redirect needs that does not exist yet

1. **State-changing effect handlers in F#** for every "No F# equivalent" and
   "Read-only diagnostic only" row above: real writes to `.ros/context/`,
   `.ros/work/`, `.ros/telemetry/`, `.ros/events/`, and
   `.ros/publications.json`, composed from the already-typed decisions using
   the already-built `Ros.Infrastructure.Work.WorkStateTransaction`/
   `BacklogStateTransaction` recovery ports (MIG-05) plus a new
   execution-creation effect (the `PendingNewExecution` boundary named in
   `ARCHITECTURE.md`'s telemetry-resolution section).
2. **`add`/`work update`/`work attach`/all `telemetry ...` producer
   commands/`adapter ...`**, none of which have any F# domain model yet —
   this is essentially all of MIG-08 (execution/telemetry core, currently
   `deferred` in `ROADMAP.md`) plus new backlog-capture/adapter modeling not
   currently on the roadmap as a named slice at all.
3. **Consumer distribution evidence** per `DF-ROS-2026-A027`'s own stated
   blocker: explicit macOS/Linux/Windows installation, startup time, binary
   size, offline/update behavior, integrity/checksum verification, and
   rollback, for a framework-dependent-or-self-contained `.NET 10` binary
   replacing a Node/npm launcher that currently has none of this evidence
   gathered.
4. **A rollback plan** for the redirect itself, since `./ros` is this
   repository's own operating interface — including for the governed work
   protocol used to produce this very record.

# Limitations

- This inventory is a command-surface census, not a line-by-line semantic
  diff of every flag/option; per-command differential tests (already
  extensive — 208 checks as of this record) remain the source of truth for
  behavioral equivalence of the commands that do have F# coverage.
- It does not re-examine HTTP (`tools/ros_server.mjs`), the hub
  (`tools/ros_hub_cli.mjs`), or bootstrap/init, all explicitly out of scope
  for the current F# migration per `ROADMAP.md`.

# Reproduction

```bash
grep -n 'args\[0\] ===' tools/ros_cli.mjs
grep -n '| "work" ::\|"artifacts"\|"registry"\|"git"' src/Ros.Cli/Program.fs
```
