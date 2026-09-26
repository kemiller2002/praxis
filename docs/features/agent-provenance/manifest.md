# Feature Manifest — Agent identity and provenance

This manifest routes engineers and agents to authority. The semantic rules
live in the linked decision, requirements, source symbols, and tests.

## Purpose

Give every agent, human, and automation that participates in Praxis-governed
work an explicit, machine-readable identity. Attribute requirements, other
canonical records, and work events to the actor and execution responsible,
without destroying earlier provenance.

## Ownership

- State, including presentation state:
  - front matter `provenance.contributions` on canonical artifacts under
    `research/**` and `missions/**`;
  - `identity.actorKind` in `.ros/telemetry/executions/*.json`;
  - `actor` on `.ros/events/events.jsonl` work events, plus
    `artifact.contributed` events;
  - `createdByActor` on `.ros/work/queue.json` items;
  - the policy in `ros.json` `provenance`;
  - authority decisions `DF-ROS-2026-A036` and `DF-ROS-2026-A037`
    (cross-system interchange), requirements `RQ-ROS-2026-A001`..`A019`.
- Transitions / commands / messages:
  - `src/Ros.Cli/Program.fs` `runProvenanceIdentity`,
    `runProvenanceRecord`, `runProvenanceShow`, `runProvenanceAudit`,
    `withResolvedActor`, and `identityOverridesFrom`;
  - `Ros.Infrastructure.Provenance.FileProvenanceRepository.record`.
- Invariants and guards:
  - `Ros.Domain.Provenance.ActorKind`, `Actor`, `ActorResolution`;
  - `ArtifactProvenance.record` and `ArtifactProvenance.problems`;
  - `ProvenanceValidation.findings`, `EventProvenance.findings`.
- Capabilities / authority:
  - identity is resolved only from the current process (flags, whitelisted
    environment, known runtimes);
  - a contribution must agree with its execution's actor
    (`FileProvenanceRepository.resolveAttribution`);
  - identity is self-reported provenance, not authentication.
- Important effects and effect contracts:
  - surgical front-matter edits (`Ros.Contracts.Provenance.
    ProvenanceFrontMatter`) verified by read-back before an atomic write;
  - event append through the shared `work-state` journal under the
    `work-protocol` lock.

## Interfaces

- Inbound:
  - `./ros provenance identity|record|show|export|audit`;
  - `ROS_EXECUTION_ID` (the environment form of `--execution`);
  - `praxis.provenance/1` blocks from other Echelon systems
    (`Ros.Contracts.Provenance.ProvenanceInterchangeJson.classify`);
  - identity flags (`--actor-kind`, `--agent`/`--actor`, `--provider`,
    `--model`, `--runtime`, …) on work transitions, `add`, and
    `telemetry start`;
  - environment `ROS_ACTOR_KIND`, `ROS_ACTOR`, and `ROS_TELEMETRY_*`.
- Outbound:
  - provenance findings in `./ros validate [--json]` (errors, plus warnings
    with `"severity":"warning"`);
  - events exported by `./ros adapter publish`;
  - `provenance` projected into `registries/*.json`;
  - `schemas/provenance-actor.schema.json`,
    `schemas/artifact-provenance.schema.json`, and
    `schemas/provenance-interchange.schema.json`;
  - `./ros provenance export` (interchange block) and the dependency-free
    reference library `lib/provenance-interchange.mjs`.

## Tests and verification

- Local behavior tests: `tests/Ros.Tests/ProvenanceTests.fs`, which includes
  property tests of accumulation and serialization round-trip.
- Boundary/contract tests:
  - `tests/Ros.Tests/ProvenanceEffectTests.fs` covers files, events, adapter
    export, registries, and impersonation refusal;
  - `tests/provenance-actor-fsharp-differential.test.mjs` checks Node/F#
    actor parity;
  - the updated work and telemetry differential goldens;
  - `tests/Ros.Tests/ProvenanceInterchangeTests.fs` and
    `tests/provenance-interchange.test.mjs` assert the shared conformance
    fixtures and the end-to-end scenario in
    `tests/fixtures/provenance-interchange/` from both implementations.
- Integration/live verification: `./ros validate` and `./ros provenance audit`
  on this repository, whose own requirements are attributed.

## Dependencies

- Allowed direct dependencies:
  - telemetry identity discovery (`Ros.Domain.Telemetry.Identity`);
  - the artifact front-matter codec and policy;
  - execution records;
  - the work-state journal.
- Required composition context: repository root, `ros.json`, and the
  telemetry execution root.

## Modification boundaries

- Normal: `src/**/Provenance/**`, provenance tests, `docs/agent-provenance.md`,
  and `research/requirements/**`.
- Escalation required:
  - the actor JSON shape and the event `actor` field, which are hashed into
    event IDs and consumed across integration boundaries;
  - the front-matter serialization;
  - the policy severities;
  - the interchange block, its receiving verdicts, and the conformance
    fixtures, which downstream Echelon systems vendor
    (`docs/echelon-provenance-architecture.md`).

  Change these only through a new decision with a migration.

## Local agent instructions

- The "Agent Identity and Provenance" sections of `AGENTS.md` and
  `docs/00-governance/Agent-Operating-Manual.md`.

## Maintenance

- Owner: repository-governance
- Last checked against implementation: 2026-09-26
- Known gaps:
  - no cryptographic attestation, by design (see `DF-ROS-2026-A036`);
  - modification detection is Git-based, with a date-based fallback when no
    Git checkout is available;
  - the Node internal library mirrors actor resolution but has no
    `provenance` commands.
