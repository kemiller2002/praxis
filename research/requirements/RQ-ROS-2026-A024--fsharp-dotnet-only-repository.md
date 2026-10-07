---
id: RQ-ROS-2026-A024
title: Repository-owned implementation and automation are F#/.NET only
status: accepted
version: 1.0.2
owners:
  - repository-governance
created: 2026-09-28
updated: 2026-10-06
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A049
  - DF-ROS-2026-A033
  - requirements/SHARED-APPLICATION-FOUNDATIONS.md
tags: [architecture, fsharp, dotnet, tooling, node-removal, invariant]
provenance:
  contributions:
    EXE-20260928T090915352Z-fb23943c:
      operations: [created]
      at: 2026-09-28T10:18:53.294Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured the F#/.NET-only repository rule from the user's objective (F#/.NET-only repository cleanup (work item FSHARP-ONLY-REPOSITORY))"
    EXE-20260928T103758056Z-43f11e6e:
      operations: [modified]
      at: 2026-09-28T11:23:29.586Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Renumbered from RQ-ROS-2026-A021 (taken on main by remote execution) (work item FSHARP-ONLY-MAIN-MERGE)"
    EXE-20260928T134520493Z-befb2bfe:
      operations: [modified]
      at: 2026-09-28T14:30:36.538Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
    EXE-20260930T175640720Z-98771e24:
      operations: [migrated]
      at: 2026-09-30T17:57:08.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PR92-ID-RENUMBER: renumbered from A022 on this branch to resolve a collision with a different main record; content unchanged"
    EXE-20261006T203816433Z-d8de3cb4:
      operations: [modified]
      at: 2026-10-06T20:40:45.363Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-MISC-09: fix the tests/Praxis.Tests path and correct the status to accepted while Python automation remains (PRAXIS-MISC-08)"
---

> **Renumbered 2026-09-30** (`PRAXIS-PR92-ID-RENUMBER`, owner-approved): this record was numbered A022 on PR #92's branch. Main had already given A022 to a different record, so this one took the next free number before the branches were reconciled. Its content is unchanged.

# Requirement

Praxis repository-owned implementation, orchestration, validation, build tooling, and executable automation MUST be implemented in F#/.NET. Repository-owned JavaScript/TypeScript/Node execution is prohibited unless an explicit architectural exception is documented and approved. Third-party GitHub Actions whose internal implementation uses Node are not considered repository-owned Node code.

"Repository-owned" means every tracked file, and every untracked file that is not ignored, in this repository, including the scaffold this repository installs into other projects. It does not include the implementation of external GitHub Actions, or packages a *consumer* project installs for its own purposes (which, for example, `echelon doctor` may inventory).

An approved exception MUST name one exact path, or one directory, and cite the accepted `DF-` decision that approves it, in `ros.json` `implementationPolicy.exceptions`. Wildcards are not permitted.

## Rationale

- A fresh environment, including a cloud agent, needs only the toolchain Praxis actually uses (.NET/F#, Git), so runs are reproducible and cheaper to provision.
- Executable behaviour stays inside the typed F# system, where the domain rules, typed outcomes and tests live.
- One implementation per behaviour: no duplicate orchestration path in a second language that can drift from the F# one.
- npm and a Node runtime can never again become an implicit prerequisite of building, testing, validating, packaging or running Praxis.
- It aligns Praxis with the wider Echelon Foundry F# architecture.

## Acceptance criteria

- No `*.js`, `*.jsx`, `*.mjs`, `*.cjs`, `*.ts`, `*.tsx`, `package.json`, `package-lock.json`, `npm-shrinkwrap.json`, `yarn.lock`, `pnpm-lock.yaml`, `bun.lock`, `bun.lockb` or `tsconfig.json` is repository-owned, unless covered by an approved exception.
- The check is automated in F#: `./praxis architecture check` exits non-zero and prints each offending path; the same findings fail `./praxis validate` when `ros.json` enables `implementationPolicy.prohibitNodeArtifacts`.
- Repository workflows run .NET/F# (and the `./praxis` launcher) for every step the repository owns; none installs npm packages or runs Node.
- No starter profile scaffolds a Node artifact into a project.

## Verification

- `tests/Praxis.Tests/ImplementationLanguagePolicyTests.fs` (policy classification, exceptions, CLI exit codes and paths, unified `validate`, scaffolded profiles, and this repository's own tree)
- `.github/workflows/praxis-validation.yml` step "F#/.NET-only repository invariant"

## Status (2026-10-06)

Status corrected from `implemented` to `accepted` (`PRAXIS-MISC-09`). The
Node/JavaScript/TypeScript part is met: no such artifact is
repository-owned, and the F# check above enforces it. The wider rule,
that build tooling and executable automation are F#/.NET, is not yet met:
tracked Python remains under `research/experiments/`, and several scripts,
workflows and the remote action still run inline `python3`. The acceptance
criteria above check only Node artifacts, so they pass anyway. Porting the
Python, or recording an approved exception for each remaining use, plus an
automated check for it, is `PRAXIS-MISC-08`.
