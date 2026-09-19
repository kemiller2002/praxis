---
id: EV-ROS-2026-A051
title: "The ros-fs launcher gave a raw 404 instead of a clear error for a main-branch snapshot rosVersion; fixed with a fail-fast version check"
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
  - PACKAGE-USAGE.md
  - lib/ros-fs-launcher.mjs
  - starter/greenfield/tools/ros_fs_launcher.mjs
  - lib/bootstrap.mjs
supersedes: []
superseded_by: []
tags: [ros-fs, bootstrap, launcher, npm, bug, sde]
confidence: high
---

# Evidence summary

A user reported that `./ros` is "broken out of the box for this version": a
bootstrapped project's `ros.json` carried `rosVersion: "2.0.1-main.78.1"`,
and `starter/greenfield/tools/ros_fs_launcher.mjs` tried to download the
`ros-fs` binary from
`https://github.com/kemiller2002/repository-operating-system/releases/download/v2.0.1-main.78.1/`
-- a release that has never existed and never will, since `publish.yml`
only builds and publishes `ros-fs` binaries on a **stable** version bump
(`X.Y.Z`), never for the `-main.<run>.<attempt>` snapshot versions it
publishes to npm's `main` dist-tag on every push.

Reproduced directly: `npx --yes --package=@echelon-foundry/repository-operating-system@main
ros-bootstrap init --target .` (the exact flow `PACKAGE-USAGE.md` documents
under "To install the newest continuously published snapshot from `main`")
installs `rosVersion: "2.0.1-main.78.1"` into `ros.json`, and running
`./ros registry check` afterward produces:

```
ros binary for linux-x64 not cached; downloading from https://github.com/.../releases/download/v2.0.1-main.78.1/ros-fs-linux-x64
./ros: failed to obtain the linux-x64 binary for version 2.0.1-main.78.1: request to .../checksums.txt failed: 404 Not Found
```

This is not a silent crash -- the error is caught and reported -- but it
contradicts `PACKAGE-USAGE.md`'s own existing claim ("`@main`-tagged
prerelease snapshots have no matching release and the CLI reports that
plainly rather than guessing"): a generic "404 Not Found" forces the reader
to already understand the stable-vs-snapshot release model to know this is
expected and permanent, not a transient network problem worth retrying.

# Method

Reproduced the exact documented `@main` install command against the real
npm registry and GitHub, confirmed the resulting `ros.json`'s `rosVersion`
and the real 404 by running the scaffolded `./ros` inside the fresh
project. Read `publish.yml` to confirm binaries are only ever built on the
stable-release path (`steps.registry-check.outputs.already_published ==
'false'` gated on `steps.version-check.outputs.changed == 'true'`), never
for the unconditional main-snapshot publish step.

# Findings

- `publish.yml`'s binary-and-release step never runs for a `-main.` snapshot
  version -- this is not a bug in the workflow, it is the documented,
  intentional design (stable releases are deliberate; `main` snapshots are
  npm-only, source-verification artifacts). The bug is entirely in how the
  launcher handles a snapshot version at request time: it always attempts
  the network round-trip and only reports whatever the fetch itself says,
  rather than checking the version's own shape first.
- Both launcher copies (`lib/ros-fs-launcher.mjs`, used by this package's
  own `ros-fs` bin entry, and `starter/greenfield/tools/ros_fs_launcher.mjs`,
  scaffolded into every bootstrapped project) share the identical gap, since
  they are intentionally near-duplicate implementations of the same
  acquire-verify-cache-exec mechanism.
- `lib/bootstrap.mjs`'s `ros-bootstrap init` itself gave no signal at
  install time that the scaffolded project's `./ros` would never work until
  upgraded to a stable release -- the failure only surfaced on the first
  attempt to actually run `./ros`, one step later than necessary.

# Consequences

Fixed by adding `isStableVersion(version)` (a plain `X.Y.Z` shape check) to
both launcher copies, checked immediately after resolving the target
version and before any network call; on a non-stable version, both now
return exit code 1 with a specific `nonStableVersionMessage` naming the
version, stating plainly that no release/binary is ever published for a
snapshot version, and pointing at `PACKAGE-USAGE.md`'s "Install from npm"
section for a stable install instead. `lib/bootstrap.mjs`'s `main()` now
also prints a warning at install time when the version it is about to
scaffold is not stable, so the constraint surfaces before the user ever
tries to run `./ros`. `PACKAGE-USAGE.md`'s existing claim about "plain"
reporting is now actually true rather than aspirational.

Verified with new tests: `isStableVersion`/`nonStableVersionMessage` unit
tests in `tests/ros-fs-launcher.test.mjs`, and a true end-to-end test in
`tests/npm-bootstrap.test.mjs` that scaffolds a real project, overwrites
its `ros.json` with a snapshot `rosVersion`, and asserts the scaffolded
launcher refuses with the specific message and **zero** network requests
(a fake HTTP server records every hit; none occur).

# Reversibility and validation

Fully reversible (additive checks only; no existing behavior for a stable
version changes). Validated via the full local gate (`npm run test:all`:
52/52 Node, including the 3 new tests; 185/185 F# differential/CLI) and by
directly swapping the fixed launcher file into the real reproduced
`@main`-installed project and confirming the specific message now appears
immediately with no network attempt, in place of the prior generic 404.
