# Review: "One Session or Five? Cohort versus Per-Item Coding-Agent Execution and Cross-Item Architectural Consistency: A Case Study with a Close Replication"

Venue: SANER 2027, Agentic AI4SE track (IEEEtran conference, 10 + 2 pages, double-anonymous). Reviewer role: hostile program-committee reviewer with access to the artifacts.

- **Overall score:** Reject
- **Reviewer confidence:** 4/5. I read the manuscript, the built PDF, every generated table, the supporting package, the anonymous artifact (extracted and run), and the baseline commit's tree.
- **Best-fit format:** Not publishable in its current form. After the required changes, and at most, it could be a 6-page early-results paper (SANER SP&P). It is not a 10-page full paper.

---

## 1. Summary

The paper compares two ways of having a coding agent (Claude Code, one fixed Claude model) implement five coupled work items. The items add a `work group create/show/add/remove/checkpoint` command family to an F#/.NET CLI.

- **G, cohort execution:** one session. It reads all items and a planner explanation, writes and commits a mandated cross-item design analysis, and then implements everything.
- **I, per-item execution:** five fresh sessions run serially on one branch, each with a per-item prompt.

There are two studies, each with one execution per arm: A021 (protocol frozen) and R2 (an internal re-execution, not preregistered).

- **RQ1 (resources):** G cost less. Worker-only cost ratio I/G is 2.27x in A021 and 2.10x in R2. Summed session time was 45% and 44% lower for G.
- **RQ2 (architecture):** an 8-dimension rubric, applied after unblinding by one AI auditor, scores G more unified on 6 dimensions (4 behavioral) in A021 and on 4 (3 behavioral) in R2. I is better on checkpoint ownership (D4), and D7 (reuse of baseline rules) is mixed.
- **RQ3 (local correctness):** mixed. I wrote more tests, and both arms have defects.

The authors frame the result as a trade-off rather than a winner. They disclose many deviations and ship an anonymous artifact.

The paper is unusually candid about its own weaknesses. I credit that. But candour does not turn the evidence into a contribution, for four reasons:

1. **Thin evidence.** The data are two single executions of one five-item cohort in the authors' own tool. The treatment is confounded by design. The headline construct (architectural unification) was scored by one AI auditor, after unblinding, with a rubric written after the results were known.
2. **The resource result is largely mechanical** (four avoided cold starts, each needing a toolchain install) and is close to what the cited prior work already shows.
3. **Treatment and outcome nearly coincide.** The architecture result is close to instruction-following: G was told to identify "shared invariants" and "reusable abstractions" and was then scored on shared invariants.
4. **Undisclosed contamination and mismatches.** Checking the artifacts turned up contamination the paper does not disclose (Section 3, R1 and R2) and several mismatches between paper, data and artifact (Section 8).

---

## 2. Strongest reasons to reject (ranked)

### R1. Undisclosed evidence leakage into both arms' workspaces and task texts

**The hypothesis and protocol were in the workspace.** Every implementing session checked out baseline `8b4ffa3`. That tree contains:

- `research/hypotheses/HY-ROS-2026-A028--grouped-execution-value.md` ("One shared reasoning context for strongly related work reduces repeated work or improves architectural consistency", with its falsification rule);
- the full experiment protocol `research/experiments/EX-ROS-2026-A021--grouped-versus-independent-execution.md` (arms, measures and consistency outcomes);
- the planner baseline outputs.

I confirmed this with `git ls-tree 8b4ffa3`. The repository's AGENTS.md tells agents to work from the research records. Transcripts are not available, so nobody can show that the sessions did not read the hypothesis they were testing. The paper never mentions this.

**The per-item tasks name the cohort.** The paper says the per-item prompts "never mention the cohort" (Abstract; Sec. I; Sec. V-A, "It does not mention the cohort"). That is true of the prompt wrapper only. The work item each session is told to read (`./ros work show PRAXIS-GROUP-0n`, see `acceptance-criteria.txt`) says, for every item:

- "proposed cohort of EX-ROS-2026-A021";
- "Phase two of requirements/PLANNING-WORK-GROUPS.md";
- "Depends on: PRAXIS-GROUP-01".

**The arms knew about each other.** Each control prompt says "Do not fetch, read or check out experiment/a021-grouped". Each control session writes metrics to `EX-ROS-2026-A021-control/`. Both arms therefore knew they were arms of an experiment, and I knew it was the "control".

