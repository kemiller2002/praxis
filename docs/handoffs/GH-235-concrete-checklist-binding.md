# GH-235 concrete checklist binding handoff

Work item: GH-235. Draft integration PR: #238. Active execution:
`EXE-20261011T234058504Z-3b80355e`. Equivalent OS isolation remains deferred.

## Implemented

Integrated the independent checklist branch (#236, head b1a7fd92) with main's
portable validator runners. The user explicitly authorized extending the native
history merger after its strict-prefix refusal. Shared events now anchor an
ordered merge; base events must remain unchanged and in relative order. Native
apply reconciled 301 work records and 1,013 events. Original event bytes and
order from all three Git stages were independently checked. Sixteen history
tests passed. Decision DF-ROS-2026-A069 records the change and its native
provenance. Source tree was published as commit
41875ee5e83c5a2196101cc37e81ed356490491b; native checkpoint
effd4f3f1d539acf74ab2f13 records remote verification of that exact commit.

Added `FileWorkerChecklist` bindings for an exact host-held assignment, live
policy, pinned requests, cancellation, clock and lease/authority guard. Reports
come directly from real `FileLocalValidator.execute` calls; test rows require
TRX. Each row observes exact candidate/input pins, all manifests must agree,
archived evidence is snapshot-verified, and context is checked after commands.
Identical requests reuse one actual report only within an evaluation, with
fresh input/archive checks per row. Later evaluations do not reuse approvals.

Added bounded `FileLocalValidator.verifyEvidence`, concrete checklist tests,
three-OS CI coverage and controller integration documentation. These are
library bindings, not live-host custody, lifecycle locking, native completion
activation or a real model-worker pilot.

The concrete checklist build passed with zero warnings/errors; all seven
concrete checklist tests and ten validator regressions passed locally. The
first integration's Linux/macOS history and all three worker-portability jobs
passed. Windows history CI exposed a forged-index fixture using platform
CRLF in Git's LF-only index-info protocol. Changed the fixture to explicit LF
and added an assertion that the forged stage really was installed. Final
Windows CI and full regression remain required, not assumed passing.

## Current blocker

Native `provenance record --id DF-ROS-2026-A068 --operation modified` refused
twice with `timed out waiting for Praxis lock 'work-protocol'`. The lock file
`.ros/locks/57e6cb7412f4ae8ac889c97935eafdeb577425154fe81507a9ba201537ef403e.lock`
names pid 2, acquired at 2026-10-12T06:42:04.7902188Z, during the completed
checkpoint invocation. Read-only inspection found no `/proc/2` entry. The
lock has not been removed, bypassed or rewritten. Do not silently claim native
provenance, validation or final completion for this new slice. Its changes to
DF-ROS-2026-A068 still require the native modified contribution; the previous
created contribution remains intact. Investigate lock ownership/recovery in
a qualified runtime before native writes. WI-0087 tracks lifecycle serialization.

## Next steps

Read actual PR checks and test results, then recover native bookkeeping safely,
record the modified decision contribution, rebuild registries and validate.
Run full regressions and the new three-OS checklist tests on the final source.
Only then mark ready and merge. The controller still needs native prerequisite
evidence custody, serialized envelope/lifecycle writers, effect-boundary
rechecks and fresh pinned validation slots or a qualified immutable cache.
No `claude`, `codex`, `ollama` or `llama-cli` backend is configured here; do not
use fixtures as evidence of a live coordinator/two-worker model pilot.
