---
id: DF-ROS-2026-A003
title: Portable greenfield bootstrap package
status: review
version: 1.1.0
author_agent: openai-codex
created: 2026-07-29
updated: 2026-08-04
related_documents: []
supersedes: []
superseded_by: []
tags: [architecture, distribution, bootstrap, npm]
---

# Portable greenfield bootstrap package

## Context

Beginning projects need to adopt ROS without requiring an agent to read or copy
files from another local repository. The first intended consumer is
Communication Engineering. The existing Python CLI validates an installed ROS
repository but does not distribute or initialize one.

## Decision

Package ROS as a zero-runtime-dependency Node CLI named
`@kemiller2002/repository-operating-system`. The package:

- embeds a frozen greenfield scaffold and its source manifest;
- supports npm and GitHub tarball installation through `npx`/`npm exec`;
- installs additively and rejects every destination collision before writing;
- supports a no-write `--dry-run`;
- derives the project display name from the target directory when `--project`
  is omitted, while retaining an explicit override;
- records package version and installed-file checksums in
  `.ros/installation.json`;
- leaves validation and registry generation inside the initialized repository
  through the dependency-free Node `./ros` CLI; and
- requires explicit migration for an existing managed file rather than
  providing a destructive force option.

This record remains in review until the distribution name, package license,
and first real consumer installation are accepted.

## Alternatives considered

### Agent-mediated copy

Rejected because it depends on source-repository access, is difficult to
reproduce, and obscures the installed version.

### Git submodule

Rejected for the greenfield default because it couples consumers to the source
layout and complicates project-owned evolution.

### Runtime download from the ROS repository

Rejected because installation would depend on a second network operation and a
mutable remote file layout. The npm/GitHub package itself must contain all
scaffold inputs.

### Overwriting installer

Rejected for v1.0 because silent merge or replacement risks destroying
project-owned records. Future upgrades require an explicit migration design.

## Consequences

- Consumers can pin an npm version, Git tag, or commit SHA.
- Consumers may instead track the latest `main` branch when freshness is an
  explicit preference over reproducibility.
- Package size includes governance, templates, and the Node validator,
  intentionally trading size for independence.
- Embedded policy is a versioned snapshot; updates do not silently alter an
  initialized project.
- Snapshot verification reports drift but does not forbid governed project
  changes.
- npm publication remains blocked until the package name/ownership and license
  are confirmed.

## Validation

The release gate includes source initialization, dry-run, collision atomicity,
drift detection, tarball completeness, execution from the generated tarball,
local registry freshness, local artifact validation, and the existing ROS test
suite.

## Reversibility

Before publication, the package layout and name are reversible. After a public
release, versions are immutable and changes require a new semantic version.

## Follow-up

1. Confirm package scope ownership and select a license.
2. Record the completed Communication Engineering consumer result and any
   migration requirements.
3. Accept, revise, or reject this decision.
