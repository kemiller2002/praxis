# Application reliability applicability declaration

Status: **Proposed** (RQ-ROS-2026-A038; decision DF-ROS-2026-A076, proposed). Not implemented; nothing in this file is
enforced by `praxis validate` or `praxis foundations verify` today.

Portfolio tenets (owner-approved, echelon-organization-administration #12, #14): Continuous Availability and Durable
Execution; Cache-Ready, Not Cache-Dependent; Reliability by Construction; Infrastructure Minimalism. Detailed
requirements: [INIT-012](https://github.com/kemiller2002/echelon-organization-administration/blob/claude/echelon-availability-architecture-ezhitq/application-governance/availability-architecture/00-INDEX.md).

## Declaration

`.echelon/reliability.json`, schema `echelon.reliability/v1` ([schema](echelon-reliability-v1.schema.json)).
Minimal non-applicable example (static site, library, research or governance repository):

```json
{
  "schema": "echelon.reliability/v1",
  "systemClass": "static-website",
  "tenets": {
    "continuousAvailability": { "applicability": "not-applicable", "reason": "Static content; no accepted work." },
    "cacheReadiness": { "applicability": "not-applicable", "reason": "No server-side queries." },
    "reliabilityByConstruction": { "applicability": "not-applicable", "reason": "No runtime." }
  }
}
```

A transactional application additionally lists requirement families with a compliance state and evidence
(`compliant`, `partially-compliant`, `noncompliant`, `not-applicable`, `unverified`), exceptions with expiry, design
evidence references (decision, SLO/RPO/RTO declaration, failure-mode matrix) and a migration plan.

## Verification layers

| Layer | What it can establish | Owner |
|---|---|---|
| Automated (Praxis declaration checks, Dokimos rules) | Presence, shape, references, dependency bans, reference boundaries | Praxis, Dokimos |
| Integration tests | Behaviour under injected faults, duplicates, version skew | Application |
| Operational evidence | Failover, restore, rollback drills; SLO attainment | Application operations |
| Architectural review | Classification correctness, honesty of user-facing claims | Reviewer |

Static analysis alone never establishes runtime reliability. `unverified` is never displayed as compliant.
