# A021 publication package

Turns the A021 grouped-versus-independent execution study and its R2
re-execution into a peer-reviewable empirical software-engineering paper,
without overstating what two executions establish.

**Readiness: state C. The publication package is ready, but more empirical
work is required before submission.** See [`READINESS.md`](READINESS.md).

## Paper

- Title: *Cohort versus Per-Item Coding-Agent Execution of Coupled Work Items:
  Cost and Cross-Item Consistency in a Case Study and an Internal
  Re-Execution* (read from `manuscript/paper.tex`).
- Full paper: `manuscript/paper.tex`, written for the SANER 2027 Agentic
  AI4SE track (IEEEtran, 10 pages + 2 pages of references, double-anonymous).
- Early-results variant: `manuscript/paper-short.tex`, written for SANER
  Short Papers (6 pages in total).
- Build both with `scripts/build_paper.sh`, or `scripts/build_paper.sh --check`
  to build and also run the manuscript checker. Output goes to
  `build/paper/`, which is git-ignored.

## Central claim (final wording)

The claim covers one five-item high-affinity cohort in one repository, with
one execution per arm in each of two studies. The treatment compared is
cohort execution (one session plus a mandated up-front cross-item analysis)
against per-item execution (fresh serial sessions).

- **Resources.** Cohort execution used less platform cost and less summed
  session time in both executions (worker-only figures). The per-item arm also
  wrote more code and tests. Run-to-run variance is not bounded.
- **Consistency.** In both executions the cohort arm had one member-admission
  rule, one error/exit contract and locked writes, where the per-item arm
  diverged.
- **Local correctness and reuse.** The per-item arm was better on checkpoint
  ownership validation and on reuse of existing baseline rules.
- **Overall.** Local correctness was mixed, and no winner is claimed.
- **Re-execution.** R2 is an internal, non-preregistered operational
  replication. It is not independent, and its domain is not independent of
  A021's.

The original A021 evaluations were not effectively blind; the reasons are in
`adjudication-log.md` AD-02. The 58.72% cost and 61.08% elapsed figures
recorded in EV-ROS-2026-A070 include a control-only orchestrator session and a
wall-clock span. They are reported only as secondary, defined figures; see
`research/evidence/EV-ROS-2026-A074--a021-r2-evidence-errata.md`.

## Contents

| Path | What it is |
| --- | --- |
| `READINESS.md` | Readiness decision, quality gates and blockers |
| `adjudication-log.md` | Disagreements between audits and records, resolved against primary evidence |
| `validity-audit.md`, `data/threats.json` | Hostile protocol and validity audit with the deviations register |
| `architecture-findings.md`, `data/architecture-findings.json`, `data/probes/` | Symmetric eight-dimension architecture audit: code citations, runtime probes, falsification attempts |
| `data/metrics.{json,csv}`, `data/metric-conflicts.json`, `data/metrics-sources.md` (internal) | Metrics extracted from pinned commits, with provenance and conflicts |
| `data/evidence-index.json` (internal) | Every manuscript claim mapped to its sources, classification and confidence |
| `related-work.md`, `references.bib`, `data/references-verification.json` | Literature, novelty assessment and the verification depth of each reference |
| `submission-plan.md`, `data/venues.json` | Venue requirements (search-extract verified) |
| `reviews/` | Hostile review, statistics review and final hostile review |
| `manuscript/` | Paper sources, figures and generated tables |
| `scripts/` | Extraction, verification, table, manuscript-check, probe and artifact scripts (Python stdlib) |
| `artifact/` | Bundle README template, manifests and checksums for the review and faithful editions |
| `internal/` | Private provenance map, denylist and behaviour verification (never bundled) |
| `analysis-plan.md`, `manuscript-outline.md`, `evidence-matrix.md`, `reproducibility-and-anonymization.md` | Planning and method documents |

`analysis-plan.md` was written after both unblindings, and its six-dimension
rubric was later extended to eight dimensions. The manuscript reports both
facts.

## Verification commands

Run from the repository root, with `P=research/publications/a021-grouped-execution`:

```
python3 $P/scripts/verify_metrics.py
python3 $P/scripts/build_tables.py --check
python3 $P/scripts/build_architecture_table.py --check
python3 $P/scripts/check_architecture_findings.py
bash    $P/scripts/build_paper.sh --check
python3 $P/scripts/build_anonymous_artifact.py
python3 $P/scripts/verify_artifact.py --strict
```

## Follow-up work items

- WI-0075: human inter-rater coding of the rubric.
- WI-0076: reference verification.
- WI-0077: venue confirmation.
- WI-0078: multi-arm granularity experiment, complementing EX-ROS-2026-A024.
- WI-0079: unblock A022.

EX-ROS-2026-A022 has not run; it is blocked at its target-repository gate
(EV-ROS-2026-A072).
