---
id: RQ-ROS-2026-A001
title: Actor identity is explicit, structured, and provider-neutral
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-28
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A036
  - docs/agent-provenance.md
tags: [provenance, agent, identity, provider-neutral]
provenance:
  contributions:
    EXE-20260925T193942361Z-84dc9b22:
      operations: [created]
      at: 2026-09-25T20:16:17.996Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from the agent identity and provenance objective (work item FEAT-AGENT-PROVENANCE)"
    EXE-20260928T090915352Z-fb23943c:
      operations: [modified]
      at: 2026-09-28T10:18:57.631Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Verification references moved from removed Node tests to their F# ports (F#/.NET-only repository cleanup (work item FSHARP-ONLY-REPOSITORY))"
    EXE-20261006T203816433Z-d8de3cb4:
      operations: [modified]
      at: 2026-10-06T20:40:39.377Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-MISC-09: cited test paths moved from tests/Ros.Tests to tests/Praxis.Tests; content unchanged"
---

# Requirement

Praxis MUST represent whoever performs an action as a structured actor with `kind` (`agent`, `human`, `automation`, `unknown`, or a namespaced `x-...` extension) and a stable `id`. Non-human actors MUST also carry `provider`, `model`, and `runtime`, with the literal `unknown` for any value that is not known. Human actors omit those fields as not applicable. The vocabulary MUST NOT hard-code any provider.

## Rationale

Agents, humans, automated processes, and unknown actors must be distinguishable, and filterable, without parsing free text. Future providers must fit without a code change.

## Acceptance criteria

- The actor JSON form is canonical, with key order `kind, id, provider, model, runtime`, and is published as `schemas/provenance-actor.schema.json`.
- An explicit `--agent`/`--actor`/`ROS_ACTOR` value is the stable id. Otherwise a non-human actor whose provider and runtime are both known is `provider/runtime`; otherwise the id is `unknown`.
- Known agent runtimes (Codex, Claude Code, Gemini CLI, Copilot) imply `agent`, and GitHub Actions implies `automation`. Nothing else implies a kind; in particular, a local model server does not.
- An invalid explicit kind is rejected, never coerced.

## Verification

- ProvenanceTests: actor kinds round-trip; agent identity is resolved from a known agent runtime; identity resolution is provider-neutral; nothing known resolves to an explicit unknown actor; actor JSON is canonical
- tests/Praxis.Tests/ProvenanceCliTests.fs (actor resolution against the frozen golden masters)
