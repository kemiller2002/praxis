# Praxis work grouping and planner experiment requirements

Status: **Accepted** (phase one, read-only: `WI-0064`; accepted by the repository owner, `PRAXIS-PLAN-03`). Extends
[`PLANNING-OPTIMIZATION.md`](PLANNING-OPTIMIZATION.md) and `DF-ROS-2026-A046`.
Design and requirement status: [`docs/planning.md`](../docs/planning.md)
("Work groups"). Experiment protocol: `EX-ROS-2026-A021`; baseline: `EV-ROS-2026-A059`; decision: `DF-ROS-2026-A047`.

## Objective

Extend the advisory planner with evidence-based **work groups** so related
work items can be reasoned about, and when appropriate executed, together by
the same agent: to improve architectural consistency, reduce repeated context
loading and duplicated investigation, expose interactions between related
changes, and enable better long-term decisions across an area (for example a
family of schema changes, API changes, related CLI commands, related
components, one telemetry subsystem, one migration spanning several items).

Individual work items MUST remain independently identifiable, traceable,
completable and auditable. The first goal is to **test** whether grouping
improves outcomes before grouped execution becomes default Praxis behavior.

## Core distinction

- **PRX-GRP-001** Praxis MUST distinguish the **work item** (the existing
  independently governed unit: ID, lifecycle state, requirements, acceptance
  criteria, dependencies, evidence, provenance, execution history, completion,
  telemetry, checkpoints), the **planning group** (an advisory recommendation
  that several items share enough context or architectural affinity to be
  considered together) and the **execution group** (a deliberate decision to
  assign items to one execution context).
- **PRX-GRP-002** A planning group is advisory and MUST NOT change the
  lifecycle state of any member.
- **PRX-GRP-003** An execution group MAY originate from a planner
  recommendation, explicit human grouping, an architecture rule, a dependency
  chain, a shared subsystem or a shared migration. It MUST NOT collapse its
  members into one work item.

## Identity and model

- **PRX-GRP-010** A group MUST have a stable ID, suggested
  `GROUP-<repository-or-area>-<sequence>` (`GROUP-PRAXIS-PLANNING-001`), or
  `GROUP-ECHELON-<area>-<sequence>` for cross-repository groups. The exact
  form MAY follow existing Praxis identity conventions.
- **PRX-GRP-011** Groups SHOULD use a typed model: `WorkGroupId`,
  `GroupOrigin` (planner-recommended, human-declared, dependency-derived,
  architecture-declared), `GroupKind` (shared area, shared architecture,
  dependency chain, shared files, shared data model, shared API surface,
  shared migration, shared test surface, context affinity, custom), members
  with a reason and confidence, shared context, required sequence, execution
  repository and group confidence. The model MUST remain deterministic and
  provider-independent.

## Grouping evidence

- **PRX-GRP-020** The planner SHOULD consider grouping when evidence shows
  shared context: subsystem/module, schema, API surface, overlapping files or
  directories, overlapping tests, common requirements, common architecture
  decisions, dependency chain, bounded context, deployment boundary,
  migration, integration, package/project, historical co-change, explicit
  declaration, explicit work-item metadata.
- **PRX-GRP-021** Every signal MUST be explainable and labelled
  **explicit**, **derived** or **inferred**; an inference MUST carry
  confidence.
- **PRX-GRP-022** Title similarity alone MUST NOT establish a group. The
  planner SHOULD prefer structural evidence, generally in this priority:
  explicit group declaration; explicit subsystem/area metadata; dependency
  relationships; requirement/acceptance-criteria relationships; file/module
  ownership; historical co-change; inferred textual relationship.

## Size and cohesion

- **PRX-GRP-030** Group size MUST NOT be hard-coded. Configurable initial
  boundaries: preferred 3..10, maximum automatic size 12. A human MAY declare
  a larger group; a group larger than the maximum MUST produce a warning
  explaining the likely context and verification cost.
