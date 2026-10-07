---
id: EX-ROS-2026-A024
title: "Claude context continuity: continuous session versus code-only resets versus structured handoffs"
research_area: repository-operating-system
status: completed
created: 2026-10-07
author_agent: openai/chatgpt
tests_hypotheses:
  - HY-ROS-2026-A030
  - HY-ROS-2026-A028
inputs:
  - EV-ROS-2026-A064
  - EV-ROS-2026-A070
outputs: [EV-ROS-2026-A074, EV-ROS-2026-A075]
related_theories: []
related_documents:
  - EX-ROS-2026-A021
  - HY-ROS-2026-A028
  - HY-ROS-2026-A029
  - HY-ROS-2026-A030
  - research/experiments/EX-ROS-2026-A024-harness/handoff.schema.json
  - prompts/CLAUDE-EX-ROS-2026-A024.md
tags: [claude, agents, context, continuity, handoff, mechanism, experiment]
derived_from: [EX-ROS-2026-A021, EV-ROS-2026-A064, EV-ROS-2026-A070]
provenance:
  contributions:
    EXE-20261007T185521101Z-d60238bc:
      operations: [origin-unrecorded]
      at: 2026-10-07T19:02:03.632Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Added by a direct push to main (cd97bdde..94258e4f, 2026-10-07) with no Praxis execution; its creation was never recorded (DF-ROS-2026-A055)"
    EXE-20261007T162653169Z-f15a70b7:
      operations: [modified]
      at: 2026-10-07T18:47:25.856Z
      last: 2026-10-07T22:36:59.831Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Executed the frozen A024 protocol; recorded the blocker, amendment A1, results, analysis and observed threats without editing the preregistered text"
      evidence: [EV-ROS-2026-A074, EV-ROS-2026-A075]
---

# Experiment

## Research question

How much of the benefit of one continuous Claude reasoning context for
high-affinity software work can be recovered by externalizing cross-item context
into a structured durable handoff consumed by fresh Claude agents?

This experiment isolates context continuity. It is not another general test of
whether grouping is useful.

## Hypotheses tested

Primary: HY-ROS-2026-A030.

Secondary calibration: HY-ROS-2026-A028 should reproduce directionally on the
same known high-affinity cohort.

## Prior evidence and reason for this design

EX-ROS-2026-A021 and its R2 execution both found that one continuous Claude
context produced a more coherent cross-item architecture and materially lower
resource use than serial fresh-agent execution on PRAXIS-GROUP-01 through
PRAXIS-GROUP-05. The fresh-agent arm was stronger on some local acceptance
details.

Those runs cannot identify whether the useful mechanism is irreducible latent
session state or information that Praxis could make durable. A024 adds a third
arm that preserves fresh-agent independence while supplying a bounded,
machine-readable handoff.

The cohort is intentionally replayed rather than presented as independent-domain
replication. Reusing it controls work content and makes A024 a mechanism study.

## Frozen work subject

Repository: kemiller2002/praxis

Product baseline before arm-specific implementation:

    8b4ffa392e93b19bf39f6672a608954c934cb815

Work cohort, in this fixed order:

1. PRAXIS-GROUP-01
2. PRAXIS-GROUP-02
3. PRAXIS-GROUP-03
4. PRAXIS-GROUP-04
5. PRAXIS-GROUP-05

Every arm must begin from one identical sanitized A024 harness commit whose
parent is exactly the baseline above. The harness commit may add experiment
instrumentation and prompts but may not change production code, product tests,
work-item acceptance criteria, or implementation guidance.

## Independent variable

Context strategy has three levels.

### Arm A: continuous context

One fresh Claude Code session receives the entire five-item cohort and completes
the items serially in the frozen order. The same session remains active for all
five items.

### Arm B: code-only fresh context

Five fresh Claude Code sessions execute serially, one per item. Each session
starts from the previous session's committed branch head, so it sees repository
state, but it receives no prior-session transcript, summary, handoff, notes, or
derived context artifact.

### Arm C: structured durable handoff

Five fresh Claude Code sessions execute serially, one per item. Each session
starts from the previous session's committed branch head and, for items 02
through 05, receives exactly one schema-valid handoff document produced at the
end of the immediately preceding item.

