---
id: RQ-ROS-2026-A004
title: Canonical artifacts accumulate an append-only contribution history
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-provenance.md
tags: [provenance, artifacts, provenance, contributors]
provenance:
  contributions:
    EXE-20260925T193942361Z-84dc9b22:
      operations: [created, modified]
      at: 2026-09-25T20:16:19.440Z
      last: 2026-09-25T20:23:33.871Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from the agent identity and provenance objective (work item FEAT-AGENT-PROVENANCE)"
---

# Requirement

A canonical artifact MAY carry `provenance.contributions`: a mapping keyed by execution ID (or `CTB-...` for a non-agent contributor outside any execution). Each entry records `operations`, `at`, `actor`, and optionally `reason` and `evidence`. Recording MUST only add entries or extend the recording execution's own entry. It MUST NOT replace, reorder, or re-attribute another contributor. At most one contribution may claim `created`, and nothing may precede it.

## Rationale

Several agents and humans must be able to contribute to one artifact without overwriting each other's provenance, and the original creator must never be displaced by the last modifier.

## Acceptance criteria

- `./ros provenance record` edits the front matter surgically, preserves every other byte and any unmodeled field, verifies by read-back before an atomic write, and is idempotent for an identical call.
- A later recording by the same execution updates that entry's `last` timestamp, so every recorded modification remains visible.
- Re-attributing an execution to a different actor, a second `created`, and a late `created` are all refused.
- Every change appends an `artifact.contributed` event with actor, execution, operation, reason, evidence, lineage, and before/after content SHA-256.

## Verification

- ProvenanceTests: property: recording never loses, reorders, or re-attributes earlier contributions; property: every recorded history serializes to front matter and reads back identically; front-matter writer tests
- ProvenanceEffectTests: recording inherits the execution's identity; re-recording is idempotent; a second agent's modification accumulates
