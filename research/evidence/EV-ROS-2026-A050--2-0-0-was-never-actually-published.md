---
id: EV-ROS-2026-A050
title: "2.0.0 was never actually published to npm or released on GitHub; publish.yml's version-changed detection cannot retry it"
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-11
updated: 2026-09-11
research_area: repository-operating-system
evidence_type: primary
supports: []
related_documents:
  - EV-ROS-2026-A049
  - .github/workflows/publish.yml
supersedes: []
superseded_by: []
tags: [ci, release, npm, publish, sde]
confidence: high
---

# Evidence summary

`EV-ROS-2026-A049` recorded that run `34570090987` (the `2.0.0` version-bump
commit `9804e34`, PR `#44`) transitioned from two billing-gated instant
failures to a real `in_progress` state on attempt 3, once the account's
Actions billing was fixed. That record stopped at the state transition and
did not follow the run to its actual conclusion.

Checked directly against the GitHub Actions API: attempt 3 (job
`103232561157`, started `10:34:52Z`, completed `10:35:15Z`) did run for
real, but its own `Test and validate` step (`npm run test:all`) failed at
`10:35:12Z` -- the exact three bugs later fixed in PR `#45` (build-order,
the version-derived `ROS-INSTALL` id, and environment-dependent identity
detection) surfaced here for the first time, on a genuinely fresh checkout
at the new version. Every step after `Test and validate` --
`Determine whether package.json's version changed`,
`Check whether this version is already on the registry`,
`Publish stable release`, `Build self-contained ros-fs binaries`, and
`Create GitHub Release` -- shows `conclusion: "skipped"`. The run's overall
`conclusion` is `"failure"`, not `"success"`.

Confirmed independently against the actual deliverables:
- `npm view @echelon-foundry/repository-operating-system version` returns
  `1.2.1` (the last real stable release); `versions --json` lists a
  `2.0.0-main.76.1` snapshot under the `main` dist-tag but no `2.0.0`.
- `GET /repos/kemiller2002/repository-operating-system/releases/tags/v2.0.0`
  returns `404`; `list_releases` returns an empty array.

PR `#45` (commit `a1ea994`) fixed the three real bugs but did not change
`package.json`'s version again -- it was already `2.0.0`. Its own
`Determine whether package.json's version changed` step correctly computed
`changed=false` against its immediate parent, so the entire stable-publish
branch (steps 10-14) was skipped again, this time by design rather than by
failure. Only the `main`-snapshot path ran, publishing
`2.0.0-main.76.1` under the `main` dist-tag.

# Method

Observational: read `publish.yml`'s own step conditions, `list_workflow_jobs`
for both the `2.0.0` bump run (`34570090987`) and PR `#45`'s run
(`34593316545`) with full step-level detail, `get_commit` on `a1ea994` to
confirm what PR `#45` actually changed, and `npm view`/`get_release_by_tag`/
`list_releases` against the real registry and repository.

# Findings

- **`2.0.0` has never been published to npm, and no `v2.0.0` GitHub
  Release has ever been created.** The version-bump commit's own publish
  attempt failed before reaching the publish/release steps; the very
  fixes that would let it succeed were committed on a later commit that,
  by the workflow's own (correct) version-changed logic, cannot re-attempt
  a stable publish for a version that isn't actually changing anymore.
- This is a structural gap in `publish.yml`, not a one-off mistake: the
  workflow's stable-publish path is keyed off "did this exact push change
  `package.json`'s version relative to its immediate parent," which is
  right for avoiding duplicate publishes but has no self-healing path for
  "the version changed, the publish attempt failed for unrelated reasons,
  and the version needs to be attempted again." The `already_published`
  registry check (step 10) exists to make a *re-run of the same commit*
  idempotent, but nothing re-runs `34570090987` after billing/tests were
  fixed, and PR `#45` -- landing after the version was already committed
  -- structurally cannot trigger it either.
- The prior report to the user ("Yes but bump it to 2.0.0" -> workflow
  ran -> conclusion recorded as reaching `success` on PR `#45`'s run) was
  true of PR `#45`'s own run but incorrectly implied the `2.0.0` release
  itself was live; it was not verified against the actual npm registry or
  GitHub Releases API before being treated as done. Verifying the
  workflow's own conclusion is not equivalent to verifying the deliverable
  it was meant to produce.

# Consequences

The only way forward that respects the workflow's existing (correct)
idempotency logic is to advance the version again -- there is no
version-bump commit left that can still legally trigger a first stable
publish of `2.0.0`, since the version is not "changing" relative to its
own parent anymore. This evidence record recommends bumping to `2.0.1`
immediately, now that the three real defects that blocked `2.0.0`'s own
attempt are already fixed and merged to `main`, so the new version's own
first publish attempt succeeds for real. `publish.yml` itself needs no
change: the underlying defects were in the tests, not in the release
workflow's logic.

# Reversibility and validation

Observational; nothing to revert. Validation is the next version bump's
own publish run reaching `Publish stable release`/`Create GitHub Release`
with `conclusion: "success"`, followed by an independent check of
`npm view @echelon-foundry/repository-operating-system version` and
`GET /repos/.../releases/tags/v<version>` against the real registry and
API -- not the workflow run's own reported conclusion alone.

# Addendum: 2.0.1 verified live

`2.0.1`'s own publish run (`34595220569`, triggered by merging PR `#46`)
reached every previously-skipped step with `conclusion: "success"`:
`Publish stable release`, `Build self-contained ros-fs binaries`, and
`Create GitHub Release with ros-fs binaries`. Independently confirmed
against the real deliverables (not the workflow's own report): `npm view
@echelon-foundry/repository-operating-system dist-tags` shows
`"latest": "2.0.1"`, and `GET /repos/.../releases/tags/v2.0.1` returns a
published, non-draft, non-prerelease release with all 6 expected assets
(`ros-fs-linux-x64`, `ros-fs-linux-arm64`, `ros-fs-osx-x64`,
`ros-fs-osx-arm64`, `ros-fs-win-x64.exe`, `checksums.txt`). This is the
first version of this package with a real, verified end-to-end release.
