# Local worker supervision mechanism

Work: [Praxis #226](https://github.com/kemiller2002/praxis/issues/226),
[RQ-ROS-2026-A028](../research/requirements/RQ-ROS-2026-A028--local-coordinator-and-worker-handoffs.md)
and draft [DF-ROS-2026-A060](../research/decisions/DF-ROS-2026-A060--local-coordinator-with-embedded-authority.md).

The isolated F# process mechanism launches a host-selected executable with an
explicit argument list, bounded UTF-8 stdin and an explicit environment allowlist.
It has no CLI execution route. The isolated
[journaled application composition](local-journaled-dispatch.md) now connects it
to journal ports in fixtures; the qualified controller remains pending. It does not authenticate authority, isolate workers or complete work.

## Launch and observations

The executable and working directory must be existing absolute paths. A SHA-256
pin is checked before start. Arguments, environment, input, timeout and combined
stdout/stderr have fixed maximum bounds. Shell execution is disabled; inherited
environment variables are cleared before the host allowlist is applied.

Cancellation before start reports no process started. A failed process start is
uncertain, never independent proof of no-start. After start, the production
observer requires PID plus OS-reported UTC start ticks. Missing or invalid
incarnation observation fails closed. The observation callback must succeed
before stdin is delivered; callback failure stops the root and retains failure.
The future controller must persist launch intent before calling this mechanism
and persist the observed incarnation in that callback under its protected locks.
A callback or identity string alone supplies no authentication.

Stdout and stderr share a byte budget. Timeout, external cancellation, excess
output or stream failure trigger process-tree termination and a bounded root-exit
wait. A normal root exit has a bounded stream-drain interval to handle inherited
pipes. Invalid UTF-8 is retained as failure evidence; a nonzero exit stays nonzero.
Exit zero grants no acceptance or integration permission.

`RootExitObserved` proves only exit of the root process handle. Attempting
`Kill(true)` does not qualify descendant containment or cleanup. Cleanup may add
five seconds and stream draining may add 500 milliseconds to the execution
budget; OS calls and synchronous host observation are not hard real-time bounds.
The adapter must qualify its own filesystem, credential, network and descendant
protection. Pinning this executable does not pin libraries, runtime, model or
validators, and a writable executable path has a replacement race. A protected
immutable dependency closure and independently pinned acceptance remain required.

## Fixture evidence and environment limits

Eight process-mechanism tests pass with actual subprocesses and an explicitly
synthetic fixture identity observer. They cover literal argv/stdin, excluded
ambient environment, combined output limits, timeout, cancellation before and
after start, wrong pins, malformed budgets, observation failure, callback failure,
nonzero exits and invalid UTF-8. Synthetic identities do not qualify actual OS
process incarnation observation. These subprocesses are deterministic test
fixtures, not agents or local-model workers.

The production observation probe failed in this environment: child handles
reported namespace PIDs while the mounted `/proc` exposed a different namespace,
so .NET could not retrieve the child start time. The mechanism retained refusal
and observed root exit; it did not substitute a PID-only identity. A separate
`bwrap --unshare-all` probe failed to create the network namespace with
`Operation not permitted`. No local model executable was found in the checked
PATH. These observations concern this test environment, not the user's machine.

Next qualify the protected host adapter for the journaled application dispatch. Actual incarnation recovery, local model execution,
network denial, result intake and independent acceptance still require the
one-coordinator/two-worker pilot. Existing ECIR and remote-checkpoint gates remain
unchanged.
