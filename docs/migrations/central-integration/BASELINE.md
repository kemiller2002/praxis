# ROS Central Service and Integration Contract Upgrade — Phase 0 Baseline

Captured before any implementation work on the ROS Central migration
began, per section 2.3 ("Establish a baseline before modification") of
the migration specification.

## Repository state

| Field | Value |
|---|---|
| Commit SHA | `ae9f709816d0384bd1a198376e79f4fe33c0574e` |
| Branch | `main` |
| Tag | `ros-central-integration-baseline` (annotated; see **Tag push status** below) |
| Date/time captured | 2026-09-13T21:28:42Z |
| dotnet version | 10.0.111 |
| npm package version (`package.json`) | 2.0.1 |
| This repository's own `ros.json` `rosVersion` | 1.0.0 (unrelated legacy field — this repo's own `./ros` execs the F# CLI directly per `DF-ROS-2026-A030`, it does not use the launcher/download mechanism that field otherwise drives; not touched by, or relevant to, this migration) |

## Tag push status

The annotated tag `ros-central-integration-baseline` was created locally
and points at the commit above. Pushing it (`git push origin
ros-central-integration-baseline`) failed with a hard `403` from this
session's git credentials — retried with an explicit refspec, same
result, not transient. This session's push scope appears restricted to
specific branch refs rather than arbitrary tags, and no GitHub API tool
available in this session can create a tag ref directly either.

**Action needed from a human with full push access:**

```bash
git fetch origin main
git tag -a ros-central-integration-baseline ae9f709816d0384bd1a198376e79f4fe33c0574e -m "ROS Central Service and Integration Contract Upgrade: Phase 0 baseline"
git push origin ros-central-integration-baseline
```

Everything else in this baseline (the evidence below) is independent of
whether the tag itself has been pushed yet — it was captured directly
against commit `ae9f709` before any migration code was written.

## Test suite results (clean build, full local gate)

Ran via `npm run test:all` from a from-scratch build (all `bin/`/`obj/`
build output deleted first) at commit `ae9f709`.

| Suite | Passing | Failing | Total |
|---|---|---|---|
| Node (`node --test`, 5-file `test` script suite: `npm-bootstrap`, `ros-fs-launcher`, `ros-server`, `ros-hub`, `artifact-compatibility`) | 55 | 0 | 55 |
| Python (`unittest discover`, `test_ros_cli.py`) | 7 | 0 | 7 |
| F# unit (`Ros.Tests.dll`) | 376 | 0 | 376 |
| F# differential/CLI (`node --test`, 34 files comparing the F# CLI's real output against frozen golden-master literals) | 187 | 0 | 187 |
| **Total** | **625** | **0** | **625** |

Zero failures of any kind, including the intermittent, previously-observed
`Ros.Tests.dll` real-clock timing flake (`classifyTarget does not
deduplicate a repeated call`) — it did not reproduce on this run.

## Additional validation

| Check | Result |
|---|---|
| `./ros registry check` | `registries are current` |
| `./ros validate` | `validation passed` |
| `npm pack --dry-run` | succeeds — 118 files, 230.9 kB packed / 811.0 kB unpacked |

## CI status on `main`

Latest `ROS validation` workflow run on `main` at this commit
(`34619052252`, triggered by the merge of PR #50): `status: completed`,
`conclusion: success`.

## Known existing failure classes (pre-existing, not migration-created)

These have been observed and root-caused earlier in this repository's own
history; recorded here so that any future migration-phase test failure
can be checked against this list before being misattributed as new:

- **`Ros.Tests.dll`'s `classifyTarget does not deduplicate a repeated
  call, matching production's real-clock (not content-addressed)
  snapshotId`** — an intermittent, real-clock timestamp-collision-shaped
  flake, unrelated to any application code change; confirmed multiple
  times this session via corroborating parallel-run passes on the
  identical commit. Did not reproduce on this baseline run, but has
  reproduced on other commits before and since, purely as a function of
  execution timing, not code. See prior session PR discussion on `#47`
  and `#49` for the confirming re-runs.
- No other known-flaky or pre-existing-failing test exists in this
  repository at this commit.

## What this baseline is for

Every later migration-phase check (a new `Ros.Integration` package
build, a `Ros Central` host test suite, a compatibility-fixture test)
must be evaluated against this baseline: if a check that passed here
later fails without a code change in this migration's own new
components, the failure is not this migration's to silently absorb —
root-cause it, per the same CI-red discipline applied throughout this
repository's history, before assuming it is a pre-existing or unrelated
flake.
