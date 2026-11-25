---
id: RQ-ROS-2026-A038
title: Every Echelon repository declares its applicability to the reliability and cache-readiness tenets, with honest compliance states
status: draft
version: 0.1.0
owners: [repository-governance]
created: 2026-11-25
updated: 2026-11-25
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
      operations: [created]
      at: 2026-11-25T20:14:03.054Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "WI-0113: INIT-012 reliability applicability declaration (proposed)"
---

# Reliability applicability declaration

**Status: draft.** Proposed under INIT-012 (echelon-organization-administration issues #12 and #14; mission
"Echelon Continuous Availability, Durable Execution, and Cache-Ready Architecture"). Nothing below is
implemented. The tenets themselves are owner-approved; this record proposes how Praxis would verify that
each repository has *declared* its position. It does not make any repository adopt runtime machinery, and it
does not claim that a declaration proves runtime reliability.

Normative portfolio requirements (EAR-*) and evidence: [INIT-012 index](https://github.com/kemiller2002/echelon-organization-administration/blob/claude/echelon-availability-architecture-ezhitq/application-governance/availability-architecture/00-INDEX.md).

## Requirements

Each requirement lists: statement · rationale · applicability · acceptance · verification · priority.
Status of every item: proposed, not implemented.

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

This record moves from draft to accepted only when DF-ROS-2026-A076 is accepted by the owner. Implementation
in `praxis foundations verify` is a separate work item.