The handoff uses
research/experiments/EX-ROS-2026-A024-harness/handoff.schema.json. It records
engineering state and decisions, not private chain-of-thought. No conversation
transcript is passed forward.

## Controlled variables

Before any arm runs, freeze and record:

- exact repository baseline and sanitized harness commit;
- work-item text and acceptance criteria hashes;
- item order;
- Claude provider, model identifier, runtime/version and all available
  generation/reasoning settings;
- environment image/toolchain versions;
- branch-creation procedure;
- permission policy;
- prompt hashes for all three arms;
- telemetry extractor version/hash;
- evaluator prompt/rubric hash;
- acceptance scoring procedure;
- randomization seed for blinded arm labels;
- retry/invalid-run rules.

All arms use the same frozen values except for the context-strategy treatment.

If the exact model/configuration cannot be established, do not call the run
confirmatory. Preserve the setup and mark the run blocked or exploratory.

## Contamination controls

Arm agents must not:

- fetch, read, search, inspect or check out current main after the sanitized
  harness commit;
- inspect A021 results, A021 implementation-arm branches, A021 evaluations,
  A024 protocol discussion, other A024 arms, or post-baseline implementations
  of the five work items;
- use GitHub search or remote history to discover later solutions;
- communicate directly with agents in another arm.

The orchestrator may read prior evidence while constructing the harness, but it
must not implement any arm. Arm prompts must contain only frozen task
instructions, instrumentation rules and treatment-specific context.

Any confirmed cross-arm or post-baseline implementation exposure invalidates
that run. Preserve it with the reason; never silently replace it.

## Structured handoff rules

Arm C's handoff is deliberately bounded. It may contain:

- item and commit identity;
- durable architecture decisions;
- discovered shared invariants;
- reusable abstractions and their paths;
- changed production/test paths;
- acceptance and test status;
- unresolved defects/risks;
- dependencies or constraints relevant to later cohort members;
- commands needed to reproduce verification.

It must not contain:

- full transcripts;
- hidden chain-of-thought;
- speculative implementation instructions not supported by committed state;
- copied future-item solutions;
- information from another experiment arm.

The next agent may read the repository, its own item, normal repository
governance, and the immediately preceding handoff. It may not read earlier
handoffs directly; durable facts that remain relevant must be carried forward
explicitly.

## Outcomes

Keep correctness, coherence and efficiency separate.

### 1. Acceptance correctness

For each of the five work items, score every frozen acceptance criterion as
pass/fail/indeterminate using deterministic tests where possible and blinded
evidence review otherwise.

Primary correctness measure:

    passed criteria / scorable criteria

Also report confirmed defect count and indeterminate criteria. Do not convert
indeterminate to pass.

### 2. Architectural coherence

A blinded evaluator scores each anonymized arm from 0, 1 or 2 on five frozen
dimensions:

1. shared persistence/state model;
2. shared admission/validation rules;
3. shared domain classification and invariants;
4. error/JSON/CLI behavior consistency;
5. shared mutation/checkpoint architecture and reuse.

0 = fragmented or contradictory
1 = mixed/partially unified
2 = one coherent reusable model

Composite range: 0-10.

The evaluator must cite concrete code/test evidence for every dimension and must
not know which arm is A, B or C.

### 3. Context-reacquisition and discovery

Capture per session and per arm:

- model requests;
- file reads;
- searches;
- reads of AGENTS.md and governance/planning material;
- builds/tests;
- repeated reads/searches when the harness can determine them;
- time to first production-code change;
- session startups;
- retries and failed attempts;
- compactions/context resets;
- peak context when available.

The preregistration must nominate one deterministic repeated-discovery aggregate
before execution. If a reliable repeated-operation calculation cannot be
implemented before the freeze, use file reads + searches as the primary
discovery proxy and state the limitation.

### 4. Resource use

Record, when available:

- platform-reported cost;
- input/output/cache-read/cache-write tokens;
- elapsed time;
- active session time;
- tool invocations;
- retries;
- handoff production and consumption overhead separately.

Missing provider telemetry is missing data, not zero.

## Primary analysis

A and B first form the calibration contrast. For a higher-is-better metric S:

    recovery(C) = (S_C - S_B) / (S_A - S_B)

