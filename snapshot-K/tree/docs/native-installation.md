# Native Praxis installation

Praxis is the product name for the repository operating system previously exposed as ROS. Native installation does not require npm, Node.js, or a machine-wide .NET runtime.

Each stable release contains a self-contained F# executable plus the exact versioned package payload needed by lifecycle commands such as init, verify, doctor, and upgrade. The native wrapper supplies that payload to the executable explicitly. Existing repository-local ROS installations remain compatible.

## Install the complete Echelon toolchain

macOS or Linux:

    curl -fsSL https://raw.githubusercontent.com/kemiller2002/praxis/main/scripts/install-toolchain.sh | sh

Windows PowerShell:

    irm https://raw.githubusercontent.com/kemiller2002/praxis/main/scripts/install-toolchain.ps1 | iex

That installs Praxis, Ordo, their compatibility aliases, and the reusable `echelon` command.

## Install Praxis only

macOS or Linux:

    curl -fsSL https://raw.githubusercontent.com/kemiller2002/praxis/main/scripts/install-native.sh | sh

Windows PowerShell:

    irm https://raw.githubusercontent.com/kemiller2002/praxis/main/scripts/install-native.ps1 | iex

The installer creates these command names:

    praxis
    ros
    echelon

praxis is the preferred product-facing command. ros remains a compatibility alias.

## Using the Echelon bootstrap after installation

After Praxis is installed:

    echelon setup

This installs or upgrades both Ordo and Praxis. If the current repository contains .echelon/toolchain.json, setup uses the versions pinned there. Otherwise it uses the latest stable GitHub Releases.

Other commands:

    echelon install ordo
    echelon install praxis
    echelon upgrade
    echelon doctor

## Doctor

`echelon doctor` is the first diagnostic command to run when the Echelon environment or a repository looks wrong.

It reports:

- platform, architecture, Echelon home, and whether the Echelon bin directory is on PATH;
- active Ordo and Praxis versions plus every side-by-side installed version;
- health of the `ordo`, `sde`, `praxis`, `ros`, and `echelon` command entry points;
- additional tools discovered under the Echelon tools directory;
- the current repository's `.echelon/toolchain.json` pins and whether active versions satisfy them;
- Ordo verification when the repository contains `.sde/`;
- Praxis validation when the repository contains `.ros/`.

Useful forms:

    echelon doctor
    echelon doctor --verbose
    echelon doctor --fix
    echelon doctor --json
    echelon doctor --updates
    echelon doctor --json --updates
    echelon inventory
    echelon inventory --json

`--fix` is intentionally conservative. It creates missing Echelon directories and reinstalls/reactivates an exact pinned version, or the already-active version when a command wrapper is missing. It does not edit repository-managed Ordo/Praxis state and it does not silently modify shell startup files to change PATH.

Warnings such as a missing PATH entry do not make the command fail. Broken command entry points, stale command aliases, missing active tools, manifest mismatches, or failed repository validation return exit code 1.

Doctor also inventories repository lifecycle manifests under `.echelon/` and physically installed `node_modules/@echelon-foundry/*` packages. The JSON forms are stable agent-facing contracts documented in [echelon-doctor.md](echelon-doctor.md).

## Layout

On macOS and Linux:

    ~/.echelon/
    ├── bin/
    │   ├── echelon
    │   ├── ordo
    │   ├── sde
    │   ├── praxis
    │   └── ros
    └── tools/
        ├── ordo/<version>/
        └── praxis/<version>/

Versions are immutable directories. The bin entries point at the active version, so upgrading a tool does not rewrite an older release.

## Compatibility

npm is no longer a distribution channel (`DF-ROS-2026-A044`); versions already published there stay installable but receive no updates. With .NET 10 installed, `dotnet tool install -g EchelonFoundry.Praxis` provides the same `praxis` command from NuGet. Existing ros and sde command names and repository installation manifests are not removed.

## Verified bootstrap for CI and remote execution

A machine that must run the *exact* Praxis version a repository pins can use
[`scripts/praxis-bootstrap.sh`](../scripts/praxis-bootstrap.sh). Examples are
a GitHub Actions runner executing a remote request (see
[remote-protocol.md](remote-protocol.md)) or any other CI job. In GitHub
Actions, use the composite action `.github/actions/praxis-setup`.

The script works in these steps:

1. It reads the `praxis` version from `.echelon/toolchain.json`. The
   manifest must be valid JSON with `schemaVersion: 1` and an exact
   `MAJOR.MINOR.PATCH` version.
2. It downloads exactly that release's native bundle and
   `native-checksums.txt`, then verifies the bundle's SHA-256.
3. It verifies the bundle's GitHub build-provenance attestation with
   `gh attestation verify`. Releases published before attestations existed
   need `--attestation skip`. That choice must be made explicitly, and it is
   reported.
4. It checks that the unpacked binary reports the pinned version.
5. It caches the verified bundle by version and digest. Every cache hit is
   re-verified, and a cache entry that was populated without attestation
   never satisfies a run that requires attestation.

The script never falls forward to another version and never builds Praxis
from source. Each failure has its own exit code:

| Exit code | Meaning |
|---|---|
| 3 | The manifest is missing, malformed, or does not pin an exact version. |
| 4 | The release or asset is unavailable. |
| 5 | Integrity or attestation verification failed. |
| 6 | Version mismatch. |
| 7 | Unsupported platform. |

**Immutable releases.** A published version's native assets are never
replaced. When a release workflow runs again without a version bump, it
publishes nothing and leaves the existing assets in place. `echelon install
praxis VERSION` also fails when the pinned version's installer cannot be
downloaded, instead of silently doing nothing.
