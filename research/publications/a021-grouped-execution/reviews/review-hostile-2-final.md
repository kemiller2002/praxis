# Review (hostile, final round): "Cohort versus Per-Item Coding-Agent Execution of Coupled Work Items"

Venues: full paper, SANER 2027 Agentic AI4SE track (IEEEtran, 10 + 2 pages, double-anonymous); short paper, SANER Short Papers (6 pages including references).
Reviewer role: hostile program-committee member. I did not write the paper and had not seen earlier versions.

What I read and ran:

- I built both PDFs with `scripts/build_paper.sh` (full: 9 pages, references from page 8; short: 4 pages, references from page 3) and read them with `pdftotext`. `check_manuscript.py` reports 0 problems.
- I extracted `build/anonymous-artifact.tar` into a scratch directory and ran `sha256sum -c` and `verify_artifact.py --bundle` (10/10 checks pass). I ran `build_tables.py` and `build_architecture_table.py` from the bundle; all 8 generated `.tex` files are byte-identical to `manuscript/tables/`. I applied the arm-x, arm-y and arm-N patches to `baseline/source/` and checked citations by hand.
- I cross-checked against `data/metrics.json` (per-session rows and `derived`), `data/architecture-findings.json`, `architecture-findings.md`, `validity-audit.md`, `data/threats.json`, `data/evidence-index.json`, `data/references-verification.json`, `related-work.md`, `adjudication-log.md`, both earlier reviews and `EV-ROS-2026-A074`. I also read the artifact's R2 `findings.json` and the baseline `requirements/PLANNING-WORK-GROUPS.md`.

---

## Scores

| | Score | Confidence |
|---|---|---|
| **Full paper** (10 + 2) | **Reject** | 4 / 5 |
| **Short paper** (early results) | **Weak reject** (borderline; weak accept is reachable after required changes 1–6) | 4 / 5 |

**State judgment: C.** The package is ready, but more empirical work is required before submission. Section 9 gives the reasons.

---

## 1. Summary

