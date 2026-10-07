# Repository, work-item and instance identity

Praxis keeps four identities apart: the **repository**, the **work item**, the
**Praxis instance** (one installation of Praxis in one repository), and the
agent, session and transaction identities described in
[`agent-provenance.md`](agent-provenance.md). This page covers the first three.

Requirements: PRX-REMOTE-045..050 (GitHub issue #90, adopted by
`RQ-ROS-2026-A021`) and items 1 and 16-26 of
[`requirements/PRAXIS-DUAL-ENTRY-RECONCILIATION.md`](../requirements/PRAXIS-DUAL-ENTRY-RECONCILIATION.md).
Code: `Praxis.Domain.Identity` (pure rules), `Praxis.Contracts.Identity.IdentityJson`
(JSON shapes), `Praxis.Infrastructure.Identity` (observation and storage),
`praxis repository identity` and `praxis instance` (CLI).

## Repository identity

A repository is identified by its **provider** (`github`, ...) and the
provider's **stable repository ID** (for GitHub, the numeric repository ID).
The `owner/repo` **locator** is a human-readable display value. It changes on
a rename or transfer, and another repository can reuse it, so it never decides
identity on its own.

It is configured in `ros.json`:

```json
"repository": {
  "id": "repository-operating-system",
  "type": "tooling",
  "identity": { "provider": "github", "repositoryId": "github:1309152643", "repository": "kemiller2002/praxis" }
}
```

`repository.id` is the legacy repository **name**. It is still read and never
rewritten, but it is not an identity.

```
praxis repository identity [--json]
praxis repository identity set --provider github --provider-id ID [--locator OWNER/REPO] [--json]
praxis repository identity set --from-environment     # in GitHub Actions
```

`set` defaults the locator to the one the `origin` remote shows. In GitHub
Actions, `--from-environment` takes the ID from `GITHUB_REPOSITORY_ID` and the
locator from `GITHUB_REPOSITORY`.

### Verification

Configuration travels with the files, so a template or fork copies it. Only an
observation of the hosting environment can tell a copy from the original:

| Observation | Status | `validate` |
| --- | --- | --- |
| No `repository.identity` | `not-established` (a legacy identity is derived from the remote when its host is known) | warning, in an installation with `.praxis/` |
| Observed stable ID equals the configured one | `verified` (the observed locator is used, so a rename or transfer is followed) | - |
| Stable ID not observable (normal outside CI) | `unverified` | - |
| Configured identity has no stable ID | `legacy` | warning |
| Different locator, no stable ID observed | `locator-changed` (a rename, transfer or copy) | warning |
| Observed stable ID differs from the configured one | `contradicted` (the configuration was copied) | **error** |

CI is the authoritative observation (`GITHUB_ACTIONS=true` with
`GITHUB_REPOSITORY_ID`); locally only the remote's locator is observed. Nothing
is fetched from the network.

## Work-item identity

A work-item ID (`WI-0042`, `VIG-15`) is unique only inside its repository. The
**canonical** identity of a work item is its repository identity plus its local
ID (PRX-REMOTE-045, 049). New local IDs are never coordinated across
repositories, and existing ones are never renumbered.

- **Display:** `owner/repo:LOCAL-ID` (for example `kemiller2002/praxis:WI-0042`).
  It is derived for people and logs and is never parsed back into identity.
- **Structure:** schemas carry `{ "repositoryId": "github:1309152643", "repository": "kemiller2002/praxis", "localId": "WI-0042" }`.
  A reference whose repository is not established is explicitly legacy:
  `repositoryId` is `null` and `provider` names the provider when it is known.
- **Uniqueness key:** `github:1309152643/WI-0042`. A legacy work item has no key: its uniqueness is not proven.

Resolution rules (all fail closed, none guesses):

- A bare ID means the repository whose context makes it unambiguous.
- A bare ID looked up across several repositories that is found in more than one is **ambiguous** and refused.
- A qualified reference to another repository is refused where only one repository is in scope.
- A reference with no stable ID whose locator differs from the context is **unverified** and refused: a rename cannot be told from another repository.
- Duplicate canonical identities, one stable ID seen with two locators, or one locator claimed by two stable IDs, are **conflicts** and refused.

### Where the canonical identity is carried (PRX-REMOTE-047)

- **Remote protocol 1.4:** `workItemId`/`workItemIds` accept the structured
  reference; responses add `repository.identity` and `workItems`
  ([`remote-protocol.md`](remote-protocol.md)).
- **Execution telemetry:** new execution records carry `workItem` next to the
  legacy `workItemId`; records without it stay valid and are read as the local
  ID in their repository.
