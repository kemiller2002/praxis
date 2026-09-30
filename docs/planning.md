# Advisory planner (`praxis plan`)

The planner reads a repository's work state and recommends an execution
strategy. It is **advisory and read-only** (PRX-PLAN-001): it never starts,
completes, blocks, reprioritizes or reconciles work, never creates branches,
launches agents or writes `.ros/`, and every command prints to stdout only.
Requirements: [`requirements/PLANNING-OPTIMIZATION.md`](../requirements/PLANNING-OPTIMIZATION.md).
Decision: `DF-ROS-2026-A046`. First shadow experiment: `EV-ROS-2026-A058`.

## Commands

```
praxis plan analyze   [--json]                        inventory, states, findings, critical path, history
praxis plan simulate  [--for baseline|speed|balanced|cost|max-parallel]
                      [--max-concurrency N] [--budget AMOUNT [--currency CODE]]
                      [--deadline 4h|90m|1h30m] [--details] [--json]
praxis plan compare   [--max-concurrency N] [--json]   every strategy, Pareto frontier, concurrency curve
praxis plan explain   ID [--json]                      why an item is (not) scheduled, under every strategy
praxis plan replay    [--details] [--json]             historical replay without hindsight
praxis plan freshness --plan FILE [--json]             is a saved plan stale; what happened since
praxis plan groups    [--json]                         evidence-based work groups (see "Work groups")
praxis plan explain-group GROUP-ID [--json]            why a group exists, what it excludes, what would change it
praxis plan simulate --groups [--max-concurrency N]    waves of groups and ungrouped items
praxis plan compare  --groups [--max-concurrency N]    grouped versus independent execution, per group
```

Common options: `--observations FILE` (external CI/GitHub evidence, below),
`--config FILE` (planner configuration, below), `--as-of TIMESTAMP` (pin the
planning timestamp, for reproducible documents). In this checkout use `./ros
plan ...`. Exit codes: `0` success, `1` input could not be read, `2` invalid
arguments, `3` (`freshness` only) the plan is stale.

Typical use: `praxis plan analyze` first; reconcile what it reports as stale;
then `praxis plan compare` to see the tradeoffs; `praxis plan simulate --for
balanced --json > plan.json` to keep a plan; later `praxis plan freshness
--plan plan.json` to learn whether it still holds and what happened since.

## What the planner reads

| Input | Source | Evidence source code |
| --- | --- | --- |
| Backlog items | `.ros/work/queue.json` (incl. optional `dependsOn: [ID]`) | `backlog-queue`, `structured-dependency` |
| Live items, blockers, verified checkpoints | `.ros/context/current.json` | `live-context`, `checkpoint` |
| Durations, classes, cost metrics | `.ros/telemetry/executions/*.json` | `telemetry` |
| Merged PRs, tags, merged checkpoint commits | read-only Git on the integration branch (`origin/HEAD`, `origin/main`, `main`, ...) | `git` |
| CI results, GitHub state | `--observations FILE`, supplied by a caller that can see them | `ci`, `github`, `external-observation` |
| Weights, declared dependencies/areas/conflicts | `--config FILE` | `planner-configuration` |

The inventory is the union of queue and live context (PRX-PLAN-010); the
effective lifecycle state uses the same authority as `work list`
(`QueuePresentation.effectiveStatus`: a live active/blocked/complete item
wins). Only checkpoints that re-verify as durable checkpoints count
(PRX-PLAN-031). Git queries are `rev-parse`, `log`, `tag` and `merge-base
--is-ancestor`; none writes. The Domain never touches Git, files or the
network (PRX-PLAN-003); the Application port (`PlanningReadPort`) has no
write member at all.

### Observations file

```json
{ "observations": [
  { "kind": "ci-passed", "subject": "PRAXIS-REMOTE-16", "source": "ci", "reference": "run 123" },
  { "kind": "ci-failed", "subject": "<commit sha>", "source": "ci" },
  { "kind": "pull-request-merged", "pullRequest": 85, "source": "github" },
  { "kind": "release-exists", "tag": "v3.6.0", "source": "github" },
  { "kind": "commit-merged", "commit": "<sha>", "into": "main", "source": "github" } ] }
```

