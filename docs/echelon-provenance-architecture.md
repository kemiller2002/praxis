# Echelon cross-system provenance

Praxis owns the meaning of agent identity and provenance:

- what an actor is;
- what an execution is;
- what a contribution is;
- how "unknown" is recorded;
- how provenance differs from attestation.

See [`agent-provenance.md`](agent-provenance.md), `DF-ROS-2026-A036`, and
`RQ-ROS-2026-A001`..`A012`.

This document explains how that meaning survives when work crosses from one
Echelon system to another (`DF-ROS-2026-A037`, `RQ-ROS-2026-A013`..`A015`).
It also records the inventory of systems that were inspected.

## The chain this must support

```text
agent execution ─► requirement ─► work item ─► implementation ─► test
      │                                                         │
      └──────────────────────────────── finding ◄────────────────┘
                                          │
                               follow-up ─► integration message ─► downstream system ─► resulting action
```

Each arrow either **transports** a record (verbatim), **transforms** it (a
contribution is appended), or **derives** a new subject (a new record whose
lineage names, and snapshots, the source). No arrow overwrites an originating
actor. No subject is attributed to an actor merely because that actor
touched something upstream.

## Architecture

```text
             ┌──────────────── Praxis (canonical) ────────────────┐
             │ actor · execution (EXE-) · contributions · lineage   │
             │ schemas/provenance-actor · artifact-provenance      │
             │ schemas/provenance-record (versioned interchange)   │
             │ schemas/conformance/provenance-record (fixtures)    │
             │ ros provenance identity --env · check-record        │
             └───────────────┬──────────────────────┬──────────────┘
          env: ROS_ACTOR_*,  │                      │ fixtures vendored with
          ROS_TELEMETRY_*,   │                      │ Praxis commit + sha256
          ROS_EXECUTION_ID   ▼                      ▼
 ┌─────────────┐  ┌─────────────┐  ┌──────────┐  ┌──────────┐  ┌─────────┐
 │ros-worker-  │  │ Dokimos     │  │ Aegis    │  │ Vigila   │  │ Ordo    │ …
 │daemon       │  │ measurement │  │ fault    │  │ follow-up│  │ request │
 │(launches    │  │ record      │  │ record   │  │ record   │  │ (audit- │
 │ agents)     │  │             │  │          │  │          │  │  only)  │
 └─────────────┘  └─────────────┘  └──────────┘  └──────────┘  └─────────┘
          each system: local codec in its own language/tier, JSON identical
          to the contract; no package reference to Praxis; works standalone
                                   │
                                   ▼
             echelon-registry: envelope v2 carries the Praxis actor +
             execution + provenance record; manifests v2 declare provenance
             capability; conformance runner
```

### Principles

1. **One identity model.** Every system's actor is the Praxis actor:
   - `kind` is `agent`, `human`, `automation`, `unknown`, or an `x-…`
     extension;
   - `id` is the stable identity;
   - `provider`, `model`, and `runtime` are present for non-humans, with
     `unknown` spelled out.

   A local representation is allowed only with an explicit, lossless,
   tested mapping.
2. **One portable form.** Provenance crosses a boundary as a
   `praxis.provenance-record`. The record is versioned by major version:
   - any minor version of a supported major is interpreted;
   - an unsupported major is carried verbatim;
   - a malformed record is rejected;
   - a legacy unversioned block is read as version 1.
3. **Execution identity propagates.**
   - `ros provenance identity --env` exports the actor and `ROS_EXECUTION_ID`.
   - Downstream systems key their contributions by that ID.
   - Without it, a system keys its own run as `EXE-<system>.<run>`, which can
     never be mistaken for a Praxis execution.
   - Orchestrators override the variables for every child actor they launch.
4. **Append, never replace.**
   - Transport is verbatim.
   - A representation change is recorded as `migrated`.
   - A content change is `modified`, `x-handled`, `x-resolved`,
     `x-remediated`, `x-validated`, or `x-dismissed`.
   - A derivation starts a new record, with the source listed in
     `derivedFrom` and snapshotted in `sources`.
   - `check-record --previous` is the executable definition of "destructive".
