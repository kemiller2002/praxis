---
id: RQ-ROS-2026-A019
title: Steps remain backward compatible and portable through fallback execution
status: verified
version: 1.0.0
owners: [repository-governance]
created: 2026-09-27
updated: 2026-09-27
research_area: repository-operating-system
priority: high
related_documents: [DF-ROS-2026-A037, requirements/PRAXIS-DUAL-ENTRY-RECONCILIATION.md]
tags: [steps, fallback, reconciliation, instance-identity, compatibility]
provenance:
  contributions:
    EXE-20260927T013711437Z-b3dfcbfc:
      operations: [created, modified]
      at: 2026-09-27T01:55:35.246Z
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

Historical executions with no steps MUST remain valid without synthetic records. The approved fallback envelope MUST additively represent execution identity, ordered step transitions, step telemetry availability, evidence, timestamps, actor identity, and Praxis instance identity; it MUST NOT become a second incompatible format.

Reconciliation MUST preserve step boundaries and provenance, be idempotent, reject contradictory duplicates, and reject a claimed instance identity that conflicts with locally authoritative identity once that authority is available. Praxis MUST remain independently installable and useful when registration or provider telemetry is unavailable.

## Acceptance criteria

- Old execution and fallback fixtures still parse and validate.
- Step-aware fallback input preserves every step boundary and identity.
- Replay cannot duplicate an already-ingested step or observation.
- Missing instance identity remains honestly unavailable during the registration bootstrap period.
