# Portable local worker host

Work: [#231](https://github.com/kemiller2002/praxis/issues/231).
Proposal: [DF-ROS-2026-A065](../research/decisions/DF-ROS-2026-A065--portable-host-dispatch-composition.md).

`FileLocalWorkerHost.run` composes the portable catalog/process platform with
signed delegation, native transaction recovery, launch journaling, output
spooling and immutable submission capture. It acquires work-protocol,
work-groups and policy leases, in that order, and keeps them through archive
capture. This serializes dispatch and policy changes while a worker runs.

The host provisions `LocalWorkerHostBindings`, including outside-repository
journal/spool/submission roots, independent repository identity, current signed
delegation loader, native assignment validator, prerequisite observer,
controller incarnation. The policy store supplies the single UTC clock for
authorization, journal events and capture. The native validator must independently
bind the packet to current group/member executions and repository state. It is
called again at reservation and launch intent; a fixture callback is not a
production implementation. Callbacks must not reacquire the held native/policy
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
observations and signing keys. Concrete vendor bridges, native assignment
observers and a model-worker pilot remain follow-ups. Equivalent OS isolation
is deferred by user instruction.

## Verification

Local Linux verification passed eight host-composition tests (included in the
20-test portability filter), 57 ECIR-filter tests, 13 signed-delegation tests,
19 journaled-worker tests, 17 output-spool tests, 23 submission-filter tests and
8 subprocess tests. Filters overlap. Release build completed with zero warnings
or errors. The host tests run under the existing Linux/Windows/macOS portability
matrix; this new composition's cross-OS results remain pending publication.
