---
id: RQ-ROS-2026-A034
title: Deterministic requirements-preservation gate
status: implemented
version: 1.0.0
owners: [repository-governance]
created: 2026-10-24
updated: 2026-10-24
research_area: repository-operating-system
priority: critical
related_documents:
  - RQ-ROS-2026-A024
  - RQ-ROS-2026-A031
tags: [requirements, intake, derivation, provenance, verification]
derived_from: [INPUT-5a43bc5edee67caa]
provenance:
  contributions:
    EXE-20261024T143321807Z-6c3cd34a:
      operations: [created]
      at: 2026-10-24T15:04:12.468Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Canonical ingestion of REQ-PRES-001 (DC-008): deterministic requirements-preservation gate"
---

# Deterministic requirements-preservation gate

Canonical ingestion of intake `REQ-PRES-001` (defect DC-008: Causa
Rehearsal 3 silently lost the mandatory P0 requirement "UI-016: Causa semantic
color system" when it derived its requirements from kemiller2002/indy-init).
The source, `.praxis/processed/INPUT-5a43bc5edee67caa/`, states the operator's
requirement in full. Implementation and contract:
[`docs/requirements-preservation.md`](../../docs/requirements-preservation.md).

## Requirements

1. **Baseline.** Praxis MUST accept an authoritative baseline manifest
   (`praxis.requirements-baseline/1`). It records:
   - the source repository and its pinned commit;
   - the source documents, with the SHA-256 of their text;
   - an inventory of source requirements, each with a stable id, a priority,
     a hash-pinned section and structured mandatory obligations.

   An obligation has an id, verbatim text and a kind (`value`, `acceptance`
   or `constraint`), plus exact tokens. Tokens are required for `value`.
2. **Disposition.** Praxis MUST accept a derived-package disposition file
   (`praxis.requirements-disposition/1`).
   - It gives each source requirement exactly one disposition:
     - Preserved;
     - Strengthened;
     - ExcludedWithApproval;
     - Superseded, by an approved replacement;
     - NotApplicable, with a justification.
   - It maps each obligation to the derived criteria that carry it.
3. **Approvals.** Approval records (`praxis.requirements-approvals/1`) carry
   a decision id, an approving authority, a scope, the ids they cover and an
   ISO date. Praxis MUST NOT generate, write or infer an approval or an
   equivalence review.
4. **Checks.** `praxis requirements preservation verify` MUST
   deterministically, and without an LLM:
   - **fail** each of these:
     - a missing disposition for an inventoried mandatory requirement;
     - an obligation that is dropped or mapped to nothing;
     - a `value` obligation whose exact tokens do not literally appear in
       its mapped criteria (weakening or false equivalence);
     - an `acceptance` obligation that is not mapped to a derived acceptance
       criterion;
     - an exclusion, supersession or conflict decision without a valid,
       covering approval;
   - report **drift** that fails until reconciled when the source text, a
     pinned section, or the inventory's completeness differs from the
     baseline;
   - **block** a declared conflict between mandatory requirements until an
     approval decides it;
   - allow one source requirement to map to many criteria only when every
     obligation is covered;
   - emit **needs-review**, which blocks, where only semantic judgement could
     decide. It never silently accepts.
5. **Evidence.** The gate MUST emit a machine-readable
   `praxis.requirements-preservation/1` evidence document. It records:
   - the input hashes;
   - the per-requirement and per-obligation outcomes;
   - every exclusion with its approval record;
   - every finding.

   It also emits a human summary, and exits nonzero on FAIL or BLOCKED
   (`3`; `2` for unusable input).
6. **Boundary.** Praxis owns intake, provenance, derivation and disposition.
   - Ordo owns decision authority for approvals: `ordoDecision` is reserved,
     with no coupling.
   - Dokimos owns the verification gates that invoke the command.
   - Percepta owns semantic UI verification.

## Acceptance criteria

| ID | Acceptance criterion |
|---|---|
| PRES-01 | `praxis requirements preservation verify` and `sections` exist in F# under the four-tier architecture, as these modules: Domain `Praxis.Domain.Requirements.Preservation`; Contracts `PreservationJson`; Application `PreservationGate`; Infrastructure `FilePreservationInputs`; CLI `RequirementsCommands`. |
| PRES-02 | Versioned JSON schemas for the baseline, disposition, approvals and report exist under `schemas/`. A test checks that their titles match the decoders and renderer, and that every finding code the gate can emit is enumerated. |
| PRES-03 | Regression scenarios A-L run as Praxis tests with fixtures in `tests/fixtures/requirements-preservation` and pass: A complete pass; B UI-016 removed fails; C weakened palette fails; D screenshot obligation dropped fails; E approved exclusion passes and is reported; F unapproved exclusion fails; G source drift detected; H conflict blocked; I one-to-many passes only when complete; J generic accessibility mapping fails; K read-only approved exclusion passes; L the original Rehearsal 3 package is detected and a corrected package passes. |
| PRES-04 | The Rehearsal 3 fixture copies the real files byte for byte, with their source SHAs recorded and checked by a test. |
| PRES-05 | `docs/requirements-preservation.md` documents: the data model, dispositions, approval records, drift, exit codes, the ownership boundary, the rehearsal initialisation procedure and the gate's limits. `docs/cli.md` links it. |
| PRES-06 | The full Praxis build and test suite pass. |
