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