The paper executes one five-item cohort (`work group create/show/add/remove/checkpoint` for an F#/.NET CLI built by the authors' organization) in two ways:

- **G:** one session that first commits a mandated cross-item analysis;
- **I:** five fresh, serial, non-communicating sessions on one branch.

This was done twice: A021, with a frozen protocol, and R2, an internal re-execution that was not preregistered. Each study has one execution per arm.

**Results.**

- **Resources.** On worker-only figures, I used about 2.3× and 2.1× G's platform cost, and 1.8× its summed session time.
- **Consistency.** On a post hoc 8-dimension rubric applied by one AI auditor after unblinding, G used one rule where I diverged on create/add admission (D2), the error and exit-code contract (D5) and write locking (D6), in both studies.
- **Counter-findings.** I was stronger on checkpoint ownership validation (D4). Reuse (D7) was "mixed". Both arms had defects.
- **Framing.** The paper calls itself hypothesis-generating. It discloses an unusually long list of confounds: treatment bundle, workspace contamination with the hypothesis, ineffective A021 blinding, stale checkouts, a post hoc rubric and role overlap.

**Strengths.**

- The disclosure is close to exemplary.
- The numbers are generated and arithmetically correct.
- Tables regenerate byte-identically from the review bundle.
- Code citations hold up when the patches are applied.
- The paper has visibly absorbed two prior internal reviews.

**Weaknesses.** None of the strengths repairs the evidential base:

- n = 1 per arm, twice, in correlated executions;
- a treatment that instructs the outcome it is scored on;
- every session could read the hypothesis;
- the consistency construct was defined, applied and adjudicated by AI agents of the same product after unblinding.

The final round of fixes also introduced two new factual errors in the headline claims (D1 and D2 in Section 5).

---

## 2. Strongest reasons to reject (ranked)

### R1. The empirical base cannot support a full paper, and the cheapest remedy was not taken

- **Design.** There are two cells per study and one execution per cell. The five items within an execution are dependent. R2 resamples the same baseline, cohort, model and organization, and was designed by someone who knew interim A021 results.
- **Variance.** The paper's own source [28] (per `related-work.md`) reports up to 30× run-to-run token variation for one task. A 2× gap from two such pairs is not distinguishable from noise. The paper says so itself (Section XI: "neither its size nor, strictly, its sign is established").
- **Missing remedy.** The authors' own figures put one execution at about $10–$26. Three to five executions per arm, plus the third arm the paper names as "the design to test next", would cost a few hundred dollars. For a venue that judges evidence, presenting n = 1 when n = 5 was this cheap is the decisive weakness. It applies to the short paper too.

### R2. The treatment asks for the outcome, and the subjects could read the hypothesis

- G's prompt requires an analysis of "shared invariants", "reusable abstractions" and "the risks of solving each separately". The RQ2 outcome is whether shared invariants are applied uniformly. The paper concedes that RQ2 "partly measures compliance with an instruction only G received".
- In R2, G's analysis had headings that match the later-scored dimensions.
- Every session's baseline contained the hypothesis HY-A028, the full protocol and the planner outputs. Every item text says "proposed cohort of EX-…-A021". Every I prompt names G's branch.
- Transcripts were not retained, so exposure cannot be audited.
- The consequence: the D2/D5/D6 observations may be instruction-following plus demand characteristics. The paper says this. Saying it does not create a finding.

### R3. The consistency construct is post hoc, singly coded by an AI of the same product, and now internally contradicted on its main exhibit (D2)

- The rubric was written after unblinding and grew from 6 to 8 dimensions. It was coded by one AI auditor who knew the arms. There was no second coder, no human coder and no reliability estimate. The guidelines the paper cites [36], [37] ask for human validation.
- The headline caveat on D2 is that I's create/add asymmetry "follows the acceptance criteria literally". That caveat is wrong, or at least selective (D2 in Section 5):
  - every item text cites `requirements/PLANNING-WORK-GROUPS.md`;
  - that document's PRX-GRP-051 applies the repository invariant to every group;
  - R2's blind evaluator classed exactly this I behavior as confirmed acceptance defect M-D2;
  - the paper itself counts M-D2 in RQ3 ("create in I (… no repository check at create)").
- So the same behavior is called spec-compliant in RQ2 and a confirmed acceptance defect in RQ3. The best-supported cross-item difference is therefore also the most ambiguous one.

### R4. The resource comparison is confounded by work volume and per-session overhead, and the paper does not report the volume

- **Volume.** `metrics.json` shows that I inserted 4,048 vs. 2,279 lines in A021 (1.78×) and 3,223 vs. 2,309 in R2 (1.40×). I also wrote about twice as many tests.
- **Normalized cost.** Cost per inserted line is about 1.28× (A021) and 1.50× (R2) for I, not 2.3× and 2.1×.
- **Interpretation.** Some of the extra volume is the very duplication that RQ2 counts against I. Some is I's stronger checkpoint validation and its extra tests.
- **What is missing.** The paper prints neither number.
- **Overhead.** Per-session toolchain installation and mandated AGENTS.md reads scale with the session count. Every I session paid them, and they "could not be removed". The resource contribution (iii) is therefore close to an accounting identity for this platform, and it is already known [7], [8].

### R5. The novelty is narrow and the comparators are unverified

- **What is left.** The paper concedes that granularity mattering is not new [5], [6] and that grouping cutting cost is not new [7], [8]. The remaining novelty is serial, non-communicating sessions combined with a cross-verb consistency measure, on one cohort.
- **How well it was checked.** 24 of the 39 cited references (41 of 57 considered) were checked only through search-engine records. This includes the comparators the novelty claim rests on: CAID [14], MSEval [6], coherence debt [23], cross-context review [26], EvoCode-Bench [12] and the variance source [28].
- **The reference list.** Each entry prints "verified 2026-10-07; depth: search-index". That is candid, but it signals that the related-work section was not done to the usual standard.

### R6. The artifact supports checking the tables, but not the architecture result

- **What works.** The bundle verifies, the tables regenerate exactly, and I could check finding citations at arm heads against the applied patches (for example `WorkGroups.fs:546` `Decisions = request.Decisions`, and R2-G `memberFacts` at 272–296).
- **What is missing:**
  - the probes P1–P10 and the 8-process concurrent create, which carry the behavioral weight of D2, D5 and D6, ship with no scripts and no raw outputs;
  - `check_architecture_findings.py` fails in the bundle because the cited commits do not exist;
  - the deviation register that Table V points to ("A-n/R-n … in the artifact") is not in the artifact;
  - harness "line 5", given to the two later I sessions, is referenced in `sessions.json` but its text is not shipped;
  - transcripts and R2 prompts were never archived.

---

## 3. Required changes (numbered, actionable, located)

1. **D2 framing (Abstract; Sec. IV; Sec. VIII-B "Where G was more consistent"; Sec. IX RQ2; Sec. X mechanism (c) and RQ5; Table II note s; short paper Abstract, Sec. II "Subject and cohort" and Sec. III).**
   - Remove "follows the acceptance criteria literally" and "G generalized the rule beyond the specification", or qualify them. The item texts cite `requirements/PLANNING-WORK-GROUPS.md`, and its PRX-GRP-051 ("A group containing work from different repositories MUST be explicitly cross-repository or be split") applies at create.
   - State that R2's blind evaluator counted I's missing create-time check as confirmed defect M-D2. Reconcile this with RQ3, where the paper already counts it.
   - Redefine the `s` superscript in Table II, or drop it.
2. **"Fewer resources on every worker-only measure we report" (Sec. X, first paragraph).** This is false for Table I: G's largest end-of-session context was 318.8k against I's 262.8k. Restrict the claim to cost, tokens and summed session time, or report context separately. The same overstatement ("under every like-for-like definition") appears in `EV-ROS-2026-A074` "Interpretation"; fix it there too.
3. **Report work volume (Tables I and IV; Sec. VIII-A and IX RQ1).** Add inserted and deleted lines and files changed per arm (data already in `metrics.json`: 2,279/4,048 and 2,309/3,223). Add one sentence that the cost ratio per inserted line is about 1.3× and 1.5×. Discuss the fact that part of I's volume is the duplication RQ2 penalizes and part is extra tests and validation.
4. **Make D7 coherent (Table II, Table VI, Sec. VIII-B, X, XIII; short Sec. III).** The dimension is now named "Reuse of baseline rules". Its cells are G divergent / I unified, yet its direction is "mixed", because G is better on *feature-internal* helpers, which are not baseline rules.
   - Either rename D7 back to "shared abstractions and reuse of baseline rules", or score it I.
   - Then make the prose ("I … reused more baseline rules") and the table agree.
5. **Probe P2 scope (Sec. VIII-B).** "G accepted an item the planner treats as external at both verbs (probe P2)" is inaccurate. P2 tests add only. At create, P3 shows all four arms, I included, accepting the item. Correct the sentence.
6. **Recurrence matrix (Table VI full / Table III short).** The caption says the repeated-context rows are "not counted as recurred evidence", but their Status cell reads "recurred (lower bounds)". Use a distinct status such as "same direction, lower bounds only".
   - Remove or mark the "Grouped checkpoint does not reject blank decision" and "Grouped concurrent creates fail" rows as restatements of D4 and D6.
   - Note that the A021 and R2 blank-decision behaviors differ (stored verbatim vs. silently dropped).
   - In the short paper, also state that D8 did not recur.
7. **Name the models (Sec. V; Sec. V-E; short Sec. II).** Give the implementing model identifier (`claude-opus-5-5`, recorded in `sessions.json`) and the R2 evaluator model ("GPT-5.6 Sol", per R2 `findings.json` and `validity-audit.md` T10) in the paper. This is the third request for it, and the LLM-in-SE guidelines the paper cites require it.
8. **Artifact completeness (Sec. XII; Table V caption; short Data Availability).**
   - Ship the deviation register (A-1…A-17, R-1…R-17).
   - Ship the probe commands and raw probe outputs.
   - Ship the text of harness line 5.
   - Or else remove "in the artifact" from the Table V caption and qualify "the A021 prompts".
   - The short paper's "the scripts that generate every table and in-text number" must carry the same caveat as the full paper's: extraction needs the full history.
9. **Artifact README accuracy.** It says `build_tables.py` "cannot run from the bundle". It does, and it regenerates the tables byte-identically. Say so, because it is the strongest reproducibility point you have. Also add an erratum note to `a021/harness/sessions.json` deviation 2 ("items 02-04 started from the correct heads"), which the paper contradicts (control-02 started stale).
10. **Anonymity residue in the artifact (README §Anonymization; bundle contents).** Alias or remove:
    - `Aegis` (namespace and the NuGet id `AcmelabFoundry.Aegis.Core`; the README says the real package is public under the organization's name, so a NuGet search for `*.Aegis.Core` is one step from the organization);
    - the schema string `ordo.evaluator-identity/1` (a sibling-tool name the README says is aliased elsewhere);
    - `Tutela`;
    - the `EX-ROS-`/`EV-ROS-` record-ID scheme and `ros-fs`;
    - PR and issue numbers.

    If a test pins a hash over a string, patch the test in the review edition and say so.
11. **Short abstract (paper-short.tex l.31).** Add the stale-start caveat to "I validated checkpoint ownership better", as the full abstract and the short body already do.
12. **Preregistration status of the replication matrix (Sec. IX RQ4).** State explicitly that, since R2's analysis headings anticipated the scored dimensions, the architecture recurrences carry little independent weight. Do not list them in the same "recurred" column as the cost rows without that marker.
13. **Package hygiene before any submission (not visible to reviewers, but the evidence index and audits are cited as the basis of the claims).** Bring these records in line with the manuscript:
    - `validity-audit.md`:
      - T05 still says "the hazard is structural to multi-session execution";
      - T13 still uses the old S1 (46.4%/30.7%);
      - T18 still says "the A021 evaluators ran on claude-opus-5-5", while the paper says the second evaluator's model was not recorded.
    - `adjudication-log.md`:
      - AD-05 still has the 6/8 tallies;
      - AD-06 still says "controlled comparison … documented trade-off";
      - AD-07 still says "close (operational) replication".
    - `related-work.md` l.138 still says "assessed by blind evaluators".
    - `evidence-index.json` C11 caveat still says "includes two failed attempts".
    - `architecture-findings.md`:
      - its Verdict says the A021 D3 difference is one "that the probes reproduce" (no probe tests D3);
      - `executed.probes` says "P1-P10", but no P8 is reported.
    - `architecture-findings.json` falsification: "R2 independent lost-update risk is only a design risk" is marked `upheld`, but its note says it was strengthened to an observed defect. The claim was refuted.

## 4. Optional improvements

- Run 3–5 executions per arm and add the third arm (shared committed analysis, then per-item sessions). Pre-provision the toolchain and permissions so that per-session overhead is equal.
- Have a human second coder, blind to arm, score the four patches. Report agreement.
- Report cost by component (cache-write, cache-read, output) to show how much of RQ1 is prompt-caching economics.
- Replace the register IDs in Table V with prose. They mean nothing on the page.
- Use one arm vocabulary. Table notes still say "grouped/independent" ("I/G is the independent/grouped ratio").
- Use the 2 spare pages of the short paper for the work-volume figures and the D2 specification discussion, rather than a full recurrence matrix.
- Cite the decomposition/retry-cost work the authors found but left out (`related-work.md` l.172, arXiv 2605.15425), and empirical work on LLM non-determinism.

## 5. Factual discrepancies between paper text and data

**New errors (introduced by fixes to earlier reviews):**

| # | Paper says (location) | Data says | Source |
|---|---|---|---|
| D1 | "G used fewer resources on every worker-only measure we report" (Sec. X) | Table I: largest end-of-session context G 318.8k > I 262.8k (A021) | `metrics.json` `context_tokens_at_end_platform` |
| D2 | I's admission asymmetry "follows the acceptance criteria literally"; G "generalized the rule beyond the specification" (Abstract; VIII-B; IX; Table II note s; short Abstract, II, III) | Items cite `PLANNING-WORK-GROUPS.md`. PRX-GRP-051 requires the repository invariant for every group. R2 evaluator: M-D2 "Create … has no member-location/repository check", result `defect`. The paper counts it in RQ3 R2. | `baseline/source/requirements/PLANNING-WORK-GROUPS.md:114-118`; `r2/evaluation/findings.json` M-D2; `paper.tex` Sec. IX RQ3 |

**Other discrepancies:**

| # | Paper says (location) | Data says | Source |
|---|---|---|---|
| D3 | "G accepted an item the planner treats as external at both verbs (probe P2)" (VIII-B) | P2 is add only. At create (P3), all four arms accept, I included. | `architecture-findings.md` probe table |
| D4 | D7 "Reuse of baseline rules: mixed" (Tables II, VI) vs. "I … reused more baseline rules" (X, XIII; short III) | Categories G Dv / I U. The G advantage is in feature-internal helpers, not baseline rules. Label and prose cannot both be right. | `architecture-findings.json` comparisons D7 |
| D5 | Table VI caption: repeated-context rows "not counted as recurred evidence" | Status cells read "recurred (lower bounds)" | `tables/replication-matrix.tex` |
| D6 | Table V: register IDs "in the artifact" | No A-n/R-n register in the bundle. `threats.json` has T-ids only; `validity-audit.md` is not shipped. | `build/anonymous-artifact/` (grep) |
| D7 | "The anonymous artifact contains … the A021 prompts" (XII) | Control-04 attempt 3 and control-05 received "harness line 5", whose text is not shipped | `a021/harness/sessions.json` |
| D8 | Short Data Availability: "the scripts that generate every table and in-text number" (no caveat) | Extraction and verification scripts cannot run from the bundle (README; the full paper says so) | artifact `README.md` |
| D9 | Artifact README: `build_tables.py` "cannot run from the bundle" | It runs, and all 8 tables are byte-identical | my run in a scratch copy |
| D10 | Short abstract: "I validated checkpoint ownership better" (no qualifier) | A021's D4 advantage comes entirely from stale item 5 (full abstract and short body say so) | `architecture-findings.md` stale-start section |
| D11 | Short Recurrence: "The classification difference did not [recur]" | D8 also did not recur | Table III (short) |
| D12 | Recurrence row "Grouped checkpoint does not reject blank decision: observed/observed, recurred" | The behaviors differ: A021-G stores it verbatim (LC-05); R2-G silently drops it (LC-06). Both rows come from code reading, not probes. | `architecture-findings.json` `local_correctness` |
| D13 | R2 I active time 100.5 min (Table IV) | 6033 s = 100.55 min; half-up rounding gives 100.6 (float artifact). Nit. | `metrics.json` |
| D14 | Table V register "R17" | Should be "R-17". Nit. | `paper.tex` Table V |
| D15 | Work volume not reported | I inserted 1.78× (A021) and 1.40× (R2) the lines of G | `metrics.json` `diff_insertions` |

**Numbers I checked that match the data exactly:**

- **A021 resources:** cost 9.48 / 21.51 (2.27 → 2.3; 56%); output 112.4k / 244.6k (2.2; 54%); cache-read 24.51M / 45.07M (1.8; 46%); cache-write 290.8k / 950.6k (3.3; 69%); uncached 234 / 658; active 61.2 / 111.4 min (3,671 s / 6,682 s, equal to the sum of the seven per-session spans); spans 1:01 / 4:52; blocked 127.1 min (7,627 s).
- **A021 lower bounds:** script counts 114/237, 52/134, 53/159, 16/34, 13/15, 1/5, 6/15; orientation 9.6 / ≥21.5 min.
- **A021 other:** commits 18/13; conflict merges 0/1.
- **S1, recomputed from per-session rows:** excluding attempt 1 gives 2.08× cost and 1.65× time. Also excluding the bookkeeping session gives 1.99× and 1.53×. These match "2.1×/1.7×" and "2.0×/1.5×", and the short paper's "about 2.1×".
- **R2:**
  - worker-only figures: cost 10.75 / 22.56 (2.1; 52%); output 132.9k / 236.7k (1.8; 44%); cache-read 27.91M / 54.49M (2.0; 49%); cache-write 314.0k / 915.0k (2.9; 66%);
  - including the orchestrator: cost 26.05 (2.4; 59%); output 262.9k (2.0; 49%); cache-read 62.96M (2.3; 56%); common-start span 2:24 (8,615 s; 61%);
  - spans: worker wall-clock 0:56 / 2:13; orientation 9.4 / 34.2 min;
  - lower bounds: script counts 126/343, 69/185, 98/208, 18/26, 8/19, 2/8, 8/34;
  - other: commits 13/21; conflict merges 0/2.
- **Tests:** 811/829 and 819/839 against a 792-test baseline, giving net 19/37 and 27/47.
- **Evaluation counts:** eval-1 rows partially met 2/0; kit evaluation 0/2 defects and 1/3 inconsistencies; R2 defects 2/2 and design risks 2/2 (M-D1, M-D2, N-D1, N-D2).
- **Audit counts:** 32 findings, 100 evidence citations, 12 falsification attempts, 16 comparisons.
- **Architecture behavior:** probe outcomes (8 concurrent creates: 8/1 in both I arms; 5/5 and 6/6 in the G arms; 3/8 and 2/8 operational failures); every Table II cell against `comparisons`.
- **Literature counts:** "41 of 57" references at search-index depth.
- **Earlier review figures:** the stats review's ratio checks.

## 6. Anonymity findings

- **PDFs.** Both are clean. The Author metadata is empty. The text contains no product, organization, person, repository or URL that identifies the authors. Self-description as "one repository built by the authors' organization" is acceptable. Citing two Anthropic engineering blog posts and disclosing Claude Code use does not identify the authors.
- **Artifact: what is clean.** My greps found no personal names, e-mails, `kemiller`, real commit SHAs (all pseudonymized, for example `00080c0…`), real repository URLs (`github.com/anonymous-owner/subjex/...` only), `Praxis` or `Echelon`.
- **Artifact: residual discoverability vectors** (not desk-reject level in my judgment, but they make the subject repository findable with one search, and the paper states that the repository is the authors' own):
  - `Aegis`, in `open Aegis` and in the NuGet id `AcmelabFoundry.Aegis.Core` 1.0.0. The README states that the real package is public under the organization's name.
  - `ordo.evaluator-identity/1`, retained as a schema string, although the README says the sibling-tool names were aliased.
  - `Tutela` (a telemetry module and a requirements file).
  - The `EX-ROS-2026-A021`/`EV-ROS-…` record-ID scheme and `./ros` / `ros-fs`.
  - PR #108, #120, #126 and issue numbers 80, 84 and 90.

## 7. Were the earlier review items resolved?

**review-hostile-1 required changes (1–21):**

| Item | Status |
|---|---|
| 1 (contamination and demand characteristics disclosed) | Resolved |
| 2 (rubric post hoc, 6 → 8 dimensions) | Resolved |
| 3 (two coders, one human; agreement) | **Not done.** Disclosed only. |
| 4 (split consistency from correctness/reuse; score D2 against the criteria) | Done, but **the fix introduced error D2**: the criteria were read from the item text only and ignore the cited PRX-GRP-051, which contradicts RQ3. |
| 5 (G's contention failures and lock gaps) | Resolved |
| 6 (item-4 account; S1 redefined) | Resolved |
| 7 (toolchain-free and stale-start-excluded resource sensitivity) | **Not done.** Declared impossible; the stale-start resource exclusion was not attempted. |
| 8 ("structural" changed to "cause unknown") | Resolved in the paper; still wrong in `validity-audit.md` T05 |
| 9 (R2 S3 correction) | Resolved |
| 10 (preregistered hypothesis, predictions, uncollected measures) | Resolved |
| 11 (main reachable; kit work disclosed) | Resolved, but the R2 evaluator model version is **still not named** |
| 12 (model and runtime versions in the paper) | **Not done.** "Recorded … in the artifact". |
| 13 (do not count correlated rows as recurred evidence) | Partly. Tallies removed, but lower-bound rows are still labelled "recurred" and D4/D6 restatement rows are kept. |
| 14 (precision) | Resolved |
| 15 (artifact scripts, probes, prompts) | Partly. The table scripts now regenerate byte-identically. **Probe scripts and outputs are missing; harness line 5 is missing.** |
| 16 (artifact README) | Resolved |
| 17 (anonymity) | Largely resolved; residue as in Section 6 |
| 18 (AI disclosure) | Resolved |
| 19 (full-text verification; missing related work) | Partly. The proportion is stated and SWE-EVO and an LLM-judge bias reference were added. **No full-text checks; decomposition and non-determinism work still missing.** |
| 20 (time definitions) | Resolved |
| 21 (naming, jargon, register table) | Partly. Register IDs remain, and point to a register that is not shipped. |

**review-statistics:**

- M1–M6 and S1–S8 and S10–S12 are resolved.
- **S9's suggested wording was adopted and is now false.** "used fewer resources on every worker-only measure we report" contradicts Table I's context row (error D1).
- S11's "mixed in both (recurred)" became "mixed in both". That is fine.

**New errors introduced by the fixes:** D1 and D2 above. The stale-audit inconsistencies are listed in required change 13.

## 8. Format and venue compliance

- Full paper: IEEEtran conference format, 9 pages with references from page 8. This is within 10 + 2.
- Short paper: 4 pages, within 6 including references.
- Both have Data Availability and AI-disclosure sections. The AI disclosure names the systems and stages.
- The reference style is non-standard. Every entry carries "verified …; depth: …", which belongs in the artifact, not the bibliography.

## 9. Judgment: state C (package ready; more empirical work required before submission)

**Not D.** There is no fatal flaw in honesty or arithmetic.

- The data are what the paper says they are.
- The confounds are disclosed.
- The artifact allows the tables and the code-level citations to be checked.

The work is publishable in principle as a pilot.

**Not A.** A 10-page full paper needs evidence that can bear interpretation. Here:

- n = 1 per arm, twice, in correlated executions;
- the treatment instructs the outcome;
- the subjects could read the hypothesis;
- the consistency construct is post hoc and singly AI-coded.

Every substantive result is hedged into near-vacuity by the paper's own (correct) caveats. What remains for a full-paper reader is a carefully documented anecdote.

**Not B, as submitted.**

- **Errors in the short paper.** It carries error D2 (the D2 specification caveat is contradicted by the cited requirements document and by the paper's own RQ3 defect count). It also carries the D7 label/prose conflict (D4) and the unqualified D4 abstract claim (D10).
- **Volume omission.** It omits the work-volume confound (R4).
- **Cheap remedy available.** The decisive gap is cheap to close. At about $10–$26 per execution, three to five executions per arm and the proposed third arm (shared analysis, then per-item sessions), with pre-provisioned toolchains, would turn single anecdotes into an early-results paper. Such a paper could say something about the variance of these numbers and about whether the consistency effect comes from retained context or from the mandated analysis.
- **Human coding.** One human, arm-blind second coder on the four patches would remove the strongest construct objection.

**Path to B or A.**

- **B (short paper).** With required changes 1–8 and 11 applied, and even three executions per arm, the short paper would be a defensible SANER early-results submission.
- **A (full paper).** It would additionally need the third arm, a second cohort or repository, and human-validated coding.
