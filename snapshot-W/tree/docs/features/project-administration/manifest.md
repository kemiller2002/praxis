# Feature Manifest — Project administration

## Purpose

Route changes to the local hub that registers ROS repositories and invokes each
repository's public ROS work interface without becoming its work-state source
of truth.

## Ownership

- State, including presentation state: `.ros/hub/registry.json` and derived
  `.ros/hub/registry.md`; decision `DF-ROS-2026-A009`.
- Transitions / commands / messages: `tools/ros_hub_cli.mjs` symbols
  `registerRepo`, `unregisterRepo`, `listRepos`, `createWorkInRepo`,
  `listWorkInRepo`, and `listWorkAcrossRepos`.
- Invariants and guards: repository identity/path checks and per-spoke CLI
  outcome parsing in `tools/ros_hub_cli.mjs`.
- Capabilities / authority: the hub may register local paths and invoke the
  target's own `./ros`; each spoke remains authoritative for its work.
- Important effects and effect contracts: hub registry file writes, local
  subprocess calls, HTTP upload temporary files, and browser HTTP requests.

## Interfaces

- Inbound: `ros-hub`, `npm run hub`, HTTP routes, and `web-hub/`.
- Outbound: hub registry JSON/Markdown, aggregated spoke JSON/errors, and
  delegated spoke work changes.

## Tests and verification

- Local behavior tests: `tests/ros-hub.test.mjs` and project-administration
  cases in `tests/npm-bootstrap.test.mjs`.
- Boundary/contract tests: spoke `./ros` JSON/exit behavior and manifest package
  assertions; no formal versioned hub API schema exists.
- Integration/live verification: package test creates a separate installed
  spoke and invokes it through `ros-hub`.

## Dependencies

- Allowed direct dependencies: local filesystem, child process, HTTP body
  parser/server, TypeScript UI, and spoke ROS public interface.
- Required composition context: project-administration profile and writable
  `.ros/hub/` state.

## Modification boundaries

- Normal: `tools/ros_hub_*`, `web-hub/`, project-administration starter files,
  docs, and tests.
- Escalation required: any attempt to make the hub authoritative for spoke work,
  expose it beyond the trusted local boundary, or couple it to Time Entry.

## Local agent instructions

- `AGENTS.md` and `docs/project-administration-hub.md`.

## Maintenance

- Owner: repository-governance
- Last checked against implementation: 2026-09-07
- Known gaps: registry writes are non-atomic/unlocked; HTTP has no
  authentication; uploaded filenames can escape the intended temporary prefix.
