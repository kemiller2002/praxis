# Identifier Standard

**Status:** Canonical  
**Version:** 1.1.0

## Format

New records use:

```text
<PREFIX>-<AREA>-<YYYY>-<TOKEN>
```

`PREFIX` is one of `RP`, `JR`, `EV`, `HY`, `TH`, `EX`, `DF`, `CN`, `GL`, or
`MS`. `AREA` is uppercase letters, digits, or hyphens. `YYYY` is the creation
year. `TOKEN` is either four decimal digits for an already coordinated legacy
sequence or four uppercase hexadecimal characters for parallel-safe creation.

Example: `EV-ROS-2026-A7F2`.

## Allocation

Sequential allocation is unsafe across parallel Git branches without a lock.
New work should generate a random hexadecimal token and retry on collision.
The validator, not allocation order, is the final uniqueness authority. Gaps
are valid. IDs are stable, case-sensitive, and never reused.

The filename for a canonical Markdown artifact is:

```text
<ID>--<short-kebab-title>.md
```

Templates may use placeholders. Real artifacts must use a filename beginning
with their ID.

## Governed kinds

`praxis validate` discovers, registers and checks every kind below with the
same identifier, filename, status, reference and provenance rules. A kind
marked *optional registry* needs no registry file until it has a record.

| Prefix | Kind | Root directory | Registry | Allowed statuses |
|---|---|---|---|---|
| `DF` | Decision Record | `research/decisions` | `registries/decisions.json` | draft, review, accepted, superseded, withdrawn |
| `EV` | Evidence | `research/evidence` | `registries/evidence.json` | draft, review, accepted, superseded, withdrawn |
| `EX` | Experiment | `research/experiments` | `registries/experiments.json` | proposed, active, blocked, completed, cancelled |
| `HY` | Hypothesis | `research/hypotheses` | `registries/hypotheses.json` | proposed, active, supported, rejected, superseded, withdrawn |
| `JR` | Journal | `research/journals` | `registries/journals.json` | any |
| `MS` | Mission | `missions` | `registries/missions.json` | proposed, approved, active, blocked, completed, cancelled, archived |
| `RP` | Research Execution Package | `research/packages` | `registries/research-packages.json` | draft, review, accepted, canonical, deprecated, archived, superseded, withdrawn |
| `TH` | Theory | `research/theories` | `registries/theories.json` | candidate, supported, established, challenged, superseded, rejected |
| `RQ` | Requirement | `research/requirements` | `registries/requirements.json` (optional registry) | draft, proposed, accepted, implemented, verified, deprecated, superseded, rejected |
| `CN` | Concept | `research/concepts` | `registries/concepts.json` (optional registry) | draft, review, accepted, superseded, withdrawn |
| `GL` | Glossary | `research/glossary` | `registries/glossary.json` (optional registry) | draft, review, accepted, superseded, withdrawn |

A Concept records a stable domain concept and its relationships; a Glossary
record defines a term and its usage boundaries. Both follow the `<ID>--<title>.md`
filename rule and may reference other records through the usual reference
fields (for example `related_documents`, `supersedes`).
