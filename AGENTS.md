---
id: GV-START-001
title: Agent Startup Guide
status: canonical
version: 1.9.0
owners:
  - repository-governance
created: 2026-07-22
updated: 2026-09-29
review_cycle: quarterly
supersedes: []
superseded_by: []
related_documents:
  - docs/00-governance/README.md
  - docs/development-telemetry.md
  - docs/agent-provenance.md
  - docs/cli.md
  - docs/installation.md
  - docs/upgrading.md
  - docs/remote-agent-contract.md
  - docs/remote-protocol.md
tags: [governance, agents, startup, provenance]
---

# Agent Startup Guide

## Mission

The Repository Operating System (ROS) makes research, engineering, decisions, and handoffs durable without relying on conversation history or tribal knowledge.

## Start Here

1. Read [the governance index](docs/00-governance/README.md).
2. Identify the task's scope and operating mode.
3. Locate the applicable canonical domain records; inspect the repository and user changes before editing.
4. State or record material unknowns, constraints, assumptions, and risks.
5. Use the smallest process that preserves correctness, traceability, and continuity.
6. Execute, validate, update affected records, and leave a handoff.

Detailed rules are in the [Agent Operating Manual](docs/00-governance/Agent-Operating-Manual.md). Research packages follow the [REP Specification](docs/00-governance/Research-Execution-Package-Specification.md); engineering follows the [Engineering Standards](docs/00-governance/Engineering-Standards.md).

## Authority

Apply, in descending order: explicit user instruction; applicable safety, legal, and platform constraints; canonical governance; accepted domain REPs and theory; accepted architecture and decision records; current implementation; local convention; agent preference. A higher authority cannot authorize a violation of an applicable safety or legal constraint. When same-level sources conflict, prefer the narrower and newer accepted record and document the resolution; escalate if the outcome materially changes the authorized goal.

## Core Rules

- Never fabricate evidence, file reads, approvals, commands, test results, or certainty.
- Preserve user work. Inspect before modifying; do not destroy or irreversibly migrate without authorization.
- Make reasonable, reversible, in-scope decisions. Escalate high-impact irreversible, security/privacy-sensitive, legally ambiguous, or materially out-of-scope decisions.
- Research by testing hypotheses against confirming and falsifying evidence. Engineering by establishing a baseline, defining acceptance criteria, making the smallest robust change, and testing in proportion to risk.
- Important claims cite `EV-`, `HY-`, and `TH-` records when those records exist. Material decisions use `DF-`, which canonically means **Decision Record**.
- Do not silently change canonical policy. Propose or record the change, its evidence, consequences, version, and migration path.
- Do not claim a test passed unless it ran and passed. Name skipped or unavailable checks and their implications.
- Treat execution telemetry as evidence: discover capabilities, distinguish zero from unavailable, preserve normalized and sanitized raw provider data, prefer deterministic collection, and never invent a metric.
- Not every edit needs a REP. Use the artifact threshold in the Agent Operating Manual.

## Handoff

For substantial work, record: objective; work completed; files changed; decisions and assumptions; tests run and results; evidence added; unresolved questions; risks; and next recommended action. A capable successor must be able to continue without the originating conversation.

## Work Protocol

