# Feature Manifest — Artifact management

## Purpose

Route changes to canonical research-artifact validation and deterministic
registry projection. Markdown artifacts are authoritative; registries are
replaceable projections.

## Ownership

- State, including presentation state: `research/**` canonical Markdown and
  generated `registries/*.json`; authority decision `DF-ROS-2026-A001`.
- Transitions / commands / messages: `tools/ros_cli.mjs` symbols
  `validate`, `buildRegistries`, and CLI branches `registry build`,
  `registry check`, and `validate`.
- Invariants and guards: `tools/ros_cli.mjs` symbols `ID_RE`,
  `REFERENCE_FIELDS`, `ALLOWED_STATUS`, `CONFIDENCE`, `KIND_CONFIG`,
  `loadArtifacts`, and `registryFindings`; public declarations in `schemas/`.
- Capabilities / authority: repository writers may edit canonical Markdown;
  generated registries must be changed only through projection.
- Important effects and effect contracts: recursive Markdown reads and
  deterministic JSON writes in `tools/ros_cli.mjs`; atomic text writes are
  supplied by `tools/ros_persistence.mjs`.

## Interfaces

- Inbound: artifact front matter, `./ros validate [--json]`, and
  `./ros registry build|check`.
- Outbound: findings on stdout/stderr, process exit status, and eight managed
  registry JSON files.

## Tests and verification

- Local behavior tests: `tests/test_ros_cli.py`, artifact cases in
  `tests/work-protocol.test.mjs`, and the F# compatibility suite linked from
  `docs/migrations/fsharp/README.md`.
- Boundary/contract tests: `schemas/*.schema.json`, registry byte comparison,
  and old/new differential fixtures.
- Integration/live verification: `./ros registry check` and `./ros validate`.

## Dependencies

- Allowed direct dependencies: filesystem, explicit front-matter codec,
  artifact policy, and deterministic JSON projection.
- Required composition context: repository root and `KIND_CONFIG` directory
  mapping; work and telemetry validation compose beside but do not define this
  area's rules.

## Modification boundaries

- Normal: `research/`, `tools/ros_cli.mjs`, artifact schemas, artifact tests,
  and generated registries.
- Escalation required: accepted decisions and schemas that alter an external
  artifact or registry contract; record the intentional migration.

## Local agent instructions

- `AGENTS.md` and `docs/00-governance/Agent-Operating-Manual.md`.

## Maintenance

- Owner: repository-governance
- Last checked against implementation: 2026-09-07
- Known gaps: the JSON schema omits the runtime-supported `medium-high`
  confidence value; Node and Python duplicate the parser and policy.
