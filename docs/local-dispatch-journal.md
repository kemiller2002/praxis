# Local dispatch journal prototype

Work: [Praxis #226](https://github.com/kemiller2002/praxis/issues/226),
[RQ-ROS-2026-A028](../research/requirements/RQ-ROS-2026-A028--local-coordinator-and-worker-handoffs.md)
and draft [DF-ROS-2026-A060](../research/decisions/DF-ROS-2026-A060--local-coordinator-with-embedded-authority.md).

This isolated mechanism records an immutable worker assignment before a future
controller launches a process. It does not start processes, change native work
state, validate an actual OS process or enable the execution path. File-backed
fixtures run under one unrestricted account and do not qualify host protection.

## Reservation and observations

The versioned `praxis.local-dispatch-reservation/1` record binds the complete
packet, reservation time and controller-session incarnation. This incarnation
must be an authenticated host observation, distinct from an actor flag and from
a PID that the OS might reuse. `praxis.local-dispatch-event/1` records strict,
contiguous, monotonic observations. No event can complete work.

| State | Meaning and permitted next observation |
| --- | --- |
| Reserved | Assignment persisted. The originating controller may persist launch intent after fresh checks; a restarted controller must reconcile first. Confirmed no-start can close the attempt. |
| LaunchUncertain | Intent persisted before a future launch. A crash could occur before or after process creation. Only observed start identity or independently confirmed no-start resolves uncertainty; another launch intent is refused. |
| Running | Host has observed the exact process incarnation. Only exit of that same incarnation can close process tracking. |
| NoStart | Host has independently confirmed no process started. Terminal; another launch requires a newly delegated dispatch/attempt. |
| Exited | Host observed exit of the same process incarnation. Terminal for launching; result intake, independent acceptance and integration remain separate obligations. |

The application service requires successful native/ECIR recovery observations,
fresh exact delegation, current authority revision, prerequisite evidence and
validity before reservation and launch intent. A different controller session
cannot record launch intent against an earlier reservation. The caller must
hold protected policy/repository locks through checking and exclusive writing.
These ports and strings alone prove neither authentication nor OS containment;
the actual protected controller remains pending.

## File mechanism and recovery

The host provisions a journal directory outside the repository. Its ownership
and worker access restrictions remain the host's responsibility. Configuration
refuses repository-contained roots and observed links. Directory names derive
from repository/dispatch identity, not interpolated model filenames. Each
attempt has an immutable reservation and at most three event files.

Final filenames are reserved with `FileMode.CreateNew`, written and flushed to
disk before success returns. Existing reservations report `Existing` instead of
another launchable creation. Reusing a dispatch with a changed packet is refused.
Event creation supplies an exclusive sequence check; concurrent contenders
cannot both append an accepted intent. Files are bounded to 128 KiB and decoded
strictly. Missing sequences, unknown entries, malformed/partial files, changed
identity and illegal state transitions refuse recovery without overwriting the
evidence. A failed append or failed flush never authorizes a process launch.

An uncertain reservation or start requires host reconciliation. Never delete its
journal, infer no-start from a missing PID, blindly relaunch it, or treat an exit
as work completion. The future reconciler must observe actual processes and
native transaction state. The journal does not supply those observations itself.

The implemented checks cover process-interruption mechanics with reopened
stores and injected partial records. They do not qualify filesystem metadata
persistence across power loss, disk loss or malicious same-account mutation.
This is a dispatch journal, not the proposed local checkpoint profile: existing
remote-verified checkpoint requirements remain unchanged.

## Verification and next connection

Release build and 10 journal tests pass. Tests include 50 two-contender
reservation races, competing intent writes, immutable dispatch binding, reopened
stores, new controller sessions, fresh revocation/expiry/dependency checks,
process-incarnation mismatch, partial files and sequence gaps. Unix host-root
link refusal is checked on Unix; that creation test is conditional on Windows.

The isolated [journaled application composition](local-journaled-dispatch.md) now
connects these ports to bounded supervision in fixtures. Next qualify the
protected controller and actual process observation adapter, then add durable
qualified result acceptance and integration handling.
Qualification still needs one coordinator and two actual workers with an
installed local model, outbound network denied and independent frozen acceptance.

The isolated [submission archive](local-worker-submissions.md) now preserves
complete stdout and rechecks claims independently after reopening. It supplies
no integration or work-completion authority.