Before meaningful mutation, identify the external work item and run `./ros work begin --id ID --occurred-at TIMESTAMP` (see the F# CLI note below for the timestamp — it must be the real current time, not an arbitrary one). That transition starts an execution-telemetry record; inspect `./ros work context ID`, classify the work, and ingest runtime telemetry that the current environment can expose. Preserve unknown provider fields through the sanitized raw layer and record unsupported/unavailable capability explicitly. Perform the bounded work, gather configured evidence, commit and push it, record a durable checkpoint (see "Durable checkpoints and continuity" below), request a legal transition with `./ros work complete --id ID --occurred-at TIMESTAMP --evidence TYPE=PATH` (repeatable; finalizes active telemetry), then run `./ros registry build` and `./ros validate`, and commit and push the resulting Praxis state. Attribute canonical records you create or change with `./ros provenance record` (see Agent Identity and Provenance below). Use `./ros work block --id ID --occurred-at TIMESTAMP --reason TEXT` and `./ros work resume --id ID --occurred-at TIMESTAMP` rather than hand-editing context. Use `./ros status` when resuming unfamiliar work. Meaningful committed changes require machine-readable attribution; see `docs/work-protocol.md` and `docs/development-telemetry.md`. If meaningful changes were committed while no work item was active, reconcile them after the fact with `./ros work reconcile --id ID --reason TEXT --commit REV --occurred-at TIMESTAMP` (Git-evidenced, recorded as post-hoc, never a substitute for beginning work). Never touch, rewrite, or recommit files to manufacture attribution, and never create a work item only to absorb changes.

No externally-assigned ID yet? Check `./ros work ready` for capturable, unblocked repository work before assuming none exists, and use `./ros add "..."` to record a newly discovered obligation instead of leaving it as an unfiled comment or dropped observation (`add` does not require `--occurred-at`; it defaults to the real current time). `./ros work start --id ID --occurred-at TIMESTAMP` (`begin` is also accepted) promotes a ready backlog item into the protocol above. This local backlog is repository-scoped triage, not a project-management system; see the "Local backlog" section of `docs/work-protocol.md`.

## Durable checkpoints and continuity

**An executor session is disposable. Repository state and Praxis state are
the continuity boundary.** No meaningful completed work may exist only in an
executor's local environment: a successor on another machine, with no access
to your filesystem, process, or conversation, must be able to continue. See
`docs/work-protocol.md` ("Durable checkpoints and continuity") and
`DF-ROS-2026-A042`.

- A **commit** is local. A **pushed commit** is on a remote. A **verified
  durable checkpoint** is Praxis's own record that your HEAD, the checkpoint
  commit, and the head of your upstream remote branch were the same commit,
  with no meaningful uncommitted work: `./ros work checkpoint --id ID
  --occurred-at NOW --summary "what is done" --next-action "what is next"
  [--step STEP-ID]`. Praxis never commits, pushes, or stashes for you.
- A **historical checkpoint** is that record; it is never rewritten. A
  **currently recoverable checkpoint** is one the remote still carries now;
  `./ros work context ID --text` and `./ros status` report both, separately.
- Checkpoint at coherent recovery boundaries, not on a timer and not per
  edit: after a meaningful implementation slice or material telemetry step;
  before a risky change; before switching work items or repositories; before
  an intentional handoff; when context exhaustion or termination looks
  possible; before blocking after new work; before completing Git-backed
  work. Never create a meaningless commit to satisfy Praxis.
- For Git-backed work the order is: commit, push, `work checkpoint`,
  `work complete`, then commit and push the Praxis state (`.ros/`). Where
  `workProtocol.continuity.requireDurableCheckpoint` is set, completion
  refuses anything else, and blocking after un-checkpointed work needs a
  checkpoint or `--unrecoverable-reason TEXT` stated truthfully. Work that
  changed nothing completes as before.
- **New observability is effective-current.** Praxis preserves truthful
  historical gaps rather than restarting work or fabricating telemetry.
  Adopt step telemetry (`./ros telemetry step start|complete|fail`) at the
  next material slice; never restart an execution or work item to gain it,
  never invent earlier steps, and never split earlier usage among steps.
  Missing historical step data is unavailable, not zero and not invalid.
- **Taking over** active work whose executor disappeared: fetch, switch to
  the checkpoint's branch in a clean checkout, then `./ros work continue
  --id ID --occurred-at NOW` under your own identity. You get a new
  execution whose parent is the predecessor's; the predecessor is recorded
  as interrupted, never as you and never as successful. `blocked -> resume`
  remains for intentionally blocked work.

## Agent Identity and Provenance

Every agent working under this repository has an explicit, machine-readable
identity, and records it on the work it creates or changes. This applies to
every provider and runtime, and equally to humans and automation. See
[`docs/agent-provenance.md`](docs/agent-provenance.md).

1. **Establish identity once, at the start of the execution.**
   `./ros work begin` records who you are in the execution record, and every
   later command inherits that identity.
   - A known runtime (Codex, Claude Code, Gemini CLI, Copilot, GitHub
     Actions) is detected automatically.
   - Otherwise declare yourself with `ROS_ACTOR_KIND`
     (`agent|human|automation`), `ROS_ACTOR` (your stable agent ID),
     `ROS_TELEMETRY_PROVIDER`, `ROS_TELEMETRY_MODEL`, and
     `ROS_TELEMETRY_RUNTIME`, or pass the matching flags on `work begin`.
   - Check the result with `./ros provenance identity`.
2. **Never impersonate** another agent, human, or execution. Never record work
   under an execution you did not run. ROS refuses a contribution whose
   actor contradicts its execution.
3. **Never fabricate** a provider, model, version, session, or agent name.
   Leave an unknown value unset: ROS records it as `unknown`, which is correct.
4. **Preserve existing provenance.** Never edit, reorder, or delete another
   contributor's `provenance` entry.
5. **Add your contribution; do not replace anyone else's.**
6. **Attribute every requirement you create**:
   `./ros provenance record --id RQ-... --operation created`.
7. **Attribute every meaningful modification you make** to a canonical record
   (requirement, decision, evidence, hypothesis, experiment, theory,
   journal, mission, research package): `--operation modified`. Use
   `reviewed` or `approved` only for review or approval you actually
   performed.
8. **Propagate lineage** when you derive one artifact from another:
   `--derived-from SOURCE-ID`. Lineage names the source. It does not make
   the source's author an author of your artifact.
9. **Make generated evidence, findings, and results traceable** to your
   execution. Record them inside the work execution, and name supporting
   records with `--evidence`.
10. **Run `./ros validate` before finishing.** Missing or contradictory
    provenance on new work is an error.

Identity recorded this way is provenance, not authentication. It is
self-reported and cross-checked, not cryptographically proven.

## No local runtime? Use remote execution

If you can reach this repository on GitHub but cannot run Praxis locally,
for example because there is no .NET, you are governed the same way through
typed remote requests. Follow
[`docs/remote-agent-contract.md`](docs/remote-agent-contract.md): discover
with a `praxis.describe` request, send typed operations with a stable
`requestId` and the `expectedSha` you read, and read results from the
repository.

- Remote execution is available only when
  `.github/workflows/praxis-remote.yml` exists. Remote mutation is available
  only where `ros.json` lists it under `remote.capabilities`.
- The rules above still apply: never impersonate, never fabricate identity,
  and never touch files to manufacture attribution.
- Do not work around a missing runtime by hand-editing `.ros/` state.
- Checkpoint and take over remotely with `work.checkpoint` (naming your own
  `execution.id`) and `work.continue` (protocol 1.3).
- Can commit but cannot dispatch Actions? Commit the request as
  `.praxis-inbox/<requestId>.json` on a `praxis-inbox/...` branch. The inbox
  relays it unchanged (`DF-ROS-2026-A045`).

## Lifecycle commands

Installation, verification, diagnosis and upgrade go through the standard
lifecycle interface, implemented in F# and distributed through npm:

```
npx --package=@echelon-foundry/repository-operating-system ros init
npx --package=@echelon-foundry/repository-operating-system ros status
npx --package=@echelon-foundry/repository-operating-system ros verify
npx --package=@echelon-foundry/repository-operating-system ros upgrade
npx --package=@echelon-foundry/repository-operating-system ros doctor
```

In this source checkout the same commands are available as `./ros init`,
`./ros verify` and so on. `init` is idempotent, every command is
non-interactive, `--dry-run` and `--check` change nothing, and `--json` puts a
single document on stdout. Exit codes are a documented contract: `0` success,
`2` invalid arguments, `3` verification failed, `4` incompatible installation,
`5` migration blocked, `6` prerequisite failure. See
[`docs/cli.md`](docs/cli.md), [`docs/installation.md`](docs/installation.md)
and [`docs/upgrading.md`](docs/upgrading.md).

Installation state lives in `.echelon/ros.json`; it is tool bookkeeping, not
repository work, and is never treated as a meaningful change for attribution.
Before editing a file the tool installed, check its ownership there: a
`tool-owned` file is replaced on upgrade, so a local edit belongs in a
`user-owned` or `shared` file instead.

## F# CLI

`./ros` in this source checkout, and every project bootstrapped via `npx
ros-bootstrap init` (both profiles), runs the F# CLI (`DF-ROS-2026-A030`,
`DF-ROS-2026-A032`). Node is no longer a CLI anywhere in this project or
what it scaffolds. Node's own implementation (`tools/ros_cli.mjs` and its
companions) remains in this repository and in the `project-administration`
starter profile only, as `tools/ros_server.mjs`'s/`ros_hub_cli.mjs`'s
in-process internal library dependency (`DF-ROS-2026-A033`) — it is no
longer characterized or scaffolded as a CLI rollback path, and the
`greenfield` starter profile no longer includes it at all. If `./ros`
reports it needs building, run `npm run build:fsharp` first; CI always
builds it before `./ros` runs, so this only affects local/manual use after
a source change.

F#'s command syntax differs from Node's in ways worth knowing rather than
guessing from memory:

- Every mutating command shown above except `add` requires an explicit
  `--id ID` (repeatable) and `--occurred-at TIMESTAMP`, rather than a
  positional ID with an implicit clock read. **Pass the real current
  time** (e.g. `` `date -u +%Y-%m-%dT%H:%M:%S.000Z` ``), not an arbitrary
  or backdated one: a telemetry execution's own `startedAt` always reads
  the real wall clock (matching production), and a later transition whose
  supplied `--occurred-at` predates it fails `./ros validate` with a
  spurious "capability state recording order must be chronological"
  finding — a real trap this decision's own preparation hit and diagnosed,
  not a defect to work around.
- `work start` and `work begin` are both accepted, as are `work complete`
  and `work done`.
- `work context ID` and `work show ID` keep Node's positional-ID form
  unchanged.
- `docs/migrations/fsharp/STATUS.md` is the authoritative ledger of any
  remaining command-surface gaps (e.g. `telemetry finalize --input`, a
  deliberately unported adapter-ingestion-at-finalize path).

This section's command-syntax notes apply equally to `./ros` in this
source checkout and to any project's own bootstrapped `./ros`, since both
run the same F# CLI.
