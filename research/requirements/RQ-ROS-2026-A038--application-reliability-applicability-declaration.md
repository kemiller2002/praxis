---
id: RQ-ROS-2026-A038
title: Every Echelon repository declares its applicability to the reliability and cache-readiness tenets, with honest compliance states
status: implemented
version: 0.2.0
owners: [repository-governance]
created: 2026-11-25
updated: 2026-11-26
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A076
  - requirements/SHARED-APPLICATION-FOUNDATIONS.md
  - requirements/APPLICATION-RELIABILITY-DECLARATION.md
  - requirements/echelon-reliability-v1.schema.json
tags: [reliability, availability, cache-readiness, applicability, compliance, foundations]
provenance:
  contributions:
    EXE-20261125T201255923Z-3477d5c5:
      operations: [created, modified]
      at: 2026-11-25T20:14:03.054Z
      last: 2026-11-26T00:30:00.170Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "WI-0113: INIT-012 reliability applicability declaration (proposed)"
---

# Reliability applicability declaration

**Status: implemented, report-only, in part** (see "Implementation status" below for exactly which items).
Proposed under INIT-012 (echelon-organization-administration issues #12 and #14; mission
"Echelon Continuous Availability, Durable Execution, and Cache-Ready Architecture"); DF-ROS-2026-A076 was
accepted by owner instruction to implement on 2026-11-26. The tenets themselves are owner-approved; this record
states how Praxis verifies that each repository has *declared* its position. It does not make any repository
adopt runtime machinery, and it does not claim that a declaration proves runtime reliability.

