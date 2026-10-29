# Integration test results

Work item: WI-0104 (with conditor WI-0062). Run on 2026-10-28, 22:00 to 23:10 UTC, against **real GitHub**: the repository's real rules, real Actions CI, the real merge API and real check-run provenance. The raw evidence is under [`evidence/2026-10-28/`](evidence/2026-10-28/).

## Environment

| Item | Value |
|---|---|
| Repository | `kemiller2002/indy-init-rehearsal-007` |
| Target branch | `praxis-autonomy-e2e/base`, a disposable branch. `main` was never touched. |
| Why a branch, not a new repository | The session's credential cannot create repositories (HTTP 403 "Resource not accessible by integration"), and no approved disposable repository existed. A scratch branch in the rehearsal repository tests the same repository-level rules: none, verified by `praxis autonomy diagnose`. |
| Base configuration | Commit `3572ca4576c1`: `.praxis/autonomy/policy.json` (digest `sha256:5ee1faae…25600`), workflow `praxis-autonomy-e2e.yml` (jobs `build-and-test` and `independent-review`; the latter builds Praxis at `75f2591` and runs `praxis autonomy review`), and `tests/verify.sh`. |
| Authority | A **disposable test authority**, not the owner: owner `e2e-test-authority`, key `e2e-test-authority-20261028`. It is an ephemeral P-256 key kept outside every repository; its public key is in `evidence/2026-10-28/trust.json`, and its private key was not committed. It proves the mechanism. A real run needs the owner's own key (decision D-1). |
| Authorization | `RUN-E2E-20261028-01`, digest `sha256:a4583bf7…341fb`, 20 hours, signed with `openssl dgst -sha256 -sign` and verified by Praxis. |
| GitHub credential | The session's GitHub App token (user `kemiller2002`, app `claude`, admin). |

## Positive: a real pull request merged with no owner action

| PR | Path | Candidate | Merge commit | Result |
|---|---|---|---|---|
| #2 | `praxis autonomy merge` run by hand | `8f2b2f4cd0f3` | `60fe833ca2eb` | MERGED. It waited while `build-and-test`/`independent-review` were pending (5 DENY-pending decisions), then ALLOW. GitHub shows `merged: true` and the base branch `identical` to `60fe833`. |
| #3 | `conditor unattended-readiness` (full probe) | `0857a901d1b0` | `a51d851d0bd4` | **READY**, all 13 applicable checks passed (see below). |
| #4 | Negative suite, lost-response scenario | `78d5a5f59b6a` | `dbc7f8c52710` | MERGED by reconciliation (`reconciled: true`, attempts 2). |

Receipt chain (`positive-journal`): 2 receipts, intact.

