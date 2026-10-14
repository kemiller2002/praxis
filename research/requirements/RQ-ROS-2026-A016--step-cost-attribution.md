---
id: RQ-ROS-2026-A016
title: Step cost retains method and pricing provenance
status: verified
version: 1.0.0
owners: [repository-governance]
created: 2026-09-27
updated: 2026-09-27
research_area: repository-operating-system
priority: high
related_documents: [DF-ROS-2026-A037, RQ-ROS-2026-A015]
tags: [telemetry, cost, pricing, provenance]
provenance:
  contributions:
    EXE-20260927T013711437Z-b3dfcbfc:
      operations: [created, modified]
      at: 2026-09-27T01:55:34.610Z
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

Praxis MUST distinguish provider-reported, calculated, estimated, unavailable, unsupported, and unknown step cost. A recorded cost MUST preserve amount, currency, quality, calculation method, pricing source/version/effective date when applicable, model, and the token measurements used by a calculation.

Pricing MUST be versioned evidence rather than hard-coded domain logic, and calculated or estimated cost MUST never be presented as provider-reported.

## Acceptance criteria

- Cost quality and availability survive serialization and query.
- Calculated cost requires pricing provenance.
- No current model price is embedded as an unversioned domain constant.
