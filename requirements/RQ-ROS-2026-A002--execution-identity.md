---
id: RQ-ROS-2026-A002
title: Execution identity is established once per run and inherited by every record
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-identity-and-provenance.md
tags: [identity, execution, telemetry]
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

# Execution identity is established once per run and inherited by every record

1. The Praxis execution (the telemetry execution record `EXE-...` that `work begin` creates) MUST be the execution identity. Its record MUST carry the run's own `actor`, whose `executionId` equals the record's `executionId`.
2. An agent MUST be able to establish identity once per execution through its environment (`ROS_ACTOR_KIND`, `ROS_ACTOR`, and the whitelisted runtime variables Praxis already discovers). Downstream commands MUST inherit it without the agent repeating identity on each command.
3. Every command that records work MUST resolve the current actor and bind it to the most recent *active* execution whose recorded identity is compatible (provider, runtime, session, and agent id agree wherever both are known). `ROS_EXECUTION_ID` MUST pin the binding explicitly.
4. A different agent, a different session of the same agent, or an unidentified process MUST NOT inherit another execution; such a process records `executionId: unknown`.
5. Two executions of the same agent MUST NOT share an execution identity, while sharing the same stable identity.
6. `identity` MUST show the actor Praxis will record, how its execution was bound, and that it is self-reported provenance.

## Verification

- `ProvenanceTests.fs`: execution binding, incompatible-session/agent/unknown cases, two runs of one agent.
- `provenance-cli.test.mjs`: execution record actor, event binding, two sessions, and a handover to a different agent.
