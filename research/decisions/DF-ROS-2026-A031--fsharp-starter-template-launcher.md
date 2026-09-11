---
id: DF-ROS-2026-A031
title: Add an additive F# ros-fs launcher to the greenfield starter template
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-11
updated: 2026-09-11
author_agent: claude-sonnet-5
supporting_evidence:
  - EV-ROS-2026-A048
related_documents:
  - DF-ROS-2026-A028
  - DF-ROS-2026-A029
  - DF-ROS-2026-A030
  - PACKAGE-USAGE.md
supersedes: []
superseded_by: []
tags: [architecture, fsharp, migration, distribution, npm, sde, decision]
confidence: high
---

# Context

`DF-ROS-2026-A030` redirected this repository's own `./ros` to the F# CLI.
The user's own request also asked to "setup npm so it can be installed and
used on other repos through it like the node version" — i.e., give
projects bootstrapped via `npx ros-bootstrap init` an F#-backed option too,
not only this source repository.

# A near-miss worth recording

The first design considered redirecting the *scaffolded* `starter/greenfield/ros`
itself to the F# launcher, mirroring `DF-ROS-2026-A030` exactly. Before
shipping it, testing surfaced that this would have been badly wrong: this
entire migration's differential-test suite bootstraps a disposable project
via `initializeProject` and spawns its `ros` file as "real, unpatched
Node" for comparison against the F# side (the phrase "driving the actual
`ros` CLI wrapper directly" recurs throughout `STATUS.md`/`TRACEABILITY.md`
for exactly this reason). Redirecting the scaffolded `ros` to the F#
launcher would have made every one of those tests compare F# against F#
(via a real network download on every test run, with no actual Node
baseline left to differ from) — silently invalidating the verification
this whole migration is built on, not just changing a template. This was
caught by testing the change before merging it, and reverted before it
reached the tests or a commit.

# Decision

Ship the F# CLI to bootstrapped projects **additively**: a new `ros-fs`
entrypoint (`starter/greenfield/ros-fs`, backed by a new
`starter/greenfield/tools/ros_fs_launcher.mjs`) scaffolded alongside the
existing, completely unchanged `ros` (Node). This exactly mirrors how
`DF-ROS-2026-A029` added `ros-fs` to this package's own npm `bin` surface
rather than replacing `ros-bootstrap`.

`ros_fs_launcher.mjs` is the same acquire-verify-cache-exec logic as this
package's own `lib/ros-fs-launcher.mjs`, adapted for a bootstrapped
project: it reads its target version from the scaffolded `ros.json`'s own
`rosVersion` field (populated at scaffold time from this package's
version) rather than a `package.json`, since a bootstrapped project has
none of this package's own files. Everything else — RID resolution,
SHA-256 checksum verification against the release's `checksums.txt`,
per-version/per-platform caching, `ROS_FS_RELEASE_BASE_URL`/
`ROS_FS_CACHE_DIR` overrides — is identical.

`starter/greenfield/manifest.json` gained one new file entry
(`ros-fs`, executable) and one new supporting module entry
(`tools/ros_fs_launcher.mjs`); nothing else in the manifest changed.

# Alternatives

- **Redirect the scaffolded `ros` to F#:** rejected — see the near-miss
  above. This is also a *materially bigger* decision than
  `DF-ROS-2026-A030`'s repository-local one, since it would affect every
  consumer of the npm package, not just this repository; it stays out of
  scope here regardless of the testing risk.
- **Copy this repository's Node CLI files out of the scaffold and replace
  them with F# entirely:** rejected for the same reason, compounded: it
  would remove the Node CLI from every future bootstrapped project with no
  proven F# replacement at that surface yet.

# Consequences

A project bootstrapped after this change gets `./ros` (Node, exactly as
before — no behavior change, no test-suite impact) and `./ros-fs`
(F#, additive, network access needed on first use per version/platform,
then offline). Command syntax between the two differs the same way it
does in this repository (see `DF-ROS-2026-A030`); a bootstrapped project's
own agents should consult this upstream project's
`docs/migrations/fsharp/STATUS.md` for current parity before relying on
`ros-fs` for anything beyond what has been verified there.

# Reversibility and validation

Fully reversible: drop the two new manifest entries and delete
`starter/greenfield/ros-fs`/`starter/greenfield/tools/ros_fs_launcher.mjs`.
No existing scaffolded file changed. Validated by: the full existing test
suite (113 Node, 7 Python, 375 F# unit, 185 differential — unaffected,
confirming the near-miss above did not reach this decision's shipped
form), a new test verifying `ros-fs`/`tools/ros_fs_launcher.mjs` are
scaffolded with the right permissions and included in the npm tarball, and
a new test exercising the launcher's real download/verify/cache/exec
behavior in-process against a local HTTP server (matching
`tests/ros-fs-launcher.test.mjs`'s own proven pattern, chosen specifically
because a spawned-subprocess variant of the same test does not complete in
this development sandbox's own outbound-network setup — an environment
limitation unrelated to the launcher's correctness, which manual testing
by direct invocation confirmed separately).
