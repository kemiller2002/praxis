# A021 related work and novelty assessment

Prepared 2026-10-07 by the literature agent (Agent 4). The companion files are `references.bib` (57 entries) and `data/references-verification.json`.

## Verification caveat (read first)

The session's egress policy blocked direct fetches of every primary scholarly record host tried: arxiv.org, export.arxiv.org, doi.org, dl.acm.org, ieeexplore.ieee.org, link.springer.com, dblp.org, openreview.net, iclr.cc, proceedings.neurips.cc, aclanthology.org, api.semanticscholar.org, api.crossref.org, api.openalex.org, unpaywall, huggingface.co and alphaxiv. The two hosts that worked were github.com / raw.githubusercontent.com and anthropic.com. The entries therefore have four levels of verification:

| Depth label | Meaning | Entries |
|---|---|---|
| **abstract (ACL Anthology canonical data)** | Title, authors, pages, DOI and abstract read from the official `acl-org/acl-anthology` metadata repository, which is the source of aclanthology.org | 9 |
| **official repo citation / README** | BibTeX or method description read from the authors' project repository (fetched), plus a search-index check of the arXiv/OpenReview record (SWE-bench, SWE-agent, MetaGPT, MAST; CooperBench README) | 5 |
| **search-index** | The authoritative URL (arXiv abs, ACM DL, NeurIPS proceedings, etc.) showed up in WebSearch, and its title, authors and abstract came from the search engine's extraction of that page. Claims stay at abstract level. Includes MSEval (README also fetched) and the Cognition blog (fetch blocked). | 41 |
| **full text (grey)** | Anthropic engineering blog posts fetched in full | 2 |

**Before submission, re-check every search-index entry against its DOI or arXiv record.** This matters most for author lists marked "and others", for DOIs flagged "re-check", and for the 2026 arXiv preprints, which are recent and not peer reviewed. No title, author, DOI or finding below was filled in from memory. A field that could not be checked was left out.

---

## 1. Coding agents and the benchmarks that define "a task"

| Key | What it established | How A021 differs | Role | Verification |
|---|---|---|---|---|
| `jimenez2024swebench` | SWE-bench has 2,294 real GitHub issue/PR task instances from 12 Python repos. At publication, models resolved very few of them. It established the **one issue = one isolated task instance** evaluation unit. | A021's unit of analysis is a *cohort* of five related items, and it treats how they are partitioned as the treatment. SWE-bench-style evaluation never runs related instances in one context. | motivation | repo citation + search-index (abstract) |
| `yang2024sweagent` | SWE-agent showed that the agent-computer interface (ACI) design strongly affects issue resolution (12.5% on SWE-bench at the time). | It shows that agent *scaffolding* is an experimental variable. A021 argues that *session partitioning* is another such variable, at a level above the scaffold. | motivation | repo citation + search-index |
| `wang2025openhands` | Open platform (event-stream architecture, sandboxed runtime) for generalist software agents, ICLR 2025. | Platform paper. It does not study session granularity. | motivation | search-index |
| `zhang2024autocoderover` | Program-structure-aware search for issue resolution. Reports per-issue cost (about $0.43) and per-issue time. | Cost is reported *per issue*. A021 shows that the per-issue cost depends on whether issues share a session. | motivation | search-index |
| `xia2025agentless` | A simple three-phase, non-agentic pipeline (localize, repair, validate) competed with complex agents at low cost ($0.70 on SWE-bench Lite). Published in PACMSE (FSE) 2025. | Precedent for showing that architectural choices *around* the model can dominate. A021 finds an analogous effect for session partitioning. | comparison | search-index |
| `li2025feabench` | FEA-Bench is a repository-level *feature implementation* benchmark built from PRs in 83 repos and paired with unit tests. | It is closest to A021's work-item type (feature work), but each instance is still evaluated alone. | comparison | ACL abstract |
| `deng2025swebenchpro` | SWE-Bench Pro: 1,865 long-horizon, multi-file, contamination-resistant enterprise-style tasks. Pass@1 stayed below 45%. | It makes "long-horizon" mean *one bigger task*. A021's long horizon is *several related tasks*. | motivation | search-index |
| `le2025sweevo` | SWE-EVO is a long-horizon software-evolution benchmark. | Evolution across versions, not partitioning of a cohort. | motivation | search-index |
| `joshi2025swebenchcl` | SWE-Bench-CL orders SWE-bench Verified issues chronologically and measures transfer and forgetting with a semantic memory module. | It is the closest benchmark to "context carried across tasks". Its interest is *learning/transfer* through an external memory. A021 compares retained live context with fresh sessions and measures *architectural unification*. | comparison | search-index |
| `shen2026evocode` | EvoCode-Bench has 26 stateful tasks with 5-15 rounds each in a *persistent workspace* (reported as the same agent session). Single-round scores from a reference prior state beat multi-round scores by 22-40 points, and regressions dominate the failures. | Most relevant counter-evidence. Carrying state forward can *hurt* when the agent's own earlier output is wrong. A021's grouped arm benefits from shared state, so the paper must explain why: a design analysis was written up front, and the cohort is high-affinity. | comparison / limitations | search-index |
| `chen2021codex` | Codex/HumanEval established functional-correctness (pass@k) evaluation of generated code. | A021's outcomes are architectural and blind-judged, not pass@k. | motivation | search-index |
| `yao2023react` | ReAct interleaves reasoning traces and actions, and is the base pattern of tool-using agents. | Background only. | motivation | search-index |