- `sha256:3fc1e9b9…398c` (#2)
- `sha256:0746c679…4503` (#3)

Each receipt binds:

- the authorization digest, the policy digest and the candidate;
- both check runs (`build-and-test`, `independent-review`), each with `app: github-actions` at the candidate head;
- the merge commit, and `targetContainsMerge: true`.

### `conditor unattended-readiness` (PR #3), receipt `sha256:5b13aff4…9e`

| Check | Result |
|---|---|
| UR-01 Praxis available | passed (praxis 3.23.0) |
| UR-02 GitHub authentication and permissions | passed (push) |
| UR-03 Branch-rule compatibility | passed: no required review, no code-owner review, rules readable |
| UR-04 Policy on the target branch | passed (`sha256:5ee1faae…`) |
| UR-05 Required CI checks declared and present | passed |
| UR-06 Valid run authorization | passed |
| UR-07 Revocation effective | passed: a revoked run is refused |
| UR-08 Branch, commit, push, PR | passed (#3 at `0857a901`) |
| UR-09 CI, independent review, Praxis authorization, merge | passed (`a51d851d`) |
| UR-10 Merge verified on GitHub | passed (merged; the base branch contains it) |
| UR-11 Evidence bound to the candidate | passed (2 github-actions checks at the candidate, including independent-review) |
| UR-12 Receipt persisted, chain intact | passed (2 receipts) |
| UR-13 Recovery: repeated request | passed (ALREADY_MERGED, same merge commit) |
| UR-14 Deployment permissions | not applicable (no deployment authorized) |

The first readiness attempt reported **BLOCKED**, correctly, not READY: UR-08 failed because a single-branch clone did not create `origin/<base>`. Conditor was fixed (explicit refspec) and re-run. Receipt `readiness-receipt-01.json` is kept.

## Negative: `tests/integration/autonomy/github-negative.fsx`, 18 of 18 passed

Results: `evidence/2026-10-28/negative-results.json`.

**Pull requests opened.** #4 (harmless), #5 (fails CI), #6 (critical defect: a fake private-key header), #7 (edits the policy), #8 (weakens the workflow), #9 (adds a permission file), #10 (edits `tests/verify.sh`, which CI runs).

**Pull requests closed unmerged** after the suite: #5 to #10.

| Scenario | Expected | Actual |
|---|---|---|
| Missing required review (evaluated before checks ran) | DENY | DENY `required-check-missing, independent-review-missing` |
| No run authorization | DENY | DENY `authorization-missing` |
| Expired authorization | DENY | DENY `authorization-expired` |
| Revoked authorization | DENY | DENY `authorization-revoked` |
| Wrong repository | DENY | DENY `repository-out-of-scope` |
| Wrong target branch | DENY | DENY `target-branch-out-of-scope` |
| Forged authorization (key not in the trust store) | DENY | DENY `authorization-invalid` |
| Tampered authorization (scope edited after signing) | DENY | DENY `authorization-invalid` |
| Failed CI (#5) | DENY | DENY `required-check-failed` |
| Critical defect open (#6, independent review failed on `secret-material`) | DENY | DENY `independent-review-failed` |
| Policy file changed (#7) | REQUIRES_OWNER | REQUIRES_OWNER `protected-change:execution_policy` |
| Workflow security weakened (#8) | REQUIRES_OWNER | REQUIRES_OWNER `protected-change:github_workflows` |
| Permission expansion (#9) | REQUIRES_OWNER | REQUIRES_OWNER `protected-change:permission_expansion` |
| Verification script changed (#10; protected by effect) | REQUIRES_OWNER | REQUIRES_OWNER `protected-change:verification_policy` |
| Candidate SHA changed (a commit after verification) | REVERIFY | REVERIFY `candidate-changed` |
| Merge completed remotely, response lost | reconcile | MERGED. A forwarding proxy performed the real `PUT …/merge` and answered 502. Praxis recorded the retry, reconciled from GitHub and adopted `dbc7f8c`. It did not resubmit. |
| Lost response recorded as reconciled | `reconciled: true` | true |
| Duplicate merge request | idempotent | ALREADY_MERGED |

## Second run, after the security fixes (Praxis `c768861`)

All of the following were re-run against the same disposable base, with the independent-review job pinned to `c768861`. That commit fixes security findings F2 to F10 (see SECURITY-REVIEW.md). Evidence: `evidence/2026-10-28/rerun-c768861/`.

| Run | Result |
|---|---|
| `conditor unattended-readiness` | **READY**, every applicable check passed. PR #11 (`eef26ff91921`) merged as `2539821443de`. The receipt chain is now 3 receipts, intact. A repeated request returned ALREADY_MERGED. |
| Negative suite | **18 of 18 passed** with the same expectations as the first run. PRs #12 to #18. #12 merged by reconciliation as `46a0a9a113a8` (lost response); #13 to #18 were closed unmerged. |

## Not executed against real GitHub, and why

| Scenario | Why | Where it is covered |
|---|---|---|
| GitHub requires human review → BLOCKED | Creating a review-requiring rule is an owner-controlled change (branch protection / rulesets). It was not made. | Unit tests: `Lifecycle.eligibility` (required reviews → BLOCKED `github-requires-human-review`, unreadable rules → BLOCKED), and the lifecycle test proving no merge call is made. Conditor test: UR-03 BLOCKED with no merge. `praxis autonomy diagnose` reads the rule live. |
| Transient GitHub failure (5xx on reads) | Cannot be induced on GitHub. | Unit test with bounded retries (two 502s then success; 50 failures → `FAILED github-unavailable`). The lost-response scenario above exercised a real injected 502 on the merge call. |

## Unit and repository tests

| Suite | Result |
|---|---|
| Praxis `AutonomyTests` | 38 of 38 pass. A deliberate mutation that disabled the revocation check failed 2 tests, as it should. |
| Praxis full suite | 2489 of 2489 at `c768861` |
| Conditor full suite, including 11 `UnattendedReadinessTests` | 1348 pass, 0 fail. The new tests caught one real verdict bug: an earlier failure was reported NOT_RUN instead of BLOCKED. It was fixed before commit. |
| Dokimos `tests/actions/gate-revision.sh` | 3 of 3. It fails against the previous `gate.sh`, as it should. |
| `praxis architecture check`, `praxis validate` | pass |