A CI subject is a work-item ID or a checkpoint commit. Observations are
evidence only; they never rewrite recorded state (PRX-PLAN-021).

### Configuration file

Every field is optional; the defaults are shown.

```json
{ "maxConcurrency": 3,
  "minimumCostSamples": 5,
  "praxisStateMergeSafe": false,
  "genericTags": ["follow-up", "high", "medium", "low", "code", "testing"],
  "balancedWeights": { "duration": 0.30, "cost": 0.15, "conflictRisk": 0.15,
                       "uncertainty": 0.10, "contextReuse": 0.10, "completionLikelihood": 0.20 },
  "remainingFractions": { "finalizationOnly": [0.00, 0.10], "verificationRemaining": [0.05, 0.25],
                          "implementationInProgress": [0.25, 0.75], "unclassified": [0.10, 0.90] },
  "dependencies": [ { "from": "B", "to": "A", "kind": "hard" } ],
  "conflicts": [ { "left": "A", "right": "B", "reason": "both rewrite Program.fs" } ],
  "areas": { "A": ["src/Ros.Cli"], "B": ["docs/"] } }
```

## How it decides

**Planning states** (PRX-PLAN-011). `complete`/`abandoned` are terminal.
Captured (or unrecognised) status is `captured` and never runnable, with its
triage needs listed (PRX-PLAN-012). A blocked item is `awaiting-human` when an
open blocker prerequisite is a human action, `awaiting-evidence` when it is a
PR, release or CI result, else `blocked`; none is ever scheduled
(PRX-PLAN-013). An active or ready item with a verified checkpoint is
`partially-complete`. Any item with stale-state evidence is
`stale-state-candidate`.

**Dependencies** (PRX-PLAN-040..044). Structured: queue `dependsOn`, config
`dependencies`. Inferred (always labelled `inferred`): IDs named *after* an
explicit phrase ("depends on", "requires", "prerequisite", "blocked by/on",
"waits for/on") in a description; IDs, `PR #N`, `release vX.Y.Z`, CI and
human-action mentions in a blocker clause that does not already say it is
complete/merged; a CI mention in a checkpoint's next action. Only IDs present
in the inventory are recognised. A work-item dependency resolves against the
target's effective state; a completed target is satisfied and reported, never
blocking (PRX-PLAN-042). Cycles are found with Tarjan's algorithm and reported;
their members are blocked, never ordered arbitrarily.

**Staleness** (PRX-PLAN-020..022). A `stale-state-candidate` finding needs
evidence, never age: a checkpoint commit already reachable from the
integration branch (Git, high confidence); CI observed passing for a
checkpoint that waits on it; a blocker whose named prerequisites are now
satisfied (all: medium confidence; some: low, listing what is still open or
unobservable). Stale items go to the state-cleanup section and a serial
**wave 0** of `reconcile` recommendations, and the plan reports that its
confidence is limited.

**Durations** (PRX-PLAN-060..063, 171..173). Each finalized execution
contributes productive time = `time.wall_ms - time.blocked_ms`, or its
runtime-measured session time (`time.active_ms`, from the
`anthropic-claude-session` adapter) when that is longer: ROS wall time starts
at `work begin`, which executors often run just before completion. Active
executions are never measured. Per task class (the telemetry classification
vocabulary) the planner uses the interquartile range (P25 / median / P75),
rounded to whole minutes below an hour and five minutes above (PRX-PLAN-061),
with confidence by sample size (20+ high, 8+ medium, 3+ low, else unknown;
one level lower when P75 exceeds 4x P25). A class without history falls back
to the pooled distribution one confidence level lower; no history is unknown.
Implementation work (the pooled class and `development`, `maintenance`,
`refactoring`, `defect-bug-fix`, `infrastructure-devops`,
`testing-verification`, `prototype-proof-of-concept`) loses one more
confidence level while fewer than half of the distribution's samples carry
runtime-measured session time (`sessionMeasured` in the analysis JSON), and
the item's estimate basis says so: in `EX-ROS-2026-A021` the history-based
model's upper bound of 45 min missed observed durations of 61 and 112 min
(`EV-ROS-2026-A064`, `HY-ROS-2026-A027`). The estimate range itself is not
rescaled, because one experiment does not calibrate a factor; measuring
sessions is what corrects it.
Segments by provider, runtime and model are reported as measurements only.
Drift compares the median of the 10 most recent executions with the earlier
interquartile range.

