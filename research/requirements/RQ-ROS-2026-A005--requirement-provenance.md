---
id: RQ-ROS-2026-A005
title: Requirements are first-class records whose creator and modifiers are attributed
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
tags: [provenance, requirements, provenance]
provenance:
  contributions:
    EXE-20260925T193942361Z-84dc9b22:
      operations: [created]
      at: 2026-09-25T20:16:19.872Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from the agent identity and provenance objective (work item FEAT-AGENT-PROVENANCE)"
---

# Requirement

Praxis MUST support requirement records (`RQ-` IDs under `research/requirements/`, with a lifecycle status and a generated registry). An agent that creates a requirement MUST record a `created` contribution, and an agent that materially modifies one MUST record a `modified` contribution. Under an enforced policy, a requirement created on or after the policy date without a recorded originator is a validation error. Human-authored requirements MUST remain representable.

## Rationale

Requirements are the anchor of traceability, so who created each one and who changed it must be answerable later.

## Acceptance criteria

- `RQ` is a canonical artifact kind with statuses draft, proposed, accepted, implemented, verified, deprecated, superseded, and rejected.
- `registries/requirements.json` is optional until a requirement exists, so upgraded repositories are not made stale.
- `ros.json` `provenance.requireOriginator` defaults to `["RQ"]`.

## Verification

- ProvenanceTests: policy: new artifacts must be attributed, requirements must name their originator
- ProvenanceEffectTests: an optional registry kind with no documents is not stale; generated registries carry artifact provenance
- The requirements in this directory carry their own recorded provenance
