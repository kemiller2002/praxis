---
id: RQ-ROS-2026-A022
title: Repository-owned implementation and automation are F#/.NET only
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-28
updated: 2026-09-28
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A042
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
---

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
- The check is automated in F#: `./ros architecture check` exits non-zero and prints each offending path; the same findings fail `./ros validate` when `ros.json` enables `implementationPolicy.prohibitNodeArtifacts`.
- Repository workflows run .NET/F# (and the `./ros` launcher) for every step the repository owns; none installs npm packages or runs Node.
- No starter profile scaffolds a Node artifact into a project.

## Verification

- `tests/Ros.Tests/ImplementationLanguagePolicyTests.fs` (policy classification, exceptions, CLI exit codes and paths, unified `validate`, scaffolded profiles, and this repository's own tree)
- `.github/workflows/ros-validation.yml` step "F#/.NET-only repository invariant"
