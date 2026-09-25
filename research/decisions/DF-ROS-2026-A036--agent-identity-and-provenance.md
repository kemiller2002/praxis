---
id: DF-ROS-2026-A036
title: Agent identity and accumulating provenance as a first-class Praxis concept
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
research_area: repository-operating-system
decision_type: architecture
supports: []
related_documents:
  - DF-ROS-2026-A006
  - DF-ROS-2026-A010
  - DF-ROS-2026-A035
  - docs/agent-identity-and-provenance.md
  - RQ-ROS-2026-A001
  - RQ-ROS-2026-A002
  - RQ-ROS-2026-A003
  - RQ-ROS-2026-A004
  - RQ-ROS-2026-A005
  - RQ-ROS-2026-A006
  - RQ-ROS-2026-A007
  - RQ-ROS-2026-A008
  - RQ-ROS-2026-A009
  - RQ-ROS-2026-A010
  - RQ-ROS-2026-A011
supersedes: []
superseded_by: []
tags: [provenance, identity, agents, validation, architecture]
confidence: high
provenance:
  contributions:
    - operation: "created"
      at: "2026-09-25T18:16:43.844Z"
      actor:
        kind: "agent"
        id: "claude-code"
        provider: "anthropic"
        model: "unknown"
        runtime: "claude-code"
        executionId: "EXE-20260925T174822557Z-7d2ee77c"
        sessionId: "0cb6dd40-ee60-5a05-8f85-35e2927960fe"
        assurance: "self-reported"
      reason: "architecture decision for agent identity and provenance"
---

# Context

Praxis could not reliably say which agent, human, or automation produced a
record. What existed was scattered and partial:

- telemetry execution records (`EXE-...`) discovered a runtime identity
  (provider, runtime, session, `ROS_ACTOR` as `agentId`) at `work begin`, but
  nothing else referenced it;
- work events recorded *what* happened to a work item, never *who* did it;
- backlog items carried a free-form `createdBy` (usually `unknown`);
- canonical artifacts carried a free-form `author_agent` string that mixed
  agents and humans (`agent-or-human-identity`) and recorded only one author;
- requirements existed only as prose with no identifier, lifecycle, or
  validation;
- validation could not detect missing attribution.

# Decision

Make identity and provenance a first-class model in `Ros.Domain.Provenance`,
reusing every existing mechanism rather than adding a parallel one.

1. **Actor (`praxis.actor/1`).** A structured actor with a `kind`
   (`agent`, `human`, `automation`, `unknown`), stable identity (`id`,
   `provider`), execution-scoped identity (`executionId`, `model`, `runtime`,
   versions, `sessionId`), and `assurance`. Required attributes that are not
   known are the explicit token `unknown`; nothing is fabricated or inferred
   from style, Git authorship, or timestamps.
2. **Execution identity is the existing telemetry execution.** `work begin`
   already creates `EXE-...` and discovers identity from whitelisted
   environment variables. The execution record now carries its own `actor`.
   Every later command resolves the current actor from the same environment
   and binds it to the latest *compatible active* execution (provider,
   runtime, session, and agent id agree where both are known), or to
   `ROS_EXECUTION_ID`. Agents identify once per execution; nothing asks them
   to repeat identity per command. A different agent or session never
   inherits another's execution.
3. **Action provenance on the existing event log.** Every work event carries
   `actor`, included in its content hash. The installer's bookkeeping event is
   attributed to an `automation` actor identically in both installers.
4. **Accumulating record provenance (`praxis.provenance/1`).** Backlog items
   and canonical artifacts carry an append-only list of contributions
   (`created`, `modified`, `reviewed`, `approved`). Backlog commands append
   automatically; artifacts gain contributions through
   `provenance record PATH`, which inherits the actor and the single active
   work item and edits by line insertion only. New records default to
   `created`, existing and legacy records to `modified`.
5. **Lineage stays in `derived_from`.** Authorship and derivation are never
   merged; projections report both separately.
6. **Formal requirements.** A new canonical artifact kind `RQ` in
   `requirements/`, registry `registries/requirements.json`. Only `RQ-` files
   are artifacts, so existing prose stays valid, and the registry is optional
   while a repository has no requirements.
7. **Validation with a legacy cutoff.** `ros.json` `provenance.enforce` and
   `requiredSince`. Malformed provenance is always an error; missing
   provenance on a new record is an error under enforcement; honest unknowns
   are warnings; pre-cutoff and legacy-mode records are informational. A
   committed contribution that is removed or altered is an error.
8. **One canonical serialization across boundaries.** The same shape is used
   in events, backlog items, execution records, registries, and Ordo handoffs
   (`producedBy`), and in YAML front matter.
9. **CLI.** `identity` (who Praxis will record), `provenance record|show|
   validate|summary`. Per `DF-ROS-2026-A035`, the command family lives in
   `src/Ros.Cli/ProvenanceCommands.fs`; `Program.fs` gained only routes.

