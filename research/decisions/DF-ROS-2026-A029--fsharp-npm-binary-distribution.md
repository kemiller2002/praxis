---
id: DF-ROS-2026-A029
title: Distribute ros-fs via npm as a self-contained single-file binary fetched from GitHub Releases
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-10
updated: 2026-09-10
author_agent: claude-sonnet-5
supporting_evidence:
  - EV-ROS-2026-A047
  - EV-ROS-2026-A048
related_documents:
  - DF-ROS-2026-A027
  - DF-ROS-2026-A028
  - docs/migrations/fsharp/ARCHITECTURE.md
  - docs/migrations/fsharp/STATUS.md
  - PACKAGE-USAGE.md
supersedes: []
superseded_by: []
tags: [architecture, fsharp, migration, distribution, npm, sde, decision]
confidence: high
---

# Context

The user asked, directly, for the F# CLI to be downloadable and usable by
other projects through npm. `DF-ROS-2026-A028` had already opened the
authority-switch decision track as three gated phases: Phase A (full
command-surface effect parity), Phase B (consumer distribution evidence),
and Phase C (the actual `./ros` dispatch switch, requiring its own new
decision record). `EV-ROS-2026-A047` closed Phase A (all 26 command-surface
rows now read "Full parity"). This record, together with `EV-ROS-2026-A048`,
closes Phase B: it accepts a specific consumer distribution shape with
evidence attached, as that phase's acceptance criterion requires.

This record deliberately does **not** attempt Phase C. `DF-ROS-2026-A028` is
explicit that redirecting `./ros`'s own dispatch (in this repository or in
`starter/greenfield/ros`) needs a further, separate decision record with a
stated rollback plan and cutover mechanism, and that no phase may be skipped
because a later one looks tractable. What this record authorizes is
narrower and additive: a **new, optional** way to obtain and run the F# CLI
via npm, alongside the existing Node-based `ros-bootstrap`/`./ros` flow,
which is unchanged by this decision.

# Decision

Ship `ros-fs` as a second `bin` entry in the existing npm package
(`bin/ros-fs.mjs`, backed by `lib/ros-fs-launcher.mjs`). On first invocation
for a given (package version, platform) pair, it downloads a self-contained,
single-file `dotnet publish` build of `ros-fs` from that version's GitHub
Release, verifies its SHA-256 against the release's `checksums.txt`, caches
it locally, and execs it with inherited stdio, forwarding the exit code.
Later invocations of the same version run entirely from the local cache.

`.github/workflows/publish.yml` builds these binaries (`linux-x64`,
`linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`) and creates the matching
`vX.Y.Z` GitHub Release only on a real, stable version bump — the same gate
that already controls the npm `latest` dist-tag publish — so a
`@main`-tagged prerelease snapshot never gets its own release; the launcher
reports that plainly rather than silently failing or guessing.

This choice follows directly from `EV-ROS-2026-A048`'s evidence:

- **Framework-dependent publish is impractical for npm consumers.** It
  requires the caller to already have the target .NET runtime installed;
  for a brand-new major runtime version, essentially no Node.js developer
  will have it, making it a much heavier ask than what this tool replaces
  (a pure-Node bootstrap with zero extra runtime dependency).
- **Self-contained single-file publish has no measured startup cost** over
  framework-dependent (both ~55-80ms for `--version` on `linux-x64`) and
  works uniformly across all five target platforms via `dotnet publish`'s
  cross-publish support, confirmed by actually building all five from one
  machine.
- **The binaries (77-85MB each) cannot be bundled in the npm tarball
  itself** (currently 0.74MB unpacked; bundling even one platform would be a
  ~100x regression to every install). Per-platform `optionalDependencies`
  npm packages (the pattern used by esbuild/swc/similar tools) were
  considered and rejected for a narrower, practical reason: each such
  package needs its own npm trusted-publisher (OIDC) configuration on
  npmjs.com, an administrative step outside what this repository or session
  can complete. Fetching from a GitHub Release attached to the existing,
  already-trusted package's own version tag needs no new npm package name
  or publisher configuration.

# Alternatives

- **Framework-dependent publish, require consumers to install .NET 10
  first:** rejected. `EV-ROS-2026-A048` shows this trades a pure-Node
  zero-extra-runtime tool for one that needs a large, unfamiliar,
  brand-new runtime pre-installed — a real UX regression with no offsetting
  benefit once self-contained publish measures the same startup cost.
- **Bundle all five platform binaries directly in the npm tarball:**
  rejected outright on size grounds (~350-400MB added to every install
  regardless of platform).
- **Per-platform `optionalDependencies` npm packages:** not rejected on
  technical merit — it is the standard pattern for this exact problem and
  avoids the network-at-first-run trade-off below — but rejected for this
  round because it requires new npm package names with their own trusted
  publishing configured on npmjs.com by a human with registry-owner access,
  which is out of scope for this decision to complete. Left as a future,
  strictly better replacement for this same launcher entrypoint if that
  administrative step is ever done; `bin/ros-fs.mjs`'s public surface
  (the `ros-fs` command) would not need to change for consumers.
- **Do nothing until Phase C is also ready:** rejected. The user's request
  is specifically for npm-downloadable-and-usable now, and this decision's
  scope (an additive, optional binary) does not require Phase C's
  preconditions — it never touches `./ros`'s own dispatch.

# Consequences

Any project can now run `npx --package=@echelon-foundry/repository-operating-system@<version> ros-fs <command>`
and get a real, working F# CLI binary, for stable releases going forward.
`./ros` and `ros-bootstrap init` are completely unchanged; Node remains the
only thing either of those invoke. `DF-ROS-2026-A028`'s Phase C — actually
redirecting `./ros`'s own dispatch — remains open, ungated by this record,
and still requires its own future decision.

The `publish.yml` workflow gains `contents: write` (previously
`contents: read`) to create GitHub Releases via the built-in `gh` CLI and
`GITHUB_TOKEN`; this is scoped to the same job that already holds
`id-token: write` for npm's OIDC trusted publishing, and only exercised on
a real stable-version-bump push, the rarest and most deliberate trigger in
that workflow.

The first `ros-fs` invocation for a new (version, platform) pair requires
network access; this is a real, documented trade-off (see
`EV-ROS-2026-A048`'s Offline finding) against today's fully offline
`node_modules`-only install, and is called out plainly in
`PACKAGE-USAGE.md` rather than left implicit.

# Reversibility and validation

Fully reversible: removing the `ros-fs` bin entry, `bin/ros-fs.mjs`,
`lib/ros-fs-launcher.mjs`, and the release-building steps in `publish.yml`
returns the package to exactly its pre-existing shape, since nothing else
depends on `ros-fs` existing. No canonical data format, schema, or file
this repository's own `./ros` reads or writes is touched. Validation is
`tests/ros-fs-launcher.test.mjs` (download, cache-hit, checksum-mismatch,
and missing-checksum-entry paths, all against a real local HTTP server and,
manually, a real self-contained binary), plus `./ros validate` and
`./ros registry check` passing unchanged.
