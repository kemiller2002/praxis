# Portable local worker mechanisms

Work: [#231](https://github.com/kemiller2002/praxis/issues/231).
Proposal: [DF-ROS-2026-A064](../research/decisions/DF-ROS-2026-A064--portable-local-worker-and-host-adapters.md).

The current milestone is functional portability across worker tools and Linux,
macOS and Windows. Equivalent worker isolation is deliberately deferred by user
instruction. Portability tests do not constitute isolation qualification.

## Shared worker contract

`LocalWorkerAdapters.catalog` accepts host-provisioned adapters, each with one
exact worker identity, unique adapter identity, supported OS set and supported
implementation/verification/review roles. Empty, malformed or ambiguous
registrations refuse. `select` accepts only an admitted `CheckedLocalWorkerPacket`
and rejects unknown workers, unsupported OSes or roles before adapter preparation.
`LocalWorkerProcessPlatform.selectCurrent` obtains the actual OS from .NET;
unsupported operating systems refuse rather than silently selecting Linux.

`LocalWorkerAdapters.command` supplies a common CLI bridge: canonical delegated
packet JSON on stdin, literal host-selected argv, explicit environment and budgets
capped by the packet. The existing journaled dispatcher independently verifies
exact stdin and budgets before reservation. A bridge must emit canonical worker
result JSON on stdout. Vendor-specific prompts, tool handling and result-envelope
conversion belong in that bridge; console logs should use stderr. The raw spool
and immutable submission archive retain actual process output as before.

This catalog supports multiple host-registered workers without embedding vendor
names in shared logic. It does not implement or claim compatibility with Claude,
Codex or a particular local-model agent. A concrete bridge still requires an
installed executable, dependency pins and its own compatibility tests. Declaring
an OS supported does not prove the selected executable exists there.

## Process platforms

`LocalWorkerProcessPlatform` separates process-incarnation observation from
stop/root-exit observation. The default uses .NET `Process.StartTime`, literal
`ArgumentList`, explicit environment, bounded redirected streams, timeouts,
cancellation, `Kill(true)` and a bounded root-exit wait. Alternative host platforms
can be injected into streaming or nonstreaming supervisors. Existing observer
helpers retain their behavior for mechanism fixtures.

Unavailable/invalid incarnation, failed start persistence and uncertain root
exit remain refusals or reconciliation evidence. No PID-only fallback is added.
A .NET process-tree kill and root-exit observation do not establish equivalent
containment of descendants across OSes. OS sandbox/credential/network adapters
can be added and qualified separately later.

## Host file access

`FileLocalEcirAuthority.create` retains the existing Unix private-mode adapter.
`createWithFileAccess` accepts a host-owned access policy, separating directory/file
checks and file-creation configuration from shared policy persistence.
`createPortableDevelopment` selects the explicit
`portable-development-unqualified` profile, also reported by `FileAccessProfile`.
It checks absolute outside-repository roots and linked/directory file paths but
does not inspect Unix modes, Windows ACLs or ownership. It is never selected as
an automatic fallback from failed Unix checks.

The same strict policy schema, size bounds, exclusive leases, atomic policy/audit
publication, CAS, signature checks, expiry and pending-write refusal run under
both profiles. The access profile does not confer signing or dispatch authority.
The existing default's qualified-release and native completion gates stay intact.
No execution command is enabled.

Use canonical non-link host paths; macOS exposes some temp locations through
`/var` aliases. Test helpers resolve their temporary directory ancestors while
production no-link checks remain unchanged.

## Verification

The `local-worker-portability` CI matrix builds the actual tests/CLI and runs
portable adapter/storage tests, existing bounded subprocess tests and signed
delegation tests on Linux, Windows and macOS. The Linux full suite remains in
its existing job. Cross-OS results are reported only after those jobs run.
Fixture process-incarnation observers and signing keys are synthetic. Real
bounded subprocess transport and file-store mechanics are tested; an actual
model-worker pilot and equivalent isolation are separate milestones.

Local Linux verification passed: 12 portability tests, 8 subprocess tests,
13 signed-delegation tests, 19 journaled-worker tests and 57 ECIR-filter tests
(filters overlap). The Release build completed with zero warnings or errors.
Windows and macOS results remain pending the new CI matrix.
