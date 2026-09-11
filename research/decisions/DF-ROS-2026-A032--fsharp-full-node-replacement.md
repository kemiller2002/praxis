---
id: DF-ROS-2026-A032
title: Full Node replacement is the accepted end-state; starter template ./ros becomes F#
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
  - DF-ROS-2026-A029
  - DF-ROS-2026-A030
  - DF-ROS-2026-A031
  - AGENTS.md
  - PACKAGE-USAGE.md
supersedes:
  - DF-ROS-2026-A031
superseded_by: []
tags: [architecture, fsharp, migration, distribution, sde, decision]
confidence: high
---

# Context

The user stated, directly and without qualification: "F# is the successor
to node. The goal is to replace node completely with F#." This supersedes
`DF-ROS-2026-A031`'s narrower framing, which deliberately kept the
starter/greenfield template's `./ros` on Node and added F# only as a
separate, additive `ros-fs` entrypoint — a scope choice made because the
question of full replacement had not yet been put this plainly. It now has
been.

Full replacement touches four things, of increasing risk and irreversibility:

1. **This repository's own `./ros` dispatch** — already F# (`DF-ROS-2026-A030`).
2. **The npm-distributed starter template's `./ros`** (what other projects
   get via `npx ros-bootstrap init`) — still Node before this record.
3. **This repository's own verification methodology** — roughly a third of
   this migration's ~185 differential/smoke tests spawn the bootstrapped
   `./ros` as a subprocess to get "real Node behavior" for comparison; if
   the file `./ros` resolves to stops being Node, those tests silently stop
   testing what they claim to.
4. **Node's own source implementation** (`tools/ros_cli.mjs`,
   `ros_git.mjs`, `ros_telemetry.mjs`, `ros_persistence.mjs`, both in this
   repository and in what gets scaffolded) — still present and untouched;
   roughly twenty more differential test files import functions from these
   modules directly as their comparison oracle.

Items 1 and 2 are safely reversible engineering changes. Item 4 is not: once
Node's own implementation is deleted, there is no live oracle left to
differential-test against, and every one of those ~20 test files would need
converting to a fixed-expectation ("golden master") style before deletion
could happen without silently losing verification coverage. That is a large,
separate, higher-risk undertaking this record does not attempt.

# Decision

**Phase 1 (this record, executed now):** the starter/greenfield template's
`./ros` becomes the F# launcher (the same acquire-verify-cache-exec shim
`DF-ROS-2026-A031` added as the separate `ros-fs` file), replacing Node as
what every newly-bootstrapped project runs by default. `DF-ROS-2026-A031`'s
separate `ros-fs` entrypoint is retired as redundant now that `ros` itself
is F#. `tools/ros_cli.mjs` and its companions remain scaffolded into new
projects unchanged, matching this repository's own rollback pattern from
`DF-ROS-2026-A030` — present, fully intact, just no longer what `./ros`
invokes.

Making this safe required fixing every test whose Node-comparison side
depended on spawning the bootstrapped `./ros`: `adapter-call`,
`adapter-publish`, `status`, `work-list`, `work-context`,
`validate-unified`, `telemetry-start`, `telemetry-validate`,
`work-telemetry`, `work-resume-parent-execution` (differential tests), plus
`npm-bootstrap.test.mjs`, `telemetry.test.mjs`, and `work-protocol.test.mjs`
(Node's own test suite). Each now invokes `node tools/ros_cli.mjs` directly
for its "production" side instead of the bootstrapped `./ros`, preserving
the exact same comparison — Node's implementation has not moved or changed,
only what `./ros` itself dispatches to.

**Phase 2 (named here, not attempted now):** actually delete Node's source
implementation, in this repository and in the starter template, and
retire or convert every differential test that currently imports Node
functions directly. This needs its own future work: each of those ~20
test files' "F# matches Node" assertions must first become "F# matches
this specific, hand-verified expected output" (a golden master captured
from Node's last-known-correct behavior) before Node's source can be
deleted without a real loss of regression coverage. Attempting this in the
same pass as Phase 1 would conflate a safe, mechanical dispatch change with
a much larger, higher-risk rewrite of this migration's entire verification
suite, and is explicitly out of scope for this record.

# Alternatives

- **Do only Phase 1 and stop there indefinitely:** rejected as the
  permanent end-state — the user's own instruction is unambiguous that
  Node should be replaced completely, not merely bypassed by default while
  its source lingers forever. Phase 2 remains a real, tracked obligation,
  not an implicit "good enough."
- **Attempt Phase 1 and Phase 2 together in one change:** rejected. Phase
  2's own scope (converting ~20 test files' verification strategy) is
  large enough to deserve its own careful, incremental treatment, following
  this migration's own established discipline of small, tested increments
  rather than one unbounded rewrite.
- **Delete Node's source now and accept reduced test coverage
  temporarily:** rejected. This would leave real regressions
  undetectable for however long Phase 2 takes to complete properly,
  violating this repository's own Engineering Standards around
  proportional testing.

# Consequences

Every newly-bootstrapped project (via `npx ros-bootstrap init`) now gets an
F#-administered `./ros` by default, matching this repository's own
Phase C switch. `ros-fs` (the separate entrypoint `DF-ROS-2026-A031`
added) is removed from the starter template; anything that adopted it in
the brief window it existed should switch to `./ros`, which now does the
same thing. Thirteen test files change their Node-comparison mechanism
(spawn `tools/ros_cli.mjs` directly) with no change to what they verify.
Node's source remains fully present and untouched, in both this repository
and the starter template, as Phase 2's future starting point and this
record's own rollback path in the interim.

# Reversibility and validation

Phase 1 is fully reversible: `git checkout` the `starter/greenfield/ros`
file and `starter/greenfield/manifest.json` back, and remove
`starter/greenfield/tools/ros_fs_launcher.mjs`, to restore the prior
Node-scaffolding behavior exactly, since Node's own template files are
untouched. The thirteen adjusted test files revert the same way. Validation
is the full test gate (Node, Python, F# unit, differential) passing
unchanged in count and outcome, plus a real end-to-end bootstrap of a fresh
project exercising its scaffolded `./ros` against a real self-contained
binary. Phase 2's own reversibility and validation bar will be set when
that record is written; it is not decided here.
