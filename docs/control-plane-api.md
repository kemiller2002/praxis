# Control-Plane API

The control plane is the typed, versioned read and transition surface that
`praxis web serve` and `praxis hub serve` expose under `/api/v1`. It covers
PRX-CTL-004 to PRX-CTL-008, PRX-CTL-011, PRX-CTL-012, PRX-UI-009, PRX-UI-024
and PRX-UI-027 of
[`requirements/EXECUTION-ORCHESTRATION.md`](../requirements/EXECUTION-ORCHESTRATION.md).

Every document is produced by one read-only CLI command group,
`praxis control-plane`, which reads through the same functions as the
corresponding CLI commands. The hosts add no rules: `web serve` runs
`praxis control-plane ...` for each request, and `hub serve` runs each
registered repository's own `./praxis control-plane ...`.

```bash
./praxis control-plane source
./praxis control-plane work [ID] [--tag T]* [--status S]
./praxis control-plane evidence ID
./praxis control-plane executions [--work-item ID]
./praxis control-plane execution EXE-ID
./praxis control-plane transition --id ID --action ready|block|abandon|start|resume|complete \
    [--reason TEXT] [--type TYPE] [--evidence TYPE=PATH]* [--conclusion TEXT]
```

The command prints one JSON document and exits `0` for a successful
document, `1` for a refusal, and `2` for an unusable request.

## The contract: `praxis.control-plane` version 1

The contract is defined in F# in
[`src/Ros.Contracts/ControlPlane/ControlPlaneJson.fs`](../src/Ros.Contracts/ControlPlane/ControlPlaneJson.fs).
A successful document:

```json
{
  "schema": "praxis.control-plane",
  "schemaVersion": 1,
  "kind": "work-item",
  "source": {
    "repository": "owner-repo",
    "commit": "f806f817e4656e00550313d839b59ceea87a682d",
    "branch": "main",
    "stateFingerprint": "sha256:..."
  },
  "data": { }
}
```

A refused request has `refusal` in place of `data`:

```json
{
  "schema": "praxis.control-plane",
  "schemaVersion": 1,
  "kind": "transition",
  "source": { },
  "refusal": {
    "category": "illegal-transition",
    "code": "illegal-transition",
    "message": "cannot start backlog item 'WI-0001' from 'captured'",
    "action": "start",
    "workItemId": "WI-0001"
  }
}
```

A client checks `schema` and `schemaVersion` before reading anything else. A
change that removes or renames a field, or changes its meaning, is a new
`schemaVersion`; added fields are not.

### Kinds

| `kind` | Command | `data` |
|---|---|---|
| `source` | `control-plane source` | `{}`: only the `source` matters |
| `work-list` | `control-plane work` | array of work items |
| `work-item` | `control-plane work ID` | one work item |
| `evidence` | `control-plane evidence ID` | recorded evidence, checkpoints, telemetry usage |
| `execution-list` | `control-plane executions` | array of executions |
| `execution` | `control-plane execution EXE-ID` | one execution with steps and receipts |
| `transition` | `control-plane transition ...` | `{action, workItem}`: the item's resulting state |

### Work item

| Field | Meaning |
|---|---|
| `id`, `title`, `description`, `tags`, `priority` | identity, from the backlog record |
| `semanticState` | the item's effective state (the live state once started) |
| `backlog` | the recorded backlog status (`captured`, `ready`, `blocked`, `abandoned`), or `null` |
| `live` | the recorded live state, semantic state and work type, or `null` before the item is started |
| `blockedReason` | why the item is blocked, if it is |
| `legalActions` | the actions the kernel allows from the recorded state |
| `actions` | every action: `legal`, what a legal action still `requires` (`reason`, `evidence:TYPE`), and for an action the kernel refuses with a reason, that `refusal` (`code`, `message`) |
| `obligations` | available once started: required completion evidence, the evidence still missing, and the latest checkpoint's next action |
| `unknowns` | Praxis records no unknowns for individual work items, so this is always `unavailable` |

Legality and refusal reasons are the kernel's own decisions
(`BacklogTransition.decide` and `WorkTransition.decide`), evaluated against
the recorded state. An action the kernel has no rule for in the item's layer
(`resume` for a backlog item, `ready` for live work) is `legal: false` with
`refusal: null`: the kernel cannot say why.

### Availability

A source Praxis may not have is never reported as an empty value:

```json
{ "status": "available", "value": ... }
{ "status": "unavailable", "reason": "no live work record: ..." }
```

Obligations, unknowns, recorded evidence, checkpoints and telemetry usage use
this shape.

### Evidence

`data` holds `evidence` (recorded `{type, path}` entries), `checkpoints` (the
`work checkpoint show --json` document, read offline), `telemetryExecutionIds`,
`usage` (the `telemetry usage ID` document, `praxis.telemetry-usage` version
1, whose groups mark incomplete totals with `complete: false`; unavailable
when nothing is recorded) and `executionIds`.

### Executions

