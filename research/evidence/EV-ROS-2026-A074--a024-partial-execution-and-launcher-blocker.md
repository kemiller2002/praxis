---
id: EV-ROS-2026-A074
title: EX-ROS-2026-A024 partial execution and platform launcher blocker
status: review
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-07
updated: 2026-10-07
research_area: repository-operating-system
evidence_type: primary
supports: []
contradicts: []
related_documents:
  - EX-ROS-2026-A024
  - HY-ROS-2026-A030
  - HY-ROS-2026-A028
  - research/experiments/EX-ROS-2026-A024-harness/manifest.json
  - research/experiments/EX-ROS-2026-A024-run/sessions.json
  - research/experiments/EX-ROS-2026-A024-run/deviations.txt
  - research/experiments/EX-ROS-2026-A024-run/BLOCKER.txt
tags: [claude, agents, context, handoff, experiment, feasibility, orchestration]
confidence: high
derived_from: [EX-ROS-2026-A024]
provenance:
  contributions:
    EXE-20261007T162653169Z-f15a70b7:
      operations: [created, modified]
      at: 2026-10-07T18:47:24.444Z
      last: 2026-10-07T22:37:02.532Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Recorded the EX-ROS-2026-A024 partial execution and the platform launcher blocker without changing the frozen protocol"
---

# Evidence

## Claim

EX-ROS-2026-A024 was frozen and started faithfully, but it is **blocked
before any comparison is possible**. Arm A (continuous) is complete, arm B
(code-only) has completed items 01-02, and arm C (structured handoff) has
completed item 01. The arm-C item-02 slot spent its single preregistered
retry on a defect in the platform launcher: `create_session` with
`outcome_branch` pins that branch to the first head the platform saw, so
every serial fresh session after item 01 started from the common start
commit and not the predecessor's head. The frozen start check stopped those
sessions before they changed anything.

This record is about **execution feasibility only**. It neither supports nor
contradicts HY-ROS-2026-A030 or HY-ROS-2026-A028: no blinded scoring was done
and no arm comparison was made. The per-session numbers below are raw
telemetry, not results.

## 1. What was frozen before any arm ran

- Manifest `research/experiments/EX-ROS-2026-A024-harness/manifest.json`
  (commit `91745d9`). It hashes the prompts, handoff schema, telemetry
  extractor, 36 atomic acceptance criteria, rubric, blinding script and
  analysis script. `make_manifest.py --check` reports no drift.
- Sanitized start `ddda837`: its only parent is the baseline `8b4ffa3`, it adds
  the four harness files only, and it is reproducible with `build_start.sh`.
  All three arm branches started from tree `f080df4` (`verify_start.sh`).
- Executor binding: claude-opus-5-5, effort medium, Claude Code 2.1.292, one
  environment, auto permission mode. It was established by preflight probe
  sessions before freezing.
- Deterministic checks at the start: build passed, 792 of 792 F# tests
  passed, `./ros validate` passed.

## 2. Harness findings before the freeze

- The first telemetry design pushed the transcript to a telemetry ref. The
  platform's auto-mode classifier denied that as data exfiltration (probe
  `session_01K2uq5tGob5heiRq38Eti7j`). The extractor was redesigned before
  the freeze: it prints gzip+base64 metrics with a SHA-256, and the
  orchestrator reads and verifies them from the platform event record.
- An independent harness review before the freeze found 10 defects. All were
  fixed before freezing (`run/phase0/preflight.json`).

## 3. Sessions run (raw telemetry; not a comparison)

| Label | Arm | Items | Outcome | Cost USD | Output tokens | Requests (transcript) | Reads + searches | Audit events |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| arm-1 | A | 01-05 | completed | 8.27 | 102,006 | 94 | 46 + 66 | 0 |
| arm-2-01 | B | 01 | completed | 3.47 | 38,010 | 51 | 29 + 44 | 0 |
| arm-3-01 | C | 01 | completed (+ valid handoff) | 3.71 | 46,391 | 52 | 35 + 39 | 0 |
| arm-2-02 | B | 02 | stale checkout, stopped | 0.39 | 842 | - | - | - |
| arm-3-02 | C | 02 | stale checkout, stopped | 0.39 | 861 | - | - | - |
| arm-3-02r | C | 02 | stale checkout, stopped; declined resync | 0.42 | 2,256 | - | - | - |
| arm-2-02r | B | 02 | completed after resync note | 2.96 | 29,080 | 49 | 26 + 25 | 0 |

Every completed session reports claude-opus-5-5, effort medium and runtime
2.1.292 on every request, with no subagents and no compactions. Cost and
token figures come from `get_session external_metadata.usage`. Request and
discovery counts come from the digest-verified session telemetry.

## 4. The blocker

See `research/experiments/EX-ROS-2026-A024-run/deviations.txt` (D1-D3) and
`BLOCKER.txt`. Three probes on a throwaway branch reproduced the cause:

- with `outcome_branch`, the checkout and `origin/<branch>` both read the
  first-seen head;
- without `outcome_branch`, the checkout is the current head (detached) and
  pushing to the branch works.

The orchestrator sent one in-session resync note to both item-02 retries.
B's session accepted it; C's declined it, as a cross-session message cannot
override the frozen stop instruction. That asymmetry is why the channel was
withdrawn.

## 5. Implications

- For Praxis: a serial fresh-agent pipeline on this platform must not rely
  on `outcome_branch` for a branch that earlier sessions advanced. The
  frozen start check (`git rev-parse HEAD` against the recorded head) caught
  the defect before any contamination. Without it, B and C would have
  silently re-implemented item 02 on the item-01-less base, which is the same
  stale-baseline failure mode R2 suffered (EV-ROS-2026-A070, "Threats").
- For the experiment: the owner must choose an amendment (BLOCKER.txt option
  1, 2 or 3) before A024 can continue. No threshold, treatment, blinding or
  stop rule was changed.

## Assessment

High confidence that A024 is blocked and why: the probe evidence is direct
and reproducible. No inference about HY-ROS-2026-A030 or HY-ROS-2026-A028 is
drawn.

## Resolution (2026-10-07)

The owner accepted BLOCKER option 1 at 20:48Z (amendment A1, deviation D5).
The remaining B and C sessions ran with the probe-validated launcher, with no
`outcome_branch`, and every one passed its start check. A024 then completed.
The results are in EV-ROS-2026-A075. This record still covers execution
feasibility only.
