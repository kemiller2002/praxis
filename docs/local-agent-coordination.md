# Local agent coordination target

Work: [Praxis #226](https://github.com/kemiller2002/praxis/issues/226), extending
[#220](https://github.com/kemiller2002/praxis/issues/220).
Requirement: [RQ-ROS-2026-A028](../research/requirements/RQ-ROS-2026-A028--local-coordinator-and-worker-handoffs.md).
Proposal: [DF-ROS-2026-A060](../research/decisions/DF-ROS-2026-A060--local-coordinator-with-embedded-authority.md).

One local coordinating agent plans the blueprint and groups. It proposes
bounded assignments to the local Praxis controller. The controller checks
operator-delegated authority and launches worker sessions. Workers return
committed artifacts and evidence. The controller validates those results and
integrates accepted changes, retaining each member's own state and evidence.

## Responsibilities

| Part | Responsibility |
| --- | --- |
| Operator | Defines the local delegation, allowed decisions, resources and budgets; resolves work outside those bounds. |
| Coordinating agent | Covers all original requirements, groups related work, proposes decisions and packets, replans affected work. |
| Local controller | Checks authority and immutable identity, reserves attempts, schedules dependencies, supervises local processes, observes results and serializes integration. |
| Worker agent | Implements its exact assignment in an isolated worktree and returns a structured result with member evidence. |
| Independent validators | Compile, test, check architecture/source conservation and enforce the frozen acceptance obligations. |

All orchestration, policy and state can live on one machine. The controller
embeds host authority; it requires no hosted service, web login or always-on
daemon. Actual protections must prevent the models/workers from rewriting its
policy or obtaining signing credentials. Same-account unrestricted fixtures
cannot claim this protection. Fides/Arca integration and remote publication
are optional later extensions.

## Handoff contract to implement

The draft packet/result schemas will be versioned and decoded strictly.
They extend native execution/work identities rather than inventing another
work-item system. Model prose is supplementary.

| Packet fields | Meaning |
| --- | --- |
| schemaVersion; dispatchId; attemptId | Version and stable dispatch/retry identity. |
| parentExecutionId; childExecutionId; workerId | Observed coordinator-to-worker lineage and target identity. |
| repositoryIdentity; sourceCommit; sourceManifestDigest; blueprintDigest | Exact immutable input and repository scope. |
| groupId; cohortId; members; originalRequirementKeys; requiredDecisionIds | Exact approved assignment and source conservation. |
| prerequisiteEvidence; mutationBoundary; acceptanceObligations | Dependency evidence, allowed resource scope and independent verification requirements. |
| authorityRevision; receiptReference; expiresAt; budgets | Current delegated authority, receipt binding, deadline and bounded resources. |

Results echo the packet identity/revision and supply output commit, changed
resource inventory, evidence references and per-member outcomes. These are
claims until the controller observes the actual worktree/commit and validator
outputs. A result is never a new authority, command or executable selection.
Workers cannot merge to the integration branch or blanket-complete a cohort.

## Bounded implementation

1. Add strict typed packets/results and delegation validation with a read-only
   plan/explanation. Reuse WorkGroups/GroupExecutions and Ordo semantics.
2. Add the embedded protected local controller adapter and explicit local
   checkpoint profile. Keep remote-verified checkpoint semantics distinct.
3. Adapt ExecutionService/FileExecutionPorts: explicit executable/argv,
   per-worker worktree, bounded scheduling, persistent reservation and observed
   containment. The existing shell launcher is not a sufficient packet trust
   boundary; never interpolate model output into it.
4. Validate results, integrate against the expected baseline, and record native
   member transitions/evidence. Recover or refuse pending work/ECIR journals
   before new dispatch; reconcile uncertain starts instead of blind retry.
5. Demonstrate one coordinator and two real workers on a small dependency-aware
   cohort, with outbound network denied, no Git remote, interface compilation,
   independent acceptance, scope/expiry/revocation tests and injected failures.

## Local and offline

Local orchestration needs no external coordination service. Fully offline
operation additionally needs a local model runtime and preinstalled pinned
Ordo/Dokimos/build dependencies. Codex/Claude-style CLIs may invoke remote
models: local processes alone do not prove no external services. A strict
profile denies networking and refuses a network-dependent adapter. Backend
selection and actual model quality/throughput remain unqualified.

## Current status

Work-group planning, execution envelopes, worktrees, role launchers, ECIR
receipt verification and staging/recovery primitives exist. The complete
coordinator-to-workers loop, local delegation adapter, strict packet schemas
and local checkpoint profile do not yet exist. This plan opens no execution
gates and changes no accepted governance. Prior CI success qualifies its
existing code, not this future offline pilot.
