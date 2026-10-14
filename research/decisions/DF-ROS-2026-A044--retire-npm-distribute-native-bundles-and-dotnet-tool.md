---
id: DF-ROS-2026-A044
title: Retire npm distribution; distribute Praxis as self-contained native bundles and a .NET global tool
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-29
updated: 2026-09-29
research_area: repository-operating-system
decision_type: architecture
supports: []
supporting_evidence: []
related_documents:
  - DF-ROS-2026-A029
  - DF-ROS-2026-A034
  - DF-ROS-2026-A041
  - PACKAGE-USAGE.md
  - docs/installation.md
  - docs/native-installation.md
  - .github/workflows/native-release.yml
  - .github/workflows/ros-fs-assets.yml
  - .github/workflows/release.yml
supersedes: [DF-ROS-2026-A034]
superseded_by: []
tags: [distribution, release, npm, nuget, dotnet-tool, native]
confidence: high
provenance:
  contributions:
    EXE-20260929T163720115Z-f381906c:
      operations: [created]
      at: 2026-09-29T16:37:21.814Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Owner decision to retire npm distribution; native bundles plus .NET global tool (PRAXIS-DIST-NATIVE-DOTNET-TOOL; re-landed from PR #108)"
---

# Context

Praxis is entirely F#, yet it was distributed through npm: a Node launcher
package (`@echelon-foundry/repository-operating-system`) that downloaded a
self-contained `ros-fs` binary from GitHub Releases (`DF-ROS-2026-A029`).
`publish.yml` published the npm package and was also the only workflow that
built those `ros-fs-<rid>` binaries. Separately, `native-release.yml` has
published self-contained native bundles since PRAXIS-REMOTE-05, and cloud
agents install through them (`scripts/praxis-bootstrap.sh`,
`DF-ROS-2026-A041`).

npm publishing had been switched off (`vars.NPM_PUBLISH_ENABLED`) since
before v3.3.0. As a result, v3.3.0 through v3.6.0 have native bundles but no
`ros-fs-<rid>` binaries or `checksums.txt`, so every project scaffolded at
one of those versions has a `./ros` that fails with a 404 on first use.

On 2026-09-29 the repository owner decided not to publish to npm any more.

# Decision

1. **npm is retired as a distribution channel.** `publish.yml` is removed,
   `package.json` is `private` with no `publishConfig`, and documentation
   no longer tells anyone to use `npx` or `npm install`. `package.json`
   remains the single version source and its `files` list still defines the
   payload the native bundle carries (assembled with `npm pack
   --ignore-scripts`, never published).
2. **Native bundles stay the no-runtime channel.** A build that needs no
   installed runtime must embed the .NET runtime, which is native code per
   operating system and CPU, so each release keeps one self-contained build
   per platform. Machines, CI and cloud agents without .NET use these.
3. **A .NET global tool is added** for machines with .NET 10:
   `EchelonFoundry.Praxis` on NuGet, command `praxis`. It is one portable
   package; the scaffold is embedded in the assembly, so it needs no package
   root. `native-release.yml` packs and smoke-tests it on every run and pushes
   it with NuGet trusted publishing (OIDC, no stored key) when
   `vars.NUGET_PUBLISH_ENABLED` is `true`.
4. **The `ros-fs-<rid>` binaries move out of the npm workflow.**
   `ros-fs-assets.yml` builds, attests and uploads them plus `checksums.txt`
   for a tag; `native-release.yml` calls it for every release, and it can be
   dispatched to backfill v3.3.0 through v3.6.0. It never replaces an
   existing asset. Existing scaffolded projects keep working unchanged.
5. **No snapshot releases.** The npm `main` dist-tag snapshots and the
   `lib/stable-ros-version.json` pin they needed are retired, superseding
   `DF-ROS-2026-A034`. The remaining reader code stays for installs of
   already-published snapshots.
6. `DF-ROS-2026-A029` remains in force for the launcher-fetches-a-binary
   design and its asset names, but no longer for the npm channel that
   carried the launcher.

# Consequences

- The installation identity recorded in `.echelon/ros.json` stays
  `@echelon-foundry/repository-operating-system` for compatibility. It is the
  product's identity, not a claim that npm is used.
- Versions already on npm stay installable and receive no updates. The owner
  may mark them deprecated with `npm deprecate`, which needs npm credentials.
- NuGet publishing needs a one-time nuget.org trusted-publishing policy for
  `native-release.yml` and the `NUGET_USER` and `NUGET_PUBLISH_ENABLED`
  variables.
- The legacy Node `ros-bootstrap` scaffolder is no longer distributed. Its
  code stays in the repository as the internal library `DF-ROS-2026-A033`
  describes.

# Alternatives considered

- **Keep npm alongside the new channels.** Rejected by the owner: an
  F#-only product does not need a JavaScript package registry, and a channel
  nobody publishes to decays silently, as v3.3.0 to v3.6.0 showed.
- **.NET tool only.** Rejected: it requires .NET 10 everywhere and breaks the
  guarantee that cloud agents and plain machines need no runtime
  (`DF-ROS-2026-A041`).
- **Native bundles only.** Viable, but developers who already have .NET gain
  a single cross-platform package and `dotnet tool update` from the tool.
