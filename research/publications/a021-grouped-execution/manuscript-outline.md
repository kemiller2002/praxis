# Manuscript outline

## Title

**Does Shared Agent Context Reduce Architectural Drift? A Blinded Replication Study of Grouped and Independent AI Software-Engineering Execution**

## Abstract structure

Do not write the final abstract until the evidence table and analysis are frozen.

1. **Problem:** Agentic software-engineering workflows often decompose related work into separate executions, repeatedly reacquiring context and potentially making incompatible local design decisions.
2. **Question:** For a tightly coupled cohort, does one shared execution reduce resource use and architectural drift without materially degrading local correctness?
3. **Method:** Compare grouped and independent execution from the same baseline and requirements; use isolated arms, frozen protocol, blind evaluation, Git/telemetry evidence, and a same-domain execution replication.
4. **Results:** Report R2 cost/time reductions, original and R2 cross-item architecture findings, and mixed local-quality findings.
5. **Limit:** Single repository/feature family, one run per arm in each execution, and documented orchestration deviations.
6. **Contribution:** Evidence that execution granularity is an experimentally meaningful design variable for agentic software engineering, plus a reproducible evaluation method and a falsifiable affinity hypothesis.

## 1. Introduction

### Motivation

Software work items are frequently planned separately even when they share:
- domain invariants;
- persistence models;
- validation rules;
- mutation pipelines;
- acceptance criteria with cross-item consequences.

Fresh agent sessions may repeatedly reconstruct the same context and optimize locally. A single grouped session may preserve shared reasoning, but could also suffer from context pressure or lose per-item rigor.

### Gap

Existing agent evaluations often focus on task completion or benchmark pass rates. A021 targets a different question: whether **execution granularity** affects cross-item architectural coherence and resource use when multiple real work items implement one cohesive feature.

### Contributions

Proposed contribution statement:

1. A frozen protocol for grouped-vs-independent execution of a real five-item, high-affinity software cohort.
2. Blind cross-arm evaluation that separates architecture, acceptance criteria, tests, and attribution.
3. Quantitative evidence on context acquisition and execution resources.
4. A same-baseline replication with an independent blind evaluation.
5. A falsifiable follow-up hypothesis that affinity moderates grouping value.

## 2. Background and related work

Required categories:
- AI/LLM coding agents and autonomous software-engineering agents;
- context management and long-horizon agent execution;
- task decomposition and multi-agent/single-agent orchestration;
- empirical software-engineering experimentation;
- architectural consistency / design drift across incremental change;
- replication and reproducibility in software-engineering research.

Rules:
- no citation enters the paper without manual verification;
- distinguish peer-reviewed research from vendor/blog claims;
- do not use repository-internal terms as if they are established academic concepts;
- explicitly state where A021 differs from benchmark-style issue resolution.

## 3. Study design

### 3.1 Research questions

**RQ1:** How does grouped execution affect resource use and repeated context acquisition for a high-affinity cohort?

**RQ2:** How does execution mode affect cross-item architectural consistency?

**RQ3:** How does execution mode affect localized acceptance-criterion fidelity and test depth?

**RQ4:** Does a second execution reproduce the direction of the original findings?

### 3.2 Subject system

Describe Praxis only to the extent necessary:
- F#/.NET software-engineering tooling;
- work items `PRAXIS-GROUP-01..05`;
- one feature family implementing `work group create|show|add|remove|checkpoint`;
- why the cohort is considered high affinity.

Avoid marketing language.

### 3.3 Baseline and cohort

Baseline:
`8b4ffa392e93b19bf39f6672a608954c934cb815`

Explain:
- same baseline;
- same cohort;
- same acceptance criteria;
- same repository instructions;
- isolated branches;
- no cross-arm reading;
- serial independent execution because planner collision analysis rated safe concurrency as one.

### 3.4 Treatments

**Grouped:** one implementation context reasons across all five items before mutation, then implements them sequentially while retaining shared context.

**Independent:** one fresh execution per item, with earlier committed repository state visible to later items.

Important: independent execution is not "no integration." It is repeated context acquisition across separate implementation sessions.

### 3.5 Blinding

Describe:
- neutral arm labels;
- evaluator did not implement either arm;
- mapping revealed only after durable evaluation;
- partial-blinding limitations, including potential code-style leakage;
- R2 mapping commitment/reveal limitation exactly as recorded.

### 3.6 Measures

Quantitative:
- platform cost;
- active/elapsed time;
- input/output/cache tokens;
- model requests;
- file reads/searches;
- governance reads;
- time to first edit;
- builds/tests;
- compactions;
- merge conflicts;
- test counts.

