# Journaled local integration protocol

Work: [Praxis #226](https://github.com/kemiller2002/praxis/issues/226),
[RQ-ROS-2026-A028](../research/requirements/RQ-ROS-2026-A028--local-coordinator-and-worker-handoffs.md)
and draft [DF-ROS-2026-A060](../research/decisions/DF-ROS-2026-A060--local-coordinator-with-embedded-authority.md).

The isolated `LocalIntegration` protocol connects fresh submission intake,
independent candidate staging/validation, immutable host journals and an atomic
expected-head update port. It has no CLI execution route, production Git/staging
adapter or native work-completion effect. A fixture target update does not qualify
the coordinator/worker runtime.

## Identity and ordering

Acquire the repository integration lease and protected policy/native locks in
the canonical order. Native/ECIR recovery precedes every action, including
historical target observation. A bounded journal inventory refuses any other
pending integration. Partial, corrupted or misplaced records also block new
mutation; a new dispatch ID cannot bypass an unresolved transaction. The file
lease excludes cooperating controllers, so atomic Git compare-and-swap is still
required against other writers.

Fresh intake supplies the exact snapshot it checked through `readChecked`.
This prevents a second read from silently supplying a different result to
staging. The snapshot is not an approval token. Only AwaitingIntegration results
can stage; partial/failed member results refuse.

Stage the candidate in isolation against the observed destination baseline.
The qualified host's candidate validation must independently establish actual
ancestry, exact scoped changes, original member execution/requirement attribution,
artifact bytes and pinned acceptance on the **candidate**, including any merge
resolution or other work already in the destination. Worker-result validation
does not supply these candidate facts. Staging cannot update the target or native
member state. These host observations remain unqualified in this prototype.

`praxis.local-integration-plan/1` binds repository, dispatch, attempt, packet
digest, the digest of the entire archived submission, exact local branch ref,
before-commit, candidate commit and UTC preparation time. Full commit identities
and distinct before/candidate commits are required. Bounded flat JSON refuses
unsafe refs, missing/extra/duplicate fields, malformed identities and non-UTC
times. Checksums provide transport integrity, not authentication.

Check the same immutable submission and candidate, then refresh authority,
dependencies and time after potentially long validation. Exclusively create and
flush `plan.json` in a provisioned host root outside the repository. A reported
prepare failure prevents any target update, even if its bytes became readable.
Independent recovery must reconcile that uncertainty.

## Target update and recovery

Revalidate after preparation and on restart. The host must hold protected locks
and enforce receipt validity at the actual update boundary. Its update port must
atomically require the recorded baseline; read-then-unconditionally-write is
insufficient. Repository/Git pins, ref identity, commit objects, ancestry,
worktree/index state and durability remain requirements on the qualified adapter.
The mechanism lease alone does not establish these properties.

| Observed target | Action |
| --- | --- |
| Original baseline, no applied receipt | Freshly check input, candidate and authority, then attempt the expected-head update. |
| Exact candidate commit | Record or confirm historical target observation without applying again. |
| Other commit, or rollback after an applied receipt | Refuse and retain the transaction for independent reconciliation. |

After an acknowledged update, observe the exact candidate before exclusively
flushing `applied.json`. Its strict `praxis.local-integration-applied/1` shape
binds the complete plan digest and a monotonic UTC observation time. Lost target
acknowledgement or receipt-write failure remains recoverable. Readable bytes never
turn the original failed invocation into success. Restart still acquires the
lease, recovers native state and independently observes the target.

An expired/revoked grant cannot authorize another update. Observing an
already-present candidate after expiry only closes historical tracking; it does
not prove when/by whom the ref changed, renew authority, complete a member or
authorize future work. A subsequently advanced ref is conservatively refused
even if the candidate might be an ancestor. No automatic rollback or re-staging
of an immutable prepared transaction occurs.

## Verification and remaining work

Release build and 20 integration tests cover restart, before/after target and
receipt failures, uncertain preparation, revocation, expiry, changed submissions,
partial results, target drift, wrong identities/candidates, pending native/other
integration recovery, strict/partial/oversized/linked files and lease exclusion.
An actual Git fixture moves a dedicated ref with `git update-ref REF NEW OLD`,
loses the acknowledgement, and recovers without a second update. Its intake,
candidate acceptance and policy observations are synthetic. It proves expected-head
and recovery mechanics, not model quality or host authorization. Unix link tests
are conditional on Unix.

Qualified staging/candidate validators, policy/native lock composition,
production atomic Git/worktree handling, power-loss metadata durability, hostile
same-account/path races, and a transaction linking integration evidence to
existing native member completion remain pending. A read-only
[completion planning bridge](local-integrated-completion.md) now feeds exact
member evidence into the existing native planner, but supplies no completion
effect or reusable authority. Accepted checkpoint policy and
execution gates are unchanged. Actual local models, network denial and the
one-coordinator/two-worker pilot remain required before runtime activation.
