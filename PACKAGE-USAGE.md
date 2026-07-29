# Portable ROS package

The package ships a frozen, self-contained Repository Operating System
greenfield profile. A target repository does not read from or link to this
source checkout after installation.

## Install from npm

After publishing version `1.0.0`:

```bash
cd /path/to/project
npx --yes @kemiller2002/repository-operating-system@1.0.0 init \
  --project "Communication Engineering" \
  --target .
```

Pin the version for reproducible initialization. Avoid an unversioned
`@latest` command in automated project creation.

## Install directly from GitHub

Before an npm release, or when testing a repository tag:

```bash
cd /path/to/project
npx --yes github:kemiller2002/Repository-Operating-System#v1.0.0 init \
  --project "Communication Engineering" \
  --target .
```

For an unpublished development commit, replace `v1.0.0` with an exact commit
SHA. A branch name is convenient but not reproducible.

## Preview and safety

```bash
npx --yes github:kemiller2002/Repository-Operating-System#<commit> init \
  --project "Communication Engineering" \
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

To verify that the installed operating-system snapshot has not drifted, invoke
the same package version:

```bash
npx --yes @kemiller2002/repository-operating-system@1.0.0 verify --target .
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