- **Planner documents:** every `praxis.plan` document carries `repository`
  ([`planning.md`](planning.md#json-contract)).
- **Cross-repository groups:** members use `owner/repo:ID` with the same
  locator grammar (`RepositoryLocator`).

### Migration rule for legacy IDs (PRX-REMOTE-048)

1. Existing local IDs, events, telemetry and evidence are not rewritten. Their
   canonical identity is derived from the local ID plus the repository identity
   of the repository that holds them.
2. A repository without `repository.identity` is legacy. Its canonical
   identities are incomplete until the identity is established with
   `praxis repository identity set`.
3. A legacy locator-only identity is upgraded only with verifiable evidence that
   carries a stable ID and does not contradict the locator
   (`RepositoryIdentity.upgrade`). A different locator is never assumed to be a
   rename.
4. A legacy unqualified ID is only resolved inside its own repository; across
   repositories it is ambiguous unless qualified.

## Praxis instance identity

Every installation has a stable instance identity, distinct from the repository,
agent, session, work-item and transaction identities (DER-16). It is
`.praxis/instance.json` (`praxis.instance/1`), and it is committed:

```json
{
  "schema": "praxis.instance/1",
  "instanceId": "pxi-3f2a...",
  "createdAt": "2026-10-06T21:00:00.000Z",
  "createdWith": "3.7.2",
  "repository": { "provider": "github", "repositoryId": "github:1309152643", "repository": "kemiller2002/praxis" },
  "predecessors": []
}
```

```
praxis instance [show] [--json]
praxis instance init [--reinitialize --reason TEXT] [--json]
praxis instance projection [--json]
praxis instance register [--config PATH] [--catalog FILE] [--require] [--json] [IDENTITY]
```

- **Creation (DER-17).** `praxis init`, `praxis upgrade` and `praxis instance init`
  create it when it is missing, before any registration is attempted. An
  installation that predates instance identity gets one on its next `upgrade`.
  The local record is authoritative; registration is never needed.
- **Upgrades and repeats (DER-24).** `init`, `upgrade` and `instance init` never
  replace an existing identity. An unreadable record is reported and never
  overwritten.
- **Clone, rename, transfer (DER-24).** The record names the repository it was
  created in. A clone of the same repository, a rename and a transfer keep the
  same stable repository ID, so the identity stays bound.
- **Template, fork, copy (DER-24).** A copy into another repository is detected
  from the stable repository ID (in CI). The copied identity is **foreign**:
  `validate` fails, new telemetry does not carry it, and an envelope that claims
  an instance is refused, until `praxis instance init --reinitialize --reason
  TEXT` creates a new identity that records its predecessor and the reason.
  Create the identity where the stable ID is observable (or after
  `repository identity set`): an identity bound only to a locator can tell a copy
  from a rename only by the locator, so a mismatch is reported as unverified
  rather than foreign.
- **Propagation (DER-22).** New native telemetry executions record `instanceId`.
  Reconciliation stamps accepted envelope history with the local instance when
  the envelope does not claim one.
- **Rejection (DER-23).** Reconciliation refuses an envelope whose claimed
  `praxisInstanceId` differs from the local identity
  (`instance-identity-mismatch`), names an instance the repository does not have
  (`instance-identity-unestablished`), or cannot be verified because the local
  record is foreign or unreadable (`instance-identity-unverifiable`).

### Projection and registration (DER-19, 20, 25, 26)

`praxis instance projection` renders `praxis.instance-projection/1`: the instance
ID, repository coordinates, Praxis version, reconciliation (`1.0`) and remote
protocol versions, capabilities, and the availability of optional integrations
(`available`, `unavailable`, `not-configured`, `unknown`). Unknown is a normal
state. The document is built from an allow-list, so credentials, environment
variables, local paths and agent-private data cannot reach it. It needs no
Echelon component, network or credential.

`praxis instance register` sends that projection through the installation
registration client ([`installation-registration.md`](installation-registration.md))
as `installation.register` for system `praxis`, with the projection as evidence
and the projection's digest as operation ID, so a retry is the same operation.
With no administration integration, or an unreachable one, the outcome is
`unavailable`, the exit code is 0, and nothing local changes.

## Branch policy (DER-01)

`ros.json` `workProtocol.branchPolicy` is `none` (the default) or
`work-item-id`. With `work-item-id`, `validate` fails when meaningful changes
(working tree, plus the `ROS_BASE_REF` range) exist on a branch whose name is
not the ID of a work item in the work context. In GitHub Actions the branch is
`GITHUB_HEAD_REF`; elsewhere it is the checked-out branch. A detached HEAD with
meaningful work is a failure, not a pass. The envelope path enforces the same
rule (`work-item-branch-mismatch`).
