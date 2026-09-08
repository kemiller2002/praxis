---
id: EV-ROS-2026-A028
title: F# artifact shadow migration implementation and verification results
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-08
updated: 2026-09-08
research_area: repository-operating-system
evidence_type: primary
supports:
  - EX-ROS-2026-A020
  - HY-ROS-2026-A021
  - HY-ROS-2026-A022
  - HY-ROS-2026-A023
related_documents:
  - EV-ROS-2026-A018
  - JR-ROS-2026-A019
  - RP-ROS-2026-A029
  - DF-ROS-2026-A027
supersedes: []
superseded_by: []
tags: [fsharp, migration, artifacts, differential-testing, verification, sde]
confidence: medium
---

# Scope and result

This evidence records the completed, additive MIG-03/MIG-04 artifact slice.
It does **not** switch production authority: `./ros`, the Node implementation,
and installed starter profiles remain unchanged as the production contract.

The F# shadow implements the scoped artifact capability in five inward-facing
projects: domain policy/projection, application use cases and effect outcomes,
filesystem/front-matter infrastructure, explicit JSON contracts, and a CLI.
Its commands are `artifacts validate [--json]`, `registry build [--dry-run]`,
and `registry check`.

# Measured compatibility evidence

| Check | Result | Mechanism |
|---|---|---|
| valid fixture | no F# findings | typed F# test |
| invalid fixture | 10 path/field/message identities equal Node and frozen fixture | Node-driven differential test |
| registry projection | all eight F# fixture registry files byte-equal to Node and frozen bytes | Node/F# differential plus existing Node/Python fixture test |
| repeat build | second F# build reported zero changed registries | F# typed and differential tests |
| canonical inputs | pre/post SHA-256 values equal in isolated F# build | F# typed test |
| current repository | F# artifact validation JSON was valid and F# registry check was current | Node-driven smoke test |
| exit handling | unknown command returns usage exit `2`; invalid artifacts return `1` | differential test |
| architecture | actual graph is inward; synthetic Domain -> Infrastructure graph is rejected | F# architecture test |
| partial write | an injected second write failure returns explicit `Indeterminate` incomplete outcome with written/pending sets | F# typed test |

The F# JSON finding record includes the same additional `severity` and `repair`
fields already used by the Node CLI's public JSON renderer. The differential
comparison intentionally compares the preregistered semantic identity fields
(`path`, `field`, `message`) rather than confusing renderer enrichment with a
new rule decision.

# Verification runs

At T5, the unrestricted local run completed:

- 87 Node tests passed;
- 7 Python tests passed;
- 8 F# architecture/unit tests passed;
- 3 Node-driven F# differential/smoke tests passed;
- both TypeScript builds passed;
- successful .NET build reported 0 warnings and 0 errors.

The first sandboxed full run could not bind temporary loopback HTTP listeners
and therefore reported 14 `EPERM` server-test failures. The exact same suite
was rerun with local listener permission and passed; this is an environment
constraint, not a product defect.

# Negative and review evidence

The guardrails were shown to fire rather than merely exist: malformed front
matter, invalid identifiers/statuses/references, stale registries, an outward
project reference, unknown CLI syntax, an indeterminate write, and an invalid
telemetry provenance source all yield non-success outcomes. The self-review checked the F# scope against the Node
artifact functions, front-matter scalar handling, registry ordering/bytes,
write boundaries, and CLI exit cases. No true independent review was available:
the three invited reviewers exhausted their execution quota before a final
review, so this review must not be treated as independent verification.

# Boundaries and remaining uncertainty

The slice deliberately does not own broader `./ros validate` responsibilities
for work, telemetry, or global stale-registry checks. It makes one registry
replacement atomic, but it does not make an eight-file build transactional;
MIG-05 remains the prerequisite for moving stateful writers. It also provides
no consumer runtime, packaged .NET payload, hosted GitHub run, or external
cost/token observation.

# Checkpoints and provenance

- T3, first vertical slice complete: `2026-09-08T06:14:34Z`.
- T4, implementation/docs/CI complete: `2026-09-08T06:18:00Z`.
- T5, heterogeneous verification and self-review complete:
  `2026-09-08T06:23:23Z`.

Command-level output and capability observations are preserved in
`docs/migrations/fsharp/TELEMETRY.md` and execution
`EXE-20260907T203141590Z-54f547f8`; this record does not estimate unavailable
provider model, token, cache, context, or cost values.
