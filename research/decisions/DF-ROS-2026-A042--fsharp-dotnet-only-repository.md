---
id: DF-ROS-2026-A042
title: The repository is F#/.NET only; repository-owned Node/JavaScript/TypeScript is removed and prohibited by an automated invariant
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-28
updated: 2026-09-28
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A022]
related_documents:
  - RQ-ROS-2026-A022
  - DF-ROS-2026-A009
  - DF-ROS-2026-A030
  - AGENTS.md
  - docs/cli.md
  - docs/native-installation.md
supersedes:
  - DF-ROS-2026-A029
  - DF-ROS-2026-A032
  - DF-ROS-2026-A033
  - DF-ROS-2026-A034
superseded_by: []
tags: [architecture, fsharp, dotnet, node-removal, distribution, invariant, decision]
confidence: high
derived_from: [RQ-ROS-2026-A022]
provenance:
  contributions:
    EXE-20260928T090915352Z-fb23943c:
      operations: [created]
      at: 2026-09-28T10:18:53.922Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Architecture decision removing repository-owned Node and enforcing the invariant (F#/.NET-only repository cleanup (work item FSHARP-ONLY-REPOSITORY))"
    EXE-20260928T103758056Z-43f11e6e:
      operations: [modified]
      at: 2026-09-28T11:23:28.913Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Renumbered from DF-ROS-2026-A041 (taken on main by remote execution); records that remote execution fits the rule (work item FSHARP-ONLY-MAIN-MERGE)"
    EXE-20260928T134520493Z-befb2bfe:
      operations: [modified]
      at: 2026-09-28T14:30:36.088Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
---

# Context

`DF-ROS-2026-A030`/`A032` made the F# CLI the only CLI, but Node stayed in the
repository: `DF-ROS-2026-A033` kept `tools/ros_cli.mjs` and its companions as
the in-process library of the Node web interface and project-administration
hub, `./ros` itself was a Node script that started the F# CLI, the npm package
(`bin/*.mjs`, `lib/*.mjs`, `publish.yml`, `DF-ROS-2026-A029`/`A034`) was a
distribution channel, the scaffolded `./ros` was a Node launcher, the public
site's tooling and tests were Node, and about forty Node test files drove the
F# CLI end to end. Building, testing and releasing therefore still required
npm and a Node runtime.

The user directed that the repository become F#/.NET only: repository-owned
implementation, orchestration, validation, tooling and automation in F#/.NET;
no npm dependency to build, test, validate, package or run Praxis; external
GitHub Actions that run on Node are not repository-owned code.

# Decision

1. **Rule.** `RQ-ROS-2026-A022`: repository-owned JavaScript/TypeScript/Node
   is prohibited unless an accepted `DF-` decision approves a narrow
   exception, recorded in `ros.json` `implementationPolicy.exceptions` as one
   exact path or directory. There are no exceptions today.
2. **Enforcement.** `praxis architecture check [--json]` (F#:
   `Ros.Domain.Architecture.ImplementationLanguagePolicy`,
   `Ros.Infrastructure.Architecture.FileImplementationPolicyRepository`)
   scans tracked plus untracked, non-ignored files via Git and fails with
   each offending path. The same findings join `praxis validate` whenever
   `ros.json` opts in with `implementationPolicy.prohibitNodeArtifacts`, so
   CI, agents and local runs share one path. It is opt-in per repository:
   projects Praxis is installed into may legitimately own JavaScript, and
   are not failed by default. This repository opts in.
3. **Required behaviour moves to F#; obsolete behaviour is deleted.**
   - The web interface and the project-administration hub become F# commands
     (`praxis web serve`, `praxis hub ...`) rendering HTML on the server with no
     browser JavaScript. `DF-ROS-2026-A009`'s hub decision stands; only its
     implementation language changes. The Node library they depended on is
     deleted.
   - The public site's tooling and tests become an F# tool project
     (`site-tools/SiteTools.fsproj`) and test project; the site's optional
     progressive-enhancement script is removed, since the page was already
     complete without it.
   - The Node end-to-end tests of the F# CLI become F# tests in
     `tests/Ros.Tests`, keeping their golden values.
   - `./ros` (this checkout) and the scaffolded `./ros` become POSIX shell
     launchers (the scaffold adds `ros.cmd`/`ros.ps1` for Windows). The
     scaffolded launcher installs the pinned native release side by side
     (`install-native --no-activate`) instead of downloading `ros-fs` assets
     through Node. (`DF-ROS-2026-A043` later made these launchers `./praxis`,
     `praxis.cmd` and `praxis.ps1`, keeping the `ros` names as aliases.)
4. **Distribution.** The npm package and its publish workflow are retired;
   the native release (`native-release.yml`, `scripts/install-native.*`) is
   the distribution. Native bundles no longer carry an `npm pack`ed
   `package/` directory: the self-contained binary embeds its scaffold
   payload. `release.json` replaces `package.json` as the single version
   and package-identity source (the package name is unchanged, so existing
   installation manifests remain valid).
5. **History is not rewritten.** Accepted records, evidence, telemetry and
   F# source comments that cite `tools/*.mjs` as the behaviour an F# port
   mirrored remain as lineage; that code is available in Git history before
   this decision's commit. The Python artifact validator
   (`tools/ros_cli.py`) is out of scope: it is not Node.

This supersedes `DF-ROS-2026-A029` (npm-distributed `ros-fs` binaries),
`DF-ROS-2026-A032` (Node launcher as the scaffolded `./ros`),
`DF-ROS-2026-A033` (Node retained as the web/hub library) and
`DF-ROS-2026-A034` (npm snapshot version pinning).

**Remote execution.** The cloud-agent/remote execution path accepted in
`DF-ROS-2026-A041` already fits this rule: `praxis-remote.yml` and the
`praxis-setup`/`praxis-remote` composite actions are execution hosts for the
F# executable (`praxis remote execute|classify`), with POSIX-shell bootstrap and
persistence scripts; no repository-owned step runs Node. Its three Node test
files were ported to F# with the rest. (This record was first drafted as
`DF-ROS-2026-A041`/`RQ-ROS-2026-A021` on its branch and renumbered when those
IDs were taken on `main` by remote execution.)

