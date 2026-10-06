# Project Administration Hub

A repository-local backlog (`docs/work-backlog-guide.md`) makes one
repository's own work cheap to capture. The hub extends that across
*multiple* repositories: it registers other Praxis repositories by filesystem
path, lets you create a work item in any of them, and shows a combined view
of what's outstanding across all of them.

This lives in a separate, installable Praxis profile
(`praxis init --profile project-administration`) — not inside a
plain Praxis repository — for the same reason described in
[`DF-ROS-2026-A008`](../research/decisions/DF-ROS-2026-A008--repository-local-work-backlog.md):
a single repository's work protocol must stay independently usable, and
cross-repository coordination is a distinct concern from repository
execution. The README's ownership table assigns "repository registration,
portfolio data" to a central reporting repository, not to Praxis core; this
hub *is* that repository, built from Praxis's own reusable tooling rather than
by hand.

## The one rule that keeps this safe

**The hub never touches a registered repository's files directly.** Every
operation on a registered repository shells out to that repository's own
`./praxis` (or `./ros` for a repository installed before the rename), exactly
the command a human would type from that repository's own directory. Creating a work item through the hub and creating one by hand in
that repository are indistinguishable afterward — same ID sequence, same
queue.json, same validation rules, same evidence requirements. The hub adds
no second source of truth for any repository's work.

One consequence: a registered repository must have `./praxis` and the backlog
commands (`praxis add`, `praxis work ...`) available to actually receive work
created through the hub. A repository bootstrapped before the backlog layer
existed needs its Praxis installation upgraded first (see
`PACKAGE-USAGE.md`'s upgrade procedure in that repository) — the hub
reports this as a clear per-repository error rather than failing silently
or guessing.

## The registry

Canonical storage is `.ros/hub/registry.json`:

```json
{
  "schemaVersion": "1.0.0",
  "repos": [
    { "id": "repository-operating-system", "name": "Praxis Core", "path": "/Users/you/dev/praxis", "registeredAt": "..." }
  ]
}
```

`.ros/hub/registry.md` is a generated, human-readable projection, same
pattern as the backlog's `queue.md`. `id` is read from the target
repository's own `ros.json` (`repository.id`) at registration time and
never changed afterward — it's the same ID that repository's own `./praxis`
already uses, so a row in the hub's aggregated view and `./praxis work show`
run directly in that repository always agree.

Registration is filesystem-path based and therefore single-machine: it only
works for repositories checked out locally where the hub runs. Coordinating
repositories across machines is a different, larger problem (authenticated
network transport, the kind the
[external work adapter contract](work-adapter-contract.md) is for) and is
explicitly out of scope here.

## CLI

The hub is part of the Praxis command-line tool itself; no Node.js or npm is
required. From the hub repository's root (`./praxis-hub ARGS` is a shorthand
for `./praxis hub ARGS`; `./ros-hub` and `./ros` remain compatibility aliases):

```bash
./praxis hub register /path/to/some/repo --name "Display Name"
./praxis hub repos
./praxis hub unregister REPO-ID
./praxis hub create REPO-ID "Title" --tag a,b --priority high --description "..." --file PATH[=NAME]
./praxis hub work                    # aggregated, every registered repo
./praxis hub work --repo REPO-ID --status ready
./praxis hub state                   # praxis.hub-state: every repo's own work-state document
./praxis hub state --repo REPO-ID [--item ITEM-ID] [--tag T] [--status S]
```

Each command prints JSON (the registered entry, the removed entry, the
repository list, the created item with `repoId`/`repoName`, or the
aggregated rows). `create` runs the target repository's own `./praxis add`
with the title, tags, priority, description, `--id` and `--actor`, then --
when files are given -- its own `./praxis work attach --file PATH[=NAME]` and
`./praxis work show`, because the hub builds and runs exactly those commands.
Each spoke runs its own pinned Praxis version.

## Web interface

```bash
./praxis hub serve                   # or: ./praxis-hub serve [--port N] [--host H]
```

Serves `http://127.0.0.1:4320` -- server-rendered HTML with plain form posts
and no browser JavaScript: a register form, a repo list (with unregister), a
create-work-item form (repository, title, tags, priority, description, and
up to three files with optional display names), a filter bar (repo, tag,
status), and the aggregated table. Every form post redirects back with a
notice or the exact error. **No authentication, localhost by default** --
this server can create work items and run commands in every registered
repository, which is a larger blast radius than the single-repo web
interface. Do not bind it to a non-loopback host without your own
authentication in front of it. `web-hub/styles.css` in the hub repository
styles the page (a built-in copy is used when it is absent).

The pages and the JSON API below call the same functions as `praxis hub`
(`Ros.Cli.Hub`); uploaded files touch disk only as short-lived temp files
passed to the spoke's own `work attach`, and are deleted afterwards.
Registering and unregistering over HTTP run this CLI's own
`praxis hub register|unregister` against the hub root, so the server process
never writes the registry itself.

The hub keeps no state between requests: the registry and every spoke are
read again on each request, so a restarted hub answers identically and
changes made through `praxis hub` or a spoke's own `./praxis` are served
without a restart. Every response carries the hub repository's
`Praxis-State-Fingerprint`, `Praxis-Repository`, `Praxis-Commit` and
`Praxis-State-Stable` headers (see "State, restart and source identity" in
`docs/web-interface.md`). Each aggregated work row (over HTTP and from `praxis hub work`) also carries
`repoSource` `{repository, commit, branch, stateFingerprint, stable}`: the
spoke's own `./praxis state identity --json` read before and after its
`work list`, or `{"unavailable": "..."}` when the spoke's Praxis is too old
to report one.

### API

