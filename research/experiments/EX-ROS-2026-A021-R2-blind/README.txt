EX-ROS-2026-A021-R2 -- blind evaluation bundle
===============================================

(Plain text on purpose: every *.md under research/experiments is parsed as an
experiment record by `./ros validate`.)

What this is
------------
Two independent implementations of the same five work items,
PRAXIS-GROUP-01..05 (`praxis work group create|show|add|remove|checkpoint`),
each started from the same baseline commit:

    8b4ffa392e93b19bf39f6672a608954c934cb815

They are presented under neutral names, arm-M and arm-N. The mapping from
neutral name to execution mode was chosen at random by the orchestrator and is
withheld until evaluation is complete; mapping-commitment.sha256 is the SHA-256
of the string "arm-M=<mode>;arm-N=<mode>;salt=<hex>", so the mapping, when
revealed, can be checked against this commitment.

Contents
--------
- arm-M.patch, arm-N.patch   `git diff` of each arm against the baseline
                             (product code, tests and docs only).
- acceptance-criteria.txt    the five work items and their acceptance
                             criteria, verbatim from the baseline backlog.
- mapping-commitment.sha256  the commitment described above.
- SHA256SUMS                 checksums of the patches and criteria.

The same trees are available as single squashed commits on top of the
baseline, so they can be built and tested:

    blind/a021-r2/arm-M   72a6c0408140bce1b9c1b60fe174e7080d3f3c05
    blind/a021-r2/arm-N   f6d0b5c49a2c592d3d8791dc819518578116a1cc

What was removed, identically for both arms
-------------------------------------------
- Praxis state (.ros/) is reset to the baseline: execution telemetry, events,
  work-item lifecycle records and session identities would reveal the arm.
  `./ros work show PRAXIS-GROUP-0N` on a blind branch therefore reports the
  baseline state (`ready`); lifecycle completion was verified separately by
  the orchestrator and is not part of this evaluation.
- Each arm's own experiment records (research/experiments/EX-ROS-2026-A021-R2-*:
  metrics, run logs, analysis notes).
- Commit history: each arm is one squashed commit with a neutral author and
  date. Commit count, commit shape and merge history are not visible.

Nothing else was changed. Code comments, documentation text and file layout
are exactly as the arms wrote them, so blinding is partial: style or wording
may still hint at the arm.

Rules for the evaluator
-----------------------
- Use only this bundle, the two blind/a021-r2/arm-* branches and the baseline
  commit.
- Do NOT fetch, read or check out any experiment/a021-* branch (original or
  R2), the orchestration branch claude/a021-r2-orchestration-e0gty2, any
  research/experiments/EX-ROS-2026-A021-R2-* directory other than this one, or
  EV-ROS-2026-A060, EV-ROS-2026-A061, EV-ROS-2026-A062.
- Do not try to recover the mapping, for example by comparing trees with other
  branches.
- Implement nothing; change neither arm.

Suggested assessment (EX-ROS-2026-A021, "Evaluation")
-----------------------------------------------------
For each item and overall: acceptance criteria met, tests, architectural
consistency, duplicated abstractions, unnecessary dependencies, conflicting
design decisions, duplication, compatibility, maintainability, churn,
technical debt, and cross-item consequences (one store model or several; one
member-validation rule or several; one checkpoint shape). Record findings as
evidence, not as a score.

Building a blind branch in this environment (as both arms did): .NET SDK 10
from the Ubuntu archive, then
    dotnet build Ros.slnx -c Release -p:FSharpCoreImplicitPackageVersion=10.1.400
