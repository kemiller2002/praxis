# Research Operating System

**ROS Version:** 1.0.0  
**REP Specification:** 2.0

This repository is a shared operating environment for autonomous research
and engineering agents.

## Canonical source hierarchy

1. Scientific Research Journals
2. Research Execution Packages
3. Theory Registry
4. Evidence Registry

Reports, websites, presentations, and training materials are derived from
canonical research artifacts. Generated products must not silently replace
or modify canonical records.

## Agent entry point

Every agent starts with [`BOOTSTRAP.md`](BOOTSTRAP.md).

## Executable contract

Canonical knowledge is stored in Markdown artifacts. JSON registries are
generated views.

```bash
./ros registry build
./ros registry build --dry-run
./ros registry check
./ros validate
python3 -m unittest discover -s tests -v
```

The CLI uses only the Python standard library. Validation returns a nonzero
exit code for malformed front matter, invalid or duplicate IDs, broken
references, invalid lifecycle values, nonreciprocal supersession, filename/ID
mismatches, and stale registries.

New agents should read the lifecycle, supersession, identifier, confidence,
artifact-tier, and taxonomy documents under `framework/` before creating
canonical records.

## Portable greenfield installation

ROS can be loaded into a separate beginning project through its self-contained
npm package. The package embeds the governance, schemas, templates, validator,
empty registries, and greenfield pilot records; the initialized repository does
not read this source checkout.

```bash
npx --yes --prefer-online \
  --package=github:kemiller2002/repository-operating-system#main \
  ros-bootstrap init \
  --target .
```

The project display name is derived from the target folder. Pass
`--project "Different Display Name"` only when an override is needed. This
command checks the remote and installs the latest `main`; use a tag or commit
SHA when reproducibility is more important than freshness. See
[`PACKAGE-USAGE.md`](PACKAGE-USAGE.md) for dry-run, collision, verification,
and release instructions.

## Repository principles

- Preserve provenance.
- Prefer stable identifiers over filenames as references.
- Do not overwrite immutable findings.
- Record what evidence supports each important claim.
- Separate observations, evidence, assumptions, inferences, and conclusions.
- Rebuild generated registries after creating canonical artifacts.
- Leave the repository usable by the next agent.
