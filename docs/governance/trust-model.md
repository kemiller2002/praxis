---
id: GOV-TRUST-001
title: Praxis trust model and threat model
status: canonical
created: 2026-11-26
work_item: WI-0117
---

# Trust model and threat model

Praxis decides consequential engineering actions (completion, merge, release) from evidence. This document
states who is trusted for what, how each principal is authenticated, and what Praxis cannot prevent.
Code: `src/Praxis.Domain/Governance/`.

## Principles applied

- A policy in Markdown is not a control. `praxis governance inspect` reports a requirement as satisfied only
  when the platform or a pinned cryptographic mechanism enforces it; an unreadable rule is `unknown`, never
  satisfied.
- A signature proves only that a key signed. A receipt counts only if its signer is pinned by the
  **protected-branch** trust root, valid at observation time, unrevoked, scoped to the policy, and the
  receipt binds the exact repository policy, work item, commit and tree.
- Technical separation is not organizational independence. A CI workload the implementer cannot alter is a
  separate *mechanism*; it is not an independent human reviewer or an external assurer, and nothing in Praxis
  says otherwise.

## Principals

| Principal | Identity | Authentication | Permitted | Cannot | Revocation | Failure behaviour |
|---|---|---|---|---|---|---|
| Organization owner | Platform account (`authorities.json`) | Platform account (GitHub login, 2FA per account policy) | Approve bootstrap; elevated approvals; exceptions; rulesets | Be impersonated by an agent's comment: approvals posted through an app are refused | Account controls | Unavailable owner blocks only elevated changes; routine work continues under standing authorization |
| Repository administrator | Platform account with admin | Platform | Change rules and settings | Change them invisibly: `governance drift` / `audit` detect weakening afterwards | Platform | Drift is reported as `GOV.DRIFT` |
| Human work authorizer | Login in `workAuthorizers` | Platform account: an unedited `/praxis authorize-work ID` comment posted in person, or an entry in the protected `.praxis/governance/work-authorizations.json` | Authorize work items | Be replaced by `.ros/` state (any change can add to it) or by an agent identity | Remove from list (elevated change) | Unauthorized work: `MRG.WORK_ITEM_UNAUTHORIZED` |
| Agent implementer | Self-reported provenance (`docs/agent-provenance.md`); platform identity is today the owner's account | Self-reported (not authentication) | Branches, commits, PRs, plain evidence, durable checkpoints | Produce trusted receipts, approve elevated changes, grant exceptions, alter the protected workflow, policy or trust root | Revoke its platform credential | Self-attestation: `MRG.RECEIPT_UNTRUSTED` |
| Automated implementer | As agent; CI actor `github-actions[bot]` | Platform (workflow identity) | As agent | As agent | Workflow change (elevated) | As agent |
| Work coordinator | Praxis claims (fenced generations) | Claim token (local) | Claim, renew, release work | Complete another's generation | Lease expiry | `PRX.CLAIM.*` |
| Verification service | Pinned ECDSA key (host) **or** GitHub OIDC workload `job_workflow_ref` of the protected merge-gate workflow | Pinned key / workload identity | Observe validations and attest receipts | Be selected or altered by the candidate (definition read from the protected branch, `pull_request_target`) | `revokedAt` in the trust root (elevated change) | No receipts: `MRG.VALIDATION_INCOMPLETE`; revoked: refused regardless of claimed time |
| Release authority | Release workflow on the protected branch | Workload | Publish releases | Release from an unprotected ref (tag rulesets: `GOV-RELEASE-TAGS`) | Workflow change | Release not attested |
| Exception authority | Login in `exceptionAuthorities` | Platform account | Grant bounded exceptions (≤ 30 days, exact heads, compensating safeguards, follow-up) | Waive identity, forgery or protected-change refusals; approve its own implementation; label a merge verified | Expiry; remove record | Invalid exception: `MRG.EXCEPTION_INVALID` |
| Hosting provider (GitHub) | — | TLS | Enforce rulesets; issue OIDC tokens; record merges | — | — | Unreadable API: controls `unknown`; issuer keys unavailable: workload receipts unverifiable (fail closed) |
| CI environment | GitHub-hosted runner, one per validation | Workload identity | Run candidate code in one `observe` job per validation, read-only token, no OIDC, CI credentials blanked in the validation's environment | Reach the attestation identity, or write anything the gate reads (the gate reads each job's conclusion from GitHub's record of the run) | — | Interrupted or cancelled job: `unavailable` receipt (refused) |
| Evidence storage | Git history + workflow artifacts (90 days) | Git object IDs | Hold receipts and decisions | Be called immutable: it is not | — | Missing evidence: `AUD.UNVERIFIED_INTEGRATION` |

**Credential lifecycle.** No long-lived signing key is required. The default verifier is the OIDC workload
identity: tokens live minutes, are minted per receipt with audience `praxis-receipt:<payload digest>`, and are
never stored as secrets. A host ECDSA key (existing `praxis verifier serve`) remains supported for
organizations that run a verifier host; its key never enters a repository or an agent environment.

**Implementers cannot control their verifier.** The verifier identity is the workflow file *on the protected
branch* at `refs/heads/<branch>` under `pull_request_target`; a candidate's modified workflow runs under
another ref or event and its tokens do not match the pin. Changing the workflow, the policy, the trust root or
the authorities is a protected change that needs an elevated approval bound to the exact head.

## Threat model

