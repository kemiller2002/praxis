---
id: RQ-ROS-2026-A015
title: Step usage telemetry preserves availability and derivation
status: verified
version: 1.0.0
owners: [repository-governance]
created: 2026-09-27
updated: 2026-09-27
research_area: repository-operating-system
priority: high
related_documents: [DF-ROS-2026-A010, DF-ROS-2026-A037]
tags: [telemetry, tokens, checkpoints, quality]
provenance:
  contributions:
    EXE-20260927T013711437Z-b3dfcbfc:
      operations: [created, modified]
      at: 2026-09-27T01:55:34.399Z
      last: 2026-09-27T07:23:55Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "First-class step-level execution telemetry requirements and architecture"
---

# Requirement

Praxis MUST support provider-neutral step attribution for input, output, cached-input, cached-output, reasoning, and total tokens when evidence exists. Zero MUST remain distinct from unavailable. Measurement quality and capability state MUST retain the established observed/reported, derived/calculated, estimated, unavailable, unsupported, and unknown distinctions without inventing values.

For reliable cumulative counters, Praxis MUST preserve begin and end observations, derive a delta only from compatible sufficient observations, and record the source measurement identities and derivation. Provider-specific fields MUST remain available through sanitized raw telemetry. Exact step attribution MUST be declared unavailable when evidence is insufficient.

## Acceptance criteria

- Measured, zero, unavailable, and unsupported cases remain distinguishable.
- Compatible checkpoints can produce a provenance-linked delta.
- Missing, decreasing, or incompatible checkpoints do not produce a delta.
- Sanitized provider-specific raw observations can be related to a step.