Qualitative:
- number of distinct persistence/store models;
- admission/join rules;
- member classifications;
- validation/checkpoint ownership rules;
- error/JSON envelope patterns;
- acceptance defects;
- design risks.

## 4. Analysis method

### 4.1 Resource analysis

Use descriptive comparisons only.

Report:
- absolute values;
- ratios;
- percent reductions where denominators are unambiguous;
- lower-bound markers where transcript capture is incomplete.

Do not compute inferential p-values from one run per arm.

### 4.2 Architectural analysis

Use the rubric in `analysis-plan.md`.

For each architectural dimension:
- quote/identify the rule being evaluated;
- list concrete implementation sites;
- count distinct incompatible models only when the distinction is observable;
- classify as unified, duplicated-compatible, divergent, or not assessable.

### 4.3 Acceptance-quality analysis

Keep defect counts separate from architectural findings.

R2:
- grouped: two confirmed acceptance defects;
- independent: two confirmed acceptance defects.

The correct interpretation is **mixed local quality**, not superiority by defect count.

### 4.4 Replication analysis

Treat R2 as an **execution replication**, not an independent-domain replication.

Ask whether direction agrees on:
- repeated context/resource use;
- cross-item architectural coherence;
- local-detail tradeoffs.

## 5. Results

### RQ1

Original A021:
- grouped: one session, $9.48 platform-reported cost, 61 min active session time;
- independent/control: seven sessions, $21.51, 112 min summed session time;
- grouped had fewer repeated governance reads, model requests, file reads and searches;
- no compaction in either arm.

R2:
- grouped: one implementing session, 55m53s, $10.7536464;
- independent/control: five implementing sessions plus one orchestrator, 2h23m35s, $26.0492768;
- grouped reduction: 58.72% cost and 61.08% elapsed time;
- grouped used roughly half the output tokens and less than half the cache-read volume.

State clearly which elapsed-time definitions differ between the original and R2.

### RQ2

Original:
- grouped produced one store, one member-join rule, one member classification, one error style/JSON envelope, one mutation pipeline;
- independent produced multiple stores, multiple join rules/grammars/classifications, and inconsistent rejection/JSON patterns.

R2:
- grouped again showed stronger cross-command repository-admission consistency and one locked mutation path/store/envelope;
- independent again showed stronger localized checkpoint integrity in some dimensions.

### RQ3

Original:
- independent arm was more faithful on several individual criteria and added more tests (37 vs 19).

R2:
- both arms had two confirmed acceptance defects;
- grouped stronger on admission consistency/transactional mutation;
- independent stronger on checkpoint ownership/validation and some detail handling.

### RQ4

The replication reproduced the broad qualitative pattern:
- grouped execution: lower repeated context/resource use and more unified cross-item design;
- independent execution: localized detail advantages remain possible;
- grouped execution did not sweep quality.

## 6. Threats to validity

### Construct

- "architectural consistency" requires a transparent rubric;
- test-count differences are not equivalent to test quality;
- platform cost is provider/runtime-specific;
- session elapsed time and active time differ.

### Internal

- one run per arm;
- control failed attempts/orchestration gaps in original;
- R2 stale-checkout deviations in control;
- code changes from earlier independent items are visible to later independent sessions;
- possible model/runtime drift;
- partial blinding.

### External

- one repository;
- same feature family in original and R2;
- high-affinity cohort;
- F#/.NET tooling context;
- no low-affinity comparison yet.

### Conclusion validity

- descriptive evidence only;
- no inferential statistical claim;
- replication is same-domain;
- do not infer universal causal superiority.

## 7. Discussion

Interpret the mechanism:

A plausible explanation is that grouped execution amortizes context acquisition **and** preserves shared decisions across related changes. The architectural result is therefore not merely a token-cost effect.

Competing explanation:
- repeated independent execution may fragment design because each session optimizes its local work item, but stale checkouts and orchestration deviations may magnify that effect.

Boundary condition:
- low-affinity work may gain little from shared context and may instead incur unnecessary context load.

## 8. Future falsification

Primary follow-up:
`EX-ROS-2026-A022`

2x2:
- high affinity / low affinity;
- grouped / independent;
- different repository/subsystem.

The key prediction is an interaction: grouping benefit should be materially larger for high-affinity than low-affinity work.

## 9. Reproducibility package

Include anonymized:
- baseline commit/tree snapshot;
- frozen prompts;
- acceptance criteria;
- arm patches;
- evaluator prompt;
- findings JSON;
- session-derived metrics stripped of identity;
- scripts that generate paper tables;
- checksums.

## 10. Conclusion

Use a narrow conclusion:
execution granularity appears to matter for high-affinity agentic software work, with replicated directional evidence for resource savings and architectural coherence, but current evidence is insufficient for a general causal claim.
