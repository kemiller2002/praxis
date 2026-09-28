---
id: RQ-ROS-2026-A023
title: Praxis is the canonical product and CLI name; ros is compatibility only
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-28
updated: 2026-09-28
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A043
  - AGENTS.md
tags: [naming, rename, praxis, cli, compatibility]
provenance:
  contributions:
    EXE-20260928T134520493Z-befb2bfe:
      operations: [created]
      at: 2026-09-28T14:30:35.274Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
---

# Requirement

Praxis is the canonical current product and CLI name. Current documentation, commands, launchers, generated installations, workflows, help, diagnostics and product-facing terminology MUST use Praxis / `praxis`. `ros` MAY remain only as (1) a compatibility alias, (2) a persisted-state or backward-compatibility format, (3) historical lineage, or (4) an immutable historical identifier or record. A compatibility alias MUST invoke the same implementation as `praxis` and MUST NOT be presented as the preferred interface.

## Rationale

Agents and people copy the commands they are shown. Compatibility with existing installations must not be broken, and history must not be rewritten.

## Acceptance criteria

- `praxis` (and `./praxis` in this checkout and in installed projects) is the documented command; `praxis --version` and `praxis --help` identify Praxis.
- `ros`, `./ros` and the scaffolded `ros*` launchers run the same Praxis implementation.
- A fresh installation uses `praxis` launchers and a Praxis-named validation workflow; an upgraded ROS-era installation gains them without losing `.ros/` state, user edits or its existing aliases.
- `PRAXIS_*` environment variables are canonical; `ROS_*` names keep working.
- Every remaining `ros` in the repository is compatibility, persisted state, history, or a deliberately deferred internal implementation name.

## Verification

- `tests/Ros.Tests/PraxisNamingTests.fs`
- `tests/Ros.Tests/LifecycleTests.fs` (`move-managed-file` planning)
- `.github/workflows/praxis-validation.yml` step "Compatibility alias runs the same Praxis CLI"
