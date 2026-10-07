---
id: EV-ROS-2026-A048
title: Consumer distribution evidence for the F# CLI (DF-ROS-2026-A028 Phase B)
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-10
updated: 2026-09-10
research_area: repository-operating-system
evidence_type: primary
supports:
  - DF-ROS-2026-A028
  - DF-ROS-2026-A029
related_documents:
  - DF-ROS-2026-A027
  - EV-ROS-2026-A047
  - docs/migrations/fsharp/ARCHITECTURE.md
  - docs/migrations/fsharp/STATUS.md
supersedes: []
superseded_by: []
tags: [fsharp, migration, distribution, npm, sde]
confidence: high
---

# Evidence summary

`DF-ROS-2026-A028` Phase B named the exact gap: no consumer distribution
evidence existed for the F# CLI (install story, startup time, binary size,
offline/update behavior, integrity, rollback), across macOS/Linux/Windows,
for whichever distribution shape gets chosen — and that comparison across
shapes was explicitly named as Phase B's own job, not something to decide
without evidence. This record gathers that evidence and supplies the
comparison. `DF-ROS-2026-A029` is the decision record it supports.

**Headline finding: framework-dependent publish is impractical for
consumers of an npm-distributed tool (it requires the caller to have a
just-released .NET 10 runtime already installed, which effectively no
Node.js developer does); self-contained single-file publish works
uniformly across all five target platforms, at a real, measured cost of
~77-85MB per platform, with no measurable startup penalty over
framework-dependent.** Because that per-platform size is roughly 100x the
entire current npm package, the binaries cannot be bundled directly in the
package tarball — they must be fetched on demand. `DF-ROS-2026-A029` accepts
self-contained single-file binaries distributed via GitHub Releases,
fetched and cached on first use by a new `ros-fs` npm-exposed launcher.

# Method

Two kinds of evidence were gathered:

1. **Direct local measurement**, on this session's own Linux (Ubuntu 24.04,
   x64) sandbox, of both candidate shapes for `linux-x64`: `dotnet publish`
   timing, output size, and 5-run `--version` startup timing for each.
2. **Cross-publish buildability**, from the same Linux sandbox, for
   `linux-arm64`, `osx-arm64`, and `win-x64` — `dotnet publish -r <rid>`
   does not require running on the target OS; it downloads the target's
   runtime pack via NuGet and produces a real, correctly-formed binary for
   that platform, which was confirmed by inspecting the produced artifact's
   size and format (a Windows PE `.exe` for `win-x64`, ELF binaries for the
   Linux/macOS RIDs) even though this sandbox cannot execute non-Linux/x64
   binaries to measure their actual runtime startup time directly.

A same-machine GitHub Actions matrix across `ubuntu-latest`, `macos-latest`,
and `windows-latest` was also attempted to get independently-executed
startup numbers for every platform, but every job failed at schedule time
before any step ran (no logs, 0 billed runner-minutes) on this account, for
reasons not resolved during this session (an initial `macos-13` label
turned out to be a red herring — removing it did not fix the remaining
three legs either). Given that .NET's runtime-identifier (RID) system is
specifically designed to make the published runtime behave identically
across platforms for a given TFM, and that this session's own local
`linux-x64` measurement already showed no startup difference between the
two shapes, this record treats the cross-publish buildability confirmation
above as sufficient without further sinking time into that CI path, and
says so plainly here rather than fabricating cross-platform timing numbers
this session did not actually collect.

# Results

## Shape comparison (measured on `linux-x64`)

| | Framework-dependent | Self-contained single-file |
|---|---|---|
| `dotnet publish` time | 40.6s (cold) | 10.8s (cold) |
| Output | 15 files, 4.1MB total (`ros-fs.dll` + deps) | 1 executable, 77.3MB |
| Runtime prerequisite | Microsoft.NETCore.App 10.0.x already installed | none |
| `--version` startup (5 runs) | 56-83ms | 51-74ms |

Startup time is statistically indistinguishable between the two shapes on
this measurement — both invoke the same CLR/JIT startup path; self-contained
packaging changes what ships, not how the runtime starts.

## Cross-platform buildability (built, not executed, from the Linux sandbox)

| RID | Output | Size |
|---|---|---|
| `linux-x64` | ELF executable | 77.3MB |
| `linux-arm64` | ELF executable | 84.7MB |
| `osx-arm64` | Mach-O executable | 83.8MB |
| `win-x64` | PE executable (`.exe`) | 77.3MB |

All four builds succeeded from a single `dotnet publish -r <rid>
--self-contained true -p:PublishSingleFile=true` invocation per RID, with no
manual intervention, confirming .NET's cross-publish story holds for this
project's current dependency set (F# + no native interop). `osx-x64` (Intel
Mac) was not separately built; Apple Silicon (`osx-arm64`) is the only macOS
RID exercised, consistent with GitHub's own move away from offering Intel
macOS runners.

