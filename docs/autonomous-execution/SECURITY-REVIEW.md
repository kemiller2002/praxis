# Security review

Work item: WI-0104.

**How it was done.** A separate agent ran the adversarial review independently, from the code alone. It was given the attack goals but not the implementer's conclusions, made no GitHub calls, and worked in a copy of the repository. It wrote 14 proof-of-concept tests. Each one passes only if the unsafe outcome actually happens:

- 11 demonstrated a weakness;
- 3 confirmed properties held.

Every weakness that can be fixed in code was fixed, and each has a regression test (`autonomy security F…` in `tests/Praxis.Tests/AutonomyTests.fs`). One critical finding cannot be closed in code; it is stated plainly below together with the owner decision that closes it.

## Threat model and trust boundaries

| Threat | Control | Status |
|---|---|---|
| Agent privilege escalation (merging without authority) | Owner-signed run authorization, fail-closed evaluator, scope in **both** the policy and the authorization | Implemented. **See F1:** this is only as strong as the executor's trust anchor. |
| Policy modification | Policy read from the base, never from the candidate; its digest is signed; `.praxis/autonomy/**` is REQUIRES_OWNER | Implemented and verified (real-GitHub scenario "Policy file changed") |
| Workflow modification | `.github/workflows/**`, `.github/actions/**`, `**/action.y{a,}ml`, local-action directories and CI-invoked scripts are REQUIRES_OWNER | Implemented and verified ("Workflow security weakened", "Verification script changed") |
| Credential theft | No token is logged or journaled; the trust store holds public keys only (private keys are refused); key, `.env` and `.pem` files are REQUIRES_OWNER; the review flags secret material | Implemented. The reviewer could not extract a credential. |
| Unauthorized branch merges | Target-branch globs in both the policy and the authorization | Verified ("Wrong target branch") |
| Forged review evidence | Only check runs created by the `github-actions` app count; runs of the same name from two suites are ambiguous | Implemented (F3 fixed). See residual R-2. |
| Forged verification evidence | Same rule as review evidence; evidence must be at the exact candidate SHA | Implemented |
| Stale SHA reuse | The merge call pins `sha`; a moved head means REVERIFY; evidence must be at the candidate | Verified ("Candidate SHA changed") |
| Repository substitution | The repository must be in both the policy and the authorization; the policy is read from that repository's base | Verified ("Wrong repository") |
| Authorization replay | Expiry, maximum duration, run-id revocation, and a signed binding to the policy digest and repositories | Verified ("Expired", "Revoked") |
| Merge API race conditions | SHA-pinned merge; the base head is re-checked just before the merge (F10); journal-first submission; reconciliation | Implemented. Verified: lost response reconciled, duplicate idempotent. |
| Malicious PR content | The review is deterministic, with no model in the loop. The PR's own workflow changes are REQUIRES_OWNER, so a PR cannot change its verifier unattended. | Implemented |
| Compromised dependency | Lock files, package manifests, package sources, `vendor/**` and `.gitmodules` are REQUIRES_OWNER (F4) | Implemented |
| Prompt injection through repository files | No language model judges eligibility. The evaluator and the review are deterministic. | By design |

**Trust boundaries.**

- The owner's private key never enters any repository or session.
- The trust store must be outside the edited repository, with symlinks resolved (F2), or come from a CI secret (`--trust-store-env`).
- Evidence is trusted only when it comes from the `github-actions` identity, through workflows read from the base.

See [ARCHITECTURE.md](ARCHITECTURE.md).

## Findings and resolution