**Remaining work** (PRX-PLAN-030..033). A verified checkpoint's next action is
classified (implementation, verification, finalization, unclassified) and the
full-effort range is scaled by the configured fractions. These fractions are
an explicit, reported **planner assumption**, not a measurement, so
continuation estimates are capped at medium confidence (low when
unclassified). An active item without a checkpoint is `unclassified`
continuation. Every item's explanation states this basis.

**Cost** (PRX-PLAN-050..053, 092). `cost.*` metrics are the only monetary
evidence: every `cost.execution_total` of an execution (summed, its
aggregation), else the sum of cost components, else the latest
`cost.session_cumulative` (a session gauge, never added to components),
classified observed / provider-reported / calculated / estimated from the
metric's quality and source. Record platform-reported cost with
`ros telemetry record ID --metric cost.execution_total --value 9.48
--currency USD --quality observed --source-type platform --source-name NAME
--mechanism session-record`. An item whose own finalized executions carry a
monetary total reports that evidence's strongest kind (`observed` for
platform cost); other items report `estimated` when history is sufficient. Unless at least `minimumCostSamples` executions
carry usable cost in one currency, every cost is unknown, the `cost` strategy
is **unavailable** with the "N of M executions" statement, and a `--budget`
is `cannot-evaluate`. Every plan still reports known contributors: executions,
continuations, context acquisitions, peak concurrency, accepted-risk pairs.

**Collisions** (PRX-PLAN-080..084). Every pair of runnable items gets a risk:
`conflict` (same checkpoint branch, overlapping declared paths, declared
conflict), `unknown` (either item has no scope evidence: no non-generic tag,
declared area or checkpoint branch), `elevated` (shared non-generic tag;
shared Praxis state files unless `praxisStateMergeSafe`), else `safe`. Unknown
ranks above elevated. No strategy ever co-schedules `unknown` or `conflict`.

**Strategies and risk policies** (PRX-PLAN-090..094).

| Strategy | Order | Concurrency | Co-schedules |
| --- | --- | --- | --- |
| `baseline` | oldest runnable first | 1 | nothing |
| `speed` | critical path, most dependents, continuations, shortest remaining | `--max-concurrency` (default 3) | safe and elevated pairs |
| `balanced` | explicit weighted score (reported per item) | `--max-concurrency` (default 3) | safe pairs, and pairs whose only signal is shared Praxis state |
| `max-parallel` | as `speed` | unbounded unless limited | safe and elevated pairs |
| `cost` | cheapest expected cost first | 1 | nothing; **unavailable** without cost evidence |

The balanced score is `duration x d + cost x c + conflict risk x r +
uncertainty x u - context reuse x x - completion likelihood x l` (lower runs
first); when cost is unknown its weight is reported as not applied. Waves are
built by deterministic list scheduling: each wave takes available items (all
hard prerequisites finished in earlier waves) in strategy order, adding an item
only if the policy admits its pair with every member and capacity remains; a
deferred item records why (capacity or collision). Every entry carries its
reasons (PRX-PLAN-121), and blocked, triage and stale sections are separate
(PRX-PLAN-122..124). Context affinity (shared area tag, requirement family
such as `PRAXIS-REMOTE`, same branch) yields "consider one executor for A ->
B" recommendations that keep each item's identity (PRX-PLAN-072).

