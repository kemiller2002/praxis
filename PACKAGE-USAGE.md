# Portable ROS package

The package ships a frozen, self-contained Repository Operating System
greenfield profile. A target repository does not read from or link to this
source checkout after installation.

## Install from npm

After a release is published under the organization scope:

```bash
cd /path/to/project
npx --yes \
  --package=@echelon-foundry/repository-operating-system@<version> \
  ros-bootstrap init \
  --target .
```

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

Initialization also installs `.github/workflows/ros-validation.yml`. It runs the self-contained, pinned snapshot from the repository and supplies the pull-request or push base revision for committed-change attribution. No network call to the ROS source repository occurs during validation.

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

## Upgrade procedure

ROS upgrades are explicit and collision-safe:

1. Pin the desired release tag or commit and run `ros-bootstrap init --dry-run` against a clean temporary repository to inspect the new snapshot.
2. Read the release migration report and compare its protocol version with `ros.json`.
3. In the consuming repository, establish an attributed upgrade work item and preserve project-owned configuration.
4. Replace only files recorded as managed in `.ros/installation.json`; resolve differing managed files as an explicit migration rather than using a force flag.
5. Update `.ros/installation.json`, run `ros-bootstrap verify`, `./ros registry check`, and `./ros validate`, then review the diff before commit.

Minor releases may add compatible commands, evidence types, or schema fields. Breaking transition, event, or configuration semantics require a new protocol major version and a repository migration. The initializer intentionally does not perform silent in-place upgrades.

## Publication gate

Before tagging or publishing:

```bash
npm run release:check
npm publish --dry-run --access public
```

Inspect the tarball file list, confirm the version, create an immutable Git tag,
select and record an explicit package license, and publish with the npm account
authorized for the `@echelon-foundry` organization scope. The package remains `UNLICENSED`
until that decision is made.

The package metadata fixes the publication registry to `https://registry.npmjs.org/`, declares public access, and links releases to the current GitHub source repository. Before the first publish, `npm whoami` must succeed and the authenticated user must have write permission in the `echelon-foundry` npm organization.