## 2. Multi-agent vs single-agent development, and how work is partitioned (closest prior work)

| Key | What it established | How A021 differs | Role | Verification |
|---|---|---|---|---|
| **`khatua2026cooperbench`** | **CooperBench**: more than 600 tasks, each with two features that "can be implemented independently but may conflict". **Solo** has one agent implement both features in one container. **Coop** has two agents, one feature each, in separate containers that talk over a message channel. Coop success was on average about 30% lower ("curse of coordination"). Failures include jammed and vague communication, deviation from commitments, and wrong expectations about the other agent's plans. | **This is the closest prior work.** It already establishes that splitting a coupled pair of features across agents/contexts hurts versus one agent holding both. Differences: (i) CooperBench's split agents are *concurrent and communicating*, while A021's independent sessions are *serial, non-communicating, and share only the branch*; (ii) CooperBench measures task success and conflicts, while A021 measures *cross-item architectural unification* (one store / admission rule / error envelope), cost and repeated reads, with blind evaluators; (iii) CooperBench uses pairs and A021 a five-item cohort with an up-front design analysis; (iv) A021 also reports where independent sessions *won* (local acceptance details, more tests). | comparison (primary) | README fetched + search-index |
| `ren2026coordination` | MSEval: 10 rubric-scored web projects × 10 collaboration topologies (feature squad, layer specialists, pipeline, PM oversight, swarming, etc.). For identical tasks and models, topology moved scores by more than 30 points and doubled wall-clock time. Structured pipelines did best. | It establishes *organizational topology* as a first-class variable on the speed-cost-quality frontier. A021's variable is narrower and different: one retained context versus serial fresh contexts, both single-agent. | comparison | search-index + README |
| `geng2026caid` | CAID combines centralized delegation, asynchronous execution and isolated workspaces (git worktree/merge). It beat single-agent baselines by 26.7 points on PaperBench and 14.3 on Commit0. | Evidence in the *opposite* direction: isolation plus a manager plus test-verified merge beat a single agent. It reconciles with A021 if the gain comes from parallelism with structured integration, which A021's independent arm lacks. Cite it to bound A021's claim. | comparison / limitations | search-index |
| `destefanis2026coordinate` | Coordination in multi-agent coding modeled as temporal networks (1,902 runs). Messaging grows roughly quadratically with team size. Shared files cut output tokens by about 42% at eight agents. A named coordinator did not help. | Shows that shared artifacts can carry coordination. A021's independent arm relies *only* on shared artifacts (the branch). | comparison | search-index |
| `cemri2025why` | MAST: 14 multi-agent failure modes in 3 categories (specification, inter-agent misalignment, task verification), drawn from more than 1,600 traces. NeurIPS 2025 D&B. | Taxonomy for coding *why* independent sessions diverge. "Inter-agent misalignment" maps onto A021's independently resolved shared invariants. | comparison / method | repo citation + search-index |
| `hong2024metagpt` | SOPs encoded into role-based multi-agent prompts reduce the "logic inconsistencies due to cascading hallucinations" of naive chaining. | Multi-*role* decomposition of *one* task. A021 is a single-agent partition of *several* tasks. | comparison | repo citation |
| `qian2024chatdev` | ChatDev chat-chain multi-agent development across design, coding and testing phases. | Same as above. | comparison | ACL abstract |
| `qian2024experiential` | Notes that agents "frequently perform a variety of tasks independently, without benefiting from past experiences, which leads to repeated mistakes and inefficient attempts", and proposes experience reuse across tasks. | Names the cross-task cost that A021 measures (repeated reads), but fixes it with *distilled experience*, not retained context. | comparison | ACL abstract |
| `wang2023plan` | Plan-and-Solve: plan first, then execute subtasks. | Background for A021's "design analysis first" step in the grouped arm. This is a confound: the grouped arm differs in both context retention and the up-front cross-item design. | method / limitations | ACL abstract |
| `hadfield2025multiagent` (grey) | Anthropic: multi-agent systems use about 15× the tokens of chat. Domains "that require all agents to share the same context or involve many dependencies between agents are not a good fit … most coding tasks involve fewer truly parallelizable tasks". | Practitioner statement of A021's hypothesis, without a controlled SE comparison. | motivation | full text |
| `yan2025dontbuild` (grey) | Cognition: "share context" and "actions carry implicit decisions, and conflicting decisions carry bad results". Sub-agents that cannot see each other produce inconsistent work. | The most direct statement of A021's mechanism ("independently resolved shared invariants"), as a practitioner claim without an experiment. A021 can position itself as an empirical test of it. | motivation | search-index (fetch blocked) |

