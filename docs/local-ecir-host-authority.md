# Embedded local ECIR host authority

Engineering work: [#231](https://github.com/kemiller2002/praxis/issues/231),
extending #220/#226. Proposal: DF-ROS-2026-A062.

`FileLocalEcirAuthority` supplies concrete local policy persistence and the
repository/host lock composition behind the existing ECIR dispatch protocol.
It is a library adapter. There is no CLI execution route or registered default
authority, and no worker isolation or authenticated operator adapter is inferred.

## Storage and administrative integration

Provision an existing host directory outside the repository. The Unix adapter
requires a non-link directory with mode 0700 and policy/lease files with mode 0600. Windows
requires a separately qualified ACL implementation and is refused here.
The deployment must separately establish ownership, stable protected ancestors,
distinct unprivileged worker identities, and a sandbox that prevents workers
accessing host state and credentials. Same-account unrestricted processes are
not protected merely because these permission checks pass.

`Initialize` provisions the initial operator policy once. `Load`, `Audit` and
`Apply` support the existing `EcirHostAdministration` ports. Authentication,
revocation rights and private-key signing remain host-owned dependencies; no
self-reported agent identity or repository configuration supplies them. Only
canonical ECDSA public PEM is stored. Private-key PEM is rejected.

`policy.json` contains the current policy and complete revision-linked audit in
one versioned document. An update checks the expected revision, matching audit,
new revision uniqueness and chronological UTC time. Unknown/missing/duplicate
JSON fields and malformed state refuse. Inventories and document size are bounded.
The store never prunes audit history to fit its limit; archival/migration must
be designed before reaching that limit.

The store writes a mode-0600 `policy.pending` with exclusive creation and flush,
then atomically renames it over `policy.json`. A failed write leaves pending
state that blocks reads, mutation and dispatch until an operator reconciles it.
Readable pending bytes do not grant authority. No automatic retry or stale-lock
deletion is used. Policy operations hold an exclusive open-file lease; closing
the handle after a process exit releases it. Replacing/deleting the lock file
while any controller operates is forbidden by the deployment boundary.

This protects cooperating processes and atomic policy/audit visibility after
normal process interruption. It does not prove power-loss durability, remote
filesystem lock behavior, authenticated policy ownership or resistance to a
privileged operator rolling back/deleting the entire store. Revision history
rejects reuse within the surviving state; a separate trusted rollback witness
is still required for stronger rollback claims.

## Dispatch and recovery

`dispatch` acquires `work-protocol`, then `work-groups`, before invoking native
recovery and the existing `EcirHostPolicy.dispatch`. The host policy lease is
acquired last by the authority compare-and-commit operation. It reloads policy,
checks revision, qualified-release state, UTC clock and expiry immediately before
the repository callback. Revocation and publication use the same policy lease.
Recovery callbacks must use locked native primitives, never recursively acquire
these locks or the policy lease.

`dispatchNative` composes existing native staging with host journal preparation
and recovery. It recovers the previous host dispatch journal and native work/
backlog transactions before considering a new dispatch, including derived
backlog reconciliation from already-complete native state. A failed/conflicting
recovery stops validation and staging. The staged write set is committed only
inside the authority callback. The adapter preserves the existing native member
planners and their real execution identities.

This route does not launch a worker, complete an integrated result, or qualify
the source/release/validator pins. The supplied native staging and validator
ports still require trusted production bindings. Worker dispatch/intake uses a
separate exact local delegation contract that must be bound to authenticated
host observations before a command can be enabled.

## Deployment blockers observed here

The actual `bwrap --unshare-all` probe refuses network namespace setup with
`Operation not permitted`; no local model backend was found in PATH. Therefore
this environment cannot qualify an isolated actual-agent run. The engineering
adapter can be compiled and adversarially tested here; actual protection and
the single-worker end-to-end run need a suitable deployment host.

## Verified engineering checks

Release build passed with zero warnings/errors. The normal Praxis test runner
passed 55 ECIR tests, including 18 new file-authority cases and an extended real
native member/group staging fixture through `dispatchNative`. Nine existing CLI
ratchet tests also passed. Registry build, repository validation and diff checks
passed. These are targeted local checks; full-suite CI on the published head is
pending. The fixture's signer, qualified-release declaration and validator
observations remain explicitly synthetic, and no real worker launch is claimed.
