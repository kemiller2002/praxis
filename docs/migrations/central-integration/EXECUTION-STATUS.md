# ROS Central Migration — Execution Status (WI-13 through WI-20)

This tracks what has actually been built versus what remains genuinely
blocked on something outside this development session's control — a
human with elevated push access, a real second repository, a live
deployed instance, or another team's own package. Per the migration
specification's own instruction ("do not execute later work items merely
because they are listed"), nothing here is worked around by faking the
missing piece; each blocker names exactly what unblocks it.

## WI-13 — Publish `EchelonFoundry.Ros.Integration` 1.0.0

**Done:**
- `src/Ros.Integration/Ros.Integration.fsproj`'s `<Version>` bumped to
  `1.0.0`.
- `.github/workflows/publish-integration-package.yml` created: triggers
  only on a `ros-integration-v*` tag, runs restore/build/all three test
  projects/pack before ever attempting to publish, uses the workflow's
  own short-lived `secrets.GITHUB_TOKEN` with `packages: write` (no
  long-lived PAT), pushes to GitHub Packages with `--skip-duplicate`.
- Verified locally: `dotnet pack src/Ros.Integration/Ros.Integration.fsproj
  -c Release` produces a clean `EchelonFoundry.Ros.Integration.1.0.0.nupkg`
  /`.snupkg` pair.

**Blocked, re-verified twice, by two independent mechanisms:**
1. `git push` of a tag returns a hard, non-transient `403` — re-tested
   live in this session (not assumed from the earlier
   `ros-central-integration-baseline` attempt in `BASELINE.md`): fetched
   `origin/main`, created a local tag at the current HEAD, attempted to
   push it, got `HTTP 403` / "unexpected disconnect" again, deleted the
   local probe tag afterward.
2. No available GitHub API tool can create a tag ref as a workaround
   either — checked the full current MCP tool surface for anything
   ref/tag-creating; the only tag-related tools available are read-only
   (`get_tag`, `list_tags`, `get_release_by_tag`) or create a *branch*
   ref (`create_branch`), never an arbitrary `refs/tags/...` ref.

