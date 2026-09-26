---
id: DF-ROS-2026-A037
title: Praxis provenance crosses Echelon system boundaries as one versioned interchange contract
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-26
updated: 2026-09-26
research_area: repository-operating-system
decision_type: architecture
supports: []
related_documents:
  - DF-ROS-2026-A036
  - RQ-ROS-2026-A013
  - RQ-ROS-2026-A014
  - RQ-ROS-2026-A015
  - RQ-ROS-2026-A016
  - RQ-ROS-2026-A017
  - RQ-ROS-2026-A018
  - RQ-ROS-2026-A019
  - docs/agent-provenance.md
  - docs/echelon-provenance-architecture.md
supersedes: []
superseded_by: []
tags: [provenance, echelon, interchange, integration, versioning]
confidence: medium
derived_from: [DF-ROS-2026-A036, RQ-ROS-2026-A009]
provenance:
  contributions:
    EXE-20260926T075049280Z-9919ff63:
      operations: [created]
      at: 2026-09-26T08:05:36.758Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Cross-system Echelon provenance upgrade (work item FEAT-ECHELON-PROVENANCE)"
---

# Decision

Praxis provenance (DF-ROS-2026-A036) is the only agent identity and provenance
model in Echelon. Other systems carry it; they do not redefine it.

1. **One interchange block.** Provenance crosses a boundary as
   `praxis.provenance/1` (`schemas/provenance-interchange.schema.json`): the
   same `contributions` map and lineage an artifact carries in front matter,
   plus a major-version tag. Systems embed the block in their own records
   (a follow-up, a fault event, a snapshot, an execution envelope).
2. **Deterministic receiving rules.** Every receiver classifies a block as
   `supported`, `unsupported` (another major: carried verbatim, never
   interpreted), or `malformed` (rejected, never dropped or repaired).
   Unknown fields are preserved. An unknown operation code under major 1 is
   tolerated and reported, so Praxis can add vocabulary in a minor release.
3. **Foreign executions.** A contribution produced in another system is keyed
   `EXT-<system>.<run-id>` with that system's registry id and its own run id.
   Praxis carries such keys verbatim and reports them for audit only.
4. **Shared role vocabulary.** `discovered`, `measured`, `transformed`,
   `remediated`, `validated`, and `resolved` join the existing operations.
   Only `created` establishes authorship; lineage stays in `derivedFrom`.
5. **Explicit propagation.** The current actor travels in an execution
   envelope, CLI flags, or the existing `ROS_ACTOR*`/`ROS_TELEMETRY_*`
   variables, and the current execution in `ROS_EXECUTION_ID`. No system
   re-implements runtime discovery; unknown stays `unknown`.
6. **No credentials, no authority.** Provenance never carries authentication
   material, and it never substitutes for authentication, authorization, or
   evidence quality.
7. **Conformance through shared fixtures, not shared binaries.** Praxis owns
   `tests/fixtures/provenance-interchange/` and a dependency-free reference
   library (`lib/provenance-interchange.mjs`). Downstream systems vendor the
   fixtures with the source commit and SHA-256 and implement a small local
   codec, so no Echelon system gains a runtime dependency for provenance.

# Why

- A second model is what the Echelon Registry execution envelope v1 had
  already started to become: its own actor shape (tri-state `knownValue`,
  `system` kind, no model or runtime) that cannot hold a Praxis actor. The
  registry now maps v1 losslessly and defines v2 to carry the Praxis actor and
  interchange block (see the echelon-registry repository).
- Keying by execution already makes "one entry per run" structural in Praxis.
  Extending the key space instead of adding an "execution" field keeps one
  shape for front matter, registries, events, and interchange.
- A version tag plus a tolerance rule is the smallest mechanism that lets
  independently released systems evolve without one silently stripping or
  corrupting what another wrote.
- Vendored fixtures keep systems loosely coupled; a published shared package
  can replace the local codecs later without changing the contract.

# Alternatives rejected

- **Each system keeps its own actor fields** (Vigila `{type,name}`, Aegis
  `RecoveryActor`, Dokimos `collector`, registry `knownValue`). Rejected: a
  schema fork per system, lossy mappings, and no cross-system metrics.
- **Registry-owned identity.** The registry routes integrations; semantics of
  agents, executions, and contributions belong to Praxis.
- **A hard dependency on the Praxis package or CLI.** Violates the Echelon
  independence invariant; provenance must survive when Praxis is absent.
- **Inventing `EXE-` ids for foreign runs.** Fabrication; `EXT-` names the
  system that actually owns the run.
- **Rejecting unknown operations.** Would make every vocabulary addition a
  breaking change for every consumer.

# Consequences

- Praxis gained `EXT-` keys, the role operations, the interchange codec,
  `ros provenance export`, `ROS_EXECUTION_ID`, a credential tripwire, and the
  conformance fixtures. Existing artifacts, events, and policies are unchanged
  and keep validating.
- Downstream systems add requirements that reference this contract and small
  codecs tested against the vendored fixtures; see
  `docs/echelon-provenance-architecture.md` for the inventory and the
  per-system decisions, including systems deliberately left unchanged.
- **Limitation:** an older Praxis reader (before this change) rejects the new
  operations and `EXT-` keys in front matter. Artifacts carrying them need a
  Praxis version that includes this decision.
- **Limitation:** the credential tripwire recognises common token shapes only;
  it is a guard against accidents, not a secret scanner.

# Revisit when

- A shared codec package is published for .NET or npm consumers.
- A second interchange major version is needed.
- An attestation authority is chosen (then attestation fields join the
  contribution entry, which already preserves unknown fields).
