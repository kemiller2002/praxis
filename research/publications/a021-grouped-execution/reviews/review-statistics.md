# Statistical and methodological review: A021 grouped-execution manuscript

Reviewer role: methodologist (small-n empirical SE, replication, quantitative reporting). I did not take part in writing the paper.
Reviewed: `manuscript/paper.tex`, `manuscript/tables/*.tex`, `manuscript/figures/design-flow.tex`, `data/metrics.json`, `data/metric-conflicts.json`, `data/metrics-sources.md`, `analysis-plan.md`, `validity-audit.md`, `scripts/build_tables.py`, `scripts/build_architecture_table.py`, `scripts/verify_metrics.py`.
Date: 2026-10-07.

## 0. What I ran and recomputed

- `python3 scripts/verify_metrics.py`: `MATCH=58, MISMATCH=3, NOT-COMPARABLE=33, ERROR=0, stale_outputs=0`. The three mismatches are documented conflicts: CF-03 (111 vs 112 min, a rounding/end-time issue in the source record) and CF-02 (an old record said "unavailable" for a cost that exists). None affects the manuscript.
- `python3 scripts/build_tables.py --check`: `tables up to date`.
- I recomputed every ratio I/G and reduction (I-G)/I in `metrics.json` `derived[]` from its `grouped` and `independent` operands. **All 42 match** to the precision stored. I also re-summed the per-session rows:
  - A021 independent cost: 3.707276 + 3.4215014 + 2.8139084 + 0.7946544 + 1.8494294 + 1.9885424 + 6.939496 = 21.514808 USD (matches).
  - A021 independent active time: 845 + 1305 + 893 + 445 + 615 + 769 + 1810 = 6682 s = 111.4 min (matches).
  - S1 (exclude attempts 1 and 2): cost (17.676836 - 9.476795)/17.676836 = 46.4%; time (5298 - 3671)/5298 = 30.7% (both match the macros).
- Every number in the prose and tables (`AcostRatio` 2.27, `AcostReduction` 56.0, `RtwoCostRatio` 2.10, `RtwoCostReduction` 52.3, `AactiveReduction` 45.1, `RtwoActiveReduction` 44.4, `AoutputReduction` 54.1, `AcacheReadReduction` 45.6, `RtwoOutputReduction` 43.8, `RtwoCacheReadReduction` 48.8, `RtwoCostInclOrchReduction` 58.72, `RtwoElapsedReduction` 61.08, the spans 4:52 / 2:13 / 2:24, all lower-bound ratios, and the R2 time-to-first-code 3.65 / 72.6) is arithmetically correct.

**I found no arithmetic errors.** The problems below concern interpretation, presentation and one factual mis-description of the data (item-4 attempt 2) that changes what sensitivity analysis S1 means.

---

## 1. MUST-FIX

### M1. Item-4 "attempt 2" was not a failed attempt; S1 removes real implementation work, and the permission block does not inflate worker-only figures

**Where:** `paper.tex` l.115 ("item 4 failed twice before a third attempt completed it"); l.163 (S1 paragraph); l.205 (Table `tab:deviations`, row A-2..A-4: "second blocked ... and was abandoned"); l.270 ("The failed attempts and the permission block inflate A021's per-item resource figures (S1)"); l.254 (RQ5 "direction ... survives S1").

**Evidence in the data:**
- `metrics.json`, row A021 / control-04 / `time_to_first_code_s`: "Covers attempt 3 only (bookkeeping); **the item-04 implementation happened in attempt 2**, which has no transcript metrics."
- `metrics-sources.md` l.224: attempt 3 is a "bookkeeping-only session; no production-code change".
- `validity-audit.md` A3: "Control-04 attempt 2 pushed `881a34d`, then blocked on a permission prompt and was abandoned".

So the per-item arm has one truly wasted session (attempt 1, $1.85, nothing pushed), one session that implemented item 4 and then blocked during bookkeeping (attempt 2, $1.99 at the block), and one bookkeeping session (attempt 3, $0.79). Consequences:

1. The paper's description "failed twice before a third attempt completed it" is factually wrong for the code.
2. S1 as computed (exclude attempts 1 *and* 2) deletes the session that wrote item 4 and keeps a $0.79 bookkeeping session in its place. That is not "per-item execution without failure overhead"; it is per-item execution with one item's implementation removed. The 46.4% / 30.7% figures therefore *understate* the per-item cost of doing five items and should not be presented as the conservative bound.
3. The permission block is not in the worker-only figures: active time ends attempt 2 at `blockedAt` (769 s) and its cost is the usage at the block. The block inflates only the wall-clock span. l.270 is therefore wrong as worded.

**Recompute for a defensible S1** (exclude only attempt 1, which produced nothing):
- cost: I = 19.665 USD, ratio 2.08, reduction 51.8%;
- active time: I = 6067 s = 101.1 min, ratio 1.65, reduction 39.5%;
- output tokens: reduction 48.9%.

**Suggested replacement wording**
- l.115: "The per-item arm needed seven sessions for five items: the first item-4 attempt stalled without pushing anything; the second implemented item 4 and pushed it, then blocked on a permission prompt before its bookkeeping and was abandoned; a third session completed the bookkeeping."
- l.148: "*S1*, excluding the A021 item-4 attempt that produced no commits (and, as a lower extreme, also the attempt that implemented item 4 but did not finish its bookkeeping)."
- l.163: "*S1:* excluding the item-4 attempt that produced nothing, the reductions are about 52% for cost and 40% for active time (ratios about 2.1x and 1.7x). Excluding also the attempt that implemented item 4 but blocked before its bookkeeping, they are about 46% and 31%; this second figure removes real implementation work and is a lower extreme, not a corrected estimate. The direction is unchanged; the size depends on the inclusion rule."
- l.270: "The stalled item-4 attempt inflates A021's per-item worker-only figures (*S1*); the permission block inflates only the wall-clock span."
- Table `tab:deviations` A-2..A-4, left cell: "Per-item item 4: first attempt stalled with nothing pushed; second implemented the item, then blocked on a permission prompt (127.1 min) and was abandoned; third did bookkeeping only; owner approval carried into later sessions".

(Requires a new macro pair from `build_tables.py` for "exclude attempt 1 only"; `FAILED_A021` currently holds both.)

### M2. The run-to-run variance caution is buried; the headline ratios are presented as if they were effect estimates

**Where:** abstract l.31; RQ1 l.160-162 and l.228-229; Discussion l.252; Conclusion l.291. The caution exists only in Related Work l.70 and Conclusion validity l.277.

