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

### Changed systems

| Repository | Relationship to Praxis | C/P/T | Actor identity before this change | Commit |
|---|---|---|---|---|
| praxis | canonical owner | C/P/T | preserved in artifacts and events; no portable versioned form, no propagation, no successor check | `58cf46a`, `b32677d`, and the final commit on this branch |
| echelon-registry | integration routing specification | T | lost: the envelope actor was a second, incompatible model | `19c1820` |
| ros-workerdaemon | launches agents | P | lost or wrong: inherited stale `ROS_*`; logical model name; no runtime | `f0234cc`, `4d487c3` |
| conditor | installs Praxis; launches agents | P | confused: the agent worked inside Conditor's execution; kind unresolved | `3965a71` |
| vigila | follow-ups | C/P/T | collapsed: `{type, name}`; unknown fields dropped; every capture was human "local" | `2f1fc8d` |
| aegis | failures and security findings | C/P/T | lost: category-only actor, and only on acknowledge and recovery | `8230a69` |
| dokimos | code-quality measurements | C/P/T | lost: no measuring actor; unknown JSON dropped on read | `1c81b2d` |
| ordo | state transitions and decisions | C | absent: no requester; only the answering provider was recorded | `660dbc9` |
| strata | schema artifacts and deploys | P | lost: no compiler, signer, or approver recorded | `09926a4` |
| percepta | semantic UI contracts; experiments | P | lossy: free-text `author_agent`; a stale-environment misattribution in one execution record (not rewritten) | `eab0f9d` |
| echelon-diagnostic-framework | research, evidence, diagnostic benchmarks | P | lossy: free-text author and reviewer fields | `eeff623` |
| chrona | time and work records | C | no product code; the contract had no origin actor or execution | `ca40c10` |
| summa | accounting | C | no product code; "Execution ID" was unspecified | `8ffb5cc` |
| forma-studio | future design persistence | P | no code | `f720a0a` |

#### praxis

- **Requirements:** `RQ-ROS-2026-A013` (interchange record), `A014` (propagation), `A015` (no silent stripping); decision `DF-ROS-2026-A037`. Each is attributed with `ros provenance record`.
- **Schemas and boundaries:** new `provenance-record.schema.json` and conformance fixtures; `ros provenance check-record` and `identity --env`; credential guard.
- **Tests:** `ProvenanceRecordTests`, which cover the conformance cases, successor pairs, round trips, the end-to-end chain, front-matter import, impersonation, versions, and credentials.
- **Migration:** none; unversioned blocks are read as version 1.

#### echelon-registry

- **Requirements:** spec §5.1–§5.9 and the follow-up protocol's provenance clauses.
- **Schemas and boundaries:**
  - envelope v2, whose actor is the Praxis actor, with `execution` and a verbatim `provenance` record;
  - system manifest v2 with a `provenance` capability descriptor;
  - a registry schema;
  - the `agent.identity` v1 contract;
  - Praxis schemas vendored with digests.
- **Tests:** Ajv conformance runner, 265 checks, in a new CI workflow.
- **Migration:** v1 is frozen; v1→v2 is documented as keeping every known value; v2→v1 is lossy, so a v1-only provider resolves as `misconfigured`.

#### ros-workerdaemon

- **Requirements:** `WD-PROV-001`..`005`.
- **Schemas and boundaries:**
  - the launched agent's environment sets `ROS_ACTOR_KIND`, `ROS_ACTOR`, `ROS_TELEMETRY_PROVIDER`, `ROS_TELEMETRY_MODEL`, `ROS_TELEMETRY_RUNTIME`, and `ROS_TELEMETRY_RUN_ID` explicitly;
  - it scrubs inherited `ROS_EXECUTION_ID`, session variables, and runtime session variables;
  - execution-record schema v2 adds `actor`, `modelId`, `recordedBy`, and the foreign key `EXE-ros-worker.<exec>.<attempt>`;
  - the context file and telemetry carry the actor.
