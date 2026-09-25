# Agent Identity and Provenance

Praxis records **who** produced each meaningful piece of work -- which agent,
human, or automation, in which execution -- as structured, validated data on
the records themselves. This page is the reference for the model
(`DF-ROS-2026-A036`) and its requirements (`RQ-ROS-2026-A001`..`A011`).

## Why it exists

Repositories under Praxis are built by many contributors: agents from
different providers, several runs of the same agent, humans reviewing and
correcting, and CI automation. Git authorship attributes files to whoever
committed them, prose attributions are unqueryable, and inference ("this reads
like agent X") is not evidence. Praxis therefore records identity explicitly
so that you can answer, for any requirement, decision, finding, or work
transition: who created it, who changed it since, in which execution, with
which provider/model when known, and what it was derived from.

## Concepts

| Concept | Meaning | Where |
|---|---|---|
| **Actor** (`praxis.actor/1`) | Who acted: `kind` (`agent`, `human`, `automation`, `unknown`) plus identity attributes. | Every event, contribution, execution record, handoff. |
| **Stable identity** | `kind`, `id`, `provider` -- the same across runs (`openai-codex`, `claude-code`, `alice`). | Actor. |
| **Execution identity** | One run: `executionId` (the Praxis telemetry execution `EXE-...`), plus `model`, `runtime`, versions, `sessionId`. | Actor; the execution record under `.ros/telemetry/executions/`. |
| **Contribution** (`praxis.contribution/1`) | One attributable act on a record: `operation`, `at`, `actor`, optional `workItem`, `reason`, `evidence`, `basis`. | `provenance.contributions` on artifacts and backlog items. |
| **Provenance** (`praxis.provenance/1`) | The append-only, ordered list of a record's contributions. | Front matter; backlog items; registries. |
| **Lineage** | What a record was derived from, in the existing `derived_from` field. | Front matter. Never merged with authorship. |

### Identity versus execution identity

Two runs of the same agent share a stable identity and never share an
execution identity:

```text
agent openai-codex  execution EXE-20260925T100000000Z-1a2b3c4d   (Tuesday's run)
agent openai-codex  execution EXE-20260926T090000000Z-5e6f7a8b   (Wednesday's run)
```

Everything recorded during one run references that run's `executionId`, which
also keys its telemetry (tokens, cost, duration), so outcomes can be analyzed
per agent, per model, or per run.

### Identity versus authentication, provenance versus attestation

A recorded identity is **provenance, not proof**. `provider: openai` says the
recording process declared that provider; it does not prove OpenAI produced
the record. Every actor therefore carries `assurance`. Praxis records
`self-reported`. A stronger level supplied by an integration (for example a
CI OIDC attestation, a GitHub App identity, or a signed execution receipt) is
preserved verbatim and reported as not yet verified, so a verifier can be
added later without changing any record. Praxis never uses provenance for
authentication or authorization.

## Establishing identity (once per execution)

1. **Declare who you are** in the environment of the agent's process, once:

   | Variable | Purpose |
   |---|---|
   | `ROS_ACTOR_KIND` | `agent`, `human`, or `automation`. Inferred only for unambiguous agent runtimes (Codex, Claude Code, Gemini CLI, Copilot sessions) and CI (automation). |
   | `ROS_ACTOR` | Your stable id (`openai-codex`, `research-bot`, `alice`). Agents default to their runtime's name when unset. |
   | `ROS_TELEMETRY_PROVIDER`, `ROS_TELEMETRY_RUNTIME`, `ROS_TELEMETRY_MODEL`, `ROS_TELEMETRY_MODEL_VERSION` | Provider, runtime, and model when known and not auto-discovered. Leave unset when unknown -- never guess. |
   | `ROS_EXECUTION_ID` | Only when a process must join an execution it did not start. |

2. **Begin the work:** `./ros work begin --id ID --occurred-at NOW` creates the
   execution and records its actor.
3. **Confirm:** `./ros identity` shows exactly what Praxis will record and how
   the execution was bound.

```console
$ ./ros identity
agent claude-code (provider anthropic, model unknown, runtime claude-code) execution EXE-20260925T174822557Z-7d2ee77c
  kind: agent
  id: claude-code
  ...
  execution bound to active execution EXE-20260925T174822557Z-7d2ee77c
```

The execution binding picks the most recent **active** execution whose
recorded identity is compatible with yours (provider, runtime, session, and
agent id agree where both are known). A different agent -- or another session
of the same agent -- never inherits someone else's execution; it records
`executionId: unknown` until it begins its own.

## How records inherit identity

| Record | How the actor gets there |
|---|---|
| Work events (`.ros/events/events.jsonl`) | Automatically on `work begin/block/resume/complete`. The actor is part of the event id. |
| Execution records | Automatically when `work begin` creates the execution. |
| Backlog items (`.ros/work/queue.json`) | Automatically: `add` records `created`; `work update`, `work attach`, and backlog transitions record `modified` with a reason. |
| Canonical artifacts (requirements, decisions, evidence, hypotheses, experiments, journals, research packages, theories, missions) | `./ros provenance record PATH` after creating or materially changing the file. It inherits the actor and the single active work item. |
| Registries | `./ros registry build` projects artifact provenance verbatim. |
| Ordo handoffs | `./ros ordo handoff` adds `producedBy`. |

The pre-existing free-form fields -- `createdBy` on backlog items, `actor` on
`.ros/context/current.json`, and `author_agent` in front matter -- keep their
old meaning for compatibility. They are not provenance; the structured
`actor` and `provenance` blocks are authoritative.

`provenance record` chooses the operation from the repository itself: a file
with no committed version is `created`; any committed file -- including a
legacy file without provenance -- is `modified`. Pass `--operation reviewed`
or `--operation approved` for a review or approval, `--reason`, `--evidence
REF` (repeatable), and `--basis REF` (the prior version you worked from).

## Canonical serialization

Front matter (YAML) and JSON use the same structure and field names.

```yaml
---
id: RQ-ROS-2026-A042
title: Example requirement
status: accepted
created: 2026-09-25
derived_from:
  - RQ-ROS-2026-A017
provenance:
  contributions:
    - operation: "created"
      at: "2026-09-25T10:04:11.201Z"
      actor:
        kind: "agent"
        id: "openai-codex"
        provider: "openai"
        model: "unknown"
        runtime: "codex"
        executionId: "EXE-20260925T100000000Z-1a2b3c4d"
        assurance: "self-reported"
      workItem: "WI-0081"
    - operation: "modified"
      at: "2026-09-25T14:31:09.884Z"
      actor:
        kind: "agent"
        id: "claude-code"
        provider: "anthropic"
        model: "unknown"
        runtime: "claude-code"
        executionId: "EXE-20260925T142000000Z-9c8d7e6f"
        sessionId: "5d1c..."
        assurance: "self-reported"
      reason: "clarified acceptance criteria"
      evidence:
        - "EV-ROS-2026-A060"
    - operation: "approved"
      at: "2026-09-26T09:00:00.000Z"
      actor:
        kind: "human"
        id: "alice"
        assurance: "self-reported"
---
```

```json
{"type":"work.completed","workItem":"WI-0081","occurredAt":"2026-09-25T10:30:00Z",
 "actor":{"kind":"agent","id":"openai-codex","provider":"openai","model":"unknown",
          "runtime":"codex","executionId":"EXE-20260925T100000000Z-1a2b3c4d",
          "assurance":"self-reported"}, "...": "..."}
```

Required attributes by kind (explicit `unknown` when not known):

| Kind | Required |
|---|---|
| `agent` | `kind`, `id`, `provider`, `model`, `runtime`, `executionId` |
| `automation` | `kind`, `id`, `runtime`, `executionId` |
| `human`, `unknown` | `kind`, `id` |

Optional attributes (`modelVersion`, `runtimeVersion`, `sessionId`) appear only
when known. JSON Schemas: [`schemas/praxis-actor.schema.json`](../schemas/praxis-actor.schema.json)
and [`schemas/praxis-provenance.schema.json`](../schemas/praxis-provenance.schema.json).

## Contributors, authorship, and lineage

- Provenance is **append-only**. Recording a contribution never changes an
  earlier one; a retried identical contribution is not duplicated; a
  malformed block is refused rather than overwritten.
- `created` is recorded **once, first**. The original author is the `created`
  contribution, never the last modifier.
- **Lineage is not authorship.** If agent B writes a requirement based on
  agent A's, B records `created` on the new file and lists A's record in
  `derived_from`. `provenance show` reports both, separately.

`./ros provenance show ID-OR-PATH` reports the creator, contributors,
involvement, lineage, and each contribution. Involvement labels distinguish:

| Collaboration | Labels |
|---|---|
| Agent-created | `agent-created` |
| Human-created | `human-created` |
| Agent-created, human-approved | `agent-created`, `human-approved` |
| Human-created, agent-modified | `human-created`, `agent-modified` |
| Agent-created, another agent modified | `agent-created`, `agent-modified`, `agent-to-agent-revision` |
| Agent-created, human corrected | `agent-created`, `human-modified`, `human-corrected-agent-work` |
| Legacy record later modified | `creator-unknown`, ... |

## Validation and legacy policy

Configure in `ros.json`:

```json
"provenance": { "enforce": true, "requiredSince": "2026-09-25T18:00:00Z" }
```

| Finding | Severity | When |
|---|---|---|
| `invalid-kind`, `invalid-operation`, `invalid-timestamp`, `invalid-token`, `missing-field`, `duplicate-authorship`, `authorship-not-first` | error | Always -- malformed provenance is never legacy. |
| `missing-provenance` | error | Enforced, and the record was created at/after `requiredSince` (every record when unset). |
| `missing-creator` | error | Enforced new record whose contributions lack `created`. |
| `provenance-rewritten` | error | A committed contribution was removed or altered. |
| `unknown-actor-kind`, `unknown-actor-id`, `unbound-execution` (agent) | warning | Honest unknowns on a new record. |
| `unrecorded-modification` | warning | Enforced; an artifact changed since its committed version without a new contribution (local check). |
| `unknown-execution`, `non-chronological` | warning | An event names an `EXE-` id with no record here; contributions out of order. |
| `legacy-unattributed`, `unverified-assurance`, `unbound-execution` (automation) | info | Pre-cutoff or legacy-mode records; a preserved but unverified assurance level; installer/CI automation without an execution. |

Errors fail `./ros validate`. `./ros provenance validate [--json]` lists every
finding, including warnings and legacy records.

**Legacy and migration.** Without a `provenance` section a repository is in
legacy mode: nothing that predates provenance becomes invalid. To adopt it,
set `enforce: true` and `requiredSince` to the adoption time. Praxis never
back-fills history: an unattributed record stays unattributed, and a legacy
`author_agent` or `createdBy` string is reported but never converted into
structured provenance, because a free-form string cannot say whether its
subject was an agent or a human, or which run it was. Recording `created` for someone
else's legacy work would itself be fabricated attribution, so there is no
back-fill command. If reliable evidence establishes a legacy record's
authorship, record a `reviewed` contribution under your own identity whose
`reason` and `evidence` cite it; the record's creator stays structurally
unknown.

## Integrations

Provenance travels in one canonical shape. Consumers should carry the `actor`
object and `provenance` block **verbatim** rather than re-deriving them.

- **Event publication** (`adapter publish`): each event keeps its `actor`.
- **Registries**: `registries/*.json` include each artifact's `provenance`.
- **Ordo** (`ordo handoff`): `producedBy` names the packaging actor and run.
- **ROS, Vigila, Aegis, Dokimos, Percepta, EDF tooling, future Echelon
  systems**: accept or emit `praxis.actor/1`/`praxis.provenance/1` objects
  unchanged. Praxis depends on none of them.

## Metrics

`./ros provenance summary [--json]` reports, per actor, records created and
modified, operations by record kind, providers, models, and executions; plus
human corrections of agent work, agent-to-agent revisions, human-approved
agent work, derived records, and modification hotspots. Cost, duration, and
token questions join on `executionId` with telemetry (`./ros telemetry show
EXE-...`). Praxis does not own an analytics subsystem; the recorded model makes
those analyses possible.

## Examples

**Different providers, one repository.**

```sh
# Codex (auto-detected from CODEX_SESSION_ID; explicit stable id)
ROS_ACTOR=openai-codex ./ros work begin --id WI-0081 --occurred-at "$(date -u +%FT%T.000Z)"

# Claude Code (auto-detected from CLAUDE_CODE_SESSION_ID)
./ros identity

# A local model runner Praxis has never heard of
export ROS_ACTOR_KIND=agent ROS_ACTOR=local-researcher \
       ROS_TELEMETRY_PROVIDER=local ROS_TELEMETRY_RUNTIME=llama-runner

# CI automation (GitHub Actions without an agent session → automation)
```

**Human and agent collaboration.**

```sh
# An agent drafts a requirement
./ros provenance record requirements/RQ-ROS-2026-A042--example.md

# A human corrects it, then approves it
ROS_ACTOR_KIND=human ROS_ACTOR=alice ./ros provenance record requirements/RQ-ROS-2026-A042--example.md --reason "tightened scope"
ROS_ACTOR_KIND=human ROS_ACTOR=alice ./ros provenance record requirements/RQ-ROS-2026-A042--example.md --operation approved

./ros provenance show RQ-ROS-2026-A042
#   involvement: agent-created, human-modified, human-approved, human-corrected-agent-work
```

## Limitations

- Identity is self-reported; see *provenance versus attestation*.
- Canonical artifacts are attributed when an agent runs `provenance record`;
  a missed call on an already committed artifact is visible only as a local
  warning before commit.
- Execution binding needs telemetry enabled (`telemetry.enabled`); without
  it, agent actors record `executionId: unknown` and are warned about.
- `model` is recorded only when the runtime exposes it or the agent declares
  it; many runtimes do not, and Praxis records `unknown` rather than guess.
- The local web interface (`tools/ros_server.mjs`, project-administration
  profile) still writes work records through the retained Node library,
  without provenance. That profile therefore stays in legacy mode until its
  writes are routed through the CLI (backlog item `WI-0071`).
