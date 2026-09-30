# Merge readiness and lifecycle-dependent CI requirements

Status: **Accepted by repository owner on 2026-09-30; implementation tracked by GH-127.**

## Objective

Praxis distinguishes productive development from integration readiness. An
in-progress branch may have failing CI while work is incomplete. A branch or
commit must not be represented as merge-ready until every configured required
check succeeds against the exact integration candidate.

This policy is orthogonal to work-item lifecycle and durable continuity:
checkpointing proves recoverability; work completion proves the item's
acceptance contract; merge readiness proves the exact candidate is eligible
for integration.

## Requirements

- **PRX-MRG-001** An in-progress branch MAY have failing CI.
- **PRX-MRG-002** Intermediate CI failures MUST remain visible and truthful.
  Praxis, agents and CI adapters MUST NOT convert a required failing check to
  success merely because the branch is in progress.
- **PRX-MRG-003** A known intermediate failure SHOULD be recorded at the next
  durable handoff/checkpoint boundary with the failing check, known reason and
  intended resolution. An unexplained failure remains an explicit unknown.
- **PRX-MRG-004** A durable checkpoint proves recoverability only. It MUST NOT
  imply successful CI, review readiness or merge readiness.
- **PRX-MRG-005** Work-item completion MUST NOT imply that the containing
  branch is merge-ready.
- **PRX-MRG-006** A merge-ready decision MUST be bound to one exact candidate
  commit.
- **PRX-MRG-007** Every configured required check MUST have a successful
  observation bound to that same candidate commit.
- **PRX-MRG-008** Failed, pending, cancelled, skipped, missing and unknown
  required-check states MUST all block merge readiness.
- **PRX-MRG-009** Duplicate or contradictory evidence for a required check
  MUST fail closed.
- **PRX-MRG-010** Any meaningful commit after readiness was established makes
  the prior readiness evidence stale. The new candidate MUST be evaluated
  independently.
- **PRX-MRG-011** When configured, a merge-ready candidate MUST have a clean
  meaningful working tree and MUST be the current remote integration
  candidate.
- **PRX-MRG-012** A known or explained failure MAY coexist with active
  development or a durable checkpoint, but MUST still block merge readiness
  when the failing check is required.
- **PRX-MRG-013** Optional checks MUST be declared by repository policy.
  Agents MUST NOT waive a required check ad hoc or through narrative.
- **PRX-MRG-014** Merge-readiness decisions MUST be provider-neutral.
  Provider adapters normalize CI/check results into the Praxis check-state
  vocabulary; provider-specific APIs and result names MUST NOT enter the
  Domain model.
- **PRX-MRG-015** The merge-readiness command MUST be read-only and
  deterministic for identical policy, Git observations and normalized
  evidence.
- **PRX-MRG-016** Praxis MUST report the reasons a candidate is not ready,
  including stale evidence, missing checks, non-success states, dirty state
  and an unverified remote candidate.
- **PRX-MRG-017** A repository MAY leave merge-readiness enforcement disabled
  for backward compatibility. Enabling it MUST name the required semantic
  checks explicitly.
- **PRX-MRG-018** CI SHOULD expose one aggregate merge gate that runs even
  when prerequisite jobs fail or are cancelled and succeeds only when every
  configured required check succeeded for the exact candidate.
- **PRX-MRG-019** An aggregate gate MUST NOT be silently skipped merely
  because an upstream required job failed.
- **PRX-MRG-020** Repository branch/ruleset protection SHOULD require the
  aggregate merge gate where the hosting platform supports required status
  checks.
- **PRX-MRG-021** Agents SHOULD continue useful bounded work after expected
  intermediate failures rather than polling or repairing every incremental
  commit solely to make it temporarily green.
- **PRX-MRG-022** Final integration validation happens at the final candidate
  boundary. Existing CI batching policy still applies; a final candidate is
  not ready until its authoritative checks have completed successfully.
- **PRX-MRG-023** Required-check evidence MUST distinguish unavailable or
  unknown from zero/success. No missing observation is success.
- **PRX-MRG-024** The greenfield starter SHOULD enable merge readiness with
  its repository-validation check so new repositories inherit the semantic
  distinction.
- **PRX-MRG-025** Existing repositories that do not configure
  `mergeReadiness` retain their current behavior and receive no inferred
  required-check policy.

## Initial CLI

```text
praxis merge readiness [--evidence FILE] [--json]
```

The evidence document is provider-normalized:

```json
{
  "schema": "praxis.merge-readiness/1",
  "candidateCommit": "<full commit>",
  "remoteCandidateCurrent": true,
  "checks": [
    {
      "id": "repository-validation",
      "state": "succeeded",
      "commit": "<same full commit>"
    }
  ]
}
```

The command exits 0 for a ready candidate or a repository that has not enabled
the policy, 1 for a candidate that is not merge-ready or unreadable evidence,
and 2 for invalid arguments.

## Required tests

Tests MUST establish at least:

1. all required successful checks on the exact clean candidate produce ready;
2. every non-success required-check state blocks;
3. a missing, duplicated or unbound required check fails closed;
4. evidence for an earlier commit becomes stale after a later commit;
5. optional-check failure does not block;
6. checkpoint/recoverability facts cannot substitute for CI evidence;
7. dirty meaningful state blocks when policy requires clean state;
8. the CLI is read-only;
9. normalized JSON round-trips or parses deterministically;
10. the aggregate CI gate runs after failed prerequisites and fails rather
    than skipping.
