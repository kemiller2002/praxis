---
id: DF-ROS-2026-A041
title: Remote execution is a typed, versioned Praxis request protocol, executed by the same command implementations, with GitHub Actions as a thin first adapter
status: accepted
version: 1.2.0
owners:
  - repository-governance
created: 2026-09-28
updated: 2026-09-28
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A021]
supporting_evidence: [EV-ROS-2026-A053]
related_documents:
  - RQ-ROS-2026-A021
  - EV-ROS-2026-A053
  - RQ-ROS-2026-A020
  - DF-ROS-2026-A007
  - DF-ROS-2026-A027
  - DF-ROS-2026-A036
  - DF-ROS-2026-A040
  - docs/remote-protocol.md
  - https://github.com/kemiller2002/praxis/issues/90
  - https://github.com/kemiller2002/praxis/issues/80
supersedes: []
superseded_by: []
tags: [remote-execution, protocol, idempotency, concurrency, provenance, security, github-actions, gh-90]
confidence: medium
provenance:
  contributions:
    EXE-20260928T073932249Z-d48161b9:
      operations: [created]
      at: 2026-09-28T07:44:28.575Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Architecture decision for the remote execution protocol and GitHub Actions adapter (work item GH-90)"
      evidence: [EV-ROS-2026-A053]
    EXE-20260928T081238572Z-5e89d3ec:
      operations: [modified]
      at: 2026-09-28T08:23:07.277Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Amendment 1.1.0: child-process execution with derived environment; repository opt-in; dirty tree is stale-ref (PRAXIS-REMOTE-03)"
    EXE-20260928T085516032Z-f48cc840:
      operations: [modified]
      at: 2026-09-28T09:08:19.677Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Amendment 1.2.0: successor continuation never links a foreign execution (PRAXIS-REMOTE-09)"
derived_from: [RQ-ROS-2026-A021]
---
# Context

Issue #90 needs cloud agents that have no .NET or Praxis runtime to be
governed as rigorously as local agents. `EV-ROS-2026-A053` reconciles
`PRX-REMOTE-001..044` against the code. That evidence contains the findings
that constrain this decision:

- **A1.** Transition orchestration lives in `Ros.Cli/Program.fs`, not in
  `Ros.Application`.
- **A2.** A GitHub Actions runner resolves as the `automation` actor.
- **A3.** There is no step concept.
- **A4.** There is no caller precondition on the repository state.
- **A5.** The Praxis version is not recorded per execution.
- **S1–S6.** Supply-chain and secret-handling defects.

There is also a transport-neutral outbound contract (`DF-ROS-2026-A007`)
whose envelope and outcome vocabulary are worth reusing, though its
replay-without-fingerprint behaviour is not (S6).

# Decision

## 1. The protocol: `praxis.remote`, version `1.0`

The protocol is a JSON request/response pair with its own schemas:

- `schemas/praxis-remote-request.schema.json`
- `schemas/praxis-remote-response.schema.json`

Both are specified in `docs/remote-protocol.md`. The protocol is
transport-independent: GitHub Actions carries it, and so could a future
GitHub App, CI system or self-hosted executor. Neither the protocol nor the
Domain mentions GitHub.

**Request.** A request contains:

- `protocol` (`"praxis.remote"`) and `protocolVersion` (`MAJOR.MINOR`)
- `requestId` and `operation`
- `repository {ref, expectedSha}`
- the *asserted* `actor`
- an optional `execution {id}` to continue the caller's own execution
- typed `arguments`, and an optional `requestedAt`

Unknown top-level or argument fields are **rejected**, so unknown semantics
fail safely. The one exception is `x-`-prefixed extension fields: these are
forward-tolerant, optional, and excluded from the request's semantics.

**Response.** A response contains:

- `protocol`, `protocolVersion`, `requestId`, `operation`
- `outcome`, one of `succeeded | rejected | failed | unknown`
- `failure {code, retry, message}`, `replayed`
- `repository {ref, expectedSha, observedSha}`
- `praxisVersion` and `executor`
- `result`, which is the underlying command's own structured output

**Versioning.** A request's major version must equal the executor's major
version, and its minor version must be no greater than the executor's.
Anything else is `unsupported-protocol`, and the diagnostics list the
supported versions. Adding a new optional field or operation is a minor
change. Changing meaning is a major change. Historical journal entries are
never rewritten to a newer schema.

