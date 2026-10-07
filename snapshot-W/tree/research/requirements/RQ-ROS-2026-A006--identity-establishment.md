---
id: RQ-ROS-2026-A006
title: An agent establishes identity once per execution and later operations inherit it
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
tags: [provenance, identity, developer-experience, impersonation]
provenance:
  contributions:
    EXE-20260925T193942361Z-84dc9b22:
      operations: [created, modified]
      at: 2026-09-25T20:16:20.303Z
      last: 2026-09-25T20:34:17.189Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from the agent identity and provenance objective (work item FEAT-AGENT-PROVENANCE)"
---

# Requirement

An agent MUST be able to establish its identity once per execution, through whitelisted environment variables, known-runtime detection, or identity flags on `work begin`. Later provenance operations MUST inherit that identity from the execution record without repeating it. The recording process MUST itself have an identity to inherit an execution implicitly; a process with none must name the execution explicitly. Its identity MUST agree with the execution's actor, a known session, conversation, or CI run ID MUST match, and implicit inheritance MUST rest on positive evidence (a shared run key or the same known stable id), or the contribution is refused. An agent MUST record inside an execution. A declared human or automation MAY contribute outside any execution under a `CTB-...` key.

## Rationale

Attribution only happens reliably when it costs nothing extra, and it is only trustworthy when one actor cannot silently record under another's execution.

## Acceptance criteria

- `./ros provenance identity` shows the resolved actor, the discovery mechanism, and the active executions.
- With several active executions, the one agreeing with the process identity is chosen, or `--execution` is required.
- Contradictory identity, another session or run of the same agent, and an agent without an execution are all refused.
- An identical recording by a human or automation outside any execution is idempotent.

## Verification

- ProvenanceEffectTests: another session of the same agent cannot record into this session's execution; a human contribution outside any execution is idempotent;
- ProvenanceTests: a process with no identity is undeclared; a session or CI run mismatch is a different run
- ProvenanceEffectTests: an agent cannot record into another agent's execution; an agent with no execution is refused; a declared human contributes under a CTB key; a human approval alongside an active agent execution is recorded separately
