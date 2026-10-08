# WI-0078 frozen protocol: execution granularity x shared analysis

Status: **authorized for preparation and execution after freeze gates pass; no arm has run**

This experiment complements EX-ROS-2026-A024. A024 tests whether a dynamic structured
handoff can recover useful context across fresh sessions. WI-0078 instead separates
two components that A021/R2 bundled together:

1. retaining one implementation context across the five-item cohort; and
2. receiving an explicit cross-item architecture analysis.

It does not replace A022's cross-domain/affinity test.

## Research questions

**RQ-G1 Context continuity.** Holding the availability of an explicit shared
architecture briefing constant, how does one continuous implementation session
compare with fresh per-item sessions on resource use, cross-item consistency and
local correctness?

**RQ-G2 Shared analysis.** Holding context continuity constant, how does a frozen
cross-item architecture briefing affect those outcomes?

**RQ-G3 Interaction.** Is the effect of retained context materially different when
the same explicit architecture briefing is available?

**RQ-G4 Run-to-run variance.** How much do resource and correctness outcomes vary
across repeated executions of the same treatment?

## Design

A 2 x 2 factorial, three scheduled replicates per cell (12 valid scheduled
replicates; 36 scheduled implementation sessions).

Factor C: **context continuity**
- C+: one implementation session executes all five items serially.
- C-: one fresh implementation session per item, five sessions serially.

Factor A: **shared architecture briefing**
- A+: every implementation context receives the same frozen, pre-written,
  treatment-neutral architecture briefing.
- A-: no architecture briefing is present or referenced.

Cells:

| Cell | Context | Briefing | Sessions per replicate |
|---|---|---|---:|
| CA | continuous | present | 1 |
| CN | continuous | absent | 1 |
| FA | fresh per item | present | 5 |
| FN | fresh per item | absent | 5 |

This is a new experiment. A021 and R2 are background evidence only and are not
counted as replicates.

## Blocking principle

The protocol, sanitized task pack, briefing-generation procedure, outcome
definitions, run order, prompts, evaluator rubric, invalid-run rules and
analysis formulas are frozen before the first implementation session.

After any implementation session starts, changes may only:
- record what happened;
- add an explicitly labelled protocol deviation;
- fix an instrumentation defect for future runs while preserving the affected
  run as deviated/invalid under the frozen rule.

No threshold or coding category changes after results are visible.

## Subject and sanitized harness

The product subject is the same five-item work-group feature used by A021/R2 so
that the treatment-bundle confound can be isolated on fixed work content.

Source product baseline:

    8b4ffa392e93b19bf39f6672a608954c934cb815

Before runs, build a **sanitized derived subject** from that baseline. The
sanitization must not change production source or product tests. It must:

1. remove/withhold A021/R2/A024/WI-0078 hypotheses, protocols, results,
   mappings and evaluation material from implementation workspaces;
2. remove treatment-revealing cohort/experiment labels from the task surface;
3. expose the five obligations under neutral task identifiers T01..T05 while
   preserving acceptance criteria semantically and byte-freezing their
   normalized text;
4. retain ordinary repository engineering guidance needed to implement the
   product, but remove study-specific guidance;
5. use neutral repository/workspace/branch names that do not reveal cell;
6. record a manifest proving that production `src/`, product `tests/` and
   relevant requirements match the frozen baseline before treatment-specific
   harness additions.

Implementation agents may not search the public repository, remote branch
history, A021/R2 artifacts or post-baseline implementations.

The sanitized subject hash and all task-text hashes are frozen before the run.

## Shared architecture briefing (A+ only)

A separate briefing author creates exactly one bounded briefing before any
implementation replicate runs.

The briefing author:
- receives only the sanitized baseline, neutral T01..T05 task pack and ordinary
  product requirements;
- receives no A021/R2 results, arm implementations, publication manuscript,
  architecture audit or treatment hypothesis;
- does not implement any arm;
- uses a frozen authoring prompt.

The briefing may contain:
- shared domain concepts/invariants visible from the task pack;
- dependencies/order among T01..T05;
- baseline abstractions likely relevant to more than one task;
- interfaces/persistence/validation surfaces that must remain coherent;
- common verification concerns.

It must not contain:
- code written for a task;
- copied solutions from A021/R2;
- treatment claims or expected outcomes;
- hidden chain-of-thought;
- post-run observations.

