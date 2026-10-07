---
id: DF-ROS-2026-A008
title: Repository-local work backlog
status: accepted
version: 1.0.0
confidence: medium-high
created: 2026-08-19
updated: 2026-08-19
owners: [repository-governance]
related_documents: [docs/work-protocol.md, docs/ROADMAP-STATUS.md, research/decisions/DF-ROS-2026-A006--provider-neutral-work-protocol.md, research/decisions/DF-ROS-2026-A007--external-work-adapter-contract.md]
supersedes: []
superseded_by: []
tags: [work-protocol, backlog, project-management]
---

# Decision

ROS adds a repository-local backlog (`ros add`, `.ros/work/queue.json`, and
the `ros work list|ready|show|start|block|abandon|done` commands) for
capturing and triaging outstanding repository work before it enters the
existing, externally-authoritative in-flight protocol (`work begin
--block--resume--complete`, `.ros/context/current.json`,
`.ros/events/events.jsonl`).

The backlog has its own small local lifecycle -- `captured -> ready ->
{blocked, abandoned}` -- with auto-generated stable IDs (`WI-0001`, ...) and
free-form tags. `ros work start` is the single graduation point: it requires
`ready`, then delegates to the existing `begin` transition unchanged. From
that moment the in-flight record is authoritative; the merged view
(`effectiveStatus` in `tools/ros_cli.mjs`) always prefers the live context
state over the backlog's own `status` field, so there is exactly one
authoritative record per ID at any time, never two.

## Rationale and alternatives

`prompts/fix2.md` asked for a self-contained local work queue that owns
work-item identity and creation end to end -- auto IDs, a full
`captured/ready/active/done` lifecycle, and CLI ergonomics
(`ros add`, `ros work ready`) with no external system required. That
conflicts directly with [[DF-ROS-2026-A006]] and
[[DF-ROS-2026-A007]] and with `docs/ROADMAP-STATUS.md`, which mark
project-management/work-item ownership as Phase 5, explicitly external and
deferred to a separate repository, and with the README's ownership table,
which assigns "repository registration, portfolio data" etc. to the central
reporting repository, not ROS core.

Fully implementing fix2.md as written was rejected: it would make ROS itself
an authority for in-flight work-item state, duplicating
[[DF-ROS-2026-A006]]'s "external system owns work-item truth" boundary and
reopening a question this repository had already closed.

Doing nothing was also rejected: fix2.md's actual pain point -- capture is
expensive today, since `work begin ID` requires an operator to already have
an externally-assigned ID -- is real and does not require external
authority to fix. A pre-execution backlog is local-only by construction
(nothing to reconcile with an external system) and directly serves
[[DF-ROS-2026-A006]]'s existing goal of a cheap local protocol.

The chosen scope: add the backlog as a staging layer that graduates into the
existing protocol rather than replacing or parallelling it. This keeps
[[DF-ROS-2026-A006]] and [[DF-ROS-2026-A007]] intact; nothing in this
decision changes `getWorkItem`/`transitionWorkItem`/`publishRepositoryEvent`,
the adapter contract, or event schemas.

## Consequences

- New storage: `.ros/work/queue.json` (canonical) and a generated
  `.ros/work/queue.md` projection; both are added to the default
  `ignoredPaths` for attribution enforcement, matching `.ros/context/**`
  and `.ros/events/**` -- backlog bookkeeping is not itself a "meaningful"
  application change requiring separate work-item evidence.
- `ros work block`/`ros work ready` now dispatch per-ID to either the
  backlog store or the existing in-flight transition, resolved by where the
  ID currently lives; existing invocations against already-tracked IDs are
  unaffected (verified by the pre-existing `tests/work-protocol.test.mjs`
  suite, unchanged and passing).
- `ros work begin`/`resume`/`complete` and the adapter contract are
  unmodified. `ros work done` is a pure alias for `complete`.
- No daemon integration was added: no execution daemon exists in this
  repository (`docs/ROADMAP-STATUS.md` keeps daemon/model-routing work in a
  separate repository), so fix2.md section 23 does not apply here.
- No migration was performed: no prior local backlog existed to migrate
  from; this is a purely additive layer.

## Validation and evolution

Tests cover: auto-ID capture and explicit-ID collisions against both the
backlog and the in-flight context; `list`/`ready` filtering by tag and
status; the `captured -> ready -> start` gate (and that `start` refuses an
item that hasn't been marked `ready`, and refuses one that was `abandoned`);
`block` dispatching correctly within one call across a mixed backlog/in-flight
ID set; the generated markdown projection; and that backlog bookkeeping does
not itself trip attribution enforcement. Extending the backlog to a full
external-authority replacement, or adding daemon consumption, would each
need their own decision record rather than silently expanding this one.