The front-matter parser (F# and the retained Node library) now accepts
`- key: value` list items as mappings, which contributor lists require. No
existing front matter used that shape, so every document parses as before.

# Alternatives

- **A single `author` string per record** (extending `author_agent`):
  rejected. It cannot separate agent, human, and automation, cannot record
  more than one contributor, and loses history when a second actor edits.
- **Git commit authorship or trailers as the provenance source:** rejected as
  the sole source. It is outside Praxis records, is lost across export
  boundaries, attributes files not records, and cannot represent a human
  approving an agent's work.
- **A separate provenance store keyed by record id:** rejected. It would be a
  second authority parallel to the event log and front matter, and provenance
  would be stripped whenever a record crosses a boundary without that store.
- **Registering agents in a registry before they may act:** rejected for now.
  It adds ceremony without adding proof; `registries/agents.json` remains
  available for a later attested registry.
- **Cryptographic signing now:** rejected; Praxis has no signing mechanism.
  `assurance` preserves any declared level so attestation can be added
  without changing the record shape.

# Bootstrap boundary

This work (`WI-0070`) was captured and started with the CLI that predated
this mechanism, so its backlog entry and `work.started` event carry no actor.
They are honest legacy records: this repository's `requiredSince` is set
after them, and nothing was back-filled. The execution
`EXE-20260925T174822557Z-7d2ee77c` likewise has no `actor` block; its
`identity` already recorded `agentId: claude-code`, provider `anthropic`,
runtime `claude-code`. Every artifact this decision introduced -- this record
and `RQ-ROS-2026-A001`..`A011` -- was attributed with the new
`provenance record` command, bound to that execution, with `model: unknown`
because the agent did not record its model identifier.

# Architecture challenge

Reviewed against the questions that motivated the work; revisions made are
noted.

- *Identity or metadata?* Identity is validated: malformed or missing actors
  fail `validate`, and every writer requires an actor (there is no
  no-attribution code path; an unidentifiable process is recorded as
  `unknown`, visibly).
- *Survives boundaries?* Events publish with `actor`, registries project
  provenance, and handoffs carry `producedBy`.
- *Multiple contributors without overwriting?* Append-only, idempotent,
  prefix-preservation checked against the committed file.
- *Humans and agents?* Separate kinds; human approval is a contribution;
  involvement labels distinguish the five collaboration shapes.
- *Stable versus execution identity?* Separated; binding refuses a different
  session or agent (revised: an initial design bound any active execution
  for the work item, which would have attributed a handover to the wrong
  run).
- *GitHub or provider assumptions?* Provider/runtime are open strings; GitHub
  Actions is only one whitelisted signal and maps to `automation`.
- *Unknowns honest?* Required unknowns are explicit; optional unknowns are
  omitted; nothing is inferred.
- *Future agents?* Any agent can declare `ROS_ACTOR_KIND`/`ROS_ACTOR`/
  `ROS_TELEMETRY_PROVIDER` without a code change.
- *Validation enforces the invariant?* Yes for events, backlog items, and
  canonical artifacts (revised: installer events initially lacked an actor
  and failed the policy on a fresh install).
- *Legacy repositories?* Legacy mode by default; cutoff-based adoption; no
  history rewrite.
- *Attestation later?* `assurance` is preserved; a verifier can grade it.
- *Duplication?* The execution record's `identity` remains the telemetry
  authority; its `actor` is the canonical projection used by every other
  record (revised: an unbound automation execution is informational rather
  than a warning, so installer bookkeeping is not noise).
- *Developer experience?* Set identity once in the environment; `work begin`
  binds the execution; backlog and event attribution is automatic; artifacts
  need one `provenance record` call per change.

# Consequences

- Golden-master differential tests compare every pre-existing field at
  parity and remove only the provenance extension
  (`tests/support/provenance-golden.mjs`); provenance is covered by
  `tests/Ros.Tests/ProvenanceTests.fs` and `tests/provenance-cli.test.mjs`.
- Event ids now include the actor, so they differ from the ids the retired
  Node CLI would have produced for the same transition.
- Canonical-artifact modifications are attributed when an agent runs
  `provenance record`. A new artifact without provenance fails `validate`; an
  existing artifact changed in the working tree without a new contribution
  is a local `unrecorded-modification` warning (CI sees only committed
  content, so it cannot detect a missed call after commit). Work events
  still attribute completed changes to the completing actor.
- The local web interface (`tools/ros_server.mjs`) writes work records
  in-process through the retained Node library (`DF-ROS-2026-A033`) and so
  records no provenance. Porting provenance into Node would create a second
  authority; the follow-up (`WI-0071`) routes those writes through the CLI.
  The project-administration profile keeps legacy provenance mode meanwhile;
  the greenfield profile enforces provenance for every record.