The resulting briefing is hashed and frozen.

CA receives the briefing once at session start.
Each FA item session receives the identical briefing at session start.
Briefing re-read/token overhead in FA is measured, not subtracted.

CN/FN workspaces contain no briefing artifact and prompts do not mention one.

## Implementation prompts

All implementation prompts share a frozen common prefix:
- implement the assigned neutral task(s);
- follow ordinary repository engineering rules;
- commit after each task;
- run the same required verification;
- do not inspect remote history or other runs;
- do not communicate with other replicates/cells;
- instrumentation rules.

Treatment-specific differences are limited to:
- whether one session receives T01..T05 or one task;
- whether the frozen briefing is supplied.

The C+ prompt does **not** require the agent to write an up-front analysis.
The A+ factor is the pre-written briefing, not an agent-generated design step.

This is intentional: it isolates retained context from explicit shared analysis.

## Executor controls

Freeze before run:
- provider and exact model identifier;
- runtime/version;
- reasoning/generation settings that are exposed;
- permission policy;
- toolchain/environment image;
- instrumentation version;
- prompt hashes;
- sanitized subject hash.

All 12 scheduled replicates use the same values.

If a provider update makes exact configuration parity impossible mid-experiment,
stop the remaining runs and record the block rather than silently mixing
configurations.

## Blocking/randomized run order

Three temporal blocks are used to reduce provider/runtime drift.

Randomization seed: `20261008`.

Frozen order:

| Block | 1st | 2nd | 3rd | 4th |
|---:|---|---|---|---|
| 1 | FN | CN | CA | FA |
| 2 | FA | FN | CA | CN |
| 3 | CN | FN | FA | CA |

Within each cell/replicate, T01..T05 execute in the same order.

Implementation agents receive only neutral run IDs, never CA/CN/FA/FN labels.

## Scheduled run IDs

See `WI-0078-run-matrix.csv`.

Exactly 12 primary replicate slots are scheduled. Outcome-driven reruns are
forbidden.

## Invalid-run and retry rule

A model-generated implementation failure, defect, context exhaustion, bad design,
test failure or merge problem is an outcome and is **not** rerun automatically.

An attempt may be replaced only when it is invalid for an external/orchestration
reason that prevents the intended treatment from being applied, for example:
- wrong starting tree;
- treatment leakage/cross-arm exposure;
- wrong model/configuration;
- infrastructure failure before meaningful implementation begins;
- missing treatment artifact in an A+ run.

The invalid attempt remains in the record.

At most one replacement slot per cell is permitted without new owner
authorization. Replacement uses a new run ID and is not chosen based on outcome.

## Outcomes

Keep four families separate. No composite quality score.

### O1 Resource efficiency

Per worker session and per replicate:
- platform-reported cost;
- active/summed worker session time;
- wall-clock span separately;
- input/output/cache-read/cache-write tokens;
- model requests;
- tool calls where available;
- file reads/searches;
- builds/test runs;
- instruction/governance reads;
- time to first production-code edit;
- compactions/context resets;
- retries/failures.

Orchestrator and evaluator cost/time are reported separately from implementation
worker cost.

### O2 Local correctness

For each replicate:
- every frozen acceptance criterion: pass/fail/indeterminate;
- acceptance pass rate over scorable criteria;
- confirmed defect count;
- final deterministic test results;
- validation failures.

Test count is reported but never treated as quality by itself.

### O3 Cross-item architectural consistency

Primary architecture dimensions, selected before runs because they reproduced as
behavioral differences in both A021 and R2:
- D2 member admission/repository rule;
- D5 error/JSON/command contract;
- D6 mutation/concurrency model.

Secondary dimensions:
- D1 persistence/store model;
- D3 lifecycle/classification;
- D4 checkpoint ownership/durability;
- D7 baseline-rule reuse;
- D8 incompatible duplicated abstractions.

Use the frozen category definitions from the WI-0075 codebook (or a byte-identical
copy with neutral subject names).

For each replicate/dimension, two independent human coders who do not know
treatment mapping code:
`unified`, `duplicated-compatible`, `divergent`, `missing`, or
`not-assessable`.

Report human-human raw agreement and Cohen's kappa where defined.
Do not force consensus. A third blind human may adjudicate disagreements.