| Method | Path | Effect |
|---|---|---|
| `GET` | `/api/repos` | List registered repositories |
| `POST` | `/api/repos` `{path, name?}` | Register a repository |
| `DELETE` | `/api/repos/:id` | Unregister (does not touch the repository itself) |
| `GET` | `/api/work?repo=&tag=&status=` | Aggregated work, one repo or all |
| `POST` | `/api/repos/:id/work` | Create a work item; JSON body `{title, tags, priority?, description?, id?, actor?}`, or `multipart/form-data` with the same fields (`tags` comma-separated) and `file` parts for attachments |

Errors are `4xx` with `{"error": "..."}`.

Aggregation is best-effort per repository: a registered repository whose
path has moved, or whose Praxis installation is too old to support a command,
surfaces as a single error entry for that repository rather than failing
the whole view.

## Versioned control-plane API (`/api/v1`, `praxis.hub-state`)

The hub aggregates repositories for discovery and navigation without
becoming a store of their work (PRX-CTL-007). Its versioned routes answer
with each registered repository's **own** versioned work-state documents --
the `praxis.work-state` version 1 contract of `praxis web serve`'s
`/api/v1/work` (see `docs/web-interface.md`) -- obtained by running that
repository's own `./praxis work state` (and `./praxis work transition` for a
transition; see `docs/cli.md`). The hub nests each document unchanged in a
`praxis.hub-state` version 1 envelope that says which repository it came
from and whether that repository could answer.

| Method | Path | Answer (`kind`) |
|---|---|---|
| `GET` | `/api/v1/repos` | `repository-list`: every registered repository with its `availability` and `repositorySource` |
| `GET` | `/api/v1/repos/:id/work?tag=&status=` | `repository-work-list`: `{repository, document}`, `document` the repository's own `work-list` |
| `GET` | `/api/v1/repos/:id/work/:item` | `repository-work-item`: `document` the repository's own `work-item` (its `work-item-not-found` is relayed with `404`) |
| `GET` | `/api/v1/work?repo=&tag=&status=` | `work-list`: `{repositories: [{repository, document}]}`, one entry per requested repository |
| `POST` | `/api/v1/repos/:id/work/:item/transitions` | `repository-work-transition`: the body of `POST /api/v1/work/:id/transitions` (`{action, reason?, type?, actor?, evidence?, conclusion?}`), run by the repository's own `praxis work transition`; `document` is its `work-transition` document or its structured refusal |

A `repository` node is the registry entry (`id`, `name`, `path`,
`registeredAt`) plus:

- `availability` `{status, reason}`, `status` one of `available` (its Praxis
  answered with the contract), `unreachable` (the registered path or its
  `./praxis` is gone, or it could not be started), `incompatible` (its Praxis
  ran but does not provide `praxis work state` -- an installation that
  predates it -- or speaks another contract version), or `unreadable` (it
  reported that its own recorded state could not be read);
- `repositorySource` `{repository, commit, branch, stateFingerprint, stable}`:
  the repository's own `./praxis state identity --json` read before and after
  the answer, or `{"unavailable": "..."}`.

The envelope's own `source` (added to every hub response, as on every
control-plane host) is the hub repository's identity -- its registry -- not
a registered repository's; use `repository.repositorySource` for that.

Statuses: the aggregated `work-list` and `repository-list` are always `200`;
a repository that cannot answer is reported in its own entry (`document` is
`null`) and never fails the response. A single-repository route answers
with the status `praxis web serve` gives the repository's document (`200`,
or the refusal's `404`/`409`/`422`/...); `502` with `praxis.error`
`repository-unavailable` (carrying the `repository` node) when that
repository could not answer; `404` with `repository-not-registered` for an
unknown repository id; `400` with `invalid-request` for a transition body
that is not a JSON object.

What this guarantees:

- **No central copy.** The hub persists only `.ros/hub/registry.json` and
  its `registry.md` projection. Every document is read from the repository
  at request time, so a change made in a repository by any means -- its own
  `./praxis`, its own `web serve`, another hub -- is served on the next
  request with no hub involvement, and nothing the hub answers can drift
  from what the repository itself reports.
- **Mutations belong to the owning repository.** The only mutations the hub
  offers -- creating a work item (`praxis add`/`work attach`) and requesting
  a transition (`praxis work transition`) -- are run by the owning
  repository's own Praxis, which applies its own rules and records its own
  events. The hub adds no rule, step or write before or after them. Identity
  and provenance are those of the repository's command running in the hub's
  environment, never taken from the HTTP client.
- **One contract.** A repository's document is the same JSON document its own
  `praxis work state` prints, so a client that reads one repository's
  `/api/v1/work` reads the hub's per-repository documents unchanged.

Each request runs a few processes per repository (`state identity` before
and after, plus the read), which suits a local hub over a handful of
repositories; it is not a bulk reporting interface.

## What this deliberately does not do

- No authentication, no multi-user access control, no audit log beyond
  what each spoke repository's own event log already records.
- No portfolio database and no cached copy of any repository's work
  state, no reporting/analytics beyond the raw aggregated
  table — building those is exactly the kind of "central project-management
  framework" Praxis's own architecture asks to be introduced only when a
  concrete need demonstrates it, not preemptively.
- No cross-machine repository access. Everything here assumes local paths.

## Tests

`tests/Ros.Tests/HubTests.fs` unit-tests the registry model (parsing and
re-rendering a registry written by the earlier Node hub byte for byte, the
Markdown projection, duplicate path/id rejection), the spoke command lines,
and the routes, then drives the real `praxis hub` commands and `praxis hub serve`
against temporary spoke repositories whose own `./praxis` runs the built CLI:
registration rules, creation with an attachment landing in the spoke's own
queue, aggregation isolating a moved repository as one error row,
unregistration, the JSON API (including a multipart upload and temp-file
cleanup), and the HTML form flows.
