# Journaled local worker composition

Work: [Praxis #226](https://github.com/kemiller2002/praxis/issues/226),
[RQ-ROS-2026-A028](../research/requirements/RQ-ROS-2026-A028--local-coordinator-and-worker-handoffs.md)
and draft [DF-ROS-2026-A060](../research/decisions/DF-ROS-2026-A060--local-coordinator-with-embedded-authority.md).

`LocalJournaledDispatch` connects the isolated application journal and worker
supervision ports. It has no CLI/runtime execution route and does not supply a
qualified controller, protected locks, a local model or worker containment.
The caller must hold protected policy/repository locks through authorization,
launch and observation. Fixture ports are not authentication or qualification.

## Ordered launch

The host selects the executable, argv, worktree and environment. Its stdin must
exactly equal the canonical JSON of the delegated packet; timeout and output
budgets cannot exceed that packet. Cancellation or invalid selection refuses
before reservation. Canonical ordering preserves the assignment across journal
serialization without making changed identity acceptable.

Fresh native recovery, authority, prerequisites and time are checked for the
exclusive reservation and again for intent. Only a newly created reservation
can proceed. An existing reservation always requires reconciliation instead of
automatic launch, including one owned by the same controller. A failed check or
write can leave a reserved or uncertain journal; it never authorizes retry.

Intent persistence must report success before the worker port is called. The
start callback loads the exact assignment and controller incarnation and saves
the observed process identity. The bounded process mechanism delivers stdin
only after this callback succeeds. Root exit can be saved only after successful
start recording and consistent worker observations of that same incarnation.
A changed controller cannot close the previous controller's process tracking.

A supervisor-confirmed preflight refusal or cancellation with no process
creation can close the attempt as NoStart. A failed start, thrown worker port,
missing identity, repeated callback, contradictory observation or unconfirmed
root exit retains uncertainty. A new attempt needs new delegated identity.
An attempt is never recycled into a second launch.

## Persistence failures and recovery

The report contains the observed process result, recoverable journal phase,
problems and a reconciliation-required flag. Errors before the worker is invoked
return refusal; a reservation or partially written intent may still exist and
must be inspected. Errors after intent retain a report when possible.

Readable event bytes do not erase a failed append/flush result. Failure while
recording start or exit requires reconciliation even if loading those files
shows Running or Exited. A start-recording failure never leads to an accepted
exit append in this invocation. Repeated invocation refuses the immutable
reservation instead of launching again. Recovery of pending native transactions,
OS incarnation and uncertain process state remains the protected host's job.

NoStart and Exited close only process tracking. They grant no result acceptance,
integration or work completion. The [submission archive](local-worker-submissions.md) now persists complete
stdout before fresh independent intake in isolated composition. Protected
acceptance and integration journals remain pending. A crash after root exit but before saving that fact leaves the earlier
state uncertain. The optional [host output spool](local-worker-output-spool.md)
seals bounded raw streams before the exit append, preserving complete output
while independent reconciliation of an uncertain journal remains required.
No exactly-once spawning or power-loss durability is claimed.

## Verification

The Release build and 16 journaled-dispatch tests pass.
Fixtures exercise actual file journals with fake host authority and process
ports, including failed writes both before and after bytes become readable,
revocation between reservation and intent, duplicate calls, uncertain starts,
missing root exit, repeated/inconsistent observations and changed controller
incarnation. A competing-dispatch fixture checks only one worker invocation.
An additional actual subprocess fixture receives the exact packet and persists
intent/start/exit using an explicitly synthetic identity observer. These fixtures
are not a coordinator/two-agent local-model pilot. Actual OS incarnation and
network sandbox qualification remain blocked in this environment as documented
in [worker supervision](local-worker-supervision.md).
