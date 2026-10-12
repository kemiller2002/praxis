# GH-235 ordering conflict after concurrent main integration

PR #238 head: 41299dc1c0e2d7f4a6e55bd8a1325d2051b112f5.
Main after PR #236: 4a6c8d1f. Both branches remain preserved on the remote.

Before main advanced, all PR #238 final-head workflows passed: full validation,
native release, public site, Linux/Windows/macOS checklists/history, worker
portability including concrete binding and lifecycle. Native provenance and
registry validation are complete. Native checkpoint dfd931f9c98e25965d515f92
and its metadata are durably published. The old checkout lock was left intact;
normal commands succeeded in a fresh exclusive worktree.

The new merge has two real common ancestors, b1a7fd92 and aa26c0ff. Git
auto-merged context but conflicted events and source. An isolated native-tool
extension now handles compatible multiple-ancestor histories and auto-merged
paths from actual commits. Release build passed with zero warnings/errors and
24 history tests passed, including criss-cross fixtures, forged staged data,
edited working history, exact interrupted recovery and ordering-cycle refusal.
This support is proposed separately for review and has not rewritten history.

Actual read-only preview refused: `event histories have incompatible ordering
(cycle)`. On PR #238, GH-235 checkpoint d94bbd8d26543a5e842a69b8 precedes GH-234
checkpoint 2929a5ea8177e461792fd393. Main orders those same records oppositely.
There is no total order preserving both complete branch orders. No native apply
ran. The attempted source merge was safely aborted, leaving the checkout clean.

Proposed owner choice: use main's shared-event order as canonical, retain every
feature-only event and every original event byte, preserve causal member state
and native provenance, and append a recorded reconciliation through the native
journal. This changes a prior published feature-branch order. It needs explicit
approval and a reviewed native amendment implementation. Do not hand-edit `.ros`,
silently choose a conflicted branch, fake a common ancestor, claim PR #238 merged,
or relax cycle refusal merely to obtain green CI. DF-ROS-2026-A069 v1.1 records
the proposed support and the unresolved ordering decision.

## Approved continuation, 2026-10-12

The owner explicitly approved the canonical-main ordering change. The same
GH-235 execution and qualification step have been resumed through native
commands on `feat/approved-order-amendment`, explicitly rebinding the workspace.
The guarded opt-in amendment implementation is published at 97d78663 on PR
#239. A clean Release rebuild passed with zero warnings/errors and all 31
history tests passed. Verified native checkpoint metadata is published at
123da343. DF-ROS-2026-A069 v1.2 records the approval and amendment contract.

## Native integration applied, 2026-10-12

Source head: 123da34331a7693cd85b8623b32184a514ed75ae.
Incoming main: 4a6c8d1f786bcb687f9513ad48e9484ccffd1537.
Approval decision digest:
`sha256:0e3918e00bd8824af4dd5e4a52c2e512ab6d0784ad8d05338ba04cd3f63baaf3`.
Fixed reconciliation observation: 2026-10-12T11:45:05.822Z.
Native audit event: e389adc7950a88e93e0b3ea6.

Ordinary preview still refused the ordering cycle. Approved preview succeeded
with 301 work records and 1,027 events. Apply in the preview checkout refused a
retained history-merge lock and changed no history. That lock and checkout are
preserved. A fresh exclusive checkout of the same exact source and incoming
commits applied the identical pinned amendment through the native journal.
No lock was removed or bypassed, and the lock-lifecycle defect is not fixed.

Independent readback verified 1,026 original events byte-for-byte, exactly one
new content-addressed audit event, all incoming event ordering, all head-only
ordering, 1,172 per-branch scope-order projections and approval digest binding.
Context retains 301 work records. Source conflict resolution retains the
upstream ordering graph, expands it for real multiple bases and opt-in
amendments, preserves upstream regression cases and typed checklist fixtures,
and rebuilds the decision registry natively. Integrated build/validation and
fresh cross-platform CI are next. Neither PR is merged and no live worker
runtime is activated by this integration.
