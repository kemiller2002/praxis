---
id: EV-ROS-2026-A055
title: "Durable checkpoints and agent continuity: two-clone agent-loss recovery and verification results"
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-29
updated: 2026-09-29
research_area: repository-operating-system
evidence_type: primary
supports: [RQ-ROS-2026-A022]
related_documents:
  - RQ-ROS-2026-A022
  - DF-ROS-2026-A042
  - tests/Ros.Tests/RecoveryProofTests.fs
  - tests/remote-checkpoint.test.mjs
tags: [continuity, checkpoints, recovery, verification]
confidence: high
derived_from: [RQ-ROS-2026-A022]
provenance:
  contributions:
    EXE-20260929T075409917Z-4a1d9b5a:
      operations: [created]
      at: 2026-09-29T08:02:39.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Verification and two-clone recovery evidence for durable checkpoints (PRAXIS-CONT-10-HARDEN)"
---

# Durable checkpoints: recovery and verification evidence

## What was run

These runs used branch `claude/durable-checkpoints-continuity-ywqs3w` on
2026-09-29. Every result below was observed in the run's output.

| Suite | Command | Result |
|---|---|---|
| F# unit, infrastructure, CLI, guard, continuation and recovery | `dotnet tests/Ros.Tests/bin/Release/net10.0/Ros.Tests.dll` | 666 passed, 0 failed |
| F#/Node differential and remote suites, including `tests/remote-checkpoint.test.mjs` | the `node --test` half of `npm run test:fsharp` | 253 passed, 0 failed |
| Node lifecycle, bootstrap, server and hub | `npm test` (node) | 104 passed, 0 failed |
| Python | `npm test` (unittest) | 7 passed |
| Structural validation | `./ros validate`, and `ROS_BASE_REF=origin/main ./ros validate` | passed |

## The two-clone agent-loss proof

`tests/Ros.Tests/RecoveryProofTests.fs` runs the real CLI against a
temporary bare remote with enforcement on. It has two independent clones:
`clone-a` and `clone-b`.

**Executor A** runs with the fixture identity `anthropic/claude-code` and
session `claude-session-A`. In `clone-a` only, A:

1. starts `FEAT-42`;
2. commits and pushes meaningful work;
3. records `tests.passed` as `external-tool` telemetry;
4. records a durable checkpoint and asserts that Praxis recorded the exact
   commit and remote branch;
5. pushes the Praxis state.

**Loss.** `clone-a` is then deleted from disk.

**Executor B** runs with the fixture identity `openai/codex` and session
`codex-thread-B`. In `clone-b` only, B:

1. fetches;
2. discovers the single active item, the branch, and the exact checkpoint
   commit from Praxis state;
3. confirms the commit with `git ls-remote`;
4. runs `work continue`, which gives it a new execution whose parent is A's,
   and sees A's summary, next action and recorded test evidence;
5. commits, pushes and checkpoints C2;
6. completes under the enforced durable-completion guard.

**Assertions:**

- B's recovered commit is A's recorded checkpoint commit.
- A and B keep separate provider and session identities, and B's execution
  names A's as its parent.
- History contains both checkpoints, each attributed to its own actor and
  execution.
- The item is complete.
- The final checkpoint is the remote branch head, modulo Praxis-state
  commits.
- `validate` passes with `ROS_BASE_REF` at the install commit and no active
  work: attribution comes from the checkpoint events' paths.
- Clone A no longer exists.

**Result:** passed.

## Remote contract

`tests/remote-checkpoint.test.mjs` drives two runtime-less agents through
`praxis remote execute` over protocol 1.3. The GitHub adapter's
persist-and-push step is emulated. The sequence is:

1. `praxis.describe`;
2. `work.start`;
3. `work.checkpoint` of code pushed through the host, including an
   identical-request replay and a `stale-ref` refusal;
4. `work.context` read by the second agent;
5. `work.continue`, which gives the second agent a new execution with the
   predecessor recorded as interrupted;
6. `work.checkpoint`;
7. `work.complete`.

**Result:** passed.

## Dogfooding

Every implementation item `PRAXIS-CONT-01` through `PRAXIS-CONT-09`
followed the same order: its code was committed and pushed, a verified
durable checkpoint was recorded against `origin`, and then the item was
completed. From `PRAXIS-CONT-05` onward this ran under this repository's
enforced guard. The first real checkpoint was `ae29e8dee93e04908dba4070`.

## Limits of this evidence

- Protocol 1.3 has not run live through `praxis-remote.yml`. That needs a
  published release containing it, pinned in `.echelon/toolchain.json`.
- The identities in the proof are fixture declarations, not real provider
  sessions.
