---
id: EV-ROS-2026-A052
title: "queue.json's own status field never tracked a promoted item's completion, relying on an unrelated later backlog write to self-heal in queue.md"
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-11
updated: 2026-09-11
research_area: repository-operating-system
evidence_type: primary
supports: []
related_documents:
  - src/Ros.Domain/Work/QueuePresentation.fs
  - src/Ros.Infrastructure/Work/FileBacklogQueueRepository.fs
  - src/Ros.Cli/Program.fs
tags: [work-protocol, backlog, sde, bug]
confidence: high
---

# Evidence summary

A user reading `.ros/work/queue.md` in this repository noticed it listed
already-completed work items (`WI-0012`/`WI-0013`) as `ready`, even though
`./ros validate` passed and the live work-protocol state
(`.ros/context/current.json`) showed them `complete`. They correctly
diagnosed the root cause themselves: "the backlog triage lifecycle has no
'complete' transition (`ready`/`block`/`abandon` only), so the two views
diverge" -- flagged as informational, not an error.

Checked directly against this repository's own code: confirmed exact.
`.ros/work/queue.json`'s own `status` field is written only by
backlog-specific effects (`capture`, `backlog-transition`, `update`,
`attach` -- `FileBacklogQueueRepository.fs`), none of which ever run again
once an item is promoted into the live work protocol via `work start`.
`work complete` (`runWorkComplete`, `Ros.Cli/Program.fs`) only ever writes
`.ros/context/current.json`; it never touches `queue.json` at all. So a
promoted item's raw backlog record is frozen at whatever status it had at
promotion time, forever.

`queue.md`'s own rendering is not a naive dump of that frozen field,
though: `QueuePresentation.effectiveStatus` merges the backlog's own
status with the live context's semantic state, letting an
active/blocked/complete live state always win. Critically, `mergedRows`
recomputes this for *every* row in the queue on *every* backlog-affecting
write -- not just the row being touched. That is why, when this was
re-checked in this same session, `queue.md` already showed `WI-0012`/
`WI-0013` as `complete`: several unrelated `./ros add`/`work complete`
calls for later work items (`WI-0052` onward) had each regenerated the
whole projection fresh, incidentally re-deriving the correct answer for
every older id too. The user's own observation was accurate at the moment
they made it; the symptom in `queue.md` is transient (self-heals on the
next unrelated backlog write), while the underlying gap in `queue.json`
itself is permanent until fixed at the source.

# Method

Read `QueuePresentation.fs`'s `effectiveStatus`/`mergedRows` to understand
the merge rule, `FileBacklogQueueRepository.fs` to confirm which commands
ever write `queue.json`'s own `status` field, and `Program.fs`'s
`runWorkComplete` to confirm it never touches the backlog file at all.
Reproduced directly: bootstrapped a fresh project, captured a backlog
item, transitioned it `ready`, promoted it with `work start`, confirmed
`queue.json`'s status was still `ready`, then completed it and confirmed
the raw field never changed (before the fix) / changed to `complete`
(after).

# Findings

- The two-tier design (a `queue.json` backlog record plus a live
  `context/current.json` record, merged for rendering) is intentional and
  sound -- `queue.md` always shows the right answer eventually, it just
  needed *some* later backlog write to trigger the recomputation.
- The actual gap is narrower than "the two views diverge": it is that
  `queue.json`'s own raw file is the one source of truth that never
  self-corrects, since nothing ever writes to it on completion. Everything
  downstream of it (the markdown projection) was already correct by
  design; only the upstream record was stale.
- `"complete"` was never a legal `status` value in `BacklogQueueValidation`'s
  own allow-list (`captured`/`ready`/`blocked`/`abandoned` only, mirroring
  `tools/ros_cli.mjs`'s `BACKLOG_STATUS_VALUES`) -- the first version of
  this fix, before this was caught by running `./ros validate` on its own
  output, wrote a value the schema itself rejected as an error. Confirmed
  every downstream consumer of a backlog item's raw status
  (`parseBacklogState`/`BacklogState.parse` in both F# and Node) already
  handles an unparseable/`"complete"` status correctly on its own --
  falling through to the existing "cannot transition from X" rejection
  path with no code change needed there -- since a promoted item always
  still has a live-context counterpart, the `backlogActions` computation
  that would otherwise call `BacklogState.parse` is structurally
  unreachable for a `"complete"` backlog row in both languages.

# Consequences

Added `FileBacklogQueueRepository.markComplete` (`root`, `id`,
`occurredAt`, `contextItems`): when a live item completes, if the same id
also has a backlog row in `queue.json`, its `status` field is now set to
`complete` (and `updatedAt` refreshed) at the moment of completion, using
the same commit path (regenerate `queue.md`, write both files through the
shared `backlog-state` recovery transaction) every other backlog effect
uses. A no-op, not an error, when the id was never captured to the
backlog at all -- most completing ids are live-only and have no queue.json
row to correct, and completing one must never synthesize a backlog row
for it. Wired into `runWorkComplete`: after the live-context write
succeeds, the backlog sync runs for every completed id, inside the same
`work-protocol` lock the whole command already holds. `"complete"` is
also added to `BacklogQueueValidation`'s allow-list (F#) and
`tools/ros_cli.mjs`'s `BACKLOG_STATUS_VALUES` (Node, validation-parity
only -- Node's own `completeWork` is unchanged, since it is not a
supported CLI path per `DF-ROS-2026-A033`), so `./ros validate` accepts
the value this fix now legitimately writes instead of reporting it as a
schema violation.

# Reversibility and validation

Fully additive and reversible -- removing the new function, its call
site, and the schema-allow-list entries returns to the prior (self-healing
but not source-correct) behavior with no cleanup needed. Verified with
two new tests in `tests/work-complete-fsharp-differential.test.mjs`: one
confirms a promoted backlog item's `queue.json` status becomes `complete`
immediately (not just `queue.md`'s merged rendering), the other confirms
completing a live-only id (never captured to the backlog) leaves
`queue.json` byte-for-byte unchanged; and one new F# unit test in
`QueueValidationTests.fs` confirming `"complete"` no longer trips a schema
finding. This repository's own `.ros/work/queue.json` was used as a live
end-to-end proof: completing this fix's own work item (`WI-0055`) through
the fixed CLI turned its own backlog row `complete` immediately, and
`./ros validate` passed clean afterward. Also manually verified against a
separate scratch bootstrapped project (capture -> ready -> start ->
complete, and a separate live-only start -> complete), matching the
automated tests'
assertions exactly. Full local gate: 57/57 Node (2 new), F# unit and
differential/CLI suites all passing.
