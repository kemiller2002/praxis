---
id: DF-ROS-2026-A069
title: Preserve ordered native histories across prior branch insertions
status: draft
version: 1.0.0
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
      operations: [created]
      at: 2026-10-12T00:43:35.000Z
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
