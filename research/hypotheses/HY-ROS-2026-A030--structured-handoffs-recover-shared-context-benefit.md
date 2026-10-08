---
id: HY-ROS-2026-A030
title: Structured durable handoffs recover the shared-context benefit of continuous Claude execution
research_area: repository-operating-system
status: active
confidence: very-low
created: 2026-10-07
author_agent: openai/chatgpt
supporting_evidence: []
contradicting_evidence: [EV-ROS-2026-A075]
related_theories: []
related_documents:
  - HY-ROS-2026-A028
  - HY-ROS-2026-A029
  - EV-ROS-2026-A064
  - EV-ROS-2026-A070
  - EX-ROS-2026-A021
  - EX-ROS-2026-A024
supersedes: []
superseded_by: []
tags: [claude, agents, context, handoff, continuity, experiment, planning]
provenance:
  contributions:
    EXE-20261007T185521101Z-d60238bc:
      operations: [origin-unrecorded]
      at: 2026-10-07T19:02:04.245Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Added by a direct push to main (cd97bdde..94258e4f, 2026-10-07) with no Praxis execution; its creation was never recorded (DF-ROS-2026-A055)"
    EXE-20261007T162653169Z-f15a70b7:
      operations: [modified]
      at: 2026-10-07T18:47:26.586Z
      last: 2026-10-07T22:37:00.463Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Recorded the first test, EX-ROS-2026-A024: blocked, then completed; not supported (EV-ROS-2026-A075)"
      evidence: [EV-ROS-2026-A074, EV-ROS-2026-A075]
---

# Hypothesis

## Statement

For serial high-affinity software work, fresh Claude agents that receive a
structured durable handoff from the preceding agent will recover a substantial
part of the architectural-coherence and context-reuse benefit observed when one
Claude session retains the entire reasoning context.

The claim is narrower than "handoffs are as good as memory." It predicts that
an explicit external representation of decisions, invariants, reusable
abstractions, changed surfaces, verification state, and unresolved risks can
carry useful cross-item context that is otherwise lost between fresh sessions.

## Mechanism

A continuous Claude session can reuse two kinds of state:

1. repository-visible state, such as code, tests, work records and commits;
2. session-local state, such as cross-item decisions, rejected alternatives,
   discovered invariants, mental indexes of relevant files, and unresolved
   risks.

Independent agents already inherit the first category through Git. They do not
reliably inherit the second. A structured handoff makes a deliberately bounded
subset of that second category durable and machine-readable without preserving
a full conversation transcript.

If the A021/R2 advantage is caused primarily by reusable engineering context,
a structured handoff should move fresh-agent execution toward continuous-agent
execution. If the advantage depends on latent session state that cannot be
captured economically, the handoff arm should remain close to the code-only
fresh-agent arm.

## Predictions

Under the same baseline, work cohort, model/runtime/configuration, ordering and
verification rules:

1. the structured-handoff arm will score higher than the code-only fresh-agent
   arm on blinded architectural coherence;
2. the structured-handoff arm will perform fewer repeated discovery operations
   than the code-only arm;
3. acceptance-criterion correctness will be non-inferior to the code-only arm;
4. the continuous-session arm may still use fewer raw tokens, requests and
   session startups because structured handoff does not eliminate fresh-session
   setup cost;
5. if the handoff captures the mechanism well, the structured-handoff arm will
   recover at least half of the observed continuous-versus-code-only advantage
   on both architectural coherence and repeated-discovery measures.

## Evidence that would support it

The primary experiment is EX-ROS-2026-A024. Support requires a valid calibration
contrast in which continuous execution is better than code-only fresh execution
on at least one preregistered mechanism measure, followed by a structured-handoff
result that moves materially toward the continuous arm without sacrificing
acceptance correctness.

Because A024 replays one known cohort, even a positive result remains
mechanistic evidence at low confidence until replicated on a different work
family.

## Evidence that would contradict it

The hypothesis is weakened if, under valid runs:

- the structured-handoff arm is indistinguishable from or worse than the
  code-only fresh-agent arm on architectural coherence and repeated discovery;
- any apparent gain is explained by relaxed acceptance quality;
- handoff creation/consumption costs erase the recovered benefit; or
- continuous execution remains materially better despite a complete,
  schema-valid handoff.

If continuous execution does not reproduce an advantage over code-only fresh
execution, A024 has no mechanism effect to recover and is inconclusive for this
hypothesis rather than supportive.

## Tests performed

EX-ROS-2026-A024 (2026-10-07), executed to valid terminal results after
owner amendment A1 (EV-ROS-2026-A074 records the launcher blocker).

## Results

Not supported (EV-ROS-2026-A075).

- Discovery, the only applicable mechanism measure: A 112, B 263, C 276.
  Recovery is -13/151.
- Architecture: A 5, B 10, C 8. There is no continuous-context advantage to
  recover.
- Acceptance is 36/36 in every arm.

## Falsification attempts

EX-ROS-2026-A024 deliberately includes a code-only fresh-agent arm so that
ordinary Git-visible state is separated from explicit externalized reasoning
state.

## Current assessment

Active, very low confidence. The first test, EX-ROS-2026-A024, contradicts
the hypothesis on its applicable measure: the structured handoff did not
reduce discovery relative to code-only resets. The architectural part could
not be tested, because continuous context showed no advantage to recover.

One replayed cohort with one execution per arm is weak evidence. A negative
result should prompt a check of whether the handoff schema omitted durable
context (preregistered replication note) before the idea is abandoned.

## Next experiment

Run EX-ROS-2026-A024. If supported, replicate on a different high-affinity work
family before changing the default Praxis execution strategy.
