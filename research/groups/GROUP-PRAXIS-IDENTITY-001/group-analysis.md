# Group analysis: GROUP-PRAXIS-IDENTITY-001

- Group: `GROUP-PRAXIS-IDENTITY-001` (`./praxis work group show GROUP-PRAXIS-IDENTITY-001`)
- Members: `PRAXIS-ID-01`, `PRAXIS-ID-02`, `PRAXIS-ID-03`, `PRAXIS-ID-04`, `PRAXIS-ID-05`, `PRAXIS-ID-06`
- Execution repository: `kemiller2002/praxis`
- Base commit: `f242aae664b4601e106b6e33ebce2b95bedfae35` (PR #177 merge)
- Source: requirements audit of 2026-10-06, track IDENTITY (PRX-REMOTE-045..050 and the
  implementation-order amendment of GitHub issue #90; DER-01, 16, 17, 19, 20, 22..26 of
  `requirements/PRAXIS-DUAL-ENTRY-RECONCILIATION.md`; PRX-PLAN-182).

The machine-checkable form is [`group-analysis.json`](group-analysis.json) (`praxis.group-analysis/1`).

## 1. Members

| Member | Obligation | Acceptance criteria | Depends on |
|---|---|---|---|
| PRAXIS-ID-01 | Typed repository identity and canonical work-item identity (PRX-REMOTE-045, 046, 048, 049, 050) | Enumerated in its description (`./praxis work context PRAXIS-ID-01`); 8 criteria | - |
| PRAXIS-ID-02 | Remote protocol 1.4 structured references (PRX-REMOTE-047, order amendment) | 7 criteria | PRAXIS-ID-01 |
| PRAXIS-ID-03 | Telemetry and planner schemas carry canonical identity; identity status docs (PRX-REMOTE-047, PRX-PLAN-182) | 5 criteria | PRAXIS-ID-01 |
| PRAXIS-ID-04 | Praxis instance identity generation and lifecycle rules (DER-16, 17, 22, 23, 24) | 8 criteria | PRAXIS-ID-01 (repository binding) |
| PRAXIS-ID-05 | Instance registration projection (DER-19, 20, 25, 26) | 4 criteria | PRAXIS-ID-04 |
| PRAXIS-ID-06 | Policy-gated branch == work-item-ID validate rule (DER-01) | 6 criteria | - |

PRX-GRP-020 is not a member: its open part (historical co-change, test overlap,
deployment boundary) is planner evidence, not identity. It belongs to the planner
track.

## 2. Reuse inventory

| Existing element | Location | What it does | Disposition |
|---|---|---|---|
| `Target.repositoryFromRemote`, `Target.validRepository` | `src/Praxis.Domain/Installation/Registration.fs:53,67` | `owner/repo` from a Git remote URL; locator grammar | reused: the observed locator of a repository comes from it |
| `ExecutionCommands.qualifyWorkItem` | `src/Praxis.Cli/ExecutionCommands.fs:52` | Renders `owner/repo:ID` from the remote for `praxis execution` | extended: it becomes a rendering of the typed identity instead of string concatenation in the CLI |
| `GitWorkspace.remoteUrl` | `src/Praxis.Infrastructure/Execution/ExecutionStore.fs:140` | Reads `origin` URL | reused for the locator observation |
| `FileWorkConfigRepository.readRepositoryId` | `src/Praxis.Infrastructure/Work/FileWorkConfigRepository.fs:252` | Reads `ros.json` `repository.id` (a name) | extended: kept as the legacy name; `repository.identity` is read beside it |
| `GitHubActionsExecutor` executor facts | `src/Praxis.Infrastructure/Remote/GitHubActionsExecutor.fs:28` | Observes `GITHUB_REPOSITORY` for remote responses | extended with `GITHUB_REPOSITORY_ID` as the observed provider ID |
| PRAXIS-GROUP-07 `MemberReference` (`owner/repo:WORK-ID`, in progress, uncommitted in `praxis-g07`) | `src/Praxis.Domain/Work/WorkGroups.fs` (working tree of `group/07-cross-repository`) | Parses and renders cross-repository member names | aligned, not duplicated: the identity module uses the same display grammar (`LastIndexOf ':'`, `owner/repo` prefix) so GROUP-07 can switch its parser to it; group members stay GROUP-07's |
| `RemoteJson.parseRequest`, `Operation.arguments`, `ProtocolVersion.isSupportedBy` | `src/Praxis.Contracts/Remote/RemoteJson.fs:430`, `src/Praxis.Domain/Remote/Protocol.fs:146,212` | Remote request parsing, argument catalog and minor-version gate | extended: 1.4 structured references are normalised before the existing argument parser; the old-peer rule is the existing minor-version gate |
| `EnvelopeReconciliation.decide` (`InstanceIdentityMismatch`, `WorkItemBranchMismatch`) | `src/Praxis.Domain/Work/EnvelopeReconciliation.fs:130,141,148` | Envelope domain rules | extended: a foreign local instance is a new finding; the mismatch rule is reused unchanged and made reachable |
| `localInstanceId` / `localPraxisInstanceId` (two copies, both swallow exceptions) | `src/Praxis.Cli/EnvelopeReconciliationCommands.fs:16`, `src/Praxis.Infrastructure/Work/FileTelemetryExecutionRepository.fs:40` | Read `.praxis/instance.json` | replaced by one Infrastructure store with typed failures |
| `AdministrationClient.send`, `RegistrationRequest.operationIdFor` | `src/Praxis.Infrastructure/Installation/*.fs:69`, `src/Praxis.Domain/Installation/Registration.fs:156` | Optional, idempotent installation registration | reused for instance registration; no second client |
| `PathFilter.meaningfulPaths`, `realObservedGitPaths`, `ProcessGitRepository.readBranchAndCommit` | `src/Praxis.Domain/Work/PathFilter.fs:74`, `src/Praxis.Cli/Program.fs:354`, `src/Praxis.Infrastructure/Git/GitRepository.fs:321` | Meaningful-change detection and branch read used by attribution | reused by the DER-01 rule |
| `computeUnifiedFindings` | `src/Praxis.Cli/Program.fs:2029` | Combines validate contributors | extended with the identity contributors |
| `Lifecycle.initialize`, `Lifecycle.performUpgrade` | `src/Praxis.Cli/Lifecycle.fs:497,524` | init and upgrade | extended: the instance identity is ensured after a successful apply |

Searches: `grep -rn "instance.json\|praxisInstanceId\|instanceId" src`,
`grep -rn "repositoryFromRemote\|owner/repo" src`, `grep -rn "workItemId" src/Praxis.Domain/Remote src/Praxis.Contracts/Remote`,
`grep -rn "ROS_BASE_REF\|enforceAttribution" src`, `git -C ../praxis-g07 diff` (GROUP-07 in progress).

New abstractions and the existing element considered:

- `Praxis.Domain.Identity.RepositoryIdentity` / `WorkItemIdentity` / `WorkItemReference`: considered
  `Target` (installation target, a string `owner/repo` with no provider ID) and GROUP-07's
  `MemberReference` (string repository, no provider ID, not yet on main). Neither carries a stable
  provider identity or the legacy state, and both are owned by other bounded contexts.
- `Praxis.Domain.Identity.InstanceIdentity`: nothing models an instance; only the field `instanceId` is read.
- `FileInstanceIdentityStore` (Infrastructure): replaces the two duplicated readers.
- `BranchPolicy` (Domain): considered `WorkItemBranchMismatch`, which compares two strings inside an
  envelope; the validate rule needs meaningful-change and policy inputs, so it is a separate pure rule
  that shares the same equality.

## 3. Group-level design

- Common architecture: one `Praxis.Domain.Identity` namespace holds the pure types and rules for
  repository, work-item and instance identity. Infrastructure observes the repository (ros.json,
  Git remote, `GITHUB_REPOSITORY_ID`/`GITHUB_REPOSITORY`) and stores `.praxis/instance.json`.
  Contracts render the structured reference `{repositoryId, repository, localId}`.
- Shared invariants: identity is decided by the provider and stable provider ID when known; the
  `owner/repo` locator is display only. A display string is never parsed to establish identity.
  Unknown is represented as unknown, never guessed. Ambiguity and conflicts fail closed.
- Conflicts: issue #90 wants identity before freezing the remote protocol, but protocol 1.0-1.3 is
  live. Resolution: protocol 1.4 adds structured references; bare IDs keep meaning "this
  repository", so live peers are unaffected and their replays fingerprint identically.
- Compatibility and migration: `ros.json` `repository.id` stays a legacy name; the identity is the
  new optional `repository.identity`. Records without it are legacy, read as before, and reported
  explicitly. Existing local IDs are never renumbered.
- Common tests: `RepositoryIdentityTests` (PRX-REMOTE-050 matrix), remote 1.4 tests in
  `RemoteProtocolTests`, `InstanceIdentityTests`, `BranchPolicyTests`.
- Risk of solving members independently: three repository-identity readers (remote, telemetry,
  instance binding) that disagree on rename/template handling.
- Shared infrastructure: the repository observation is attributed to PRAXIS-ID-01; the instance store to PRAXIS-ID-04.

## 4. Order

1. PRAXIS-ID-01 (first PR): the identity types and the repository observation every other member uses.
2. PRAXIS-ID-04, then PRAXIS-ID-05 (first PR): the instance binds to the repository identity of step 1.
3. PRAXIS-ID-06 (first PR): independent; it shares the validate contributor added for identity.
4. PRAXIS-ID-02 and PRAXIS-ID-03 (second PR): consumers of the canonical identity.

A group checkpoint is recorded after each PR merges.

## 5. Verification pass

Recorded per member before completion, in `group-verification-<member>.md` beside this file.
