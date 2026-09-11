---
id: DF-ROS-2026-A030
title: Redirect this repository's own ./ros dispatch to the F# CLI
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-11
updated: 2026-09-11
author_agent: claude-sonnet-5
supporting_evidence:
  - EV-ROS-2026-A047
  - EV-ROS-2026-A048
related_documents:
  - DF-ROS-2026-A027
  - DF-ROS-2026-A028
  - DF-ROS-2026-A029
  - AGENTS.md
  - docs/work-protocol.md
  - docs/migrations/fsharp/ARCHITECTURE.md
  - docs/migrations/fsharp/STATUS.md
supersedes: []
superseded_by: []
tags: [architecture, fsharp, migration, distribution, sde, decision]
confidence: high
---

# Context

The user directly asked, in explicit terms, to make the F# application "the
only used item to administrate the framework." `DF-ROS-2026-A028` named
this exact question as its Phase C, gated on Phase A (full command-surface
effect parity) and Phase B (consumer distribution evidence) both being
accepted, plus its own new decision record with a rollback plan and
cutover mechanism. `EV-ROS-2026-A047` closed Phase A; `DF-ROS-2026-A029`
(evidenced by `EV-ROS-2026-A048`) closed Phase B. This record is that
Phase C decision — scoped to this repository's own `./ros` only, not to
`starter/greenfield/ros` or any other consumer-facing template, which is
its own separate, subsequent decision.

# What "full parity" did and did not mean

Before redirecting anything, this record's own preparation surfaced a real
gap `EV-ROS-2026-A047`'s row-level census did not capture: "full parity"
there means each Node command has a differential-tested F# command
producing the **same effect**, not that the two CLIs share an **identical
command-line vocabulary**. Concretely:

- Node's `add TITLE ...` has no F# top-level command at all; the
  equivalent was `work capture --title ... --occurred-at ...`.
- Node's `work ready` (no ID) is a status-filtered read view; F#'s
  `work`/`work list` had no `--status`/`--tag` filtering at all (a gap
  already named, but not closed, in `ARCHITECTURE.md`/`TRACEABILITY.md`
  since the increment that ported `work list`).
- Node accepts `begin`/`done` as transition-action spellings (`done` via
  `ACTION_ALIASES`); F# only recognized `start`/`complete`.
- Node never requires an explicit timestamp for these commands (it reads
  the real clock internally); every F# mutating command requires an
  explicit `--occurred-at`, a deliberate purity choice made throughout
  this migration (Tier 4/CLI supplies the clock value explicitly rather
  than a pure decision reading `DateTime.Now` itself) that Node's own CLI
  surface never exposed a reason to notice before now.

A Node-compatible translation shim in front of F# was considered and
rejected: it would mean building and maintaining a second, largely-untested
CLI-parsing layer whose only purpose is cosmetic syntax preservation,
working against the stated goal that F# actually be the administrator
rather than hidden behind a lookalike mask. Instead, the three cheap,
genuinely valuable, purely additive gaps above were closed directly in F#
this round (`add`, `work`/`work ready --tag`/`--status` filtering,
`begin`/`done` aliases — see the differential tests added to
`tests/work-list-fsharp-differential.test.mjs`), and the handful of
commands whose calling convention differs more substantially (`--id`
flags instead of positional IDs, required `--occurred-at`) are handled by
updating `AGENTS.md`'s own Work Protocol section to describe F#'s real,
already-established, already-tested syntax rather than Node's.

# A real, load-bearing timestamp finding

