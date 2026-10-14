# Feature Manifest — Automation and release

## Purpose

Route changes to GitHub validation and native release declarations while
keeping platform mechanics separate from Praxis semantic decisions. Workflows
are execution hosts for the F# CLI and .NET tooling; they own no Node
execution (`RQ-ROS-2026-A024`). External actions (for example
`actions/checkout`) running on their own Node runtime are not repository code.

## Ownership

- State, including presentation state: `.github/workflows/*.yml`, the
  installed workflow template (`starter/greenfield/.github/workflows/`), and
  `release.json`.
- Transitions / commands / messages: GitHub push/pull-request triggers and
  workflow `run` blocks invoking `dotnet`, `./praxis`, the site tool, the
  installers and `gh`.
- Invariants and guards: build/test/validation exit status, the F#/.NET-only
  invariant (`./praxis architecture check`), release only from the canonical
  `main` branch, and bundle checksums.
- Capabilities / authority: `contents: read` for validation; `contents:
  write` only for creating the native GitHub Release; Pages permissions only
  for site deployment.
- Important effects and effect contracts: checkout/.NET setup, build/test
  subprocesses, GitHub Release creation/upload, Pages deployment, and
  transient runner file mutation.

## Interfaces

- Inbound: GitHub events.
- Outbound: CI conclusions/logs, GitHub step outputs, native GitHub Releases,
  and the Pages deployment.

## Tests and verification

- Boundary/contract checks run in CI: the F# test suite, `./praxis architecture
  check`, `./praxis registry check`, `./praxis validate`, the cross-platform
  lifecycle job, and the site tool's own workflow assertions.
- Integration/live verification: GitHub-hosted runs; no local workflow
  execution harness exists.

## Dependencies

- Allowed direct dependencies: GitHub Actions, Git, the .NET SDK, Python for
  the remote action's embedded report script, the GitHub CLI, and Praxis CLI
  commands. Not Node/npm.

## Modification boundaries

- Normal: workflow YAML.
- Escalation required: release publication, permissions, channel/version
  semantics, credential/trust changes, removal of a release gate, or any
  repository-owned Node execution (requires an accepted `DF-` exception).

## Local agent instructions

- `AGENTS.md`, `PACKAGE-USAGE.md`, and `.github/workflows/*.yml` comments.

## Maintenance

- Owner: repository-governance
- Last checked against implementation: 2026-09-28 (`DF-ROS-2026-A049`: npm
  publication workflow removed; every workflow step the repository owns runs
  .NET/F#).
- Known gaps: a release is created for `release.json`'s version whenever a
  release input changes on `main`, so an unbumped version is re-uploaded
  (`--clobber`) rather than refused.
