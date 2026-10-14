---
id: EV-ROS-2026-A075
title: EX-ROS-2026-A024 results — continuous context, code-only resets and structured handoffs
status: review
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-07
updated: 2026-10-07
research_area: repository-operating-system
evidence_type: primary
supports: [HY-ROS-2026-A028]
contradicts: [HY-ROS-2026-A030]
related_documents:
  - EX-ROS-2026-A024
  - HY-ROS-2026-A030
  - HY-ROS-2026-A028
  - EV-ROS-2026-A074
  - EV-ROS-2026-A070
  - EV-ROS-2026-A064
  - research/experiments/EX-ROS-2026-A024-run/analysis/summary.txt
  - research/experiments/EX-ROS-2026-A024-run/evaluation/report.txt
tags: [claude, agents, context, handoff, continuity, experiment, blind-evaluation]
confidence: low
derived_from: [EX-ROS-2026-A024, EV-ROS-2026-A074]
provenance:
  contributions:
    EXE-20261007T162653169Z-f15a70b7:
      operations: [created]
      at: 2026-10-07T22:36:58.379Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Recorded the EX-ROS-2026-A024 results under the frozen analysis"
---

# Evidence

## Claim

Under the preregistered protocol and frozen analysis, EX-ROS-2026-A024 does
**not support HY-ROS-2026-A030**.

- **Discovery.** Continuous execution (A) did less discovery than code-only
  fresh sessions (B): 112 file reads plus searches against 263. A structured
  handoff between fresh sessions (C) recovered none of that advantage:
  276, a recovery of -13/151, about -0.09.
- **Architecture.** Continuous execution did not produce the more coherent
  architecture this time. The blinded rubric composite was A 5, B 10, C 8, so
  there is no A-over-B effect to recover. That reverses the A021/R2
  direction on architecture.
- **Correctness.** All three arms passed all 36 frozen acceptance criteria.

This is one replayed cohort with one execution per arm and one same-family
evaluator. It is mechanism evidence at low confidence, not a population
estimate.

## Validity

- Frozen harness (manifest `91745d9`). Sanitized start `ddda837` (parent
  `8b4ffa3`), identical on all arms.
- Executor bound per request: claude-opus-5-5, effort medium. Runtime
  2.1.292, then 2.1.293 after a platform update (D6), recorded as a
  sensitivity note.
- Isolation: every session was a genuinely fresh `create_session`. Audits
  were clean except one branch-name listing in `arm-3-04` (D7, a deviation)
  and own-branch `FETCH_HEAD` false positives.
- Launcher defect: `outcome_branch` pinning (D1-D3). Owner amendment A1 (D5)
  made pre-start launcher failures not consume retries. Two rename-only harness
  amendments are proven by script (D4, D8). No treatment, threshold, margin,
  blinding rule or stop condition changed.
- Blinding: mapping commitment `508a8f94...` was committed before packaging.
  One fresh blinded scorer per snapshot. One fresh blinded evaluator (eval
  commit `1414a88`) was committed before unblinding. The mapping
  (continuous = P, code-only = K, handoff = W) is verified by the commitment
  and by exact tree identity.

## Results (raw; no combined score)

| Measure | A continuous | B code-only | C handoff |
| --- | --- | --- | --- |
| Acceptance (blinded) | 36/36 | 36/36 | 36/36 |
| Confirmed defects | 2 | 1 | 1 |
| Architecture D1-D5 | 1,1,1,1,1 = 5 | 2,2,2,2,2 = 10 | 2,2,2,1,1 = 8 |
| Divergence points | 8 | 5 | 6 |
| File reads + searches | 112 | 263 | 276 |
| Repeated-read excess / cross-session re-read paths | 15 / 0 | 55 / 18 | 60 / 20 |
| Governance reads | 6 | 16 | 21 |
| Sessions | 1 | 5 | 5 |
| Platform cost (USD) | 8.27 | 15.30 | 17.43 |
| Output tokens | 102,006 | 163,797 | 204,253 |
| Model requests | 94 | 231 | 236 |
| Elapsed (summed sessions) | 51 min | 129 min* | 78 min |
| Tests at head | 819/819 | 855/855 | 841/841 |

\* B's elapsed time includes a stale-start turn (D2) and a slow SDK install.
Including pre-start orchestration failures: B $15.69, C $18.24.

Frozen support rule: (1) calibration effect: yes, on discovery only;
(2) C better than B on discovery: no; (3) recovery of at least 0.50: no;
(4) correctness: yes; (5) no arm invalid: yes. **Not supported.**

## What the blinded evaluation found

- **A (snapshot P).** A second parser and store for declared groups, a second
  `queue.json` reader and a second execution-location rule. Durability
  verification was copied rather than shared, and `--json` handling was
  inconsistent on `show` errors.
- **B (snapshot K).** One store and one writer. The `grouping.groups`
  reader was extracted and shared, along with one validation path, one
  JSON envelope, and one `apply` used by every mutation. Durability
  verification was extracted from `work checkpoint` and reused.
- **C (snapshot W).** Shared reader, rules and classification. But the JSON
  refusal shapes are per-command, and create, add and remove write without the
  work-protocol lock. A probe confirmed a write while another process held the
  lock.

## Interpretation

1. **The A021/R2 architecture advantage did not replicate.** The resource
   advantage did: A cost 0.54x B and took 0.40x the elapsed time, with less
   repeated discovery. One plausible difference is guidance. A021's grouped
   prompt required a written group analysis covering common architecture,
   invariants and reusable abstractions. A024's continuous prompt only asked
   for the architecture to be decided once (shortened before the freeze so as
   not to paraphrase the rubric). Under A024, serial fresh sessions inheriting
   committed code converged on cleaner reuse than one long session. This is
   one observation and it is confounded by the prompt difference, not a
   general result.
2. **Structured handoffs did not reduce discovery.** C read and searched
   slightly more than B: each session also read the handoff, and C sessions
   read more governance material. Architecturally C sat between A and B.
   Correctness did not suffer.
3. **Correctness is at ceiling** for every arm, so it cannot discriminate
   between them.

## Assessment

- HY-ROS-2026-A030 is contradicted at low confidence by one valid execution.
- HY-ROS-2026-A028 is supported only on its repeated-work disjunct. Its
  architectural-consistency claim is not reproduced and is reversed here.
- No default Praxis execution strategy should change on this evidence.
