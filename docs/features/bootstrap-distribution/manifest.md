# Feature Manifest — Bootstrap and distribution

## Purpose

Route changes to npm acquisition, profile materialization, installation
attribution, and installation-integrity verification.

## Ownership

- State, including presentation state: `starter/*/manifest.json`, `package.json`,
  package contents, and installed `.ros/installation.json`.
- Transitions / commands / messages: `bin/ros-bootstrap.mjs` delegates to
  `lib/bootstrap.mjs` symbols `initializeProject`, `verifyProject`, and `main`.
- Invariants and guards: safe destination checks, collision preflight,
  preserve-existing policy, profile/manifest validation, checksums, file modes,
  and verification findings in `lib/bootstrap.mjs`.
- Capabilities / authority: caller selects profile/target/force; manifest is
  authoritative for declared package materialization.
- Important effects and effect contracts: filesystem creation/copy/render,
  cleanup of newly written declared files after failure, and no network/Git
  semantics inside the bootstrap implementation.

## Interfaces

- Inbound: npm `ros-bootstrap` binary and `init`/`verify` CLI arguments.
- Outbound: installed files/directories/modes, installation attribution JSON,
  stdout/stderr, and process exit status.

## Tests and verification

- Local behavior tests: `tests/npm-bootstrap.test.mjs`.
- Boundary/contract tests: both profile manifests, npm pack inventory, checksum
  verification, and collision/rollback assertions.
- Integration/live verification: clean temporary installation followed by the
  installed `./ros registry check` and `./ros validate` commands.

## Dependencies

- Allowed direct dependencies: Node filesystem/path/crypto, package metadata,
  profile manifests, and source templates.
- Required composition context: npm package layout and a writable target.

## Modification boundaries

- Normal: `bin/`, `lib/`, `starter/`, package metadata, and bootstrap tests.
- Escalation required: changes to installed contracts, overwrite policy,
  acquisition/runtime support, or profile composition.

## Local agent instructions

- `AGENTS.md`, `PACKAGE-USAGE.md`, and `BOOTSTRAP.md`.

## Maintenance

- Owner: repository-governance
- Last checked against implementation: 2026-09-07
- Known gaps: initialization writes related state without a multi-file
  transaction; the root lockfile version is stale relative to `package.json`;
  bootstrap prints validation as a next step but does not execute it.