- **Tests:** 11 new tests, including a real child process that proves nothing stale leaks.
- **Migration:** v1 records still validate.

#### conditor

- **Requirements:** `CON-065`..`CON-070`.
- **Behavior:**
  - Conditor records itself as automation (`--actor-kind automation --actor conditor`).
  - It no longer runs `work start` for the agent, so the agent begins its own execution.
  - The launch instruction carries provenance guidance.
  - The descriptor declares `capabilities.provenance.since = 3.5.0` (unreleased). Planning warns when the selected Praxis predates it.
  - A `praxis:provenance` verification step checks for the `ros.json` policy block and the AGENTS.md section once the version is capable.
- **Tests:** 43 new checks.
- **Migration:** existing locks need a `conditor upgrade`, because the descriptor digest changed.

#### vigila

- **Requirements:** `VIG-DOM-050`..`055`, `VIG-PER-024`/`025`, `VIG-AGT-036`/`037`; `VIG-AGT-061` modified; ADR-0004; OQ-12.
- **Schemas and boundaries:**
  - item file v2;
  - the actor is a valid Praxis actor, with a lossless type↔kind mapping (`x-vigila-type` in the record);
  - history entries carry `execution`;
  - items carry a verbatim provenance record;
  - `Item.generate` and `Item.contribute` keep discoverer, generating system, handler, resolver, and validator distinct.
- **Tests:** e2e 06→09 through Vigila's own operations; 182 .NET tests pass.
- **Migration:** v1 items load with nothing inferred. Older builds refuse v2 items rather than silently stripping them.

#### aegis

- **Requirements:** `AEG-PROV-001`..`007`; `DF-AEGIS-2026-7A1C`.
- **Schemas and boundaries:**
  - optional `attribution`, `validatedBy`, and `provenance` fields on `aegis/fault/v1` and `aegis/event/v1`;
  - the fault record's discoverer is its `created` contribution;
  - lifecycle mapping: acknowledge → `x-handled`; recovery → `x-remediated`; verified resolution → `x-validated`; resolve → `x-resolved` or `x-dismissed`; reopen → `x-reopened`;
  - human ids are redacted unless the caller marks the actor as identified;
  - Tutela evidence carries attribution only when it is supplied.
- **Tests:** 412 F# and 13 C# tests; e2e 05→10 is reproduced exactly; golden payloads without provenance are byte-identical.
- **Migration:** additive; stored events read unchanged.

#### dokimos

- **Requirements:** R0.11–R0.18; `DF-GOV-012`; `DOK-PROV-001`.
- **Schemas and boundaries:**
  - snapshot and comparison schema 1.1.0 carries provenance; the measuring actor is `created`;
  - the measured commit is lineage; the code author appears only in `sources`;
  - `provenance finding|append|accept-baseline`;
  - identity is never taken from git; the `agent-*` heuristics are documented as not being authorship claims.
- **Tests:** 85 of 85; e2e 03→04 is reproduced.
- **Migration:** 1.0.0 baselines load unchanged.

#### ordo

- **Requirements:** `ORDO-NEXT-07` (`RP-SDE-2026-DE93`); `DF-SDE-2026-D68A`.
- **Schemas and boundaries:**
  - an optional `RequestedBy` on the decision request, the transition context, and the transition authorization, which is audit-only and never read by `evaluate`;
  - the `ordo.resolution-observation` wire schema moves to v3;
  - providers cannot see or set the requester.
- **Tests:** 20 new; 153 pass; invariance across 6 contexts × 6 requesters.
- **Migration:** v2 observations decode with the requester absent and are rewritten as v2. A pre-existing `Sde.Core.Tests` failure also occurs on `main`.

#### strata

- **Requirements:** PR-026, PR-027, NFR-013; `DF-STRATA-2026-E4B7`.
- **Schemas and boundaries:**
  - provenance lives in the artifact wrapper, outside the signed body;
  - `compile` records `created`; `sign` records `x-signed` with a key digest; `--approve` records `approved` and warns when the approver is an agent or unknown;
  - `--require-human-approval` is an opt-in policy, documented as self-reported, not authorization;
  - project sources are lineage, never SQL authorship.