## 2. Operations are an allow-list with capability classes

| Operation | Capability | Maps to (same implementation) |
|---|---|---|
| `praxis.describe` | read | discovery document (PRAXIS-REMOTE-07) |
| `status` | read | `status` |
| `validate` | read | `validate --json` |
| `work.context` | read | `work context ID` |
| `provenance.identity` | read | `provenance identity --json` |
| `request.status` | read | request-journal lookup |
| `work.start` | mutate | `work start` |
| `work.resume` | mutate | `work resume` |
| `work.block` | mutate | `work block` |
| `telemetry.record` | mutate | `telemetry record` |
| `work.complete` | complete | `work complete` |
| `work.reconcile` | reconcile | `work reconcile` (#80) |

- Step operations (`step.start | step.complete | step.fail`) arrive in a
  minor version together with the step model (PRAXIS-REMOTE-04). Batch
  requests arrive the same way (PRAXIS-REMOTE-08). The `admin` capability is
  reserved.
- Every operation argument is typed and validated as untrusted input:
  - identifier patterns, relative paths only, and no leading `-` on any
    value that reaches the command line
  - bounded lengths and no control characters
  - known vocabularies for actor kind and telemetry quality
  - recognisable secret material, which is **refused and never echoed**
- The remote interface never accepts a command string.

## 3. One implementation: typed requests route into the existing commands

Finding A1 rules out calling a single Application function today.
`praxis remote execute --request FILE --context FILE` (PRAXIS-REMOTE-03)
therefore works like this:

1. It runs the pure decision (section 4).
2. It maps the typed request to the **same** command implementation the
   local CLI dispatches, in-process. The typed argument list is built from
   validated values, and no shell is involved.
3. It captures that command's structured output and exit status into the
   response.

Validation parity holds by construction, and differential tests prove it:
the same repository and operation, run locally and remotely, give the same
resulting state and the same `validate` output.

Lifting the orchestration into `Ros.Application` is a later refactor. It is
consistent with `DF-ROS-2026-A027`'s "typed request, typed outcome, effect
port", and it needs no protocol change when it happens.

## 4. Decision order (pure, in `Ros.Domain.Remote`)

1. The envelope shape must be well-formed, else `invalid-request`.
2. `protocol` and version must be supported, else `unsupported-protocol`.
3. The operation must be known, else `unsupported-operation`.
4. The typed arguments and untrusted-input rules must pass, else
   `invalid-request`, or `secret-detected` for secret material.
5. A mutating operation must name `repository.expectedSha`, else
   `invalid-request`.
6. The required capability must be in the **trusted** grant, else
   `unauthorized`.
   - Authorization comes before replay, so a caller without authority
     cannot read another request's recorded outcome by guessing its ID.
   - The grant comes from the adapter's trusted context, never from the
     request document. Holding a request confers no authority.
7. The request journal is checked next.
   - The same ID with the same fingerprint replays the recorded response,
     with `replayed: true`.
   - The same ID with a different fingerprint is `idempotency-conflict`.
8. For a mutation, `expectedSha` must equal the observed `HEAD`, else
   `stale-ref`.
9. Only then does the command execute. A domain refusal is
   `domain-rejected`.

**Why replay precedes the stale check.** A caller whose mutation succeeded,
but whose result was lost, will see the branch moved by its own commit.
Replay therefore precedes the stale check, and the **fingerprint excludes
`expectedSha`**, `requestedAt` and `x-` fields. The retry recovers the
original outcome instead of failing as stale or duplicating the transition.

The fingerprint is SHA-256 over a deterministic, length-prefixed encoding
of the protocol major version, operation, ref, actor, execution and typed
arguments.

## 5. Durable request journal: the commit is the atomic unit

- Each accepted mutating request writes
  `.ros/remote/requests/<requestId>.json`. The entry holds the request
  fingerprint, the response, the resulting event IDs and the execution ID.
- The adapter commits the entry **in the same commit** as the Praxis state
  it describes. Both therefore exist, or neither does.
- **Timeout after commit.** The entry is on the branch, so a retry replays
  it and `request.status` reports it. Nobody has to guess.
- **Push rejected** because the branch moved. Nothing was persisted, so the
  adapter reports `concurrency-conflict` with `retry: after-refresh`.
- Read-only operations are not journalled.
- Rejections are returned, and remain as workflow diagnostics. They are
  never committed as state.

## 6. Repository-state binding and concurrency

- Praxis compares `expectedSha` with the observed `HEAD` (section 4).
- The adapter pushes **without force**. A ref that moved between checkout
  and push is refused by Git itself, which gives compare-and-swap semantics
  on the ref.
- GitHub `concurrency` groups per ref reduce contention but are not the
  protection.
- Delayed and out-of-order requests fail as `stale-ref`, because they were
  formed against an older SHA.
- `RegistryLock` continues to serialize processes on one host.
- Protected branches use a PR-based persistence mode: the adapter pushes to
  `praxis/remote/<requestId>` and opens a PR. The outcome then reports
  `pending-merge` state rather than applied state.

## 7. Identity and provenance roles

Each execution and journal entry records these roles separately:

| Role | Source | Label |
|---|---|---|
| Request actor | the request | **asserted** |
| Execution actor | the request actor, for executions the request creates or continues; the existing cross-check applies | asserted |
| Transport principal | adapter trusted context, e.g. the GitHub account that dispatched | observed by the transport |
| Executor | kind, run ID and attempt, workflow ref, Praxis version | **observed** |
| Change author | Git | observed |
| Reconciliation actor | as in `DF-ROS-2026-A040` | — |
| Work-item attribution | as in `DF-ROS-2026-A040` | — |

- When a request supplies an actor, host detection (finding A2) never
  replaces it.
- A request without an actor records `unknown`. It is never recorded as the
  runner.
- Continuing an execution requires the same actor under the existing
  `ActorResolution` rules. A different agent gets a new execution whose
  `parentExecutionId` names its predecessor.

This is PRAXIS-REMOTE-02.

## 8. Telemetry

Usage and cost reuse the existing metric registry and quality model.
PRAXIS-REMOTE-04 adds four things:

1. Steps inside an execution, each with its own ID and outcome.
2. Step-scoped measurements.
3. A documented, tested projection from `quality` × `source.type` to the
   #90 vocabulary:

   | #90 term | `quality` | `source.type` |
   |---|---|---|
   | measured | observed | `ros-git`, `ros-clock` |
   | provider-reported | observed | `runtime-api`, `runtime-output` |
   | agent-reported | — | `agent-report` |
   | calculated | derived | `calculated`, with pricing source and version |
   | estimated | estimated | — |
   | unavailable | capability status | — |

4. Aggregation by execution, step, provider, model and time period.

Remotely supplied telemetry is always labelled with its supplied
`source.type` and never promoted to `observed`.

## 9. Bootstrap and supply chain

The runner obtains Praxis from the version pinned in
`.echelon/toolchain.json`:

1. Download that exact release asset.
2. Verify its SHA-256 and its build-provenance attestation.
3. Cache it by version and digest.
4. Fail explicitly if the version, asset, checksum or attestation is
   missing.

It never falls forward and never builds from source.

PRAXIS-REMOTE-05 also fixes S1 and S2:

- Release assets become immutable per version. A different digest refuses
  to overwrite.
- The pinned install fails closed.
- Workflow actions are pinned to commit SHAs.
- Each execution records the Praxis binary version (finding A5).

## 10. GitHub Actions adapter (PRAXIS-REMOTE-06)

A reusable workflow, with a `workflow_dispatch` entry for agents and
`workflow_call` for other repositories.

**Inputs.** The inputs are `request` (JSON) and, for correlation,
`request_id`. The request travels through an environment variable into a
file, and is never interpolated into `run:` text.

**Jobs.**

- *read* job: permissions `contents: read`.
- *write* job: permissions `contents: write`, used only for mutating
  operations, and pull-requests write only in PR mode.

**Steps.**

1. Check out the named ref.
2. Bootstrap the pinned Praxis.
3. Write the trusted context: principal = `github.actor`, grants from the
   repository's committed remote policy, executor facts from `GITHUB_*`.
4. Run `praxis remote execute`.
5. Commit only the paths Praxis reports it wrote (`.ros/**`), as the Praxis
   automation committer, with trailers naming the request ID, the asserted
   actor and the execution.
6. Push without force.
7. Upload the response as an artifact, write it to the step summary, and
   set the job outcome from the response.

**Guards.**

- `run-name` carries the request ID.
- Fork and `pull_request` contexts cannot trigger it.
- No secrets are passed to Praxis.

There is no domain logic in the YAML.

## 11. Discovery and agent contract (PRAXIS-REMOTE-07)

`praxis remote describe` produces the `praxis.describe` document:

- available, versions, protocol versions
- operations with required arguments and capabilities
- current work and executions
- how to retrieve results

`docs/remote-agent-contract.md` is the short agent-facing contract.
`AGENTS.md` points to both and embeds no scripts.

# Failure semantics

| Code | Decided by | Outcome | Retry |
|---|---|---|---|
| `invalid-request`, `secret-detected`, `unsupported-operation` | Praxis | rejected | never (fix the request) |
| `unsupported-protocol` | Praxis | rejected | never (use a supported version) |
| `unauthorized` | Praxis | rejected | never |
| `idempotency-conflict` | Praxis | rejected | never (new request ID) |
| `stale-ref` | Praxis | rejected | after-refresh |
| `domain-rejected` | Praxis | rejected | never |
| `validation-failed` | Praxis | failed, nothing persisted | never |
| `concurrency-conflict` | adapter (non-fast-forward push, lock timeout) | failed, nothing persisted | after-refresh |
| `bootstrap-failed` | adapter | failed, Praxis never ran | same-request |
| `repository-write-failed` | adapter | unknown until `request.status` says otherwise | same-request |
| `transport-failed`, `rate-limited` | adapter or client | unknown | same-request |
| `timeout`, `cancelled` | adapter or client | unknown | same-request |
| `internal` | Praxis | failed | same-request |

`same-request` is safe only because of section 4. A caller seeing
`unknown` first asks `request.status`, or simply retries with the same
request ID. It never forms a new ID for the same intent.

# Decomposition (children of GH-90)

| Work item | Purpose | #90 coverage | Depends on |
|---|---|---|---|
| PRAXIS-REMOTE-01 | Protocol v1 contract: schemas, typed Domain model, validation, capability classes, fingerprint, decision order, failure taxonomy | 002, 003 (representation), 015/017 (semantics), 021, 022 (input), 023 (classes), 025, 031, 032, 038 (classification), 040 | none |
| PRAXIS-REMOTE-02 | Provenance roles: asserted request actor vs observed executor vs principal; runner never becomes actor; Praxis version per execution | 005, 006, 007, 024 (version) | 01 |
| PRAXIS-REMOTE-03 | `praxis remote execute` boundary: in-process routing into the same commands, request journal, SHA check, `request.status`, structured result, local/remote parity tests | 001, 004, 013, 014, 015, 016, 017, 018 (Praxis side), 026, 027, 028 | 01, 02 |
| PRAXIS-REMOTE-04 | Steps in executions, step-scoped usage/cost, evidence-quality projection, aggregation by execution/step/provider/model/time | 003 (steps), 004 (steps), 008, 009, 010 | none (the remote exposure lands in 03 or a minor version) |
| PRAXIS-REMOTE-05 | Deterministic, verifiable bootstrap: immutable assets, attestation, fail-closed pin, SHA-pinned actions, cache | 012, 024, 036 (cache) | none |
| PRAXIS-REMOTE-06 | GitHub Actions reusable workflow adapter | 011, 018, 020, 021, 022, 029, 032 | 03, 05 |
| PRAXIS-REMOTE-07 | Discovery (`praxis.describe`) and agent contract; AGENTS.md pointer; npm `praxis` bin | 013, 033, 039, 044 | 03, 06 |
| PRAXIS-REMOTE-08 | Ordered batch/session requests with per-operation identity and unambiguous partial failure | 036, 037 | 03 |
| PRAXIS-REMOTE-09 | Remote reconciliation (#80), fallback records, successor continuation | 019, 034, 035 | 02, 03 |
| PRAXIS-REMOTE-10 | Operator documentation | 043 | 06, 07 |
| PRAXIS-REMOTE-11 | End-to-end no-.NET proof, and the remaining 041 tests against a live runner | 041, 042, 028, 029 | 04, 06, 07, 09 (+ the adapter merged to the default branch: human action) |
| PRAXIS-REMOTE-12 | Conditor installs/configures the remote surface | 030 | 11 (external repository) |

```
01 ──┬─> 02 ──┬─> 03 ──┬─> 06 ──┬─> 07 ──> 10
     │        │        │        │
     │        └────────┼─> 09 ──┤
     │                 └─> 08   │
04 ────────────────────────────>├─> 11 ──> 12
05 ──────────────────> 06       │
```

Items 01, 04 and 05 have no unmet dependency. 01 comes first because every
other increment builds on its types.

The captured #80 follow-ups `ATTR-COMPLETE-BASE-REF-SWEEP` and
`ATTR-RECONCILE-SYMLINK-SUBMODULE` stay separate. PRAXIS-REMOTE-09 depends
on the reconciliation they harden but does not absorb them.

# Amendment 1.1.0 (2026-09-28, PRAXIS-REMOTE-02/03)

Section 3's "in-process" routing is implemented as a **child process of
the same binary**, not as a call inside the executor's own process. The
child receives the typed argument list and an environment *derived* by
`RemoteIdentity.childEnvironment`.

The command implementation and its rules are unchanged. The one difference
is the environment:

- A derived environment is the only way to guarantee that a runner's own
  identity markers and credentials cannot reach the command.
- Identity discovery reads the process environment. A shared process could
  therefore not keep the requester's identity separate from the runner's.

Validation parity is proven by `tests/remote-execute.test.mjs`: local and
remote `work.start` produce the same work state, and remote `validate`
returns the local validation document.

Two rules added during implementation:

- **Remote mutation is opt-in per repository.** It requires `ros.json`
  `remote.capabilities`. Without it, the effective grant is `read`.
- **A dirty working tree is `stale-ref`.** It is not the commit the request
  names.

# Amendment 1.2.0 (2026-09-28, PRAXIS-REMOTE-09)

**Evidence.** `PRAXIS-REMOTE-09` reproduced a defect in core behaviour,
local and remote alike. When agent B resumed a work item that agent A had
blocked, B's `work.resumed` event was linked into A's still-active
execution. B's work would therefore have accrued to A's execution, which
`PRX-REMOTE-004`, `PRX-REMOTE-005` and `PRX-REMOTE-034` forbid.

**Rule.** A transition now links or recovers only executions that the
acting process may continue, as decided by `ActorResolution.mayContinue`:
the recorded actor agrees with the current one, and nothing known about the
run differs. Every other execution is *foreign*
(`TelemetryItemState.ForeignExecutionIds`). A successor, or a new session
of the same agent, therefore gets its own execution whose
`parentExecutionId` names its predecessor.

**Exception.** Completing a work item still finalizes every active
execution for that item.

**Remote requests.** A remote request whose `execution.id` names a foreign
execution is `domain-rejected`.

**Tests:**
- `TelemetryResolutionTests` ("continuation: ...")
- `ProvenanceTests` ("continuation: only the same actor in the same
  run...")
- `tests/remote-execute.test.mjs` ("a successor agent continues in its own
  execution...")

# Consequences

- **One implementation.** Remote execution introduces no second rule set.
  A requirement change to a transition changes local and remote behaviour
  together.
- **More in `.ros/`.** A small journal directory is added. It is Praxis
  bookkeeping, so it is ignored by meaningful-path attribution the way other
  `.ros/` state is.
- **Idempotency moves into the repository.** It is Git-backed rather than
  held in a service. This keeps #90's non-goals: no server, no database, no
  cloud infrastructure, no GitHub App.
- **Throughput is limited by the ref.** Concurrent mutations of the same ref
  serialize through non-fast-forward rejection. That is accepted, because
  correctness beats throughput for governance state.

# Revisit triggers

- `Ros.Application` gains typed transition handlers. Switch the in-process
  routing (section 3) to them.
- A second transport is implemented. Confirm that no protocol change is
  needed.
- GitHub adds dispatch-returned run IDs, or immutable releases become
  enforceable. Simplify correlation and integrity accordingly.
