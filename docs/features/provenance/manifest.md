# Feature Manifest — Agent identity and provenance

## Purpose

Record who acted -- agent, human, or automation, and in which execution -- on
work events, execution records, backlog items, requirements, and every other
canonical artifact; accumulate contributor history without overwriting it;
validate the invariant for new work while keeping legacy records readable.
Decision: `DF-ROS-2026-A036`; requirements `RQ-ROS-2026-A001`..`A011`.

## Ownership

- State: the `actor` on `.ros/events/events.jsonl` events and
  `.ros/telemetry/executions/*.json` records; the `provenance` block on
  `.ros/work/queue.json` items and canonical-artifact front matter; the
  `provenance` policy in `ros.json`.
- Transitions / commands: `identity`, `provenance record|show|validate|
  summary` (`src/Ros.Cli/ProvenanceCommands.fs`); automatic stamping in `work
  begin|block|resume|complete`, `add`, `work update|attach`, and backlog
  transitions (`src/Ros.Cli/Program.fs` routes only).
- Invariants and guards: `Ros.Domain.Provenance` -- `Actor` (required fields
  by kind, explicit `unknown`), `Provenance.append`/`preserves` (append-only,
  idempotent), `ActorResolution.bindExecution` (compatible active execution
  only), `ProvenanceValidation` (severity grading, legacy cutoff, rewrite
  and unrecorded-modification checks).
- Capabilities / authority: identity is self-reported (`assurance`); Praxis
  never authenticates or attests an actor.
- Important effects and effect contracts: `FrontMatterProvenance.append`
  (line insertion only), `ProvenanceJson.appendContribution` (JSON-node
  append, refuses malformed blocks), `FileProvenanceRepository` (reads every
  store; reads committed file versions through `ProcessGitRepository`).

## Interfaces

- Inbound: `ROS_ACTOR_KIND`, `ROS_ACTOR`, `ROS_EXECUTION_ID`, the telemetry
  identity variables, `--actor`; `ros.json` `provenance`.
- Outbound: `praxis.actor/1` and `praxis.provenance/1`
  (`schemas/praxis-actor.schema.json`, `schemas/praxis-provenance.schema.json`)
  in events, execution records, backlog items, registries, and Ordo handoffs
  (`producedBy`); provenance errors in `validate`.

## Tests and verification

- Local behavior tests: `tests/Ros.Tests/ProvenanceTests.fs` (including
  seeded property tests for serialization and append-only front-matter edits).
- Boundary/contract tests: `tests/provenance-cli.test.mjs` (end-to-end CLI in
  a bootstrapped repository); golden-master differential tests keep every
  pre-existing field at parity through `tests/support/provenance-golden.mjs`.
- Integration/live verification: `./ros provenance validate` and
  `./ros validate` on this repository.

## Dependencies

- Allowed direct dependencies: telemetry identity discovery
  (`Ros.Domain.Telemetry.Identity`), the artifact front-matter codec, the
  work event log, the backlog queue, Git read of committed file content.
- Required composition context: repository root.

## Modification boundaries

- Normal: `src/**/Provenance/**`, `src/Ros.Cli/ProvenanceCommands.fs`,
  provenance tests, provenance docs and schemas.
- Escalation required: changing the canonical serialization, the meaning of
  an operation or kind, or validation severities (a compatibility change for
  every repository and integration that stores provenance).

## Local agent instructions

- `AGENTS.md` ("Agent Identity and Provenance") and
  `docs/00-governance/Agent-Operating-Manual.md` ("Identity and Provenance").

## Maintenance

- Owner: repository-governance
- Last checked against implementation: 2026-09-25
- Known gaps: identity is self-reported; an already committed canonical
  artifact modified without `provenance record` is detectable only as a local
  warning before commit.