## 3. Grouping several tasks into one inference/context (non-agentic analogues)

| Key | What it established | How A021 differs | Role | Verification |
|---|---|---|---|---|
| `cheng2023batch` | **Batch prompting**: putting several samples in one prompt cut token and time cost almost inversely with batch size (up to 5× with six samples) at equal or better accuracy. Batch size and task complexity affect quality. | The single-call analogue of A021's cost result. A021 extends the idea to multi-step agent sessions over *related* repository work, where the shared content is architectural state, not a few-shot prefix. | comparison | ACL abstract |
| `son2024multitask` | **Multi-Task Inference**: handling 2-3 sub-tasks in one call cut inference time by 1.46× and, "contrary to the expectation that LLMs would perform better when tasks are divided", *improved* GPT-4 by up to 12.4%. | Prior evidence that grouping can help quality, not only cost, for small NLP tasks. A021 is the agentic-SE, repository-scale case. | comparison | ACL abstract |

## 4. Context windows, context engineering, agent memory

| Key | What it established | How A021 differs | Role | Verification |
|---|---|---|---|---|
| `liu2024lost` | Performance depends on where relevant information sits in a long context (U-shaped curve), even in long-context models. | Threat to the grouped arm: retained context could bury early-item details. This may explain independent sessions' better *local* acceptance. | motivation / limitations | ACL abstract |
| `laban2025lost` | LLMs degrade when a task's information arrives over multiple turns ("lost in conversation"). | Same threat for grouped sessions. | limitations | search-index |
| `mohammadi2026coherence` | "Coherence debt": repository-scale edits need coupled facts (tests, imports, config, migration rules). Whether a fact is *available* decides the outcome, while its distance in context does not. A missing fact produces *wrong* work, such as fabricated files or guessed values, rather than absent work. | A mechanism-level account that fits A021's interpretation: fresh sessions must rebuild the coupled-fact set and may guess shared invariants differently. A021 supplies a five-item, real-repository observation of that effect *across items*. | comparison | search-index |
| `rajasekaran2025context` (grey) | Anthropic on "context rot" (recall drops as tokens grow), compaction, and sub-agents with clean context windows that return 1-2k-token summaries. | The vendor guidance recommends *both* retained-context compaction *and* clean sub-agent contexts. A021 tests one boundary choice empirically. Disclose that the agent under study is from the same vendor. | motivation | full text |
| `lindenbauer2025complexity` | On SWE-bench Verified with SWE-agent, simple observation masking matched LLM summarization's solve rate at about half the raw-agent cost. | Context *management inside* a session is itself a strong cost lever. A021 must report whether compaction happened in either arm. | comparison / limitations | search-index |
| `packer2023memgpt` | OS-style hierarchical memory (paging between main context and external storage) gives LLMs a larger effective context. | Background on memory alternatives to retained context. | motivation | search-index |
| `zhang2023repocoder` | Iterative retrieval of repository context improves repository-level completion. | Background: retrieval is the standard way fresh sessions rebuild shared state. A021's "repeated reads" are this rebuilding cost. | motivation | ACL abstract |
| `song2026crosscontext` | Reviewing in a *fresh* session beat same-session self-review on injected-error detection (F1 28.6 vs 24.6), which the author attributes to context separation itself. | Evidence that fresh contexts *help* some activities (review/verification). This matches A021's finding that independent sessions did better on local acceptance and tests. Use it to argue for a hybrid (grouped implementation, fresh-context verification). | limitations / comparison | search-index |

