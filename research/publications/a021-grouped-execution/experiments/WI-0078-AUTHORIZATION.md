# WI-0078 owner authorization

## Authorization

On 2026-10-08, the repository owner explicitly instructed the assistant to
proceed after the assistant recommended:

1. prepare WI-0075 independent human coding; and
2. immediately design and authorize WI-0078.

That instruction is recorded here as owner authorization for the **frozen
WI-0078 experiment scope** in `WI-0078-PROTOCOL.md`.

The authorization covers:
- preparation of the sanitized subject/harness;
- creation of the shared briefing under the frozen procedure;
- the 12 scheduled primary replicates in the 2 x 2 design;
- the resulting 36 scheduled implementation sessions;
- normal orchestration, telemetry capture and blinded evaluation required by
  the protocol;
- at most one externally-invalid replacement attempt per cell under the
  protocol's predeclared invalid-run rule.

No implementation arm has been run by this authorization commit.

## What is not authorized

This is not open-ended spending or permission to chase a desired result.

Fresh owner authorization is required for:
- more than three scheduled primary replicates per cell;
- more than one invalid-run replacement per cell;
- adding a fifth treatment cell;
- changing the subject cohort after runs start;
- switching provider/model/configuration mid-study and treating the mixed runs
  as one confirmatory experiment;
- outcome-driven reruns;
- changing thresholds, coding categories or primary outcomes after results are
  visible;
- expanding WI-0078 into A022 or another repository/domain experiment.

## Cost handling

No dollar ceiling was specified by the owner, so this record does not invent
one. The orchestrator must:
- record platform cost separately for worker, orchestration and evaluation
  sessions;
- preserve failed attempts;
- report accumulated cost after each temporal block;
- stop for new authorization if completing the frozen design would require a
  scope expansion or retry pattern outside the rule above.

Historical A021/R2 costs suggest a low-hundreds-of-dollars worker-cost scale,
but that is a planning observation, not a guaranteed budget or forecast.

## Scientific constraints

Authorization does not override the validity gates. The experiment must not
start until the protocol's completion gates for sanitization, model/config
freeze, briefing, prompts, instrumentation, codebook and blinding are satisfied.

If those gates cannot be met, WI-0078 remains authorized but blocked rather
than being weakened to force a run.
