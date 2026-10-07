# Manuscript outline (matches `manuscript/paper.tex`)

Built with `scripts/build_paper.sh` (output `build/paper/paper.pdf`, git-ignored).
Checked with `scripts/check_manuscript.py`. Claims are indexed in
`data/evidence-index.json` and marked `% CLAIM: Cxx` in the source.

## Title

Full paper (`manuscript/paper.tex`, 10 + 2): **Cohort versus Per-Item Coding-Agent
Execution of Coupled Work Items: Cost and Cross-Item Consistency in a Case Study
and an Internal Re-Execution**

Short paper (`manuscript/paper-short.tex`, SANER SP&P, 6 pages including
references): **... Early Results from a Case Study and an Internal Re-Execution**.
Same macros, same evidence index (subset of claims), same checker (which also
enforces both page limits on the built PDFs).

Revised after `reviews/review-hostile-1.md` and `reviews/review-statistics.md`:
"close replication" became "internal, non-preregistered re-execution"; tallies
of dimensions and of "reproduced" rows were replaced by named dimensions and a
direction-only recurrence matrix; ratios are one decimal and reductions whole
percent; contamination (C52), specification-literal D2 (C53), the cohort arm's
lock-contention failures (C54), the preregistered elements (C55, C56), role
contamination (C57), the post hoc rubric (C58), treatment-outcome overlap (C59),
per-session overhead (C60) and run-to-run variance (C61) were added; S1 now
excludes only the stalled item-4 attempt.

The earlier working title ("Does Shared Agent Context Reduce Architectural
Drift? A Blinded Replication Study ...") is retired: the treatment is a bundle
(AD-01, T01) and the A021 evaluations were not blind (AD-02, T02).

## Format

SANER 2027 Agentic AI4SE track: `\documentclass[10pt,conference]{IEEEtran}`,
double-anonymous, 10 pages + up to 2 pages of references. Data Availability
after the Conclusion; anonymized "Acknowledgment of AI-Generated Content"
before the references.

## Structure

| § | Section | Content | Main claims |
| --- | --- | --- | --- |
| — | Abstract | treatment bundle; worker-only resource figures; architecture tallies; independent advantages; mixed local correctness; blinding status | C01, C02, C06-C08, C11-C14, C21-C24, C30, C34 |
| I | Introduction | one-item-per-session practice; narrowed novelty (AD-06); treatment definition and confound; contributions | C01, C02, C04, C08 |
| II | Background and related work | task units; partitioning (CooperBench, MSEval, CAID); grouping in one context; cost variance; architecture and method; novelty statement | C45 |
| III | Research questions | RQ1 resources, RQ2 architectural consistency, RQ3 local correctness, RQ4 replication, RQ5 boundary; no composite score | — |
| IV | Subject system and cohort | anonymized description; five verbs; planner affinity/conflict/serial; cohort authored by the designer lineage | C05, C41 |
| V | Experimental design | Fig. 1 (`figures/design-flow.tex`); treatments; A021 freeze; R2 as internal close replication; evaluation and unblinding | C01-C04, C06-C10, C37-C40 |
| VI | Measures | worker-only definition; four time definitions; lower-bound transcript counts; rubric dimensions; local correctness | C11-C13, C16, C18 |
| VII | Analysis method | descriptive comparison; rubric categories and coding rule; S1-S5 | C29 |
| VIII | Results: A021 | Table I (resources), Table II (architecture), Table III (quality counts); S1, S2, S3, S4, S5 | C11, C12, C15-C21, C23-C26, C28, C30, C32-C34 |
| IX | Re-execution: R2 | Table IV (resources), Table V (deviations, table*), Table VI (replication matrix) | C13-C16, C18, C20, C22-C25, C27, C30, C31, C33-C35 |
| X | Discussion | meaning; RQ5 boundary; mechanism candidates (inferred); practical implications | C02, C17, C26, C27, C34, C44, C46, C47 |
| XI | Threats to validity | construct, internal, external, conclusion; deviations table | C02, C06, C07, C09, C17, C36-C38, C41-C43, C48 |
| XII | Reproducibility | generated numbers; corrections; artifact contents | C49-C51 |
| XIII | Conclusion | narrow trade-off statement | C11, C13, C21-C23, C34 |
| — | Data Availability; AI disclosure | anonymous artifact (link withheld); AI drafting disclosed without model id | — |

## Tables and figure (all generated except the deviations table and Fig. 1)

| Item | File | Generator |
| --- | --- | --- |
| Table I A021 resources | `manuscript/tables/resources-a021.tex` | `scripts/build_tables.py` |
| Table II architecture matrix | `manuscript/tables/architecture-matrix.tex` | `scripts/build_architecture_table.py` |
| Table III test/defect counts | `manuscript/tables/quality-counts.tex` | `scripts/build_tables.py` |
| Table IV R2 resources | `manuscript/tables/resources-r2.tex` | `scripts/build_tables.py` |
| Table V deviations and threats | inline in `paper.tex` (no result numbers; macro only) | hand-written from `validity-audit.md` register |
| Table VI replication matrix | `manuscript/tables/replication-matrix.tex` | `scripts/build_architecture_table.py` |
| Fig. 1 design flow | `manuscript/figures/design-flow.tex` | TikZ, no data |
| Prose numbers | `manuscript/tables/macros.tex`, `architecture-macros.tex` | both generators |

## Binding rules applied (from `adjudication-log.md`)

- Treatment named "cohort execution with a mandated up-front cross-item
  analysis" vs "per-item execution"; confound stated in abstract, §I, §V-A, §XI.
- A021 evaluations "not effectively blind"; R2 blind by self-report only; no
  "blinded" in the title.
- R2 = internal close (operational) replication; A021 and R2 in separate
  sections/columns.
- Primary resource figures worker-only; 58.72% / 61.08% only as defined
  secondary figures; spans never compared with summed time.
- Architecture: 6/8 (4 behavioral) and 4/8 (3 behavioral); D4 and D7 given
  equal prominence; stale-start sensitivity; rubric post-unblinding, one AI
  auditor, no inter-rater reliability.
- No p-values, intervals, cross-study means or composite score; no
  "beneficial" classification.
