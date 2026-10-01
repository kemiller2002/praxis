---
id: RQ-ROS-2026-A017
title: Step evidence uses references with contribution-safe provenance
status: verified
version: 1.0.0
owners: [repository-governance]
created: 2026-09-27
updated: 2026-09-27
research_area: repository-operating-system
priority: high
related_documents: [DF-ROS-2026-A036, DF-ROS-2026-A037]
tags: [steps, evidence, provenance, engineering]
provenance:
  contributions:
    EXE-20260927T013711437Z-b3dfcbfc:
      operations: [created, modified]
      at: 2026-09-27T01:55:34.823Z
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

Steps MUST be capable of referencing observed files, changed files, commits, commands, tests, validations, evidence, decisions, requirements, and outcomes without duplicating their canonical records. Every relationship MUST preserve whether it was agent-reported or independently observed.

Praxis MUST NOT infer that a step authored another contributor's pre-existing change merely because the step inspected or touched the file. References are provenance, not attestation.

## Acceptance criteria

- Supported relationship kinds serialize deterministically.
- Cooperative claims identify their source.
- Step recording does not rewrite referenced canonical evidence.
