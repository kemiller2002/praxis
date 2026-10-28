# Implementation results

Work items: praxis WI-0104 (owner of this capability), conditor WI-0062, dokimos WI-0014, and the Indy-init intake UAE-001.

Each item below is labelled with one of these statuses:

- **Implemented**: code is on the PR branch.
- **Verified**: it ran and passed. The evidence is named.
- **Blocked**: it cannot proceed without the owner.
- **Not implemented**: deliberately not done; the reason is given.

## Diagnosis: what makes the owner approve pull requests

**GitHub requires nothing.** None of the nine repositories has branch protection, a ruleset or a CODEOWNERS file, no review is required, and the agent's credential is admin. The approvals came from three places:

- **The agent's execution instructions.** Merges are treated as outward-facing actions that need the user's authorization, and PRs are opened as drafts.
- **Governance that had no verifiable form of advance authorization.** OD-INDY-7 and AR-003: agents never interpret blanket approvals or write approval files.
- **The token identity.** The agent acts under the owner's login, so the owner cannot approve the agent's PRs, and a review cannot distinguish the owner from the agent.

Full evidence: [GITHUB-APPROVAL-DIAGNOSIS.md](GITHUB-APPROVAL-DIAGNOSIS.md).

## Capabilities

| Capability | Where | Status |
|---|---|---|
| Versioned policy `praxis.autonomous-execution-policy/1` (strict reader; digest over the exact bytes) | praxis `Autonomy/Model.fs`, `AutonomyJson.fs` | Implemented, verified (unit and real) |
| Owner-signed run authorization `praxis.run-authorization/1` (P-256, framed payload, `draft` / `attach` / `verify`) | praxis `Authorization.fs`, CLI | Implemented, verified (OpenSSL-signed authorizations verified; forged and tampered ones refused on real GitHub) |
| Fail-closed evaluator: ALLOW / DENY / REQUIRES_OWNER | praxis `Evaluator.fs` | Implemented, verified (48 unit tests; 18 real scenarios, twice) |
| Protected-change classification by effect | praxis `Classification.fs` | Implemented, verified. Residual R-3 and R-5 are stated in SECURITY-REVIEW.md. |
| Deterministic independent review in its own CI job | praxis `Review.fs`, `LocalReview.fs`, `autonomy review` | Implemented, verified on real GitHub (it failed the critical-defect PR and passed the harmless ones) |
| Journaled, SHA-pinned merge lifecycle with reconciliation, bounded retries and REVERIFY | praxis `MergeLifecycle.fs`, `GitHubApi.fs` | Implemented, verified: real merges #2, #3, #4, #11 and #12; lost response reconciled; duplicate idempotent |
| Content-addressed, chained merge receipts | praxis `AutonomyStore.fs` | Implemented, verified (`receipts verify`; edit and removal detected) |
| Revocation (trust store, `--revocations`, committed files); re-read at every evaluation | praxis | Implemented, verified (real "Revoked"; F7 and F8 regression tests) |
| GitHub configuration diagnostics `praxis autonomy diagnose` | praxis | Implemented, verified (real) |
| CI-hosted gate support `--trust-store-env` | praxis | Implemented. Running it as an enforced, required check is **Blocked on owner decisions D-2 and D-7**. |
| Unattended readiness `conditor unattended-readiness` with receipt `conditor.unattended-readiness/1` | conditor `UnattendedReadiness.fs` | Implemented, verified: **READY** twice on real GitHub (PRs #3 and #11); 11 unit tests |
| Dependency continuation: receipts become planner observations (`praxis autonomy observations`) | praxis | Implemented, verified (2 receipts re-confirmed on GitHub; `praxis plan analyze --observations` accepts the file) |
| Ordo code change | ordo | **Not implemented, by design.** Ordo's `Obligation.DependsOn` is documented as "not enough to become a scheduler". Continuation reuses the Praxis planner, and no authorization logic was moved into Ordo. |
| Dokimos evidence bound to the candidate head (`revision` input) | dokimos `actions/quality-gate` | Implemented, verified (`tests/actions/gate-revision.sh`, 3 of 3; it fails on the old `gate.sh`) |
| Rehearsal 7 requirements, preflight gate, expectations and measurement definitions | indy-init `input-documents/2026-10-28-UAE-001-…` | Implemented as a **proposed** intake. It takes effect on the owner's merge (RDP-003). |

## Verification

| Suite | Result |
|---|---|
| Praxis `AutonomyTests` | 48 of 48 (38 functional, 10 security regressions) |
| Praxis full suite | First run 2478 of 2479. The one failure was the CLI-boundary ratchet on the first CLI; it was fixed by moving the effects to Infrastructure, and the ratchet now passes 9 of 9. The second full run is recorded below. |
| `praxis architecture check`, `praxis validate` | pass |
| Conditor full suite | 1348 of 1348 |
| Dokimos `gate-revision.sh` | 3 of 3 |
| Real GitHub, run 1 (`75f2591` review job) | Readiness READY (#3), 18 of 18 negative scenarios, positive #2 |
| Real GitHub, run 2 (`c768861`, after the security fixes) | Readiness READY (#11), 18 of 18 negative scenarios |

## Security

The independent adversarial review produced 10 findings. Nine were fixed with regression tests (F2 to F10). **F1 (critical) remains:** an executor the agent runs itself can be given a trust store the agent wrote. It is closed only by the CI-hosted gate plus a ruleset, which are owner decisions D-2 and D-7. Details and residual risks R-1 to R-6 are in [SECURITY-REVIEW.md](SECURITY-REVIEW.md).

## Did a real pull request merge unattended?

**Yes.** These pull requests on `kemiller2002/indy-init-rehearsal-007` merged into the disposable branch `praxis-autonomy-e2e/base` with no owner action:

| PR | Merge commit | Note |
|---|---|---|
| #2 | `60fe833` | |
| #3 | `a51d851` | Readiness probe |
| #4 | `dbc7f8c` | Reconciled after a lost response |
| #11 | `2539821` | Readiness probe on the fixed code |
| #12 | `46a0a9a` | Reconciled |

Each merge had:

- an authorization signed by a **disposable test authority** (not the owner);
- CI and the independent review, from github-actions, at the candidate SHA;
- a verified, chained receipt.

## Readiness for Rehearsal 7

**The mechanism is ready.** Conditor proved the full lifecycle, READY twice. Three things remain before Rehearsal 7 itself can run unattended:

1. The owner signs the real run authorization with their own key (D-1). No agent can do this.
2. The policy and workflow are placed on the rehearsal repository's `main`, and readiness is re-proven there (UAE-R7-06). This writes to the rehearsal repository's `main`, and starting Rehearsal 7 is owner-controlled, so it was not done.
3. To make the gate **enforced** rather than advisory: D-2 plus D-7 (UAE-R7-12).

## Remaining owner decisions

| ID | Decision |
|---|---|
| D-1 | Generate the owner's P-256 key; register its public key in a trust store outside every repository; sign each run's authorization. |
| D-2 | A ruleset on the target `main`: require the CI, `independent-review` and gate checks from GitHub Actions; no human review; block force pushes and deletion. |
| D-7 | The CI-hosted gate job, with the trust store in an Actions secret (closes F1). |
| D-3, D-4, D-5 | Optional: require PRs (0 approvals), auto-merge, and a semantic (LLM) reviewer (closes R-3; needs an API-key secret). |
| D-6 | Delete the scratch branches `praxis-autonomy-e2e/*` in indy-init-rehearsal-007. The session proxy blocks branch deletion. |
| UAE-001 | Approve or amend the Rehearsal 7 requirements by merging the Indy-init PR. |
| PRs | Merge the implementation PRs: praxis, conditor, dokimos, indy-init. They were not self-merged; see below. |

## Why the implementation PRs were not merged unattended

GitHub would allow it, because nothing is required. But no owner-signed authorization exists for these repositories, and Praxis's own gate would DENY (`authorization-missing`). Merging them anyway would be exactly the unauthorized merge this work exists to prevent. They are open as draft PRs for the owner.