- **Tests:** 753 against a live PostgreSQL; body bytes, digest, and signature are unchanged.
- **Migration:** legacy artifacts read with nothing invented.

#### percepta

- **Requirements:** PCT-036; `DF-PERCEPTA-2026-0001`.
- **Schemas and boundaries:** `percepta.experiment-run` 1.0.0 for **future** experiments, stored only in sealed evaluator-side run records.
- **Tests:** 14 of 14, including a leak guard over lane-visible and reviewer bundles and a check that every frozen digest is verified. No frozen path changed.
- **Migration:** EX-0001..0004 are not backfilled.

#### echelon-diagnostic-framework

- **Requirements:** Claim and Confidence Policy v1.1 (identity never changes weight, score, or confidence); `DF-EDF-2026-A001`.
- **Schemas and boundaries:** the `edf.provenance-sidecar` schema, stored outside every hashed set.
- **Tests:** 20 of 20, including scorer invariance under actor, provider, and model permutation using the unmodified frozen scorer.
- **Migration:** no backfill.

#### chrona

- **Requirements:** CHR-PROV-001..010.
- **Schemas and boundaries:** the receiver-owned `chrona.time-observation-origin` v1 contract: `originActor`, `originExecution`, a verbatim record, and lineage that survives split and merge. Agent origins still require review.
- **Tests:** 85 of 85 contract tests.
- **Migration:** none; there is no product code yet.

#### summa

- **Requirements:** INV-PROV-001..008.
- **Schemas and boundaries:** the `summa.billing-source-origin` v1 contract: Chrona origin lineage is kept per source through invoice-line grouping; issuance never requires Praxis.
- **Tests:** 78 of 78.
- **Migration:** none.

#### forma-studio

- **Requirements:** FS-PROV-001..010. The metadata envelope sits outside the presentation tree, and generated output is byte-identical with or without provenance.
- **Tests and migration:** none; there is no code yet.

### Inspected and intentionally unchanged

| Repository | Why unchanged |
|---|---|
| forma | Its outputs, `dist/` and `site-manifest.json`, are deterministic build artifacts. Execution data would pollute them. It gains the policy through `ros upgrade`: installed ROS 3.1.4 predates provenance. |
| folio | It renders documents supplied by other systems and consumes no Praxis provenance today. A rendering requirement waits for a consumer to pass contribution data. It gains the policy through `ros upgrade`. |
| limen | A runtime boundary and installer. Its integrity manifest is tool-owned, and adding actor data would pollute it. Its repository governance needs a ROS upgrade from 1.2.1. |
| visual-engineering, communication-engineering | Installers of deterministic context. They gain the policy through `ros upgrade`. Research records keep their self-declared `author_agent`, which is not converted. |
| echelon-foundry | A consumer-only static site. `praxis init` at a provenance-capable version will install the policy. |

Four repositories could not be inspected, because this session was denied access to them: `tutela`, `research-publisher`, `signal`, and `mercatus`. Tutela receives Aegis evidence, and research-publisher generates documents, so both are likely relevant. See the follow-up work.

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

Each weakness found and fixed has a regression test.

