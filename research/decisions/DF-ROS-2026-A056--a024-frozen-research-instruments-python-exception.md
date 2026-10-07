---
id: DF-ROS-2026-A056
title: The EX-ROS-2026-A024 frozen research instruments are approved Python exceptions under DF-ROS-2026-A054's frozen-instrument class
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-07
updated: 2026-10-07
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A024]
related_documents:
  - DF-ROS-2026-A054
  - RQ-ROS-2026-A024
  - EX-ROS-2026-A024
  - EV-ROS-2026-A074
  - EV-ROS-2026-A075
tags: [architecture, python, invariant, exception, decision, experiment]
confidence: high
derived_from: [DF-ROS-2026-A054]
provenance:
  contributions:
    EXE-20261007T162653169Z-f15a70b7:
      operations: [created]
      at: 2026-10-07T22:41:56.936Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Approved Python exceptions for the EX-ROS-2026-A024 frozen research instruments under DF-ROS-2026-A054's class"
---

# Context

DF-ROS-2026-A054 prohibits repository-owned Python. It approves exact-path
exceptions for **frozen research instruments**: evidence instruments, cited
by accepted evidence as the procedure that produced their results, which
cannot be rewritten without changing the instrument.

EX-ROS-2026-A024's harness was written in Python and frozen by hash in
`research/experiments/EX-ROS-2026-A024-harness/manifest.json` (commit
`91745d9`, 2026-10-07T16:57Z). That was before DF-ROS-2026-A054 reached
`main` (merged after 19:00Z the same day). The harness and the orchestrator's
run helpers produced EV-ROS-2026-A074 and EV-ROS-2026-A075, and the frozen
manifest pins them by hash.

The owner's A024 brief (2026-10-07) requires every harness file, script and
scoring output to be committed, and the repository to validate. Rewriting
the instruments in F# would change the frozen, hashed procedure the evidence
depends on. Deleting them would make the results irreproducible.

# Decision

The following exact paths are approved exceptions under DF-ROS-2026-A054's
frozen-research-instrument class:

- `research/experiments/EX-ROS-2026-A024-harness/`: `analysis.py`,
  `decode_telemetry.py`, `make_manifest.py`, `make_prompts.py`,
  `prepare_blind.py`, `session_telemetry.py`, `validate_handoff.py`,
  `verify_amendment_1.py`, `verify_amendment_2.py` and `run_checks.sh`;
- `research/experiments/EX-ROS-2026-A024-run/`: `assemble_analysis.py`,
  `ledger.py`, `collect.sh` and `check_handoff.sh`.

They are evidence instruments, not automation the repository runs. They
expire as Python exceptions when EX-ROS-2026-A024's evidence is superseded.
Any follow-on experiment uses an F# instrument, as DF-ROS-2026-A054 requires
(the `anthropic-claude-session` adapter already covers transcript telemetry).

# Acceptance basis

The executing agent recorded this decision as accepted under the owner's A024
brief, which requires the complete harness and evidence to be committed and
the repository to validate, applying the class the owner accepted in
DF-ROS-2026-A054. The owner may revoke it. Revoking it requires porting or
removing these instruments and marking the A024 evidence as no longer
reproducible from the repository.

# Consequences

- `ros.json` `implementationPolicy.exceptions` lists the fourteen paths,
  each citing this decision.
- No new Python automation is approved: the class is limited to frozen,
  hash-pinned research instruments.
