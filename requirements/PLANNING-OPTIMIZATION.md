# Praxis planning and optimization requirements

Status: **Proposed** (first implementation: `PRAXIS-PLAN-01`, shadow mode)

Implementation, design decisions and the first shadow-experiment results are
described in [`docs/planning.md`](../docs/planning.md),
`DF-ROS-2026-A046` and `EV-ROS-2026-A058`.

## Objective

Praxis shall provide a deterministic planning capability that analyzes
repository work and produces evidence-backed execution strategies intended to
reduce total wall-clock completion time, agent/model cost, repeated context
acquisition, unnecessary restarts, merge/conflict risk, duplicated work, time
spent on work whose prerequisite has already been satisfied, and time spent
implementing work that is already substantially complete. The planner informs
execution. The initial implementation does not autonomously execute work.

## Core principles

- **PRX-PLAN-001** The first planner release MUST operate in shadow/read-only
  mode. It MUST NOT start or complete work items, resume or continue
  executions, mutate priorities, change dependencies, change work-item state,
  create branches, launch agents, modify `.ros/`, or automatically reconcile
  stale state. It MAY recommend these actions.
- **PRX-PLAN-002** The scheduling and optimization engine MUST be
  deterministic: identical work state, telemetry, dependencies, constraints,
  planner configuration and repository state MUST produce the same plan. LLMs
  MAY later assist in deriving uncertain metadata but MUST NOT be the
  authoritative scheduling engine.
- **PRX-PLAN-003** The planner MUST be implemented in F# and follow the
  four-tier architecture (Domain, Contracts, Application,
  Infrastructure/CLI). Domain planning logic MUST NOT depend on GitHub,
  filesystem access, network services, an LLM or provider-specific APIs.
- **PRX-PLAN-004** No new external dependency may be introduced without
  explicit approval.

## Work inventory

- **PRX-PLAN-010** Planning MUST operate over the union of
  `.ros/work/queue.json` and live work from `.ros/context/current.json`. A live
  item absent from the queue MUST still be visible. The planner MUST NOT assume
  `queue.json` alone is the complete inventory.
- **PRX-PLAN-011** Every nonterminal item MUST be classified into an explicit
  planning state: at minimum captured, ready, active, blocked, partially
  complete, awaiting external evidence, awaiting human action, stale-state
  candidate, abandoned, complete. Planning state MAY be richer than lifecycle
  state but MUST NOT silently modify it.
- **PRX-PLAN-012** Captured work MUST NOT be treated as executable. The
  planner MUST distinguish known runnable work, work needing triage, work
  lacking acceptance criteria, work lacking dependency information, and work
  deliberately captured for later.
- **PRX-PLAN-013** A blocked item MUST NOT be placed in an execution wave
  unless the blocker has been proven resolved. A blocker being old is not
  sufficient.

## State freshness

- **PRX-PLAN-020** The planner MUST detect evidence suggesting recorded state
  is behind reality (a blocker's PR has merged; CI a checkpoint waits for has
  passed; a dependency recorded incomplete is complete; an item stays active
  after its implementation merged; an item stays blocked by a release that now
  exists).
- **PRX-PLAN-021** The planner MUST distinguish recorded state, external
  observed evidence and inferred stale-state candidates, and MUST NOT silently
  rewrite recorded state from external evidence.
- **PRX-PLAN-022** When stale state materially changes the runnable graph, the
  planner SHOULD recommend a reconciliation wave before implementation work and
  state that planning confidence is limited.

## Remaining-work modeling

- **PRX-PLAN-030** Planning MUST estimate remaining effort from current state,
  not every active item from zero.
- **PRX-PLAN-031** Verified durable checkpoints MUST be used: summary, next
  action, commit, recoverability, execution status, evidence produced.
- **PRX-PLAN-032** With a valid checkpoint and little work remaining, the
  planner SHOULD prefer continuation/completion over a new unrelated front when
  other constraints are equal.
- **PRX-PLAN-033** An item whose executor disappeared but which has a
  recoverable checkpoint MUST be recognised as resumable; restarting from
  scratch SHOULD be modelled as more expensive than continuation.