| Weakness | Fix |
|---|---|
| Confusion: a lineage snapshot describing a different subject than its key | now invalid (`invalid/snapshot-subject-mismatch.json`) |
| Schema downgrade: a consumer relabelling a newer minor record as an older version | the successor check refuses it (`successor/version-lowered`) |
| Forgery: a record claiming an execution this repository ran, but with another actor | `check-record` cross-checks against local execution records (`impersonationProblems`) |
| Stale inherited identity: an orchestrator's child inheriting `ROS_ACTOR` or `ROS_EXECUTION_ID` | the worker daemon scrubs and sets the variables per attempt (real child-process test) |
| Identity leaking into another run: Conditor handing its own execution to an agent | the agent now begins its own execution |
| Destructive reads: Dokimos and Vigila dropping unknown JSON on read and write | both preserve the raw record; Vigila bumps its file version so older builds refuse instead of stripping |
| Identity as authorization or evidence | Ordo invariance tests; the EDF scorer invariance test; Strata's approval policy documented as self-reported |
| Registry second identity model | envelope v2 adopts the Praxis actor; v1 is frozen and lossy toward v2 |
| Coupling | no repository references Praxis code or packages; the Chrona and Summa tests assert it; the registry makes the capability optional |

A live cross-system check was also run in this session:

1. `ros provenance identity --env` exported this session's real execution.
2. Dokimos measured its own commit under that execution.
3. `ros provenance check-record` validated the result, including the impersonation cross-check.
4. A remediation appended by a different declared actor passed the successor check.
5. A forged actor was rejected as impersonation.
6. A stripped contribution was rejected as destructive.
7. Chrona's independent JavaScript codec gave the same verdicts.

Residual risks that are accepted and documented:

- **Identity is self-reported.** A process that lies consistently cannot be detected until attestation exists.
- **`ROS_EXECUTION_ID` is only a hint.** A downstream system cannot verify it. Praxis catches mismatches only when the record returns to a repository that holds the execution.
- **Credential detection is shape-based.**

## Remaining limitations and follow-up work

1. **Release Praxis with this contract** (3.5.0). Then:
   - qualify it in Conditor, flipping `capabilities.provenance.status` to `released`;
   - run `ros upgrade` in the governed repositories to add the `provenance` policy and the AGENTS.md guidance: aegis, vigila, strata, chrona, summa, EDF, forma, folio, visual-engineering, communication-engineering, dokimos, and ordo. Ordo and Limen need their vendored ROS 1.2.1 replaced first.
2. **Re-vendor the conformance fixtures from the merged Praxis commit.** Ordo, Aegis, Dokimos, and ros-workerdaemon vendored `58cf46a`. The others vendored `b32677d`. All of them already implement the two rules `b32677d` added.
3. **Resolve the unreadable repositories.** Grant access to, or separately review, `tutela` (it receives Aegis evidence), `research-publisher` (it generates documents), `signal`, and `mercatus`.
4. **Praxis:**
   - Decide whether to promote `x-reopened` (Aegis) and `x-signed` (Strata) into the documented interchange operations.
   - Define a convention for a deliberately withheld identity. Aegis writes `[redacted]` for a human that is not identified.
   - Provide a way for a new run to take over an already-active mission (Conditor finding).
   - Decide the precedence between detected agent runtimes and GitHub Actions in CI.
5. **Wiring still missing:**
   - Vigila's agent command envelope (VIG-AGT-035/036) is unbuilt, so nothing calls `Item.generate` or `Item.contribute` yet.
   - Vigila does not check that a record's subject matches the item.
   - Aegis's `Lifecycle.Projected` does not fold attributions.
   - Dokimos CI does not pass `--subject-provenance` or record baseline acceptance.
   - Strata `deploy` prints the approval rather than persisting it in the artifact.
   - The ros-workerdaemon daemon does not persist execution records.
   - The Chrona and Summa contract tests are not in CI.
6. **Percepta:**
   - Add the `ros.json` provenance policy after EX-0004 completes.
   - Its 9 pre-existing `ros validate` errors on frozen EX-0004 files are unchanged.
   - Its stale-environment execution record (`EXE-20260926T064434719Z-3f3b6b20`) is left as recorded.
7. **Chrona:** `readiness-work.yml` stamps `--actor "agent:chatgpt"` from GitHub Actions. This needs a ROS version with `--actor-kind`; it is tracked as WI-0008.
8. **Attestation:** signed contributions and CI or OIDC execution receipts are still deferred (`DF-ROS-2026-A036`).
