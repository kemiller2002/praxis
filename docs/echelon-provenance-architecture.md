# Echelon provenance architecture

How the Praxis agent identity and provenance contract travels through the
Echelon ecosystem. This document holds the cross-system architecture, the
repository inventory, and the per-system decisions for the upgrade tracked as
work item `FEAT-ECHELON-PROVENANCE`.

- Praxis model: [`agent-provenance.md`](agent-provenance.md), `DF-ROS-2026-A036`,
  `RQ-ROS-2026-A001`..`A012`.
- Cross-system contract: `DF-ROS-2026-A037`, `RQ-ROS-2026-A013`..`A019`,
  [`schemas/provenance-interchange.schema.json`](../schemas/provenance-interchange.schema.json),
  [`lib/provenance-interchange.mjs`](../lib/provenance-interchange.mjs),
  [`tests/fixtures/provenance-interchange/`](../tests/fixtures/provenance-interchange/).

## Goal

We want to trace this chain without losing the originating identity or anyone
who contributed later:

```text
agent execution -> requirement -> work item -> implementation -> test
  -> finding -> follow-up -> integration message -> downstream system -> resulting action
```

No single actor is presented as the author of the whole chain.

## Principles

1. **Praxis owns the semantics.** What an agent, an execution, a
   contribution, "unknown", and self-reported provenance mean is defined once,
   in Praxis. Other systems reference and carry that contract; they never
   redefine it.
2. **One block, embedded everywhere.** Provenance crosses boundaries as
   `praxis.provenance/1`: the front-matter contribution map plus `derivedFrom`
   lineage and a major-version tag. Each system embeds it in its own record
   format.
3. **Nothing is silently stripped.** A receiver keeps a supported block
   (including fields it does not model), carries an unsupported major
   verbatim, and rejects a malformed one loudly.
4. **Original actors are never overwritten.** A system that transports or
   transforms a record adds its own contribution (`transformed`, keyed by its
   own execution) and leaves every earlier entry untouched.
5. **Keys identify runs.** `EXE-...` is a Praxis execution, `EXT-<system>.<run>`
   is another system's run, and `CTB-...` is a non-agent contributor outside
   any execution. Two runs of one agent are two keys.
6. **Lineage is not authorship.** `derivedFrom` links records across systems
   (`aegis:fault/F-12`, `vigila:item/...`, `dokimos:snapshot/...`, Praxis IDs);
   the derived record's contributors are its own.
7. **Identity is not authority.** A recorded actor is self-reported
   provenance. It is never authentication (Tutela identity bindings),
   authorization (Ordo capabilities), or evidence quality (EDF claims,
   Dokimos/Aegis severity).
8. **Loose coupling.** No system gains a runtime or build dependency on Praxis
   (or on any other Echelon system) for provenance. Contract conformance comes
   from vendored fixtures with a recorded source commit and SHA-256.
9. **No secrets, no invention.** Provenance never carries credentials, never
   fabricates a provider, model, or version, and never back-fills history.

## The contract at the boundary

| Concern | Rule | Source |
|---|---|---|
| Actor | `{kind, id, provider?, model?, runtime?}`; non-humans spell unknown values as `"unknown"`; humans omit them | `RQ-ROS-2026-A001` |
| Execution | the contribution key: `EXE-`, `EXT-<system>.<run>`, or `CTB-` | `RQ-ROS-2026-A002`, `A013` |
| Contributions | append-only map; one `created`; same key merges only when the actor agrees | `RQ-ROS-2026-A004` |
| Roles | `discovered`, `measured`, `transformed`, `remediated`, `validated`, `resolved` beside the core operations | `RQ-ROS-2026-A014` |
| Lineage | `derivedFrom`, namespaced for foreign records | `RQ-ROS-2026-A008` |
| Version | `schema: praxis.provenance/<major>`; supported / unsupported / malformed | `RQ-ROS-2026-A015` |
| Propagation | envelope, flags, `ROS_ACTOR*`, `ROS_TELEMETRY_*`, `ROS_EXECUTION_ID`; otherwise `unknown` | `RQ-ROS-2026-A016` |
| Secrets | credential-like values make a block malformed | `RQ-ROS-2026-A017` |
| Conformance | shared fixtures and the end-to-end scenario | `RQ-ROS-2026-A018` |
| Authority | never authentication, authorization, or evidence weight | `RQ-ROS-2026-A019` |

### Identity propagation

Every integration boundary passes two things:

1. **The originating provenance**: the record's existing `praxis.provenance/1`
   block, verbatim.
2. **The current actor and execution**: whoever is performing this
   transformation or request.

| Boundary | Originating provenance | Current actor / execution |
|---|---|---|
| Artifact file (Markdown) | front matter `provenance` + `derived_from` | `ros provenance record` inside the execution |
| Generated registries | projected `provenance` block | not applicable (a projection, not an action) |
| Work events / adapter publish | `actor` on events, `artifact.contributed` | the event's `actor` and `telemetryExecutions` |
| Echelon integration executable | envelope v2 `provenance` | envelope v2 `actor` and `execution` |
| Echelon envelope v1 (legacy) | none | mapped losslessly: `system` becomes `automation`, `knownValue` unknown becomes `"unknown"`, not-applicable is omitted; key `EXT-run.<runId>` or `EXT-op.<operationId>` |
| CLI of another system | `--provenance FILE` or equivalent | flags or `ROS_*` variables, else `unknown` |
| Child process / agent launch | lineage in the launch context | `ROS_ACTOR_KIND`, `ROS_TELEMETRY_*`, `ROS_EXECUTION_ID` (or the launcher's own run id) |
| Published catalogs (research-publisher) | block kept per document; lineage becomes graph edges | not applicable |

**Creating a record versus relaying one.** The received block always describes
what came in. What the receiver does with it depends on the operation:

- **Create** (`followup.create`, `time.record`, `billing.record`, and any other
  operation that makes a new domain record): the new record gets a **new**
  block.
  - The invoking actor is `created`, keyed by its execution.
  - The receiving system may add its own `transformed`.
  - `derivedFrom` names the source.
  - The received block is stored verbatim beside the new block as source
    provenance, and is never appended to. Otherwise the source's discoverer
    would appear to be an author of the follow-up.
- **Relay or update** of an existing record (the received block is that
  record's own history): the invoking actor is appended with a
  non-authorship operation (`transformed`, or a more specific role). It is
  never `created` when an originator exists.

The invoking actor is always recorded.

`EXT-run.*` and `EXT-op.*` are reserved pseudo-systems. They say honestly that
the executing system is unknown (only a run id or operation id is), and they
never impersonate a real system's run.

### Versioning

- The tag carries the major version only. Minor evolution is additive: a new
  operation, an optional field, or an `x-` kind. Major-1 readers tolerate and
  preserve it, and report unknown operation codes as warnings.
- A new major is needed only when an existing field changes meaning. Readers
  that do not know it carry the block verbatim and never merge into it.
- The Echelon Registry declares, per system, which majors it supports and
  whether it preserves unknown fields (`echelon.system/v2` `provenance`
  descriptor). An undeclared system is assumed **not** to preserve, so senders
  keep their own copy. It never fails the call: optional providers stay
  optional.
- Downstream systems pin the contract by vendoring the fixtures with the Praxis
  commit and SHA-256. Upgrading the contract means re-vendoring and re-running
  the conformance tests.

### Validation, by failure mode

| Failure | Detected by |
|---|---|
| Provenance accidentally removed | `preservationViolations` (reference library) and each system's round-trip tests; Praxis Git-based unattributed-modification detection |
| Original actor overwritten | append refuses re-attribution; preservation check compares actors per key |
| Contribution history replaced | preservation check: every earlier key, operation, and evidence entry must survive |
| Execution identity lost | agents must be keyed by `EXE-`/`EXT-`; the Praxis execution cross-check |
| Lineage confused with authorship | `created` only through contributions; lineage only in `derivedFrom`; tests in every system |
| Malformed actor | classification (`malformed`) at every boundary |
| Unsupported destructive transformation | unsupported majors are never appended to, and are compared byte-for-byte |
| Fabricated identity (where detectable) | contribution/execution cross-check in Praxis; the impersonation guards of `DF-ROS-2026-A036`; downstream systems accept identity only from explicit declarations |
| Secrets | credential tripwire in every codec |

Nothing requires historical records to invent provenance. A record without a
block is **unattributed**, which is valid.

## End-to-end scenario

`tests/fixtures/provenance-interchange/echelon-chain.json` is the cross-system
contract test. Every participating system replays its part of it:

1. Agent A (`openai/codex`, `EXE-…a1a1a1a1`) creates requirement
   `RQ-APP-2026-A001`.
2. Agent B (`anthropic/claude-code`, `EXE-…b1b1b1b1`) modifies it.
3. An implementation execution of agent B (`EXE-…b2b2b2b2`) creates change
   `git:commit/5e1f0c2`, derived from the requirement.
4. Dokimos (`EXT-dokimos.snapshot-…`) measures the change. The measurer is not
   the change's author.
5. An Aegis review by agent C (`google/gemini-cli`) creates and discovers
   security finding `aegis:finding/SF-0001`, derived from the change and the
   observation.
6. Vigila (`EXT-vigila.op-…`, automation) generates follow-up
   `vigila:followup/FU-0001`, derived from the finding.
7. A different execution of agent A (`EXE-…a2a2a2a2`) remediates the finding
   and resolves the follow-up.
8. CI (`EXT-github-actions.run-777-1`) validates the fix.
9. A human reviews the resolution (`CTB-…`).

After the replay, the chain can be reconstructed:

- the follow-up's lineage reaches the finding, the change, the observation,
  and the requirement;
- each record has its own originator, five different actors in all;
- the discoverer, remediator, and validator of the finding are three distinct
  actors;
- agent A's two runs stay two keys.

Praxis replays it from JavaScript (`tests/provenance-interchange.test.mjs`)
and from F# (`ProvenanceInterchangeTests`).

## Metrics readiness

No analytics platform is built. The recorded facts answer these questions:

| Question | Facts |
|---|---|
| Which agent produced this? / Which execution? | originator contribution: actor and key |
| Which agent modified it? | contributions with modifying operations |
| Which agents tend to create findings? | `created`/`discovered` on Aegis, Dokimos, and Vigila records, by `actor.id` |
| Which agents resolve them? | `remediated`/`resolved`/`validated` by actor |
| Which agents generate the most rework? | modifications of records originated by another actor (`agentToAgentRevision`, `humanCorrectionOfAgentWork`) |
| Which models produce requirements later revised by humans? | Praxis `provenance audit --json` facts: origin actor model, later human `modified` |
| Which agents produce code with recurring findings? | finding `derivedFrom` a change, joined to the change's originator |
| Agent cost/time per feature | contribution keys joined to Praxis telemetry (`EXE-`) and to Chrona time observations |
| Which combinations of agents work well? | co-contributors per record joined to finding and rework counts |

## Repository inventory

Discovery covered 25 repositories. Legend: **P** produces, **C** consumes,
**T** transports provenance.

| Repository | Relationship to Praxis | P/C/T | Preserves actor today? | Loses actor today | Change required | Outcome |
|---|---|---|---|---|---|---|
| praxis | canonical owner | P C T | yes (A036) | no interchange contract; `EXE`/`CTB` keys only | contract, versioning, conformance, export | **changed** (kemiller2002/praxis#78) |
| echelon-registry | integration routing spec | T | no | envelope v1 defines a competing actor (tri-state `knownValue`, `system` kind, no model/runtime; closed schema) | envelope v2 carrying the Praxis actor and block; lossless v1 mapping; manifest provenance descriptor; conformance harness | **changed** (kemiller2002/echelon-registry#1) |
| vigila | follow-up provider (`followup.*`) | C P | partly (`{type,name}` free text) | no provider/model/execution; roles collapsed; codec drops unknown fields; hard-coded `local` user | provenance block on items, envelope intake, role separation, lossless codec | **changed** (kemiller2002/vigila#18) |
| aegis | fault/security-finding library | P | category only (`RecoveryActor`) | no discovering/validation actor, no execution; resolution has no actor | provenance on faults and events; remediation/validation roles; lineage; Tutela projection | **changed** (kemiller2002/aegis#3) |
| dokimos | code-quality evidence | P | no (tool name only) | no measuring actor or execution; no artifact author | snapshot provenance; author ≠ measurer ≠ remediator ≠ validator | **changed** (kemiller2002/dokimos#1) |
| tutela | security evidence gate | C | authenticated bindings (separate concept) | Aegis evidence cannot carry contributions; `provenance` key means attestation | namespaced `contributionProvenance`, proven unable to change gate outcomes | **changed** (kemiller2002/tutela#2) |
| ordo | state-transition methodology/library | C | provider identity on observations only | no requester of a transition | requester provenance kept outside capability/evidence evaluation | **changed** (kemiller2002/ordo#36) |
| percepta | UI contracts + experiments | P | legacy free text; inconsistent legacy event identity | – | requirements and templates for future experiment provenance; frozen experiments untouched | **changed**: requirements and templates (kemiller2002/percepta#13) |
| echelon-diagnostic-framework | diagnostic research | P | free text `author_agent` | – | rule and validator: identity is never evidence weight; blinded material stays clean | **changed** (kemiller2002/echelon-diagnostic-framework#19) |
| chrona | time system (planned) | C | requirements only | CI attributes an Actions job to an agent | provenance on time observations; no fabricated time; CI actor fix | **changed** (kemiller2002/chrona#5) |
| summa | hub + billing (planned) | C T | bare `--actor` string | hub identity never recorded; CI mislabels automation | hub records its own actor and passes identity explicitly; billing provenance | **changed** (kemiller2002/summa#6) |
| conditor | installer for new repos | T | free text `--actor conditor` | launched agents get no identity; installed Praxis 3.1.4 predates provenance | provenance-readiness verification; automation identity; true agent identity for launched runtimes | **changed** (kemiller2002/conditor#1) |
| ros-workerdaemon | executes agents | P T | provider/model config only | child processes receive no identity; attempts not linked to Praxis executions | identity environment for agents; actor + `EXT-ros-worker` key on attempts | **changed** (kemiller2002/ROS-WorkerDaemon#4) |
| research-publisher | publishes Praxis research | T | `authorAgent` string | drops `provenance` and `derived_from`; mis-maps `author_agent` | preserve the block; lineage edges; legacy authors labelled self-declared | **changed** (kemiller2002/research-publisher#12) |
| forma | design system | – | n/a | – | none: provenance does not belong in tokens, CSS, or components; its "provenance trail" pattern displays data lineage | unchanged |
| folio | print components | – | n/a | – | none: export provenance is already caller-owned (EPC-RPT-076/077) | unchanged |
| limen | browser boundary kernel | – | n/a | – | none: runtime messages are application data | unchanged |
| strata | PostgreSQL schema management | – | n/a | – | none: `app.strata` is deterministic (NFR-001) and signatures are custody, not identity; callers record the execution beside the artifact digest | unchanged |
| forma-studio | design environment (unbootstrapped) | – | n/a | – | none yet: it gets the policy through Conditor/Praxis on bootstrap | unchanged |
| visual-engineering | installs UI research context | – | n/a | – | none: gets provenance through a Praxis upgrade | unchanged |
| communication-engineering | research program | P (legacy) | `author_agent` free text | – | none: frozen experiments pin hashes; new work adopts it through a Praxis upgrade | unchanged |
| signal | ROS pilot | C | – | – | none beyond a Praxis upgrade | unchanged |
| project-administration | docs hub | – | – | – | none beyond a Praxis upgrade | unchanged |
| echelon-foundry | website | – | – | – | none | unchanged |
| mercatus, echelon-organization-administration | requirements/business documents | – | – | – | none (no executable integration yet) | unchanged |

### Per-system decisions

- **Registry.** It routes and transports; it does not define identity.
  Envelope v2 carries the Praxis actor, the execution key, and the block. v1 is
  still accepted through the explicit mapping. `echelon.system/v2` lets a
  system declare which provenance majors it supports, whether it preserves
  unknown fields, and whether it propagates actor, execution, contributions,
  and lineage. An integration executable can then decide where information
  belongs without losing provenance. Absence of a provider or of a declaration
  never fails a caller.
- **Vigila** keeps these separate: the discoverer (`discovered`, often through
  lineage to the finding), the generating system (`created` by `echelon/vigila`
  or by the requesting actor), later handlers (`modified`/`remediated`), human
  reviewers and resolvers (`reviewed`/`resolved`), and the execution of each.
- **Aegis** records the discovering actor and execution, evidence provenance,
  affected-artifact lineage, remediation, validation, and later contributors.
  Its immutable event stream folds into a fault's accumulated block.
- **Dokimos** keeps four roles apart: artifact author (from the artifact's own
  recorded provenance only), measurement actor, remediation actor, and
  validation actor. Historical snapshots are never rewritten.
- **Tutela** accepts contribution provenance as a separate, non-authoritative
  field and proves it cannot change a gate decision.
- **Ordo** records who requested a transition, and tests that no requester can
  change an evaluation. Identity, capability, and evidence stay three
  concepts.
- **Percepta and EDF.** Experiment integrity overrides convenience. Provenance
  of experiment runs goes only into sealed, evaluator-side material. Frozen and
  preregistered inputs and outputs are never touched, and actor identity never
  adds evidentiary weight.
- **Chrona and Summa.** Execution identity joins agent work to time and
  billing. Time is never fabricated from provenance, and accounting never
  depends on Praxis being available.
- **Conditor and ROS worker.** Launchers give an agent only its true identity
  (runtime, provider, and model only when configured) and identify themselves
  as automation. Conditor verifies that an installed Praxis enables the
  provenance policy once a published release carries it.
- **Forma, Folio, Limen, Strata** stay unchanged by design. Provenance belongs
  to the record that stores or publishes their outputs, not to the
  presentation, document, runtime, or schema artifact itself.

## Adversarial review

An independent review tried to make provenance get dropped, overwritten,
forged, confused, duplicated, wrongly inherited, lost at integration, or
broken by schema change. Its confirmed findings, and the fixes that closed
them (contract revision 1.1, Praxis `c2657ef`, re-vendored everywhere):

| # | Finding | Fix |
|---|---|---|
| 1 | .NET `$` matched before a trailing newline. A `created\n` operation could smuggle a second originator past the F# codecs (Praxis, Aegis, worker). | All patterns anchored with `\z`. Newline cases added to the fixtures. |
| 2 | Hub, launcher, and worker identity (session ids, CI run id, runtime signals) leaked into another actor's execution. | Canonical `identity-environment.json`. Launchers remove every listed variable, then declare explicitly. Summa, Conditor, and the worker are fixed. |
| 3 | Reference `appendContribution` could return a block that `classify` rejects: credentials, back-dated entries, a merged `created`. | Appends credential-check their input and re-classify their output. Chrona and Summa store only library results. |
| 4 | Timestamp semantics diverged: JS, F#, and Python disagreed on Feb 30, 24:00, year 0, and sub-millisecond ordering. Python crashed on year 0. | Calendar-valid timestamps, millisecond ordering, never crash. Fixtures added. |
| 5 | Praxis F# treated JSON `null` as an absent field. | `null` is never absent. Fixtures added. |
| 6 | Aegis let an identity-less process claim an execution from `ROS_EXECUTION_ID`. | The execution is honoured only with a declared identity (Praxis already refuses this). |
| 7 | An unknown invoker could merge into a known actor's entry. v1 run ids from different senders could collide, and `EXT-op` escaping was not injective. | Unknown-identity merges are refused. v1 runs are namespaced by repository (REG-PROV-008). Key escaping is injective. |
| 8 | A same-key merge dropped incoming unknown fields and `last`. | Incoming unknown fields are kept (the existing value wins) and the later `last` is kept. |
| 9 | EDF blinding check missed author and agent keys and execution ids. | Full identity key set plus text patterns (EDF `d81e25b`). |
| 10 | Praxis front matter rejected operations from a later 1.x release. | Grammar-valid unknown operations are carried with a warning. |
| 11 | Aegis serialization bypassed redaction and could rewrite text inside a "verbatim" block. | Structural serialization. Blocks that match redaction rules are rejected, not altered. |
| 12 | research-publisher published malformed provenance as `null`, which looks unattributed. It also split author names on commas. | `provenanceWithheld`. Scalars stay single values. Catalog schema 1.2. |
| 13 | The worker schema copied and weakened the Praxis actor schema. The Conditor guidance check depended on an exact heading. | The worker uses the vendored actor schema. Conditor uses a tolerant guidance marker. |

A design issue surfaced as well: whether a receiver appends the invoker to a
received block, or gives a created record its own block. The rule is stated
above under "Creating a record versus relaying one". The registry, Vigila,
Chrona, and Summa now all implement it.

### Second pass

A second, read-only review ran the round-2 heads of every repository. It
compared 96 new classify inputs and append inputs across the JavaScript,
Python, and F# codecs. The codec cores agreed, but integration paths did not.
Contract revision 1.2 (Praxis) and a third round in each repository address:

| # | Finding | Fix |
|---|---|---|
| 1 | High. Vigila stored a credential taken from the follow-up payload's `context.source.ref` in `derivedFrom`. The item then could not be reloaded. | `addLineage` credential-checks and re-classifies (contract 1.2). Receivers reject the request on refusal. |
| 2 | The registry reference receiver had the same lineage bypass. | Same fix. |
| 3 | The registry and Vigila disagreed on the shape of `followup.create` `context.source`, and the lineage was silently lost. | Vigila's object shape `{ ref }` is canonical. The registry reads it. |
| 4 | The registry made an agent that updated an unattributed record its creator. | Only create capabilities record `created`. Updates record their own role and leave the origin unknown, as Vigila does. |
| 5 | Duplicate JSON member names: readers disagreed on the originator, and several threw. | Text is classified as text. A repeated name is malformed, and `classify` never throws. |
| 6 | Key escaping collided for characters outside the BMP. This was an incomplete fix of #7 above. | Escaping is per code point (`escapeKeySegment`). |
| 7 | The Dokimos CLI mixed flag and environment identities, and the run came from `ROS_EXECUTION_ID`. | One identity source (RQ-ROS-2026-A016 revision 1.2). |
| 8 | Aegis applied key-name redaction rules to free text, rejecting ordinary finding reasons such as "Session cookie ...". | Key rules apply to keys. Values get the contract credential check. |
| 9 | The EDF blinding check could still be bypassed (snake_case keys, escaped values, `CLAUDE_CODE_SESSION_ID=`). This was an incomplete fix of #9 above. | Keys are normalised, and patterns run on parsed values. |
| 10 | Codecs threw on unpaired surrogates. | They are malformed (contract 1.2). |
| 11 | Unicode whitespace and credential regex semantics diverged across languages. | ASCII-only semantics (contract 1.2). |
| 12 | Tutela's gate accepts malformed `contributionProvenance`. | Known limitation: the gate is a trust root and needs independent approval (see below). |
| 13 | Vigila read a stored `"provenance": null` as absent. | It is malformed. |
| 14 | A namespaced registry run key could equal an un-namespaced one. | `.` is escaped in every segment. |
| 15 | Registry envelope timestamps went through `Date.parse`, which depends on the host. | The contract's `parseTimestamp` is used. |

## Coupling review

- No repository imports Praxis code at runtime or build time for provenance.
  JavaScript systems vendor the reference library unchanged; .NET and Python
  systems implement a small codec. All are pinned by the shared fixtures.
- Every consumer treats an absent block as "unattributed" and an absent
  provider as `unavailable`. Core behaviour never depends on another system
  being present.
- The registry descriptor is advisory metadata. A missing descriptor is
  "undeclared", never an error.

## Known limitations

- Identity remains self-reported. Signing and attestation are deferred
  (`DF-ROS-2026-A036`).
- Praxis versions before `DF-ROS-2026-A037` reject the new operations and
  `EXT-` keys in front matter. No published Praxis release yet contains
  provenance (the latest published package is 3.1.4). Conditor therefore
  reports "not supported by the installed version" until a release is
  qualified.
- The .NET and Python codecs are local implementations of one contract, kept
  honest by shared fixtures, not a shared package.
- The credential tripwire recognises common token shapes only.
- Legacy records with free-text or inconsistent identity (for example
  Percepta events claiming `id: claude` under the Codex runtime, or Chrona,
  Summa, and EDF CI runs labelled `agent:chatgpt`) stay as they are, labelled
  legacy.
- Tutela's gate does not check `contributionProvenance`. Only the opt-in
  evidence CLI classifies it. Making the gate reject a malformed block changes
  a trust root and needs independent approval, like the missing Aegis rule in
  `PROVENANCE-AUTHORITY.json`.
- Aegis's default redaction rules reject any block with a `signature` field.
  This must be revisited once an attestation authority is chosen.

## Follow-up work

1. Publish a Praxis release containing `DF-ROS-2026-A036`/`A037`. Then qualify
   it in Conditor, which switches greenfield verification to "enabled".
2. Publish a shared codec (NuGet and npm subpath) and replace the local
   codecs, keeping the fixtures as the contract.
3. Upgrade Praxis in governed repositories (signal, visual-engineering,
   communication-engineering, forma, folio, limen, strata, project-administration)
   and adopt the `ros.json` provenance policy with a `requiredFrom` after
   their current HEAD.
4. Add a Tutela `PROVENANCE-AUTHORITY.json` rule for Aegis evidence. This is a
   trust-root change and needs independent approval.
5. Choose an attestation authority (GitHub OIDC, App identity, or signed run
   manifests) and add attestation fields to contribution entries.
6. Wire the ROS worker's execution engine to a work store so its attempt
   provenance is exercised end to end.
7. Build the analytics described under Metrics readiness on top of
   `provenance audit --json` and the downstream blocks.
8. Model Ordo's optional `requestProvenance` observation member in the
   `ros ordo ingest` projection and idempotency check. Today it is preserved
   in the raw copy only, so a re-ingest that differs only in that member
   reports `already-present`.