## 5. Cost and efficiency of coding agents

| Key | What it established | How A021 differs | Role | Verification |
|---|---|---|---|---|
| `kapoor2025agents` | Agent evaluations must be cost-controlled. Simple baselines can match complex agents at much lower cost. Accuracy and cost should be optimized jointly (TMLR 2025). | A021 follows this by reporting platform cost next to quality. Cite it as the methodological reason for the cost outcome. | method | search-index |
| `salim2026tokenomics` | Token breakdown of an SE multi-agent system: about 54% input, 24% output, 22% reasoning. Iterative code review took about 59% of tokens, read as a "communication tax". | Supports the claim that input-side context (re)construction dominates cost. | comparison | search-index |
| `bai2026tokens` | On SWE-bench Verified trajectories from eight frontier models, agentic tasks use about 1000× the tokens of code chat, input tokens drive cost, **runs of the same task differ by up to 30×**, and models cannot predict their own usage. | **Key limitation source.** With n=1 per arm, a 55-60% cost difference sits inside documented run-to-run variance for a single task. A021 must say so and should not imply the cost effect is precisely estimated. | limitations | search-index |

## 6. Empirical studies of agents in the wild

| Key | What it established | How A021 differs | Role | Verification |
|---|---|---|---|---|
| `li2025aiteammates` | AIDev: more than 456k agent-authored PRs (Codex, Devin, Copilot, Cursor, Claude Code) across 61k repos. | Field-scale context. In practice agent PRs are per-task, which motivates studying granularity. | motivation | search-index |
| `watanabe2025agentic` | 567 Claude Code PRs in 157 projects. 83.8% merged, 54.9% of merged ones unmodified. Human revisions focus on bug fixes, docs and project-specific standards (reported accepted at TOSEM). | "Adherence to project-specific standards" is a field-level symptom of the consistency problem A021 studies. | motivation | search-index |
| `zhu2026smells` | Analyses code and architecture smells in LLM- and agent-driven development. | Only title and authors verified; do not cite for specific findings until read. | motivation | search-index (title/authors only) |