**Comparison** (PRX-PLAN-100..102). `compare` simulates every strategy,
marks the non-dominated ones (minimising expected duration, peak concurrency,
accepted-risk pairs, context acquisitions, and cost when every plan has it) as
the frontier, never names a single optimum, and simulates the speed strategy
at 1..N executors, flagging where one more saves under 10%.

**Identity and freshness** (PRX-PLAN-140..142, 162). Each document carries
repository, commit, branch, planning time, planner version, and SHA-256
fingerprints of the work state, all inputs, and the collision graph, plus a
digest per item. `freshness` diffs a saved plan's snapshot with the current
one (completed, blocked, added, removed, checkpoint recorded, inputs changed,
repository moved, collisions changed) and lists what happened to each
recommended item, stated as observation, never as the planner's effect.

**Replay** (PRX-PLAN-150..152, 170). Each finalized execution is predicted
from executions finalized strictly before it started, then compared with its
actual productive time (within range, absolute and relative error, by class).
It also reports observed execution overlap against the baseline's modeled
concurrency of one, how often backlog creation order matched actual start
order, and cost evidence.

**Determinism** (PRX-PLAN-002). All ordering is ordinal with explicit
tie-breaks; no clock is read below the CLI (`--as-of` pins it); JSON field
order is fixed. The logical plan (everything but the snapshot timestamp) of
identical inputs renders byte-identically, which a test asserts.

## Work groups

Requirements: [`requirements/PLANNING-WORK-GROUPS.md`](../requirements/PLANNING-WORK-GROUPS.md).
Decision: `DF-ROS-2026-A047`. Baseline and experiment: `EV-ROS-2026-A059`,
`EX-ROS-2026-A021`.

A **work item** is the governed unit; a **planning group** is an advisory
recommendation that several items share enough context to be reasoned about
together; an **execution group** is a deliberate decision to give them one
execution context. The planner produces planning groups only. Membership
never changes a member's lifecycle state, evidence, attribution, telemetry
or checkpoints, and no group field can carry another member's changes.

**Signals** (PRX-GRP-020..022), in evidence priority, each labelled with its
basis:

| Signal | Basis | Affinity alone |
| --- | --- | --- |
| declared together (`grouping.groups`), common architecture decision (`grouping.architecture`) | explicit | high |
| shared non-generic tag | explicit | medium |
| hard dependency between the two | explicit (structured) or inferred (text) | medium |
| both cite the same requirement or decision (`PRX-`, `RQ-`, `DF-`) in their description | derived | medium |
| overlapping declared paths (`areas`), same checkpoint branch | explicit / derived | high |
| same ID family, two or more shared title words | inferred | low |

Two non-inferred medium signals corroborate each other to high. A pair with
no signal is `none` when both items carry scope evidence, and `unknown` when
either carries none: unknown is never reported as unrelated.

**Forming groups.** Open items (not complete, abandoned or stale; captured
items may join a planning group but must be triaged before execution) are
clustered per execution repository. The best-connected item seeds a group;
the candidate with most qualifying links joins next, ties broken by evidence
priority, and only if it reaches `minimumAffinity` (default medium) with at
least two thirds of the members. Nothing joins to fill capacity; a cohesive
pair is kept and noted as below the preferred size. A candidate larger than
`maximumAutomaticSize`, or larger than a size at which **context pressure**
was observed for its members, is split in dependency order and the split is
explained. A group joined only through a shared architecture decision says so
(a merge). Human declarations are taken as declared, outrank inference, and
warn when oversized, overlapping, mixing repositories without
`crossRepository`, or past observed context pressure.

