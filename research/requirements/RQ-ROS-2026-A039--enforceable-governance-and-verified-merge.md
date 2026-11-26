---
id: RQ-ROS-2026-A039
title: Governance is enforced by platform rules and verified evidence, not by documents; merges require verified completion; bypasses are detected
status: implemented
version: 0.1.0
owners: [repository-governance]
created: 2026-11-26
updated: 2026-11-26
research_area: repository-operating-system
priority: high
related_documents:
  - docs/governance/README.md
  - docs/governance/trust-model.md
  - docs/governance/bootstrap.md
  - docs/governance/verified-merge.md
  - docs/governance/incident-2026-11-26-echelon-assurance-pr3.md
  - docs/governance/rollout.md
  - docs/verified-completion-rollout.md
tags: [governance, verified-completion, trust-root, merge-gate, audit, provenance]
provenance:
  contributions:
    EXE-20261126T123453931Z-9f20ee63:
      operations: [created]
      at: 2026-11-26T13:09:55.942Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "WI-0116: enforceable governance and verified merge"
---

# Enforceable governance and verified merge

Sources: echelon-assurance WI-PRAXIS-03 (verified completion with pinned verifier keys), WI-ASR-03
(rulesets), OQ-615 (authenticated provenance), OQ-700–OQ-709 (owner resolutions DF-ASR-2026-0D11), the
incident GOV-INC-2026-001, and the existing PRAXIS-REL-005/006/009 and REL-GATE-003/004 requirements, which
this record strengthens rather than duplicates (the host-verifier path is unchanged).

Business objective for every item: autonomous engineering with deterministic governance and trustworthy
evidence, at minimal routine human effort.

