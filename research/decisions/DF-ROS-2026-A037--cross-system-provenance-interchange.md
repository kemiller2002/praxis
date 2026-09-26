---
id: DF-ROS-2026-A037
title: Echelon systems exchange Praxis provenance as a versioned interchange record, propagate the execution, and never strip history
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
  - RQ-ROS-2026-A009
  - RQ-ROS-2026-A013
  - RQ-ROS-2026-A014
  - RQ-ROS-2026-A015
  - docs/agent-provenance.md
  - docs/echelon-provenance-architecture.md
supersedes: []
superseded_by: []
tags: [provenance, echelon, integration, versioning, lineage]
confidence: high
provenance:
  contributions:
    EXE-20260926T204846052Z-8cef0d1e:
      operations: [created]
      at: 2026-09-26T20:50:07.971Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Architecture decision for cross-system provenance interchange"
derived_from: [DF-ROS-2026-A036]
---

# Decision

1. **One portable form.** Provenance crosses a boundary as `praxis.provenance-record`.
   - It is the existing execution-keyed contribution mapping and the existing actor.
   - It adds `contract`, a semantic `version`, `subject`, `derivedFrom` (lineage), and optional verbatim `sources` snapshots.
   - It defines no new actor, kind, execution, or contribution semantics.
2. **Versioning by major version.**
   - A consumer interprets any minor of its supported major.
   - It carries an unsupported major verbatim.
   - It rejects malformed input.
   - It reads the legacy unversioned registry projection as version 1.
3. **Shared conformance fixtures, not a shared library.**
   - Praxis publishes `schemas/conformance/provenance-record/`.
   - Each Echelon system implements the small codec in its own language and tier.
   - Each system vendors the fixtures with the Praxis commit and file digests they came from.
   - Both sides test against the same cases.
4. **Execution propagation through the environment.**
   - `ros provenance identity --env` exports the resolved actor and the caller's own `ROS_EXECUTION_ID`.
   - A downstream system with no propagated execution keys its own run as `EXE-<system>.<run>`.
5. **Append, never replace.**
   - Transport is verbatim.
   - A representation change is `migrated`. A content change is `modified` or a documented interchange operation (`x-handled`, `x-resolved`, `x-remediated`, `x-validated`, `x-dismissed`).
   - A derivation starts a new record whose sources are lineage, never authorship.
   - `check-record --previous` proves a successor did not destroy history.
6. **The registry describes provenance capability; it does not own identity.** echelon-registry's execution envelope adopts the Praxis actor, and its system manifests declare which interchange versions a system accepts and emits, and whether it preserves them. A system that lacks the capability resolves as `misconfigured` for callers that require it; the system itself is never broken.

# Why

- **Why a single portable form.**
  - Every downstream system the inventory covers either had no actor, had a free-text `author_agent`, or had its own incompatible actor. The registry envelope's actor used `identity`, had no model or runtime, used a `system` kind, and expressed unknown as `{state}`.
  - Each of these was a second identity model in the making.
  - A single versioned wrapper around the existing shapes lets them all converge without Praxis owning their code.
- **Why fixtures rather than a package dependency.**
  - The systems are F# on .NET 8 and .NET 10, Node, and C#. Several have strict tier rules, for example Ordo Core and Vigila Semantic, which reference only FSharp.Core.
  - A shared package would have created exactly the hard coupling the independence requirement forbids.
  - Fixtures give the same assurance at test time with zero runtime coupling.
- **Why snapshots in `sources`.**
  - A follow-up in Vigila must keep the agent that discovered the underlying finding even when Aegis is not installed.
  - Carrying the source's record verbatim under lineage does that without merging identities.
- **Why namespaced foreign execution keys.** Minting a Praxis-shaped `EXE-<timestamp>-<random>` outside Praxis would be indistinguishable from impersonating a Praxis run.

# Alternatives rejected

- **A new Echelon-wide identity schema owned by the registry.** This would fork the model that DF-ROS-2026-A036 established.
- **A shared NuGet/npm provenance library.** This creates a hard dependency and violates Ordo, Vigila, and Strata tier rules.
- **Treating `ROS_EXECUTION_ID` as equivalent to `--execution` inside Praxis.** Praxis keeps its own stricter same-run checks. The variable is a propagation hint for downstream systems, and Praxis validation still cross-checks any contribution that returns to it.
- **Stripping unknown fields on read and re-emitting a canonical form.** This destroys attestation fields that other systems or later versions add.

# Consequences

- Praxis gains:
  - `ProvenanceRecord` (Domain);
  - `ProvenanceRecordJson` (Contracts);
  - `Credentials` (Domain), a credential-shape guard that now also applies to artifact provenance and event actors;
  - `provenance check-record` and `provenance identity --env`;
  - the conformance fixtures.
- The schemas and fixtures ship in the npm package but are not installed into repositories. The provenance-record schema and the conformance directory are excluded from the embedded scaffold, like the provenance-actor and artifact-provenance schemas.
- Downstream systems are changed per `docs/echelon-provenance-architecture.md`. Systems whose installed Praxis predates DF-ROS-2026-A036 get the repository policy through `ros upgrade`. Their history is not rewritten.

# Revisit when

- An attestation authority is chosen. It would add signed fields to contributions, which consumers already preserve.
- A second major version of the interchange record is needed.