## 7. Architecture erosion, design consistency, decomposition

| Key | What it established | How A021 differs | Role | Verification |
|---|---|---|---|---|
| `perry1992foundations` | Architecture as elements, form and rationale. Introduced the vocabulary of architectural *erosion* and *drift*. | A021's "architectural drift" across items should use this vocabulary. The search result did not confirm that the erosion/drift passage is in the abstract, so check the full text before quoting. | motivation | search-index |
| `desilva2012controlling` | Survey of how to minimize, prevent and repair erosion. Argues no single strategy is enough. | A021's grouped design analysis works like a lightweight "architecture design enforcement" step. | motivation | search-index (DOI omitted) |
| `li2022understanding` | Systematic mapping of architecture erosion: divergence between intended and implemented architecture and its effects. | Gives an operational definition A021 can map its blind-evaluator rubric onto. | motivation | search-index (DOI omitted) |
| `parnas1972criteria` | Decomposition should follow design decisions (information hiding), not processing steps. | Theoretical anchor: A021's high-affinity cohort shares *design decisions*. Splitting work that shares a hidden decision across contexts violates the Parnas criterion at the *work* level. | motivation | search-index |
| `herbsleb1999splitting` | Conway's law revisited: splitting an organization makes integration the hardest part, largely because informal communication breaks down. | The human-team analogue. Independent sessions are like a team with no informal channel, and coordination happens only through the code. | motivation | search-index |

## 8. Evaluation validity (LLM judges, LLMs in SE research)

| Key | What it established | Role for A021 | Verification |
|---|---|---|---|
| `zheng2023judging` | GPT-4 judges agree with humans over 80% of the time, but have position, verbosity and self-enhancement biases. | If any A021 evaluator is an LLM, counterbalance arm order and disclose it. | search-index |
| `wang2024fair` | LLM-judge rankings can be flipped just by changing answer order (positional bias). Proposes calibration. | Same: randomize or swap arm presentation in blind comparisons. | ACL abstract |
| `he2025judgese` | Literature review and roadmap for LLM-as-a-judge in SE. | Positions the evaluator design. | search-index |
| `sallou2024breaking` | Threats in LLM-based SE research: closed models, data leakage, reproducibility. Includes guidelines. | Threats-to-validity section (closed model, version drift). | search-index |
| `wagner2025towards`, `baltes2025guidelines` | Guidelines for empirical SE studies involving LLMs: declare the LLM's role, report model versions and configuration, document the tool architecture, disclose prompts and logs, use human validation, use an open-model baseline, use suitable baselines and metrics, state limitations. | A021 reporting checklist. The "open LLM baseline" guideline is not met and should be stated as a limitation. | search-index |
| `trinkenreich2025train` | Position on how SE research should take up LLMs. | Optional framing. | search-index |

## 9. Empirical SE methodology

| Key | Role | Verification |
|---|---|---|
| `wohlin2012experimentation` | Experiment terminology (treatments, objects, validity classes). With n=1 run per arm, A021 is *not* a controlled experiment in Wohlin's inferential sense, so use "comparative case study with a replication". | search-index |
| `runeson2009guidelines` | Case-study design and reporting. Best fit for A021's single-repository, single-cohort design (holistic case with two embedded units, the arms). | search-index |
| `ralph2020empirical` | ACM SIGSOFT Empirical Standards. Pick the Case Study (and, if framed so, Experiment) standard and include the checklist. | search-index |
| `shull2008role`, `gomez2014understanding`, `baldassarre2014replication` | Replication terminology (see the final section). | search-index |

---

## Novelty assessment

**Has prior work already established that execution granularity (how work items are partitioned into agent sessions/contexts) is a variable in agentic SE?**
**Partly, yes.** The general claim, that how coupled software work is partitioned across agents/contexts materially changes cost and outcome, has already been shown and should not be claimed as new.

