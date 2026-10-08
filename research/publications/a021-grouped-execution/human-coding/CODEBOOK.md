# WI-0075 human architecture coding codebook

Status: **frozen before human coding**

Purpose: obtain an independent human application of the eight-dimension architecture rubric used by the A021 publication audit, without exposing treatment mapping, prior AI ratings, evaluator conclusions, resource results, or the manuscript.

## Coding unit

A coding unit is one anonymous implementation x one dimension.

There are four anonymous implementations:
- Study 1: `arm-X`, `arm-Y`
- Study 2: `arm-M`, `arm-N`

There are eight dimensions, so one coder returns 32 category judgments.

The labels are identifiers only. Do not attempt to infer which treatment produced an arm.

## Evidence the coder may use

Use only the human-coding packet:
- the anonymized baseline source tree;
- the four neutral arm patches;
- the frozen acceptance criteria/specification excerpt supplied in the packet;
- this codebook.

The coder must **not** receive or consult:
- `MAPPING.json`;
- experiment or treatment names;
- the publication manuscript;
- `architecture-findings.md` or `data/architecture-findings.json`;
- prior evaluator reports/findings;
- resource/cost/timing results;
- arm prompts, group-analysis documents, or session records;
- the public repository or branch history.

If exposure occurs, record it in the returned sheet and stop coding. Do not silently continue.

## Categories

Choose exactly one category per implementation/dimension.

### unified
One implementation or shared rule serves every command/item that needs the concern, and reachable equivalent inputs are handled under one invariant/behavior.

### duplicated-compatible
More than one implementation or representation exists, but no reachable equivalent input is shown to produce contradictory behavior or invariants. Differences are organizational, naming, layout, or wording only.

### divergent
Two or more paths derive or enforce the same concern differently, and code-level evidence shows different behavior or invariants for a reachable equivalent input. Organization alone is not enough.

### missing
The concern is required by the supplied task/specification but is absent where required.

### not-assessable
The supplied anonymous material is insufficient to make a defensible category judgment. Do not infer missing behavior from absence in a patch when the behavior may live in unchanged baseline code.

## General coding rules

1. Code each arm independently before comparing arms.
2. Cite file paths and symbols/line ranges from the anonymous packet for every judgment.
3. Prefer behavior/invariant evidence over file-count or module-count differences.
4. Do not treat more files, stores, parsers, helpers, or tests as worse by itself.
5. Do not use test quantity as an architecture category.
6. A shared abstraction can still be `divergent` if it applies different rules internally for equivalent inputs.
7. Separate correctness from consistency. A single consistently wrong rule can be `unified`.
8. Separate baseline reuse from feature-internal reuse. D7 covers baseline rules only.
9. When two dimensions overlap, code the behavior under the most specific dimension and do not double-count it.
10. If evidence supports two categories equally, use `not-assessable` and explain why.

## Dimensions

### D1 — Persistence and store model

Question: does the feature use one coherent persistence/state representation for group declarations/membership/checkpoints where a shared model is required?

Count semantic representations, not files. A separate file for a genuinely separate concept is not automatically duplication.

- `unified`: one canonical representation/model governs the concern.
- `duplicated-compatible`: multiple representations exist but remain behaviorally consistent.
- `divergent`: duplicated representations encode or permit incompatible state/invariants.
- `missing`: required durable state is absent.

### D2 — Member admission and repository rule

Question: do create/add/member-admission paths enforce the same eligibility and execution-repository invariant for equivalent members?

Evaluate the shared requirement that a group identifies where implementation occurs and that mixed-repository work must be explicitly cross-repository or split.

Do not assume a rule is correct merely because it is shared. Category here measures consistency; note correctness separately in rationale.

### D3 — Lifecycle and member classification

Question: do commands that present or checkpoint group progress classify equivalent member lifecycle states consistently?

Compare handling of active, ready, complete, blocked, abandoned, external/unknown states when observable.

### D4 — Checkpoint durability and ownership validation

Question: is one coherent checkpoint integrity rule used for ownership, member references, required fields, durability, and revalidation?

A stronger rule in one path and weaker/missing validation in another is `divergent`; absence everywhere when required is `missing`.

### D5 — Error, JSON, and command contract

Question: do the related commands expose a coherent external command contract for equivalent failure classes?

Consider:
- error structure/codes;
- JSON envelope shape;
- exit-code conventions;
- not-found/invalid-input behavior.

Cosmetic wording differences alone are `duplicated-compatible`, not `divergent`.

### D6 — Mutation and concurrency model

Question: do feature mutations share a coherent read/decide/write and locking/atomicity model?

Look for:
- serialization/locking boundaries;
- unlocked read-modify-write;
- atomic replacement without decision locking;
- materially different concurrency guarantees among related mutations.

Do not infer runtime lost updates unless code semantics make the race concrete; record uncertainty.

### D7 — Reuse of baseline rules and abstractions

Question: when baseline code already defines a relevant rule, do feature paths reuse that rule consistently instead of re-deriving it?

Examples may include existing repository-location, declaration parsing, or lifecycle/status rules present in the supplied baseline.

Do **not** score helpers created inside the new feature here; those belong under D5/D6 or the dimension they implement.

- `unified`: relevant baseline rules are consistently reused.
- `duplicated-compatible`: baseline rule is re-expressed but remains behaviorally equivalent.
- `divergent`: re-derived logic differs materially from the baseline rule.
- `missing`: required baseline rule is bypassed without replacement where needed.

### D8 — Duplicated incompatible abstractions inside the feature

Question: does the feature introduce two or more abstractions intended to represent the same concept that are behaviorally incompatible?

This dimension is deliberately narrow:
- parallel types/modules are not divergence by themselves;
- nested or stricter grammars are not automatically incompatible if used for different domains;
- count only duplicated same-concept abstractions with a concrete incompatible invariant/behavior.

## Rationale field

For each row, write a short factual rationale:
- what rule/model you observed;
- the anonymous path/symbol evidence;
- why that evidence fits the chosen category;
- any correctness concern that is separate from consistency.

Do not state which arm is "better" or guess the treatment.

## Confidence

Choose:
- high: direct and complete code evidence;
- medium: evidence is direct but a relevant path is indirect/ambiguous;
- low: category is defensible but important runtime/baseline behavior is not observable.

Confidence does not change the category.

## Frozen adjudication rule

The human coding is not overwritten to match the existing AI audit.

After the human sheet is committed/frozen:
1. compute AI-human raw agreement and Cohen's kappa where defined;
2. publish disagreements by dimension/category;
3. a manuscript architecture claim is called **independently corroborated** only when the human category agrees with the existing audit for the relevant implementation, or when a second independent human coder resolves the disagreement while still blind to treatment;
4. an investigator who knows the arm mapping may document a disagreement but may not serve as the blind adjudicator;
5. unresolved disagreements remain unresolved and are excluded from any claim that requires agreement.

This rule is frozen before human coding.
