# Shared native completion effects

Work: [Praxis #226](https://github.com/kemiller2002/praxis/issues/226),
[RQ-ROS-2026-A028](../research/requirements/RQ-ROS-2026-A028--local-coordinator-and-worker-handoffs.md)
and draft [DF-ROS-2026-A060](../research/decisions/DF-ROS-2026-A060--local-coordinator-with-embedded-authority.md).

`FileNativeCompletionEffects` extracts the effect tail formerly embedded in
`runWorkComplete`. The actual `praxis work complete` handler now invokes this
shared kernel after its existing quality/evidence, native recovery, checkpoint,
planning and telemetry-resolution checks. This is production CLI wiring, not
another completion lifecycle. No local worker execution route is enabled.

## Ordering and scope

The caller holds the existing work-protocol lock. Recover work-state and backlog
transactions before inspecting/planning a new completion. The effect kernel
rejects empty, duplicated or inconsistent completion plans and changed native
context before any finalization. It preserves unrelated work and requires the
original members to be active. A boundary callback allows a future qualified
local host to recheck integrated-candidate/authority evidence immediately before
effects; the ordinary CLI already supplies its native guards. Context is
reobserved after this potentially slow callback.

Finalize native telemetry using the existing repository implementation. Then
write conclusions, readiness records, completion events and context through the
existing `FileWorkContextRepository`/`WorkStateTransaction` path. Update backlog
projections through the existing `FileBacklogQueueRepository` transaction.
Research defaults to `inconclusive`, explicit conclusions remain supported,
readiness records remain on both item and event, and telemetry-disabled native
repositories retain their existing behavior.

## Recovery and limits

A failed invocation remains failed even if some bytes became readable. The
existing event/context journal replays its frozen writes without regenerating
completion events. `recoverBacklogLocked` repairs backlog projections from
already-complete native context using each item's original completion timestamp.
The CLI runs this after native/backlog transaction recovery. It never finalizes
telemetry, transitions active members, emits completion events or fabricates
missing historical timestamps. Repeated recovery preserves native event counts.

This extraction does **not** make telemetry, context and backlog one atomic
transaction. Finalization can succeed for an earlier member before another
member fails; those members' native context remains active until reconciliation.
Already-complete members are not replanned or relabeled as newly delivered.
A future local completion adapter must bind accepted integrated evidence to the
native transaction, stage/reconcile the full protected write set, and recover
uncertain effects without extending expired authority. Do not pass an earlier
read-only integration plan to this effect kernel as if it were an approval.

## Validation

Ten tests cover real native event/context/backlog effects, conclusions/readiness,
guard refusal, context changes before/during validation, malformed plans,
finalization failure, context-write failure with retained native journal, lost
backlog acknowledgement before/after persistence and repeat recovery. The
injected failure callbacks test ordering; native persistence uses real files.
Existing native completion CLI goldens and integration-to-completion planning
regressions also pass. These tests do not qualify a protected local host or
an actual model-driven offline agent loop.

No `ollama`, `llama-cli` or `llama-server` runner was found on PATH during this
slice. The actual local-model coordinator/two-worker pilot therefore remains
unrun. No model was downloaded and no hosted fallback was introduced.
