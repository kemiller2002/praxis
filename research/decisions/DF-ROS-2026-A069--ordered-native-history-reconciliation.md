---
id: DF-ROS-2026-A069
title: Preserve ordered native histories across prior branch insertions
status: draft
version: 1.1.0
owners: [repository-governance]
created: 2026-10-12
updated: 2026-10-12
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A029]
related_documents: [DF-ROS-2026-A067, docs/work-history-merge.md]
tags: [history, continuity, integration]
confidence: medium
derived_from: [DF-ROS-2026-A067]
provenance:
  contributions:
    EXE-20261011T234058504Z-3b80355e:
      operations: [created, modified]
      at: 2026-10-12T00:43:35.000Z
      last: 2026-10-12T08:09:04.000Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "User-approved extension after strict-prefix refusal; preserve both ordered branch histories"
---

# Context and authorized change

Integrating the independent checklist branch into main for GH-235 exposed a
strict-prefix limitation. All 1,004 base event lines remained byte-identical
and in the same relative order on both branches. Prior integration had inserted
GH-234 observations before later base events. The old native planner refused
this valid ordered history. The user explicitly authorized extending the tool
after that refusal; no native state was hand-edited.

Permit base history as an unchanged ordered subsequence. Every shared event
anchors the merge, and both branches must agree on shared-event order. Merge
independent gaps with local observations first, retaining each branch's order.
This tie specifies serialization, not chronology or causal precedence. Deleted
or rewritten base events, contradictory order, duplicate IDs and divergent
work-item states remain refusals. Preserve all existing Git-stage binding,
bounded input, link checks, reobservation and journal recovery safeguards.

# Validation and migration

Sixteen history tests passed, including exhaustive small permutations, actual
Git insertion conflicts, forged stages, exact byte preservation and interrupted
journal recovery. Read-only preview of the actual integration returned 301
work records and 1,013 events. Following native apply, every original event
line from all three Git stages was verified present, unchanged and in order.

No stored history or schema migration is required. The planner accepts a wider
class of order-compatible histories; it never synthesizes historical events.
Exclusive idle checkout remains required until lifecycle writers share a lock.
The policy change needs review together with full repository and cross-OS CI.

# Multiple real ancestors and auto-merged paths (1.1 proposal)

PR #236 entered main while PR #238 validation ran. The resulting Git graph has
two real common ancestors: b1a7fd92755155ec147640d2250143a4adf2d5d2 and
aa26c0fff24344f19a2f2c4b2fcaaad0134c7a0b. Context auto-merged; events conflicted.
Extend the planner to read one to 32 real ancestors plus actual HEAD/incoming
commits. Never parse a synthetic stage-1 history. Bind conflicted stages 2/3
to actual commits, retaining stage-1 binding for a single real ancestor. Require
every ancestor's events unchanged and ordered in both branches. Divergent known
work-item versions remain ambiguous, never resolved by timestamp.

Auto-merged paths must retain staged content (allowing CRLF/LF checkout conversion)
or equal the exact generated native
write, and their data must equal the authoritative commit-derived plan. This
allows an exact interrupted journal to recover while refusing staged inventions
and working edits. Preserve main's ordering graph, real-commit source authority,
all event bytes, bounded reads, link refusal, reobservation and journal checks.
Twenty-four tests passed locally, including real criss-cross Git merges,
forged/edited auto-merged data, interrupted recovery and contradictory ordering.

# Actual integration refusal and proposed owner choice

Read-only preview of PR #238 head 41299dc1c0e2d7f4a6e55bd8a1325d2051b112f5
against main 4a6c8d1f refused an ordering cycle. The same shared events appear
in opposite serialized orders: GH-235 checkpoint d94bbd8d26543a5e842a69b8
precedes GH-234 checkpoint 2929a5ea8177e461792fd393 on the feature branch,
while main places GH-234's checkpoint before GH-235's checkpoint. Their content
is unchanged. Independent prior merge ties are serialization rather than a
claim of causal precedence, but preserving both complete recorded orders is
mathematically impossible. No native apply was attempted for this cycle.

The proposed owner choice is to retain main's ordering for shared records and
retain every feature-only record, preserving original event bytes and recording
the authorized ordering reconciliation through the native transaction. This
would change the earlier feature branch's serialized order and needs explicit
approval; the current tool deliberately continues to refuse it. No silent
reordering or stored-history migration is authorized by this proposal.