- **PRX-GRP-031** Unrelated work MUST NOT be grouped to fill capacity; a
  smaller coherent group is preferred. A recommended group MUST expose a
  cohesion explanation (for example "8/8 modify the ledger persistence
  boundary").

## Grouped execution (guidance for executors; phase two for tooling)

- **PRX-GRP-040** Before mutating code for a grouped execution, the agent
  MUST inspect every member and produce a group-level plan: common
  architecture, shared invariants, conflicting requirements, dependencies,
  implementation order, reusable abstractions, migration implications,
  compatibility constraints, common tests, opportunities for one coherent
  design, and the risks of solving each item independently. Order: understand
  group, identify shared architecture, resolve conflicts, choose a common
  design, sequence, then execute items.
- **PRX-GRP-041** Grouped execution MUST preserve each member's acceptance
  criteria, evidence, changed paths where identifiable, completion,
  provenance, telemetry where possible and checkpoints. It MUST NOT create one
  undifferentiated attribution record for every member.
- **PRX-GRP-042** Groups MUST support partial completion; a group MUST NOT
  imply every member succeeded; a blocked member MUST NOT prevent unrelated
  members from completing unless a hard dependency or invariant requires it.
- **PRX-GRP-043** No attribution laundering: one member MUST NOT claim
  another's changes; `PRAXIS-CONT-12` still applies. Shared changes MUST be
  identified as shared group infrastructure, attributed to specific members,
  or given their own work item.
- **PRX-GRP-044** Grouped executions SHOULD checkpoint after architectural or
  implementation milestones with group ID, active, completed and remaining
  members, shared decisions, branch and commit, next action; members keep
  their own checkpoint history. Durable group-level architecture notes
  SHOULD be available to later member executions.

## Dependencies and repositories

- **PRX-GRP-050** Praxis MUST support item->item, group->group, item->group
  and group->external-prerequisite dependencies. Group dependencies MUST be
  derived from member dependencies, not hide them. Cycles MUST remain
  detectable.
- **PRX-GRP-051** Every group MUST identify where implementation occurs. A
  group containing work from different repositories MUST be explicitly
  cross-repository or be split into repository-local groups. The planner MUST
  NOT schedule another repository's change into this checkout because this
  repository tracks it.
- **PRX-GRP-052** Cross-repository groups (eventually) represent one
  coordinated outcome; each repository still gets its own branch, commits,
  validation, evidence and pull request.

## Affinity, cost and parallelism

- **PRX-GRP-060** Praxis SHOULD compute a pairwise context affinity (none,
  low, medium, high) with evidence. High: same module, bounded context,
  schema, test fixture. Medium: same package, service, feature family. Low:
  same repository only, similar title only. Affinity that cannot be assessed
  MUST remain unknown.
- **PRX-GRP-061** The planner SHOULD model context setup cost as
  `coldStart + sharedContext + sum(memberIncremental)` versus
  `sum(coldStart + itemCost)`, and MUST NOT assume grouping saves tokens:
  savings MUST come from measured evidence (repeated reads, searches, tokens,
  cached tokens, tool calls, time to first productive mutation, duration,
  model requests, context resets, compactions).
- **PRX-GRP-062** Context affinity, dependency, collision risk and parallel
  safety MUST be separate relations. High affinity does not imply parallel
  safety.
- **PRX-GRP-063** A group SHOULD default to one reasoning owner; subtasks MAY
  run in parallel only where boundaries are proven independent. The planner
  MUST NOT assume one agent per item. Parallelism SHOULD normally occur
  between independent groups (portfolio -> groups -> items -> steps).

## Planner surface (phase one: read-only)

- **PRX-GRP-070** A read-only `praxis plan groups` (and `--json`) MUST
  report each group's area, members, affinity, reason, estimated context
  reuse, collision risk and recommended execution.
- **PRX-GRP-071** `praxis plan explain-group GROUP-ID` MUST answer: why these
  items are together; why another item was excluded; what evidence supports
  the group; what is inferred; what is unknown; what architecture is shared;
  what dependency order exists; what collision risk exists; why one agent
  versus several; what evidence would change the recommendation.
- **PRX-GRP-072** `plan simulate --groups` and `plan compare --groups` SHOULD
  show group-level scheduling and grouped-versus-independent tradeoffs.
- **PRX-GRP-073** A human MUST be able to declare that items belong together.
  Phase one reads declarations from planner configuration; mutation commands
  (`work group create|show|add|remove|checkpoint`, `plan execute-group`) are
  phase two, only after evidence supports the approach. Automatic grouped
  execution requires a separate explicit decision.
- **PRX-GRP-074** A group MAY be split when cohesion drops, context demand
  becomes excessive (context-pressure evidence: compactions, forgotten
  requirements, retries, inconsistent late decisions, excessive duration,
  re-reading, lost acceptance criteria), distinct architecture boundaries
  appear, an independent dependency chain emerges, collision risk can be
  reduced, or a subset is externally blocked. Candidate groups MAY merge when
  a common architectural decision materially affects both. Splits and merges
  MUST be explained.
- **PRX-GRP-075** Given identical inputs and configuration, automatic group
  recommendations MUST be deterministic. No LLM output may be the
  authoritative grouping decision; LLM analysis MAY propose annotations the
  deterministic planner consumes as explicit evidence.

## Experiment

- **PRX-GRP-080** The next experiment MUST test two hypotheses separately.
  A (scheduling value): the planner makes useful recommendations about stale
  work, dependencies, critical path, safe concurrency and remaining work
  without mutating state. B (grouping value): executing strongly related
  items in one shared reasoning context reduces repeated work and/or improves
  architectural consistency compared with independent fresh executions.
- **PRX-GRP-081** Select one coherent set of preferably 5 to 10 related work
  items with strong natural affinity; never invent items to reach a size.
- **PRX-GRP-082** Control arm: one fresh execution per item, normal
  instructions, no accumulated private context, normal governance, separate
  commits/checkpoints. Grouped arm: one fresh execution for the whole set,
  which reads every item and writes the shared architecture analysis before
  mutating, then implements with per-item attribution, evidence, checkpoints
  and acceptance-criteria validation. Neither arm merges before evaluation.
- **PRX-GRP-083** Both arms MUST start from the exact same commit, with
  separate branches, execution identities, Praxis executions and workspaces,
  and SHOULD use the same provider/model/runtime (differences recorded).
  Neither arm may see the other's implementation.
- **PRX-GRP-084** A separate evaluator execution (never an implementer),
  ideally blind to the expected winner, SHOULD compare acceptance criteria,
  tests, architectural consistency, duplicated abstractions, unnecessary
  dependencies, conflicting design decisions, duplication, migration
  correctness, compatibility, maintainability, churn and technical debt, and
  document cross-item architectural consequences as evidence, not a score.
- **PRX-GRP-085** Capture per arm: input, output and cached tokens, cost,
  wall-clock, active and blocked time, tool calls, file reads and writes,
  searches, builds, test runs, retries, failed approaches, context resets,
  compactions, executions, commits, merge conflicts, validation failures; and
  the context-overhead metrics (time to first productive change, repeated
  reads of the same files and of AGENTS.md/architecture documents, repeated
  module exploration, test discovery and architecture reasoning) and quality
  metrics (criteria passed, regressions, defects, inconsistencies,
  duplication, conflicting API patterns, design revisions, evaluator
  findings, rework). Unknown values remain unknown.
- **PRX-GRP-086** Before either arm runs, save `plan analyze`, `plan compare`
  and `plan groups` JSON as evidence and freeze predictions (duration, safe
  concurrency, group affinity, collisions, critical path, context-reuse
  opportunities, expected unknowns, cost availability). The planner MUST NOT
  execute its recommendations; predictions MUST NOT change after results.
- **PRX-GRP-087** Compare predicted with observed outcomes without claiming
  causation from one experiment, and classify the finding (clearly beneficial
  for high affinity; beneficial only above a cohesion threshold; saves
  context but increases context pressure; improves architecture but not
  speed; independent execution better for loosely related work; insufficient
  evidence). Watch for context-pressure failure.
- **PRX-GRP-088** Deliverables: the read-only model; tests; recorded
  baseline planner output; selected cohort; frozen protocol; control branch;
  grouped branch; independent evaluation; telemetry comparison; experiment
  evidence document; recommendation for the next planning iteration; explicit
  unknowns and limitations. The experiment succeeds even if grouping loses:
  success is an evidence-based grouping policy.

## Required tests

- **PRX-GRP-090** Tests MUST cover: (1) high-affinity items form a candidate
  group; (2) unrelated items are not grouped to hit a target size; (3)
  explicit human grouping outranks inferred grouping; (4) hard dependency
  order is kept inside a group; (5) external-repository work is not scheduled
  into the wrong checkout; (6) blocked items remain blocked within a group;
  (7) completed items are excluded; (8) membership does not alter lifecycle
  state; (9) membership does not merge attribution; (10) partial completion is
  supported; (11) unknown affinity remains unknown; (12) title similarity
  alone is insufficient; (13) shared files raise affinity and collision risk
  separately; (14) high affinity does not imply parallel safety; (15)
  deterministic input gives deterministic groups; (16) the group JSON
  round-trips; (17) a group explanation names its evidence; (18) oversized
  groups warn; (19) context-pressure evidence can trigger a split; (20) no
  grouping or planning command mutates repository state.
