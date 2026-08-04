# Portable ROS package

The package ships a frozen, self-contained Repository Operating System
greenfield profile. A target repository does not read from or link to this
source checkout after installation.

## Install the latest directly from GitHub

Run inside the target repository:

```bash
cd /path/to/project
npx --yes --prefer-online \
  --package=github:kemiller2002/repository-operating-system#main \
  ros-bootstrap init \
  --target .
```

The project display name is derived from the target folder. Use
`--project "Different Display Name"` to override it. `--prefer-online` directs
npm to check the remote instead of preferring cached package data.

## Reproducible GitHub installation

Before an npm release, or when testing a repository tag:

```bash
cd /path/to/project
npx --yes github:kemiller2002/repository-operating-system#<tag> init \
  --target .
```

For an unpublished development commit, replace `v1.0.0` with an exact commit
SHA. A branch name is convenient but not reproducible.

## Preview and safety

```bash
npx --yes github:kemiller2002/repository-operating-system#<commit> init \
  --target . \
  --dry-run
```

Initialization is additive. Existing project-owned `README.md`,
`.gitattributes`, `.gitignore`, and `.editorconfig` files are preserved and
recorded as unmanaged in the installation manifest. An identical managed file
is adopted. Any differing managed destination stops the command before it
writes files. Version 1.0.0 intentionally has no force or merge flag; managed
collisions require an explicit migration.

## Validate the installed project

```bash
./ros registry check
./ros validate
```

To verify an installed snapshot against its recorded checksums, invoke the
current GitHub package:

```bash
npx --yes --prefer-online \
  --package=github:kemiller2002/repository-operating-system#main \
  ros-bootstrap verify \
  --target .
```

Project-owned files will legitimately change during use. Snapshot verification
is a diagnostic, not a rule forbidding project evolution; accepted changes
should be recorded through normal project governance.

## Publication gate

Before tagging or publishing:

```bash
npm test
npm run pack:inspect
```

Inspect the tarball file list, confirm the version, create an immutable Git tag,
select and record an explicit package license, and publish with the npm account
authorized for the `@kemiller2002` scope. The package remains `UNLICENSED`
until that decision is made.