Manual sandbox testing (a full `add` → `work ready` → `work begin` →
`work block` → `work resume` → `work done` → `registry build` →
`validate` chain, driven against a disposable bootstrapped project, not
this repository) surfaced a genuine trap, not a defect: `createExecution`
(`FileTelemetryExecutionRepository.fs`) reads the real wall clock for a
new telemetry execution's `startedAt` and every initial capability's
`discoveredAt`/`lastAssessedAt`/`recordedAt` — mirroring production's own
`startExecution`, which does the same when no `startedAt` override is
supplied (true of every current CLI command, Node and F# alike). Passing
synthetic, backdated `--occurred-at` values to subsequent `work
block`/`work resume` calls against that same execution — as an early,
faster version of this same sandbox test did — produces a real
`telemetryFindings` rejection ("capability state recording order must be
chronological"), because the transition's own supplied timestamp then
predates the execution's real-wall-clock `startedAt`. Once the sandbox
test was corrected to pass approximately-real timestamps (as any real
caller naturally would, e.g. `` `date -u +%Y-%m-%dT%H:%M:%S.000Z` ``), the
entire chain validated cleanly. `AGENTS.md` is updated to state this
plainly: `--occurred-at` must reflect the real time of the action, not an
arbitrary or backdated one.

# Decision

Redirect this repository's own `./ros` (the repo-root file only) to exec
the F# CLI:

```js
#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";
import fs from "node:fs";

const repoRoot = path.dirname(fileURLToPath(import.meta.url));
const dll = process.env.ROS_FS_DLL_PATH_OVERRIDE
  ?? path.join(repoRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

if (!fs.existsSync(dll)) {
  console.error(`./ros needs the F# CLI built first. Run 'npm run build:fsharp', then retry.\nMissing: ${dll}`);
  process.exit(1);
}

const result = spawnSync("dotnet", [dll, ...process.argv.slice(2)], { stdio: "inherit" });
if (result.error) {
  console.error(`./ros: failed to invoke dotnet: ${result.error.message}`);
  process.exit(1);
}
process.exitCode = result.status ?? 1;
```

This is a **framework-dependent** invocation (not the self-contained
single-file shape `DF-ROS-2026-A029` chose for external npm consumers):
this repository already requires the .NET 10 SDK to build and test its own
F# source (`npm run build:fsharp`/`npm run test:fsharp`, already part of
`npm run test:all` and both CI workflows), so there is no consumer
distribution problem to solve here — only "is it built yet," which the
existence check above answers with a clear, actionable message rather than
a cryptic `dotnet` failure. `ROS_FS_DLL_PATH_OVERRIDE` exists for testing
and for pointing at a non-Release build; unset, it resolves the normal
Release output path.

`tools/ros_cli.mjs` and every other Node file are left completely in
place, untouched, and still covered by all 112 Node tests — this is a
one-file dispatch change, not a removal of Node.

`AGENTS.md`'s Work Protocol section is updated to describe F#'s real
invocation syntax for the commands whose calling convention differs from
Node's (`--id ID --occurred-at TIMESTAMP` instead of a positional ID with
an implicit clock), and to state the real-timestamp requirement above.

# Alternatives

- **Build a Node-syntax-compatible shim in front of F#:** rejected — see
  above; it substitutes one large, new, low-value translation layer for
  the actual goal.
- **Leave `./ros` on Node and only expose F# via `ros-fs` (the npm
  launcher from `DF-ROS-2026-A029`):** rejected as the sole outcome,
  because the user's request is specifically that F# administer this
  framework, not merely that it be reachable alongside Node. `ros-fs`
  remains available and unaffected by this decision (it targets external
  consumers of the npm package, an unrelated distribution question).
- **Redirect `starter/greenfield/ros` in the same change:** rejected as
  out of scope for this record; that template reaches every consumer of
  the npm package and needs its own distribution mechanism (self-contained
  binaries, no source checkout to build from), evidence, and decision,
  tracked separately.
- **Delete `tools/ros_cli.mjs` now that F# administers this repository:**
  rejected. Node remains the sole distribution target for
  `starter/greenfield/ros` until that separate decision exists; removing
  it now would break every future `ros-bootstrap init` consumer.

# Consequences

Every `./ros` invocation in this repository (by a human contributor, an
agent, or CI) now runs the F# CLI. Command syntax for the small set of
commands `AGENTS.md` documents changes as described above; anyone
following stale muscle memory from before this record gets a clear F#
error (unrecognized command, or a stated required flag) rather than silent
wrong behavior. CI is unaffected: `ros-validation.yml` and `publish.yml`
already run `npm run test:all` (which builds F#) before any `./ros`
invocation, so the F# binary is always present by the time `./ros` runs
there. Local/manual use requires having run `npm run build:fsharp` (or
`test:all`) at least once since the last source change; `./ros`'s own
error message says so when it has not.

# Reversibility and validation

Fully reversible: `git checkout` the single `ros` file back to its
previous content (`import { main } from "./tools/ros_cli.mjs"; ...`)
restores Node dispatch instantly, since Node's own implementation is
untouched and still fully tested. Validation: the full test gate (112
Node, 7 Python, 375 F# unit, 185 differential/smoke tests) passing, plus a
live dogfood — this repository's own remaining SDE work for this change is
completed through the newly-redirected `./ros` itself, using it for its
own `work start`/`work complete` cycle, as the final integration proof
before merge.
