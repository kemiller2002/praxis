# Enforced worker checklists

Work: [GH-234](https://github.com/kemiller2002/praxis/issues/234).
Decision: [DF-ROS-2026-A067](../research/decisions/DF-ROS-2026-A067--independent-worker-checklist-gates.md).

The local controller generates a checklist from the exact delegated packet.
Every member must satisfy build, tests, architecture and scope checks, plus
every assigned acceptance obligation. The packet already requires every
assigned requirement to have acceptance coverage. An agent cannot choose fewer
checks, remove a member, borrow a sibling's evidence or supply a pass through
worker stdout. Diagnostic compilation stays available.

`handoff explain --packet FILE --json` exposes `checklistRequirements` per
assignment. Text output prints `REQUIRED` rows. This is read-only proposal
inspection: every row is `unobserved`, and system validators are unknown until
the protected controller loads policy. Inspection never authorizes execution.

## Enforcement and freshness

`LocalSubmissionIntake.readChecked` first rejects invalid submissions using the
existing identity, authority, prerequisite, path and independent acceptance
checks. A package eligible for integration must additionally pass
`WorkerChecklistOperations.verify`. The controller refreshes candidate inputs
and validator policy after checking, then refreshes worker observations,
authority, prerequisites and time. Failed and blocked packages remain
inspectable as `NoIntegration`; they cannot authorize integration.

`LocalIntegration` checks the staged integration candidate, not just the worker
output commit, before preparing or applying its journal. Prepared recovery
repeats those checks before a new target update. Historical observation that a
target was already applied may close tracking after expiry without granting
new mutation authority. `LocalIntegratedCompletion` checks the applied candidate
again before planning native completion. Its existing native quality,
checkpoint, member-execution and takeover checks remain mandatory.

Checklist identity binds the packet digest (including requirements, decisions,
scope, execution identities and authority), exact candidate commit, candidate
input-manifest digest, complete validator map, policy revision and generated
rows. Each host-observed run binds that identity, validator identity/input
digest, UTC execution times and evidence digest. Test rows require a positive
executed-test count. Missing, duplicate, unexpected, failed, skipped,
unavailable, future, stale and mismatched observations refuse. There is no
worker decoder for a checklist pass and no self-approval option.

## Protected-host contract and limits

This first slice supplies pure rules, controller ports, mandatory local
intake/integration/completion gates, inspection and adversarial tests. It does
**not** yet provide concrete authenticated build/test/architecture runners,
protected result storage or a production controller entry point. The ordinary
standalone `work complete` CLI still uses its existing completion-readiness
policy; this change does not silently replace that migration contract.

Host implementations must establish the following independently:

- Observe an actual candidate Git commit and complete reproducible input
  closure, including source, requirements, decisions, configuration, generated
  inputs, dependencies and validator/tool versions. A digest of a worker claim
  is insufficient. Reject unavailable inputs or concurrent candidate writes.
- Select validators and policy outside model authority. Acceptance validator
  identities and input digests must match the signed assignment.
- Evaluate checks on that exact candidate; authenticate immutable evidence
  outside worker-writable storage. `WorkerCheckRun` is a typed host observation,
  not cryptographic authentication by itself. An evidence digest's syntax
  check cannot prove its contents or origin.
- Hold and coordinate native/integration/policy/input leases across observation
  and effects. Before/after reads detect observed drift; they do not establish
  atomicity against writers that ignore the lease protocol. The existing
  execution-envelope lifecycle serialization follow-up remains necessary.
- Recheck inside the native completion effect guard. A successful planning
  record is ephemeral, not an authorization token for a later write.
- Bound validator runtime, cancellation, output and observation size, and
  report unavailable checks instead of inventing results.

All new behavior is F# and provider/OS-neutral; tests use synthetic protected
observer results alongside the existing real file-store and Git integration
fixtures. These tests exercise refusal and wiring, not a qualified live model
worker. Equivalent OS isolation remains deferred. Compiler-derived F# tier
analysis, native CLI adoption and the coordinator/two-worker pilot are separate
remaining slices of GH-234.

## Executed verification

On 2026-10-11, the Release solution build completed with zero warnings/errors;
all 1,985 non-browser regression tests passed. The narrower checklist,
submission, integration, completion, handoff and architecture filters also
passed (119 test executions). Registry, repository validation, architecture
and diff checks passed. Linux/Windows/macOS checklist and handoff CI is
configured; cross-platform and browser CI results remain pending publication.
These results validate the implemented library gates, not concrete trusted
runners or a live model-worker pilot.
