# Agent identity and provenance

Praxis records **who or what produced each meaningful piece of work**: an agent,
a human, or an automated non-agent process. It also records **which run** did it.
Attribution does not come from Git authorship, prose, or inference.

The records live in three places, all extensions of the existing ROS
architecture:

| Record | Where | What it answers |
|---|---|---|
| **Execution identity** | `.ros/telemetry/executions/EXE-*.json` → `identity` (now with `actorKind`) | Which run, which agent, provider, model, runtime, and session |
| **Action provenance** | `.ros/events/events.jsonl` → `actor` on every work event; `artifact.contributed` events | Who performed a transition or contribution, when, in which execution, and on which state |
| **Artifact provenance** | front matter `provenance.contributions` on canonical artifacts (requirements, decisions, evidence, …) | Original creator, every later contributor, and each contributor's execution, reason, and evidence |

Requirements for this capability: `RQ-ROS-2026-A001` through `RQ-ROS-2026-A012`
(`research/requirements/`). Decision: `DF-ROS-2026-A036`.

## Why

A repository shared by several agents and humans needs to answer these questions
without anyone's memory:

- who created a requirement and who changed it since;
- which execution produced a finding, a decision, or some evidence;
- whether the result came from an agent, a human, or CI;
- whether a human corrected an agent's work, or one agent revised another's.

