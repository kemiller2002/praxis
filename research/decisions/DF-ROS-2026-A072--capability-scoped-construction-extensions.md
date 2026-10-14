---
id: DF-ROS-2026-A072
title: Route construction work mechanically first and admit handwritten code only into host-declared slots
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-12
updated: 2026-10-12
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A028]
related_documents: [DF-ROS-2026-A071, docs/construction.md, docs/cli.md]
tags: [construction, ordo, routing, extensions, authority, containment]
confidence: medium
derived_from: [DF-ROS-2026-A071]
provenance:
  contributions:
    EXE-20261012T185257851Z-f0478885:
      operations: [created]
      at: 2026-10-12T19:07:40.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record capability-scoped construction extensions and mechanical-first routing (GH-243)"
---

# Context

Praxis #243 (CONSTRUCT-004) asks that model reasoning and code editing be
reserved for behavior that cannot be generated, and that a custom-code worker
be scoped to specific non-generated files. In time-entry timer pilot 01 the
construction agents wrote `Extensions.fs`, `Completion.fs` and
`TimerOracleAdapter.fs` into an outbox. A bash harness, not Praxis, decided
which outbox files were allowed and committed them on top of the generated
construction (`DF-ROS-2026-A071`). That allow-list lived in the harness, so
nothing in Praxis enforced it.

Host containment for worker processes (#231, #235) is not qualified yet.

# Decision

1. **Slots are host policy.** The local host configuration
   (`praxis.local-host/1`, outside the repository) gains an optional
   `extensionSlots` list. Each slot is one exact file name directly under
   `constructionMount`, the Ordo extension contracts it may implement, and a
   byte limit. A plan, worker or submission cannot declare, widen or rename a
   slot. A contract belongs to at most one slot.
2. **One deny rule at both layers.** Some names are never extensions, whatever
   the host declares: path separators, dot entries (`.github`, `.claude`,
   `.echelon`, `.git`), the `generated` directory and `*.g.*` names, project
   and build files, package and dependency files, policy, agent-instruction
   and CI files, scripts and binaries, ECIR documents and requirement sources.
   The rule refuses a host declaration at configuration load and a submitted
   file at `extend`, so neither layer relies on the other.
3. **Routing is a separate, effectful command.** `praxis construct route`
   classifies an admitted construction's work:
   - Ordo's generated artifacts are *deterministic*;
   - `resolve` obligations are *semantic* decisions;
   - `implement` obligations are *bounded custom* work in their slot;
   - `verify` obligations are routed to independent verification.

   An `implement` obligation with no slot is a typed `unroutable-implementation`
   exception that keeps its requirement keys. The record is content-addressed
   and write-once under `routes/` in the host construction root. It is never
   written to `.ros`. Routing is not folded into `preview`, because `preview`
   is read-only by contract and tested as such; persisting a record is an
   effect.
4. **`praxis construct extend` commits bytes, never runs code.** It re-admits
   the plan exactly as `apply` does: grant, signed approval, committed sources
   and pinned engine. It then requires the target to carry that exact
   construction: every generated file unchanged, and a commit in its history
   with this plan's `Construction-Plan`, `-Cohort` and `-Group` trailers. It
   judges every submitted entry on its own and reports every refusal before
   any effect. It also refuses slot placement inside the generated root,
   beside the ECIR sources, on a requirement source, or onto a generated or
   non-regular entry. The commit is built from Git objects in a private
   index, uses the journalled preparation time, carries `Extension-*`
   trailers, is journalled like `apply`, and moves the target by
   compare-and-swap. The same files yield `already-current`.
5. **Autonomous custom-code workers stay disabled.** No construction command
   starts a worker, a build or submitted code. `--worker`, `--command`,
   `--run`, `--exec`, `--agent-command` and `--adapter` are refused with an
   explicit reason; they are never silently ignored. Each routing record says
   `"workerExecution": {"status": "disabled"}`.
6. **Only the implementation role fills slots.** `--role` defaults to
   `implementation`. Verification and review roles are refused so that they
   stay independent of the code they judge. This is a routing constraint on
   the request, not authentication.

# Alternatives

- **Routing inside `preview`.** Rejected: it would make `preview` write to
  the host. Keeping one read-only command and one effectful one is simpler
  to audit.
- **Slots as directories or globs.** Rejected: a glob is a scope that can
  silently expand. Exact names make every accepted path enumerable in the
  host configuration.
- **Letting `extend` launch the worker that writes the files.** Deferred
  until containment is qualified (#231, #235). Committing submitted bytes
  needs no containment, because the host runs nothing.
- **Treating the deny rule as the only check.** Rejected: the allow-list
  (declared and used slots) is the primary control. The deny rule is a
  second, independent layer.

# Consequences and limits

- What is done: host-declared slots; mechanical-first classification with a
  persisted, evidence-backed routing record; typed exceptions for unslotted
  `implement` obligations; and host commits of handwritten files with
  provenance trailers.
- What is NOT done:
  - autonomous worker execution: no worker is launched, scheduled or
    budgeted by these commands;
  - OS containment: none is claimed;
  - generating versioned extension interfaces beyond Ordo's existing
    contracts.
- Routing records count deterministic writes, bounded custom slots and
  obligations, and semantic obligations. They do not observe reasoning time
  or tokens, and say `unavailable` rather than invent them. Execution
  telemetry owns those figures.
- Reading the submission directory is file mechanics, not OS protection. A
  concurrent writer could swap a file between the link check and the read.
  The host owns the outbox, and the submitted bytes are only committed as
  data, never executed.
- An `implement` exception does not block `extend` for the slots that do
  exist. It stays in the record until an operator-authorized host
  configuration revision adds a slot and the plan is re-routed.

# Validation

`PRAXIS_TEST_FILTER="construction cli"` runs the real CLI against Ordo's timer
fixture and the pilot's handwritten `Extensions.fs`. The tests cover:
- routing counts and the typed exception;
- the exact extended tree, its trailers and idempotence;
- privilege escalation and bogus scope through slot names;
- unsafe dependency, project and generated files, and an oversized file;
- symlinks;
- stale plans: a target without the construction, missing trailers and
  another plan digest;
- a wrong role or group, a revoked signer and worker-execution flags;
- placement beside the ECIR sources.