| Threat | Control | Test |
|---|---|---|
| Completion/merge without authorization | Work item must be open on the protected branch or covered by an active standing authorization | `governance #1`, `#11` |
| Skipped or empty validation | Every policy validation needs a passed receipt at one commit; empty policy refused; optional executed-test count (`testCountPattern`, `minimumTests`) turns an empty suite into a failure | `#2`, `#2b`, `#3` |
| Receipt reuse / substitution (repo, commit, work item, generation, policy) | Signed bindings: policy digest (names the repository), work item, generation, commit, tree; ancestor + exempt-path rule; generation consistency | `#4`–`#7`, `#14` |
| Forged or untrusted verifier, agent self-attestation | Pinned trust root on the protected branch; signature verified before parsing; tree cross-check | `#8`, `#8b` (mutation), `#9`, `#13`, `#13c` |
| Revoked / expired verifier | Revocation refuses all receipts; retirement only those after `validUntil` | `#10`, `#10b` |
| Trust-root rotation or replacement by an agent | Weakening analysis + protected-path classification + elevated approval bound to head | `#12`, `#28` |
| Modified policy / workflow / build configuration | Protected classes (autonomy floor + governance paths, CI-referenced scripts) | `#14`, `#15`, property test |
| Suppressed failures, interrupted runs | Latest receipt per validation must pass; `unavailable` refused; truncated receipts refused | `#16`, `#16b`, `#22`, `#23` |
| Changes between validation and merge, stale base | Candidate must contain the target head; post-verification changes allowed only under `.ros/`, `.praxis/receipts/`, `.praxis/evidence/` | `#27`, Git end-to-end test |
| Direct push, force push, admin bypass | Rulesets (inspected), audit findings `AUD.DIRECT_PUSH`, `AUD.HISTORY_REWRITTEN`, `AUD.GATE_REFUSED_BUT_MERGED`, `AUD.GATE_SOURCE_UNTRUSTED` | `#19`–`#21c` |
| Spoofed required check | **Partly a platform limit (personal accounts):** a same-named job in a candidate-added workflow also comes from GitHub Actions; preventive only if agents lack workflow-write permission, detected by the audit (`AUD.GATE_SOURCE_UNTRUSTED`), reported as `GOV-GATE-EXCLUSIVE`. Also: required check pinned to GitHub Actions' integration; gate workflow only under `pull_request_target` for the protected branch; default workflow token read-only (`GOV-DEFAULT-TOKEN`); no other pull-request workflow may write checks (`GOV-CHECK-WRITERS`) | inspect and review tests |
| Gate run against an attacker-chosen base branch or a tag shadowing it | Branch filter; fixed, fully qualified `--base`; tags never read as the protected branch; live cross-check of the platform's branch head | `#29`, review and round-2 tests |
| Self-authorized work via `.ros/` state | Authorization only from listed authorizers (comment or protected file) | Git end-to-end test |
| Evil merge / forged "merge" commit | Audit re-merges parents (`git merge-tree`) and, live, confirms the platform's `merge_commit_sha`; the committer name is never trusted | `#5c`, `#20b` |
| Candidate code reaching attestation credentials | `observe` job: read-only token, no secret, no `id-token`; gate job runs no candidate code | `#29` (template assertions) |
| Unauthorized exceptions | Exception validation (authority, not agent, not implementer, ≤ 30 days, exact heads, non-waivable codes) | `#25`, `#25b` |
| The assurance incident | Audit | `#30`, `#30b` |

## Residual risks and trust boundaries (stated, not solved)

1. **Administrators.** A repository administrator can disable rulesets or merge with a bypass the platform
   allows. Praxis detects this afterwards (`drift`, `audit`); it cannot prevent it.
2. **Agents act as the owner on GitHub.** Today agent pull requests are authored by the owner's account. The
   platform cannot separate them, the owner cannot approve such a PR, and an app-posted comment is the only
   distinguishing signal (`performed_via_github_app`); edited comments are refused because any writer can edit
   another's comment. A token that lets an agent post as the owner *without* an app (a personal access token)
   would defeat elevated approval and work authorization. Mitigation: never give agents a personal token;
   provision a distinct GitHub App identity for agents (OQ-706), after which native PR reviews can replace
   comment commands.
3. **Candidate code in `observe`.** Each validation runs in its own job on its own runner, and the gate takes
   each result from GitHub's record of that job's conclusion, so candidate code cannot forge *another*
   validation's result or the attestation. It can still make the job that runs it succeed (a test that exits 0
   regardless, or a printed test count): a validation is only as trustworthy as the code it executes. Oracle
   tests can be pinned (`protectedPaths`, checked before candidate code runs), and changes to tests, test
   projects and CI scripts are protected classes. The executed-test count catches empty suites, not
   adversaries. Third-party actions are pinned by tag, not commit SHA.
4. **Workflow-write credentials (most important residual on personal accounts).** An agent credential with
   workflow permission can add a workflow whose job is named like the gate; GitHub then shows a passing
   same-named check from GitHub Actions, and personal accounts have no rule that requires a specific workflow.
   It cannot produce a valid workload receipt or a `verified` decision, and the audit reports the foreign check,
   but the platform may accept the merge. Grant agents no workflow permission; an organization account can
   require the workflow itself.
5. **Issuer key rotation.** Workload receipts are verified against GitHub's published keys. After GitHub
   retires a key, old receipts can be re-verified only against an archived JWKS (`--jwks`). Decision records
   in the evidence artifact keep the result.
6. **Evidence storage.** Git history and workflow artifacts (90-day retention) are not immutable storage.
   Integration with Arca (immutable storage) and Vigila (alerting) is not available: those systems do not yet
   provide the capability (echelon-assurance capability findings).
7. **Personal-account limits.** No audit log, no push restrictions, no required workflows, no merge queue on
   personal repositories. Moving to an organization account removes several of these limits (owner decision;
   no purchase is implied by this document).