**Per group** the planner reports kind, origin, area, members with their own
state and the blocked members they wait on, cohesion lines (`k/n` members per
signal), shared context, required sequence (hard dependencies, ordinal
tie-breaks), execution repository, affinity and confidence, intra-group
collision risk and parallel safety (separately from affinity), recommended
execution (one sequential agent; one owner with parallel subtasks only when
every pair is collision-safe; or split by repository), context cost (`n`
independent acquisitions versus 1 grouped, with the cold start priced from
measured history once at least three finalized executions carry
`time.first_code_change_ms`, else unknown), partial-completion progress and
notes. The analysis JSON's `history.contextOverhead` reports the measured
cold start (interquartile range), the median governance-document and repeated
reads per session, and whether it is sufficient. Across groups it derives
group->group, item->group and group->external dependencies from member
dependencies (naming each), detects cycles, and says which groups may run
concurrently under the `accept-elevated` policy.

**Configuration** (`--config FILE`, all optional):

```json
{ "grouping": {
    "preferredSize": [3, 10], "maximumAutomaticSize": 12, "minimumAffinity": "medium",
    "groups": [ { "id": "GROUP-SUMMA-DATABASE-004", "members": ["DB-21", "DB-22"],
                  "kind": "shared-migration", "origin": "human-declared",
                  "sharedContext": ["one typed migration model"],
                  "executionRepository": "summa", "crossRepository": false,
                  "architectureNotes": ["no direct SQL outside Strata"] } ],
    "architecture": [ { "decision": "DF-...", "members": ["A", "B"], "statement": "..." } ],
    "executionRepositories": { "PRAXIS-REMOTE-12": "conditor" } } }
```

An item whose description says "External repository" and that has no
`executionRepositories` entry is never grouped into this checkout.

**Context-pressure evidence** is an observation (`--observations FILE`):

```json
{ "kind": "context-pressure", "members": ["A", "B", "C", "D"],
  "indicators": { "compactions": 2, "forgottenRequirements": 1 },
  "source": "telemetry", "reference": "EXE-..." }
```

Any positive indicator limits groups sharing a member to one fewer member
than the observed execution (at least two).

**IDs.** Recommendations are named `GROUP-<REPOSITORY>-<AREA>-<NNN>` and are
stable for identical inputs only; durable IDs come from declarations.

## JSON contract

Every document has `"schema": "praxis.plan/1.0.0"` and a `kind`: `analysis`,
`plan`, `comparison`, `explanation`, `replay`, `freshness`, `groups`,
`group-explanation`, `group-plan` or `group-comparison`. `groups` documents
round-trip through `PlanningJson.parseGroups`. Every estimate
is `{lowerMs, expectedMs, upperMs, confidence, display}` (or `{lower,
expected, upper, confidence}` with `{amount, currency}` for money) and an
unknown bound is `null`, never `0`. Codes (planning states, reasons,
findings, collision signals, evidence sources) are stable kebab-case strings.
`plan` documents round-trip through `PlanningJson.parsePlan`, which
`freshness` uses. Work items are identified by their existing Praxis ID; no
new identity scheme is introduced (PRX-PLAN-182).

## Architecture

| Tier | Module |
| --- | --- |
| Domain | `Ros.Domain.Planning`: `Model`, `History`, `Inventory`, `Graph`, `Snapshot`, `Scheduling`, `Comparison`, `Replay`, `Planner`, `Grouping` |
| Contracts | `Ros.Contracts.Planning.PlanningJson` (render, parse, config and observation inputs) |
| Application | `Ros.Application.Planning`: `PlanningReadPort`, `PlanningOperations.gather/analyze` |
| Infrastructure | `Ros.Infrastructure.Planning.FilePlanningRepository` (files, read-only Git) |
| CLI | `Ros.Cli.PlanCommands` |

No external dependency was added (PRX-PLAN-004).

## Requirement status

