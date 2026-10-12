# Concrete local validator runners

Work: [GH-235](https://github.com/kemiller2002/praxis/issues/235).
Decision: [DF-ROS-2026-A068](../research/decisions/DF-ROS-2026-A068--concrete-local-validator-observations.md).

`FileLocalValidator.execute` is a concrete F#/.NET process adapter for
independently running a host-selected build, test, architecture or acceptance
command. It does not read a worker's claimed pass. It accepts literal argv,
an explicit environment, a pinned executable, bounded timeout/output, an exact
candidate Git commit, pinned Git, and host-declared dependency file pins.
`FileLocalValidator.observe` supplies fresh read-only candidate/input identity
for checklist generation and comparison with signed validator pins before
execution. It supplies no verdict; `execute` observes inputs again.

The controller must supply a fresh clean candidate checkout. Before and after
the command, bounded Git subprocesses observe actual HEAD, uncommitted inputs
and the full recursive Git tree. Wrong HEAD, dirty inputs, links/submodules,
unavailable Git, changed tree observations or replaced tool/dependency pins
refuse. Candidate identity includes commit, file modes and blob identities.
Validator input identity includes command, environment, input, budgets, paths
and every declared dependency pin. Two observations do not provide an atomic
lock against uncooperative writers; host lease/custody remains mandatory.

Managed runners must pin their assemblies, dependency/runtime configuration,
scripts and other inputs as well as the native `dotnet` host. Pinning `dotnet`
alone does not pin an F# validator. The adapter cannot discover an arbitrary
program's complete dependencies. Ignored build/package inputs must be absent
in a fresh checkout or covered by the protected input closure. Validators
must not fetch mutable dependencies and call that reproducible validation.

## Test evidence

For a test command, the host supplies a fresh absolute TRX output path outside
the candidate checkout, and puts that path in the literal runner argv. The
path must not already exist or contain links. Process exit zero alone does
not pass a test command: a bounded TRX snapshot must contain a completed run,
positive executed count, matching passing result entries, unique execution
identities and no failed, skipped or inconsistent totals. XML DTDs refuse.
The evidence digest binds the exact snapshot that was parsed, not a later
read of a potentially changed result. Stdout prose never supplies test counts.

`TrxPath=None` is for non-test commands. A checklist adapter must require
`TrxPath=Some ...` for a test row; it must not reinterpret a generic process
success as test execution. TRX is runner-produced data, not proof of meaningful
coverage. Malicious tests can manufacture artifacts unless the controller's
runner and input-custody model prevents that. Existing checklist, requirement,
scope, quality, authority and freshness gates remain necessary.

## Evidence and limits

Actual stdout/stderr, observed outcome, candidate/input
identities, times, test count and refusal are written once to a new bounded
JSON evidence file in a host-provisioned outside-checkout directory. The file
is flushed to durable storage and read back for hash verification before a
report is returned. No existing evidence is overwritten. The host must protect
that directory and consume the returned digest under its own leases; a writable
file with a digest is not independently authenticated custody. Retained output
may contain sensitive test logs, so the host must apply its data policy.

Transient controller tools are supervised through the actual .NET `Process`
handle and return `LocalControllerToolObservation`, a separate type without a
worker incarnation. Very short Git processes can exit before .NET can query
`StartTime`. That is not resolved by inventing an incarnation or accepting a
PID-only worker identity. Journaled worker dispatch still requires the existing
real incarnation observation before delivering input; controller-tool reports
cannot be supplied to that port or become worker submissions.

This slice does **not** enable an execution CLI, supply a model worker bridge,
connect native completion effects, or claim equivalent OS containment. The
portable process mechanics are reused; the new adapter still needs its own
three-OS CI. The coordinator/two-worker model pilot remains outstanding. This
environment has no `claude`, `codex`, `ollama` or `llama-cli` executable on PATH;
process fixtures must not be presented as a live model-worker pilot.

## Concrete checklist binding

`FileWorkerChecklist.create` supplies the existing `WorkerChecklistPorts` from
an exact host-held packet/result, fresh policy, host-owned request resolver,
cancellation token, UTC clock and live guard. It observes each requested command
and candidate before executing. Actual input digests must match protected system
policy or the assigned acceptance pin. All rows must observe the same candidate
manifest. Test rows require a fresh TRX path. Evaluate regenerates and compares
the entire checklist, executes actual pinned commands, checks the archived
evidence against the directly returned report, and reobserves inputs afterward.
There is no worker JSON pass decoder or callback supplying a passing verdict.

Identical full requests share a real process observation only within one
evaluation. Every consuming row still reobserves inputs and verifies the archive.
This is not a persistent cache or reusable approval. Repeating an evaluation
with a preexisting TRX file refuses. A live controller must provision fresh
validator slots or implement a separately qualified immutable cache. Because
paths/argv are part of the input digest, changing slots requires preparing
the matching protected policy and signed acceptance pins before delegation.

`FileLocalValidator.verifyEvidence` reads one bounded snapshot, checks its digest,
generated filename, outside-checkout custody root, exact fields and report
identity. This proves consistency with a host-held report, not authentication.
The guard must verify held lifecycle/input/evidence leases and assignment
authority before and after observations. It does not acquire locks, establish
OS protection, or replace integration/authority/prerequisite checks. The
bindings must come from trusted controller code, never from worker artifacts.

Remaining controller integration must bind native prerequisite
evidence, serialize execution-envelope writers, and recheck inside the native
completion effect boundary before enabling acceptance. Installation/release
activation follows that end-to-end qualification, not this library addition.
