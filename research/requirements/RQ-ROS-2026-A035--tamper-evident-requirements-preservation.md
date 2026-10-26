---
id: RQ-ROS-2026-A035
title: Tamper-evident requirements preservation and authenticated approvals
status: proposed
version: 1.0.0
owners: [repository-governance]
created: 2026-10-26
updated: 2026-10-26
research_area: repository-operating-system
priority: critical
related_documents:
  - RQ-ROS-2026-A034
  - docs/requirements-preservation.md
tags: [requirements, derivation, verification, approvals, provenance]
derived_from: [INPUT-d9086d5909f4b5ac]
provenance:
  contributions:
    EXE-20261026T163447652Z-ae64136e:
      operations: [created]
      at: 2026-10-26T16:55:41.089Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Canonical ingestion of REQ-PRES-001 (tamper-evident requirements preservation); R1 and R2 implemented under REQ-PRES-004"
---

# Tamper-evident requirements preservation and authenticated approvals

Canonical ingestion of intake `REQ-PRES-001` (tamper-evident requirements
preservation and authenticated ratification). The source,
`.praxis/processed/INPUT-d9086d5909f4b5ac/`, records:

- the bypasses an independent review found in the draft gate of
  `RQ-ROS-2026-A034`: a self-written approvals file or review, an edited
  baseline, a role swap of two palette values, and a negated restatement;
- the proposed requirements R1 to R5.

Indy-init's Rehearsal 4 readiness names R1 and R2 as the minimum release
condition (kemiller2002/indy-init
`requirements-compiled/v1/readiness/CONTRACT-RESOLUTION.md` §3, finding F-004).

## Requirements

| ID | Requirement | State (work item REQ-PRES-004) |
|---|---|---|
| R1 | Every disposition records the baseline file's SHA-256. The gate refuses baseline bytes that differ from the recorded digest, and does not pass when no digest is recorded. | Implemented: `baseline-digest-mismatch` (fail) and `baseline-digest-missing` (blocked) |
| R2 | An exclusion, supersession, not-applicable judgement or conflict decision counts only with an authenticated owner approval. An approval whose actor is an agent is refused with a distinct code. | Implemented in its minimum form: the approvals file must match the digest the approving authority published (`--approvals-sha256`), the approver must be a trusted identity (`--trusted-approver`), and an agent approval gets `approval-agent-authored`. Reviews count only for trusted identities (`review-untrusted`). Not implemented: owner signatures and Ordo-backed approvals. |
| R3 | Tokens are bound to the row and role they came from, so a role swap of two values fails. | Not implemented |
| R4 | A repository can declare the gate required; Praxis validation then runs it in CI. | Not implemented |
| R5 | Negation and paraphrase are reported as `needs-review`, never as preserved. | Not implemented |

Also implemented under REQ-PRES-004: a requirement's obligations may not be
carried by a derived requirement of lower front-matter priority
(`priority-downgraded`), so a P0 requirement cannot be downgraded silently.

## Acceptance

- Each implemented check fails or blocks with its own code, through the real
  CLI: `tests/Praxis.Tests/RequirementsPreservationTests.fs`.
- On the real Indy-init baseline (461 requirements), an identity derivation
  of its compiled package passes. Each unauthorized change to it is refused:
  a missing P0 requirement, a downgrade, an unauthorized exclusion, an altered
  baseline, an identity change, false completion, and cross-run inputs.
  Inputs the gate cannot establish leave it `blocked` or without a verdict,
  never `pass`. The verifier is kemiller2002/indy-init
  `tools/praxis-preservation-verification`.
- The gate's output is byte-identical on rerun.

## Not claimed

- Protection against a caller who controls both the files and the trust
  inputs (the pin and the trusted identities). Those must come from a place
  the derivation does not control.
- Cryptographic authentication of approvers or reviewers.
