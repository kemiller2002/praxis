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

The first slice supplies pure rules, controller ports, mandatory local
intake/integration/completion gates, inspection and adversarial tests.
The controller integration adds concrete runner/evidence mapping and connects
the protected host API to the existing native effect transaction.
[`praxis local run`](local-runtime.md) is the production entry point.

Migration: work types listed in `workProtocol.workerChecklists.delegatedTypes`
complete only through `praxis local run`. Standalone `work complete` and
runtime-free envelope reconciliation refuse them, and the runtime refuses
members of unlisted types, so a dispatched member cannot bypass its checklist.
Repositories without the setting keep the previous completion-readiness policy
and cannot run the local runtime.

`FileWorkerChecklist.withEvidenceSession` holds an exclusive evidence-store
handle and exposes checklist/acceptance observations only for that scope.
Only actual `FileLocalValidator` reports populate its bounded in-memory cache.
Every reuse freshly observes candidate/tool inputs and verifies the archived
evidence bytes and selected access policy. There is no persisted pass decoder.
Unobserved acceptance is an error, never a synthetic pass. Intake checks
identities, paths, lineage and artifacts before running validators, then
requires their observed acceptance. Failed/blocked packages still cannot
authorize integration.

System validators are candidate-wide: identical host-selected validator pins
can share one actual process observation across the generated member rows.
Member acceptance obligations remain independently pinned and mandatory.
Validator pins bind the checkout and TRX locations by role
([input identity v2](local-validator-runners.md#relocatable-input-identity)),
so one signed pin covers the worker's candidate and the staged integration
candidate. Within a session the runtime keeps one fresh checkout and TRX path
per (candidate, validator); a retry or reopened session provisions new ones.
Preexisting TRX is never overwritten or accepted.

`FileLocalWorkerHost.runAndComplete` dispatches, archives and reopens fresh
native/group/policy leases for acceptance. It binds concrete checklists and
their actual acceptance observations, overriding any factory-supplied
checklist or acceptance pass. Integration uses its durable journal; completion
uses `FileLocalIntegratedCompletion` and the existing native transaction.
It repeats the whole plan before finalization, then rechecks candidate,
evidence, target, authority and prerequisites after finalization before
persisting completion. A refusal after member telemetry was finalized leaves an
identifiable interrupted completion (member active, single linked execution
finalized, integration for that dispatch applied); rerunning the same
delegation resumes it with every check repeated while the delegation is valid.

`FileLocalRuntime` composes these with pinned Git staging, the shipped worker
bridge and the same quality-readiness and durable-checkpoint gates as
`work complete`. A portable development profile is not OS containment or
protection from a worker running with the same identity.

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

## Construction-package scope verification

Work: [GH-234](https://github.com/kemiller2002/praxis/issues/234), local work
item WI-0097. Decision:
[DF-ROS-2026-A074](../research/decisions/DF-ROS-2026-A074--construction-package-scope-verification.md).
Construction host: [construction.md](construction.md).

**What it does and does not establish.** This check verifies the scope and
provenance of writes the Praxis host applied with `construct apply` and
`construct extend`. It does **not** provide OS containment, and it cannot stop
a process running as the host user from rewriting host roots. Containment is
qualified separately (#231, #235, PR #248).

### Row

- **Kind:** `WorkerCheckKind.ConstructionScope`.
- **Check ID:** `system/MEMBER/construction-scope`, one row for each member.
- **Validator:** `praxis.construction-scope/1`. This is implemented by the
  host (`ConstructionScope`), not a configurable worker command. `generate`
  refuses any other validator identity, or an invalid digest, with
  `checklist-construction-scope-validator-invalid`.
- **When it is required:** exactly when the protected host identifies the
  package as a construction package (see the next section). It is added to the
  build, tests, architecture, scope and acceptance rows. None of those rows
  changes or can be replaced.
- **Identity:** the validator and its input digest are framed into the
  checklist identity, like every other row. The input digest pins the packet
  digest, the candidate, the host configuration digest, the policy revision,
  the identification facts, and a fingerprint of every file in the
  construction root. A non-construction checklist keeps its previous digest.
- **Enforcement:** intake, integration and completion all reach the row
  through `WorkerChecklistOperations.verify` and `recheckCandidate`. There is
  no worker decoder and no self-approval flag.
- **Evidence:** every observation, passed or refused, is archived as
  `construction-scope-*.json` in the existing evidence store. It is written
  under the store's access policy, and its digest is rechecked before use.
- **Refusal:** a refused row reports
  `system/MEMBER/construction-scope:checklist-check-failed: REASONS`.

### Identifying a construction package

A package is a construction package when any one of these protected facts
holds. Nothing a worker asserts is consulted.

1. **(a) Journal.** The host construction journal holds an entry whose
   host-written commit names the packet's native group in a
   `Construction-Group` or `Extension-Group` trailer. An untrusted or
   unreadable journal also counts, so this fact fails closed.
2. **(b) Trailers.** A commit between the delegated source commit and the
   candidate carries a host `Construction-*` or `Extension-*` trailer. This
   applies even when construction is disabled on the host.
3. **(c) Generated paths.** With construction enabled, the candidate changes
   a path under `MOUNT/generated/`.

Fact (a) does not depend on the candidate, and fact (c) does not depend on
trailers. A construction package therefore cannot evade the row by omitting
trailers. Once the host has constructed for a group, every package of that
group is a construction package.

### Inputs and their sources

| Input | Source | Trust |
|---|---|---|
| Packet: group, cohort, source commit, digests, members, decisions, receipt, policy revision | Signed delegation, bound by the packet digest | Protected |
| Candidate commit | Checklist context, observed from Git | Observed |
| Host configuration: mount, slots, roots, file-access policy | `praxis.local-host/1` outside the repository, digest-checked | Protected |
| ECIR grant and signed approval | Protected host policy store, reloaded and re-verified at check time | **Authority** |
| Ordo engine and generator | Pinned vendored assemblies | Protected |
| Committed ECIR manifest and blueprint | Git objects at the approved source commit | Observed |
| Journal entries (`plan.json`, `applied.json`) | Host construction root | **Evidence only** |
| Plan record `plans/HEX.json` | Written by `apply`/`extend` in the construction root | **Evidence only**, re-admitted |
| Routing record `routes/ROUTE-ID.json` | Written by `route`/`extend` | **Evidence only**, compared with the re-derived route |
| Commit trailers and candidate tree | Git objects (`cat-file`, `ls-tree`, `diff` between commits), never a working tree | Observed |

**Why the journal can be trusted as host-produced evidence.** The
construction root must be absolute, outside the repository and unlinked
(`LocalHostJournalFiles.rootOutside`, `noLinks` on every path). It must pass
the host file-access policy: `CheckDirectory`, which requires mode `0700`
under `unix-private`. It must not lie inside or around the checkout, spool or
submission roots. Otherwise the check refuses with `journal-untrusted`.

Even a trusted journal is never authority:

- Every pinned value is re-derived and compared.
- Every byte of the candidate tree is compared with the re-derived output.
- A slot write is accepted only into a slot that the current host policy
  declares, and only with the exact digest the journalled extension commit
  recorded.

### What is pinned

- **Construction authorization.** `ConstructionAdmission.admit` runs on the
  recorded plan. It uses the current grant for the group's current members
  and re-verifies the signed approval against the trusted signers now. The
  packet's policy revision and receipt digest must equal the current grant's.
- **Cohort.** The packet, the `Construction-Cohort` and `Extension-Cohort`
  trailers, and the approved grant must agree.
- **Requirement and decision sets.** The packet's members and decisions must
  equal the signed approval's.
- **Plan digest.** The re-admitted plan record, the journal's
  `packetDigest`, the `Construction-Plan` and `Extension-Plan` trailers, and
  the plan record's file name must agree.
- **Generator version.** The `Construction-Generator` and `Construction-Ordo`
  trailers must equal the pinned engine.
- **Ownership root.** The re-derived ownership root, placed by the host
  mount.
- **Slot policy.** The `Extension-Route` trailer must equal the route
  re-derived under the current host slot policy. The routing record must
  match it byte for byte.
- **Exact candidate commit.** The candidate must be the journalled commit of
  the newest host entry on its chain. The chain must start at the delegated
  source commit.
- **Journal identities.** Each entry's identity and submission digest must
  equal the values re-derived from its plan, route, target, base and files.
  Each journalled commit must be a single commit on its recorded base.

### Refusal reasons

Every reason is reported, prefixed `construction-scope-`:

| Reason | Meaning |
|---|---|
| `journal-unavailable` | The host names no construction root. |
| `journal-untrusted` | The root is in the repository, linked, overlaps a worker-writable root, or fails the host access policy. A plan record is linked. |
| `journal-unreadable` | An entry is partial, unexpected, linked, malformed, stored under another identity, or has an `applied.json` that does not bind its plan. |
| `journal-foreign-entry` | An entry belongs to another repository identity. |
| `journal-unreconciled` | A prepared transaction has no applied record. Rerun the construction command to reconcile it. |
| `journal-inconsistent` | A journalled commit is unreadable, not a single commit on its base, carries no unambiguous trailers, has the wrong identity kind, or its identity differs from the re-derived one. |
| `evidence-missing` | No applied construction is journalled for the group, or the chain has no construction. |
| `candidate-not-host-applied` | The candidate is not the exact commit of a journalled host entry. This covers worker commits, substituted commits and copied trees. |
| `base-mismatch` | The host-applied chain does not start at the delegated source commit. |
| `evidence-stale` | A host-applied entry for the group is not carried by the candidate. A newer or divergent construction exists. |
| `extension-stale` | An extension names a construction other than the candidate's current one. |
| `plan-record-missing` | There is no readable host plan record for the journalled plan digest. |
| `authorization-refused` | The policy is unavailable, the grant or signed approval fails re-verification (for example, a revoked signer), the policy revision changed, or the receipt differs. |
| `plan-digest-mismatch` | The journal, trailer, plan record or re-admitted plan disagree. |
| `generator-mismatch` | The generator or Ordo trailer differs from the pinned engine. |
| `cohort-mismatch` | The packet, trailers and grant name different cohorts. |
| `source-mismatch` | The packet's source commit, manifest or blueprint differs from the grant. |
| `requirement-set-mismatch` | The packet's requirement keys differ from the approval's. |
| `decision-set-mismatch` | The packet's decisions differ from the approval's. |
| `trailer-mismatch` | A group, source, manifest, blueprint or decision trailer differs from the re-derived binding. |
| `digest-inconsistent` | A journal submission digest differs from the re-derived manifest or slot list. |
| `operation-mismatch` | A journalled operation, re-planned on its recorded base, differs from its journalled commit. |
| `operation-missing` | A re-derived artifact is absent from the candidate tree. |
| `artifact-digest-mismatch` | A generated file or slot file differs from its re-derived or recorded digest. |
| `unexpected-file` | A file inside the ownership root is not re-derived, an extension wrote outside its declared slots, or a change lies outside the ownership root and the current slot writes. |
| `slot-not-authorized` | A slot path or contract is not declared, or is not usable, under the current host policy. |
| `slot-policy-changed` | The extension's route differs from the route under the current slot policy. |
| `route-record-missing` / `route-record-mismatch` | The host routing record is absent, or differs from the re-derived route. |
| `host-observation-failed` | An unexpected host failure occurred. The check fails closed. |

### Freshness rules

- Authority, approval expiry, the plan record and the journal are re-read on
  every evaluation. A construction-scope result is never cached across
  evaluations.
- `verifyCandidate` observes the inputs before and after evaluation. Any
  change refuses with `checklist-inputs-changed-during-verification`. This
  includes a change to the construction root fingerprint, the policy
  revision or the configuration.
- The evaluator re-identifies the package immediately before verification.
  It refuses if the pinned digest moved.
- Every applied entry for the group must be on the candidate's chain, and
  pending entries refuse. A replayed older construction or journal therefore
  refuses.

### Operational notes

- **Integration.** Integration of a construction package must fast-forward to
  the host-applied tip. When the target has moved, re-plan against the new
  head.
- **Re-construction.** After a re-construction, reapply the extensions.
- **Migration.** Constructions applied before this change lack a plan record.
  Rerunning `construct apply` with the same plan records it and changes
  nothing else.
- **Preview and diagnostics.** `construct preview` and the diagnostics stay
  read-only and work while acceptance refuses.

## Executed verification

On 2026-10-11, the Release solution build completed with zero warnings/errors;
all 1,985 non-browser regression tests passed. The narrower checklist,
submission, integration, completion, handoff and architecture filters also
passed (119 test executions). Registry, repository validation, architecture
and diff checks passed. Linux/Windows/macOS checklist and handoff CI is
configured; cross-platform and browser CI results remain pending publication.
These results validate the implemented library gates, not concrete trusted
runners or a live model-worker pilot.
