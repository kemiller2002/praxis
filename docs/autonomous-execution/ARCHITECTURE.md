# Autonomous execution: architecture

Work item: WI-0104. Contracts: [EXECUTION-POLICY.md](EXECUTION-POLICY.md). Why: [GITHUB-APPROVAL-DIAGNOSIS.md](GITHUB-APPROVAL-DIAGNOSIS.md).

## Responsibilities

No new service was introduced. Each system does the part it already owns.

| System | Owns | Delivered (WI-0104 and siblings) |
|---|---|---|
| **Praxis** | Authorization and the merge decision | `praxis.autonomous-execution-policy/1`; owner-signed `praxis.run-authorization/1`; the fail-closed evaluator; effect-based protected-change classification; the deterministic independent review; the journaled, SHA-pinned GitHub merge lifecycle with reconciliation; chained merge receipts; `praxis autonomy …` |
| **Conditor** | Environment readiness | `conditor unattended-readiness` (WI-0062): a real probe of branch → commit → PR → CI → review → Praxis → merge → verified receipt, with the receipt `conditor.unattended-readiness/1`. It makes no authorization decision of its own. |
| **Dokimos** | Verification evidence | The quality-gate `revision` input (WI-0014), which binds evidence to the candidate head. Dokimos never decides authorization. |
| **Praxis planner** (Ordo's role) | When dependent work is executable | `praxis autonomy observations` turns verified receipts into the planner's existing `pull-request-merged` and `commit-merged` observations. `praxis plan analyze --observations` releases the dependents. Ordo is not a scheduler: its `Obligation.DependsOn` is documented as "not enough to become a scheduler", and its `StepLedger.nextAction` only awaits dependencies. So no Ordo code changed, and no authorization logic moved into Ordo. |
| **GitHub** | Mechanics | Pull requests, Actions check runs, the merge API with `sha` pinning, and (by owner decision) rulesets. |

## Trust boundaries

```
 owner ──(P-256 private key, never in any repo)──► signs run authorization
   │
   └─► trust store (public keys, revoked runs)  ── outside every repository the run edits
                                                    (Praxis refuses one inside)

 implementing agent (session, token = owner's login via the "claude" app)
   │  can: push branches, open PRs, call praxis
   │  cannot: produce the owner's signature; create a github-actions check run
   ▼
 GitHub ──► Actions (github-actions app identity, fresh runner)
              ├─ CI job               ──► check run "build-and-test"   (app: github-actions)
              └─ independent-review   ──► check run "independent-review" (app: github-actions)
                   runs praxis autonomy review against the BASE branch's policy
   │
   ▼
 praxis autonomy merge (any executor)
   reads: the policy at the target branch's head (never the candidate), the candidate's files,
          the base workflows (CI-referenced scripts), revocations (base + candidate), check runs
          at the candidate SHA with their producing app, branch rules
   decides: ALLOW / DENY / REQUIRES_OWNER (pure evaluator)
   acts: PUT merge {sha: candidate}; verifies the merge on GitHub; writes a chained receipt
```

What each boundary guarantees, and what it does not:

1. **Authorization is unforgeable by the agent.** The agent cannot produce a signature that verifies against the owner key in a trust store it cannot edit.
2. **Evidence is unforgeable by the agent's token.** A check run counts only when GitHub reports that the `github-actions` app created it at the candidate SHA. Runs created through the agent's own app are rejected (`*-untrusted-producer`).
3. **The candidate cannot weaken its own verifier without the owner.** Workflows, the policy, CI-invoked scripts, test deletions and test projects are protected (REQUIRES_OWNER). The policy and workflows are read from the base, not from the candidate.
4. **Not guaranteed today:** that the agent goes through Praxis at all. GitHub enforces no rule on these repositories, and the token is admin. Making the gate mandatory is diagnosis decision D-2, which is owner-controlled. See [SECURITY-REVIEW.md](SECURITY-REVIEW.md).

## Lifecycle state

Each pull request's journal lives under the run's journal directory (`--journal DIR`). That directory is committed with the run's evidence:

```
journal/
  pulls/<owner>_<repo>/pr-<n>.json   phase: not-started | submitted(candidate, attempt, decisionDigest) | completed(mergeCommit, receiptDigest)
  decisions/<sha256>.json            every decision, content-addressed
  receipts/<sha256>.json             praxis.merge-receipt/1, content-addressed
  receipts/chain.jsonl               digest + previous link per receipt
  events.jsonl                       append-only: decisions, retries, submissions, reconciliations, outcomes
```

The journal is the durable checkpoint. A restarted executor first reconciles it with GitHub, and only then acts.

| Situation | Behaviour |
|---|---|
| A submission was journaled and GitHub shows the candidate merged | Adopted and receipted (`reconciled: true`). Never resubmitted. |
| Already completed | ALREADY_MERGED (idempotent). |
| Merged by anything else | `foreign-merge`. Never claimed. |

## Retry and recovery

| Failure | Classification | Behaviour |
|---|---|---|
| HTTP 5xx, 429, 408, rate limit, network error | transient | Retried with backoff `base × 2^n`, up to `transientRetryLimit`. Every retry is recorded. |
| Merge call fails transiently | transient | Reconcile first; adopt if merged; otherwise retry within the bound. |
| 409 on the merge (head moved) | head changed | REVERIFY. A new candidate needs new CI and a new review. |
| `mergeable_state` dirty or behind | needs a new candidate | REVERIFY (the conflict is resolved on a new candidate). |
| Checks pending or missing | waiting | Poll until `checksTimeoutMinutes`, then DENY with the findings. |
| A check failed, or the review failed | deterministic | DENY. Never retried as transient. |
| Required human review, draft, unreadable blocking rules | GitHub-imposed | BLOCKED. Reported; never bypassed. |

Praxis never force-pushes, never updates a branch on its own, and never builds its own merge engine.
