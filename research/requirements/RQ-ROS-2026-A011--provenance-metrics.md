---
id: RQ-ROS-2026-A011
title: Provenance is queryable for per-agent metrics
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
tags: [provenance, metrics, analysis]
provenance:
  contributions:
    EXE-20260925T193942361Z-84dc9b22:
      operations: [created]
      at: 2026-09-25T20:16:22.523Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from the agent identity and provenance objective (work item FEAT-AGENT-PROVENANCE)"
---

# Requirement

Praxis MUST expose provenance as flattened, machine-readable facts. Each fact is one artifact contribution with its actor, execution, operations, time, and origin flag. Praxis MUST also expose per-actor summaries, so that metrics by agent, model, or provider can be computed without a bespoke parser: requirements created or modified, findings, rework, human corrections, agent-to-agent revisions, and evidence produced. It MUST NOT introduce an analytics subsystem.

## Rationale

Future analysis of agent output, rework, and outcomes needs trustworthy attribution facts more than it needs dashboards.

## Acceptance criteria

- `./ros provenance audit --json` emits `contributions`, `byActor`, and coverage counts for artifacts, events, executions, and backlog items.
- Execution IDs join the facts to telemetry cost and duration.

## Verification

- ProvenanceTests: metrics index: contributions by actor across artifacts
