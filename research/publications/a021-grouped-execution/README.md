# A021 publication package

## Purpose

Turn the existing A021 evidence into a peer-reviewable empirical software-engineering paper without overstating what the experiment establishes.

Primary candidate venue: **SANER 2027 Agentic AI4SE Track**.
- Mandatory abstract: 2026-10-19 AoE
- Paper: 2026-10-23 AoE
- Limit: 10 pages plus up to 2 pages of references
- Review: double anonymous

Secondary candidate: **MSR 2027 Technical Papers**.
- Abstract: 2026-10-20 AoE
- Paper: 2026-10-23 AoE

The paper should be submitted only if the evidence review below closes the blocking gaps. A deadline is not a reason to weaken the methods or claims.

## Working title

**Does Shared Agent Context Reduce Architectural Drift? A Blinded Replication Study of Grouped and Independent AI Software-Engineering Execution**

Alternate:
**Grouped versus Independent Agent Execution for High-Affinity Software Work: Two Blinded Evaluations and an Execution Replication**

## Central claim

The defensible claim is deliberately narrow:

> For one tightly coupled, high-affinity software-engineering cohort in Praxis, grouped execution used substantially fewer resources and repeatedly produced a more unified cross-item architecture than independent per-item execution. Local acceptance-quality advantages were mixed. A second execution reproduced the qualitative architectural pattern and measured a 58.72% platform-cost reduction and 61.08% elapsed-time reduction for grouped execution. These results do not establish a universal causal advantage for grouping.

Do **not** claim:
- that grouping is always better;
- that this establishes a general law for agentic development;
- that this compares waterfall with agile/iterative development;
- that the replication is an independent-domain replication;
- that defect count improved overall;
- that the experiment establishes statistical significance.

## Evidence already available

Canonical protocol:
- `research/experiments/EX-ROS-2026-A021--grouped-versus-independent-execution.md`

Original results:
- `research/evidence/EV-ROS-2026-A064--grouping-experiment-results.md`

Original independent blind-evaluation method/report:
- `research/evidence/EV-ROS-2026-A065*`
- `research/evidence/EV-ROS-2026-A067*`

R2 blind replication:
- `research/evidence/EV-ROS-2026-A070--a021-r2-blind-evaluation-and-replication.md`
- `research/experiments/EX-ROS-2026-A021-R2-blind/`

R2 evaluator findings:
- `research/experiments/EX-ROS-2026-A021-R2-blind/output/findings.json`

R2 post-unblinding metrics:
- `research/experiments/EX-ROS-2026-A021-R2-blind/output/POST-UNBLINDING-METRICS.txt`

Mechanism hypothesis:
- `research/hypotheses/HY-ROS-2026-A028--grouped-execution-value.md`

The common baseline for A021/R2 is commit:
- `8b4ffa392e93b19bf39f6672a608954c934cb815`

## Publication framing

This is best framed as an **empirical replicated case study** of agent execution strategy, not as a benchmark paper.

The contribution is the combination of:

1. a frozen, pre-specified grouped-vs-independent protocol;
2. isolated implementation arms from a shared baseline;
3. blind architectural/acceptance evaluation;
4. execution-resource measurements;
5. an execution replication with another independent blind evaluation;
6. explicit negative/mixed results rather than a winner-only narrative;
7. reproducibility artifacts anchored to Git commits.

## Research questions

- **RQ1 Resource efficiency:** For this high-affinity cohort, how does grouped execution change cost, elapsed time, token usage, repeated reads, searches, builds, and context acquisition?
- **RQ2 Architectural consistency:** Does grouped execution produce a more coherent cross-item domain and persistence model than independent per-item execution?
- **RQ3 Local correctness:** What tradeoff exists between cross-item coherence and fidelity to individual acceptance criteria, tests, and checkpoint rules?
- **RQ4 Replicability:** Does a second execution from the same baseline reproduce the direction of the architectural and resource findings?
- **RQ5 Boundary of the claim:** Which observed effects can plausibly be attributed to high work affinity, and which remain confounded by orchestration, stale checkouts, failed attempts, or repository-specific structure?

## Hypotheses for the manuscript

These are manuscript-level restatements of the repository hypotheses. They must not be presented as newly preregistered.

- **H1:** Grouped execution will require less repeated context acquisition than independent execution for a high-affinity cohort.
- **H2:** Grouped execution will produce fewer divergent cross-item architectural models.
- **H3:** Grouped execution will not necessarily dominate independent execution on localized acceptance-criterion fidelity or test depth.
- **H4:** A same-domain execution replication will reproduce the direction of H1/H2, while still permitting mixed local-quality results.

## Blocking work before submission

1. Verify every numeric value in the manuscript against a committed artifact.
2. Reconcile any apparent differences between original-run telemetry summaries and later narrative summaries.
3. Produce a machine-readable table containing every metric actually used in the paper.
4. Define the qualitative coding rubric used to classify architectural divergence before writing the Results section.
5. Document evaluator independence and the exact point at which arm mappings were revealed.
6. Separate facts, evaluator judgments, and author interpretations in all result tables.
7. Build an anonymized artifact bundle for double-anonymous review. Do not link the public `kemiller2002/praxis` repository from the anonymous submission.
8. Conduct a real related-work search and verify every citation manually.
9. Run manuscript consistency checks so every claim maps to evidence in `evidence-matrix.md`.
10. Decide whether the current evidence is strong enough for a 10-page full paper. If not, prefer a shorter/early-research track rather than inflating the claim.

## Next empirical study

The highest-value follow-up is not another same-domain A021 rerun. It is the pre-registered affinity falsification:

- high-affinity vs low-affinity work;
- grouped vs independent execution;
- in a non-Praxis repository.

That study is what can distinguish a genuine **cohesion mechanism** from generic session-startup amortization.

See:
- `research/experiments/EX-ROS-2026-A022--affinity-by-execution-mode.md`
- `research/hypotheses/HY-ROS-2026-A029--affinity-moderates-grouping-value.md`
