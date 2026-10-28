# Autonomous execution policy and run authorization

Work item: WI-0104. This page is the authoritative reference for the contracts. The implementation is in `src/Praxis.Domain/Autonomy/` (pure) and `src/Praxis.Infrastructure/Autonomy/` (effects), and the CLI is `praxis autonomy`.

## The model in one paragraph

The owner authorizes a run **once**, by signing a run authorization with a P-256 key that only the owner holds. For every merge-related operation of that run, Praxis then evaluates four things together:

1. the **policy** on the target branch;
2. the **verified authorization**;
3. the **candidate's changes**;
4. the **evidence** GitHub reports for that exact candidate SHA.

The answer is `ALLOW`, `DENY` or `REQUIRES_OWNER`. GitHub's permission to merge is necessary but never sufficient. The evaluation fails closed: anything missing, unreadable, unverified, expired, revoked or out of scope is `DENY`.

## 1. `praxis.autonomous-execution-policy/1`

Location: `.praxis/autonomy/policy.json` in the **target repository**. For each evaluation it is read from the **head of the target branch** (the protected base), never from the candidate. A candidate therefore cannot authorize itself.

The policy's digest is `sha256:` over the exact file bytes. The run authorization signs that digest, so a later edit to the policy breaks every existing authorization (`authorization-policy-mismatch`).

```json
{
  "schema": "praxis.autonomous-execution-policy/1",
  "description": "optional",
  "policyVersion": 1,
  "execution": { "mode": "unattended" },
  "scope": {
    "repositories": [ "owner/repo" ],
    "targetBranches": [ "main" ],
    "allowedOperations": [ "create_branch", "commit", "create_pull_request", "run_verification",
                           "request_automated_review", "merge_eligible_pull_request" ]
  },
  "pullRequests": { "mergeStrategy": "squash" },
  "mergeRequirements": {
    "requiredChecks": [ "build-and-test" ],
    "checkProducer": "github-actions",
    "independentReviewCheck": "independent-review",
    "maxCriticalDefects": 0
  },
  "protectedPaths": [ { "pattern": "requirements/**", "class": "frozen_requirements" } ],
  "failureHandling": { "transientRetryLimit": 3, "retryBaseDelaySeconds": 2, "checksTimeoutMinutes": 30 },
  "authorization": { "maxHours": 24 }
}
```

### Reading rules

The reader allows only the members shown. It refuses any unknown, duplicate or missing member, and it supplies no defaults.

| Member | Meaning and allowed values |
|---|---|
| `policyVersion` | Must be `1`. |
| `execution.mode` | `unattended` or `attended`. An `attended` policy denies every unattended operation (`policy-not-unattended`). |
| `scope.*` | Each list must be non-empty. Target branches are globs. Operations: `create_branch`, `commit`, `create_pull_request`, `run_verification`, `request_automated_review`, `merge_eligible_pull_request`, `deploy`. |
| `mergeRequirements.requiredChecks` | Check-run names. Each must have concluded `success` **at the candidate SHA** and been produced by `checkProducer`. |
| `mergeRequirements.checkProducer` | The GitHub App slug that must have created the check runs, normally `github-actions`. A check run that another app or token created never counts, including the implementing agent's own (`app = claude`). |
| `mergeRequirements.independentReviewCheck` | The independent-review check run (section 5). Same rules as the required checks. |
| `mergeRequirements.maxCriticalDefects` | Must be `0` in version 1. |
| `protectedPaths` | `{pattern, class}` pairs added to the built-in floor (section 3). Classes: `frozen_requirements`, `acceptance_criteria`, `verification_policy`, `security_policy`, `execution_policy`, `github_workflows`, `repository_rules`, `credentials`, `permission_expansion`, `infrastructure`. |
| `failureHandling.transientRetryLimit` | 0 to 10. Bounded retries of transient GitHub failures, with backoff `retryBaseDelaySeconds × 2^attempt`. |
| `failureHandling.checksTimeoutMinutes` | How long the lifecycle waits for checks that have not finished. After that, the result is `DENY` with the pending findings. |
| `authorization.maxHours` | 1 to 168. The longest authorization this policy accepts. |

## 2. `praxis.run-authorization/1`

This is the owner's one-time authorization of a run.

