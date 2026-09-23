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

The existing npm package remains supported as a compatibility distribution channel. Existing ros and sde command names and repository installation manifests are not removed by this change.
