# Integrated local results and native completion planning

Work: [Praxis #226](https://github.com/kemiller2002/praxis/issues/226),
[RQ-ROS-2026-A028](../research/requirements/RQ-ROS-2026-A028--local-coordinator-and-worker-handoffs.md)
and draft [DF-ROS-2026-A060](../research/decisions/DF-ROS-2026-A060--local-coordinator-with-embedded-authority.md).

`LocalIntegratedCompletion.plan` is a read-only bridge into the existing
`WorkOperations.planVerifiedContext` planner. It exposes no native write,
telemetry mutation, integration update or process-launch port. Its returned
plan is ephemeral inspection output, not a transferable approval. The local
runtime remains disabled and accepted checkpoint policy remains unchanged.

## Required observations

Acquire the integration observation lease, with qualified hosts responsible for
protected policy/native/repository lock composition. Recover native state and
refuse any pending integration before planning completion. Load and validate the
exact applied journal for the requested repository, dispatch and target ref.
The entire immutable archive and packet must still match the plan. A historical
applied receipt alone is insufficient: fresh intake must independently verify
the worker result, current authority, prerequisites and delegation time window.

Revalidate the actual integrated candidate, not merely the worker output commit.
The host's `CandidateEvidence` observation must return native evidence for each
original member, derived from independently verified candidate artifacts and
pinned validators. It must bind actual artifact bytes, original requirement
coverage and execution attribution to this exact candidate. It cannot merely
rename worker assertions into native evidence. This production observer remains
unqualified; the fixtures supply synthetic candidate observations.

Observe native work context, repository identity and active execution ownership.
Every original member must be active, linked to its delegated native execution,
and have exactly that execution currently active. Historical links can remain;
a different active execution or multiple active executions refuse. Duplicate
native context IDs, disabled telemetry and unrelated repository identity refuse.
No detached execution is synthesized or substituted.

## Preserve the native planner and gates

Run the existing accepted checkpoint guard for the exact member set. Judge each
member's quality evidence separately. Feed each member's evidence and changed
paths to the existing verified context planner, retaining native work-type
evidence requirements, evidence-path observations and telemetry-finalization
intents. Member A's evidence cannot satisfy member B's required evidence types.
Malformed, empty, missing, extra or duplicate candidate evidence refuses. Native
dirty-baseline overlap refuses instead of silently dropping member attribution.

Compose the in-memory plans only after every member passes. There is no partial
member completion on failure because no writer exists. After potentially slow
candidate/evidence/quality/checkpoint checks, independently reobserve native
context and active execution ownership, the exact target candidate, and fresh
intake authority/dependencies/time. Non-UTC or backwards clocks refuse. Expired
or revoked delegations cannot use previously applied tracking to plan a new
completion. Already-complete members conservatively refuse; this is not an
idempotent completion-effect or recovery implementation.

## Remaining effect boundary

A future native effect adapter must repeat the binding and gates while holding
the existing native transaction locks, use existing telemetry finalization and
work-state recovery, attach integration identity to native completion evidence,
and reconcile interrupted effects without duplicated lifecycle events. Do not
apply this returned plan later through a raw context writer or skip native
checkpoint/quality/ECIR guards. No CLI execution route is added here.

The current tests use real journal/archive stores and the real native planner,
but synthetic authority, candidate validators, native observations and quality
and checkpoint host ports. They establish composition/refusal behavior, not
production host authorization, transaction durability or offline agent quality.
Protected host qualification and the actual one-coordinator/two-worker local
model pilot under outbound-network denial remain required.

## Recorded validation for this slice

Release build passed with no warnings or errors. All 12 integrated-completion
tests, 20 integration-recovery tests, 28 completion-obligation tests, 7 native
`work complete` CLI tests, 13 ECIR-host tests and 3 architecture tests passed.
Registry rebuild and native repository validation passed in a fresh checkout.
This is targeted validation, not a claim that the entire test suite ran locally.

The prior checkout's native provenance command crashed with a stack overflow in
`RegistryLock.acquire` while a pre-existing work-protocol lock carried PID 2
metadata. That lock was not deleted or overwritten. A fresh checkout of the same
branch, carrying the exact implementation and native step state, successfully
recorded provenance and validated. `WI-0086` captures investigation of this
host/runtime lock-recovery defect; its root cause is not established. This
recovery does not qualify the production local-host adapter.