An execution is an `execution show --json` envelope (`.ros/executions/`)
presented as: `executionId`, `workItem`, `role`, `status`, `statusReason`,
`startedAt`, `actor` (`id`, `kind`), `executionHost` (`provider`, `model`,
`runtime`), and `workStateOwner: "repository"`. Provider, model and runtime
describe where the execution ran; they never own work state (PRX-CTL-012).
A single execution adds its revisions, workspace binding, containment,
`scopeEffects`, `verification`, `legalActions` and `steps`. Each step has its
`expected` receipt, its `status` (`match`, `mismatch`, `indeterminate`,
`not-started`, `reconciled-occurred`, `reconciled-not-occurred`) and its
`observations`: every observed receipt with the result it produced.

## Transition requests

A transition request names one action and its arguments. It is judged, then
executed, in this order:

1. The item must exist (`not-found`).
2. The kernel's decision for the action, from the recorded state, with the
   request's arguments: an illegal action is `illegal-transition`; a legal
   action missing a reason or completion evidence is `missing-argument`,
   with the kernel's code (`block-reason-required`,
   `abandon-reason-required`, `missing-evidence`).
3. A permitted request runs the CLI command an operator would type
   (`work backlog-transition`, `work block`, `work abandon`, `work start`,
   `work resume`, `work complete`), so the CLI's locking, guards (for
   example the durable-checkpoint requirement), identity resolution and
   provenance apply unchanged. If the CLI refuses, the category is
   `rejected` and the message is the CLI's own.

A refused request writes nothing: steps 1 and 2 only read, and the CLI's
transition commands refuse before writing.

**Identity is never taken from the request.** The command runs with the
host process's environment, so the actor and provenance recorded are exactly
those of the same command typed in the shell that started the host
(PRX-UI-027). A request body has no identity fields; `actor` or `agent`
fields in it are ignored. A request value beginning with `--` is refused as
`invalid-request`, so no value can be read as an option.

### Refusal categories

| Category | HTTP | Meaning |
|---|---|---|
| `not-found` | 404 | no such work item, execution, repository or route |
| `invalid-request` | 400 | unknown action, missing `--id`/`--action`, malformed arguments or body |
| `illegal-transition` | 409 | the kernel does not allow the action from the recorded state |
| `missing-argument` | 422 | the kernel allows it only with a reason or evidence the request lacks |
| `rejected` | 422 | the CLI's transition path refused it when it ran |
| `unavailable` | 503 | the repository's Praxis could not be run |
| `incompatible` | 502 | the repository's Praxis does not speak this contract version |

## The host holds no state

The hosts keep no workflow state and no cache. Every `/api/v1` response is
produced by a new `praxis control-plane` process reading the repository's
durable records at request time (PRX-CTL-004, PRX-CTL-008, PRX-UI-009):

- `source.stateFingerprint` is a SHA-256 digest over every file under
  `.ros/` (path and content, in ordinal path order), excluding `.ros/locks/`
  (transient locks) and `.ros/hub/` (hub registration data). A client that
  sees a different fingerprint or `commit` knows its view is stale.
- Restarting a host on the same repository gives byte-identical responses;
  a change made with the CLI while a host runs is visible on the next
  request, with a new fingerprint.
- The hosts write no repository file. The only files they write are
  uploads in a private temporary directory (removed after the request) and,
  for the hub, its own registration data. A test fails if the host sources
  call any other file-writing API, and the read endpoints are tested to
  leave every repository file unchanged.

## Endpoints

`web serve` (single repository):

| Method | Path | Command |
|---|---|---|
| `GET` | `/api/v1/source` | `control-plane source` |
| `GET` | `/api/v1/work?tag=T&status=S` | `control-plane work --tag T --status S` |
| `GET` | `/api/v1/work/:id` | `control-plane work ID` |
| `GET` | `/api/v1/work/:id/evidence` | `control-plane evidence ID` |
| `GET` | `/api/v1/executions?workItem=ID` | `control-plane executions --work-item ID` |
| `GET` | `/api/v1/executions/:id` | `control-plane execution EXE-ID` |
| `POST` | `/api/v1/work/:id/transitions` `{action, reason?, type?, evidence?: [{type, path}], conclusion?}` | `control-plane transition --id ID --action ...` |

`hub serve` (registered repositories) is described in
[`docs/project-administration-hub.md`](project-administration-hub.md#versioned-api).

The unversioned `/api/work` routes of `web serve` keep their behaviour and
response shapes; they are superseded by `/api/v1` and documented in
[`docs/web-interface.md`](web-interface.md#api-reference).

## Tests

`tests/Ros.Tests/ControlPlaneTests.fs`: the kernel-derived action views,
availability, refusal mapping, fingerprint and execution-host projection;
the `/api/v1` routes of both hosts; a source check that the hosts call no
other file-writing API; the CLI documents over real repositories (including
execution steps with matched and mismatched receipts, and evidence with
usage identical to `telemetry usage`); and real `web serve` and `hub serve`
processes: structured 404s, refusals that leave `.ros/` byte for byte
unchanged, the host's identity recorded for an API transition, identical
answers across a restart, CLI changes seen without a restart, reads that
write nothing, and a hub that reports unreachable and incompatible
repositories individually and stores nothing of theirs.
