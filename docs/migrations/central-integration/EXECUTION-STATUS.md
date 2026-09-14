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

**Blocked:** actually triggering a publish requires pushing the tag
`ros-integration-v1.0.0`. This session's git credentials returned a hard,
non-transient `403` pushing the earlier `ros-central-integration-baseline`
tag (see `BASELINE.md`'s "Tag push status") — the same credential-scope
restriction applies to any tag push, not just that one. No GitHub API
tool available in this session can create a tag ref directly either.

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

**Requires**: a second real repository, distinct from this one, whose
maintainer agrees to have it call the Central host; and a reachable,
running deployment of `Ros.Host` (`ros-central`) for that repository to
call. This session's GitHub access is scoped to
`kemiller2002/repository-operating-system` only — no second repository is
attached, and this session has no cloud/AWS credentials or hosting
target to deploy `Ros.Host` to (the migration spec itself places real
AWS deployment in a later, explicitly separate phase, gated on GitHub
OIDC being configured for a specific repo/branch/environment, none of
which exists yet).

**What is ready for when that exists**: `Ros.Host` (`src/Ros.Host`) is a
complete, tested, runnable ASP.NET Core minimal-API executable —
`GET /health`, `GET /version`, and `POST /integration/v1/activities` all
verified end-to-end in this session (manual `curl` smoke test against a
locally running instance, covering the 200/400/422/202-idempotent-retry
paths). Standing it up anywhere reachable and pointing one repository's
tooling at it is the only remaining step, and that step needs a human
decision about where it runs (the spec explicitly wants "one monolith +
managed infra," not a decision this session should make unilaterally).

## WI-15 — Shadow-mode comparison and metrics

**Requires**: WI-14 to be live first, plus an observation period of real
traffic. This is explicitly a metrics-driven work item — the spec
requires metrics "collected during implementation, never invented after"
— so there is nothing honest to produce here until WI-14 exists and has
run for a real period. Fabricating comparison numbers would violate the
same evidence-fabrication rule this session's operating instructions
(`AGENTS.md`) already forbid.

## WI-16 — Define `Chrona.Integration` requirements with Chrona

**Requires**: joint definition with whoever owns the Chrona
repository/team — this session has no Chrona repository attached and no
channel to that team. As a best-effort proxy for ROS's side of that
conversation, see
`docs/migrations/central-integration/CHRONA-INTEGRATION-REQUIREMENTS-PROPOSAL.md`,
written from what ROS already knows it needs to send. It is a starting
proposal for Chrona's team to react to, not a joint agreement — WI-16
is not complete until Chrona's side has actually reviewed and (dis)agreed
with it.

## WI-17/WI-18/WI-19 — Consume `Chrona.Integration`, GitHub datastore
adapter, shadow-test ROS→Chrona

**Requires**: `Chrona.Integration` to exist as a published package,
which requires WI-16 to conclude first. Until then, this migration
already has the documented fallback the spec itself prescribes:
`IntegrationTarget = Chrona | Summa | Other of string` and
`OutboundDeliveryState = NotReady | ...` (both implemented in
`src/Ros.ProjectAdministration`, WI-7/WI-8) let Central create outbox
entries for a `Chrona` target and hold them at `NotReady` indefinitely,
with zero dependency on `Chrona.Integration` existing. That is the
concrete, already-built stand-in for WI-17–19 until Chrona's package is
real.

## WI-20 — Incrementally onboard additional producer repositories

**Requires**: WI-14 and WI-15 to have concluded for a first repository,
then the same for each additional one, one at a time, per
`MIGRATION-PLAN.md`'s own Phase 10 acceptance criteria. Not started for
the same reason as WI-14: no second repository or live deployment target
exists in this session's scope.

## Summary

| Work item | Status |
|---|---|
| WI-13 | Code/workflow/version bump done; actual tag push + publish blocked on human push access |
| WI-14 | `Ros.Host` built and verified locally; blocked on a second repository + a deployment target |
| WI-15 | Blocked on WI-14 (cannot honestly produce metrics before real traffic exists) |
| WI-16 | Best-effort ROS-side proposal written; blocked on Chrona team review |
| WI-17–19 | Not started; `NotReady`/`IntegrationTarget` fallback already in place per spec; blocked on WI-16 concluding and Chrona publishing a package |
| WI-20 | Not started; blocked on WI-14/WI-15 concluding for a first repository |
