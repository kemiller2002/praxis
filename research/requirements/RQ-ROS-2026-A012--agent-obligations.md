---
id: RQ-ROS-2026-A012
title: Agent guidance requires identity, honesty, and preservation of provenance
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
tags: [provenance, governance, agents]
provenance:
  contributions:
    EXE-20260925T193942361Z-84dc9b22:
      operations: [created]
      at: 2026-09-25T20:16:22.955Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from the agent identity and provenance objective (work item FEAT-AGENT-PROVENANCE)"
---

# Requirement

Praxis agent guidance (AGENTS.md and the Agent Operating Manual) MUST require every agent, provider-neutrally, to:

1. establish identity at the start of an execution;
2. never impersonate another actor;
3. never fabricate provider, model, or version information;
4. record unknown values as unknown;
5. preserve existing provenance;
6. add its own contribution rather than replace another's;
7. propagate lineage for derived artifacts;
8. attribute requirements it creates;
9. attribute meaningful modifications it makes;
10. make generated evidence, findings, and results traceable to its execution.

## Rationale

Mechanisms only produce provenance when agents are told plainly how and why to use them.

## Acceptance criteria

- AGENTS.md section "Agent Identity and Provenance" lists all ten obligations and ships to installed repositories.
- The Agent Operating Manual startup protocol establishes identity before work begins.

## Verification

- Review of AGENTS.md and docs/00-governance/Agent-Operating-Manual.md
