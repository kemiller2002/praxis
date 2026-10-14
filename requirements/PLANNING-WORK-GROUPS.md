# Praxis work grouping and planner experiment requirements

Status: **Accepted** (phase one, read-only: `WI-0064`; accepted by the repository owner, `PRAXIS-PLAN-03`).
**Amended 2026-10-06 by `DF-ROS-2026-A053`** (`PRAXIS-PLAN-09`, the repository
owner's explicit approval): PRX-GRP-100..109 (cross-repository groups),
110..117 (group commands and completion), 130..138 (grouped execution by
default) and 150..158 (context and cost measurement), with tests PRX-GRP-190,
are accepted requirements that are **not yet implemented** except where a
requirement marks a part as existing (follow-on work
`PRAXIS-GROUP-07..10`, `PRAXIS-PLAN-10..11`); PRX-GRP-040, 045, 051, 052, 061
and 073 and the Objective are amended in place, with the replaced text kept
and marked. Every other ID is unchanged. Extends
[`PLANNING-OPTIMIZATION.md`](PLANNING-OPTIMIZATION.md) and `DF-ROS-2026-A046`.
Design and requirement status: [`docs/planning.md`](../docs/planning.md)
("Work groups"). Experiment protocol: `EX-ROS-2026-A021`; baseline: `EV-ROS-2026-A059`;
results: `EV-ROS-2026-A064`, replication `EV-ROS-2026-A070`; decisions:
`DF-ROS-2026-A047`, amended by `DF-ROS-2026-A053`.

New requirements mark their baseline: **(exists)** names behaviour already
implemented, **(new)** behaviour that does not exist yet, and **(changes)**
behaviour that exists and is required to change.

## Objective

Extend the advisory planner with evidence-based **work groups** so related
work items can be reasoned about, and when appropriate executed, together by
the same agent: to improve architectural consistency, reduce repeated context
loading and duplicated investigation, expose interactions between related
changes, and enable better long-term decisions across an area (for example a
family of schema changes, API changes, related CLI commands, related
components, one telemetry subsystem, one migration spanning several items).

Individual work items MUST remain independently identifiable, traceable,
completable and auditable.

*Amended by `DF-ROS-2026-A053`.* Replaced text: "The first goal is to
**test** whether grouping improves outcomes before grouped execution becomes
default Praxis behavior." That test ran twice on one cohort
(`EV-ROS-2026-A064`, `EV-ROS-2026-A070`). Grouped execution becomes the
default for groups that meet the high-affinity thresholds (PRX-GRP-130..138),
behind enforced reuse and verification gates, with continued measurement
(PRX-GRP-150..158) and a defined review and rollback trigger
(PRX-GRP-138). Groups may span repositories (PRX-GRP-100..109).

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
  The plan MUST include a **reuse inventory**: the existing parsers, domain
  rules, types, stores, command pipelines and tests in the repository that
  the members touch, found by searching the codebase rather than assumed; for
  every new abstraction the plan proposes, it names the existing one it
  considered and why that one is not reused. (`EV-ROS-2026-A064`: the grouped
  arm designed one consistent model but re-implemented the `grouping.groups`
  parser and the planner's execution-location rule.) The template is
  [`docs/group-analysis-template.md`](../docs/group-analysis-template.md).
  *Amended by `DF-ROS-2026-A053`:* for a grouped-mode execution
  (PRX-GRP-130) the reuse inventory is no longer guidance only; it MUST be
  recorded in machine-checkable form and is enforced at member completion
  (PRX-GRP-133, PRX-GRP-135).
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
- **PRX-GRP-045** Before completing any member of a grouped execution, the
  agent MUST run a **per-member, per-criterion verification pass**: for each
  member, each acceptance criterion from that member's own description is
  recorded as met, partially met, not met or unknown, with evidence (a test
  name, a command run and its result, or a file and line). A criterion is
  verified by exercising it, not inferred from the shared design; a
  criterion the group design satisfies only for some members is recorded per
  member. A member with a criterion not met is not completed; it stays
  active, is blocked, or has the gap captured as its own work item.
  (`EV-ROS-2026-A064`: independent executions were more faithful to
  individual criteria than the grouped one.)
  *Amended by `DF-ROS-2026-A053`:* for a grouped-mode execution the pass
  MUST be recorded in machine-checkable form and the completion gate refuses
  a member without it (PRX-GRP-134, PRX-GRP-135). "Has the gap captured as
  its own work item" is made precise by PRX-GRP-134 (`deferredTo`).

## Dependencies and repositories

- **PRX-GRP-050** Praxis MUST support item->item, group->group, item->group
  and group->external-prerequisite dependencies. Group dependencies MUST be
  derived from member dependencies, not hide them. Cycles MUST remain
  detectable.
- **PRX-GRP-051** Every group MUST identify where implementation occurs. A
  group containing work from different repositories MUST be explicitly
  cross-repository or be split into repository-local groups. The planner MUST
  NOT schedule another repository's change into this checkout because this
  repository tracks it. *Amended by `DF-ROS-2026-A053`:* "explicitly
  cross-repository" now means a first-class cross-repository group
  (PRX-GRP-100..109); the last sentence is restated, unchanged in meaning, by
  PRX-GRP-108.
- **PRX-GRP-052** *Superseded by PRX-GRP-100..109 (`DF-ROS-2026-A053`).*
  Replaced text: "Cross-repository groups (eventually) represent one
  coordinated outcome; each repository still gets its own branch, commits,
  validation, evidence and pull request." Cross-repository groups are no
  longer eventual; the per-repository independence it required is kept by
  PRX-GRP-104.

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
  model requests, context resets, compactions). *Amended by
  `DF-ROS-2026-A053`:* the measurements and how the planner consumes them
  are specified by PRX-GRP-150..158; the rule that savings are never assumed
  is unchanged.
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
  *Amended by `DF-ROS-2026-A053`:* that decision is `DF-ROS-2026-A053`.
  Phase two is specified by PRX-GRP-110..117 (commands, including `work group
  list`) and PRX-GRP-130..138 (grouped execution as the default for
  high-affinity groups). "Automatic" means the default recommendation and the
  default mode of `plan execute-group`; Praxis still never launches an agent
  (PRX-GRP-117).
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

## Cross-repository groups (`DF-ROS-2026-A053`, decision 1)

Motivating use case: a **portfolio sweep**. One change (for example adopting
a Praxis release across the Echelon repositories; the 3.7.1 sweep's
findings became `WI-0073`) is run from one shared brief. Each repository gets
its own pull request, and progress is kept in one consolidated status table.
Today that brief and table live outside Praxis and are maintained by hand.
These requirements make the sweep one recorded group whose status Praxis
derives, while each repository keeps governing its own work.

- **PRX-GRP-100** *Identity.* A cross-repository group MUST have an ID of
  the form `GROUP-ECHELON-<AREA>-<SEQUENCE>` (PRX-GRP-010). It is valid under
  the existing grammar (`WorkGroups.isValidGroupId`) (exists). The `ECHELON`
  area segment is reserved: a group recorded with `crossRepository: true`
  MUST use it, and a repository-local group MUST NOT (new). IDs are stable
  and never reused. A member of a cross-repository group MUST be named by
  repository-qualified work identity, `owner/repo:WORK-ID`
  (`docs/execution-runtime.md`, PRX-PLAN-182). A bare ID means the home
  repository (new).
- **PRX-GRP-101** *Authoritative home.* Exactly one repository, the group's
  **home**, holds the group record (new). The record holds the declaration,
  membership history, group plan (PRX-GRP-107) and group checkpoints, in the
  same store and through the same parser as a repository-local group
  (`.ros/work/groups.json`; `WorkGroupJson`) (exists for local groups). The
  home is the repository in which `work group create` ran, recorded as
  `homeRepository` (`owner/repo`). A home never changes implicitly. No second
  repository may hold a copy of the record.
- **PRX-GRP-102** *Member repositories reference; they do not copy.* Each
  member work item is recorded and governed in its own repository, with its
  own queue entry, context, executions, evidence and telemetry. The only
  group data a member repository holds is an immutable **group reference**
  on that item: group ID and home repository. The reference is written by
  `work group link` run in the member repository (new; PRX-GRP-110). Neither
  side stores the other's mutable state:
  - a member repository never stores the group's membership, status or
    checkpoints;
  - the home never stores a member's lifecycle state, only dated
    observations of it (PRX-GRP-103).

  A reference without a matching home membership, or a membership without a
  matching reference, is reported by `work group show` and `validate` as
  `unlinked`. It is a warning, not an error, because the two repositories
  change at different times.
- **PRX-GRP-103** *Derived status, never asserted.* The home derives each
  member's state by reading that member repository's own Praxis state,
  read-only (new). It reads either a fetched ref of the member repository
  (default branch, or the member's recorded branch) or the remote protocol's
  read operations (`docs/remote-protocol.md`). Each observation records the
  source repository, commit, `observedAt` and method. Group status is
  computed from those observations, in this order of precedence:
  1. `complete`: every current member was observed complete on its own
     evidence.
  2. `unknown`: at least one member is unobservable and the others do not
     settle the status.
  3. `partially-complete`: at least one member is complete.
  4. `blocked`: every open member is blocked or waits on a blocked
     prerequisite.
  5. `active`: at least one member is active.
  6. `not-started`.

  No command, flag or file may set a group's status. An observation older
  than the configured maximum age (`grouping.crossRepository.maxObservationAgeMinutes`,
  default 1440) is reported `stale`, not current. An unobservable member is
  `unknown`. It is never assumed complete or incomplete.
- **PRX-GRP-104** *Per-repository independence.* Each member keeps its own
  branch, commits, validation (`praxis validate` in its repository),
  evidence, pull request, checkpoints and completion. A cross-repository group
  never creates a commit, pull request, checkpoint or completion that spans
  repositories. A member completes only through its own repository's
  `work complete` (restates PRX-GRP-052) (exists for local work).
- **PRX-GRP-105** *Cross-repository order.* Member dependencies MAY name
  qualified IDs. Each dependency names a required producer milestone (new):
  - `complete`: the producer completed in its repository;
  - `merged`: the producer's recorded pull request or checkpoint commit is
    reachable from its repository's default branch;
  - `released`: a named release or tag of the producer's repository exists.

  Order is derived from these edges, and cycle detection spans repositories
  (PRX-GRP-050). A consumer whose producer has not reached the milestone is
  `waiting`. This is a planning state: it never changes the consumer's
  lifecycle state. A consumer whose producer is unobservable is `unknown`.
  Example: "release `limen` 0.8.0 before the consumers adopt it".
- **PRX-GRP-106** *Partial completion across repositories.* PRX-GRP-042
  applies across repositories. Progress is reported per repository and
  overall (`k of n complete`) (new). A blocked or unobservable member in one
  repository does not block members in others unless a PRX-GRP-105 edge
  requires it. A group whose remaining open members were removed (each with a
  recorded reason) reports its removed-open members, so the group never reads
  as if every member succeeded (PRX-GRP-116).
- **PRX-GRP-107** *Group plan and checkpoint across repositories.* The home
  holds one group plan covering every repository (new). The plan is the
  PRX-GRP-040 analysis, extended with each member's repository and the
  cross-repository order. Group checkpoints (PRX-GRP-044, exists for local
  groups) in the home record, per member: the repository, the branch, the
  latest member checkpoint ID and commit, the pull request when observed, and
  `observedAt`. The group checkpoint's own durability is verified against the
  home's Git by the existing rule. Member entries are labelled observations,
  never verifications of another repository's remote. `work group show`
  (text and `--json`) is the consolidated status table that replaces the
  hand-maintained sweep table.
- **PRX-GRP-108** *Never execute elsewhere.* In any checkout, the planner and
  `plan execute-group` act only on members whose execution repository is
  that checkout (restates PRX-GRP-051) (exists for the planner). For every
  other member they report the repository and the action to take there. They
  never write another repository's Praxis state or working tree, and never
  commit, push or open a pull request there.
- **PRX-GRP-109** *Reachability.* Observing a member repository requires
  only read access (new). In each of these cases the members are reported
  `unknown` with the reason, and the rest of the group view still renders:
  - the repository is unreachable;
  - Praxis is not installed there;
  - its Praxis state schema is unsupported;
  - read access is denied.

## Group mutation commands and completion (`DF-ROS-2026-A053`, decision 2)

- **PRX-GRP-110** *Command surface.*
  - Exists (`PRAXIS-GROUP-01..06`, `docs/cli.md` "`work group`"): `work
    group create|show|add|remove|checkpoint`.
  - New: `work group list`, `work group link` (PRX-GRP-102),
    `plan execute-group` (PRX-GRP-117), and the cross-repository options of
    `create` and `add` (qualified members, `homeRepository`).

  `work group list [--status S] [--member ID] [--repository R] [--json]`
  is read-only. It reports each group's ID, kind, origin, home, `crossRepository`, member
  count, derived status (PRX-GRP-103/116), progress, execution mode
  (PRX-GRP-130) and latest checkpoint time, sorted by ID.
- **PRX-GRP-111** *Validation.*
  - Existing rules are kept (exists): the ID grammar; a member must be a
    recorded, non-terminal work item named once; the
    `Grouping.executionLocation` join rule unless the group is
    cross-repository; an unknown group is refused; removing the last member
    needs `--allow-empty`; every problem is reported together and nothing is
    written.
  - Cross-repository members (new). A qualified member must name a
    repository. Its existence and standing are checked by observation when
    reachable. An unreachable member is recorded with `verified: false` and
    a warning. A member observed terminal is refused, as for local members.
  - The reserved `ECHELON` segment is enforced (PRX-GRP-100) (new).
- **PRX-GRP-112** *JSON contracts.*
  - Every group command supports `--json` and prints exactly one document
    (exists: the `{command, schemaVersion, status, ...}` envelope).
  - The new fields (`homeRepository`, qualified members, observations,
    execution mode, opt-outs, group executions) are additive in the store
    (`schemaVersion` 2) (new). A version-1 store is read without
    rewriting its history.
  - `list`, `link` and `execute-group` documents use the same envelope (new).
  - Unknown values are `null`, never `0`. Codes are stable kebab-case.
  - Each document kind round-trips through its parser (tested).
- **PRX-GRP-113** *Audit and provenance of membership changes.* Every
  mutation appends one history entry recording operation, member, time,
  resolved actor, reason and explicit-empty (exists). The entry also records
  the caller's active execution ID when it has one, and `link` and
  `execute-group` decisions are recorded too (new). History is append-only:
  `validate` refuses a history that was rewritten, reordered or truncated
  relative to the committed store (changes: today `validate` checks only that
  history begins with the creation). Each mutation SHOULD also append a
  `work.group.*` event to `.ros/events` so the audit trail sits with other
  work events (new).
- **PRX-GRP-114** *Idempotency* (changes). A repeated request whose
  intended end state already holds succeeds with `"changed": false`, exits
  `0`, and appends no history. Examples: `add` of a current member, `remove`
  of a non-member whose removal is already recorded, `create` of an
  identical declaration, `link` of an existing reference, or `execute-group`
  for a member already begun in this group execution.
  Today `AlreadyMember`, `NotMember` and `DuplicateGroup` exit `1`. A
  request that conflicts with recorded state is still refused with exit `1`,
  for example `create` of an existing ID with different members, kind, origin
  or home. Concurrent mutations stay serialized by the store lock (exists).
- **PRX-GRP-115** *Membership never alters members.* Group commands write
  only the group store, and `link` writes only the member's group reference.
  They never change a member's lifecycle state, evidence, executions,
  checkpoints, telemetry or attribution (PRX-GRP-002, 041, 043) (exists for
  `create`, `add`, `remove` and `checkpoint`). The no-mutation hash test
  covers every group command. For `execute-group` it covers every path the
  existing `work begin` transition does not write.
- **PRX-GRP-116** *Completion semantics.* A group has no completion
  transition of its own: there is no `work group complete`, and no command
  can mark a group done (new, as a prohibition). Group status is derived
  (PRX-GRP-103), for local groups too.
  - `complete` requires every current member to be complete on its own
    evidence (exists: `progress.complete`).
  - A removed member does not count toward `k of n`. A member that was
    removed while open is reported as `removed-open`, with its removal reason
    (new).
  - An abandoned member is reported `abandoned`, never complete (exists).
- **PRX-GRP-117** *`plan execute-group`* (new).

  ```
  praxis plan execute-group GROUP-ID --occurred-at TS [--member ID]
                            [--mode grouped|independent --reason TEXT]
                            [--dry-run] [--json] [IDENTITY]
  ```

  It is the only `plan` verb that mutates. It mutates only Praxis state, and
  only through existing transitions. Every other `plan` verb stays read-only
  (PRX-PLAN-001, PRX-GRP-090 case 20).
  1. It refuses with exit `1` when: the group is unknown; it has an
     unresolved dependency cycle; no member is runnable in this checkout
     (PRX-GRP-108); or the caller already owns an open group execution of
     another group.
  2. On first use it records a **group execution** (`GEX-<timestamp>-<suffix>`)
     in the group record. The record holds the group ID, actor, start time,
     execution repository, required member order, mode and its basis
     (PRX-GRP-130..132), and opt-outs.
  3. It begins the next runnable member in the required order, or
     `--member`, through the existing `work begin` transition. The member's
     execution is linked to the group execution (`groupExecution`), so one
     execution context carries the members one after another. Each member
     keeps its own execution (PRX-GRP-041).
  4. It never launches an agent, creates a branch, selects a provider or
     model, or changes priorities or dependencies (the PRX-PLAN non-goals).
  5. A group execution ends when every runnable member is terminal or
     removed, on fallback (PRX-GRP-136), or when its owner blocks it.
     Takeover uses `work continue` per member; the group execution records
     the successor.

## Grouped execution by default (`DF-ROS-2026-A053`, decision 3)

- **PRX-GRP-130** *Default.* For a group that qualifies (PRX-GRP-131), the
  planner's recommended execution is **grouped** (new): one execution
  context, one reasoning owner, members in the required order (PRX-GRP-063).
  `plan execute-group` uses grouped mode unless an opt-out applies
  (PRX-GRP-132). For a group that does not qualify, the recommendation is
  the existing advisory one (exists). `execute-group` then defaults to
  `independent`, and grouped mode needs `--mode grouped --reason TEXT`.
  The default takes effect only when the gates PRX-GRP-133..135 are
  implemented. Until then the planner keeps reporting grouping as advisory
  and says why.
- **PRX-GRP-131** *Qualification thresholds.* All configurable under
  `grouping.groupedExecution` (new). A group qualifies only when all of these
  hold:
  1. The group's affinity, as the planner computes it, is `high`
     (PRX-GRP-060), and no member pair is `none` or `unknown`.
  2. It has between `minimumSize` (default 2) and `maximumSize` (default 6)
     runnable members. The default ceiling is just above the five members the
     evidence covers. A larger group qualifies only by explicit `--mode
     grouped --reason`, and the oversized warning (PRX-GRP-030) still
     applies.
  3. Every runnable member executes in this checkout.
  4. No context-pressure observation limits the group below its size
     (existing rule, PRX-GRP-074).
  5. No member is still `captured`.

  The qualification and each reason a group fails it are shown by `plan
  groups` and `plan explain-group`.
- **PRX-GRP-132** *Opt-out with a recorded reason* (new). Two levels:
  - **Per group:** `work group create|add ... --execution-mode independent
    --reason TEXT`, or `execute-group --mode independent --reason TEXT`.
  - **Per item:** `--independent-member ID --reason TEXT`. The member stays
    a group member but executes in its own fresh context.

  A reason is required and non-empty. It is recorded with actor and time in
  the group history, and shown by `show`, `list` and `explain-group`. An
  opt-out applies only to executions not yet begun. It never rewrites
  history.
- **PRX-GRP-133** *Reuse-inventory gate* (changes PRX-GRP-040 from
  guidance). Before any member of a grouped-mode execution completes, the
  group execution MUST have a recorded **group analysis**: a committed
  `praxis.group-analysis/1` JSON document. The Markdown template stays the
  human-readable form. The document holds:
  - the group ID and group execution ID;
  - the member table, including each member's enumerated acceptance
    criteria;
  - `reuseInventory` entries `{element, location (path:line), disposition:
    reused|extended|not-reused, reason}`;
  - the `searches` that established the inventory, which are required
    when the inventory is empty;
  - `newAbstractions` entries `{name, consideredExisting, whyNotReused}`.

  It is supplied as `work complete --evidence group-analysis=PATH`, the
  `DF-ROS-2026-A052` evidence mechanism. The gate checks shape and
  consistency:
  - the document is valid and names this group and execution;
  - every new abstraction names the existing element it considered;
  - the document was committed in an ancestor of the member's first
    attributed commit, so the analysis came before the code.

  It does not judge whether the reuse choices were right.
- **PRX-GRP-134** *Per-member, per-criterion verification gate* (changes
  PRX-GRP-045 from guidance). A grouped-mode member completes only with
  `--evidence group-verification=PATH`. The document is a committed
  `praxis.group-verification/1` with one row per acceptance criterion of
  that member: `{member, criterion, status: met|partially-met|not-met|unknown,
  evidence: {kind: test|command|location, reference, result}}`. The gate
  refuses completion when any of these holds:
  - the member's criteria in the document differ from those enumerated in
    the group analysis;
  - any row is not `met`, unless it carries `deferredTo: WORK-ID`, naming a
    recorded, non-terminal work item that takes over that criterion;
  - a `location` reference names a path that does not exist at the
    completion commit.

  Evidence is an attestation checked for presence and consistency, as with
  `implementation` evidence. It is not proof.
- **PRX-GRP-135** *Gate behaviour.* The gates apply to members that execute
  in grouped mode. They report through the completion-readiness record as a
  `group-verified` facet. A refusal exits `3`, prints the refusal document
  and changes no state (`DF-ROS-2026-A052` semantics). The gates fail
  closed. Missing, malformed or inconsistent evidence is never a pass. They
  can be avoided only by opting the member out before its execution begins
  (PRX-GRP-132), never per completion. Members that execute independently
  complete under the normal policy.
- **PRX-GRP-136** *Fallback on context pressure* (new). During a grouped-mode
  execution, any context-pressure signal (PRX-GRP-074) recorded for the
  group execution causes `execute-group` to stop beginning further members
  in grouped mode. The signals come from telemetry (PRX-GRP-151) or an
  explicit `context-pressure` observation:
  - `context.compactions` at least `compactionLimit` (default 1);
  - `context.repeated_file_reads` above `repeatedReadLimit`;
  - an acceptance criterion found unmet after the member was reported
    finished;
  - elapsed time over 1.5 times the upper estimate.

  `execute-group` records `fallback: independent` with the signal and its
  evidence. Each remaining member is then recommended for a fresh independent
  execution, or the group is split (PRX-GRP-074). The fallback leaves members
  that are already complete or active unchanged. The active member completes
  or checkpoints in place. A fallback is never silent, and detection is
  deterministic from recorded data.
- **PRX-GRP-137** *Determinism and attribution kept.* Qualification, mode,
  opt-out and fallback decisions are deterministic for identical inputs
  (PRX-GRP-075). Grouped mode changes nothing in PRX-GRP-041..043: every
  member keeps its own executions, commits, evidence and checkpoints, and
  shared infrastructure is labelled and attributed as PRX-GRP-043 requires.
- **PRX-GRP-138** *Why the default moved, and when to revisit it.*
  - **Evidence.** Two executions of one high-affinity cohort
    (`EV-ROS-2026-A064`, `EV-ROS-2026-A070`) both produced the more coherent
    architecture. Grouped execution cost 56 % and 59 % less on the
    platform, and took about 2.6 times less elapsed time in R2. Independent
    executions were more faithful to individual criteria and reused more,
    which is why PRX-GRP-133..135 are hard gates. The evidence is
    low-confidence, from one cohort and one repository, so the default is
    bounded by PRX-GRP-131 and measured continuously.
  - **Continued measurement.** Every group execution and every independent
    execution of a qualifying group records PRX-GRP-150..158. The planner
    reports, separately for grouped and for independent executions:
    - cost per member and active time per member;
    - the rate at which members pass the verification gate on the first
      attempt;
    - context-pressure fallbacks;
    - defects or rework items linked to a member within 30 days of its
      completion.
  - **Review trigger.** An evidence record and a decision review are due
    after the first 10 grouped-mode executions, or 90 days after the default
    takes effect, whichever comes first (`PRAXIS-PLAN-11`).
  - **Rollback trigger.** The default reverts to advisory if any one of
    these holds:
    1. With at least 5 comparable samples each, the median cost per member
       of grouped executions is at least that of independent ones.
    2. The grouped first-pass verification failure rate exceeds the
       independent rate by more than 10 percentage points.
    3. More than one third of group executions fall back for context
       pressure.
    4. A confirmed attribution-laundering incident (PRX-GRP-043) is caused by
       grouped mode.

    Rollback is a configuration change (`grouping.groupedExecution.default:
    advisory`) recorded by a decision record. It is not a code removal.

## Context-reuse and cost measurement (`DF-ROS-2026-A053`, decision 4)

- **PRX-GRP-150** *Adapter-sourced context metrics.* The context metrics are
  collected by telemetry adapters, not by ad-hoc scripts. The
  `anthropic-claude-session` adapter already does this (exists,
  `PRAXIS-PLAN-05`, `docs/development-telemetry.md`). The metric IDs below
  are provider-neutral (new). Any adapter (`openai-codex`,
  `google-gemini-otel`, `anthropic-claude-otel`, `generic`) MAY supply them
  and MUST declare its capability for each. A metric an adapter cannot
  observe is `supported-unavailable` or `unsupported`, never `0`.
- **PRX-GRP-151** *Per-execution context-overhead metrics.*
  - Existing (exists for the Claude session adapter): `context.repeated_file_reads`,
    `context.governance_reads`, `context.compactions`,
    `time.first_code_change_ms`, `tokens.cache_read`, `tokens.cache_write`,
    `model.requests`, `tool.file_reads` and `tool.searches`.
  - New: `time.first_productive_change_ms`, measured against the
    repository's configured meaningful paths instead of the hard-coded
    `src/` and `tests/`, with `time.first_code_change_ms` kept as an alias
    where they coincide.
  - New: `context.peak_tokens` and `context.window_tokens` when the runtime
    exposes them.
  - New: the count of distinct files read more than once, alongside the
    total of repeated reads.

  Every metric keeps its quality (`observed`, `derived`, `estimated`) and
  source.
- **PRX-GRP-152** *Cost recorded.* Platform-reported cost is recorded as
  `cost.execution_total` (exists: `telemetry record`). Where a runtime
  surface exposes the platform total, an adapter SHOULD record it without a
  manual step (new). Completing a member of a group execution without a
  `cost.execution_total` and without a recorded capability state saying why
  cost is unavailable produces a completion **warning**, not a refusal (new).
- **PRX-GRP-153** *Group-execution telemetry* (new). A session that carries
  a group execution is ingested once, into the group execution, rather than
  into one member's execution. This replaces, for grouped work, the current
  rule "ingest into one of their executions". Its snapshot ID guarantees
  that neither the group execution nor any member execution counts it twice.
  Member executions keep any telemetry that is genuinely theirs.
- **PRX-GRP-154** *Attribution of shared cost* (new). Shared group-execution
  totals (cost, tokens, time, context metrics) are apportioned to members
  deterministically, by a recorded method:
  1. **Direct.** Usage inside a member's execution window (from its
     `work begin` to its completion, block or the next member's begin) is
     that member's, with `observed` or `derived` quality.
  2. **Group-shared.** The remainder (the analysis before the first member,
     shared infrastructure, gaps) is a separate `group-shared` line.
  3. **Allocated.** For per-member totals only, the group-shared line is
     allocated `equal-share` by default, labelled quality `allocated`, and
     never reported as observed.

  Allocations sum exactly to the group total, and rounding residue stays on
  the group-shared line. Changing the method recomputes the allocations and
  never rewrites raw data.
- **PRX-GRP-155** *Planner consumption* (changes the cost model of
  PRX-GRP-061 from counted to priced).
  - The cold start is the median `time.first_productive_change_ms` (and
    cost) of independent executions.
  - The per-member incremental cost comes from the direct segments of group
    executions.
  - Cost per member comes from `cost.execution_total` and PRX-GRP-154
    allocations.

  Each figure uses the existing sample thresholds and confidence tiers (at
  least 3 samples, else unknown). `plan groups`, `explain-group` and
  `compare --groups` report predicted grouped-versus-independent cost and
  time with their basis and sample counts. They never assume savings.
- **PRX-GRP-156** *Unknowns stay unknown.* A missing metric is unknown, not
  zero. The allocation of an unknown total is unknown. Currencies are never
  summed across. Every aggregate states its coverage ("N of M executions
  carry `cost.execution_total`").
- **PRX-GRP-157** *Privacy.* Raw snapshots stay content-free (exists for the
  session adapter). Group-execution telemetry adds no prompt, message, tool
  input or output, or command text.
- **PRX-GRP-158** *Predicted against observed.* For each group execution,
  the planner's frozen prediction at `execute-group` time (mode, members,
  cost and time ranges) is stored with it and compared with the observed
  outcome at its end (new; PRX-PLAN-150..152 style). These comparisons are
  the input to the PRX-GRP-138 review.

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
- **PRX-GRP-190** (`DF-ROS-2026-A053`) Tests MUST also cover: (21) a
  cross-repository group requires the `GROUP-ECHELON-` prefix and qualified
  members, and a local group may not use it; (22) group status is derived
  from member observations and no command can set it; (23) an unobservable
  or stale member is `unknown` or `stale`, never complete; (24) a member
  repository's group reference and the home's membership hold no copy of
  each other's mutable state, and a mismatch is reported `unlinked`; (25)
  cross-repository `complete`/`merged`/`released` edges order members and
  cross-repository cycles are detected; (26) no command writes another
  repository's state or schedules its member into this checkout; (27)
  `work group list` is read-only, sorted and filterable; (28) every group
  command's JSON round-trips, and a version-1 store reads unchanged; (29)
  repeated identical mutations are idempotent (`changed: false`, no history)
  and conflicting ones are refused; (30) history is append-only and a
  rewritten history fails `validate`; (31) no group command, including
  `execute-group`, alters a member's lifecycle, evidence, checkpoints,
  telemetry or attribution beyond what `work begin` itself writes; (32)
  there is no group completion transition and removed-open members are
  reported; (33) `execute-group` begins members only in required order,
  only in this checkout, never launches anything, and is idempotent per
  member; (34) qualification thresholds select grouped mode and each failed
  threshold is explained; (35) per-group and per-item opt-outs require a
  reason and are honoured; (36) a grouped-mode member without valid
  `group-analysis` and `group-verification` evidence is refused, a non-`met`
  criterion without `deferredTo` is refused, and an analysis committed after
  the member's first change is refused; (37) an opted-out member completes
  without the group gates; (38) context-pressure signals trigger a recorded
  fallback without changing completed members; (39) qualification, mode and
  fallback are deterministic; (40) metrics an adapter cannot observe are
  unavailable, not zero; (41) a shared session is ingested once and never
  double-counted; (42) shared-cost allocations sum exactly to the group
  total and are labelled `allocated`; (43) the planner prices grouping only
  from at least three measured samples and otherwise reports unknown; (44)
  the default reverts to advisory by configuration alone.