Normative portfolio requirements (EAR-*) and evidence: [INIT-012 index](https://github.com/kemiller2002/echelon-organization-administration/blob/claude/echelon-availability-architecture-ezhitq/application-governance/availability-architecture/00-INDEX.md).

## Requirements

Each requirement lists: statement · rationale · applicability · acceptance · verification · priority.
Status of each item: see "Implementation status".

1. **PRX-REL-001 Declaration file.** A repository installed with Praxis SHOULD contain
   `.echelon/reliability.json` conforming to `echelon.reliability/v1`
   ([schema](../../requirements/echelon-reliability-v1.schema.json)). *Rationale:* applicability must be declared, not
   assumed. *Applicability:* every Praxis-installed repository. *Acceptance:* `praxis foundations verify` reports
   present/absent/invalid. *Verification:* automated. *Priority:* P1. Report-only until owner decision U-14
   changes it to enforcing.
2. **PRX-REL-002 System class.** The declaration names one system class: transactional-application,
   interactive-application, stateless-service, background-processing-service, developer-tooling, shared-library,
   static-website, research-repository, governance-repository or other. *Acceptance:* schema enum. Automated. P1.
3. **PRX-REL-003 Tenet applicability.** For each tenet (EAR-TEN-001 continuous availability and durable
   execution; EAR-TEN-002 cache readiness; EAR-TEN-003 reliability by construction) the declaration states
   `applicable`, `partially-applicable` or `not-applicable`; anything other than `applicable` carries a reason.
   EAR-TEN-004 (infrastructure minimalism) is always applicable. *Acceptance:* schema + non-empty reason.
   Automated. P1.
4. **PRX-REL-004 Compliance states.** Each applicable requirement family carries one state:
   `compliant`, `partially-compliant`, `noncompliant`, `not-applicable` or `unverified`. *Rationale:* honest
   status. *Acceptance:* a state of `compliant` or `partially-compliant` without at least one evidence reference
   that resolves to a path in the repository (or an immutable external reference) is reported as
   `unverified`; Praxis never renders `unverified` as compliant. Automated. P0.
5. **PRX-REL-005 Evidence classes.** Each evidence reference states its class: `automated`,
   `integration-test`, `operational` or `architectural-review`. A family may not be `compliant` on
   `automated` evidence alone where its requirements name integration or operational verification.
   *Rationale:* static analysis alone never establishes runtime reliability. Automated (cross-check against the
   vendored requirement-family verification table). P1.
6. **PRX-REL-006 Documented exceptions.** Exceptions name the requirement, rationale, compensating controls,
   approver, decision record and expiry date. Expired exceptions are reported as findings. Automated. P1.
7. **PRX-REL-007 Required design evidence.** A repository declaring `applicable` for EAR-TEN-001 references:
   an architecture decision covering request classification and topology; an SLO/RPO/RTO declaration; and a
   failure-mode matrix. Missing references are reported, not fabricated. Automated (presence) +
   architectural review (content). P1.
8. **PRX-REL-008 Requirement traceability.** Requirement identifiers referenced by the declaration match the
   published identifier pattern (`EAR-[A-Z]{3}-\d{3}`) and, where the repository vendors the published list,
   resolve. Automated. P2.
9. **PRX-REL-009 Dependency compatibility.** Where a repository consumes a shared reliability capability
   (for example the durable Execution module once extracted, ADR-EAR-004), the declaration pins its version or
   immutable digest, consistent with "Dependency discipline and completion" in
   `requirements/SHARED-APPLICATION-FOUNDATIONS.md`. Automated. P2.
10. **PRX-REL-010 Migration planning.** A repository that is `applicable` and not `compliant` names a migration
    plan reference (work item or document). Automated (presence). P2.

## Acceptance for this record

DF-ROS-2026-A076 was accepted by owner instruction to implement (2026-11-26); implementation is WI-0113.

## Implementation status (2026-11-26, WI-0113)

`praxis foundations verify` reads `.echelon/reliability.json` and reports a separate, report-only `reliability`
section (finding codes `ECHELON-FND-RELIABILITY-001..013`, all `info`; they never change `passed` or the exit code).
The evaluation is pure (`src/Praxis.Domain/Foundations/ReliabilityDeclaration.fs`, over the parse in
`src/Praxis.Infrastructure/Foundations/ReliabilityDeclarationReader.fs`); only `src/Praxis.Cli/Foundations.fs`
reads files. Tests: `tests/Praxis.Tests/ReliabilityDeclarationTests.fs`. Praxis declares itself in
`.echelon/reliability.json` (`developer-tooling`). Documentation: `docs/application-foundations.md`.

| Item | Status |
|---|---|
| PRX-REL-001 | Implemented, report-only: present / absent / invalid. Absent is a note, never a failure. Enforcing mode is not implemented (U-14). |
| PRX-REL-002 | Implemented (system class enumeration). |
| PRX-REL-003 | Implemented (three tenets, applicability enumeration, non-empty reason unless `applicable`). EAR-TEN-004 has no field to check. |
| PRX-REL-004 | Implemented: a `compliant`/`partially-compliant` family with no evidence, or with any evidence ref that does not resolve, is reported with effective state `unverified`. |
| PRX-REL-005 | **Partly implemented:** the evidence class enumeration is validated. The cross-check that a family is not `compliant` on `automated` evidence alone where its requirements name integration or operational verification is **not implemented**: no vendored requirement-family verification table exists in Praxis. |
| PRX-REL-006 | Implemented: every exception field is required; an `expires` date before today is reported; an unparseable expiry is reported. |
| PRX-REL-007 | Implemented (presence of `designEvidence.decision`, `serviceLevels`, `failureModes` when continuous availability is `applicable`); content remains architectural review. |
| PRX-REL-008 | **Partly implemented:** exception requirement identifiers are checked against `EAR-[A-Z]{3}-\d{3}`. Resolution against a vendored published list is **not implemented** (no vendored list). |
| PRX-REL-009 | **Partly implemented:** each dependency must name its capability and a non-empty pin. Whether the pin is immutable or matches the consumed capability is **not checked**. |
| PRX-REL-010 | Implemented (presence of `migrationPlan`). |

### Interpretations recorded as unresolved questions

These are the implementer's readings, not owner decisions; each is report-only, so none can fail a repository.

1. *Immutable external reference* (PRX-REL-004) is read as an absolute URL that names a full 40-hex commit or a
   `sha256:` digest. Other URLs, absolute paths, paths escaping the repository and record identifiers (for example
   `DF-...`) do not resolve. A repository path's `#fragment` is ignored.
2. *Applicable and not compliant* (PRX-REL-010) is read as: some tenet is exactly `applicable`, and either no
   family is declared or some family's *effective* state is neither `compliant` nor `not-applicable`.
3. The schema forbids unknown properties (including `$schema`); the check reports them as invalid.
