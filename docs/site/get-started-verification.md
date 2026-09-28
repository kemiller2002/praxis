# Get-started verification (PRAXIS-SITE-15)

The public site's get-started section (`site/index.html#get-started`) names
only commands that were run for real before publication. This file records
that run. It is evidence for PRAXIS-SITE-15, not a support matrix.

Date: 2026-09-27. Platform: Linux x64 (the build container). Other platforms
were **not** tested; the page says so.

## Release

- Latest release: `v3.4.0` ("Praxis v3.4.0"), published 2026-09-23, with
  native assets `praxis-{linux-x64,linux-arm64,linux-musl-x64,osx-x64,osx-arm64}.tar.gz`,
  `praxis-win-x64.zip` and `native-checksums.txt`.
- `praxis-linux-x64.tar.gz` SHA-256 `2a125dd91cbba2a3c22a23bcbf602bf5889508484ffbb09e12e6c1f5bf82cac7`, identical to the
  entry in `native-checksums.txt`.
- The bundle contains `praxis`, `praxis-bin`, `echelon`, `VERSION` and `package/`.

## Installer

`scripts/install-native.sh` was run with `ECHELON_HOME` pointing at a scratch
directory. It printed `Installed Praxis 3.4.0` and created three commands:
`praxis`, `ros` and `echelon`. It verifies the downloaded asset against
`native-checksums.txt` before installing (lines 67-78); the PowerShell
installer does the same.

## Commands, in a fresh `git init` repository

| Command | Result |
| --- | --- |
| `praxis init` | `applied 121 change(s)`, manifest `.echelon/ros.json` |
| `praxis status` | exit 0, JSON status |
| `praxis verify` | `verification passed: @echelon-foundry/repository-operating-system@3.4.0` |
| `praxis validate` | `validation passed` |
| `praxis work begin --id DEMO-1 ...` | work started, execution record created |
| `ros --version` | `ros-fs 3.4.0` (same program as `praxis`) |
| `echelon doctor` | ran; reported the scratch environment needs attention (expected: not on PATH) |

## Naming transition, as observed

- `praxis --version` prints `ros-fs 3.4.0`; `praxis --help` begins `ros -- Repository Operating System lifecycle CLI`.
- `praxis init` installs a repository-local `./ros` launcher.
- The npm package `@echelon-foundry/repository-operating-system` declares bins `ros`, `ros-bootstrap` and `ros-fs`; it has no `praxis` bin.

The site states all of this under "From ROS to Praxis" instead of implying the
rename is finished.