```json
{
  "schema": "praxis.run-authorization/1",
  "runId": "RUN-…", "owner": "kemiller2002", "keyId": "owner-key-1",
  "issuedAt": "2026-10-28T22:00:00Z", "expiresAt": "2026-10-29T18:00:00Z",
  "policyVersion": 1, "policyDigest": "sha256:…",
  "repositories": [ "owner/repo" ], "targetBranches": [ "main" ],
  "permittedOperations": [ "merge_eligible_pull_request", … ],
  "protectedOperations": [ "frozen_requirements", …every class… ],
  "signature": "base64 ECDSA P-256 signature (P1363 or DER accepted)"
}
```

### Signing

The signed bytes are a framed payload. It follows the same scheme as `ecir.approval/1` (`EcirApprovals.signingPayload`):

- every field is UTF-8 byte-length framed;
- every list is sorted ordinally and prefixed with its count.

Producing and signing an authorization:

```bash
praxis autonomy authorize draft --policy policy.json --run-id RUN-1 --owner kemiller2002 --key-id owner-key-1 \
  --repository owner/repo --branch main --operation merge_eligible_pull_request … --hours 24 --out auth.draft.json
openssl dgst -sha256 -sign owner.key -out auth.sig auth.draft.json.payload     # the owner, with their own key
praxis autonomy authorize attach --draft auth.draft.json --signature auth.sig --out authorization.json
praxis autonomy authorize verify --authorization authorization.json --trust-store ~/.praxis/autonomy-trust.json --policy policy.json
```

### Trust store

The trust store, `praxis.autonomy-trust/1`, holds `{ keyId, owner, publicKeyPem }` signers and owner-revoked `revokedRunIds`.

It must live **outside every repository the run edits**. Praxis refuses a trust store inside the repository root or the working directory, because whoever can write the trust store decides whose signature counts. Only public keys are accepted.

### Evaluation rules (`Evaluator.evaluate`)

| Gate (DENY) | Code |
|---|---|
| No policy, or an unreadable policy | `policy-unavailable` |
| Policy mode is not `unattended` | `policy-not-unattended` |
| No authorization | `authorization-missing` |
| Signature does not verify, the key is not in the trust store, or the key belongs to another owner | `authorization-invalid` |
| Authorization signed for another policy digest or version | `authorization-policy-mismatch` / `-version` |
| Not yet valid / expired / longer than `maxHours` | `authorization-not-yet-valid` / `-expired` / `-duration` |
| Revoked | `authorization-revoked` |
| The owner did not reserve every protected class | `authorization-protected-incomplete` |
| Repository, target branch or operation not in **both** the policy and the authorization | `repository-out-of-scope` / `target-branch-out-of-scope` / `operation-not-permitted` |
| Candidate is not a full SHA | `candidate-malformed` |

A gate failure is always `DENY`. It is never `REQUIRES_OWNER`, so a run without valid authority is not told it merely needs the owner.

### Revocation

A run is revoked if any of these name its run id:

- the trust store's `revokedRunIds`;
- a `praxis.run-revocation/1` file in a `--revocations DIR`;
- a revocation file committed under `.praxis/autonomy/revocations/` on the target branch **or** in the candidate.

A revocation only removes authority, so one is honoured wherever it appears and whoever wrote it. Revocation is checked at every evaluation, including the one immediately before each merge submission.

## 3. Protected changes (REQUIRES_OWNER)

Changes are classified **by effect**, not only by location (`Classification.classify`).

**Built-in floor.** A policy cannot remove these:

| Paths | Class |
|---|---|
| `.praxis/autonomy/**` | execution policy |
| `.github/workflows/**`, `.github/actions/**`, `**/action.y{a,}ml` | workflows |
| `CODEOWNERS`, `.github/CODEOWNERS`, `docs/CODEOWNERS`, `.github/settings.yml` | repository rules |
| `ros.json`, `global.json`, `**/Directory.Build.{props,targets}` | verification policy |
| `NuGet.config`, `**/nuget.config`, `**/.npmrc`, `aegis-boundaries.json` | security policy |
| `**/*.{pem,key,pfx,p12}`, `**/.env`, `**/.env.*` | credentials |

**By effect** (verification policy):

- deleting a test, or moving a test out of the test suite;
- changing a test project;
- changing any script that the **protected base's** CI workflows invoke. These are found by reading the base's workflow text and matching tracked script paths.

