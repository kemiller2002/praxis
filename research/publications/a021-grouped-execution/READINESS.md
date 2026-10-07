# Submission-readiness decision

**Decision (2026-10-07): C. PUBLICATION PACKAGE READY, MORE EMPIRICAL WORK REQUIRED.**

The package is complete:
- a full paper, `manuscript/paper.tex`;
- an early-results variant, `manuscript/paper-short.tex`;
- regenerable data, tables and checks;
- a deterministic anonymous artifact in two editions;
- audits, reviews and errata.

The evidence does not yet support submitting either paper:
- The full paper must not go to SANER 2027 Agentic AI4SE.
- The short paper must not be submitted until the blocking items below are closed.

Of the four states, this is neither A nor B. It is not D: no fatal flaw invalidates the study as a hypothesis-generating case study, provided the treatment is described as the bundle it was.

## What the evidence establishes

The scope is narrow: two executions, one per arm in each study, of one five-item high-affinity cohort in one repository, using one implementing model family.

1. **Resources (RQ1).** In both executions, cohort execution used less platform cost and less summed session time than per-item execution.
   - Measured on worker sessions only, the per-item arm cost about 2.3× (original study) and 2.1× (re-execution) as much.
   - Summed active session time was about 45% and 44% lower for the cohort arm.
   - The size of these gaps is not established:
     - there is one run per arm;
     - published run-to-run variance of agent token use is large;
     - each per-item session repeats setup;
     - the per-item arm wrote more code and tests.
   - All values are generated from `data/metrics.json`.
2. **Architectural consistency (RQ2).** In both executions the cohort arm applied one member-admission rule, one error/exit contract and locked writes, where the per-item arm diverged. The per-item arm's lost update is observed at runtime.
   - In the original study only, member classification and the ID grammar also diverged. These differences depend on a stale-start item.
3. **Local correctness (RQ3).** Neither arm dominated.
   - The per-item arm validated checkpoint ownership more fully. In the original study that advantage comes only from the stale-start item.
   - The per-item arm also reused more existing baseline rules and added more tests.
   - Both arms have confirmed defects, two each in the re-execution.
   - The cohort arm's shared admission rule ignores the planner's external-repository inference in both studies.
4. **Replication (RQ4).** The direction recurred for resources, the three named consistency dimensions, and the per-item checkpoint-validation advantage. Classification and ID grammar did not recur. The re-execution is internal, non-preregistered and correlated with the original. It is not an independent replication.

## Why not A (full paper)

Both fresh hostile reviews rated the full paper Reject at confidence 4: `reviews/review-hostile-1.md` and `reviews/review-hostile-2-final.md`. The statistics review (`reviews/review-statistics.md`) found the arithmetic correct and the language defensible only after its fixes. The reasons cannot be fixed by writing:

- **n = 1 per arm per study.** Two correlated executions cannot separate a roughly 2× resource difference from run-to-run variance.
- **Treatment bundle (validity-audit T01; AD-01).** Retained context and the mandated cross-item analysis are confounded. The analysis prompt asks for exactly the shared invariants the rubric later scores.
- **Workspace contamination (AD-11).** The implementers' repository contained the hypothesis, the protocol and cohort labels.
- **Post hoc rubric with one AI coder.** There is no human second coder and no inter-rater agreement (AD-05).
- **Ineffective blinding in the original study (T02, AD-02).** The re-execution's blinding rests on the evaluator's own report.
- **Narrow novelty (AD-06).** Prior work already shows that how work is partitioned among agents matters (CooperBench and others).

## Why not B (short/early-results paper) yet

A 4-page early-results variant exists and is honest. The final reviewer rated it Weak reject, and weak accept is reachable. Submission is still blocked by:

| Blocker | Gate | Work item |
| --- | --- | --- |
| No independent human coding of the consistency rubric. Its main exhibit (D2) was reclassified twice during this review (AD-10). | 2 / 9 | WI-0075 |
| 21 of the 36 references cited in the full paper (6 of 11 in the short paper), including key comparators, were verified only from search-engine extracts. The network policy blocked arXiv, DOI, ACM, IEEE, DBLP, OpenReview and ACL. | 6 | WI-0076 |
| Venue facts come from search extracts. The official pages were egress-blocked. The IEEE AI-disclosure rule conflicts with SANER's no-acknowledgments rule. | — | WI-0077 |
| At least three executions per arm are needed to bound run-to-run variance. Both final reviewers name this as the main path to B. | 1 / 4 | WI-0078 (with EX-ROS-2026-A024) |

