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

## Typed handoff contract

The first implementation slice supplies versioned domain records and strict
JSON decoding in `LocalAgentHandoff` and `LocalAgentHandoffJson`. These are pure
transport and validation contracts; they do not launch workers, establish host
authority, persist attempts or integrate commits. They extend native
execution/work identities. Model prose is supplementary.

| Packet fields | Meaning |
| --- | --- |
| schemaVersion; dispatchId; attemptId | Version and stable dispatch/retry identity. |
| parentExecutionId; childExecutionId; workerId | Observed coordinator-to-worker lineage and target identity. |
| repositoryIdentity; sourceCommit; sourceManifestDigest; blueprintDigest | Exact immutable input and repository scope. |
| groupId; cohortId; members; decisionIds | Exact assignment; each member retains workItemId, native executionId and original requirementKeys. |
| prerequisites; allowedPaths; acceptance | Dependency evidence, allowed resource scope and independent verification requirements. |
| authorityRevision; receiptDigest; issuedAt; expiresAt; timeoutSeconds; maxOutputBytes | Current delegated authority, receipt binding, deadline and bounded resources. |

Results echo dispatch, attempt, worker and child identities and the canonical
packet digest. They supply the output commit, global changed resources and
per-member native execution identity, changed resources, evidence and outcome. These are
claims until the controller observes the actual worktree/commit and validator
outputs. A result is never a new authority, command or executable selection.
Workers cannot merge to the integration branch or blanket-complete a cohort.

The v1 schemas are `praxis.local-worker-packet/1` and
`praxis.local-worker-result/1`. JSON objects require exactly their declared
fields, including nested members, obligations and evidence. Duplicate fields,
unknown fields, malformed types and `completed` outcomes are refused. Documents
are bounded to 65,536 UTF-8 bytes, nesting to 12 and arrays to 256 elements.
Commit identities are full lowercase SHA-1 Git hashes; digests are lowercase
`sha256:` identities. Timestamps explicitly name UTC; validity lasts at most
24 hours. Per-attempt timeout and output limits are bounded.

Packet fingerprints use versioned UTF-8 byte-length framing and canonical set
ordering. The fingerprint binds every assignment field, including native
member executions; it supplies identity, not approval. Fresh controller
observations must match the exact delegated packet, current authority revision,
validity window and prerequisite evidence at packet and result boundaries.

Resource scope in v1 is an exact list of portable relative paths, with no globs,
traversal or case ambiguity. Git and controller state paths are excluded.
Implementation workers need a mutation boundary; review and verification workers
are read-only. Administration and integration are controller responsibilities.
These lexical checks do not establish filesystem containment: a future host
adapter must observe links, actual changes and protected authority itself.

Result checking requires actual commit/ancestry and changed-path observations,
per-member execution and file attribution, observed artifact digests and pinned
independent acceptance results. Member file attribution must cover the global
inventory. A fully submitted result can reach only `AwaitingIntegration`;
blocked/failed members retain their claims and admit `NoIntegration`. Neither
outcome completes work. The observations are trusted controller inputs that the
future adapter must authenticate, not worker-supplied approval fields.

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
coordinator-to-workers loop, protected local delegation adapter, read-only
explanation, durable dispatch journal and local checkpoint profile do not yet
exist. Typed packet/result contracts and pure adversarial intake checks are now
implemented. No adapter authenticates the controller observations yet. This plan opens no execution
gates and changes no accepted governance. Local verification: the tests project
and CLI dependencies build in Release with zero warnings/errors; 20 local
handoff tests and 37 existing ECIR tests pass. Registry check and native Praxis
validation cover the accompanying records. These checks do not qualify the
future offline pilot.