Automated runtime probes may corroborate a dimension, but do not replace human
coding unless the protocol explicitly freezes a deterministic criterion before
runs.

### O4 Context reacquisition

Primary discovery proxy:
- total file reads + searches per replicate, worker sessions only.

Secondary:
- repeated reads of identical paths;
- governance/instruction reads;
- time to first edit;
- model requests before first edit.

If instrumentation cannot capture a measure consistently in all cells before
freeze, mark it unavailable and remove it from the primary set before any run.

## Primary estimands

This experiment is descriptive with n=3 per cell. It is not powered for a
population-level significance claim.

For each numeric outcome and each temporal block, compute treatment contrasts
from raw replicate values.

For lower-is-better resource metrics:

- context benefit without briefing:
  `FN - CN`
- context benefit with briefing:
  `FA - CA`
- briefing benefit under continuous context:
  `CN - CA`
- briefing benefit under fresh context:
  `FN - FA`
- interaction:
  `(FA - CA) - (FN - CN)`

Positive "benefit" means the named treatment reduced the lower-is-better metric.

For higher-is-better correctness measures, reverse subtraction so positive
benefit still means improvement.

Report:
- all three block-level contrasts;
- median contrast;
- range;
- cell raw values.

Do not report a p-value as primary evidence.

## Architecture analysis

For each dimension and cell, report the three replicate categories from each
human coder.

Primary D2/D5/D6 results are summarized as:
- number of replicates coded `divergent` or `missing`;
- number coded `unified` or `duplicated-compatible`;
- disagreements/not-assessable separately.

Do not convert the eight dimensions into a single score.

Factor conclusions are directional and dimension-specific.

## Interpretation rules

The experiment may support a **retained-context mechanism** when, across the
three blocks, C+ improves resource/context outcomes and/or reduces D2/D5/D6
divergence at both analysis levels without a systematic acceptance-correctness
loss.

It may support a **shared-analysis mechanism** when A+ improves the same outcomes
at both context levels.

It may show an interaction when the context contrast differs materially between
A+ and A- in the same direction across blocks.

No result from this one feature family establishes generality to low-affinity
work, other repositories, other model families, or other task sizes.

## Blinding

After all implementations are terminal:
1. export each replicate as a neutral patch/tree snapshot;
2. randomly assign evaluator aliases using a seed generated and committed by an
   orchestrator that does not code results;
3. keep treatment mapping separate;
4. give coders only sanitized baseline, neutral task criteria, codebook and
   anonymous implementation;
5. freeze/commit coding sheets before unblinding;
6. compute agreement and treatment summaries only afterward.

Coders must not receive session metrics or treatment prompts before coding.

## Contamination audit

For every worker session preserve enough event/tool metadata to determine whether
it:
- accessed prohibited branches/history;
- read study protocol/results;
- crossed into another replicate;
- received the wrong briefing treatment.

A run with confirmed exposure that can plausibly reveal treatment/outcome is
invalid under the replacement rule.

## Relationship to A024

EX-ROS-2026-A024 asks whether **dynamic durable handoffs** recover useful
continuous-session context.

WI-0078 asks whether **retained context** and a **static shared architecture
briefing** have separable effects.

Do not merge their observations into one arm or silently change A024.
A024 may be run before, after, or alongside WI-0078, but each retains its own
preregistered analysis.

## Completion gates

WI-0078 may begin implementation only after all are true:

- [ ] sanitized subject built and hashed;
- [ ] T01..T05 criteria hashes frozen;
- [ ] shared briefing generated by an independent briefing author and hashed;
- [ ] common/treatment prompts frozen and hashed;
- [ ] exact provider/model/runtime/settings frozen;
- [ ] run matrix frozen;
- [ ] telemetry extraction validated on a non-study dry run;
- [ ] architecture codebook frozen;
- [ ] human coder independence procedure ready;
- [ ] blinding/mapping procedure frozen;
- [ ] owner authorization record present;
- [ ] no implementation run has started.

## Results

No implementation run has started under this protocol.

## Amendment rule

Any pre-run amendment must:
- be committed before the first implementation session;
- explain why;
- increment a protocol revision;
- regenerate the freeze manifest.

After the first implementation session, treatment/outcome/analysis rules are
immutable. Deviations are appended, not repaired retroactively.
