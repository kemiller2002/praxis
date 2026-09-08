# Feature Manifest — Automation and release

## Purpose

Route changes to GitHub validation and npm publication declarations while
keeping platform mechanics separate from ROS semantic decisions.

## Ownership

- State, including presentation state: `.github/workflows/*.yml`, installed
  workflow template, `package.json`, and `package-lock.json`.
- Transitions / commands / messages: GitHub push/pull-request triggers, npm
  package scripts, and workflow `run` blocks.
- Invariants and guards: validation/test exit status, explicit public license,
  main-branch/repository gate, version-change check, registry lookup, and unique
  snapshot version construction.
- Capabilities / authority: GitHub permissions declare `contents: read` and
  `id-token: write` only for trusted npm publication.
- Important effects and effect contracts: checkout/runtime setup, package/test
  subprocesses, npm registry reads and publication, and transient runner file
  mutation.

## Interfaces

- Inbound: GitHub events and npm task invocations.
- Outbound: CI conclusions/logs, GitHub step outputs, stable npm releases, and
  `main`-tagged snapshot releases.

## Tests and verification

- Local behavior tests: release-policy text assertions in
  `tests/npm-bootstrap.test.mjs`; package scripts exercise runtime gates.
- Boundary/contract tests: `npm pack --dry-run`, `npm test`,
  `./ros registry check`, and `./ros validate`.
- Integration/live verification: GitHub-hosted workflow and npm publication;
  no local workflow execution harness exists.

## Dependencies

- Allowed direct dependencies: GitHub Actions, Git, Node/npm, Python for the
  root legacy oracle, and ROS CLI commands.
- Required composition context: repository history, package metadata, and npm
  trusted-publishing configuration.

## Modification boundaries

- Normal: workflow YAML, package task glue, and textual policy tests.
- Escalation required: registry publication, permissions, channel/version
  semantics, credential/trust changes, or removal of a release gate.

## Local agent instructions

- `AGENTS.md`, `PACKAGE-USAGE.md`, and `.github/workflows/*.yml` comments.

## Maintenance

- Owner: repository-governance
- Last checked against implementation: 2026-09-07
- Known gaps: root validation does not install dependencies; TypeScript builds
  are outside `npm test`; registry lookup cannot distinguish every remote
  failure from package absence; package and lockfile versions currently drift.