| # | Finding | Severity | Resolution |
|---|---|---|---|
| **F1** | The executor chooses the trust store. An agent that runs `praxis autonomy merge` itself can generate a key, write a trust store naming itself the owner outside the repository, and sign its own authorization. | **critical** | **Not closable in code while the agent controls the executor.** Fully closed by owner decisions **D-1 + D-2 + D-7**: run the gate as a GitHub Actions job (`praxis autonomy evaluate … --trust-store-env PRAXIS_AUTONOMY_TRUST`, with the owner's trust store in an Actions secret the agent cannot read or write), and require that job's check on the target branch through a ruleset. GitHub then enforces the owner's key. `--trust-store-env` was added for this. Until then, Praxis's gate binds a cooperating agent only (see the diagnosis, "Consequence for security"). |
| F2 | The trust-store location check could be bypassed with a symlink | medium | **Fixed.** Every path component is resolved before the check (`AutonomyStore.realPath`). Test F2. |
| F3 | The newest check run of a name won, so a later same-name run (from another workflow, or posted through the API) masked a failed run | high | **Fixed.** Runs are grouped by check suite: one suite, newest run (a legitimate re-run passes); two suites, `*-ambiguous`, DENY. Test F3. |
| F4 | Weakening by effect was missed: CI scripts reached through `working-directory`, workspace prefixes or local actions; scripts called by scripts; MSBuild and package config; case variants | high | **Fixed.** The CI-reference closure follows working directories, prefixes, `uses: ./dir` and script-to-script calls. The floor adds `*.props`, `*.targets`, `*.rsp`, `*.sln(x)`, `Makefile`, `package.json`, lock files, `vendor/**`, `.gitmodules`, `.config/dotnet-tools.json` and `dependabot.yml`. Matching is case-insensitive. Tests F4 (two). |
| F5 | The review missed Expecto `ftest…`, `ptest…` and `skiptest` | medium | **Fixed.** Test F5. |
| F6 | Any id-shaped token satisfied the requirement trace | low | **Fixed** where the base has a Praxis backlog: the trace must name an id in the base's `.ros/work/queue.json`. Without a backlog, the format check remains (residual R-4). Test F6. |
| F7 | A committed revocation that failed strict parsing was silently ignored | high | **Fixed.** An unreadable revocation denies every run (`revocation-unreadable`). Test F7. |
| F8 | The trust store, `--revocations` and the signature were read once, so a revocation made during the checks wait was never seen | high | **Fixed.** The authority is re-read at every evaluation, including the one just before the merge. Test F8. |
| F9 | Listings over 3000 entries and truncated trees were silently partial | medium | **Fixed.** Both are refused. Test F9. |
| F10 | The base could move between the final decision and the merge call | low | **Fixed** (narrowed): the base head is re-read just before the merge; if it moved, the decision is made again. Test F10. A sub-second window remains, because GitHub's merge API cannot pin the base. |

## Residual risks (open, stated)

| # | Risk | Mitigation, or the decision that closes it |
|---|---|---|
| R-1 | F1. Without D-2 and D-7 the gate is advisory: the agent's admin token can merge or push directly, bypassing Praxis entirely. | Owner decisions D-2 and D-7. Nothing in this work weakens GitHub controls. |
| R-2 | A workflow on any branch with `checks: write` can post a `github-actions` check run at the candidate SHA. If GitHub places it in the same suite as the real run, F3's grouping cannot tell them apart. | Pushing such a workflow to the base is REQUIRES_OWNER. Pushing it to another branch needs only push rights, which the agent has. A ruleset "required workflow" (D-2) pins the producing workflow. |
| R-3 | A test file **modified** to assert nothing (a "gutted" test) is ALLOWED and passes the deterministic review. | Making every test edit owner-controlled would stop routine work. Optional decision D-5 (a semantic reviewer) addresses it. Coverage ratchets in Dokimos are another route. |
| R-4 | In repositories without a Praxis backlog, the trace check is format-only. | Install Praxis in the target, or supply known ids. |
| R-5 | Files that CI reads as data (not as scripts), and source project files (`*.fsproj` outside tests), are not protected. | Protecting them would make routine work owner-controlled. Repository policies can declare them in `protectedPaths`. |
| R-6 | Identity: GitHub records the agent's merges and reviews as the owner's login. | Praxis receipts record the run and the authorization digest. An owner review of an agent-opened PR stays impossible (diagnosis item 13). Owner decisions keep the rehearsal-006 bot-PR attestation. |

## Properties tested and found to hold

The reviewer tried to break these and could not:

- **Signatures:** a scope, owner or repository tampered after signing fails; a private key in the trust store is refused; one owner's key cannot sign as another; a malleated (high-S) signature gains nothing, because the authorization digest is over the payload; the length-framed, sorted payload blocks collisions.
- **Policy:** it is read from the base head, its digest must match the signed one, and a non-unattended policy is denied.
- **Candidate and branch:** a moved head is denied and the merge pins the SHA; an uppercase SHA is denied; `Main` versus `main` is out of scope.
- **Checks:** a check produced by the agent's own app is rejected.
- **Classification:** rename-from a protected path, nested `action.yaml`, `.env.*` and the policy directory are all caught.
- **Reconciliation:** it never double-merges and never claims a merge it did not make; the receipt chain detects an edit or a removal.

## Verification executed

| Check | Result |
|---|---|
| Regression tests F2 to F10 (10 tests) | Pass (48 of 48 in `AutonomyTests`) |
| The reviewer's PoCs before the fixes | 11 of 11 demonstrated the weaknesses (`/tmp` copy; source summarized here) |
| Real-GitHub negative suite on the fixed code (Praxis `c768861`) | 18 of 18 passed. Conditor readiness READY (PR #11). See INTEGRATION-TEST-RESULTS.md, second run. |