For a lower-is-better metric L:

    recovery(C) = (L_B - L_C) / (L_B - L_A)

Compute a recovery ratio only when A is directionally better than B on that
metric. Otherwise mark that metric "no recoverable calibration effect."

Primary support for HY-ROS-2026-A030 requires all of:

1. A is better than B on architectural coherence or the frozen discovery
   measure, establishing at least one calibration effect;
2. C is better than B on both architectural coherence and the frozen discovery
   measure when those measures have a recoverable A-vs-B effect;
3. C recovers at least 0.50 of the A-vs-B advantage on each applicable primary
   mechanism measure;
4. C's acceptance-correctness rate is no more than 0.05 below B's rate;
5. no arm is invalidated by contamination, baseline mismatch, treatment
   leakage, or unrecorded executor/configuration differences.

Report raw values even when the support rule is not met. Ratios above 1 and
below 0 are not clipped.

Because there is one cohort and one execution per arm, this is a mechanism
demonstration/replication, not a population-level statistical estimate.

## Secondary analysis

Report:

- A/B, C/B and C/A cost and elapsed-time ratios;
- handoff authoring/reading overhead;
- per-item acceptance and defect patterns;
- whether C combines A-like architecture with B-like local verification;
- divergence points where an arm introduced a second parser/store/rule or
  reversed an earlier decision;
- sensitivity of conclusions to excluding orchestration failures.

Do not create a single combined "agent score."

## Acceptance criteria

A024 is execution-complete only when:

- the sanitized base commit is recorded and identical across arms;
- executor model/runtime/configuration is frozen and recorded;
- all prompts, schemas, scoring rules and stop rules are hashed before arm A,
  B or C starts;
- all attempted sessions and failures are retained;
- every arm has raw telemetry or an explicit missing-data record;
- acceptance scoring is complete;
- anonymized evaluation inputs and mapping commitment exist before qualitative
  evaluation;
- the blind evaluation is committed before unblinding;
- analysis applies the frozen formulas without post-hoc threshold changes;
- a durable evidence record and REP/journal update capture results, limitations,
  and the next experiment.

## Falsification criteria

HY-ROS-2026-A030 is not supported if the primary support rule fails.

Especially important falsifiers are:

- C remains at or below B despite a valid handoff;
- C improves architecture but loses more than 5 percentage points of acceptance
  correctness versus B;
- handoff overhead consumes the recovered discovery/resource benefit;
- A does not reproduce any mechanism advantage over B.

Do not change the 0.50 recovery or 0.05 correctness thresholds after seeing
results.

## Retry and failure policy

Every attempted session gets a unique run/session ID and remains in the record.

No silent retry is permitted. An orchestration-only failure may be retried once
with a new run ID if the original failure and reason are preserved. A
model-generated implementation failure is part of the outcome and is not
automatically rerun.

Permission prompts, stale checkouts, missing pushes, merge conflicts, tool
failures and context exhaustion are measured events.

## Blinding

After all arms are terminal, create immutable snapshots and randomly map them
to three neutral labels. The evaluator sees only those labels and a scrubbed
evaluation package.

Before evaluation, commit:

- the mapping hash/commitment;
- the exact evaluator prompt and rubric;
- the scrubbed inputs;
- deterministic acceptance/test outputs.

Unblind only after the evaluator result is committed.

## Threats to validity

Known in advance:

- the cohort was used in A021 and R2, so this is not independent-domain
  replication;
- prior observed failure modes influenced the mechanism question;
- one run per arm cannot estimate run-to-run variance;
- Claude-family evaluation may share model biases with the implementers;
- structured handoff quality is itself an intervention and may depend on its
  schema;
- fresh sessions still inherit code/commit state, so B is not memory-free; it
  is explicit-handoff-free.

These are limitations, not reasons to alter the frozen design after execution.

## Procedure

1. Build and validate the A024 harness without running an implementation arm.
2. Create a sanitized harness commit whose parent is the frozen baseline.
3. Freeze executor/configuration, prompts, hashes, scoring and blinding seed.
4. Validate that the three arm branches have identical starting trees.
5. Run A, B and C under the isolation rules.
6. Preserve all run records and telemetry.
7. Run deterministic acceptance scoring.
8. Build the blinded evaluation package and mapping commitment.
9. Run the blinded architectural evaluation.
10. Commit the evaluation before unblinding.
11. Unblind, compute frozen analyses, write evidence and REP/journal updates.
12. Rebuild generated registries and run repository validation.

