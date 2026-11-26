---
id: DF-ROS-2026-A076
title: Praxis carries the reliability and cache-readiness tenets into repository governance through a declared-applicability file, report-only first
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-11-25
updated: 2026-11-26
research_area: repository-operating-system
decision_type: governance
supports: [RQ-ROS-2026-A038]
related_documents:
  - requirements/APPLICATION-RELIABILITY-DECLARATION.md
  - requirements/SHARED-APPLICATION-FOUNDATIONS.md
  - docs/00-governance/Engineering-Standards.md
  - RQ-ROS-2026-A038
tags: [reliability, availability, cache-readiness, governance, decision]
confidence: medium
derived_from: []
provenance:
  contributions:
    EXE-20261125T201255923Z-3477d5c5:
      operations: [created, modified]
      at: 2026-11-25T20:14:03.943Z
      last: 2026-11-26T00:30:01.118Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "WI-0113: INIT-012 reliability applicability declaration (proposed)"
---

# Context

The owner approved two engineering tenets for all applicable Echelon systems
(echelon-organization-administration [#12](https://github.com/kemiller2002/echelon-organization-administration/issues/12),
[#14](https://github.com/kemiller2002/echelon-organization-administration/issues/14)) and asked that they be integrated
into canonical engineering standards and Praxis repository governance. INIT-012 ([index](https://github.com/kemiller2002/echelon-organization-administration/blob/claude/echelon-availability-architecture-ezhitq/application-governance/availability-architecture/00-INDEX.md)) produced the
portfolio requirements, alternatives assessment and applicability matrix. Most Echelon repositories are libraries,
tooling, static sites or research and must not be pushed into runtime machinery; a few are transactional applications.

`requirements/SHARED-APPLICATION-FOUNDATIONS.md` already establishes the precedent of a portfolio-wide standard owned
by Praxis and verified where a repository can show it (URL-addressable state).

# Decision

1. Praxis owns the *declaration and its verification*, not the runtime capability.
2. Each repository declares applicability in `.echelon/reliability.json` (RQ-ROS-2026-A038, schema
   `echelon.reliability/v1`).
3. Verification starts **report-only** for one quarter, then becomes enforcing for repositories that declare
   `applicable` (owner decision U-14).
4. Compliance states are compliant, partially compliant, noncompliant, not applicable and unverified; unverified
   is never displayed as compliant; static evidence alone never yields `compliant` for families whose requirements
   need integration or operational evidence.
5. On acceptance, `docs/00-governance/Engineering-Standards.md` gains a short "Operated systems" paragraph
   pointing to the canonical tenets; this decision does not edit that canonical document while proposed.

# Alternatives considered

* **Enforce immediately** — rejected: every repository would fail at once, normalising red CI (observed
  portfolio-wide in the 2026-10-05 quality audit).
* **No Praxis involvement; policy document only** — rejected: tenets without a verifiable declaration drift.
* **Praxis verifies runtime behaviour** — rejected: outside Praxis's competence; runtime evidence belongs to
  application tests and operations.

# Consequences

Low adoption cost for non-applicable repositories (one file); explicit evidence trail for applicable ones;
a new schema to maintain. Implementation requires a separate work item.

# Status

Accepted by owner instruction to implement (2026-11-26).

Drafted by an agent on 2026-11-25. Implemented report-only under WI-0113; RQ-ROS-2026-A038 states which
PRX-REL items are implemented. Item 5 (the Engineering Standards paragraph) is not done by this change.