# Rationale

A fresh environment, including a cloud agent, needs only .NET and Git;
behaviour lives once, in the typed F# system; npm cannot become an implicit
prerequisite again; and Praxis matches the wider F# architecture.

# Alternatives

- **Document the rule only.** Rejected: nothing would stop a regression.
- **Keep the web/hub in Node as a documented exception.** Rejected: they
  were the main reason Node's library survived, and an F# HTTP adapter over
  the CLI is small.
- **Port the site script to F# via a JavaScript compiler.** Rejected: that
  still ships JavaScript, and the page does not need it.
- **Enforce the rule in every installed project.** Rejected: it is this
  repository's architecture, not a rule for every project that uses Praxis.

# Consequences

`npx`/`ros-bootstrap`/`ros-fs` npm entry points no longer exist; users
install with `scripts/install-native.sh`/`.ps1` or `echelon install praxis`.
CI needs only `actions/setup-dotnet` (plus Python for the Python oracle
tests). Adding any JavaScript/TypeScript/npm file fails CI with its path.
`upgrade` in an existing project replaces its Node `./ros` with the shell
launcher, adds `ros.cmd`/`ros.ps1`, and stops managing
`tools/ros_fs_launcher.mjs`; as with every retired artifact, the lifecycle
never deletes that file, so the project may remove it.

# Reversibility and validation

Reversible by `git revert`, but re-adding Node requires a new accepted
decision and an explicit exception. Validated by the full F# test suite
(including `ImplementationLanguagePolicyTests`, which checks this
repository's own tree and both starter profiles), `./praxis architecture
check`, `./praxis registry check`, `./praxis validate`, and the site verification.