If WI-0075, WI-0076 and WI-0077 close and the human coding agrees with the audit, the short paper becomes **B**. That holds even without new runs, provided the variance limitation stays prominent. Adding at least three executions per arm would make B robust.

**Deadline note.** The SANER 2027 abstract deadline is 2026-10-19 AoE and the paper deadline 2026-10-23 AoE. Both are search-extract verified, not confirmed on the official page. Closing WI-0075 to WI-0077 before then is possible but not assured. Do not submit to meet the deadline if they are open. The fallback venue list is in `submission-plan.md`.

## Quality gates

| Gate | Status | Evidence |
| --- | --- | --- |
| 1 Numbers | **Pass** | Every manuscript number is a macro generated from `data/metrics.json`. `scripts/extract_metrics.py` reads pinned commits. `verify_metrics.py`: 58 match, and all 36 non-matches are documented conflicts (CF-01..09) or errata (EV-ROS-2026-A074). `check_manuscript.py` rejects bare result numbers. |
| 2 Claims | **Pass with caveat** | `data/evidence-index.json` holds 61 claims, each classified, sourced and given a confidence level. Architecture claims rest on one AI auditor (see blocker). |
| 3 Symmetry | **Pass** | One rubric was applied to all four implementations with code citations, runtime probes and falsification attempts (`architecture-findings.md`, `check_architecture_findings.py`). |
| 4 Replication | **Pass** | The two studies are reported in separate sections and columns. The direction-only recurrence matrix is generated from data. The re-execution is labelled internal and non-preregistered. |
| 5 Validity | **Pass** | The deviation register (validity-audit) is fully reflected in the manuscript's threats section and deviations table. |
| 6 Literature | **Fail (environmental)** | 57 references are recorded with their verification depth; 41 are search-extract only. |
| 7 Reproducibility | **Pass** | `build_anonymous_artifact.py` is deterministic, and `verify_artifact.py --strict` passes. Tables regenerate byte-identically from the bundle. A behaviour check gives identical test outcomes for the review and faithful editions. Metric extraction needs the full repository history (documented). |
| 8 Anonymity | **Pass with residual risk** | The PDFs are clean. The review edition aliases product and organization names and every resolvable object id. Searchable residuals are documented in `reproducibility-and-anonymization.md`: verbatim code identifiers, one protected schema literal, and a dependency namespace. |
| 9 Review | **Pass (process)** | Three fresh reviews were run (hostile, statistics, final hostile). Every credible issue was fixed or recorded in `adjudication-log.md`. The reviewers' verdict is what sets this classification. |
| 10 Build | **Pass** | Both PDFs build cleanly: full paper within 10 + 2 pages, short paper within 6. The artifact builds and verifies. `praxis validate` reports only two provenance errors inherited from `main` in records this work did not create (EX-ROS-2026-A024, HY-ROS-2026-A030). |

## Is a shorter venue defensible?

Yes, as an early-results or new-ideas paper, once the blockers are closed. Its contribution would be:
- a carefully disclosed case study;
- the measured two-sided trade-off;
- an execution-granularity design that others can reuse.

It is not defensible as a full research paper, a benchmark result or a causal claim.

## A022 and follow-up experiments

- **EX-ROS-2026-A022** (affinity × execution mode) has **not run**. It is blocked at its target-repository gate (EV-ROS-2026-A072). This package does not imply otherwise, and its preregistration was not changed.
- **EX-ROS-2026-A024** was preregistered on `main` on 2026-10-07 and has not run. It tests whether structured handoffs between fresh sessions recover the cohort benefit. It addresses part of the treatment-bundle confound.
- No new experimental campaign was launched, because none is authorized for this work. WI-0078 and WI-0079 record what is needed, and they need the owner's cost authorization.
