# Native Praxis installation

Praxis is the product name for the repository operating system previously exposed as ROS. Native installation does not require npm, Node.js, or a machine-wide .NET runtime.

Each stable release contains a self-contained F# executable plus the exact versioned package payload needed by lifecycle commands such as init, verify, doctor, and upgrade. The native wrapper supplies that payload to the executable explicitly. Existing repository-local ROS installations remain compatible.

## Install Praxis

macOS or Linux:

    curl -fsSL https://raw.githubusercontent.com/kemiller2002/praxis/main/scripts/install-native.sh | sh

Windows PowerShell:

    irm https://raw.githubusercontent.com/kemiller2002/praxis/main/scripts/install-native.ps1 | iex

The installer creates these command names:

    praxis
    ros
    echelon

praxis is the preferred product-facing command. ros remains a compatibility alias.

## Install the whole Echelon engineering toolchain

After Praxis is installed:

    echelon setup

This installs or upgrades both Ordo and Praxis. If the current repository contains .echelon/toolchain.json, setup uses the versions pinned there. Otherwise it uses the latest stable GitHub Releases.

Other commands:

    echelon install ordo
    echelon install praxis
    echelon upgrade
    echelon doctor

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
