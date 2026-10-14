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

## Relocatable input identity

Input identity uses schema `praxis.local-validator-inputs/2`. The candidate
checkout and TRX output are host-provisioned per run, so they are bound by role
rather than literal path: exact occurrences of the working directory and TRX
path in argv, environment values and stdin are replaced by `{candidate}` and
`{trx}` before hashing, and only whether a TRX artifact is required is hashed.
One signed acceptance pin therefore covers the worker's output candidate, the
staged integration candidate, a retry and a reopened controller session. Each
run still requires a fresh clean checkout of the exact candidate commit and a
fresh, non-existent TRX path outside it; stale TRX is never overwritten or
accepted. Executable, Git, argv shape, environment, budgets and every
dependency pin remain bound. Host inputs containing `{` or `}` are refused so
a literal value cannot impersonate a role token. Changing any bound input
requires a new pin and therefore a newly signed packet.

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

The next controller integration must map reports to protected checklist rows,
require the signed validator identities/input pins, bind native prerequisite
evidence, serialize execution-envelope writers, and recheck inside the native
completion effect boundary before enabling acceptance. Installation/release
activation follows that end-to-end qualification, not this library addition.
