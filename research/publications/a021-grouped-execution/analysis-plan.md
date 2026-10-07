# Analysis plan

> **Status note (2026-10-07).** This plan was first committed after both A021 and R2 were unblinded, so it is not a preregistration. Its six-dimension rubric was later extended to eight dimensions (D7 baseline reuse, D8 duplicate definitions) in `architecture-findings.md`. Sensitivity analysis S1 was corrected to exclude only the stalled item-04 attempt 1 (`adjudication-log.md` AD-12). The manuscript reports these facts. The text below is kept as written.

## Principle

The sample is too small for conventional inferential statistics. The paper should use transparent descriptive comparisons, structured qualitative coding, and replication of direction rather than p-values.

## 1. Quantitative analysis

For each metric available in both arms:

- report grouped value;
- report independent value;
- compute independent/grouped ratio;
- compute grouped reduction relative to independent when meaningful:
  `(independent - grouped) / independent`;
- retain raw units;
- state whether the measure is complete, partial, or lower-bound.

Do not mix:
- active session time;
- wall-clock span including orchestration gaps;
- evaluator time;
- orchestration-session time.

### Primary quantitative outcomes

1. platform-reported implementation cost;
2. implementation elapsed/active time with definition explicitly stated;
3. output tokens;
4. cache-read/cache-write tokens;
5. repeated context acquisition;
6. model requests / file reads / searches where captured consistently.

### Secondary outcomes

- builds;
- test runs;
- merge conflicts;
- compactions;
- final passing-test count.

## 2. Architectural-consistency rubric

Evaluate the same dimensions in both arms.

### Dimensions

1. **Persistence model**
   - unified: one canonical store/model;
   - duplicated-compatible: more than one representation but consistent;
   - divergent: representations encode incompatible rules;
   - not assessable.

2. **Member admission / repository rule**
   - unified rule reused across create/add;
   - duplicated equivalent rules;
   - divergent rules;
   - not assessable.

3. **Member lifecycle/classification**
   - shared classification;
   - duplicated equivalent classification;
   - divergent classification;
   - not assessable.

4. **Checkpoint/ownership validation**
   - common ownership rule and revalidation;
   - partial;
   - divergent/missing;
   - not assessable.

5. **Error and command-contract shape**
   - common error/JSON envelope;
   - compatible duplicates;
   - divergent envelopes/exit behavior;
   - not assessable.

6. **Mutation/concurrency model**
   - one guarded read-decide-write path;
   - atomic write without decision lock;
   - multiple inconsistent mutation paths;
   - not assessable.

### Coding rule

A dimension may be called "divergent" only when concrete implementation evidence demonstrates distinct behavior or invariants, not merely different file organization.

Each coded finding must include:
- repository path(s);
- relevant function/type/rule;
- behavior or invariant;
- evaluator/source record;
- whether it is observed, evaluated, or inferred.

## 3. Acceptance-quality analysis

Keep the following separate:

- confirmed acceptance defects;
- design risks;
- test count;
- breadth/depth of tests;
- architecture findings;
- validation failures.

Do not construct a synthetic "quality score" unless a weighting scheme was defined before evaluation. No such score exists for A021.

## 4. Replication logic

R2 supports replication only when:
- the outcome is defined the same way;
- arm mapping is established after blind evaluation;
- evidence direction can be compared without changing the original criterion.

### Replicated directional outcomes

Candidate:
- lower grouped resource use;
- more unified grouped cross-item architecture;
- independent arm retains localized-detail advantages;
- no overall acceptance-defect-count sweep.

### Not replicated / not independently established

- generalization to other repositories;
- low-affinity work;
- larger group sizes;
- other providers/models;
- statistical effect size distribution.

## 5. Sensitivity analysis

The manuscript should explicitly re-evaluate conclusions under conservative exclusions:

### S1 Remove original control failed-attempt overhead
Ask whether the architecture conclusion still holds. It should, because the architecture finding is code-based rather than time-based.

### S2 Ignore elapsed wall-clock span with orchestration gaps
Use active/implementation session time only.

### S3 Treat R2 stale checkout as a confounder
Do not use R2 alone to argue that independent execution causes divergence. Use it as corroboration of the original direction with a documented threat.

### S4 Ignore test-count differences
Ask whether acceptance-defect findings alone change the conclusion. R2 is tied 2-2 by confirmed defect count.

### S5 Resource-only explanation
Ask whether lower cost alone could explain the architectural finding. It cannot directly establish mechanism; architecture must remain separately evidenced.

## 6. Tables/figures planned

1. Study design diagram: baseline -> grouped arm / independent arm -> blinded evaluator -> unblinding.
2. Resource comparison table for A021 and R2.
3. Architectural-dimension matrix for A021 and R2.
4. Acceptance-quality table separating defects, tests, and design risks.
5. Threats-to-validity table.
6. Replication summary: reproduced / mixed / not tested.

No chart should imply a statistical distribution from two executions.

## 7. Decision rule for submission

Proceed as a full paper only if:

- every reported number is regenerated from committed evidence;
- the qualitative rubric can be applied symmetrically to both arms;
- the anonymized bundle is complete;
- related work supports a clear novelty claim;
- the paper can state its limitations prominently without undermining the central contribution.

Otherwise submit a shorter early-results paper or continue the A022 falsification study first.