**Policy-declared paths.** The policy can add more, for example frozen requirements, acceptance criteria and permission files.

If no gate fails and any change is protected, the result is `REQUIRES_OWNER`. The candidate is not merged unattended.

## 4. Evidence (for `merge_eligible_pull_request`)

- The pull request's current head must equal the candidate. Otherwise the result is `candidate-changed`, and the lifecycle answers `REVERIFY`.
- Every required check, and the independent-review check, must be `completed/success` **at the candidate SHA** from the trusted producer. Codes for anything else:
  - `*-missing` / `*-pending` (the lifecycle waits up to `checksTimeoutMinutes`);
  - `*-failed` (deterministic: never retried);
  - `*-untrusted-producer`.
- Evidence for another commit does not count. Evidence is never carried from one candidate to another.

## 5. Independent review

`praxis autonomy review --base REV --head REV --policy BASE-POLICY [--event FILE]` runs in **its own CI job**: a fresh runner, the `github-actions` identity, and the policy from the base branch. It never runs in the implementer's session.

Findings:

| Severity | Codes | Effect |
|---|---|---|
| critical | `secret-material`, `conflict-marker`, `disables-test`, `removes-test` | Fails the review. |
| blocking | `requirements-trace-missing` (no WI-/RQ-/REQ-/DEC-/AR-/… reference or `Work-Item:` trailer), `outside-scope` (with `--allowed-path`) | Fails the review. |
| major | `untested-change`, `large-change` | Recorded only. |
| owner | `protected:*` | Recorded. REQUIRES_OWNER is the evaluator's decision. |

The check run's conclusion is the evidence the evaluator reads. The job uploads the review JSON (`praxis.independent-review/1`) as an artifact. The review never approves anything, and it is never represented as a human review.

## 6. Merge lifecycle (`praxis autonomy merge`)

```
reconcile journal with GitHub ─┬─ already receipted ───────────────► ALREADY_MERGED (idempotent)
                               ├─ submitted, merged remotely ──────► verify + receipt (reconciled)
                               ├─ merged outside the lifecycle ────► FAILED foreign-merge (never claimed)
                               └─ open ─► gather (base policy, files, base workflows, revocations, checks, rules)
                                          ─► evaluate ─┬─ REQUIRES_OWNER ─► stop
                                                       ├─ DENY (waiting) ─► poll until checksTimeoutMinutes
                                                       ├─ DENY ───────────► stop
                                                       └─ ALLOW ─► GitHub eligibility ─┬─ BLOCKED (human review, draft, …)
                                                                                       ├─ REVERIFY (conflict, behind)
                                                                                       └─ journal "submitted"
                                                                                          ─► PUT merge {sha: candidate}
                                                                                          ─► verify merged + target contains it
                                                                                          ─► receipt ─► journal "completed"
```

- The merge call carries `sha = candidate`. If the head moved, GitHub refuses, and the result is `REVERIFY`.
- A transient failure of the merge call is retried only after reconciling first. If GitHub merged, the merge is adopted, never resubmitted.
- Every attempt, retry, decision and outcome is appended to `journal/events.jsonl`.
- Decisions are stored content-addressed under `journal/decisions/`.

## 7. Merge receipts

`praxis.merge-receipt/1` binds:

- the run id and the authorization digest (the owner-signed bytes);
- the policy version and digest;
- the repository, pull request, target branch and candidate SHA;
- the merge method;
- the ALLOW decision's digest;
- the check runs used (id, app, head SHA, conclusion);
- the merge commit, the target head at verification, and `targetContainsMerge`;
- the attempts, `reconciled`, the previous receipt's digest, and the time.

Receipts are content-addressed (`sha256` over key-sorted compact JSON, `CanonicalJson`) and chained in `journal/receipts/chain.jsonl`. `praxis autonomy receipts verify --journal DIR` re-derives every address and link. An edited, removed or reordered receipt is reported.

## 8. Exit codes

| Code | Meaning |
|---|---|
| 0 | ALLOW, MERGED, ALREADY_MERGED, or valid. |
| 3 | DENY, REQUIRES_OWNER, BLOCKED, REVERIFY or FAILED. The JSON says which. |
| 2 | Invalid arguments. |
| 6 | A prerequisite is unusable (trust store, policy file). |