Metrics over agents need the same records: output, rework, defects,
validation failures, cost and duration by agent, model, or provider. See
[Metrics](#metrics).

## Identity, execution, and actor

Praxis records identity at two levels that it keeps separate.

- **Stable identity: the actor.** This identifies *who*, and it is the same
  across runs. It is a small, portable object:

  ```json
  {"kind":"agent","id":"openai/codex","provider":"openai","model":"gpt-5-codex","runtime":"codex"}
  ```

  - `kind` is `agent`, `human`, `automation` (CI or another deterministic
    non-agent process), or `unknown`. A namespaced `x-...` extension is also
    allowed.
  - `id` is the stable identifier:
    - an explicit `--agent`/`--actor`/`ROS_ACTOR` value always wins;
    - otherwise a non-human actor whose provider and runtime are both known
      is identified as `provider/runtime`;
    - otherwise the id is `unknown`.
  - `provider`, `model`, and `runtime` are present for every non-human actor,
    as the literal `"unknown"` when not known. They are omitted for a human,
    because they do not apply. "Unknown" and "not applicable" are never
    conflated.

- **Execution identity: the run.** This identifies *which run*. It is the
  existing `EXE-<timestamp>-<random>` telemetry execution ID, created by
  `work begin`. Two runs of the same agent always get two execution IDs, and
  everything produced in one run carries that run's ID.

A contribution is keyed by the execution that produced it. Contributions from
two runs of the same agent therefore share an actor `id` but have different
keys. They never merge, and neither is ever mistaken for the other.

### How an agent establishes identity: once per execution

1. **Start the execution.** Run `./ros work begin --id ID --occurred-at NOW`.

   Identity is discovered from a small whitelist of non-secret environment
   variables, and explicit declarations always win:

   | Source | Examples |
   |---|---|
   | Flags on `work begin`/`resume`/`block`/`complete`, `add`, and `telemetry start` | `--actor-kind`, `--agent`, `--actor`, `--provider`, `--model`, `--runtime` |
   | Environment variables set once per session | `ROS_ACTOR_KIND`, `ROS_ACTOR`, `ROS_TELEMETRY_PROVIDER`, `ROS_TELEMETRY_MODEL`, … |
   | Known agent runtimes | Codex, Claude Code, Gemini CLI, Copilot |
   | Known CI | GitHub Actions resolves as `automation` |

   Nothing is guessed. A local model server (`OLLAMA_HOST`) proves nothing
   about who is acting, so it implies no actor kind.

2. **Check who you will be recorded as.** Run `./ros provenance identity`. It
   prints the resolved actor, the mechanism that determined it, and the active
   executions.

3. **Work normally.** Work events carry the actor automatically. To attribute an
   artifact you created or materially changed, run:

   ```bash
   ./ros provenance record --path research/requirements/RQ-...md --operation created \
     [--reason "why"] [--evidence EV-...] [--derived-from ID]
   ```

   The command inherits the actor from the active execution record, so the
   agent does not repeat its identity.

4. **Finish.** `./ros work complete …` then `./ros validate`, which catches
   missing or broken provenance.

A recording inherits the execution's identity only when the recording process
is plausibly that same run. ROS never attributes a contribution to another
actor's execution, or to another run of the same agent. The rules are:

- **The process must have an identity of its own.** The identity can come from
  a flag, from `ROS_ACTOR`/`ROS_ACTOR_KIND`, or from a detected agent runtime.
  A process with no identity at all, such as a plain terminal, never inherits
  an execution implicitly: it must declare itself, or name the execution with
  `--execution`.
- **The identity must agree with the execution's actor.**
- **Session and run IDs must match.** When both sides know a session,
  conversation, or CI run ID, the two must be equal. A second Claude Code
  session, a second Codex thread, or a second CI run is a different run.
- **Implicit inheritance needs positive evidence.** A shared run key, or the
  same known stable actor id, must show the process is that run. Declaring
  only `ROS_ACTOR_KIND=agent` agrees with every agent, so it proves nothing.
- **`--execution` is an explicit assertion.** Naming an execution means "I am
  acting within this run". A declared process must still agree with that
  execution. A process with no identity is taken at its word, so only the
  actor actually running the execution should use it; everyone else should
  declare themselves.
- **An agent must record inside its own execution.**
- **Several active executions.** ROS picks the one that matches the recording
  process, or asks for `--execution`.
- **Outside any execution.** A declared human or automation acting outside any
  execution (for example, a human approving while an agent is mid-run)
  contributes under a `CTB-<UTC day>-<actor digest>` key. One actor's
  recordings on one day form a single entry that merges operations and
  advances `last`, exactly like an execution's entry.

## Identity is provenance, not authentication

Everything described here is **self-reported provenance**. A record saying
`provider: openai` does not prove that OpenAI produced anything, and
`kind: human, id: kevin` does not authenticate Kevin. ROS guards against
*accidental* misattribution:

- it resolves identity only from the current process;
- it never inherits another run's actor. The legacy "last actor" field in
  `.ros/context/current.json` is not provenance and is never used for it;
- it cross-checks every contribution against its execution record;
- it refuses re-attribution.

ROS does **not** defend against a malicious process that lies about itself.

The model leaves room for stronger **attestation** later, without redesign:

- Each contribution is a keyed mapping entry, so a future signer can add
  fields such as `attestation` or `signature`. `provenance record` edits the
  front matter surgically and never re-renders another contributor's entry,
  so unknown fields survive. The test suite covers this.
- The execution record is the natural place for an external execution receipt
  or CI attestation (GitHub Actions OIDC, a GitHub App installation identity, a
  signed run manifest). The actor already distinguishes `automation` from
  `agent`.
- Events carry `previous.sha256` and `current.sha256` content digests, which
  a signature can cover.

Signed events, verified execution receipts, and key management are deferred.
They belong behind the adapter boundary or in an attestation service, not in
ROS core; see `DF-ROS-2026-A036`.

## Artifact provenance

### Canonical serialization

```yaml
provenance:
  contributions:
    EXE-20260925T194000000Z-ab12cd34:        # the execution that contributed
      operations: [created, modified]
      at: 2026-09-25T19:40:00.000Z           # first operation by this execution
      last: 2026-09-25T20:05:00.000Z         # optional: its latest recorded operation
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Initial capture"
      evidence: [EV-ROS-2026-A049]
    CTB-20260926T090000000Z-5f2e19aa:        # a human, outside any execution
      operations: [approved]
      at: 2026-09-26T09:00:00.000Z
      actor:
        kind: human
        id: kevin
derived_from: [EV-ROS-2026-A049]             # lineage (existing reference field)
```

**Why a mapping and not a list.** Contributions are a mapping keyed by
execution (`EXE-…`) or contribution (`CTB-…`) ID. The repository's front-matter
reader supports nested mappings but not lists of mappings. The keyed form also
makes "one entry per execution" a structural property.

**Operations.** `created`, `modified`, `reviewed`, `approved`, `superseded`,
and `migrated`, plus `x-...` extensions. At most one contribution may claim
`created`, and nothing may precede it.

The **role operations** `discovered`, `measured`, `transformed`, `remediated`,
`validated`, and `resolved` (`RQ-ROS-2026-A014`) let other Echelon systems say
which part an actor played: who found a problem, who measured something, which
integration carried a record into another representation, who fixed it, who
confirmed the fix, and who closed it. They never transfer authorship, and
`discovered`, `measured`, `reviewed`, `approved`, and `validated` do not count
as modifying the artifact.

**Keys.** `EXE-...` is a Praxis execution and `CTB-...` a non-agent
contribution outside any execution. `EXT-<system>.<run-id>`
(`RQ-ROS-2026-A013`) is a run owned by another Echelon system, named by its
registry id (for example `EXT-dokimos.snapshot-20260926-01`). Praxis carries
foreign keys verbatim, cannot cross-check them, and reports them in
`provenance audit` only.

**Timestamps.** `at` is an ISO-8601 UTC timestamp.

**Registries.** Generated registries (`registries/*.json`) project the whole
block, so provenance is queryable without parsing Markdown.

### How contributors accumulate

- **Additive only.** `provenance record` appends a new entry, or merges a new
  operation into the same execution's own entry. It never replaces or rewrites
  another contributor's entry.
- **Idempotent.** Re-recording an identical call changes nothing and writes no
  event. This also covers a human's `CTB` contribution. A later recording by the same execution updates that entry's `last`
  timestamp, so every recorded modification stays visible even when the
  execution had already recorded the same operation. This matters when an
  agent commits several times within one execution.
- **Refused edits.**
  - It refuses to re-attribute an execution to a different actor.
  - It refuses a second `created`, and a `created` recorded after existing
    history.
  - It refuses to touch provenance that is already malformed.
  - It refuses to rewrite a `derived_from` value it could not write back
    unchanged.
- **Verified before writing.** Before writing, it re-reads the result and proves
  four things: the provenance reads back as intended, `derived_from` reads
  back as exactly the existing list plus the new references, no other
  front-matter field changed, and the document body is byte-for-byte
  unchanged, including line endings.
- **Reasons.** A reason is a single line; line breaks become spaces.
- **Evented.** Every recorded change also appends an `artifact.contributed`
  event carrying:
  - the actor and the execution;
  - the operation, reason, and evidence;
  - `derivedFrom`;
  - the artifact's id, path, and `version`;
  - `previous.sha256` and `current.sha256`, which record the state the
    contribution operated on and the state it produced.

### Human and agent involvement

`./ros provenance show ID` derives involvement from the recorded contributions,
never from "last modifier":

| Label | Meaning |
|---|---|
| `agent-created` | an agent created it; nothing since |
| `human-created, agent-modified` | a human created it; an agent changed it |
| `agent-created, human-approved` | an agent created it; a human approved it |
| `agent-created, agent-modified` | another agent revised it (`agentToAgentRevision: true`) |
| `agent-created, human-modified` | human correction of agent work (`humanCorrectionOfAgentWork: true`) |
| `origin-unknown, …` | contributions exist but none records the creation (legacy artifact later modified) |
| `unattributed` | no provenance recorded (legacy); nothing is inferred |

### Derived artifacts: authorship versus lineage

When agent B writes an artifact based on agent A's artifact, the two facts are
recorded separately:

- **Authorship.** B's execution is the `created` contribution of the new
  artifact.
- **Lineage.** The new artifact's existing `derived_from` reference field names
  A's artifact. Use `--derived-from` on `provenance record`.

`provenance show` resolves each lineage source to its own originator, and lists
the reverse direction as `derivatives`. It never merges the source's authors
into the derivative's. Lineage may name a foreign, namespaced reference from
another system (for example `ordo:resolution/r-17`), so lineage crosses
integration boundaries.

## Validation

`./ros validate` includes provenance.

- **Errors** fail validation.
- **Warnings** are printed (and emitted with `"severity":"warning"` in
  `--json`) but do not fail validation.
- **Informational findings** appear only in `./ros provenance audit`.

| Situation | Severity |
|---|---|
| Malformed provenance block (unknown kind or operation, bad timestamp, agent without an execution key, an agent actor missing provider/model/runtime, duplicate or late `created`) | error |
| A contribution's actor contradicts its execution record (impersonation or copy-paste) | error |
| Contribution evidence names a non-existent artifact ID | error |
| Enforced policy: artifact created on or after `requiredFrom` with no provenance | error |
| Enforced policy: requirement (`RQ`, configurable) created on or after `requiredFrom` without a `created` contribution | error |
| Enforced policy: new artifact whose `updated` date is more than one day after its latest contribution (unattributed modification; the one-day tolerance absorbs local-date versus UTC skew) | error |
| Enforced policy: new artifact changed since the base revision while its contributions did not change (unattributed modification, detected from Git independently of dates; the base is the working tree against `HEAD`, or everything since `ROS_BASE_REF` in CI) | error |
| Invalid `ros.json` `provenance` block (enforced without a date) | error |
| Malformed `actor` on an event | error |
| Contribution names an execution with no local record (imported, pruned, or mistyped) | warning |
| Enforced policy: new non-requirement artifact without a `created` contribution | warning |
| Legacy artifact updated on or after `requiredFrom` without a recorded contribution | warning |
| Legacy artifact changed since the base revision without a recorded contribution | warning |
| Legacy artifact (created before `requiredFrom`) with no provenance, including any self-declared `author_agent`/`created_by_agent`/`owner_agent`/`source_author` | info |
| Legacy event that records executions but no actor | info |

### Policy and legacy repositories

Provenance is enforced per repository in `ros.json`:

```json
"provenance": {"version": "1.0.0", "enforce": true, "requiredFrom": "2026-09-25", "requireOriginator": ["RQ"]}
```

- **No `provenance` block.** Nothing is required, so an upgraded repository
  keeps validating exactly as before. Any provenance that is present is still
  checked structurally.
- **Enforced.**
  - Every canonical artifact whose `created` date is on or after
    `requiredFrom` must be attributed.
  - Artifacts created earlier are **legacy**. They stay valid and readable, and
    are never required to acquire invented history.
  - Legacy authorship fields such as `author_agent` are free-text,
    self-declared, and unverified. `audit` and `show` report them under that
    label; ROS never converts them into structured provenance.

**Migration** is deliberately a no-op. ROS does not rewrite history and does
not infer authorship from style, timestamps, filenames, or Git metadata. A
legacy artifact gains provenance only when a real contributor records a real
contribution: its first modification after the policy date adds an
`origin-unknown` history whose later entries are known.

**Legacy records elsewhere.**

- Execution records written before `identity.actorKind` existed are projected
  from the discovery mechanism the record itself preserved
  (`provenance.sources`), which is recorded evidence, or else as `unknown`.
- Events written before `actor` existed remain valid.
- Backlog items without `createdByActor` remain valid.

## Integrations and export

Provenance travels with data rather than being stripped at a boundary.

- **Work adapter publication.** `./ros adapter publish` copies events verbatim,
  so the external work system receives `actor` on work events and the full
  `artifact.contributed` records.
- **Registries.** Generated registries include each artifact's `provenance`
  block and `derived_from`.
- **Portable files.** The artifact file itself carries its contributors, so
  copying it to another repository or system keeps its origin. A receiving
  repository without the originating execution records reports those
  contributions as a *warning*, not an error, and keeps them intact.
- **Ordo.** Ordo resolution observations already carry `provider`
  `{id, model, …}`, and assessments carry a provider-neutral `assessor`. ROS
  preserves raw Ordo records verbatim.
- **Other Echelon systems.** Provenance crosses a system boundary as the
  versioned interchange block described below. There is no dependency on any
  other system: Praxis remains independently usable, and other systems need
  only the JSON contract. See
  [`echelon-provenance-architecture.md`](echelon-provenance-architecture.md)
  for how each Echelon system carries it.
- **Git host neutrality.** Nothing here assumes GitHub. `github-actions` is one
  whitelisted automation runtime among others, and a future attestation from a
  GitHub App or OIDC token would be one attestation source among others.

## Interchange across Echelon systems

`DF-ROS-2026-A037` and `RQ-ROS-2026-A013`..`A019` define how provenance leaves
Praxis and comes back without being stripped, forked, or corrupted.

**The block.** `./ros provenance export ID|PATH` prints an artifact's provenance
as `praxis.provenance/1` (`schemas/provenance-interchange.schema.json`):

```json
{"schema":"praxis.provenance/1",
 "subject":{"id":"RQ-APP-2026-A007","path":"research/requirements/RQ-APP-2026-A007--x.md"},
 "contributions":{"EXE-20260925T194000000Z-ab12cd34":{"operations":["created"],"at":"2026-09-25T19:40:00.000Z",
   "actor":{"kind":"agent","id":"anthropic/claude-code","provider":"anthropic","model":"unknown","runtime":"claude-code"}}},
 "derivedFrom":["EV-APP-2026-A002"]}
```

Other systems embed the same block in their own records (a follow-up, a fault
event, a quality snapshot, an execution envelope). It has exactly the
contribution shape of front matter, so a block written by another system can be
placed into a Praxis artifact and validated unchanged.

**Receiving rules** (every implementation, `RQ-ROS-2026-A015`):

| Verdict | When | What the receiver does |
|---|---|---|
| `supported` | `praxis.provenance/1`, or no tag with a `contributions` map (the registry projection) | keeps it, preserving unknown fields; an unknown operation code is tolerated, reported, and kept verbatim |
| `unsupported` | another major, such as `praxis.provenance/2` | carries it verbatim; never interprets, merges into, or rewrites it |
| `malformed` | anything else, including a credential-like value anywhere | rejects it at the boundary with an error; never drops or repairs it silently |

Appending follows the rules of `provenance record`: the same key merges and
advances `last` only when the actor agrees, another contributor is never
replaced, and a second or late `created` is refused.

**Contract revision 1.1** closes gaps found by the cross-system adversarial
review. Every implementation must also meet these rules:

- **Exact matching.** Keys, codes, kinds, and tags are matched exactly. A
  trailing newline makes the block malformed. In .NET, anchor patterns with
  `\z`, not `$`.
- **Timestamps.**
  - They must be calendar-valid: year 0001-9999, no February 30, no `24:00`.
  - They are ordered at millisecond precision. Extra fraction digits are
    truncated.
- **Null is not absence.** A field present with the value `null` is
  malformed; it never means "absent" or "not applicable".
- **Appends stay valid.** An append must itself classify as `supported`:
  - it never admits a credential;
  - it never admits a contribution dated before the creation;
  - it never adds a second originator.
- **Merging the same key.**
  - The incoming contribution's unknown fields are kept; the existing entry
    wins on conflict.
  - `last` becomes the later of the two times.
  - An actor with unknown identity cannot extend an entry held by a known
    actor.
- **Unknown operations.** Praxis's own front-matter reader carries a
  grammar-valid operation it does not know as an extension and warns; it does
  not reject it.
- **Launching for another actor.** A launcher, hub, or worker that starts a
  process for another actor first removes every identity variable in
  `tests/fixtures/provenance-interchange/identity-environment.json` (exported
  as `IDENTITY_ENVIRONMENT_VARIABLES`). Only then does it set that actor's
  explicit identity.

**Contract revision 1.2** closes gaps found by the second adversarial review.
New fixtures `text-cases.json`, `envelope-key-cases.json`, and
`lineage-cases.json` pin them, and `cases.json` adds the whitespace and
credential cases.

- **Well-formed text.** A block received as JSON text is malformed, whatever
  its major version, when:
  - it is not valid JSON;
  - it repeats a member name within any one object; readers disagree about
    which duplicate wins, so a second `created` could be smuggled past one of
    them;
  - it holds an unpaired UTF-16 surrogate, which has no UTF-8 form and cannot
    be carried verbatim.

  The reference and Praxis read text through `classifyText`. `classify` never
  throws.
- **ASCII whitespace only.**
  - "Blank" means empty after trimming tab, LF, VT, FF, CR, and space. Every
    other character is content, including U+0085, U+FEFF, and U+001C.
  - Credential patterns use no `\b`, `\s`, or case folding. Their meaning
    differs between JavaScript, .NET, and Python.
- **Lineage is checked like contributions.** `addLineage` returns
  `{ ok, block }` or `{ ok: false, error }`:
  - it refuses a credential, a blank reference, and a block that is not
    supported;
  - its result must classify as supported.

  A receiver that derives lineage from a payload uses it and rejects the
  request on refusal. `ros provenance record` refuses a credential in
  `--reason`, `--evidence`, or `--derived-from` before writing.
- **Key segments.**
  - `escapeKeySegment` escapes per Unicode code point. Every code point except
    ASCII letters, digits, and `-` becomes `_xx` per UTF-8 byte, so `.`, `_`,
    and characters outside the BMP are escaped injectively.
  - A namespaced key `EXT-run.<namespace>.<id>` can therefore never equal an
    un-namespaced one.
  - An id that is empty or not well-formed Unicode cannot form a key; the
    envelope is rejected.
- **One identity source.** A system that cannot check an execution against its
  execution record takes the actor wholly from one source:
  - An explicit declaration (flags, `--actor-json`, an envelope) replaces the
    environment's identity field by field and completely. It also does not
    inherit `ROS_EXECUTION_ID`; the execution must be declared with it.
  - `ROS_EXECUTION_ID` is honoured only together with an identity declared in
    the same environment.

  Praxis itself may combine sources, because it verifies the named execution
  against its record (the actor must agree) before it attributes anything.

**Versioning.** The tag names only the major version. Adding an operation, an
optional field, or an `x-` kind is a minor change: older major-1 readers
tolerate and preserve it. Anything that changes the meaning of an existing field
is a new major, which older readers carry without interpreting. Praxis's own
front-matter validator knows the vocabulary of its release, so an artifact
carrying newer operations needs a Praxis version that knows them.

**Propagating the current actor and execution** (`RQ-ROS-2026-A016`). A tool
launched inside a Praxis execution reads the execution from `ROS_EXECUTION_ID`
(the environment form of `--execution`) and the actor from explicit
declarations: an execution envelope, flags, or `ROS_ACTOR_KIND`, `ROS_ACTOR`,
and `ROS_TELEMETRY_PROVIDER`/`MODEL`/`RUNTIME`. When Praxis is available it may
also ask `./ros provenance identity --json`. Anything else is recorded as
`unknown`; nothing is guessed.

**Conformance.** `tests/fixtures/provenance-interchange/cases.json` pins the
verdict for 40 cases and `echelon-chain.json` is a replayable end-to-end
scenario (requirement, change, measurement, security finding, follow-up,
remediation, validation). Both the F# implementation and the dependency-free
reference library `lib/provenance-interchange.mjs` are tested against them;
downstream systems vendor them with the source commit and SHA-256.

**Never credentials, never authority.** A block containing something that looks
like a token, key, or bearer credential is malformed (`RQ-ROS-2026-A017`). And a
recorded actor never becomes authentication, authorization, or evidence weight
in any system (`RQ-ROS-2026-A019`).

## Metrics

This work adds no analytics subsystem. It records the facts that later
analysis needs.

`./ros provenance audit --json` emits:

- one row per (artifact, contribution): artifact id, kind, path, contribution
  key, execution, operations, time, actor, and whether it is the origin;
- per-actor summaries: artifacts, created, modified, reviewed or approved, and
  executions;
- coverage counts for artifacts, events, executions, and backlog items.

Joined with telemetry execution records (cost, duration, and tokens by
execution) and with defect or validation data by path or ID, these rows
support the following analyses by agent, model, or provider:

- requirements created or modified;
- findings;
- rework and modification hotspots;
- human corrections of agent work and agent-to-agent revisions;
- evidence generated;
- cost, duration, and outcomes.

## Examples

**Two agents and a human on one requirement.**

```bash
# Claude Code session (identity discovered from CLAUDE_CODE_SESSION_ID)
./ros work begin --id FEAT-9 --occurred-at "$(date -u +%Y-%m-%dT%H:%M:%S.000Z)"
./ros provenance record --id RQ-APP-2026-A007 --operation created --reason "From customer interview" --derived-from EV-APP-2026-A002
./ros work complete --id FEAT-9 --occurred-at … --evidence implementation=… --evidence tests=…

# Codex session, later (identity discovered from CODEX_SESSION_ID)
./ros work begin --id FEAT-12 --occurred-at …
./ros provenance record --id RQ-APP-2026-A007 --operation modified --reason "Tighten acceptance criteria"

# A human reviewer, outside any execution
ROS_ACTOR_KIND=human ROS_ACTOR=kevin ./ros provenance record --id RQ-APP-2026-A007 --operation approved

./ros provenance show RQ-APP-2026-A007
# involvement: agent-created, agent-modified, human-approved
```

**A local or future agent with no whitelisted environment** declares itself
once:

```bash
export ROS_ACTOR_KIND=agent ROS_ACTOR=acme-planner ROS_TELEMETRY_PROVIDER=acme ROS_TELEMETRY_RUNTIME=acme-cli
./ros work begin --id FEAT-3 --occurred-at …    # model left unset: recorded as "unknown", never invented
```

**CI automation** needs no declaration. Under GitHub Actions the actor resolves
as `{"kind":"automation","id":"github/github-actions",…}`, and a CI job that
records provenance is attributed to automation, not to an agent.
