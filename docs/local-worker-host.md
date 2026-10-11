# Portable local worker host

Work: [#231](https://github.com/kemiller2002/praxis/issues/231).
Proposals: [DF-ROS-2026-A065](../research/decisions/DF-ROS-2026-A065--portable-host-dispatch-composition.md),
[DF-ROS-2026-A066](../research/decisions/DF-ROS-2026-A066--native-local-worker-assignment-checks.md).

`FileLocalWorkerHost.run` composes the portable catalog/process platform with
signed delegation, native transaction recovery, launch journaling, output
spooling and immutable submission capture. It acquires work-protocol,
work-groups and policy leases, in that order, and keeps them through archive
capture. This serializes dispatch and policy changes while a worker runs.

The host provisions `LocalWorkerHostBindings`, including outside-repository
journal/spool/submission roots, independent repository identity, current signed
delegation loader, native assignment validator, prerequisite observer,
controller incarnation. The policy store supplies the single UTC clock for
authorization, journal events and capture. Built-in native assignment checks
bind exact group/home/membership and group execution links, active live members,
current unique active telemetry, member envelopes/baselines, coordinator/worker
lineage, worker role/identity and the real source Git commit. They run at
admission, reservation and intent before the additional `ValidateNative` callback.
Missing, ambiguous, malformed, duplicate-field or linked records refuse. JSON
files are bounded to 16 MiB and telemetry inventories to 4096 records. Cross-repo
and successor-mapped assignments require future observers and currently refuse.
Prerequisite semantics and independent acceptance remain host obligations.
Execution-envelope writers currently do not all participate in the work/group
lease protocol. These envelope checks are fresh observations, not a claim that
every lifecycle writer is serialized. The operational controller must coordinate
those writers, and intake/integration must recheck current state. Completing that
serialization remains a follow-up before a qualified controller is enabled.
Callbacks must not reacquire the held native/policy
leases and must finish within host-controlled bounds.

The store's internal in-scope policy reader reloads the file at every authority
boundary without recursively acquiring its exclusive lease. Pending policy or
native transactions cannot be ignored. Store creation failures, cancellation,
bad delegation and native observation refusal stop dispatch before launch.
Exceptions release the acquired leases. A lease release failure reports an
error even if dispatch already produced files; inspect immutable journals
before any next attempt.

Only a new immutable reservation may launch. Spool completion precedes the
journal's root-exit record, and complete output is archived as untrusted input.
Archive refusal is retained in the dispatch report; uncertain starts do not
become confirmed submissions. Repeated dispatch refuses instead of relaunching.

This is a library host composition, with no execution CLI. Independent result
intake, acceptance, integration and native completion remain separate. Tests
use real bounded subprocesses and file stores, with synthetic process/native
incarnation observations and signing keys. Concrete vendor bridges, prerequisite
evidence binding and a model-worker pilot remain follow-ups. Equivalent OS isolation
is deferred by user instruction.

## Verification

Local Linux verification passed 29 portability tests (12 adapter, 8 assignment,
9 host), 18 spool tests, 20 submission tests, 19 journaled-worker tests,
13 delegation tests, 57 ECIR-filter tests and 8 subprocess tests. Filters
can overlap. The Release build completed with zero warnings/errors.
Native fixtures use actual Git commits and native group/envelope serializers;
process identities, signing keys and state provisioning are still fixtures.

At implementation commit `ee9699c4`, the full Linux validation, Ubuntu/macOS
worker portability, all three lifecycle jobs, native release checks and site
checks passed. Windows exposed read-only Git-object deletion in the new test
fixtures. Their cleanup now removes the read-only attribute before deletion,
without suppressing cleanup errors. Windows worker and spool qualification
remains pending the corrected CI run.
