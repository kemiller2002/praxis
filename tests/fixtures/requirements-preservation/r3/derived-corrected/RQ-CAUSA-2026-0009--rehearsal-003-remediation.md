---
id: RQ-CAUSA-2026-0009
title: Rehearsal 3 remediation — reliability, provider conformance and acceptance
status: accepted
version: 1.0.0
owners: [causa-rehearsal-003]
created: 2026-10-24
updated: 2026-10-24
priority: high
tags: [remediation, reliability, security, deployment]
related_documents: [RQ-CAUSA-2026-0001, RQ-CAUSA-2026-0005, RQ-CAUSA-2026-0006, RQ-CAUSA-2026-0008]
provenance:
  contributions:
    EXE-20261024T123355134Z-d569add0:
      operations: [created]
      at: 2026-10-24T12:33:55.600Z
      actor:
        kind: agent
        id: claude-code-lead
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Rehearsal 3 remediation requirements"
derived_from: [INPUT-1559052530cd6a38]
---

# RQ-CAUSA-2026-0009 — Rehearsal 3 remediation

Source: the operator's remediation brief (`.praxis/inbox/documents/causa-rehearsal-003-remediation-brief.md`).
These criteria are **additional** to the 56 criteria in RQ-CAUSA-2026-0001..0008. They do not change
the original experiment's result, which stays NOT ACCEPTED (blocked). The five partial criteria
(HYP-01, CAU-02, UNC-01, COR-01, UI-02) keep their original wording in RQ-0005, 0006 and 0008.

| ID | Acceptance criterion |
|---|---|
| REM-01 | Deployed acceptance is deterministic under GitHub's anonymous quota: the content check (catalog, all three investigations, digests) cannot fail because the shared runner IP's anonymous quota is exhausted, and the anonymous path either verifies fully or ends in the application's `rate-limited` state with GitHub independently confirming the quota is exhausted. |
| REM-02 | The live suites report which credential an authenticated test used, by name only. Test A on `CAUSA_GITHUB_TOKEN` is reported as passing only when that secret was present and the run passed; otherwise it is reported `BLOCKED: OPERATOR CREDENTIAL REQUIRED`. |
| REM-03 | Request correlation (DC-002): every mutable GitHub read carries a typed request identity. A response is applied only when it matches the active request; superseded, duplicated or unknown responses are rejected by a typed transition. Both completion orders (A then B, B then A) are tested. |
| REM-04 | Freshness (DC-005): every mutable GitHub read has an explicit freshness rule. A credential, source or retry change starts a new generation, so no earlier answer (cached or late) can update state, and an authentication failure is never masked by an earlier success. |
| REM-05 | Provider conformance: sanitized fixtures captured from real GitHub (status, relevant headers, cache headers, error body shape; no credentials) are replayed deterministically, and the fake provider used by the browser tests is checked against them. |
| REM-06 | Adversarial content: an investigation fixture with hostile text (markup, script, `javascript:` and `data:` URLs, SVG payloads) renders inert, and the page makes no request outside `self` and `api.github.com`. |
| REM-07 | Percepta semantic UI evidence is produced by a Percepta capability, or the exact missing capability and a narrowly scoped waiver request are recorded for the operator. |
| REM-08 | A separate remediation acceptance record (`docs/REHEARSAL-003-REMEDIATION-REPORT.md`) states a verdict that follows the evidence, preserving the original report and telemetry unchanged. |