1. **CooperBench (`khatua2026cooperbench`)** compares one agent implementing two potentially conflicting features (Solo) with two agents implementing one each (Coop). Splitting cost about 30% in success on average. This is a direct, benchmark-scale demonstration that partitioning coupled feature work across contexts is harmful, and its failure analysis (wrong expectations about the other agent's plans, deviation from commitments) is a version of A021's "independently resolved shared invariants".
2. **MSEval (`ren2026coordination`)** explicitly makes "coordination mode" a first-class experimental variable for multi-agent coding and finds topology effects comparable to model effects.
3. **CAID (`geng2026caid`)** and **Destefanis & Aste (`destefanis2026coordinate`)** study isolated-workspace delegation and coordination cost. CAID finds isolation *with* structured merge can beat a single agent.
4. **Batch prompting (`cheng2023batch`)** and **Multi-Task Inference (`son2024multitask`)** showed, outside agentic SE, that grouping tasks in one context cuts cost and can keep or improve quality.
5. **Coherence debt (`mohammadi2026coherence`)** gives the mechanism: the outcome depends on whether coupled facts are *available*, and missing facts lead to fabricated or guessed values.
6. Practitioner sources (`yan2025dontbuild`, `hadfield2025multiagent`) state the "share context / implicit decisions conflict" principle outright.

**Has prior work measured cross-item architectural consistency across agent sessions?**
**No such study was found.** In the sources examined, outcomes are test-based success, merge conflicts, rubric scores of a single artifact, token cost, or regressions (EvoCode-Bench). None reports whether separate sessions converged on the *same* cross-cutting architectural decisions, such as a single shared store, admission rule and error envelope, judged blind. Two caveats apply. (a) Coverage of the 2026 arXiv flood is necessarily incomplete. (b) CooperBench's "conflict" signal partly covers the same ground.

**What remains novel (stated precisely):**
- **The serial, non-communicating independent arm.** Prior partition studies use *concurrent* agents that *message* each other (CooperBench, MSEval, CAID, Destefanis). A021's independent arm runs fresh sessions one after another on a shared branch, so each later session *can* read earlier work through the repository but has none of the earlier reasoning. This is how many practitioners actually use one-ticket-per-session agents, and the cited literature does not isolate it.
- **The outcome construct.** Cross-item architectural unification, meaning the number of distinct implementations of shared invariants, was assessed by evaluators (blind only in the re-execution; the original study's evaluations were not effectively blind) and re-coded post hoc by one auditor. A021 reports it alongside cost and repeated reads, and also reports the *opposite-direction* local-quality and test-count advantage of independent sessions. This trade-off is consistent with `song2026crosscontext` (fresh context helps verification) and `shen2026evocode` (carried state can propagate errors), but no study found here measures both sides on the same cohort.
- **The ecological setting.** A real repository, a five-item high-affinity feature cohort, a production agent (Claude Code), platform-billed cost, and an execution replication with a changed evaluator.

**What is not novel and should not be claimed:** that partitioning work across agents is an experimental variable; that splitting coupled work hurts consistency; that grouping reduces token cost. A defensible framing is: *"Prior work shows that splitting coupled features across concurrent, communicating agents lowers success (CooperBench) and that coordination topology matters (MSEval). We study a different and common partition, serial fresh sessions versus one retained-context session, and measure a construct those studies do not: whether shared architectural invariants are resolved once or repeatedly."* Because n=1 per arm and run-to-run token variance can reach 30× (`bai2026tokens`), the contribution is an exploratory, hypothesis-generating case study, not an effect-size estimate.

**Confounds to state openly (from the literature):** the grouped arm also had an up-front cross-item design analysis (compare `wang2023plan`, MetaGPT SOPs), so "retained context" and "explicit design step" are not separated. Also report compaction events in either arm (`lindenbauer2025complexity`, `rajasekaran2025context`).

---

## Replication terminology

**Recommended name for R2:** an **internal, operational replication** in the sense of Gómez, Juristo and Vegas (`gomez2014understanding`), specifically one that varies the **experimenters dimension** (a different evaluator) while keeping protocol, operationalizations and population (same baseline commit, same cohort, same protocol, same model family) and producing new executions.

Why:
- Gómez et al. classify replications as **literal**, **operational** and **conceptual** (abstract-level/search-index reading). A *literal* replication follows the original as exactly as possible *and is run by the same experimenters*. Only the sample differs. R2 changed the evaluator, so it is not literal.
- An *operational* replication varies one or more of four dimensions of the experimental configuration: protocol, operationalizations, populations, experimenters. R2 changes only experimenters (evaluator) and re-draws the stochastic "sample" (new agent executions). It is therefore an operational replication with minimal variation.
- It is a *conceptual* replication only if constructs or the procedure change, and R2 changed neither.
- It is **internal**, not external or independent, because the same research team ran it. Do not call R2 "independent". The README already forbids "independent-domain replication".
- In the older two-way scheme of Shull et al. (`shull2008role`), R2 falls under **exact** (as opposed to conceptual) replication. Baldassarre et al. (`baldassarre2014replication`) propose a consolidated SE taxonomy, and the search results attribute the "close" vs "differentiated" wording to Juristo and Vegas. If reviewers expect that wording, "close replication with a changed evaluator" is an acceptable gloss. Cite Gómez et al. as the primary taxonomy.

Suggested sentence: *"R2 is an internal operational replication [gomez2014understanding] that keeps the protocol, operationalizations and cohort and varies only the experimenter (evaluator) dimension, with fresh agent executions; in Shull et al.'s terms it is an exact rather than conceptual replication [shull2008role]."*

Two cautions:
1. If R2 used a different *model version* within the same family, that is arguably a change to the treatment's operationalization or instrumentation. Name it explicitly as a varied element; it still counts as operational.
2. These taxonomy definitions were verified only at search-index depth. Read Gómez et al. 2014 (IST 56(8):1033-1048, doi 10.1016/j.infsof.2014.04.004) to confirm the exact dimension names before quoting them.

---

## Sources considered and not included in references.bib

- **Ahmed, Devanbu, Treude, Pradel, "Can LLMs Replace Manual Annotation of Software Engineering Artifacts?" (MSR 2025).** Relevant to evaluator validity, but no DOI or arXiv identifier could be confirmed.
- **Conway 1968, "How do committees invent?" (Datamation).** No resolvable identifier verified. Use `herbsleb1999splitting` instead.
- **Mei et al., "A Survey of Context Engineering for LLMs" (arXiv 2507.13334).** The author list could not be verified.
- **Gao et al., "Single-agent or Multi-agent Systems? Why Not Both?" (arXiv 2505.18286).** Verified title and authors, but it is general (not SE-specific) and adds little beyond CooperBench and MSEval.
- **"Runtime-Structured Task Decomposition for Agentic Coding Systems" (arXiv 2605.15425).** Authors not verified. Relevant finding: static decomposition can raise retry cost by up to 80.5% over a monolithic run.
- **AgentVerse (arXiv 2308.10848) and SWE-bench Multimodal (arXiv 2410.03859).** Verified but dropped as peripheral.
- **DevBench, SWE-bench Verified (OpenAI blog), data-contamination papers (e.g., "SWE-bench illusion").** Not verified in this session and not needed, since A021 uses a private repository that is not a benchmark.
- **Practitioner blogs (augmentcode, mindstudio, willness.dev, codex.danielvaughan.com, etc.)** that appeared in searches. Not authoritative and not cited.
- **"Praxist: From Experimental Artifacts to Solution Lineages" (arXiv 2608.25955).** Appeared in a search. The name resembles the studied system, but it was not examined. Check whether it relates to Praxis (an anonymization or self-citation risk) before submission.
