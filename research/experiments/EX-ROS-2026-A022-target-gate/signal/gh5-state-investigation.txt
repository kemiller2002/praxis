# Signal GH-5 lifecycle-state investigation

Investigated under `PRAXIS-PLAN-EXP-03` on 2026-10-02, separately from the
EX-ROS-2026-A022 gate. Nothing in `kemiller2002/signal` was changed.

Repository: `kemiller2002/signal`, `main` at
`637218a14179c9ac4f1ab1333c75d03f9df6d797` (unchanged during the
investigation). Signal pins ROS `1.2.1-main.16.1` in `ros.json` and records
installed version `3.1.4` in `.echelon/ros.json`. Read with Praxis 3.6.0, always
against a throwaway copy.

## Reported discrepancy

`.ros/work/queue.md` lists GH-5 ("Prepare Signal implementation baseline") as
`ready`; `.ros/context/current.json` lists it as `active`.

## Observations

| Source | GH-5 state | Last written |
| --- | --- | --- |
| `.ros/work/queue.json` (backlog record) | `ready` | commit `f1ae868`, 2026-09-22 22:12:03Z, "Begin GH-5 readiness work" |
| `.ros/work/queue.md` (generated projection) | `ready` | same commit |
| `.ros/context/current.json` (live record) | `active`, type `feature`, no evidence | same commit |
| `.ros/events/events.jsonl` | one `work.started` at 2026-09-22T22:12:02Z; no later GH-5 event | same commit |
| Telemetry `EXE-20260922T221202958Z-57a84c17` | `status: active`, `finalizedAt: null` | same commit |
| `praxis work list --json` | `active` (allowed: abandon, block, complete) | derived |
| `praxis work ready` | `[]` | derived |
| `praxis work checkpoint show GH-5` | "none recorded"; WARNING: active work has no durable checkpoint | derived |
| GitHub issue #5 | open, no closing pull request | 2026-09-22T22:11:22Z |

Capture, readiness and start all happened within one second (backlog
`createdAt` 22:12:02.322Z, start 22:12:02Z) and were committed together.
Nothing on `main` has touched GH-5 since.

### Unmerged GH-5 implementation

Branch `work/gh-5-readiness` (head `0c018eb`) is 20 commits ahead of and 13
behind `main`. Its commits (2026-09-22 22:18Z to 22:26Z, owner account,
mostly one file per commit, plus a `github-actions[bot]` lockfile commit) add:

- a four-tier F# skeleton (`Echelon.Signal.Semantic`, `.Engine`,
  `.Application`, `.Host.Wasm`) and a test project;
- `docs/implementation/FIRST-VERTICAL-SLICE.md` ("Publish, Respond, Score");
- rewritten `context/CURRENT-STATE.md` and `context/ARCHITECTURE.md`;
- `scripts/verify-application.sh`, two CI workflows and pinned dependencies.

It does not touch `.ros/`: there is no checkpoint, completion or evidence for
GH-5. No pull request has ever been opened from it (open: #6 CI batching;
closed: #1 to #4).

Running `scripts/verify-application.sh` on a scratch worktree of `0c018eb`
(.NET SDK 10.0.112 via `rollForward`) passes: the Wasm host builds,
"PASS: Signal first vertical slice", "Limen verification passed (strict)" and
ROS 3.1.4 "validation passed". The worktree was removed afterwards.

## Cause of the queue.md / current.json difference

**Projection lag. Neither file is corrupt, and no transition is incomplete.**

`queue.md` is regenerated only when the backlog queue itself is written
(capture, update, ready/block/abandon; `FileBacklogQueueRepository`). It
renders `QueuePresentation.effectiveStatus`, in which a live
active/blocked/complete state wins. `work start` writes only the live context,
so the projection rendered at the `ready` transition stays on disk until some
later backlog write re-renders it. The raw `queue.json` status also remains
`ready`, by design: from promotion on, the live record is authoritative
(`docs/work-protocol.md`, "Local backlog").

Reproduced in a fresh repository with Praxis 3.6.0: capture, then ready, then
`work start` leaves `queue.md` and `queue.json` at `ready` while `work list`
reports `active`. The behavior is therefore current, not an artifact of
Signal's older install. It is filed as Praxis backlog item
`PRAXIS-QUEUE-MD-LIVE-STATE`.

Classification against the candidate causes:

| Candidate cause | Finding |
| --- | --- |
| stale generated documentation | **yes**: `queue.md` lags the live state by design |
| stale materialized state | no: `current.json` is the authority and is current |
| incomplete transition | no: `work.started` was recorded and committed |
| interrupted execution | **yes, at the work level**: execution open 10 days, no checkpoint, implementation stranded on an unmerged branch |
| incorrect handoff | **partly**: `HANDOFF.md` and `context/CURRENT-STATE.md` on `main` are the pre-GH-5 bootstrap text; the updated text exists only on the unmerged branch |
| parser/view inconsistency | no: every Praxis reader applies the same `effectiveStatus` |

## Canonical state

GH-5 is **active**. It has one open execution, no durable checkpoint, and its
implementation exists only on `work/gh-5-readiness`.

## Can it continue?

Yes, but only as a deliberate owner-level step, not as a mechanical repair:

1. A successor would run `./ros work continue --id GH-5` under its own
   identity. The predecessor execution is then recorded as interrupted.
   Because there is no checkpoint, the successor must first decide from
   branch history that `0c018eb` is the intended recovery point.
2. It would merge `main` into `work/gh-5-readiness` (13 commits, including
   the Aegis/Forma/Folio foundation contract and CI enforcement), re-run
   `scripts/verify-application.sh`, push, checkpoint, open a pull request
   that closes issue #5, and complete GH-5 with evidence after merge.

No repair was performed. GH-5's recorded `active` state is correct. The
`queue.md` lag is a Praxis behavior that self-corrects at Signal's next
backlog write and must not be hand-edited. Completing GH-5 means integrating
the owner's unmerged baseline into `main`, which is a product decision for
the owner.

## What must happen before WI-0002 to WI-0020 become implementable

1. GH-5's baseline is merged and GH-5 completes, giving the code skeleton and
   accepted first slice that the backlog items extend.
2. Each item is triaged `captured` to `ready` through
   `./ros work backlog-transition --action ready` on its own merits. The
   item records also expect expansion to follow the slice's order (template
   storage, persistence, selector/response contracts, scoring, results, ...).
3. Signal's own `HANDOFF.md` and current state stop describing a pre-charter
   bootstrap.

WI-0002 to WI-0020 remain `captured`. None was promoted, edited or split
here. Readying them to satisfy an experiment would be the padding
EX-ROS-2026-A022 forbids.
