---
id: DF-ROS-2026-A050
title: Praxis is the canonical product and CLI name; ros remains only for compatibility, persisted state and history
status: accepted
version: 1.0.1
owners:
  - repository-governance
created: 2026-09-28
updated: 2026-09-30
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A025]
related_documents:
  - RQ-ROS-2026-A025
  - DF-ROS-2026-A049
  - DF-ROS-2026-A041
  - AGENTS.md
  - docs/cli.md
  - docs/native-installation.md
supersedes: []
superseded_by: []
tags: [naming, rename, praxis, cli, compatibility, decision]
confidence: high
derived_from: [RQ-ROS-2026-A025]
provenance:
  contributions:
    EXE-20260928T134520493Z-befb2bfe:
      operations: [created]
      at: 2026-09-28T14:30:35.702Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
    EXE-20260930T175640720Z-98771e24:
      operations: [migrated]
      at: 2026-09-30T17:57:07.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PR92-ID-RENUMBER: renumbered from A043 on this branch to resolve a collision with a different main record; content unchanged"
---

> **Renumbered 2026-09-30** (`PRAXIS-PR92-ID-RENUMBER`, owner-approved): this record was numbered A043 on PR #92's branch. Main had already given A043 to a different record, so this one took the next free number before the branches were reconciled. Its content is unchanged.

# Context

The product is Praxis, Echelon Foundry's repository operating system. The
native installers already provided `praxis` with `ros` as an alias, but the
CLI still identified itself as `ros`/`ros-fs`, this source checkout and every
installed project were driven by `./ros`, the scaffolded CI workflow was
"ROS validation", and `AGENTS.md` taught agents `./ros` commands, which they
copy literally. The user directed that the rename be finished: Praxis is the
canonical current name, and `ros` survives only where compatibility,
persisted state or history require it.

# Decision

1. **Canonical surface.** The CLI is `praxis`: its help, usage, version
   output (`praxis X.Y.Z`), diagnostics, repair hints and web/hub titles say
   Praxis. The built assembly and native binary are `praxis` (was `ros-fs`);
   release bundle and asset names (`praxis-<platform>`, `praxis-bin`) are
   unchanged. Documentation, `AGENTS.md`, workflows and scaffolded files
   teach `praxis` / `./praxis`.
2. **One implementation, compatibility aliases.** `./praxis` is this
   checkout's launcher; `./ros` execs it. Installed projects get `praxis`,
   `praxis.cmd`, `praxis.ps1` (and `praxis-hub` for the project-administration
   profile) as the launchers, with `ros`, `ros.cmd`, `ros.ps1`, `ros-hub` as
   thin aliases that exec them. The native installers keep installing a `ros`
   command beside `praxis`; the hub runs a spoke's `./praxis`, falling back to
   `./ros` for a spoke installed before the rename.
3. **Environment variables.** `PRAXIS_*` names (`PRAXIS_ACTOR`,
   `PRAXIS_ACTOR_KIND`, `PRAXIS_TELEMETRY_*`, `PRAXIS_BASE_REF`,
   `PRAXIS_PACKAGE_ROOT`) are canonical. The historical `ROS_*` names keep
   working: the CLI maps each set `PRAXIS_*` variable onto its `ROS_*` name
   once at start-up (`Ros.Domain.Naming.EnvironmentAliases`), and the
   canonical name wins when both are set. Remote execution still passes its
   child processes only an explicit identity, never an inherited variable.
4. **Renamed scaffold files move on upgrade.** The scaffolded workflow is now
   `.github/workflows/praxis-validation.yml`. A manifest entry may declare the
   destinations it `replaces`; when an earlier destination is recorded in the
   installation manifest and present on disk, `upgrade`/`init` plan a
   `move-managed-file` change instead of creating a duplicate: an unmodified
   file moves and takes the current content, an edited one moves with its
   edits untouched.
5. **What deliberately stays `ros`:**
   - *Persisted state and format contracts:* `.ros/`, `ros.json`
     (configuration read by every installed project, remote execution and
     `echelon doctor`), `.echelon/ros.json` and its `"tool": "ros"`,
     `rosVersion`, the `ROS-INSTALL-*` install work item, and telemetry
     values such as `collector: "ros"` and `ros-clock`.
   - *Compatibility aliases:* the `ros*` launchers, the `ros` command, the
     `ROS_*` variables, the legacy `ros-fs.dll` fallback in the reusable
     foundations workflow, and the package identity
     `@echelon-foundry/repository-operating-system`.
   - *Historical lineage:* `DF-ROS-*`, `RQ-ROS-*`, `EV-ROS-*` and every other
     immutable identifier; accepted and superseded records, evidence,
     telemetry, events, migration documents and prompts describing the
     ROS-era system; code comments citing the retired Node implementation.
   - *Internal implementation names (deferred):* the `Ros.*` projects,
     namespaces and assemblies, `Ros.slnx`, the `ros.payload/` resource
     prefix, and the Python oracle `tools/ros_cli.py`. They are not a user
     interface; migrating them is a separate, larger change tracked on its
     own.

# Rationale

Agents and people copy the commands they are shown; a product that teaches
`./ros` keeps producing `ros` usage. Keeping one implementation behind every
alias means compatibility can never drift into a second behaviour. Persisted
names are contracts with every existing repository, so renaming them would
strand installations for no user benefit.

# Alternatives

- **Blind repository-wide replacement.** Rejected: it would rewrite history,
  break `.ros/` and `ros.json` for every installed project, and corrupt
  immutable identifiers.
- **Drop `ros` entirely.** Rejected: existing installations, scripts and
  muscle memory depend on it.
- **Rename `.ros/` and `ros.json` now.** Rejected: that needs a data
  migration for every installed repository and remote-execution consumer;
  no requirement asks for it.
- **Migrate the `Ros.*` namespaces in the same change.** Rejected for scope:
  large blast radius, no user-facing effect.

# Consequences

`praxis --version` prints `praxis X.Y.Z` (consumers read only the version
token). Upgrading an existing project adds the `praxis` launchers, turns its
`ros` launchers into aliases and moves its validation workflow. Releases
published before this change still honour only `ROS_*` variables, so the
scaffolded workflow also sets `ROS_BASE_REF` for them.

# Reversibility and validation

Reversible with `git revert`. Validated by the naming-contract tests in
`tests/Ros.Tests/PraxisNamingTests.fs` (canonical and alias invocation,
version and help, environment aliases, fresh scaffold, upgrade of a
ROS-era installation with clean and edited workflows, `.ros/` preserved),
the lifecycle planner tests for `move-managed-file`, the full suite,
`./praxis architecture check`, `./praxis registry check` and
`./praxis validate`.