A single pair of runs per study, with agentic token use known to vary several-fold between identical runs (the paper's own citations, `bai2026tokens`, `salim2026tokenomics`), cannot distinguish a 2x contrast from run-to-run noise. The paper's own data illustrate the scale of heterogeneity: per-session cost inside the A021 per-item arm ranges from $0.79 to $6.94 (about 9x), and in R2 from $3.19 to $5.63. The two studies agreeing (2.27x and 2.10x) is weak protection: they share baseline, cohort, model and organisation, R2 was designed with knowledge of A021, and two draws do not estimate a variance. The fact that the per-item arm has more sessions (more opportunities for a stall, a stale start or a permission block) is part of the treatment, but it also means the per-item total is more exposed to single chance events, as A021 item 4 shows.

**Suggested wording**
- Abstract, after l.31, add: "These are single runs; agent token use can vary several-fold between identical runs, so a difference of about two-fold from one pair of executions may lie within run-to-run variation, and the agreement of two correlated executions does not rule this out."
- RQ1 (l.160), add after the first sentence: "With one execution per arm, this ratio is an observation, not an estimate of the effect of execution mode; we have no estimate of run-to-run variation in this setup."
- Conclusion validity (l.277), replace "so the size of the resource difference in one execution is not an effect-size estimate" with "so a difference of about two-fold in one execution, or in two correlated executions, may lie within run-to-run variation; neither its size nor, strictly, its sign is established. Within our own per-item arms, single-session cost varied up to nine-fold across items."
- Discussion l.252 and Conclusion l.291: insert "in both single executions" / "in each of two single executions" where resources are stated.

### M3. Two-decimal percentages and four-decimal ratios in prose and macros are fake precision

**Where:** l.230 prints `\RtwoCostInclOrchReduction` = **58.72** and `\RtwoElapsedReduction` = **61.08**. `macros.tex` also defines ratio4/pct2 macros (`2.4224`, `1.9781`, `2.2557`, `2.5693`, `49.45`, `55.67`) that are currently unused in the prose but invite reuse. Every primary contrast in the abstract and results is given as a two-decimal ratio *and* a one-decimal percentage (e.g. "2.27x ... 56.0%"; "2.10x ... 52.3%"; "45.1% and 44.4%").

These are derived from a single noisy run. The third significant figure carries no information, and printing 45.1% next to 44.4% suggests a precision of agreement that two runs cannot show. Ratios and reductions are also two encodings of the same number; giving both doubles the figures without adding information, and "56% reduction" vs "2.27x" is a known source of reader confusion.

**Recommendation**
- Keep raw measured values at recorded precision in the tables (USD to cents, tokens in k/M, minutes to 0.1 are fine: they are exact records).
- For derived contrasts, use **one** encoding in prose: ratios to one decimal ("about 2.3x and 2.1x") or reductions to whole percent ("about 56% and 52%"; or "roughly half"). I recommend ratios in prose (they compose with the replication matrix) and reductions only in the tables, rounded to whole percent.
- Change `FMT["ratio"]` to one decimal and `FMT["pct1"]` to integer for derived cells; delete `ratio4`/`pct2` macros or stop emitting them.
- l.230: "Including the per-item arm's own orchestrator ... the cost reduction is about 59%. The wall-clock span ... gives about 61%."
- Abstract l.31: "per-item execution used about 2.3x the platform cost of cohort execution in the original study and about 2.1x in the replication, and about 1.8x the summed active session time in each."

### M4. The analysis plan and rubric are post hoc, but the paper reads as if they were prespecified

**Where:** l.144 "Following the analysis plan, a dimension is divergent only when..."; l.147 says only the *sensitivity analyses* were defined after the executions.

`analysis-plan.md` was first committed on 2026-10-07 (`cc7f32ea`), after both studies ran (2026-09-30/10-01) and after both unblindings; the eight-dimension audit was committed the same day (`8a2da683`). The plan lists six dimensions; D7 (reuse of baseline rules) and D8 (incompatible duplicates) were added later. The auditor knew the arm identities and the earlier evaluators' conclusions. R2's metrics were also defined after unblinding (deviation R-14). Only A021's protocol and planner predictions were frozen. The paper does not report the A021 preregistered hypothesis (HY-A028) or its falsification criterion ("weakened if ... loses quality"), which the validity audit (T12) says is arguably met.

**Suggested wording**
- l.144: "The analysis plan, including this rubric, was written after both studies had run and been unblinded; the rubric's eight dimensions (six in the first draft of the plan, plus D7 and D8) were therefore chosen with knowledge of the arms and of the earlier evaluations. All architecture results are exploratory."
- l.147: "(defined, with the rubric, after both studies were unblinded)".
- Section 6.2 (A021) or Results: add one sentence reporting the frozen A021 prediction(s) and whether the observed per-criterion quality result meets the preregistered falsification condition. If the frozen protocol contains no prespecified primary outcome measure, say so.

### M5. "Replication", "reproduced", and "close (operational) replication" overstate R2

**Where:** title l.21; abstract l.30; l.51, l.57, l.117-118; Table `tab:deviations` R-11/R-14; replication matrix status column and caption (`replication-matrix.tex`); RQ4 l.246-247; Conclusion l.291; figure `design-flow.tex` ("R2: close replication").

R2 changed the treatment operationalisation (fuller, unarchived, owner-written prompts; the grouped analysis gained headings matching the later-scored dimensions), changed the evaluator, was not preregistered, defined its metrics after unblinding, and was designed by someone who knew interim A021 results. In the Gomez et al. vocabulary this is an *internal operational* replication with changed operationalisations of both the treatment and the measurement; "close" (which in common usage means the protocol and operationalisations are kept as similar as possible) is hard to defend when the treatment prompts changed and cannot be inspected. The paper discloses all of this, which is good, but the labels still carry more weight than the design supports. The term "reproduced" in the matrix, applied by a direction-only rule, sounds like a statistical replication verdict.

**Suggested wording**
- Title: "...: A Case Study with an Internal Re-execution" (or "with a Second, Non-Preregistered Execution").
- l.30 / l.51 / l.118 / Fig. 1: "an internal, non-preregistered re-execution (an operational replication in the sense of Gomez et al.: same baseline, cohort and model; changed, unarchived prompts and a different evaluator)".
- Replication matrix: rename statuses to "direction recurred / mixed / direction not recurred / not tested"; caption: "Was the A021 direction observed again in R2?" and add: "A recurred direction in two correlated single executions is not a replication of an effect size."
- l.246: "Of its rows, 17 showed the same direction in R2..." and l.247: "A recurred direction is consistent with stability under re-execution of this one setup; with one re-execution it does not estimate run-to-run variation, and it is not evidence of generality."
- For the architecture rows, add a sentence that the designer-knowledge threat (R2 analysis headings matched the scored dimensions) bears much more on architecture recurrence than on cost recurrence.

### M6. Counting "6 of 8 dimensions" is a score across non-commensurable dimensions

**Where:** abstract l.32; l.172; l.233; `architecture-matrix.tex` tally lines; `architecture-macros.tex` (`ArchAGroupedMore` etc.); RQ4 l.246 ("17 reproduced").

The paper rightly refuses a composite quality score (l.87), but "more unified on six of eight dimensions (four behavioral)" is a composite score in all but name: it treats an organisational store-layout difference (D1), a duplicated-but-compatible ID grammar (D8: the audit itself found "no incompatibility is shown"), and a lost-update defect (D6) as equal units, and it sets them against D4 as 6:1. The count also depends on how the post hoc rubric (M4) was cut into dimensions. In A021 two of the six (D3, D8) and the whole per-item advantage (D4) come from item 5, which started from the baseline (S3), yet the abstract reports the unadjusted count. Similarly, "17 rows reproduced" sums eight resource and repeated-context rows that are near-deterministic consequences of one contrast (cost, output, cache-read and requests are strongly correlated; AGENTS.md and governance reads are design-entailed, l.166), so the count is inflated by correlated and design-entailed outcomes.

**Suggested replacement for abstract l.32:**
"On an eight-dimension architecture rubric written and applied after unblinding by one auditor, the cohort arm, in both studies, applied one rule where the per-item arm showed divergent behavior for member admission (create vs. add), the error and exit-code contract, and write locking (concurrent creates lost updates); it also had one store where the per-item arm had two (organization only). The per-item arm had stronger checkpoint ownership validation in both studies (in the original study only in the item that started from a stale checkout)."

- Drop the tally lines from `architecture-matrix.tex` (or replace with a list of dimension names per direction, split by behavioral/organizational).
- RQ4 l.246: replace the count with the named list, and mark design-entailed rows (AGENTS.md / governance reads) as such or remove them from the matrix.
- D8: given the audit found the per-item duplicate grammar compatible, coding "G more unified" on a dimension named *incompatible duplicates* is inconsistent; recode as "equivalent (no incompatibility shown)" or rename the dimension to "duplicate definitions inside the feature".

---

## 2. SHOULD-FIX

### S1. "Trade-off" framing contradicts "local correctness was mixed"
**Where:** abstract l.35; RQ3 wording l.83; Discussion l.252; Conclusion l.292.
A trade-off implies the per-item arm was better on local correctness; the paper's own finding is that neither arm was better. What is supported is: cohort lower on resources and more unified on three behavioral invariants; per-item better on one checkpoint dimension and on baseline-rule reuse; local correctness not rankable.
- l.35: "We observed lower resource use and more unified cross-item invariants for the cohort arm, stronger checkpoint validation and more baseline-rule reuse for the per-item arm, and confirmed defects of different kinds in both; we do not claim a general advantage for either mode."
- l.252: "The study shows differences in both directions, not a winner."
- l.83: "What differences appear in per-item acceptance fidelity, tests and defects, alongside the consistency differences?"

### S2. Test counts are listed as an advantage
**Where:** abstract l.33 ("Per-item execution was better on ..., reused more ..., and added more tests"); l.252 ("...and wrote more tests"); l.291 ("...and wrote more tests").
Placing "added more tests" in a list headed "was better on" conflates test count with quality, contradicting l.137 and l.267.
- l.33: "Per-item execution had stronger checkpoint ownership validation in both studies and reused more of the existing baseline rules; it also added more tests (37 vs. 19 and 47 vs. 27), which we report as a count, not as evidence of better testing."
- l.252 / l.291: move "wrote more tests" to a separate clause: "...; it also wrote more tests, a count we do not interpret as quality."

### S3. Architectural unification is not correctness; make this explicit where D2 is reported
**Where:** l.173, l.233, abstract l.32.
The cohort arm's admission rule is "unified" across create and add, but it re-derived the location rule and admits an external item (probe P2) in both studies: it is consistently wrong for that input. The paper notes P2 under D7, but a reader of D2 alone infers correctness.
- Add after l.173: "Unified here means the verbs agree with each other, not that they are correct: the shared \G{} admission rule itself accepts an item the planner treats as external (D7, probe P2)."
- Methods l.135: "The rubric measures internal consistency across verbs; it does not measure correctness against the acceptance criteria, which Section RQ3 reports separately."

### S4. Ratios of lower bounds, and direction claims from lower bounds
**Where:** `resources-a021.tex`, `resources-r2.tex` (script rows with 2-decimal ratios and 1-decimal reductions, asterisked); `replication-matrix.tex` repeated-context rows (2.08*, 2.58*, 3.00*, 2.50*, 2.72*, ...), all marked "reproduced".
The footnote says such ratios are "not a bound on the true value", which is correct, but printing 2.58 or 4.25 still invites reading them as estimates. Both arms' counts are lower bounds (the grouped session also ran its script before its final steps), so even the *direction* rests on an assumption that the grouped arm's uncounted final steps were smaller than the gap.
- Replace ratio/reduction cells for script rows with "--" and keep only the "≥" raw counts; in the replication matrix give "G fewer (≥114 vs ≥237)".
- Add to l.132: "Because both arms' counts are truncated, the direction is inferred under the assumption that the uncounted final steps were smaller than the observed gap; in every row the gap is large relative to the counted totals."

### S5. Time to first code: summed onsets are not comparable across arms
**Where:** `resources-a021.tex` and `resources-r2.tex` row "Time to first code change, summed (min)" (A021 2.24*/55.3*; R2 3.65/72.6, no asterisk); macros `AfirstCodeMin*`, `RtwoFirstCodeMin*`.
The grouped value is one onset (and comes *after* a mandated analysis); the per-item value is a sum of five onsets. The ratio is largely a count of session starts, i.e. design-entailed, like the AGENTS.md reads. In A021 the per-item sum also omits item 4's real implementation session (attempt 2, see M1), so it is a lower bound of a quantity that does not include all items.
- Remove the ratio and reduction cells for this row, or relabel: "Orientation time before first code change, summed over sessions (one onset in G, five in I; design-entailed)".

### S6. Wall-clock span ratios should not be printed
**Where:** `resources-a021.tex` "Wall-clock span ... 4.77 / 79.1"; `resources-r2.tex` spans "2.38 / 58.1" and "2.57 / 61.1"; l.230.
The text (l.130, l.164) correctly says spans are secondary and describe run conditions, but the tables still compute contrasts. The grouped span equals its single session span and cannot contain gaps, whereas the per-item span includes orchestration cadence (A-7) and, in A021, the 127-minute permission block. The comparison is structurally asymmetric.
- Replace ratio/reduction cells for all span rows with "--"; keep the raw h:mm. In l.230 keep the 61% figure only as a quoted record value: "The R2 record's 'elapsed' reduction (about 61%) is a wall-clock span that includes orchestration gaps; we do not use it."

### S7. Per-session fixed costs are part of the ratio and should be named in the interpretation
**Where:** l.160, l.228, Discussion l.252; currently only in `tab:deviations` A-1/R-1/R-2.
Every session had to install the toolchain (R2's harness note was weaker, so more so in R2), and every fresh session reads AGENTS.md by instruction. These per-session fixed costs scale with session count and depend on this platform, not on execution mode as such. Readers should know part of the ~2x is such overhead.
- Add to RQ1: "Part of the difference is per-session overhead of this environment (toolchain setup and mandated instruction reads repeated in every fresh session); we could not separate it from the rest."

### S8. "Per-item execution was better on checkpoint ownership validation in both studies"
**Where:** abstract l.33; l.252; l.291.
In A021 the whole D4 advantage comes from item 5, which started from the baseline (S3, l.179). The paper treats the stale-start dependence of D3/D8 carefully but does not qualify D4 in the abstract or conclusion. Symmetry requires the same caveat.
- "...in both studies (in A021 only in an item that started from a stale checkout)".

### S9. Language implying causal effects or universality
- l.50 "every effect we report is an effect of the bundle" -> "every difference we report is a difference between the bundles".
- l.29 "do not attribute effects to shared context alone" -> "do not attribute differences to shared context alone".
- l.252 "used fewer resources in both executions under every like-for-like definition" -> "used fewer resources on every worker-only measure we report, in each of the two executions".
- l.187 / l.252 "Neither arm was better on local correctness overall" -> "We cannot rank the arms on local correctness: each had confirmed defects of different kinds, and we have no agreed weighting."
- l.238 is fine as written ("not a substantially more unified architecture overall"); "substantially" is used there only to deny a claim.

### S10. Inclusion rule for the primary resource analysis: defensible, but S1 deserves equal prominence
Including all worker sessions (and excluding orchestrator and evaluator, which have no counterpart or are shared) is a defensible primary rule: a practitioner pays for stalled sessions, and more sessions mean more opportunities for such events. Excluding the R2 per-item orchestrator is conservative for the cohort advantage. But with n=1, the stall is one chance event, and a single cohort session could equally have stalled. I recommend reporting the primary figure and the corrected S1 (M1) together wherever the A021 figure appears (abstract, RQ1, Discussion), as a range: "about 2.1-2.3x (cost) and 1.7-1.8x (active time), depending on whether the stalled attempt is counted."

### S11. Replication-matrix labels and status rules
- "Confirmed acceptance defects: not tested" should be "not comparable (no A021 tally)". It was measured; it cannot be compared.
- D7 is "mixed" in both studies, so by the paper's own logic it *recurred*; labelling it "mixed" in the status column (rule in `build_architecture_table.py` `status_from`) obscures that. Use "mixed in both (recurred)".
- D1 and the four resource rows are "reproduced" regardless of magnitude; add "(direction only)" to the caption.
- "Tests added" rows use different labels in A021 ("work-group tests added (evaluation 1)") and R2 ("net F# tests added vs. baseline"). The numbers are both final-minus-baseline (811-792=19, 829-792=37, 819-792=27, 839-792=47), so harmonise the label to "net tests added vs. baseline" in `quality-counts.tex` and the macros.

### S12. "Uncached input tokens" ratio
`resources-a021.tex` "Uncached input tokens 234 vs 658, 2.81, 64.4". These counts are tiny and economically irrelevant next to 24-45 M cache-read tokens; the ratio adds noise to a table of ratios. Drop the ratio/reduction cells or the row.

---

## 3. FINE (checked, no change needed)

- **No p-values, tests, confidence intervals, or "significant" language.** None found (grep for significan*, p-value, CI, test statistic). l.142 states that no inferential statistics are computed. Good.
- **No implied distributions.** The only figure is a TikZ design flow with no data. No bar charts, error bars, means, "on average", or cross-study averaging. l.142 explicitly says studies are not averaged. Good.
- **n = 1 per cell is stated correctly** (l.30, l.59, l.142, l.277, replication caption "One execution per arm per study"). Items within an execution are explicitly declared dependent (l.142, l.277). No per-item or per-session statistics are computed. No pseudo-replication in the analysis itself (the counting issue in M6 is a presentation problem, not a computed test).
- **Timing definitions** are defined (l.129) and the primary measure (summed active session time) is used consistently in the abstract and results; summed time is never compared to a span in the prose. Blocked time is excluded from active time by definition (`session_duration_s` for attempt 2 ends at `blockedAt`). The R2 orchestrator-inclusive figures are labelled secondary.
- **Aggregation code** (`build_tables.py` `excluded_reduction`, `metrics.json` `agg:workers` rule) does what the text says: workers = implementation + failed attempts, excluding orchestrator and evaluator. Per-session sums reproduce the aggregates exactly.
- **Treatment-bundle confound** is stated prominently (abstract l.29, l.50, l.111, l.265). Good.
- **Test count vs quality** is explicitly separated in Measures (l.137) and Threats (l.267); only the abstract/conclusion list wording (S2) slips.
- **Generality** is disclaimed (l.59, l.247, l.254, l.275, l.292).
- **Effect-size adjectives**: no "markedly", "substantially" (except as a denial at l.238), "consistently", "strongly", "dramatically" or "clearly" used for results. The paper's tone is restrained; the problems are in labels and precision, not adjectives.
- **Lower-bound marking** (≥ and asterisk) is applied systematically in tables and macros.

---

## 4. Overall judgment

The arithmetic is correct and fully reproducible, and the paper is unusually candid: it computes no inferential statistics, states n = 1 per cell, declares items dependent, separates time definitions, and discloses the treatment bundle, the evaluation leaks and the R2 independence threats.

The **effect-size language is not yet scientifically defensible as presented**, mainly for four reasons:

1. Precision and form: two-decimal ratios, one- and two-decimal percentages, and paired ratio-plus-reduction figures present single noisy runs as measured effects (M3). Rounded ratios ("about 2x") are what one pair of runs supports.
2. The run-to-run variance caveat, which is the main threat to the resource claim, sits in Related Work and Conclusion validity rather than beside the numbers (M2).
3. The S1 sensitivity analysis rests on a mis-description of the item-4 sessions (M1). The corrected range (about 2.1-2.3x cost, 1.7-1.8x active time in A021) still shows the same direction, so the conclusion survives, but the text and Table 6 must be corrected.
4. The architecture result is reported as a tally (6/8, 4/8) over a post hoc, non-commensurable rubric (M4, M6). Named dimensions are what the evidence supports: in both executions, the cohort arm had one rule where the per-item arm diverged behaviorally on admission, error/exit contract and write locking, and the per-item arm was stronger on checkpoint validation.

The **replication language is also overstated**. "Close replication" and "reproduced" should give way to "internal, non-preregistered re-execution (operational replication)" and "direction recurred" (M5). Given changed and unarchived prompts, post-unblinding metric definitions, and a designer who knew interim results, R2 supports this statement: "the direction of the resource difference, and three behavioral consistency differences, recurred in a second execution of the same setup." It does not establish that the effect replicates, its size, or that two executions rule out run-to-run variation.

With M1-M6 addressed, the paper would make a defensible descriptive claim of the right strength for its design.
