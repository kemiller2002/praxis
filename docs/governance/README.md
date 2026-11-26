---
id: GOV-IDX-001
title: Enforceable governance and verified merges
status: canonical
created: 2026-11-26
work_item: WI-0116
requirement: RQ-ROS-2026-A039
---

# Enforceable governance

Praxis distinguishes what a repository *requires* from what its platform and trust root *enforce*, gates
merges on verified evidence, and detects integrations that bypassed governance.

| Document | Content |
|---|---|
| [trust-model.md](trust-model.md) | Principals, authentication, delegation, revocation, threat model, residual risks |
| [bootstrap.md](bootstrap.md) | Completion authority specification, keyless provisioning, secure bootstrap, rotation, revocation, emergency |
| [verified-merge.md](verified-merge.md) | Receipt bindings, merge decision, lifecycle, standing authorization, exceptions, provenance, evidence |
| [incident-2026-11-26-echelon-assurance-pr3.md](incident-2026-11-26-echelon-assurance-pr3.md) | Root-cause analysis of the assurance merge |
| [rollout.md](rollout.md) | Cross-repository rollout, migration risks, human actions |
| [review-2026-11-26.md](review-2026-11-26.md) | Multiple-agent review findings and dispositions |
| [../../research/requirements/RQ-ROS-2026-A039--enforceable-governance-and-verified-merge.md](../../research/requirements/RQ-ROS-2026-A039--enforceable-governance-and-verified-merge.md) | Requirements and traceability |

## Commands

| Command | Purpose | Exit |
|---|---|---|
| `praxis governance inspect (--repository O/N \| --snapshot F) [--audit] [--save-snapshot F] [--out F] [--require LEVEL] [--json]` | Read-only platform governance evaluation and readiness ladder (`--audit` also assesses operational demonstration) | 0; 3 below `--require` |
| `praxis governance drift --baseline REPORT (--repository O/N \| --snapshot F)` | Controls satisfied in the baseline and no longer | 0 none; 3 drift |
| `praxis governance audit [--repository O/N] [--branch REF] [--limit N] [--since SHA] [--previous-head SHA] [--decisions DIR]` | Integrations without verified evidence, direct pushes, bypassed gates, rewritten history, stranded commits | 0; 3 error findings |
| `praxis governance authority [--base REF]` | Completion-authority diagnosis: what is missing, why, who, how, what may continue | 0 ready; 3 not |
| `praxis governance bootstrap --repository O/N --approved-by LOGIN --validation NAME=COMMAND…` | Write the keyless bootstrap proposal (refuses if a trust root exists) | 0; 3 refused |
| `praxis governance validations` / `observe` / `attest` | CI only: list the policy's validations (matrix); run one validation on the candidate; build receipts from this run's job conclusions and bind them to the workflow's OIDC identity | |
| `praxis merge authorize --repository O/N --candidate SHA [--base REF] [--pull-request N] [--receipts DIR]… [--live] [--evidence-out F]` | The merge decision | 0 permits; 3 refused |
| `praxis verifier run … --ephemeral-key` | Observation receipts for a later attestation step (never trusted by key) | |

Validation policies may add `testCountPattern` (regex with a named group `count`) and `minimumTests` to a
validation: an exit code of 0 without that many executed tests is a failure
(`PRX.VALIDATION.NO_TESTS_EXECUTED`).

## Readiness (machine-readable: `praxis.governance-report/1`)

`not-installed` → `installed` (Praxis on the protected branch) → `configured` (governance profile and
protected validation policy) → `validated` (the gate passed on the protected head) → `platform-enforced`
(every required platform control enforced) → `verified-completion-enabled` (trust root, policy, verified
merges required) → `operationally-demonstrated` (a verified integration and no audit errors). A level is
reached only if every lower level is; `unknown` never counts as enforced.

## Installation and adoption

Greenfield and existing repositories follow the same steps ([rollout.md](rollout.md#per-repository-procedure-agent-steps-unless-marked)).
Upgrade is additive: no state migration; the new files are user-owned protected configuration. Rollback:
remove the ruleset requirement (owner), then revert the bootstrap through an elevated change; existing claims,
receipts and completions are untouched. Release verification of Praxis itself is unchanged
(`docs/native-installation.md`).

## Operating cost (estimates, not measurements)

| Item | Cost |
|---|---|
| Setup | One agent PR plus ~5 minutes of owner time per repository |
| Routine human intervention | None for work inside authorized work items or standing categories; one comment for protected changes |
| Validation compute | One run of the repository's policy per PR update on GitHub-hosted runners (free for public repositories) plus a ~1-minute gate job |
| Signing and identity | None: GitHub OIDC, no keys, no KMS |
| Storage | Workflow artifacts (90 days) and Git history |
| Provider dependency | GitHub (rules, Actions, OIDC issuer) — already the hosting provider |

## Release notes (unreleased; for the release after 3.26.1)

- **New:** `praxis governance inspect|drift|audit|authority|bootstrap|observe|attest`, `praxis merge authorize`.
- **New:** keyless completion authority (GitHub Actions OIDC workload identity), `praxis.verifier-trust/1`
  trust root with validity, revocation, scopes and weakening analysis; pinned ECDSA keys remain supported.
- **New:** standing authorization, governance exceptions and elevated approvals bound to an exact head.
- **New:** executed-test evidence in validation policies (`testCountPattern`, `minimumTests`).
- **New:** `praxis verifier run --ephemeral-key`; the verifier engine's observation is separated from claim
  admission (`run` behaviour unchanged).
- **Compatibility:** additive. No state-schema change. `release.json` is bumped by the release workflow.