| ID | Requirement | Source | Threat / failure | Mechanism · component | Validation (tests) | Evidence | Failure behaviour | Residual risk | Status |
|---|---|---|---|---|---|---|---|---|---|
| PRX-GOV-001 | Report each governance control as enforced, not enforced, unknown, platform-unavailable or not required, from the provider's API; never infer enforcement from files | OQ-700, OQ-761, WI-ASR-03, RC1 | Advisory CI mistaken for a control | `PlatformGovernance.evaluate`, `GitHubGovernance` | inspect tests (unprotected, full, spoofable check, bypass, unknown) | `praxis.governance-report/1` | Unknown = not satisfied | Admins can change rules | implemented |
| PRX-GOV-002 | Readiness ladder installed → … → operationally demonstrated, each rung requiring the previous | brief §19 | Repository reported governed because files exist | `PlatformGovernance.evaluate` | "complete ruleset … verified-completion-enabled", "#20/#21" | report `ladder` | Lower rung blocks higher | — | implemented |
| PRX-GOV-003 | Detect governance drift (a satisfied control no longer satisfied) | brief §10 | Silent weakening | `PlatformGovernance.drift` | "drift reports…" | `praxis.governance-drift/1` | Exit 3 | Detection after the fact | implemented |
| PRX-GOV-004 | A completion authority trust root on the protected branch with identities, pins, scopes, versions, validity and revocation | WI-PRAXIS-03, OQ-703, OQ-704 | Self-attestation, untrusted or revoked verifiers | `VerifierTrust` | #9, #10, #10b, trust round-trip | trust file history | Refuse receipt | Issuer key rotation (archive JWKS) | implemented |
| PRX-GOV-005 | Keyless default verifier: GitHub OIDC workload identity pinned to the protected workflow, ref and event; token audience binds the payload digest; results taken from GitHub's record of isolated per-validation jobs | OQ-702, OQ-703, OQ-706, WI-FIDES-02 | Key custody burden; candidate-controlled verifier; forged observations | `VerifierTrust.verifyWorkload`, `GovernanceBootstrap.attest` | #13c, #1b, #29/#1 | workload receipts | Refuse | A candidate can make its own validation's job succeed | implemented |
| PRX-GOV-006 | Weakening a trust root (add, replace, unrevoke, extend, widen, unversioned change) requires elevated approval bound to the exact head | brief §6, §12 | Unauthorized rotation/replacement | `VerifierTrust.weakenings`, `Authority.protectedChanges` | #12, #12b, #28 | decision `protectedChanges` | `MRG.PROTECTED_CHANGE_UNAUTHORIZED` | Owner account compromise | implemented |
| PRX-GOV-007 | Secure bootstrap: keyless proposal, owner merge establishes trust, refuses to repeat or replace | brief §7 | Agent establishing its own verifier | `GovernanceBootstrap.bootstrap` | bootstrap test | bootstrap PR, inspect reports | Refuse | First merge of the gate is not gated (stated) | implemented |
| PRX-GOV-008 | Merge only with a verified decision binding repository, work item, generation, commit, tree, policy, verifier and freshness | WI-PRAXIS-03, OQ-704, brief §8, §11 | Receipt reuse/substitution, stale validation, artifact change | `MergeAuthorization.decide`, `GovernanceOperations.authorize` | #2, #4–#7, #14, #16, #16b, #22–#24, #26, #27, tree test, Git end-to-end | `praxis.merge-decision/1` | Refused with code, remedy, resolver | Admin bypass (audited) | implemented |
| PRX-GOV-009 | Merge gate runs from the protected branch (fixed branch) with least privilege; candidate code never reaches attestation credentials or anything the gate reads; no other workflow can post the gate's check | brief §15 | Modified workflow, credential theft, check spoofing, base-branch substitution | workflow template, `GOV-GATE-WORKFLOW`, `GOV-CHECK`, `GOV-DEFAULT-TOKEN`, `GOV-CHECK-WRITERS` | #15, #29/#1, review tests | workflow artifacts | Not enforced reported | Workflow-write credentials | implemented |
| PRX-GOV-010 | Mandatory validations cannot be empty, skipped, failed, stale or unverifiable; optional executed-test count | brief §14 | Empty suites, suppressed failures | `TestExecution.gate`, verifier engine, empty-policy refusal | #2b, #3, #16 | receipts `status` | Failed | Pattern must match the runner's output | implemented |
| PRX-GOV-019 | Work is authorized only by a listed human work authorizer (unedited in-person comment or protected list) or standing authorization; repository state the candidate can change never authorizes | brief §12, §13 | Agent self-authorization via `.ros/` | `Authority.workAuthorized`, `GovernanceOperations.authorize` | Git end-to-end, review "work authorization" | decision `authorizationBasis` | `MRG.WORK_ITEM_UNAUTHORIZED` | Agents holding the owner's personal token | implemented |
| PRX-GOV-011 | Standing authorization for bounded categories; never covers protected classes | OQ-701, brief §12 | Unrestricted standing authority | `Authority.standing` | #11, #11b | decision `authorizationBasis` | `MRG.WORK_ITEM_UNAUTHORIZED` | Category design is the owner's | implemented |
| PRX-GOV-012 | Exceptions are scoped, ≤ 30 days, non-self-approved, cannot waive identity/forgery/protected changes, and never yield "verified" | brief §18, RC4 | Silent disabling of governance | `Authority.validateException` | #25, #25b | decision `waived`, `exceptions` | `MRG.EXCEPTION_INVALID` | — | implemented |
| PRX-GOV-013 | Elevated approval only from a listed human, in person (not via an application), bound to the exact head | OQ-702, OQ-706 | Agent impersonating the owner | `Authority.elevated`, `GitHubGovernance.elevatedApprovals` | #13b | decision `elevatedApprovals` | Refused | Personal access tokens given to agents | implemented |
| PRX-GOV-014 | Audit the protected branch: unverified integrations, integrated-while-active work, direct pushes, evil or unconfirmed merges, bypassed or spoofed gates, rewritten history, stranded commits | RC3, brief §10, §21 | The assurance incident class | `IntegrationAudit`, `GovernanceOperations.audit` | #30, #30b, #5c, #19/#20b, #21c | `praxis.integration-audit/1` | Exit 3 on errors | API-dependent fields need live access | implemented |
| PRX-GOV-015 | Missing completion authority diagnosis: which, why, reuse, who, how, what continues, what is blocked | brief §20 | Blocked agents improvising self-attestation | `AuthorityDiagnostics` | diagnosis test | `praxis.completion-authority-status/1` | Exit 3 | — | implemented |
| PRX-GOV-016 | Agent actions attributable to authenticated principals | OQ-615, OQ-702, OQ-706, WI-PRAXIS-01 | Self-reported identity | Workload identity for verification; platform login for approvals | #13c, #13b | decisions | — | **Agent identity on GitHub is still the owner's account; distinct App identity is an owner action** | partial |
| PRX-GOV-017 | Release integrity: release tags protected | OQ-709 | Moved or forged release tags | `GOV-RELEASE-TAGS` (opt-in in profile) | — | report | Not enforced reported | Release signing itself unchanged (existing attested native release) | partial |
| PRX-GOV-018 | Evidence delivered to immutable storage and alerting | OQ-705, brief §16 (Arca, Vigila) | Evidence loss | — | — | — | — | Arca/Vigila lack the capability; Git and artifacts are not immutable | not implemented (external dependency) |

Bidirectional trace: each row names its tests in `tests/Praxis.Tests/GovernanceTests.fs` (test names start
with `governance`); each test name carries the brief's §21 case number or "review" for a review-round regression
([review record](../../docs/governance/review-2026-11-26.md)); each component file's header names WI-0116.

## Acceptance

`PRAXIS_TEST_FILTER=governance dotnet tests/Praxis.Tests/bin/Release/net10.0/Praxis.Tests.dll` passes; the
full suite passes; `praxis governance audit --repository kemiller2002/echelon-assurance --branch origin/main`
reports the incident (verified 2026-11-26).
