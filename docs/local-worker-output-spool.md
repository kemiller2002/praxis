# Host-owned worker output spool

Work: [Praxis #226](https://github.com/kemiller2002/praxis/issues/226),
[RQ-ROS-2026-A028](../research/requirements/RQ-ROS-2026-A028--local-coordinator-and-worker-handoffs.md)
and draft [DF-ROS-2026-A060](../research/decisions/DF-ROS-2026-A060--local-coordinator-with-embedded-authority.md).

The optional isolated `LocalSpooledDispatch` composition preserves raw worker
stdout/stderr before [submission archival](local-worker-submissions.md). It has
no CLI execution route and does not enable the protected controller or complete
native work. Existing in-memory supervision/capture remains available for
mechanism fixtures; only the spooled composition provides this recovery path.

## Ordering and storage

After fresh reservation and launch intent, exclusively create a spool in a
host-provisioned root outside the repository. Its hashed dispatch directory has
`reservation.json`, `stdout.bin`, `stderr.bin`, and, only upon complete normal
exit, `seal.json`. The reservation uses the existing strict dispatch format.
No existing or partial attempt is reopened for writing. Contenders cannot both
own its writable streams, and failed/partial writes remain on disk.

The streaming supervisor gives the host owned raw byte chunks before retaining
them in memory. Each successful spool write includes a disk flush. Split UTF-8
characters remain raw bytes until completion. Both streams share a 64 KiB cap,
further restricted by the delegated output budget. Spooling refuses a larger
host-selected supervisor budget before reservation; it never silently truncates
that budget. Stderr is retained in the spool for diagnostic and byte-accounting
checks but is not copied into the submission archive.

A sink error stops the worker and prevents a normal complete submission. On
normal root exit, seal only when exact stdout/stderr bytes and combined count
match the supervisor, both streams decode as strict UTF-8, and process start,
incarnation and root exit are consistently observed. Nonzero normal exits may
be sealed as untrusted failure evidence. Interrupted, invalid or mismatched
streams cannot be sealed. A failed session cannot resume writing or sealing.

The bounded flat `praxis.local-worker-output-seal/1` JSON binds the exact packet
digest, original controller incarnation, observed process incarnation, exit
code, both raw-byte SHA-256 digests and both byte counts. It rejects missing,
extra and duplicate fields. Complete files and their checksums provide transport
integrity, not authority, authentication or independently observed exit.

The seal is exclusively written and flushed **before** journaled dispatch
appends `ExitObserved`. A sealing failure changes the process report to a stream
failure and prevents ordinary capture. An exit append failure retains the sealed
bytes while the dispatch report still requires reconciliation, including when
the attempted event bytes became readable. No retry launches this attempt.

## Restart recovery

`LocalSpooledDispatch.recover` requires independently supplied native and process
recovery, the requested repository/dispatch, the exact original reservation and
controller incarnation, a reconciled Exited journal, and the same process
incarnation. A seal alone cannot turn Running or LaunchUncertain into Exited.
The protected host must independently reconcile those cases. It must not treat
readable bytes as proof that a previously failed write was acknowledged.

Recovery reads bounded non-link files, verifies both digests/counts and unchanged
packet identity, then archives exact stdout using the existing immutable capture
checks and a fresh UTC capture time. Exact duplicate archives preserve the first
capture time. Recovery grants no acceptance; subsequent intake still refreshes
authority, dependencies, time and independent commit/artifact/validator evidence.
It starts no process and changes no native member state.

## Limits and verification

This closes loss of already-sealed output between dispatch exit persistence and
submission archival. A controller killed after a flushed chunk but before sealing
leaves partial bytes that cannot enter intake. Bytes still in worker/OS pipes or
an unacknowledged write may be lost on controller termination. Recovery must
reconcile incomplete attempts or receive a newly delegated attempt. This does
not promise completion for every crashed worker or exactly-once spawning.

Host filesystem ownership, same-account malicious mutation, path races, disk
failure, bounded storage-I/O latency, power-loss directory metadata durability,
descendant containment and actual OS incarnation remain qualification work.
Synchronous host writes must themselves have a qualified operational bound; a
process timeout alone does not bound stalled storage. No local-model/backend or
network-denial qualification is claimed, and remote-checkpoint policy is unchanged.

Release build and 17 spool tests pass. Coverage includes raw multibyte UTF-8,
partial/oversized files, changed bytes and metadata, strict seal decoding, failed
write/seal/exit persistence, pending recovery, wrong assignments/incarnations,
an actual subprocess with seal-before-exit observation, 20 exclusive-open races,
and controller-store fixtures forcibly terminated after partial-flush or sealed
acknowledgement. Unix link creation is conditional on Unix. Process identities,
authority and independent recovery observations are explicitly synthetic fixtures,
not an actual coordinator/two-agent offline pilot.
