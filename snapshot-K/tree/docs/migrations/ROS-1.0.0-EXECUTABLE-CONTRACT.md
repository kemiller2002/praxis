# ROS 1.0.0 Executable Contract Migration Report

**Date:** 2026-07-24  
**Scope:** Roadmap Phase 1 and validation/registry portion of Phase 2

## Baseline

The ROS scaffold was untracked user work on top of commit `be0f4e3`. No
canonical research artifacts existed. The scaffold supplied templates, empty
registries, one generic schema, and policies that required manual registry
updates. The roadmap prompt was also untracked. This implementation preserved
those files and did not commit or tag them.

## Incompatibilities found

- Governance REP v2 uses `identifier` and structured confidence/completion;
  scaffold templates use `id` and scalar values.
- REP governance uses `canonical`; the roadmap proposes `accepted`.
- The naming standard specifies sequential IDs without branch coordination.
- Bootstrap requires manual registry updates, creating dual-write drift.
- “Immutable” had no acceptance boundary or enforcement definition.
- Context summaries could contain claims without canonical references.

## Decisions and compatibility

- `id` is the preferred common field; the validator also accepts `identifier`
  for existing REP compatibility.
- `accepted` is the research lifecycle boundary; REP `canonical` remains a
  compatibility alias.
- Both four-digit legacy sequences and four-character hexadecimal tokens
  validate. New parallel work should use tokens.
- Artifact files are canonical; registries are generated.
- Existing templates were not mass-migrated. A future template migration
  should align required fields with the type schemas.

## Files and behavior added

- Lifecycle, supersession, identifier, confidence, artifact-tier, and taxonomy
  contracts.
- Decision Records `DF-ROS-2026-A001` and `DF-ROS-2026-A002`.
- Type schemas for missions and primary research artifacts.
- `./ros validate`, `./ros registry build [--dry-run]`, and
  `./ros registry check`.
- Unit tests and GitHub Actions validation.

## Rollback

Remove the added CLI, workflow, schemas, policies, decisions, and generated
registries, then restore the README/bootstrap wording. Canonical scaffold
content was not destructively migrated.

## Known limitations

- The dependency-free YAML reader intentionally supports the front-matter
  subset used by ROS, not arbitrary YAML.
- Schemas are delivered as contracts; the initial CLI implements the required
  validation slice directly and does not yet evaluate full JSON Schema.
- Accepted-content mutation is policy-only until a trusted Git-baseline digest
  mechanism is implemented.
- Circular supersession beyond self-links is not yet detected.
- Artifact creation and ID allocation commands are deferred.
