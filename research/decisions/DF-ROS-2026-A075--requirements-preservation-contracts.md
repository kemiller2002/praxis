---
id: DF-ROS-2026-A075
title: Requirements preservation uses four versioned public contracts, with approvals kept outside the derivation
status: draft
version: 1.1.0
owners: [repository-governance]
created: 2026-10-24
updated: 2026-10-26
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A034, RQ-ROS-2026-A035]
related_documents:
  - RQ-ROS-2026-A034
  - docs/requirements-preservation.md
  - docs/cli.md
tags: [requirements, intake, derivation, contracts, schemas, ordo, dokimos]
confidence: medium
derived_from: [RQ-ROS-2026-A034]
provenance:
  contributions:
    EXE-20261024T164136513Z-323cd830:
      operations: [created]
      at: 2026-10-24T16:42:09.239Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record the requirements-preservation public contracts (REQ-PRES-002)"
    EXE-20261026T163447652Z-ae64136e:
      operations: [modified]
      at: 2026-10-26T16:56:19.817Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Amendment before first release: baseline digest, authenticated approvals and reviews, priority downgrade (REQ-PRES-004)"
---

# Context

`RQ-ROS-2026-A034` (intake `REQ-PRES-001`, defect DC-008) adds `praxis
requirements preservation verify`. The command is a deterministic gate that
checks a derived requirements package against its authoritative source. It
was written after Causa Rehearsal 3 silently lost the P0 requirement UI-016.

The gate establishes persistent data shapes and a public CLI contract that
other repositories and tools will author and consume:

- Indy-init rehearsals write baselines and dispositions.
- Operators write approval records.
- A Dokimos gate reads the evidence document.

The Engineering Standards require a `DF-` record for a choice that
establishes a public contract or a persistent data shape.

# Decision

1. **Four versioned contracts.** Each is a JSON document whose `schema`
   member names the contract and its major version. Each has a JSON Schema
   under `schemas/`:

   | Contract | Schema file | Written by |
   |---|---|---|
   | `praxis.requirements-baseline/1` | `schemas/praxis-requirements-baseline.schema.json` | intake: the inventory of the authoritative source, pinned to a commit and to SHA-256 hashes of each document and section |
   | `praxis.requirements-disposition/1` | `schemas/praxis-requirements-disposition.schema.json` | the derivation: one disposition per source requirement and an obligation-to-criterion mapping |
   | `praxis.requirements-approvals/1` | `schemas/praxis-requirements-approvals.schema.json` | the approving authority only |
   | `praxis.requirements-preservation/1` | `schemas/praxis-requirements-preservation.schema.json` | the gate: the evidence document |

   `praxis.requirements-sections/1` is the output of the `sections` helper.
   It is a CLI output, documented in `docs/requirements-preservation.md`,
   and has no schema file.
2. **Approvals are a separate document.** An exclusion, supersession,
   not-applicable disposition or conflict decision cites an approval by
   `decisionId`; the disposition never embeds one. Praxis reads approval
   records and never writes, generates or infers them. Equivalence reviews
   follow the same rule.
3. **Strict decoding.** A document naming any other schema or version is
   refused (exit `2`). An unknown future version is never interpreted.
   Hashes are lowercase hex SHA-256 of UTF-8 text with line endings
   normalized to LF.
4. **Compatibility.**
   - Within major version `/1`, members may be added only when they are
     optional and their absence keeps the current meaning.
   - Removing or renaming a member, changing a hash rule, changing token
     matching, or changing what a disposition requires is a new major
     version, migrated by a new decision.
   - Finding codes are added to the report schema's enumeration together
     with their implementation; a test keeps the two equal.
5. **Exit codes follow the CLI contract.**
   - `0`: pass.
   - `3`: fail or blocked; `verdict` says which.
   - `2`: unusable input.
   - `1`: internal failure.

   There is no new exit code.
6. **Not scaffolded yet.** The schemas are excluded from the embedded
   scaffold, so `praxis init` and `praxis upgrade` do not install them into
   projects. Adding them to the starter manifests is a separate change. The
   CLI enforces the contracts itself.
7. **Ordo boundary.** `ordoDecision` on an approval record is reserved for
   the Ordo decision that will back the approval. Praxis records it and
   does not verify it. No Ordo coupling is added.

8. **Amendment before first release (REQ-PRES-004, `RQ-ROS-2026-A035`).**
   No `/1` contract had been released, so `/1` is amended rather than
   versioned:
   - the disposition's `baseline` requires `sha256`, the SHA-256 of the
     baseline file's bytes. A mismatch fails (`baseline-digest-mismatch`); a
     missing digest blocks (`baseline-digest-missing`);
   - an approval record requires `approvedBy: { id, kind }`, with `kind`
     one of `human`, `agent` or `automation`;
   - `verify` takes `--approvals-sha256` (the digest the approving
     authority published) and repeatable `--trusted-approver`. An approval
     counts only from a matching file, by a trusted identity, never by an
     agent; a review counts only for a trusted identity;
   - the report gains `trust`, `baseline.recordedSha256` and
     `approvedBy`, plus eight finding codes, among them
     `priority-downgraded`.

   From the first release on, the compatibility rule above applies.

# Alternatives considered

- **Approvals inside the disposition file.** Rejected. The party deriving
  could then approve its own exclusions in the same document. A separate
  file does not prove who wrote it, but it keeps the roles distinct and
  maps directly onto a future Ordo-backed approval.
- **One combined document.** Rejected. Baselines are pinned once per source
  commit and reused across rehearsals; dispositions change per package; and
  approvals belong to another authority.
- **No schema files, decoder only.** Rejected. Consumers outside Praxis
  (Indy-init, Dokimos) need a published contract to author and validate
  against.
- **A distinct exit code for `blocked`.** Rejected. The CLI exit-code table
  is a public contract, and a gate must stop in both cases. `verdict`
  distinguishes them.
- **Ship the schemas in the scaffold now.** Deferred. It changes what
  installation and upgrade write into every project, and that deserves its
  own decision.

# Consequences

- Rehearsal tooling and gates can depend on stable, versioned shapes.
- The evidence document records the SHA-256 of every input it consumed, so
  a verdict is reproducible and auditable.
- The trust in approvals and reviews rests on the digest and identities the
  caller supplies: an approval counts only from a file matching the
  published digest, by a trusted identity, and never by an agent. This is
  not a cryptographic signature; owner signatures or Ordo-backed approvals
  remain the stronger remedy. It is stated in
  `docs/requirements-preservation.md` ("Limits").

# Reversibility

The change is additive: one new command family, four schema files, and no
change to existing state or contracts. Removing it before any consumer
depends on it costs a revert. After adoption, contract changes follow the
versioning rule above.

# Validation

`tests/Praxis.Tests/RequirementsPreservationTests.fs` checks:

- that the schema titles equal the decoder and renderer constants;
- that a real report carries exactly the members the report schema
  requires;
- that every finding code the gate can emit is in the schema's enumeration
  and nothing else is.

Scenarios A-L exercise the contracts end to end.

# Status

This record is a draft. It records the decision as implemented in PR #292
and awaits owner acceptance; the agent does not treat it as accepted.
