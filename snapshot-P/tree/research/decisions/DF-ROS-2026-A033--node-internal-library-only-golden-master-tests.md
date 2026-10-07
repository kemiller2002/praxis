---
id: DF-ROS-2026-A033
title: Node retained as internal-library-only; test suite stops treating it as a live CLI oracle
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
  - DF-ROS-2026-A028
  - DF-ROS-2026-A030
  - DF-ROS-2026-A031
  - DF-ROS-2026-A032
  - AGENTS.md
  - PACKAGE-USAGE.md
supersedes: []
superseded_by: []
tags: [architecture, fsharp, migration, testing, sde, decision]
confidence: high
---

# Context

`DF-ROS-2026-A032` named Phase 2 of full Node replacement — delete Node's
source implementation (`tools/ros_cli.mjs`, `ros_git.mjs`,
`ros_telemetry.mjs`, `ros_persistence.mjs`) in this repository and in the
starter template, after converting the roughly twenty differential test
files that import its functions directly into golden-master tests first —
but explicitly did not attempt it. The user then confirmed: "Confirm and do
phase 2."

Attempting it surfaced a real blocker before any deletion happened.
`tools/ros_server.mjs` — the `project-administration` starter profile's web
UI HTTP backend, a separate, permanently out-of-scope feature this
migration never touches (this repository's own `STATUS.md` classifies
HTTP/UI contracts and the hub as permanently-retained Node) — imports
`ros_cli.mjs`'s functions directly, in-process:
`tools/ros_server.mjs:25: } from "./ros_cli.mjs";`. `tools/ros_hub_cli.mjs`
and `ros_hub_server.mjs` depend on the same module family for the hub
feature. Deleting Node's source wholesale, as originally planned, would
have broken both.

Presented with this, the user chose: keep the four modules as an internal
library only. Node's CLI entrypoint stays fully retired everywhere (already
true — this repository's own `./ros` and the npm starter template's `./ros`
are both F#, per `DF-ROS-2026-A030`/`DF-ROS-2026-A032`). The modules stay in
the repository, but only as the web/hub servers' internal dependency — no
longer documented or scaffolded as "the CLI" or a rollback path — and the
test suite stops treating Node as a live oracle.

# Decision

1. **Node's source is not deleted.** It remains, unchanged, in this
   repository and in the `starter/project-administration` profile (which
   scaffolds `ros_server.mjs`/`ros_hub_cli.mjs`/`ros_hub_server.mjs` and
   therefore needs it). It is dropped entirely from
   `starter/greenfield/manifest.json` — a greenfield-bootstrapped project
   has no feature that needs it, and it is no longer characterized as a CLI
   rollback path for that profile.
2. **Every canonical doc** (`AGENTS.md`, `PACKAGE-USAGE.md`, `STATUS.md`,
   `ROADMAP.md`, `ARCHITECTURE.md`, `docs/features/bootstrap-distribution/manifest.md`)
   re-characterizes these four modules as the web/hub servers' in-process
   internal library dependency, not as a CLI or a rollback path.
3. **The differential/CLI test suite stops executing Node at test time.**
   Every test that previously computed an "expected" value by importing
   Node's functions directly, or by spawning `node tools/ros_cli.mjs`
   (against either this repository's own source tree or a copy scaffolded
   into a disposable bootstrapped fixture), now compares the real F# CLI's
   output against a golden-master literal: the same expected value,
   captured once from that same real Node behavior, frozen as a constant in
   the test file. Node is not executed by any of these tests anymore. This
   also resolves a second-order break: the ~15 tests that spawned Node
   against a bootstrapped fixture's own copy of `tools/ros_cli.mjs` would
   have failed outright once `greenfield`'s manifest stopped scaffolding
   that file; golden-master conversion fixes both problems in the same
   pass, since the setup steps that used to go through Node's CLI now go
   through F#'s.