5. **Identity is provenance only.** It is not authentication, authorization,
   evidence, or evidence weight. Ordo keeps "who requested" separate from
   capability and evidence. EDF keeps it out of scoring. Strata keeps
   approval identity out of the signed body and never treats it as custody.
6. **Loose coupling.**
   - Fixtures are shared; code is not.
   - The registry describes capability; it is not a runtime dependency.
   - A system that lacks the capability resolves as `misconfigured` for
     callers that require provenance. It is never broken on its own.
7. **No invented history.**
   - Legacy records keep their absence.
   - Free-text `author_agent` / `created_by_agent` fields are reported as
     self-declared, never converted.
   - Frozen experiments are not touched.
8. **No secrets.** Credential-shaped values are refused in every provenance
   field. `--env` exports only the non-secret identity whitelist.

### How each question is answered

| Question | Where the fact lives |
|---|---|
| Which agent produced this? | the `created` contribution's `actor` on the subject's record |
| Which execution? | that contribution's key (`EXE-…`), joined to Praxis telemetry (cost, duration, tokens) |
| Which agent modified it? | later contributions (`modified`, `x-…`), each with its own key |
| Which agents create findings? | `created` contributions on Aegis fault, Dokimos snapshot, and EDF sidecar records |
| Which agents resolve them? | `x-resolved` / `x-remediated` / `x-validated` contributions on those records and on Vigila follow-ups |
| Which agents generate rework? | subjects whose `created` actor differs from later `modified` actors (Praxis `provenance audit`: `agentToAgentRevision`, `humanCorrectionOfAgentWork`) |
| Which models' requirements are revised by humans? | `RQ` records: `created` actor model versus a later human `modified` (Praxis `provenance audit`) |
| Which agents produce code with recurring findings? | a finding's `derivedFrom`/`sources` names the implementation subject and its `created` actor, never merged into the finding's authorship |
| Agent cost or time per feature? | execution keys joined to Praxis telemetry and to Chrona `originExecution` |
| Which agent combinations work well? | the set of distinct actors across a chain (`ProvenanceRecord.chain`) joined with outcomes |

No analytics platform is built. These are joins over recorded facts.

## Inventory

Legend: **C**, consumes provenance. **P**, produces provenance. **T**,
transports provenance. "Before" describes what was observed at the start of
this upgrade. The branch for every change is
`claude/echelon-provenance-upgrade-r4lr9c`.

<!-- INVENTORY-TABLE -->

## Versioning strategy

- **The interchange record.** It carries `contract` and a semantic `version`.
  Consumers implement one major version. A new minor may add optional fields,
  which older consumers preserve. A new major is carried verbatim by older
  consumers, which neither interpret nor extend it, and is introduced only by
  a new Praxis decision with a migration.
- **Downstream wire formats.** These evolve under each system's own rules:
  - Ordo `schemaVersion` bumps;
  - the Dokimos snapshot `1.1.0`;
  - the worker execution-record `schemaVersion`;
  - the additive, optional fields in Aegis `aegis/*/v1`;
  - the registry envelope `v2` next to a frozen `v1`.

  Each downstream format reads its previous version with provenance absent,
  never invented.
- **Fixture versions.** Each vendored fixture set records the Praxis commit
  and per-file SHA-256 it came from. Refreshing the set is a deliberate change
  that is reviewed and tested, not an automatic update.

## Legacy compatibility

- Records written before this upgrade stay valid and readable, with no
  provenance. Nothing is back-filled from git authorship, `author_agent`,
  `created_by_agent`, or timestamps.
- The bare registry projection `{"contributions": …}` is accepted as version 1.
- Registry envelope v1 remains valid. The v1→v2 mapping is documented, and
  v2→v1 is declared lossy, so a v1-only provider resolves as `misconfigured`
  for callers that require provenance.

## Adversarial review

The review looked for ways provenance could be:

- dropped;
- overwritten;
- forged;
- confused;
- duplicated;
- incorrectly inherited;
- incorrectly attributed;
- lost during integration;
- broken by schema upgrades;
- a source of unwanted coupling.

<!-- ADVERSARIAL -->

## Remaining limitations and follow-up work

<!-- FOLLOWUPS -->
