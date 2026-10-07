# Claude orchestration script: EX-ROS-2026-A024

You are the experiment orchestrator for **EX-ROS-2026-A024**, not an
implementation-arm agent.

Repository:

    kemiller2002/praxis

Your job is to execute the context-continuity experiment faithfully to
completion, preserving every attempt and refusing to manufacture evidence.

Read first:

1. `CLAUDE.md`
2. `AGENTS.md`
3. `research/experiments/EX-ROS-2026-A024--context-continuity-and-externalized-handoff.md`
4. `research/hypotheses/HY-ROS-2026-A030--structured-handoffs-recover-shared-context-benefit.md`
5. `research/evidence/EV-ROS-2026-A064--grouping-experiment-results.md`
6. `research/evidence/EV-ROS-2026-A070--a021-r2-blind-evaluation-and-replication.md`
7. `docs/development-telemetry.md`
8. `research/experiments/EX-ROS-2026-A024-harness/handoff.schema.json`

Do not modify the preregistered thresholds or treatment definitions after any
implementation arm has started.

## Mission

Run three isolated implementations of the same frozen five-item cohort:

- **A, continuous:** one Claude session implements PRAXIS-GROUP-01 through 05.
- **B, code-only fresh:** five fresh Claude sessions, one per item, serially,
  with repository state but no carried handoff or transcript.
- **C, structured handoff:** five fresh Claude sessions, one per item, serially,
  with repository state plus exactly one schema-valid handoff from the
  immediately preceding item.

The frozen product baseline is:

    8b4ffa392e93b19bf39f6672a608954c934cb815

The item order is fixed:

    PRAXIS-GROUP-01
    PRAXIS-GROUP-02
    PRAXIS-GROUP-03
    PRAXIS-GROUP-04
    PRAXIS-GROUP-05

You may create specialized helper agents for harness construction, telemetry
validation, deterministic scoring and blinding. **Never allow an agent that has
seen one implementation arm to implement or inspect another arm.**

## Phase 0: establish valid research state

Before creating an arm:

1. Follow the repository work/provenance protocol for this orchestration effort.
2. Run the deterministic registry build/check so the new HY/EX artifacts are
   indexed from canonical front matter. Do not hand-edit generated registries.
3. Run repository validation and record any pre-existing failures separately.
4. Create
   `research/experiments/EX-ROS-2026-A024-harness/` support artifacts needed
   for execution. Reuse deterministic A021 telemetry machinery where valid,
   but copy/version it into A024 rather than depending on mutable later state.
5. Add a machine-readable frozen manifest containing:
   - baseline SHA;
   - cohort and order;
   - hashes of the five work-item definitions/acceptance criteria;
   - exact provider/model/runtime/version/configuration available for Claude;
   - environment/toolchain versions;
   - prompt hashes;
   - handoff-schema hash;
   - telemetry extractor hash;
   - evaluator rubric hash;
   - randomization seed;
   - stop/retry rules.
6. If the exact executor model/configuration cannot be recorded consistently
   for all arms, stop before execution and record the blocker. Do not label an
   unbound run confirmatory.

## Phase 1: build a sanitized common start

Create one A024 sanitized harness commit whose **parent is exactly**
`8b4ffa392e93b19bf39f6672a608954c934cb815`.

The sanitized commit may contain only instrumentation, frozen prompts,
schemas/manifests and experiment-local support files. It must not contain
product implementation from A021/R2/current main or commentary revealing which
architecture previously won.

Create all three arm branches from that exact sanitized commit and verify their
trees are identical before execution.

Suggested branch names:

    experiment/a024-continuous
    experiment/a024-code-only
    experiment/a024-handoff

Do not let arm agents fetch or inspect current main, A021 implementation
branches/evaluations/results, post-baseline commits containing the solution, or
other A024 arms.

## Phase 2: freeze prompts and scoring before implementation

Create explicit arm prompts and commit them before any arm starts.

Every arm prompt must:

- follow AGENTS.md and the Praxis work protocol;
- use the fixed item order;
- commit/checkpoint each item durably;
- run the same required tests/verification;
- capture telemetry immediately before terminal checkpoint/finish;
- work autonomously;
- never merge;
- never read another arm;
- never inspect post-baseline implementations.

Treatment differences must be only the context strategy.

### Arm A prompt

One fresh session receives all five item IDs. It must inspect all five before
changing production code, make cross-item architecture decisions once, and then
implement the items serially in one session.

Do not give it A021 results.

### Arm B prompts

Create one prompt per item. Start a genuinely fresh Claude session for each
item. Each prompt contains only:

- the current item;
- normal repository governance/work instructions;
- instrumentation instructions.

The new session starts on the branch head left by the preceding item. Do not
give it summaries, transcripts, notes, handoffs, or descriptions of preceding
agents' decisions.

### Arm C prompts

Create one prompt per item. Start a genuinely fresh Claude session for each
item.

