---
id: JR-ROS-2026-A024
title: EX-ROS-2026-A024 context-continuity experiment execution journal
status: completed
version: 1.0.0
research_area: repository-operating-system
author_agent: anthropic/claude-code
created: 2026-10-07
updated: 2026-10-07
related_mission: EX-ROS-2026-A024
related_package: null
evidence_ids:
  - EV-ROS-2026-A074
  - EV-ROS-2026-A075
hypothesis_ids:
  - HY-ROS-2026-A030
  - HY-ROS-2026-A028
theory_ids: []
tags: [claude, agents, context, handoff, experiment, execution-journal, orchestration]
provenance:
  contributions:
    EXE-20261007T162653169Z-f15a70b7:
      operations: [created, modified]
      at: 2026-10-07T18:47:49.999Z
      last: 2026-10-07T22:37:01.694Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Execution journal for EX-ROS-2026-A024 up to the launcher blocker"
      evidence: [EV-ROS-2026-A074, EV-ROS-2026-A075]
---

# Objective

Run EX-ROS-2026-A024 under its preregistered protocol and
`prompts/CLAUDE-EX-ROS-2026-A024.md`: three isolated arms (continuous,
code-only fresh, structured handoff) over PRAXIS-GROUP-01..05 from frozen
baseline `8b4ffa3`, followed by blinded scoring and the frozen analysis.

# Starting state

`main` at `94258e4` holds the A024 protocol, HY-ROS-2026-A030, the handoff
schema and the orchestration prompt. Validation had two pre-existing errors:
missing provenance on the A024 and HY-A030 records authored by
openai/chatgpt. They are recorded in `run/phase0/` and resolved by this
execution's own `modified` contributions; the originator stays unknown.

# Steps

1. **Harness (frozen at `91745d9`).** Versioned A021 telemetry extractor,
   handoff validator, reproducible sanitized start, prompt template, 36 atomic
   acceptance criteria, rubric, deterministic checks, seeded and salted
   blinding, frozen analysis and manifest.
2. **Preflight.** A child session's configuration was observed
   (claude-opus-5-5, effort medium, 2.1.292). Pushing a transcript was denied
   by the auto-mode classifier, so the telemetry was redesigned to print and
   verify. A reviewer agent's 10 harness defects were fixed before the freeze.
3. **Start.** `ddda837`, identical on `experiment/a024-arm-{1,2,3}`; checks
   792 of 792.
4. **Execution.** Launch order B, C, A (seed 2026100724). A completed 01-05;
   B and C completed 01.
5. **Blocker.** Item-02 sessions were checked out at `ddda837`
   (deviations D1-D3). The cause, reproduced with three probes, is that an
   `outcome_branch` is pinned to the first head the platform saw. B's retry
   completed after a resync note; C's retry declined the note, which spent the
   arm-C item-02 retry. Execution stopped (`run/BLOCKER.txt`).

6. **Resumption.** The owner replied "Try it now", which was recorded as
   amendment A1. The probe-validated launcher (no `outcome_branch`) was used
   from then on. B completed 02r-05 and C completed 02r2-05. The runtime
   moved to 2.1.293 (D6). `arm-3-04` ran `git branch -a` (D7, a deviation).
7. **Scoring.** Deterministic checks pass at all heads (819, 855 and 841
   tests). The mapping commitment was committed first. Three fresh blinded
   scorers each found 36/36, with defects K 1, P 2, W 1. The one-line harness
   amendment 2 fixed a rename regression (D8).
8. **Blind evaluation** (fresh session), committed before unblinding:
   K 10, P 5, W 8.
9. **Unblinding.** The mapping (A = P, B = K, C = W) was verified by the
   commitment and by tree identity. Under the frozen analysis HY-A030 is not
   supported, and the A021/R2 architecture direction reversed.

# Decisions

- Neutral branch names (`arm-1/2/3`) were used so the treatment is not
  announced to arm agents.
- File reads plus searches were nominated as the primary discovery measure
  (the protocol's fallback), because Bash read detection is pattern-based.
- The orchestration channel was withdrawn after its asymmetric acceptance.
- The retry rule was not stretched. The owner decides on an amendment.

# Next action

A024 is closed. The proposed follow-ons in `run/FOLLOW-ONS.txt` are not run.
The guidance ablation has the highest information value.