| Requirement | Status |
| --- | --- |
| 001-004 | Met. Read-only port; test 30 hashes every file and Git state before and after every command. |
| 010-013 | Met. |
| 020-022 | Met for Git-observable evidence and supplied observations. The planner does not query GitHub or CI itself. |
| 030-033 | Met; remaining fractions are an explicit configurable assumption, not yet calibrated. |
| 040-046 | Met. Soft dependencies are modelled but only structured config can declare them. |
| 050-053 | Met. Per-component cost modelling (053) is limited to what `cost.*` metrics carry. |
| 060-063 | Met. |
| 070-072 | Met as recommendations; context acquisition is counted, not priced. |
| 080-084 | Met. File-level overlap is known only from declared `areas`. |
| 090-094 | Met. |
| 100-102 | Met. |
| 110-112 | Met. |
| 120-124, 130-131 | Met. |
| 140-142 | Met. |
| 150-152 | Met, except predicted-vs-actual cost (no cost evidence exists yet). |
| 160-161 | Met; see `EV-ROS-2026-A058`. |
| 162 | Mechanism met (`freshness` outcomes); the comparison itself needs time to pass. |
| 170-173 | Met in replay and drift; error is not yet persisted over time. |
| 180-182 | Met. |

## Work-group requirement status

| Requirement | Status |
| --- | --- |
| GRP-001..003 | Met: planning groups only; members unchanged (tests 8, 9). |
| GRP-010..011 | Met with the typed model in `Grouping`; recommended IDs stable for identical input only. |
| GRP-020..022 | Met for tags, declared paths, branches, dependencies, requirement/decision references, declarations, ID families and titles. Historical co-change, test overlap and deployment boundaries are not observable yet. |
| GRP-030..031 | Met (tests 2, 18). |
| GRP-040, 044 | Guidance for executors; `EX-ROS-2026-A021` requires the group analysis. Group checkpoints and durable notes are phase two (`PRAXIS-GROUP-05`). |
| GRP-041..043 | Met by construction (tests 9, 10); per-item attribution in a grouped execution is enforced by the existing work protocol. |
| GRP-050..052 | Met for dependencies and cycles (dependency test) and repositories (tests 5, 5b); cross-repository orchestration is future work. |
| GRP-060..063 | Met; context cost is counted, and the cold start is priced from session metrics once measured (`PRAXIS-PLAN-05`). |
| GRP-070..072 | Met. |
| GRP-073 | Declarations from configuration; mutation commands captured as `PRAXIS-GROUP-01..05`, deferred. |
| GRP-074 | Size and context-pressure splits and architecture merges met (tests 18, 19, merge test); splitting by independent chain or external blockage is not implemented. |
| GRP-075 | Met (test 15). |
| GRP-080..088 | Met for one cohort: baseline and predictions frozen (`EV-ROS-2026-A059`), both arms run and blindly evaluated (`EX-ROS-2026-A021`), results and classification in `EV-ROS-2026-A064`. One run; replication pending. |
| GRP-090 | All 20 cases in `tests/Ros.Tests/GroupingTests.fs`; case 20 in `PlanningCliTests`. |

## Known limitations and next steps

- Execution wall time is a weak effort signal in this repository: many
  executions begin just before completion (median productive time about two
  minutes), so estimates are narrow and replay error is high. Starting work
  items when work actually starts would improve every estimate.
- Cost and context overhead are observable (`PRAXIS-PLAN-05`) but not yet
  recorded for past executions, so `cost` and `--budget` stay unavailable and
  context reuse unmeasured until executions record platform cost and ingest
  their session transcripts.
- Scope evidence is tags only unless areas are declared; most pairs are
  `elevated` (shared Praxis state) or `unknown`.
- Decide from the shadow evidence whether autonomous execution should ever
  become a separate later phase (non-goal of this release).
- Work groups (`EV-ROS-2026-A064`): one grouped execution of five
  high-affinity items cost less than half as much as five independent ones,
  repeated far less context and produced one consistent model; independent
  executions met individual criteria more faithfully. Next: add a
  reuse-inventory and per-criterion verification step to grouped-execution
  guidance; (done in `PRAXIS-PLAN-05`: the session-metrics script is the
  `anthropic-claude-session` adapter, platform cost is recorded as
  `cost.execution_total`, and implementation durations are down-weighted
  until sessions are measured); replicate with a loosely related cohort; consider requiring an
  admitted member to reach the group's typical affinity (`EV-ROS-2026-A059`).
