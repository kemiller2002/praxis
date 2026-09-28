# Praxis remote protocol (`praxis.remote` 1.0)

This page specifies the typed request/response contract that lets an agent
with **no local .NET or Praxis runtime** ask a trusted executor to run an
explicit Praxis operation.

- **Why:** [`RQ-ROS-2026-A021`](../research/requirements/RQ-ROS-2026-A021--remote-execution-first-class-capability.md),
  which adopts issue #90.
- **How it was designed:** [`DF-ROS-2026-A041`](../research/decisions/DF-ROS-2026-A041--remote-execution-protocol-and-adapter-architecture.md).
- **Schemas:** [`schemas/praxis-remote-request.schema.json`](../schemas/praxis-remote-request.schema.json)
  and [`schemas/praxis-remote-response.schema.json`](../schemas/praxis-remote-response.schema.json).
- **Typed model:** `Ros.Domain.Remote` (`src/Ros.Domain/Remote/Protocol.fs`).
- **JSON contract:** `Ros.Contracts.Remote.RemoteJson`.

> **Status.**
>
> - **Implemented:** the contract (PRAXIS-REMOTE-01), identity roles
>   (PRAXIS-REMOTE-02), and the transport-independent executor boundary
>   `praxis remote execute` (PRAXIS-REMOTE-03, see [Executing a
>   request](#executing-a-request)).
> - **Not implemented yet:** the GitHub Actions adapter (PRAXIS-REMOTE-06)
>   and the verified release bootstrap (PRAXIS-REMOTE-05). Both are tracked
>   under `GH-90`. Until they exist, a cloud agent still needs someone else
>   to run the executor for it.

## Principles

- **Praxis remains the authority.** An executor maps a typed request onto
  the *same* command implementation the local `praxis` CLI runs. There is no
  second rule set, so remote validation is never weaker than local
  validation.
- **Operations, never a shell.** The operation catalog is an allow-list.
  No operation accepts a command string. Every request value is untrusted
  data.
- **Transport-independent.** Nothing in the protocol names GitHub. GitHub
  Actions is the first transport, not the protocol.
- **Provider-neutral.** The actor vocabulary is the ordinary Praxis one
  (`agent | human | automation | unknown | x-<extension>`), with free-form
  provider, model and runtime strings.

## Request

```json
{
  "protocol": "praxis.remote",
  "protocolVersion": "1.0",
  "requestId": "req-2026-09-28-work-start-0001",
  "operation": "work.start",
  "repository": {
    "ref": "refs/heads/main",
    "expectedSha": "59b4e032818a4c765886e48c117595dc58019d43"
  },
  "actor": {
    "kind": "agent",
    "id": "example/cloud-agent",
    "provider": "example",
    "runtime": "cloud-agent",
    "sessionId": "session-123"
  },
  "execution": { "id": "EXE-20260928T080000000Z-0a1b2c3d" },
  "arguments": { "workItemIds": ["WI-0100"], "type": "task" },
  "requestedAt": "2026-09-28T08:00:00.000Z"
}
```

| Field | Required | Meaning |
|---|---|---|
| `protocol` | yes | Always `praxis.remote`. |
| `protocolVersion` | yes | `MAJOR.MINOR`. |
| `requestId` | yes | Caller-chosen, stable per intent. 8-128 characters of `[A-Za-z0-9._:-]`, starting with a letter or digit. Reuse it on every retry of the same intent. Never reuse it for a different intent. |
| `operation` | yes | One entry from the catalog below. |
| `repository.ref` | mutations | The branch the request targets (`refs/heads/NAME`). |
| `repository.expectedSha` | mutations | The full commit SHA the request was formed against. |
| `actor` | no | The actor the requester **asserts**. It is self-reported and is never inferred from the executor. Absent values are recorded as `unknown`, and a human has no provider, model or runtime. An absent `actor` means unknown. It never means the runner. |
| `execution.id` | no | Continue the caller's *own* execution. The existing no-impersonation cross-check still applies. |
| `arguments` | per operation | Typed arguments. Unknown argument fields are rejected. |
| `requestedAt` | no | When the caller formed the request (asserted). Praxis stamps transition times from the executor's clock. |

**Unknown fields fail safely.** A field that is not listed is rejected at
any level of the request. The one exception is a field whose name starts
with `x-`. Such a field is a tolerated extension: it carries no meaning, is
not validated, and is excluded from the fingerprint.

## Operations

| Operation | Capability | Arguments | Same implementation as |
|---|---|---|---|
| `praxis.describe` | read | none | discovery document (PRAXIS-REMOTE-07) |
| `status` | read | none | `praxis status` |
| `validate` | read | none | `praxis validate --json` |
| `work.context` | read | `workItemId` | `praxis work context ID` |
| `provenance.identity` | read | none | `praxis provenance identity --json` |
| `request.status` | read | `requestId` | request-journal lookup |
| `work.start` | mutate | `workItemIds[]`, `type?`, `classifications[]?` | `praxis work start` |
| `work.resume` | mutate | `workItemIds[]` | `praxis work resume` |
| `work.block` | mutate | `workItemIds[]`, `reason` | `praxis work block` |
| `telemetry.record` | mutate | `metric`, `value`, and optional `workItemId`, `unit`, `currency`, `quality`, `confidence`, `scope`, `sourceType`, `sourceName`, `mechanism`, `pricingSource`, `pricingVersion`, `collectedAt` | `praxis telemetry record` |
| `work.complete` | complete | `workItemIds[]`, `evidence[{type, path}]?`, `conclusion?` | `praxis work complete` |
| `work.reconcile` | reconcile | `workItemId`, `reason`, and at least one of `commits[]` or `ranges[]` (`BASE..HEAD`), plus `paths[]?` | `praxis work reconcile` (#80) |

**Planned additions.** Step operations (`step.start`, `step.complete` and
`step.fail`) and ordered batches are planned for later minor versions
(PRAXIS-REMOTE-04 and PRAXIS-REMOTE-08). The `admin` capability is reserved.

**Capabilities.** The executor grants capabilities from *trusted*
configuration. A request document never grants its own. `read` never
implies any mutation, and `mutate` implies neither `complete` nor
`reconcile`.

### Untrusted-input rules

The protocol checks shape and safety only. Whether a transition is legal,
or a metric is well formed, is still decided by the command that runs.

- **Identifiers** (work items, tokens, actor values) start with a letter or
  digit, so they can never be read as an option.
- **Branch refs:**
  - must match `refs/heads/NAME`;
  - must not contain `..` or `//`;
  - must not end in `/` or `.lock`.
- **Revisions** must be lowercase hexadecimal commit SHAs. Symbolic
  revisions such as `HEAD~1` or branch names are refused remotely.
- **Paths:**
  - must be repository-relative, with `/` separators;
  - must not contain `..`, `\`, `:`, empty segments or control characters;
  - must not start with `/` or `-`.
- **Free text** (`reason`, `conclusion`):
  - 1 to 2000 characters;
  - no control characters except newline and tab;
  - must not start with `-`.
- **Line terminators.** Every pattern is anchored to the true end of the
  value. A trailing newline cannot slip past a pattern, as it could with a
  `$` anchor.
- **Telemetry cannot claim executor observation.**
  - `telemetry.record` refuses the source types that denote Praxis's *own*
    observation: `ros-git`, `ros-clock` and `environment`.
  - Supplied values keep their asserted `quality` and `sourceType`. For
    example, `observed` with `runtime-api` means "provider-reported,
    relayed by the requester".
  - A missing `value` is refused. It never becomes zero.
- **Credential material is refused and never echoed.** A request that
  contains what looks like a secret is refused with `secret-detected`. The
  response names the offending field but never includes the value.
  Patterns cover:
  - GitHub tokens
  - `sk-` API keys
  - AWS access keys
  - Google API keys
  - Slack tokens
  - private-key blocks
  - JWTs

## Versioning

- **Supported versions.** An executor at `1.N` accepts requests at `1.0`
  through `1.N`. A request from a newer minor version, or from a different
  major version, is `unsupported-protocol`, and the diagnostics name the
  supported versions.
- **Version is checked first.** The protocol and its version are read before
  anything else in the request. A newer-version request carrying fields this
  executor does not know is therefore reported as unsupported. It is not
  reported as a list of unknown fields.
- **Minor versions** may only add optional fields or new operations.
  Changing the meaning of a field or operation requires a new major version.
- **History is kept.** Journal entries record the protocol version they were
  written under. They are never rewritten to a newer schema.

## Decision order

The order below is part of the contract.

1. **Well-formed document.** The document must be a JSON object. Otherwise
   the result is `invalid-request`.
2. **Protocol and version.** Otherwise `unsupported-protocol`.
3. **Known operation.** Otherwise `unsupported-operation`.
4. **Structure, values and secret scan.** Otherwise `invalid-request` or
   `secret-detected`.
5. **Mutation binding.** A mutation must name `repository.ref` and
   `repository.expectedSha`. Otherwise `invalid-request`.
6. **Authorization.** The trusted grant must include the operation's
   capability. Otherwise `unauthorized`. This check comes *before* replay,
   so a caller without authority cannot read another request's recorded
   outcome.
7. **Journal** (mutations only). If the same `requestId` is recorded with
   the same fingerprint, the result is a **replay**. The recorded response
   is returned with `replayed: true`, and nothing executes again. If the
   fingerprint differs, the result is `idempotency-conflict`.
8. **Repository binding** (mutations only). The executor's checked-out ref
   and commit must equal `repository.ref` and `repository.expectedSha`.
   Otherwise `stale-ref`.
9. **Execute** the same command implementation as the local CLI.

### Idempotency

The fingerprint is a SHA-256 hash (`sha256:<hex>`) of a length-prefixed
encoding of these fields:

- protocol and major version
- operation
- ref
- actor
- execution
- typed arguments

It deliberately **excludes** `expectedSha`, `requestedAt` and `x-` fields.

Here is why. A caller whose mutation succeeded, but whose result was lost in
transit, will see the branch moved *by its own commit*. When it retries with
the same `requestId`, the journal is consulted before the stale-ref check,
so the caller recovers the original outcome. The retry does not fail as
stale, and the transition is not applied twice. Whether the retry names the
old SHA or the new one makes no difference.

## Response

```json
{
  "protocol": "praxis.remote",
  "protocolVersion": "1.0",
  "requestId": "req-2026-09-28-work-start-0001",
  "operation": "work.start",
  "outcome": "rejected",
  "replayed": false,
  "repository": {
    "ref": "refs/heads/main",
    "expectedSha": "59b4e032818a4c765886e48c117595dc58019d43",
    "observedSha": "8646ade0000000000000000000000000000000aa"
  },
  "praxisVersion": "3.4.0",
  "failure": {
    "code": "stale-ref",
    "decidedBy": "praxis",
    "retry": "after-refresh",
    "message": "the ref has moved since the request was formed; re-read the repository state and form a new request",
    "problems": [
      {
        "field": "repository.expectedSha",
        "message": "does not match the observed commit"
      }
    ]
  },
  "result": null
}
```

`result` carries the executing command's own machine-readable output,
unchanged. A `requestId` that fails validation is never echoed back.

### Outcomes and failures

| `outcome` | Meaning |
|---|---|
| `succeeded` | The operation ran, and for a mutation, its state was persisted. |
| `rejected` | Praxis refused before any effect. |
| `failed` | The attempt ended, and it is known that nothing was persisted. |
| `unknown` | An effect may have been persisted and has not been confirmed. It is never promoted to success or failure. |

| `failure.code` | `decidedBy` | `outcome` | `retry` |
|---|---|---|---|
| `invalid-request`, `secret-detected`, `unsupported-operation`, `unsupported-protocol`, `unauthorized`, `idempotency-conflict`, `domain-rejected` | praxis | rejected | `never` |
| `stale-ref` | praxis | rejected | `after-refresh` |
| `validation-failed` | praxis | failed | `never` |
| `internal` | praxis | failed | `same-request` |
| `concurrency-conflict` | executor | failed | `after-refresh` |
| `bootstrap-failed` | executor | failed | `same-request` |
| `repository-write-failed`, `transport-failed`, `rate-limited`, `timeout`, `cancelled` | executor | unknown | `same-request` |

The retry values mean:

- `never`: change the request first.
- `after-refresh`: re-read the repository state and form a new request.
- `same-request`: retry with the *same* `requestId`. This is safe only
  because mutations are journalled.

After an `unknown` outcome, ask `request.status` or retry with the same
`requestId`. Never mint a new ID for the same intent.

## Identity roles

Each of these is recorded separately. None is ever collapsed into another.

| Role | Source |
|---|---|
| Request actor | Asserted in the request. |
| Executor or runner | Observed by the executor. |
| Transport principal | Authenticated by the transport. |
| Git change author | Git history. |
| Reconciliation actor | The actor who reconciles post-hoc attribution. |
| Work-item attribution | The work item a change is attributed to. |

A runner never becomes the author of an agent's work (PRAXIS-REMOTE-02).

## Executing a request

```
praxis remote execute --request FILE [--grant read|mutate|complete|reconcile]* [--output FILE] [--timeout-seconds N]
```

(`ros remote execute` works the same, for compatibility.) The command prints
the response JSON, writes it to `--output` when given, and exits with one of
these codes:

- `0` when the outcome is `succeeded`.
- `1` for any other outcome.
- `2` for bad command-line arguments.

**Who runs it.** The executor boundary is run by a trusted adapter, for
example a CI job. It is not run by the requester.

**Grants.** `--grant` is the transport's grant, set from trusted adapter
configuration. It is intersected with the repository's own opt-in:

```json
{ "remote": { "capabilities": ["read", "mutate", "complete", "reconcile"] } }
```

This setting lives in `ros.json`. A repository that says nothing allows
remote **reads only**, so adding the executor never grants mutation by
itself.

**How the command runs.** An accepted request runs this same binary's
local command in a child process:

- It passes a typed argument list and never uses a shell.
- It uses the executor's clock for `--occurred-at`.
- It uses a *derived* environment. The requester's asserted actor is set
  explicitly, the executor's observed facts are included, and only an
  operational allow-list of variables survives. Host identity markers and
  credentials are never passed on. See `docs/agent-provenance.md`.

**Around a mutation, the boundary also:**

1. Refuses a working tree with uncommitted changes (`stale-ref`), because
   such a tree is not the commit the request names.
2. Records `validate` findings before and after. A mutation that introduces
   a new validation error is undone and reported as `validation-failed`.
3. Undoes anything written by a refused, failed, or timed-out command, and
   keeps only Praxis-owned state (`.ros/**` outside locks and transactions).
4. Writes the journal entry `.ros/remote/requests/<requestId>.json`. It
   holds the fingerprint, asserted requester, principal, repository binding
   and full response. `:` in a request ID becomes `~` in the file name.
5. Reports every path the adapter may commit in `persistence.paths`,
   including the journal entry. The adapter commits exactly those paths, in
   one commit.

**Concurrency.** Mutations are serialized per working tree, with the journal
lookup, command and journal write held under one lock. Across runners,
`expectedSha` and a non-fast-forward push serialize them. A request that
lost the race fails as `stale-ref` and must be re-formed.

**Finding out what happened.** `request.status` reports whether a request
ID is recorded, and returns the recorded response when it is. A mutation
whose result was lost is recovered by retrying it with the same
`requestId`, or by asking `request.status`. The answer comes from the
repository, never from guessing.