For PRAXIS-GROUP-01, no handoff exists.

At the end of each item, the agent must write a handoff conforming exactly to:

    research/experiments/EX-ROS-2026-A024-harness/handoff.schema.json

Validate it mechanically before committing it. The handoff records engineering
state, not private reasoning or chain-of-thought.

For items 02-05, the new agent may read exactly the immediately preceding
handoff in addition to the same repository/governance information available to
Arm B. It may not read older handoffs directly.

Track the time/tokens/tool usage for handoff production and handoff consumption
separately when possible.

## Phase 3: execution

Run the arms with the exact same frozen Claude model/runtime/configuration.

Order of A/B/C execution may be randomized from the frozen seed, but within
each arm the five work items remain ordered 01 to 05.

For every session record:

- session/run ID;
- model/provider/runtime/configuration;
- start and end commit;
- start/end time;
- output/input/cache tokens when available;
- platform cost when available;
- model requests;
- file reads and searches;
- builds/tests;
- retries;
- permission blocks;
- merge conflicts;
- compactions/context resets;
- context size when available;
- failure/invalid reason when applicable.

Do not silently retry. Preserve failed attempts. An orchestration-only failure
may be retried once under the protocol with a new run ID; retain the original.

If you cannot create truly fresh isolated sessions for B or C, stop and record
that as a validity blocker. Do not simulate fresh agents inside one shared
conversation and call the result valid.

## Phase 4: deterministic scoring

Before any qualitative unblinding:

1. score every frozen acceptance criterion for all three arms;
2. run all applicable tests from each arm head;
3. compute the frozen discovery/resource metrics;
4. preserve raw outputs and scripts;
5. create immutable snapshots of the three arm heads.

No arm is allowed to repair itself after looking at another arm's result.

## Phase 5: blind architectural evaluation

Randomly map the three snapshots to neutral labels from the frozen seed.

Create and commit:

- a mapping commitment/hash;
- scrubbed evaluation copies;
- the frozen 0-2 x five-dimension architecture rubric from A024;
- the evaluator prompt;
- deterministic acceptance/test summaries.

The evaluator must not know the mapping and must cite concrete evidence for
every score.

Prefer a fresh evaluator session that has seen none of the orchestration or
implementation. If only Claude is available, use a separate fresh Claude
session and record the same-family limitation.

Commit the evaluation before unblinding.

## Phase 6: unblind and analyze

Only after the blind evaluation commit is durable:

1. unblind A/B/C;
2. compute the preregistered recovery formulas exactly as written;
3. apply the 0.50 recovery and 0.05 correctness thresholds unchanged;
4. report raw correctness, architecture, discovery, cost, token and time
   measures separately;
5. explicitly classify invalid/missing measurements;
6. compare observed A-vs-B direction with A021/R2 without treating the replay
   as independent-domain replication.

Do not create a single combined agent score.

## Phase 7: durable research outputs

Create/update the normal Praxis research artifacts required by AGENTS.md and
the REP specification, including:

- evidence record for A024;
- journal/REP as required;
- HY-ROS-2026-A030 assessment;
- EX-ROS-2026-A024 results/status;
- any justified decision record only if the evidence actually warrants a
  production decision;
- generated registries via the canonical registry command;
- reproducible raw run bundle and analysis scripts.

Do not promote a default Praxis execution strategy from one replayed cohort.

## Phase 8: prepare the next experiments, but do not run them automatically

After A024 is closed, write proposed follow-ons based on its actual result:

- independent fresh verifier after grouped implementation;
- context-saturation/work-group-size experiment;
- single-agent vs coordinator/subagent topology;
- forced interruption/recovery experiment;
- guidance ablation for reuse inventory + per-criterion verification;
- model/reasoning sensitivity.

Do not preregister thresholds for these follow-ons based on desired outcomes.
Use A024 only to decide which uncertainty has the highest information value.

## Stop conditions

Stop rather than fabricate or contaminate the study if:

- the sanitized start cannot be reproduced;
- arms do not share an identical start tree;
- model/configuration differs across arms and cannot be corrected before any
  implementation begins;
- fresh-session isolation cannot be established;
- an arm sees another arm or a post-baseline implementation;
- the handoff schema is changed after Arm C starts;
- scoring or blinding cannot be frozen before outputs are examined.

When stopping, preserve the partial run, record the exact blocker, rebuild
registries if applicable, validate the repository, and leave an executable
handoff for resumption.

## Completion definition

You are done only when either:

A. A024 has valid terminal results, blinded evaluation, unblinded frozen
analysis, durable evidence/REP updates and repository validation; or

B. a protocol stop condition is hit and the repository contains a complete,
reproducible blocker record and handoff explaining exactly how to resume.

Do not ask the owner routine questions. Make conservative protocol-preserving
decisions and document them. Never weaken an experimental control merely to
make the run complete.