## Results

Executed on 2026-10-07 under the frozen manifest
(`research/experiments/EX-ROS-2026-A024-harness/manifest.json`, commit
`91745d9`) and owner amendment A1. All three arms reached a valid terminal
state. Results are in EV-ROS-2026-A075; the partial-execution and blocker
record is EV-ROS-2026-A074. The raw bundle is in
`research/experiments/EX-ROS-2026-A024-run/`.

| Measure | A continuous | B code-only | C handoff |
| --- | --- | --- | --- |
| Acceptance (blinded) | 36/36 | 36/36 | 36/36 |
| Architecture composite (blinded) | 5 | 10 | 8 |
| File reads + searches (primary discovery) | 112 | 263 | 276 |
| Platform cost (USD) | 8.27 | 15.30 | 17.43 |
| Output tokens | 102,006 | 163,797 | 204,253 |

## Analysis

The frozen `analysis.py` (`run/analysis/output.json`) was applied unchanged:

- architecture: A is not better than B, so there is no recoverable calibration effect;
- discovery: A is better than B; recovery(C) = (263 - 276) / (263 - 112) = -13/151 (about -0.086), below 0.50;
- support rule: calibration effect yes; C better than B no; recovery of at least 0.50 no; correctness within 0.05 yes; no arm invalidated yes.

Verdict: **HY-ROS-2026-A030 is not supported.** As a secondary calibration,
HY-ROS-2026-A028 reproduces on repeated work and resources (A/B cost 0.54,
elapsed 0.40) but not on architecture, where the A021/R2 direction reversed.
The comparison is a replay of the same cohort, not independent replication.

### Execution amendments (owner decision pending)

**Amendment A1 (accepted by the owner, 2026-10-07T20:48Z, "Try it now" in
response to BLOCKER.txt, adopting the recommended option 1):** a session the
platform starts on a commit other than the recorded head, which the frozen
start check stops before any repository change, is a launcher defect and not
an attempt; it does not consume the slot's single retry. Every later B/C
session is launched without `outcome_branch`, using the probe-validated
launcher. No treatment, threshold, margin, blinding rule or stop condition
changes. BLOCKER.txt lists the options that were offered. Any amendment is recorded here
with its date and author, below the preregistered text, and never edits it.

## Threats to validity

See the preregistered threats above. Add observed threats after execution
without deleting or rewriting the preregistered section.

Observed during execution (2026-10-07):

- Platform launcher pinning of `outcome_branch` (deviations D1-D3). It is
  harmless to validity so far: the start check stopped every affected session
  before any change. It does block the serial arms.
- `arm-2-02r` (B, item 02) received a fixed orchestrator resync note; C's
  retry declined the same note. B item 02 therefore carries a small extra
  stale-start turn in its usage that no other session has.
- Arm B receives each prior session's Praxis checkpoint summary and next
  action through committed `.ros/` state. That is normal repository state, but
  it is an informal handoff, so B is not handoff-free in the strict sense.
- Arm C's prompt alone mentions "remaining members of this work group",
  which the handoff schema's `nextItemContext` requires.
- The frozen arm-A prompt asked for cross-item architecture to be decided
  once, as the orchestration script required.

## Replication notes

A positive result should next be tested on a different high-affinity work
family. A negative result should inspect whether the schema omitted a class of
durable context before concluding that externalized handoff is generally
ineffective.

## Conclusion

A structured durable handoff did not recover the continuous-context
discovery advantage, and in this execution continuous context did not yield
the more coherent architecture. HY-ROS-2026-A030 is not supported, at low
confidence, on one replayed cohort with one execution per arm. The follow-on
proposals are in `research/experiments/EX-ROS-2026-A024-run/FOLLOW-ONS.txt`.
None of them was run automatically.

## Registry updates required

The canonical HY and EX artifacts are authoritative. Run the repository's
deterministic registry build/check after these files are present. Do not
hand-maintain generated registry entries.
