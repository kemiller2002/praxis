---
id: RQ-ROS-2026-A011
title: Agents establish, preserve, and propagate provenance
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-identity-and-provenance.md
tags: [agents, governance, instructions]
provenance:
  contributions:
    - operation: "created"
      at: "2026-09-25T18:16:43.440Z"
      actor:
        kind: "agent"
        id: "claude-code"
        provider: "anthropic"
        model: "unknown"
        runtime: "claude-code"
        executionId: "EXE-20260925T174822557Z-7d2ee77c"
        sessionId: "0cb6dd40-ee60-5a05-8f85-35e2927960fe"
        assurance: "self-reported"
      reason: "formal requirement for agent identity and provenance (WI-0070)"
---

# Agents establish, preserve, and propagate provenance

Provider-neutral agent guidance (`AGENTS.md`, the Agent Operating Manual) MUST require every agent working under Praxis to:

1. establish its identity at the start of a Praxis-governed execution and confirm it with `identity`;
2. never impersonate another agent or human;
3. never fabricate model, provider, or version information;
4. record unknown values as `unknown`;
5. preserve existing provenance;
6. add its own contribution rather than replacing another contributor's;
7. propagate provenance and `derived_from` lineage when creating derived artifacts;
8. attribute requirements it creates;
9. attribute meaningful modifications it makes;
10. ensure generated evidence, findings, and results trace to its execution.

## Verification

- Review of `AGENTS.md` and `docs/00-governance/Agent-Operating-Manual.md`; enforced mechanically by `RQ-ROS-2026-A006`.