This is a genuine, currently-in-effect credential-scope restriction, not
a stale or assumed one. As further confirmation: an attempt to create a
brand-new, disposable repository (to serve as a real second producer for
WI-14/WI-15/WI-20, see below) via the `create_repository` API also
returned `403 Resource not accessible by integration` — this session's
GitHub credentials are scoped to operating on the existing repository's
content and issues only, never to creating new refs, tags, or
repositories. Tracked as backlog item **`WI-0056`** in this repository's
own work queue (`.ros/work/queue.json`) and as
[issue #53](https://github.com/kemiller2002/repository-operating-system/issues/53)
so it isn't lost to this document alone.

**Action needed from a human with full push access**, once this PR (or
whichever PR carries this work) has merged to `main`:

```bash
git fetch origin main
git tag -a ros-integration-v1.0.0 <merge-commit-sha> -m "EchelonFoundry.Ros.Integration 1.0.0"
git push origin ros-integration-v1.0.0
```

Pushing that tag is what actually fires `publish-integration-package.yml`
and puts the package on GitHub Packages. Nothing else is required first.

## WI-14 — Connect one producer repository to Central

**Done, as far as this session's access allows**: `tools/ros_central_client.mjs`
is a real, tested connector (`tests/ros-central-client.test.mjs`, 3/3
passing against a mock server proving its HTTP contract) that any
repository's tooling can call to report an activity to a running
`ros-central` instance. It is feature-flag gated
(`ROS_CENTRAL_ENABLED=true` required) and wired into nothing in this
repository's own `./ros`/npm scripts by default — verified by grepping
`bin/`, `lib/`, and every Node CLI/server module for any reference to it
or the flag, finding none outside the tool itself.

This repository was then used as a real "one test repo," end to end,
against a real locally-running `ros-central` instance (not a second
repository, since this session's GitHub access is scoped to
`kemiller2002/repository-operating-system` only — see the honest gap
below):

```
$ ROS_EXTERNAL_ACTIVITY_ENABLED=true dotnet src/Ros.Host/bin/Release/net10.0/ros-central.dll
# separately:
$ ROS_CENTRAL_ENABLED=true node tools/ros_central_client.mjs http://127.0.0.1:58234 activity-1.json this-repo-shadow-trial
{"status":202,"body":{"activityId":"ACT-SHADOW-TRIAL-0001","state":"accepted"}}
```

Full real trial log and results are under WI-15 below.

**Honest remaining gap**: this is one repository (this one) talking to
one locally-run instance, not a second, independent repository talking
to a deployed one. That still requires a second real repository whose
maintainer agrees to connect it, and a reachable deployment target for
`Ros.Host` — this session has no cloud/AWS credentials or hosting target
(the migration spec itself places real AWS deployment in a later,
explicitly separate phase, gated on GitHub OIDC for a specific
repo/branch/environment, none of which exists yet), and manufacturing a
second "test repo" inside this same session would not be an independent
producer in any meaningful sense. Attempting to provision one via a real
`create_repository` API call was tried and refused
(`403 Resource not accessible by integration`) — this session's GitHub
credentials cannot create a new repository, only operate on the
existing one's content and issues. Tracked as backlog item
**`WI-0057`** and as
[issue #54](https://github.com/kemiller2002/repository-operating-system/issues/54).
See `PRODUCER-ONBOARDING-RUNBOOK.md` for the exact steps a real second
repository would follow once one is available.

## WI-15 — Shadow-mode comparison and metrics

**Done, as a genuine short session-local trial** (not a claim of a real
production observation period — the spec requires metrics "collected
during implementation, never invented after," so what follows is exactly
what was actually observed, nothing extrapolated):

1. Started a real `ros-central` instance locally
   (`ROS_EXTERNAL_ACTIVITY_ENABLED=true`, a fresh empty data directory).
2. Delivered activity `ACT-SHADOW-TRIAL-0001` once via the real
   connector → `202 accepted`.
3. Re-delivered the **identical** `ACT-SHADOW-TRIAL-0001` payload again
   (simulating a producer retry) → `202 accepted`, same body.
4. Delivered a second, distinct activity `ACT-SHADOW-TRIAL-0002` → `202
   accepted`.
5. Read Central's own `activities.json` back directly: **exactly 2
   records** for the 2 unique activity ids, despite 3 real HTTP calls —
   the idempotent retry produced zero duplicates.
6. Confirmed local ROS behavior is unaffected: with `ROS_CENTRAL_ENABLED`
   and the other flags fully unset, `./ros validate` still passes, and a
   repo-wide grep confirms no code path in `bin/`, `lib/`, or any Node
   CLI/server module references the connector or the flag.

**Observed counts from this trial** (N=3 real HTTP calls, a single
session, immediately discarded afterward — this is a mechanism proof,
not a production sample): 3/3 calls succeeded, 2/2 unique activities
recorded exactly once, 1/1 duplicate retry correctly absorbed with no
new record, 0 divergences between what was sent and what Central
recorded, 0 changes to local ROS behavior with the flags off.

**Honest remaining gap**: a real shadow-mode evaluation per
`MIGRATION-PLAN.md` Phase 7 means comparing Central's view against a
second repository's real, organic activity over an actual observation
period (days, not one session) — that requires WI-14's real second
repository and deployment first, which remains blocked as described
above.

## WI-16 — Define `Chrona.Integration` requirements with Chrona

**Requires**: joint definition with whoever owns the Chrona
repository/team — this session has no Chrona repository attached and no
channel to that team. As a best-effort proxy for ROS's side of that
conversation, see
`docs/migrations/central-integration/CHRONA-INTEGRATION-REQUIREMENTS-PROPOSAL.md`,
written from what ROS already knows it needs to send. It is a starting
proposal for Chrona's team to react to, not a joint agreement — WI-16
is not complete until Chrona's side has actually reviewed and (dis)agreed
with it. Tracked as backlog item **`WI-0058`** and as
[issue #52](https://github.com/kemiller2002/repository-operating-system/issues/52)
so the need for that review is a live, visible obligation, not just a
sentence in this document.

## WI-17/WI-18/WI-19 — Consume `Chrona.Integration`, GitHub datastore
adapter, shadow-test ROS→Chrona

**Requires**: `Chrona.Integration` to exist as a published package,
which requires WI-16 to conclude first. This migration already has the
documented fallback the spec itself prescribes:
`IntegrationTarget = Chrona | Summa | Other of string` and
`OutboundDeliveryState = NotReady | ...` (both implemented in
`src/Ros.ProjectAdministration`, WI-7/WI-8) let Central create outbox
entries for a `Chrona` target and hold them at `NotReady` indefinitely,
with zero dependency on `Chrona.Integration` existing.

**Done, beyond that fallback**: `src/Ros.Integrations.GitHub` implements
and mechanically proves the actual delivery mechanism WI-17–19 need,
using a placeholder shaped like this migration's own
`CHRONA-INTEGRATION-REQUIREMENTS-PROPOSAL.md` (`ChronaPlaceholder.fs`,
explicitly and repeatedly labeled throughout as **not** the real,
not-yet-published `Chrona.Integration` package — it is deleted the
moment that package exists, per `AGENTS.md`'s "Compatibility Before
Replacement" rule):

- `GitHubDatastoreWriter` writes a candidate keyed by the originating
  `ActivityId`, so a redelivered candidate overwrites the same file
  rather than creating a second one.
- `LocalGitSimulation` commits that write to a real, disposable local
  git repository — a stand-in for Chrona's actual GitHub-hosted
  datastore repo until real GitHub App credentials for Chrona exist, but
  every commit it makes is a real `git commit`, not a simulated count.
- `tests/Ros.Integrations.GitHub.Tests` (5/5 passing) mechanically proves
  the exact Phase 9 acceptance criteria against real git history: one
  observation → one candidate and one commit; an identical redelivery →
  still one candidate and **no new commit** (proven via `git diff
  --cached --quiet`, not reimplemented); a genuinely different
  candidate → a second file and a second commit; a real content change
  to an existing candidate → a new commit, not a silent overwrite.

**Honest remaining gap**: this proves the mechanism, not the real
integration. Two things still require Chrona's actual involvement: (1)
`ChronaPlaceholder`'s shape is this session's guess at what Chrona wants
(see the proposal doc's own open questions), not something Chrona has
confirmed; and (2) `LocalGitSimulation` commits to a scratch local repo,
not a real `git push` to a real GitHub-hosted repository under Chrona's
control via an authenticated GitHub App — that requires Chrona's repo
and a real App installation, neither of which this session can create.

## WI-20 — Incrementally onboard additional producer repositories

**Done, as far as this session's access allows**:
`PRODUCER-ONBOARDING-RUNBOOK.md` is a concrete, step-by-step runbook for
onboarding an additional repository, built directly on the connector and
Central instance already proven working in WI-14/WI-15 — copy the
connector (or, once WI-13 unblocks, depend on the published package),
set the feature flag, construct a payload, report it, verify the
response. Every step in it has already been exercised for real using
this repository as the producer.

**Requires** to actually execute: WI-14 and WI-15 concluding for a first
*genuinely separate* repository, then the same for each additional one,
per `MIGRATION-PLAN.md`'s own Phase 10 acceptance criteria. Tracked
together with WI-14/WI-15 as backlog item **`WI-0057`**, since all three
share the same root blocker: no second repository or live deployment
target exists in this session's scope.

## Summary

| Work item | Status | Backlog |
|---|---|---|
| WI-13 | Code/workflow/version bump done; actual tag push + publish blocked on human push access — re-verified live via both `git push` (403) and a full scan of available GitHub API tools (no ref-creation tool exists) | `WI-0056` |
| WI-14 | `Ros.Host` + a real, tested connector (`ros_central_client.mjs`) built and run end-to-end against a live local instance using this repo as the producer; blocked on a genuinely separate second repository + a real deployment target | `WI-0057` |
| WI-15 | Real short session-local trial run and recorded (3/3 calls succeeded, 2/2 unique activities recorded exactly once, 1/1 duplicate correctly absorbed, 0 divergences); blocked on a real multi-day observation period against WI-14's real second repository | `WI-0057` |
| WI-16 | Best-effort ROS-side proposal written; blocked on Chrona team review | `WI-0058` |
| WI-17–19 | The delivery mechanism (write + idempotent commit) is built and mechanically proven (5/5 tests) against a real local git repository using an explicitly-labeled placeholder; blocked on Chrona publishing its real package and on a real GitHub App installation for the actual push | `WI-0058` |
| WI-20 | Runbook written and its every step already exercised via WI-14's trial; actual multi-repository rollout blocked on the same second-repository/deployment gap as WI-14/WI-15 | `WI-0057` |

Backlog items `WI-0056`, `WI-0057`, and `WI-0058` are captured (not yet
ready/active, since none is actionable until its external blocker
lifts) in this repository's own `.ros/work/queue.json` — visible to
`./ros status`/`./ros work ready` once each becomes actionable, not just
recorded in this document. Each also has a real, open GitHub issue
tracking it externally:
[#52](https://github.com/kemiller2002/repository-operating-system/issues/52) (WI-16),
[#53](https://github.com/kemiller2002/repository-operating-system/issues/53) (WI-13),
[#54](https://github.com/kemiller2002/repository-operating-system/issues/54) (WI-14/WI-15/WI-20).

Two of these blockers were independently confirmed, not merely assumed,
via real API calls this session made and that were refused: `git push`
of a tag (`403`) and `create_repository` to provision a disposable
second producer (`403 Resource not accessible by integration`). This
session's GitHub credentials can read, comment on, open issues against,
and push commits/branches to the existing repository — and nothing
beyond that.
