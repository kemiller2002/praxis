---
id: RQ-ROS-2026-A001
title: Every Praxis actor has an explicit, machine-readable identity
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-identity-and-provenance.md
tags: [identity, actor, provider-neutral]
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

# Every Praxis actor has an explicit, machine-readable identity

Praxis MUST represent who performed an action as a structured actor (`praxis.actor/1`), never only as Git authorship, free-form prose, or inference.

1. An actor MUST declare `kind`, one of `agent`, `human`, `automation`, or `unknown`, so humans, agents, and automated non-agent processes are never collapsed into one string.
2. An actor MUST carry a stable `id`. Agents additionally carry `provider`, `model`, `runtime`, and `executionId`; automation carries `runtime` and `executionId`. Optional attributes (`modelVersion`, `runtimeVersion`, `sessionId`) appear only when known.
3. A required attribute that is not known MUST be recorded as the literal `unknown`. Praxis MUST NOT fabricate a provider, model, version, or identity, and MUST NOT infer one from style, timestamps, filenames, or Git authorship.
4. Provider and runtime vocabularies MUST remain open strings. Praxis MUST NOT depend on any particular provider (OpenAI, Anthropic, Google, GitHub, local runtimes, or future ones).
5. Stable identity (`kind`, `id`, `provider`) MUST be distinguishable from execution-scoped identity (`executionId`, `model`, `runtime`, versions, `sessionId`).

## Verification

- `tests/Ros.Tests/ProvenanceTests.fs`: unknown-token, agent/human rendering, explicit-override, CI-automation, and unknown-environment cases.
- `tests/provenance-cli.test.mjs`: `identity --json` for Claude Code, a local non-whitelisted agent, a human, and an unidentified process.