**Consequence.** "Uninstructed per-item execution" is a mischaracterisation, and there is an uncontrolled demand-characteristics threat. Neither appears in Sec. X or in Table V.

### R2. Treatment and outcome nearly coincide; the rubric is post hoc and selected with knowledge of the differences

**The treatment asks for the outcome.** The G prompt mandates an analysis of "common architecture, shared invariants, … reusable abstractions, … where one design can serve several items, and the risks of solving each separately". The outcome is then "does one rule serve every verb". The authors concede that the treatment is a bundle (Sec. V-A, X). But the problem is worse than a confound: the main RQ2 result largely measures whether the agent complied with an instruction that only one arm received. In R2 the grouped analysis even had headings for the dimensions that were later scored (Sec. V-C; validity-audit T08).

**The rubric changed after the results were known.** `analysis-plan.md` defines a 6-dimension rubric. The paper uses 8: D7 (reuse) and D8 (incompatible duplicates) were added. The plan itself was written after both executions (it cites R2's 2-2 defect tie). Its dimensions mirror what the unblinded A021 evaluators had already reported ("three stores, two join rules, two ID grammars"). Yet Sec. VII says "Following the analysis plan". The rubric is neither preregistered nor independent of the observed differences.

**One coder, no human validation.** One AI auditor from "the same organization" coded every finding after unblinding, with no second coder and no human validation. The guidelines the paper cites ([wagner2025towards], [baltes2025guidelines]) ask for human validation. related-work.md itself notes that the "open LLM baseline" guideline is not met. Neither gap is stated.

### R3. n = 1 per arm per study, and two correlated studies

Each arm was executed once per study. R2 shares the baseline, cohort, model, organisation and orchestrator lineage, and its designer knew interim A021 results.

**Token variance swamps the effect.** The paper cites [bai2026tokens]. The authors' own notes (related-work.md) record that it reports run-to-run token differences of up to 30x for the same task. The paper softens this to "can vary widely" (Sec. X), yet the abstract still headlines 2.27x and 2.10x ratios with three significant figures, and the prose gives "58.72%" and "61.08%".

**The replication matrix inflates agreement.** "17 reproduced" rows (Table IV, Sec. IX) are not 17 pieces of evidence:

- 8 rows are mechanically correlated resource and context counts (cost, tokens, time, requests, reads), several of them ratios of lower bounds.
- 2 of the "local correctness" rows restate D2/D7 (P2) and D6 (lost update).

### R4. The resource comparison is unfair to the per-item arm, and the paper misdescribes its failed attempts

**Per-session setup is counted as execution-mode cost.** No .NET toolchain was preinstalled, so every session installed an SDK from apt and worked out an FSharp.Core override (Table V, A-1/R-1/R-2). That setup is counted 5 to 7 times for I and once for G, including inside "time to first code change". No sensitivity analysis removes it.

**One per-item session hit a permission environment the others did not.** The A021 item-4 sessions met a permission prompt that "Control-01..03 and the grouped arm did not need" (sessions.json). Permissions were then changed mid-arm (A-4).

**"Failed attempts" is wrong.** The paper says "item 4 failed twice before a third attempt completed it" (Sec. V-B), and Table I calls these "two failed item-04 attempts". sessions.json shows otherwise:

- Attempt 2 pushed the remove implementation (`881a34d`) before it blocked.
- Attempt 3 was bookkeeping only. metrics.json says: "no production-code mutation occurred in this session".

**Consequences of that error:**

- S1 ("excluding the failed attempts") removes the cost of the session that actually wrote item 4, so it is not a meaningful sensitivity analysis.
- The per-item arm's "repeated context" lower bounds contain no transcript data at all for the session that implemented item 4.

**Stale checkouts are blamed on the execution mode.** All four stale-checkout events hit per-item sessions. The paper calls the hazard "structural to multi-session execution on this platform" (Sec. X, Internal validity). The audit lists "The cause of the stale checkouts" under *could not determine*, and the protocol record says "cause unknown". Treating an unexplained orchestration fault as a property of the execution mode is unsupported. A021 control-02's stale clone also inflated its metrics (validity-audit A6), and no resource sensitivity analysis excludes it.

### R5. Weak novelty relative to cited work

**Partitioning results are already known.** CooperBench already shows on 600+ tasks that one agent holding two coupled features beats a split [khatua2026cooperbench]. Batch prompting and multi-task inference already show that grouping cuts cost [cheng2023batch, son2024multitask]. The frozen planner prediction itself states "4 cold starts avoided". That G costs less than 5 to 7 cold-started sessions is close to definitional under prompt-caching economics: cache-write tokens are 3.27x higher for I.

**What remains new is not established.** The claimed novelty is (a) serial, non-communicating sessions and (b) the unification construct. (b) is undermined by R2 above. (a) is a narrow variant: the authors' own notes say "What is not novel and should not be claimed: … that splitting coupled work hurts consistency; that grouping reduces token cost". The paper still lists the resource findings as contribution (iii) and puts them first in the abstract.

**The novelty search is thin.** The novelty claim (Sec. II, "Novelty") rests on a literature pass in which 41 of 57 checked entries are search-index extracts only. This includes the main comparators [ren2026coordination], [geng2026caid], [mohammadi2026coherence] and [song2026crosscontext]. The paper describes this as "several entries were verified only at abstract level". It is most entries, and even abstract pages were not fetched.

### R6. The "unified" construct conflates consistency, correctness and specification compliance

**D2 rewards a consistent error and penalises following the specification.**

- G's D2 is "unified", yet that single rule is wrong: it admits an item the baseline planner classifies as external (probe P2).
- I's D2 "divergence" (repository check at add but not at create) is exactly what the acceptance criteria specify. PRAXIS-GROUP-03 requires the repository check at add, and PRAXIS-GROUP-01 does not require it at create.
- The rubric therefore rewards G for going beyond the specification and penalises I for following it. Mechanism (c) in Sec. VIII ("Per-item item 3 … introduced the add-only repository check") presents spec compliance as evidence of "independent resolution of shared invariants".

**D4 and D7 do not measure cross-item unification.**

- D4 (checkpoint ownership) is a single-verb correctness property. The definition of "unified" ("one rule serves every verb") cannot apply to it, yet I is scored "unified" and G "missing".
- D7 scores reuse of *baseline* rules, which is a different construct.
- Mixing these into one tally of "more unified" dimensions makes the 6-1-1 and 4-1-1-2 counts uninterpretable.

**G's concurrency failures are not reported.** The architecture audit (`data/architecture-findings.json`, LC-08) records that under 8 concurrent creates, 3 of 8 (A021-G) and 2 of 8 (R2-G) requests fail with "unexpected operational failure". The paper reports only I's lost update and says "Every group write is serialized" for G (Sec. VIII-B). The audit also records two weaknesses in G's own D6 that the paper omits: A021-G's lock does not serialise with `work checkpoint`, and R2-G reads member facts outside the lock.

### R7. Blinding and role contamination are worse than the paper states

**The R2 evaluator could reach the answer.** R2's evaluator could reach `main`. According to validity-audit T09, `main` already held:

- EV-A064, which states the grouped arm's "one store" result;
- the phase-two implementation built on the A021 grouped arm.

Comparing a blind patch with `main` reveals which arm is G. The paper mentions only that the arm branches were reachable.

**An implementer later worked on the blinding kit.** One A021 per-item implementing session (control-02) later did evaluation-kit work (EV-A065/A066; metric-conflicts.json CF-07). The paper describes this only as "Two A021 sessions continued after their experiment work" (Sec. XII).

**Model identities are missing.** The A021 evaluators shared the implementers' model. The R2 evaluator's model version (named in the audit) is not given. The implementing model's identifier is not given in the paper at all ("recorded per session in the artifact"), contrary to the LLM-in-SE reporting guidelines the paper cites.

### R8. The preregistered analysis is not reported

A021's frozen protocol defines:

- a hypothesis with an explicit falsification clause: "Weakened if the grouped arm … loses quality";
- outcome classes (PRX-GRP-087);
- planner predictions;
- a list of measures, including retries, failed approaches, validation failures, design revisions, rework, regressions and "forgotten requirements".

The paper says the protocol was frozen (Sec. V-B), but it never reports:

- the preregistered hypothesis outcome;
- that the authors' own record classified A021 as "grouping clearly beneficial" despite the falsification clause (validity-audit T12);
- the prediction results (the duration prediction failed);
- most of the preregistered measures.

It also does not report that the protocol's own controls were not followed: patch-series evaluation and retention of the event logs. Claiming a frozen protocol while silently replacing its analysis with a post hoc one is a reporting failure.

### R9. Reproducibility and anonymity of the submission artifact

**Reproducibility.** I extracted `build/anonymous-artifact.tar` and ran it.

- `verify_artifact.py --bundle` passes 9/9 checks: checksums and patch application only.
- Regenerating the tables with the bundled `analysis/build_tables.py` produces a `macros.tex` that differs from the manuscript's. It lacks `\AcostReductionExclFailed` (46.4) and `\AactiveReductionExclFailed` (30.7), so the bundled script is stale and cannot regenerate the S1 numbers.
- `build_architecture_table.py`, which produces Tables III and IV and all `\Arch*`/`\Rep*` macros, is not in the bundle.
- `check_architecture_findings.py` fails because the cited commits do not exist outside the authors' repository.
- `extract_metrics.py` and `verify_metrics.py` need the full Git repository. The README admits they "cannot run from the bundle".
- The runtime probes behind D2, D5 and D6 (P1–P10 and the concurrent create) have no scripts and no raw outputs in the artifact. There are only summary exit codes in a markdown file that is not shipped.
- R2 prompts and all transcripts are absent.

A reviewer therefore cannot regenerate the architecture result or the repeated-context counts.

**The artifact contradicts the paper.** Its README still carries the old title, "Does Shared Agent Context Reduce Architectural Drift? A Blinded Replication Study …". It says A021 was "compared by two blind evaluations" and that R2 used "the same protocol" with "a further independent blind evaluator". The paper retracts all three claims.

**Anonymity.** Product and organisation names are deliberately retained throughout the artifact. In the baseline these include:

- "Praxis", `PRAXIS-GROUP-*`, "Echelon", the package `EchelonFoundry.Aegis.Core` and "Ordo";
- `github.com/anonymous-owner/praxis` URLs;
- PR numbers ("PR #126", "PR #120").

Commit SHAs are kept everywhere. The README asks reviewers "not to use these names or SHAs to look up the subject system's public repository". That request admits the identities are one search away, and the authors' own submission plan lists exactly these as desk-reject risks. The paper also says the subject is the authors' own tool ("extends the planner's own grouping feature"), so finding the repository identifies the authors.

---

## 3. Unsupported or overstated claims (quoted, with location)

1. **Abstract and Sec. I:** "five fresh sessions … with prompts that never mention the cohort". The task text each session reads says "proposed cohort of EX-ROS-2026-A021" and lists the dependencies. For R2 the claim cannot be checked, because the prompts were not archived (evidence-index C01 caveat).
2. **Abstract:** "Per-item execution was better on checkpoint ownership validation in both studies, reused more of the existing baseline rules". D7 is classified as *mixed*, not as an I advantage. In A021 the whole D4 advantage comes from a stale-start item (Sec. VIII-B, S3).
3. **Sec. VIII-B:** "four of them with a behavioral difference that the probes reproduce". D3 (A021) has no probe. The probe table covers P1–P10 and concurrency, none of which tests show/checkpoint classification, so D3's behavioral status rests on code reading only.
4. **Sec. VIII-B:** "Every group write is serialized (D6)". This omits G's failures under contention (LC-08), the A021-G lock that does not cover `work checkpoint`, and R2-G's reads outside the lock.
5. **Sec. V-B:** "item 4 failed twice before a third attempt completed it". Attempt 2 implemented item 4 and attempt 3 did bookkeeping (sessions.json; metrics.json `time_to_first_code_s` note for control-04).
6. **Sec. IX, R2 S3:** "the D2, D5 and D6 divergences come from items that started correctly". architecture-findings.md says D2's add-only check came from item 03, which started from a stale checkout and merged before implementing. It also says the show-side D5 gaps (P7) "plausibly" came from stale item 02.
7. **Sec. X, Internal validity:** "The hazard is structural to multi-session execution on this platform". The cause is recorded as unknown (validity-audit "Could not determine"; protocol "cause unknown").
8. **Sec. VII:** "Following the analysis plan" (rubric). The plan has 6 dimensions and the paper scores 8.
9. **Sec. I, contribution (i):** "a controlled comparison". It is not controlled: the prompts differ in five ways, the permission environment changed mid-arm, the orchestration differed (R2), the checkouts were stale, and the workspaces were contaminated (R1).
10. **Sec. I, contribution (ii):** "a symmetric eight-dimension measurement". The rubric was designed after unblinding, and D4 and D7 are not symmetric applications of one construct (R6).
11. **Sec. V-C:** "R2 reused the baseline, cohort, criteria, organization and implementing model … It changed the evaluator". R2 also changed the prompts, the harness note, the orchestration structure (its own orchestrator) and the metric definitions (after unblinding). Under Gómez et al., changing the protocol makes it more than a change to the experimenters.
12. **Sec. XII:** "Two A021 sessions continued after their experiment work". This understates it: one per-item implementer then worked on the evaluation kit (CF-07), and attempt 2 "ran again after being abandoned".
13. **Sec. II:** "several entries were verified only at abstract level". It is 41 of 57, as search-index extracts. **AI disclosure:** "verified all … references" contradicts related-work.md ("Before submission, re-check every search-index entry").
14. **Sec. VIII, mechanism (c):** item 3's add-only repository check is offered as independent resolution of shared invariants. It is what PRAXIS-GROUP-03's acceptance criteria require.
15. **Sec. VI:** "We use four time definitions". Five are then defined: session span, summed active, wall-clock span, R2 "elapsed", and blocked time.

---

## 4. Weak operational definitions

- **"Unified":** "one rule serves every verb". This is undefined for single-verb concerns (D4) and for reuse of external rules (D7). It does not distinguish consistent-but-wrong from correct (D2 with P2).
- **"Divergent":** "concrete evidence shows different behavior for the same input". It counts divergences that the specification itself mandates (D2).
- **"Behavioral difference":** defined only in a table footnote. For D3 (A021) it is inferred from code, not observed.
- **"Affinity" / "high-affinity cohort":** used throughout (Sec. IV, X) and in the "not tested" rows, but never defined. The reader is not told how the planner computes affinity or what "high" means.
- **"Workers":** includes the failed attempts, one of which did the implementation (R4).
- **"Lineage" / "same agent lineage":** undefined jargon. It apparently means that AI agents of the same product designed, ran, evaluated and analysed the study. Say so plainly.
- **"Defect":** the A021 "kit evaluation" defects include a lost update that no acceptance criterion requires, while G's failure of an explicit criterion (PRAXIS-GROUP-05: "requires the same durable-checkpoint verification as work checkpoint") is placed under architecture (D4) rather than correctness.

---

## 5. Missing related work

- **Related work the authors found but did not cite.** related-work.md notes it as relevant: "Runtime-Structured Task Decomposition for Agentic Coding Systems" (arXiv 2605.15425), where static decomposition raised retry cost by up to 80.5% over a monolithic run. This bears directly on RQ1.
- **SWE-Evo** (`le2025sweevo`) was verified and is in the reference check, but is not cited. It covers multi-step evolution in a persistent codebase.
- **Run-to-run nondeterminism of LLM code generation** (empirical studies of ChatGPT non-determinism in code generation). This is needed to interpret n = 1.
- **LLM-judge self-preference and self-recognition bias.** Relevant because the A021 evaluators ran on the implementers' model.
- **Empirical studies of agent instruction and context files (AGENTS.md / CLAUDE.md).** Relevant because "AGENTS.md reads" is a reported metric and the per-session reread is design-entailed.
- **Human SE literature on task switching, interruption cost and batch size.** This is the human analogue of the cold-start cost and is absent.
- **Specification ambiguity and under-specification in agent tasks.** Relevant because the cohort's own criteria are inconsistent between create and add (R6).

---

## 6. Formatting, venue compliance and writing

- **Template and length.** IEEEtran `[10pt,conference]`, no compsoc: compliant. The PDF is 10 pages, with references starting on page 8: within 10 + 2.
- **Data Availability.** The section is present and correctly placed after the Conclusion.
- **AI disclosure is non-compliant with IEEE policy.** IEEE requires the AI system to be identified. The section names no system ("an AI coding assistant").
  - It also understates AI involvement. AI agents authored the cohort, designed and orchestrated the study, evaluated the arms, applied the rubric and audited the records. The disclosure covers only "manuscript text … scripts … literature summaries".
  - It claims references were verified, which the package contradicts.
- **PDF metadata** is clean.
- **Writing:**
  - The paper uses three naming schemes for the arms: cohort/per-item, grouped/independent, G/I. The artifact uses a fourth (grouped/control, X/Y, N/M).
  - It relies heavily on artifact register IDs (A-1…R-17) that mean nothing on the page.
  - Table V is a full-width deviations log that reads like an audit trail rather than a paper.
  - RQ5 ("boundary") is a limitations section, not a research question.
  - The 26-word title combines a question, a comparison and a design label.
  - The prose uses spurious precision ("58.72%", "61.08%") next to tables rounded to one decimal.

---

## 7. Required changes (numbered, actionable)

1. **(Sec. V, X, Table V)** Disclose that the baseline workspace contained the hypothesis HY-A028, the full protocol and the planner outputs. Disclose that every work item text named the cohort, the experiment ID and the dependencies, and that each control prompt named the grouped branch. Remove "never mention the cohort" from the Abstract and Sec. I, or qualify it as "the prompt wrapper did not". Add a demand-characteristics threat.
2. **(Sec. VII, Table III)** State that the rubric (8 dimensions) was defined after both executions and after the unblinded A021 evaluations. State that D7 and D8 were added beyond the analysis plan's 6. Report the 6-dimension result as the closest thing to planned.
3. **(Sec. VII)** Have at least two independent coders, at least one of them human and blind to arm, apply the rubric to the four blind patches. Report inter-rater agreement. Without this, drop the dimension tallies from the Abstract.
4. **(Sec. VII, Table III)** Split the rubric into "cross-item consistency" (D1–D3, D5, D6, D8) and "correctness/reuse" (D4, D7). Score D2 against the acceptance criteria and state that I's create/add difference matches the specification. Fix mechanism (c) in Sec. VIII accordingly.
5. **(Sec. VIII-B, Table IV)** Report G's concurrent-create failures (3/8 and 2/8, LC-08), the A021-G lock gap and R2-G's out-of-lock reads. Revise "Every group write is serialized".
6. **(Sec. V-B, Table I footnote, Sec. VIII-A S1)** Correct the item-4 account: attempt 2 implemented item 4 and attempt 3 did bookkeeping. Redefine S1, or drop it, since it removes the implementing session. State that I's transcript counts exclude item 4's implementation entirely.
7. **(Sec. VIII-A, IX)** Add a sensitivity analysis that removes toolchain-setup time and cost from every session, using the transcripts or an estimate from the setup commands. Add one that excludes the stale-start sessions' resources (A021 control-02 and control-05, R2 C2 and C3).
8. **(Sec. X)** Replace "The hazard is structural to multi-session execution on this platform" with "cause unknown", as the authors' own audit says.
9. **(Sec. IX)** Correct "the D2, D5 and D6 divergences come from items that started correctly" to match architecture-findings.md (item 03 stale for D2; item 02 contributes to D5).
10. **(Sec. V-B, new subsection)** Report the preregistered hypothesis HY-A028, its falsification clause and the outcome. Report the planner predictions (including the failed duration prediction) and which preregistered measures were not collected. Report that the original record classified A021 as "clearly beneficial", and why the paper departs from that classification.
11. **(Sec. V-D)** Disclose that the R2 evaluator could reach `main`, which held EV-A064 and the grouped-derived implementation. Disclose that an A021 per-item implementing session later prepared the evaluation kit. Name the R2 evaluator model version.
12. **(Sec. V)** Name the implementing model and runtime versions in the paper, not only in the artifact.
13. **(Abstract, Sec. IX, Table IV)** Stop counting correlated resource rows and lower-bound ratios as independent "reproduced" outcomes. Report the replication at the level of outcome families (resources, architecture, correctness). Remove rows that duplicate D2/D7 and D6.
14. **(Abstract, Sec. VIII-A, IX)** Report resource ratios with at most two significant figures and next to the documented run-to-run variance (up to 30x, [bai2026tokens]). Remove "58.72%" and "61.08%".
15. **(Artifact)** Ship the current `build_tables.py` and `build_architecture_table.py`, check that `macros.tex` and the architecture/replication tables regenerate byte-identically from the bundle, and ship the probe scripts and raw probe outputs. Ship the actual prompts used for A021 control-04 attempt 3 and control-05, including the added harness line.
16. **(Artifact README)** Remove the old title and the "two blind evaluations", "same protocol" and "independent blind evaluator" claims, so the artifact matches the paper.
17. **(Artifact anonymity)** Alias the product, organisation and package names (Praxis, Echelon, Aegis, Ordo, `PRAXIS-*`). Remove PR numbers and `github.com/anonymous-owner/praxis` URLs. Replace commit SHAs with logical IDs, or rewrite history in a fresh anonymous repository. If aliasing breaks the build, say so and provide the artifact at camera-ready, as SANER allows.
18. **(AI disclosure)** Name the AI system(s). List every stage that used AI: cohort authoring, design, orchestration, evaluation, rubric coding, auditing and writing. Remove the claim that all references were verified, or actually verify them against the full text.
19. **(Sec. II)** Re-verify the central comparators ([khatua2026cooperbench], [ren2026coordination], [geng2026caid], [mohammadi2026coherence], [song2026crosscontext], [shen2026evocode], [bai2026tokens]) against their full text. Correct "several entries" to the true proportion. Add the missing related work from Section 5.
20. **(Sec. VI)** Fix "four time definitions" (five are defined).
21. **(Global)** Use one naming scheme for the arms. Define "affinity" and "lineage", or drop them. Move the register-ID table to the artifact and keep a short prose summary.

## 8. Every factual discrepancy found between paper, data and artifact

| # | Paper says (location) | Data / artifact says | Source |
|---|---|---|---|
| 1 | "item 4 failed twice before a third attempt completed it" (V-B); "two failed item-04 attempts" (Table I note) | Attempt 2 pushed the implementation `881a34d`. Attempt 3: "no production-code mutation occurred in this session"; "metrics/control-04.json covers attempt 3 only (bookkeeping)" | `a021/harness/sessions.json`; `data/metrics.json` (`time_to_first_code_s`, control-04) |
| 2 | Per-item prompts "never mention the cohort" (Abstract, I, V-A) | Each item text read by the session: "proposed cohort of EX-ROS-2026-A021 … Depends on: PRAXIS-GROUP-01"; control prompts name `experiment/a021-grouped`; R2 prompts unarchived | `acceptance-criteria.txt`; prompts; evidence-index C01 caveat |
| 3 | "four of them with a behavioral difference that the probes reproduce" (VIII-B) | No probe tests D3; the probes cover D2, D5 and D6 only | `architecture-findings.md` probe table |
| 4 | "Every group write is serialized (D6)" (VIII-B); concurrency reported only as an I defect | G: 3/8 (A021) and 2/8 (R2) concurrent creates exit 1 with "unexpected operational failure"; A021-G lock does not serialise with `work checkpoint`; R2-G reads facts outside the lock | `data/architecture-findings.json` LC-08; `architecture-findings.md` D6 |
| 5 | R2: "the D2, D5 and D6 divergences come from items that started correctly" (IX) | D2's add-only check was introduced by item 03, which started from a stale checkout; P7/show-side D5 gaps "plausibly" from stale item 02 | `architecture-findings.md`, stale-start section |
| 6 | Stale-checkout hazard "is structural to multi-session execution on this platform" (X) | "Could not determine: The cause of the stale checkouts"; protocol: "cause unknown" | `validity-audit.md`; `evaluation-protocol-with-results.txt` |
| 7 | Rubric "Following the analysis plan" (VII), 8 dimensions | The analysis plan defines 6 dimensions | `analysis-plan.md` §2 |
| 8 | S1–S5 "defined in the analysis plan" (VII) | The plan's S1 asks whether architecture holds (not resource exclusion); its S3 concerns the R2 stale checkout, not A021 item 5 | `analysis-plan.md` §5 |
| 9 | "We use four time definitions" (VI) | Five definitions follow in the same paragraph | `paper.tex` Sec. VI |
| 10 | Prose "58.72%", "61.08%" (IX) | Table II gives 58.7 and 61.1; inconsistent precision | `tables/macros.tex`; `tables/resources-r2.tex` |
| 11 | Artifact contains "all A021 prompts" (XII) | Control-04 attempt 3 and control-05 received an extra harness line and `extraAllowedTools`; the shipped prompt files and harness note (4 lines) do not contain it | `sessions.json` (deviation note); `a021/harness/` |
| 12 | Scripts "regenerate every table and in-text number" (Data Availability, XII) | Bundled `build_tables.py` regenerates a `macros.tex` that lacks `\AcostReductionExclFailed` and `\AactiveReductionExclFailed`; `build_architecture_table.py` is absent; `check_architecture_findings.py`, `extract_metrics.py` and `verify_metrics.py` fail or cannot run without the private Git history | Run in scratch copy of `build/anonymous-artifact.tar` |
| 13 | Paper title, "not effectively blind" A021 evaluations, "close replication" | Artifact README: title "Does Shared Agent Context Reduce Architectural Drift? A Blinded Replication Study …", "compared by two blind evaluations", R2 "the same protocol … further independent blind evaluator" | `artifact/README.md`; `build/anonymous-artifact/README.md` |
| 14 | "Two A021 sessions continued after their experiment work" (XII) | Control-02, an I-arm implementer, "later did evaluation-kit work (EV-A065/A066)"; attempt 2 "ran again after being abandoned" | `data/metric-conflicts.json` CF-07 |
| 15 | R2 blindness limited by reachable arm branches (V-D) | The evaluator could also reach `main`, which held EV-A064 ("one store") and the grouped-derived implementation | `validity-audit.md` T09 |
| 16 | "several entries were verified only at abstract level" (II); disclosure: authors "verified all … references" | 41 of 57 entries are search-index extracts with no fetch; related-work.md: "Before submission, re-check every search-index entry" | `data/references-verification.json`; `related-work.md` |
| 17 | "the second evaluator's model was not recorded" (V-D) | validity-audit T18: "The A021 evaluators ran on claude-opus-5-5" (plural), contradicted by its own "Could not determine" list. The package is internally inconsistent | `validity-audit.md` |
| 18 | "R2 reused … and changed the evaluator" (V-C) | R2 also changed the prompts, harness note, orchestration (own orchestrator) and metric definitions after unblinding (R-2, R-11, R-13, R-14) | `validity-audit.md` deviations register |
| 19 | "Per-item execution … reused more of the existing baseline rules" (Abstract) | D7 is classified "mixed" in both studies; G is stronger on feature-internal helpers | `data/architecture-findings.json` comparisons |
| 20 | "Per-item item 3 … introduced the add-only repository check" as independent resolution of shared invariants (VIII) | PRAXIS-GROUP-03's acceptance criteria require the repository check at add; PRAXIS-GROUP-01's do not require it at create | `acceptance-criteria.txt` |
| 21 | Frozen protocol: "protocol, prompts and planner predictions were committed" (V-B), with no outcome reported | Protocol has outcome classes and a falsification clause; the original record classified A021 as "grouping clearly beneficial"; the duration prediction failed; preregistered patch-series evaluation and event-log retention were not done | `evaluation-protocol-with-results.txt`; `validity-audit.md` T12 |
| 22 | "MSEval varies collaboration topology" / topology "moved quality, cost and time" (I) | Notes record score and wall-clock effects; cost is not mentioned | `related-work.md` (ren2026coordination row) |
| 23 | Table II (quality) labels A021 "work-group tests added (evaluation 1)" and R2 "net F# tests added vs. baseline" and compares them in Table IV | Both rows come from the same metric (`fsharp_tests_net_added`); the labels imply different definitions | `data/metrics.json` |

These figures checked out exactly against `data/metrics.json`:

- **A021 resources:** cost 9.48/21.51 (2.27x, 56.0%); output 112,377/244,630; cache-read 24.51 M/45.07 M; active 61.2/111.4 min; span 4:52; blocked 127.1 min; S1 46.4% and 30.7% (recomputed from per-session rows).
- **A021 counts:** tests 811/829/792; partially met 2/0; kit 0/2 and 1/3.
- **R2:** worker cost 10.75/22.56 (2.10x, 52.3%); incl. orchestrator 26.05; elapsed 2h23m35s = 8,615 s; first code 9.4/34.2.
- **All script lower bounds** and the 17/1/2/6 replication tally.

## 9. Optional improvements

- Run at least 3 to 5 executions per arm, and add the third arm the authors themselves propose: a shared committed analysis followed by per-item sessions. This is the only way to separate retained context from the design step, and the cost is small (about $10–25 per execution).
- Add a second cohort in a repository the authors did not build, ideally with low affinity.
- Pre-provision the toolchain and fix the permission configuration so that per-session overhead is equal across arms.
- Give every per-item session in a "fair" control the same decision record that G produced. This turns the comparison into one of context retention rather than instruction.
- Archive full transcripts, so that the repeated-context counts are not lower bounds and arm isolation can be audited.
- Report cost per component (cache write vs. cache read vs. output) to show how much of RQ1 is prompt-caching economics.

## 10. Verdict on format

**Not publishable yet.** As a 10-page full paper, the contribution does not justify the space:

- one cohort and n = 1 twice;
- a treatment that asks for the outcome;
- a post hoc, single-AI-coder rubric with no human validation;
- an artifact that neither reproduces the architecture result nor preserves anonymity;
- undisclosed contamination of the workspaces.

After required changes 1–18, an honest **6-page early-results paper** (SANER SP&P) could make a narrow, useful point. The point would be: in one cohort, an instructed single-session run settled three cross-item invariants (admission, error contract, locking) once, while uninstructed serial sessions did not; and per-item sessions were stronger on per-item verification. It should be framed as hypothesis-generating, with the three-arm design as the stated next step.
