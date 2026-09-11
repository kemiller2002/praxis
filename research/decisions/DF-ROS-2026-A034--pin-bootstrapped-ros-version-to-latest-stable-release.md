---
id: DF-ROS-2026-A034
title: "ros-bootstrap init pins a scaffolded project's ros.json to the latest stable release, not the installed package's own version"
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-11
updated: 2026-09-11
research_area: repository-operating-system
decision_type: architecture
supports: []
related_documents:
  - EV-ROS-2026-A051
  - DF-ROS-2026-A029
  - PACKAGE-USAGE.md
  - lib/bootstrap.mjs
  - .github/workflows/publish.yml
supersedes: []
superseded_by: []
tags: [ros-fs, bootstrap, launcher, npm, release]
confidence: high
---

# Decision

`ros-bootstrap init`'s scaffolded `ros.json` `rosVersion` field is now
resolved from a bundled `lib/stable-ros-version.json` file (written by
`publish.yml` into every tarball it publishes, naming the real committed
version at build time) when present, falling back to the installed
package's own `package.json` version otherwise. A user who installs via
the documented `@main` snapshot flow gets a scaffolded project whose
`./ros` is pinned to the newest **stable** release with real published
binaries -- not to the exact (binary-less) snapshot they happened to
install.

Every other use of the installed package's own version -- the
`.ros/installation.json` provenance record, the `ROS-INSTALL-<version>`
work-item id, and the CLI's own "installed ROS X into Y" message -- is
unchanged and still reports the true installed version, so audit trails
stay accurate even though `ros.json`'s `rosVersion` no longer always
matches it.

# Context

`EV-ROS-2026-A051` fixed `ros_fs_launcher.mjs` to fail fast with a clear
message instead of a raw 404 when `ros.json`'s `rosVersion` is a
main-branch snapshot -- but a user reasonably pointed out that this only
polishes the failure, it doesn't fix it: bootstrapping via the documented
`@main` install flow (`PACKAGE-USAGE.md`, "install the newest continuously
published snapshot from `main`") still produces a project whose `./ros`
can never run, because `publish.yml` only ever builds and releases
`ros-fs` binaries on a stable version bump, never for the unconditional
`-main.<run>.<attempt>` snapshot it publishes to npm's `main` dist-tag on
every push.

# Alternatives considered

1. **Publish a GitHub Release (with binaries) for every main-branch
   snapshot too.** Guarantees `@main` installs always have a matching
   binary. Rejected as disproportionate: it means five platform builds and
   a new GitHub Release on every single push to `main`, a real, recurring
   CI cost and unbounded release-list growth, to serve a install path whose
   own documentation already limits it to "only already-ported,
   differential-tested commands are safe to rely on" -- i.e. it was never
   meant to be a fully-supported, always-working surface in the first
   place.
2. **Have the bootstrapped project's `rosVersion` strip the `-main.` suffix
   down to its base version** (e.g. `2.0.1-main.78.1` -> `2.0.1`) with no
   further check. Rejected as unsound: the base version is not guaranteed
   to have a completed release. `EV-ROS-2026-A050` is the concrete case --
   `2.0.0-main.76.1` snapshots were published while `2.0.0`'s own stable
   release attempt had already failed and no `v2.0.0` release existed at
   all; stripping the suffix would have pinned a scaffolded project at a
   version with no binary either.
3. **Query the npm registry or GitHub Releases API at bootstrap time** to
   discover the true latest stable version. Rejected: it adds a network
   dependency to what is otherwise a fully offline, local-file operation
   (`PACKAGE-USAGE.md`'s own framing: "a target repository does not read
   from or link to this source checkout after installation"), and
   duplicates information the publish job already has for free.
4. **Bake the real, already-verified stable version into the tarball at
   publish time** (chosen). `publish.yml` already computes
   `steps.version-check.outputs.current` -- the real committed
   `package.json` version, always a plain stable release -- before it does
   anything version-mutating for the snapshot publish. Recording that value
   into a small bundled file costs nothing extra at publish time, adds no
   network dependency at install time, and is provably correct: the value
   is written before any of the binary-build/release-creation steps run,
   and if any of those steps fail the whole job stops, so no tarball is
   ever published (stable or snapshot) carrying a version this file
   doesn't already have a completed release for by the time it's
   downloadable.

# Consequences

- `lib/bootstrap.mjs` gains `resolveRosVersion(packageMetadata,
  stableVersionOverride)`, a pure function preferring the override when
  present; `loadPlan` reads `lib/stable-ros-version.json` (if it exists)
  and passes its parsed content as that override. A source checkout (this
  repository itself, or a `github:...#<ref>` git install, neither of which
  goes through `publish.yml`) has no such file and falls back to the
  installed `package.json` version unchanged -- both were already stable
  in that case, so this is a no-op for every install path except the npm
  `@main` dist-tag.
- `publish.yml` gains one new step, "Record the stable ros-fs version for
  bootstrapped snapshot installs", writing
  `{"version": "<the real committed version>"}` to
  `lib/stable-ros-version.json` right after computing that value and
  before any publish happens. The file is gitignored -- it is a build
  artifact of the publish job, never committed to source.
- `ros-bootstrap init`'s console output now prints an explanatory note
  (not a "this won't work" warning) whenever the resolved `rosVersion`
  differs from the installed package version, naming both, so the
  substitution is visible rather than silent.
- A `@main` install's `./ros` now runs the newest stable release's F# CLI,
  not whatever the exact installed snapshot's scaffolding templates
  produced. This is an accepted tradeoff: the `@main` flow's real value is
  always-current scaffolding/bootstrap-logic fixes, not a way to preview
  unreleased F# CLI behavior through `./ros` -- that already required
  building from source per `PACKAGE-USAGE.md`.

# Reversibility and validation

Fully reversible: `lib/stable-ros-version.json` is generated, gitignored,
and additive; removing the new publish.yml step and `resolveRosVersion`'s
override branch returns to the prior (broken) behavior with no cleanup
needed elsewhere. Validated with new unit tests for `resolveRosVersion`
(both branches), an integration test confirming a normal install's
`rosVersion` still matches the installed package version with no override
file present, and a manual end-to-end check: writing a distinguishable
override file, running `initializeProject`, and confirming `ros.json`'s
`rosVersion` follows the override while `.ros/installation.json`'s
`package_version` still reports the true installed version.