4. **`tests/work-protocol.test.mjs` and `tests/telemetry.test.mjs` are
   deleted outright**, not converted. Both spawn `node tools/ros_cli.mjs`
   directly and assert only on Node's own CLI dispatch behavior, with no F#
   comparison at all — the last place the suite ran Node as a live CLI for
   its own sake. Nothing in production invokes that entrypoint anymore
   (this repository's `./ros`, the starter template's `./ros`, and
   `ros_server.mjs`'s in-process calls all bypass it), so this coverage
   protected a dead code path. The underlying business logic they exercised
   is already covered by the golden-master differential tests and by
   `tests/Ros.Tests/*.fs`'s native F# unit tests. Confirmed with the user
   before deleting, given the size and history of this suite.
5. **`tools/artifact-fsharp-differential.test.mjs`'s two direct
   `parseGitStatus`/`observeGitStatus` unit tests (in
   `git-fsharp-differential.test.mjs`)** and the `registry check`
   cross-check in `artifact-fsharp-differential.test.mjs`'s own repository
   smoke test are kept unchanged: they exercise Node's retained library
   code directly, for its own sake, not as an oracle whose output an F#
   assertion is derived from — the same distinction this record draws
   everywhere else.

A real, independent bug surfaced by this work and fixed in passing:
`ros_hub_cli.mjs`'s `createWorkInRepo` passed `--file` inline on its `add`
call. Node's CLI accepts that; F#'s `add` deliberately does not (its own
documented equivalent effect is the separate `work attach` command). This
was invisible before because `tests/ros-hub.test.mjs`'s delegator pre-seed
stood in with Node regardless of which backend a real spoke would run.
Once wired to a spoke's *real* F# CLI — matching production, where every
spoke's `./ros` is F# by `DF-ROS-2026-A032` — this broke file attachment
through the hub for real. Fixed by having `createWorkInRepo` issue a
follow-up `work attach` call (and read the final item back via
`work show`, since `work attach`'s own stdout is a queue-row projection
without attachments) when files are given, a shape that works identically
against either backend.

# Alternatives

- **Delete Node's source now regardless, and accept the web UI breaking:**
  rejected outright — the web UI is explicitly out of this migration's
  scope, and breaking a working, unrelated feature to satisfy an aesthetic
  preference for a clean deletion is not a reasonable trade.
- **Port `ros_server.mjs`/`ros_hub_cli.mjs`/`ros_hub_server.mjs` to call F#
  instead, then delete Node's source:** rejected as a large, unscoped
  expansion of this migration into a feature this repository's own
  `STATUS.md` already classifies as permanently retained Node — not
  something the user asked for, and not free (the web server calls these
  functions in-process; F# would need a process-boundary or library
  redesign to replace that).
- **Leave the differential tests importing/spawning Node unchanged, only
  drop the greenfield manifest entries:** rejected — this reconciles
  nothing for the ~15 tests whose Node call targeted a bootstrapped
  fixture's own copy (they would simply fail once that copy stops being
  scaffolded), and leaves the broader "Node as live oracle" pattern in
  place for the ~20+ import-based tests, which was the user's explicit
  concern.

# Consequences

Node's CLI is retired everywhere it ever ran as a CLI; its source lives on,
unchanged, solely as the web/hub feature's internal library, in this
repository and in the `project-administration` starter profile.
`greenfield`-bootstrapped projects no longer receive it at all. The test
suite no longer executes Node as a process for any comparison purpose;
`work-protocol.test.mjs`/`telemetry.test.mjs` are gone. A latent hub bug
(file attachments silently lost against a real F#-backed spoke) is fixed.
Every differential/CLI test file's own verification claim — "F# matches
production" — is now anchored to a frozen, real, once-captured value rather
than a live re-derivation, matching how this migration's own `EV-ROS-2026-A047`
already treats Phase A's closure as settled, not open-ended.

# Reversibility and validation

Reversible in the ordinary sense any test or manifest change is: each
golden-master literal was captured from real, unmodified Node behavior and
can be regenerated the same way if ever needed; `git revert` restores the
prior spawn/import mechanism and the greenfield manifest entries exactly.
Node's own source is untouched throughout, so nothing about its behavior is
at risk of drift from this change. Validation is the full gate (Node,
Python, F# unit, differential/CLI) passing with every converted file
re-verified individually before integration, plus a real bootstrap of a
fresh greenfield project confirming `tools/ros_cli.mjs` is absent and the
scaffolded `./ros` still validates cleanly, and a real
`project-administration` bootstrap confirming the hub's file-attachment fix
against its own real F#-backed spoke.