## Dependency graph

- **PRX-PLAN-040** Dependencies MUST be modelled explicitly with at least the
  kinds Hard, Soft, External, Human and Evidence.
- **PRX-PLAN-041** An item with an unresolved hard dependency MUST NOT be
  scheduled.
- **PRX-PLAN-042** A dependency on a Praxis item MUST use that item's effective
  state; a completed dependency MUST NOT keep blocking because old text says
  "Depends on".
- **PRX-PLAN-043** Dependencies derived from free text MUST be marked inferred;
  structured dependencies MUST carry higher evidence quality.
- **PRX-PLAN-044** Dependency cycles MUST be detected and reported as findings,
  never ordered arbitrarily.
- **PRX-PLAN-045** The planner SHOULD calculate the critical path through
  executable work and identify items that unlock several others.
- **PRX-PLAN-046** The planner SHOULD calculate an explainable unlock measure.

## Cost model

- **PRX-PLAN-050** Missing monetary cost MUST be unknown: never zero, an
  invented default, a provider stereotype, or an unevidenced dollar estimate.
- **PRX-PLAN-051** Observed, provider-reported, calculated, estimated and
  unavailable cost MUST be distinguished.
- **PRX-PLAN-052** With insufficient cost telemetry, cost optimization MUST
  report the limitation (for example "0 of N sampled executions contain usable
  cost evidence"). The planner MAY still optimize known contributors:
  execution count, restarts, context setup, duration, retries, CI invocations.
- **PRX-PLAN-053** Expected cost MAY include token, cache, tool, CI, external
  and retry components, each preserving its evidence source.

## Duration model

- **PRX-PLAN-060** The planner SHOULD use historical execution duration.
- **PRX-PLAN-061** Predictions MUST use ranges when uncertainty exists; avoid
  false precision.
- **PRX-PLAN-062** Every duration estimate MUST carry confidence: high,
  medium, low or unavailable.
- **PRX-PLAN-063** Elapsed time since an active execution began MUST NOT be
  read as productive work time.

## Context reuse

- **PRX-PLAN-070** The planner SHOULD model the cost of starting in a new
  problem area.
- **PRX-PLAN-071** Related items MAY declare or derive context affinity (same
  subsystem, modules, requirement family, defect cluster, branch or topic).
- **PRX-PLAN-072** The planner SHOULD recommend sequential execution by one
  executor where context reuse is likely, and MUST NOT merge work-item identity
  for convenience.

## Parallel execution

- **PRX-PLAN-080** The planner MUST NOT assume dependency-independent work is
  safely parallel.
- **PRX-PLAN-081** The planner SHOULD keep a collision graph independent of
  the dependency graph (same files, modules, persistent state, generated
  files, branch, overlapping requirements, historical merge conflicts).
- **PRX-PLAN-082** Parallel mutations of the shared Praxis state files
  (`.ros/context/current.json`, `.ros/work/queue.json`,
  `.ros/events/events.jsonl`) MUST carry elevated collision risk until
  `PRAXIS-STATE-MERGE-01` or an equivalent removes it.
- **PRX-PLAN-083** Insufficient evidence about overlap MUST be classified as
  unknown, not safe.
- **PRX-PLAN-084** The planner MUST support a maximum-concurrency constraint.

## Optimization strategies

- **PRX-PLAN-090** A baseline strategy MUST exist for comparison (oldest
  runnable first, one at a time).
- **PRX-PLAN-091** A minimum-duration strategy MUST exist.
- **PRX-PLAN-092** A minimum-cost strategy MUST exist only with sufficient
  cost evidence; otherwise it is reported unavailable or low-confidence.
- **PRX-PLAN-093** A balanced strategy MUST combine expected cost, duration,
  conflict risk, uncertainty, context reuse and completion likelihood with
  explicit weights.
- **PRX-PLAN-094** A maximum-safe-parallelism plan SHOULD exist, still obeying
  dependencies and known conflicts.

## Pareto frontier

- **PRX-PLAN-100** Praxis MUST NOT claim a universally optimal schedule when
  objectives conflict.
- **PRX-PLAN-101** Where evidence allows, the planner SHOULD produce a Pareto
  frontier (cheapest, fastest, balanced, highest safe parallelism).
- **PRX-PLAN-102** The planner SHOULD expose diminishing returns.

## Uncertainty

- **PRX-PLAN-110** Unknown values MUST remain explicit.
- **PRX-PLAN-111** A plan MUST carry an overall confidence derived from the
  completeness and quality of its inputs.
- **PRX-PLAN-112** The planner SHOULD identify the evidence that would most
  improve the plan.

## Plan output

- **PRX-PLAN-120** Plans SHOULD be represented as waves.
- **PRX-PLAN-121** Every scheduled item MUST state why it appears where it
  does.
- **PRX-PLAN-122** A plan MUST separately report work that cannot currently
  execute.
- **PRX-PLAN-123** Captured work requiring triage MUST be reported separately
  and never silently queued.
- **PRX-PLAN-124** Stale-state candidates MUST be reported separately.

## Explainability

- **PRX-PLAN-130** The planner SHOULD support `praxis plan explain
  <work-item>`: why scheduled or not, what blocks it, grouping, parallelism,
  and the evidence behind its estimate.
- **PRX-PLAN-131** Conclusions MUST preserve provenance to their sources
  (lifecycle state, telemetry, checkpoint, Git, CI, GitHub, structured or
  inferred dependency).

## Plan identity and freshness

- **PRX-PLAN-140** Every plan MUST identify the state it was computed against:
  repository identity, commit, planning timestamp, planner version, work-state
  fingerprint.
- **PRX-PLAN-141** A plan MUST be identifiable as stale after material change
  (item completed or blocked, new work, dependency resolved, checkpoint
  recorded, repository changed, conflict relationship changed).
- **PRX-PLAN-142** Praxis SHOULD support deterministic replanning.

## Historical experiment

- **PRX-PLAN-150** The experiment MUST replay completed historical work where
  evidence exists, using only evidence available at each decision point.
- **PRX-PLAN-151** Historical experiments MUST NOT knowingly use later
  outcomes as though known earlier.
- **PRX-PLAN-152** Replay SHOULD compare predicted and observed duration,
  ordering, concurrency, and cost where available.

## Shadow experiment

- **PRX-PLAN-160** The initial planner MUST be tested against Praxis's own
  nonterminal work.
- **PRX-PLAN-161** The shadow experiment MUST only recommend; execution stays
  manual.
- **PRX-PLAN-162** Praxis SHOULD later compare recommendations with what
  happened, without claiming causation.

## Calibration and learning

- **PRX-PLAN-170** Praxis SHOULD track estimate error over time.
- **PRX-PLAN-171** Estimates MAY be segmented by task class.
- **PRX-PLAN-172** Measurements MAY be segmented by provider/model/runtime;
  Praxis MUST NOT hard-code provider performance claims.
- **PRX-PLAN-173** Praxis SHOULD detect drift; old measurements MUST NOT stay
  authoritative forever.

## CLI and contract

- The command family SHOULD be `praxis plan analyze`, `simulate`, `compare`
  and `explain`, with `simulate --for balanced|speed|cost` and
  `--max-concurrency N`. Future `--budget` / `--deadline` constraints MUST
  NOT be accepted as satisfiable without evidence to evaluate them.
- **PRX-PLAN-180** Every planner command MUST support machine-readable JSON.
- **PRX-PLAN-181** Planner output MUST use an explicitly versioned contract.
- **PRX-PLAN-182** Planner schemas SHOULD use Praxis's canonical
  repository-qualified work identity once available, and MUST NOT introduce a
  competing global identity scheme.

## Non-goals of the first experiment

Automatic agent dispatch, provider or model selection, automatic branch
creation, work-item mutation, reprioritization, dependency creation or
stale-state repair; autonomous execution; automatic budget spending;
cross-repository scheduling; machine-learning optimization.
