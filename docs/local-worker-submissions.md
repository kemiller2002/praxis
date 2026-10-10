# Durable local worker submissions

Work: [Praxis #226](https://github.com/kemiller2002/praxis/issues/226),
[RQ-ROS-2026-A028](../research/requirements/RQ-ROS-2026-A028--local-coordinator-and-worker-handoffs.md)
and draft [DF-ROS-2026-A060](../research/decisions/DF-ROS-2026-A060--local-coordinator-with-embedded-authority.md).

The isolated application composition now archives complete worker stdout before
independent intake. It has no CLI/runtime execution route and performs no native
work completion or integration. Archive records remain untrusted submissions.
Protected host authority, locks, OS incarnation and containment remain unqualified.

## Archive format and ordering

`praxis.local-worker-submission/1` binds repository, dispatch, attempt, exact packet
digest, process incarnation, UTC capture time, observed exit code and unchanged
stdout bytes. The payload is strict UTF-8, bounded to 64 KiB and encoded as
canonical Base64 inside a bounded 128 KiB record. Its SHA-256 detects changed
payload bytes; it is not a signature, authentication or accepted evidence.
Strict decoding refuses missing, extra or duplicate metadata, noncanonical
encoding, invalid UTF-8, changed checksums and unknown schema versions.

`dispatchAndCapture` runs journaled supervision and then archives the observed
stdout. Capture requires an unambiguous dispatch report, exact immutable
assignment and original controller incarnation, successful root-exit tracking,
complete normal-exit streams and matching combined stdout/stderr byte accounting.
It refuses interrupted streams, output-limit failures and invalid Unicode. Stderr
is not retained in the submission record. The result transport remains 64 KiB even
when a delegated process has a larger combined diagnostic-output budget.

Malformed result JSON, empty stdout and nonzero normal exits can be archived
unchanged for inspection. Capture may preserve evidence after a receipt expires;
that does not extend its authority. It never interprets model prose as approval.

The host provisions a separate archive root outside the repository and protects
it from workers. Hashed dispatch directories contain one `submission.json`.
Exclusive creation and disk flush precede a successful new save. Exact duplicate
content preserves the first capture time; changed payload, assignment, process
identity or exit code cannot replace it. Partial files, unknown entries and
observed links refuse access without being overwritten. Shared file mechanics
also keep failures after exclusive creation distinct from an existing-file
collision, so a failed write/flush is not converted to successful duplicate save.
These mechanics do not establish OS ownership or power-loss metadata durability.

## Fresh independent intake

On restart, load the archive and its original dispatch journal. Require the
requested repository/dispatch, exact packet, attempt, process incarnation and
an Exited journal, with capture
not preceding recorded exit. The protected host must reconcile pending native
transactions and actual process state; decoded files alone do not supply that
proof. A new controller can inspect the old submission only through these
independent recovery ports.

Nonzero exit cannot enter integration intake. For exit zero, decode the existing
strict worker-result contract and independently observe actual commit, ancestry,
paths, per-member execution attribution, artifact bytes and pinned acceptance.
After those potentially lengthy observations, refresh authority, prerequisites
and time. Revocation or expiry during validation refuses intake. Changed source,
blueprint, receipt, cohort, decisions or requirements cannot be substituted.

Successful checking yields only AwaitingIntegration or NoIntegration. No accepted
marker is stored, no member is completed, no commit is merged and no process is
started by inspection. Every inspection repeats fresh checks; integration must
still be separately serialized, revalidated and journaled.

## Crash limits and verification

The archive survives controller interruption after its save completes. A crash
before archival completes can leave no submission or a partial file; intake
refuses it. Output remains in memory until capture, so a crash between root-exit
tracking and capture can lose stdout. This slice does not close that interval
with streaming/spooled output. The immutable dispatch still prevents blind
relaunch; recovery must reconcile it or receive a newly delegated attempt.
Actual power-loss, malicious same-account mutation and the full offline pilot
remain unqualified.

Release build and 20 tests pass. Tests include reopened stores, a controller-store
fixture terminated after its flushed-save acknowledgement, an actual worker
subprocess fixture followed by reopened independent intake, 50 two-contender
archive races, immutable replacement refusal, malformed claims, nonzero exits,
partial/oversized/invalid files, substituted metadata, cross-wired lookup replies,
revoked/expired authority,
expiry during observation, unavailable native/process recovery and forged
acceptance evidence. Unix link creation is tested on Unix; it is conditional on
Windows. Fixture authority, validator observations and process identities are
synthetic and do not qualify agents, containment or the local-model backend.