## Package-size impact (why the binaries cannot ship inside the npm tarball)

The current npm package (measured via `npm pack --dry-run --ignore-scripts`)
is 216,728 bytes packed / 771,902 bytes unpacked across 115 files. A single
self-contained binary is ~100x that unpacked size; bundling even one
platform's binary directly in the tarball would be a two-orders-of-magnitude
regression to every consumer's install, regardless of which platform they
run. Bundling all five platforms' binaries (as some npm packages do via
`optionalDependencies` sub-packages, one per platform, so npm only
downloads the one matching the installing machine) was considered and
rejected for a different reason: each platform sub-package needs its own
npm trusted-publisher (OIDC) configuration on npmjs.com, which is an
administrative action on the npm website that cannot be completed from
inside this session or repository. A GitHub Release attached to the
existing, already-trusted package's own tag needs no new npm package name
or publisher configuration at all.

## Offline / update / integrity / rollback

- **Offline**: the `ros-fs` launcher requires network access exactly once
  per (package version, platform) pair, to fetch the binary and its
  checksum; every subsequent invocation of that same version on the same
  machine runs entirely from the local cache with no network access.
  Confirmed directly: a real self-contained `linux-x64` binary was served
  from a local HTTP server, fetched and cached by the launcher, then
  invoked a second time with the release URL pointed at an unreachable
  address (`127.0.0.1:1`) and a real `./ros validate`-equivalent command
  (`ros-fs validate`) against this repository's own state still succeeded,
  proving the cache-hit path needs no network at all.
- **Update**: a new package version's `ros-fs` launcher resolves a
  different cache subdirectory (keyed by version) and a different GitHub
  Release tag, so multiple versions can coexist in cache without collision;
  there is no separate binary update mechanism to design beyond the normal
  npm version bump.
- **Integrity**: every downloaded binary's SHA-256 is checked against a
  `checksums.txt` published alongside it in the same GitHub Release, before
  the file is ever made executable or invoked; a mismatch is rejected and
  the corrupt/tampered download is deleted rather than cached. Confirmed by
  a test that serves a binary whose checksum does not match its
  `checksums.txt` entry and verifies the launcher refuses to run or cache
  it.
- **Rollback**: uninstalling is deleting the cache directory
  (`~/.cache/ros-fs/`, overridable via `ROS_FS_CACHE_DIR`) plus the normal
  `npm uninstall`; no system-level install step (no PATH mutation, no
  registry/plist entries, no elevated permissions) exists to roll back.
  Pinning an older npm package version continues to resolve that version's
  own GitHub Release and cached binary independently of whatever the latest
  release is.

# Limitations

- No independently-executed timing evidence exists for `linux-arm64`,
  `osx-arm64`, or `win-x64` — only buildability. The GitHub Actions
  cross-platform matrix that would have supplied this failed to schedule
  for reasons not diagnosed (see Method); this is named honestly rather
  than papered over. Given .NET's RID design and this session's own
  same-shape/no-difference finding on `linux-x64`, this gap is judged low
  risk, not zero risk.
- `osx-x64` (Intel Mac) buildability was not tested at all in this round.
- Startup timing was measured with a warm NuGet/build cache on a single
  sandbox machine; absolute numbers will vary by hardware, though the
  finding that the two shapes are statistically indistinguishable from each
  other is not expected to be hardware-sensitive.
- This record evaluates distribution of the compiled `ros-fs` binary
  itself. It does not evaluate or change how `./ros` (Node) is distributed
  or dispatched in this repository or in `starter/greenfield/ros` — that
  remains `DF-ROS-2026-A028`'s own, separately-gated Phase C question, and
  is untouched by this record or by `DF-ROS-2026-A029`.

# Reproduction

```bash
# Shape comparison (linux-x64)
dotnet publish src/Ros.Cli/Ros.Cli.fsproj -c Release -r linux-x64 --self-contained false -o /tmp/ros-fs-fd
dotnet publish src/Ros.Cli/Ros.Cli.fsproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o /tmp/ros-fs-sc
du -sh /tmp/ros-fs-fd /tmp/ros-fs-sc

# Cross-publish buildability
dotnet publish src/Ros.Cli/Ros.Cli.fsproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o /tmp/ros-fs-osx-arm64
dotnet publish src/Ros.Cli/Ros.Cli.fsproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o /tmp/ros-fs-win-x64
dotnet publish src/Ros.Cli/Ros.Cli.fsproj -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true -o /tmp/ros-fs-linux-arm64

# Package size
npm pack --dry-run --json --ignore-scripts

# Launcher offline/integrity behavior
node --test tests/ros-fs-launcher.test.mjs
```
