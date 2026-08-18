ROS Upgrade Agent Prompt — Repository Work Queue

You are upgrading the existing Repository Operating System (ROS) implementation.

Your task is to inspect the current ROS repository, understand the existing architecture and conventions, and then implement a simpler repository-local work execution model.

The goal is to separate repository execution work from project administration.

Do not create a second project-management system inside ROS.

ROS should answer a much simpler question:

What work exists in this repository, and what can a human or agent execute next?

Core Design Principle

Each repository should have one authoritative master work queue.

The repository itself is the source of truth for executable repository work.

The work queue must not depend on:

* a central project-administration repository
* Jira
* GitHub Projects
* external project-management systems
* another ROS repository
* organization-wide planning infrastructure

A repository should remain fully understandable and executable after being cloned independently.

A developer or agent should be able to enter the repository and run:

ros work

or its equivalent and immediately determine what work exists.

⸻

1. Inspect the Existing Implementation First

Before changing code:

1. Inspect the current ROS repository structure.
2. Identify:
    * work-item implementations
    * project administration concepts
    * CLI commands
    * daemon behavior
    * repository initialization behavior
    * schemas
    * state models
    * work directories
    * agent workflows
    * documentation
    * tests
3. Determine which existing pieces can be reused.
4. Avoid unnecessary rewrites.
5. Preserve compatibility where doing so does not undermine the new model.

Document any important architectural conflict you discover.

Do not assume the current implementation matches this specification.

The specification below is authoritative for the upgrade.

⸻

2. Separate Repository Work From Project Administration

ROS must distinguish between:

Repository work

Work that can be executed against this repository.

Examples:

* implement a feature
* fix a defect
* investigate behavior
* perform research
* migrate code
* remove obsolete code
* update documentation
* address a failed test
* resolve an ambiguity
* perform architectural work
* complete a required follow-up

Project administration

Examples:

* roadmap management
* portfolio planning
* cross-repository initiatives
* executive reporting
* organizational coordination
* staffing
* budgeting
* project schedules

These are not the same concern.

Repository execution must not require the project-administration system.

A project-administration system may reference or create repository work items, but repository work must remain independently usable.

Conceptually:

ORGANIZATION / PROJECT ADMINISTRATION
       initiatives
       roadmaps
       coordination
            |
            v
——————————————
          REPOSITORY BOUNDARY
——————————————
        ROS WORK QUEUE
              |
      +-——+-——+
      |       |       |
    human   agent   daemon
      |       |       |
      +-——+-——+
              |
          execution
              |
          evidence
              |
         transitions
              |
             done

Preserve this architectural boundary.

⸻

3. Create One Master Work Queue Per Repository

Each ROS-enabled repository should expose one authoritative work queue.

Prefer a structure conceptually similar to:

repo/
  .ros/
    work/
      work.md
      items/
    evidence/
    decisions/

Do not blindly create this exact structure if the existing ROS implementation already has an equivalent canonical location.

Adapt the current implementation while preserving the semantic model.

The important rule is:

There is one logical work queue per repository.

Do not create separate primary queues such as:

bugs/
features/
research/
architecture/
technical-debt/
ideas/

Those are classifications, not separate work systems.

⸻

4. Use Tags for Classification

A work item may belong to many conceptual categories.

Use tags instead of hierarchical work buckets.

Example:

WI-0045
title: Determine whether HTML can own routing
tags:
  - wasm
  - routing
  - research

Tags may describe:

* subsystem
* technical area
* work type
* domain
* concern
* experiment
* migration
* research topic

Examples:

wasm
routing
state
performance
research
frontend
cleanup
security
architecture
migration
documentation

Tags must not determine lifecycle state.

⸻

5. State and Tags Have Different Meanings

Keep these concepts separate.

State answers:

Can something act on this item, and where is it in execution?

Tags answer:

What kind of work is this?

Do not encode workflow state as tags.

Do not create states merely to represent categories.

For example, research should normally be a tag rather than a lifecycle state.

⸻

6. Keep the Work-Item Lifecycle Small

Start with the smallest useful lifecycle.

Recommended baseline:

captured
ready
active
done

Exceptional states:

blocked
abandoned

Do not add states merely because conventional project-management software has them.

Avoid states such as:

backlog
selected-for-development
in-review
qa
accepted
scheduled
planned
grooming

unless the existing ROS state model demonstrates that one of these represents a genuinely necessary semantic distinction.

Every state must have a clear execution meaning.

Prefer explicit legal transitions.

For example:

captured -> ready
captured -> abandoned
ready -> active
ready -> blocked
ready -> abandoned
active -> done
active -> blocked
active -> abandoned
blocked -> ready
blocked -> abandoned

If reopening completed work is supported, define that transition deliberately rather than allowing arbitrary mutation.

⸻

7. Adding Work Must Be Extremely Cheap

This is one of the most important requirements.

Adding a work item must require very little ceremony.

A human should be able to execute something approximately equivalent to:

ros work add “Add WASM navigation transition”

Optional metadata:

ros work add \
  “Add WASM navigation transition” \
  —tag wasm \
  —tag routing \
  —priority high

A shorter capture form may also be useful:

ros add “Investigate large state payloads” -t wasm,state,performance

Determine whether an existing ROS CLI naming convention should be preserved.

The semantic requirement matters more than the exact command syntax.

Do not require users to manually:

* generate IDs
* create directories
* create YAML
* choose workflow files
* create metadata documents
* register the item elsewhere
* update multiple indexes

The system should do this automatically.

Capture must be cheap enough that humans and agents use it instead of bypassing ROS.

⸻

8. Generate Stable Work IDs

Each work item needs a stable identifier.

For example:

WI-0042
WI-0043
WI-0044

Use an existing repository ID convention if one already exists and is sound.

Requirements:

* IDs must be unique within the repository.
* IDs must not change when titles change.
* Humans should be able to reference them easily.
* Agents should be able to reference them deterministically.
* IDs should work in filenames when detail files exist.

Avoid unnecessary globally distributed ID complexity unless ROS already requires it.

⸻

9. Support Lightweight Work Items

Do not force every work item to become its own document.

A simple item should be representable directly in the master queue.

Conceptually:

WI-0046 | Rename WasmStateStore | ready | cleanup,wasm

or equivalent structured data.

The master work list might render as:

# Work
| ID | Work | Status | Tags | Priority |
|—|—|—|—|—|
| WI-0042 | Add WASM navigation transition | ready | wasm, routing | high |
| WI-0043 | Investigate state serialization size | captured | wasm, state, performance | medium |
| WI-0044 | Remove obsolete JS state handler | blocked | frontend, cleanup | low |

Do not assume Markdown must be the canonical storage representation.

If the existing ROS architecture has a better structured canonical representation, retain it and generate the human-readable master list.

The important properties are:

* one authoritative logical queue
* human readability
* deterministic machine access
* easy editing through commands
* no duplicated truth

⸻

10. Allow Detail Files Only When Needed

Complexity should be paid only when complexity exists.

A trivial work item should remain trivial.

When an item needs additional context, ROS should allow or generate a detail record such as:

.ros/work/items/WI-0046.md

Example:

# WI-0046 — Rename WasmStateStore
## Objective
Rename the state abstraction without changing its behavior.
## Constraints
- No new dependencies.
- Public WASM boundary must remain unchanged.
- Existing tests must continue to pass.
## Evidence
- src/state/WasmStateStore.ts
- Decision D-0017
## Completion
- New name used throughout repository.
- No references to old name remain.
- Tests pass.

Useful detail sections may include:

* objective
* context
* constraints
* evidence
* dependencies
* blocking reason
* completion criteria
* related decisions
* discovered obligations

Do not require all sections for every item.

⸻

11. Provide Useful Work Queries

The CLI must make the master queue easy to inspect.

Support the equivalent of:

ros work list

Filtering:

ros work list —tag wasm
ros work list —tag research
ros work list —status ready
ros work list —tag wasm —status ready

Also support direct retrieval where appropriate:

ros work show WI-0042

The exact command syntax may follow existing ROS conventions.

Filtering must operate over the one logical queue rather than separate physical queues.

⸻

12. Optimize for Agent Execution

Agents should be able to ask ROS:

What am I legally able to work on now?

Provide a deterministic way to retrieve executable work.

For example:

ros work list —status ready

or a dedicated command such as:

ros work ready

Do not make agents infer readiness from prose.

The execution daemon should be able to consume this same model.

Avoid creating a separate daemon-specific work representation.

Humans, CLI tools, agents, and daemon execution should operate on the same semantic work system.

⸻

13. Model Work as Outstanding Repository Obligations

Conceptually, the ROS work queue is more than a conventional TODO list.

It represents unresolved obligations created by the repository and its operation.

Think in terms of:

repository current state
        +
desired changes
        +
unresolved discoveries
        +
failed validation
        +
required follow-up
        |
        v
     WORK QUEUE

Work can therefore originate from many sources.

Examples:

human idea
    -> work item
test failure
    -> work item
agent discovers ambiguity
    -> work item
architecture decision requires migration
    -> work item
review discovers problem
    -> work item
external dependency prevents execution
    -> blocked work item
research discovers required follow-up
    -> work item

Design the implementation so future ROS components can create work items programmatically.

Do not hard-code work creation solely to human CLI input.

⸻

14. Preserve Provenance

When ROS itself, an agent, a daemon, validation, or another subsystem creates a work item, preserve enough provenance to understand why it exists.

Possible metadata:

created_by
created_at
source
source_reference

Examples:

source: test-failure
source_reference: tests/navigation.spec.ts

or:

source: agent-discovery
source_reference: execution-2026-08-18-0042

Do not burden manual work capture with required provenance beyond what is useful.

Defaults should make normal use effortless.

⸻

15. Blocking Should Be Explicit

A blocked work item should contain a reason.

Prefer something semantically similar to:

status: blocked
blocked_reason: Waiting for browser compatibility experiment

If the existing ROS architecture supports dependencies, reuse them where useful.

Do not require a full dependency graph merely to mark something blocked.

⸻

16. Do Not Turn Priority Into Workflow

Priority may exist as metadata.

For example:

high
medium
low

or the repository’s existing priority system.

Priority should influence selection.

Priority should not determine legality.

A low-priority ready item is still ready.

A high-priority blocked item is still blocked.

⸻

17. Preserve State-System Discipline

ROS architecture should continue to follow explicit state-system principles.

Where practical:

* make states explicit
* make legal transitions explicit
* reject invalid transitions
* preserve transition history
* distinguish classification from state
* distinguish evidence from assertion
* expose legal actions instead of unconstrained mutation

Avoid APIs equivalent to:

updateWorkItem(anything)

Prefer intent-revealing operations such as:

captureWork()
markReady()
startWork()
blockWork()
unblockWork()
completeWork()
abandonWork()

Names should follow the repository’s implementation language and conventions.

The underlying principle is constrained transition rather than arbitrary record mutation.

⸻

18. Keep External Dependencies Minimal

Do not introduce new libraries unless they are genuinely necessary.

Prefer:

* standard library
* platform capabilities
* existing ROS dependencies
* small native implementations

over adding dependencies for convenience.

If a dependency is proposed, document:

1. what capability it provides
2. why existing code or platform functionality is insufficient
3. maintenance cost
4. runtime implications
5. whether it expands the trusted dependency surface

Do not add a project-management framework or database merely to support this feature.

⸻

19. Repository Initialization

Update ROS repository initialization so new repositories automatically receive the work system.

For example, whatever command currently performs repository setup should establish:

* canonical work storage
* empty master queue
* item-detail location if required
* schema/version metadata if required

Initialization should not require connection to a central administration repository.

Existing repositories must have a safe upgrade path.

⸻

20. Migration

Inspect the current ROS implementation for existing work items.

Create a migration strategy.

Where possible:

* preserve existing IDs
* preserve history
* preserve useful metadata
* map old categories to tags
* map old statuses to the new state model
* avoid losing evidence or references

If an existing concept cannot map cleanly, document the conflict.

Do not silently discard data.

If migration can be automated safely, implement it.

⸻

21. CLI Experience

Optimize commands for frequent use.

The normal workflow should feel approximately like:

ros add “Investigate WASM state payload growth” -t wasm,state
ros work
ros work ready
ros work show WI-0042
ros work start WI-0042
ros work block WI-0042 —reason “Need benchmark results”
ros work ready WI-0042
ros work done WI-0042

These are examples, not mandatory syntax.

Fit the implementation into the existing ROS command grammar where possible.

The important property is that common actions require very little typing and are obvious without documentation.

⸻

22. Agent Interface

Inspect how current ROS agents consume repository context.

Update agent startup/context generation so an agent can cheaply learn:

* current ready work
* currently active work
* blocked work
* relevant tags
* detail for the selected item
* constraints
* completion criteria
* relevant evidence

Do not inject the entire history of all work items into every agent context.

Retrieve only what is necessary.

This should reduce unnecessary token use and semantic noise.

⸻

23. Daemon Integration

If the ROS execution daemon already exists, update it to consume the repository work model.

The daemon should not own a separate task database unless there is a strong technical reason.

Prefer:

repository work queue
        |
        v
execution policy
        |
        v
eligible work
        |
        v
agent/model selection
        |
        v
execution
        |
        v
evidence + work transition

The daemon should request or perform legal work-item transitions.

It should not arbitrarily edit status values.

⸻

24. Completion Should Produce Evidence

Completing work should support recording evidence.

Possible evidence:

* commit
* changed files
* passing tests
* research document
* benchmark
* decision
* generated artifact
* review result

Do not require heavyweight evidence for trivial items.

But the architecture should make evidence attachable and traceable.

Where appropriate:

active
   |
execution
   |
evidence
   |
completion guard
   |
done

This aligns repository work with ROS’s broader state-constrained architecture.

⸻

25. Avoid Duplicate Sources of Truth

Be especially careful here.

Do not maintain:

work.md
+
work.json
+
database
+
daemon queue
+
project administration record

as independently editable representations of the same work.

Choose one canonical representation.

Everything else should be:

* generated
* indexed
* cached
* projected
* referenced

Never independently authoritative.

Document which representation is canonical.

⸻

26. Human-Readable Repository State

A person browsing the repository without specialized tooling should still be able to understand the outstanding work.

If canonical storage is structured data, provide a generated or easily readable projection.

Do not make ROS require a proprietary UI to understand repository state.

⸻

27. Tests

Add tests covering at minimum:

Creation

* create minimal work item
* automatically generate ID
* add one tag
* add multiple tags
* optional priority
* optional detail

Querying

* list all
* filter by state
* filter by tag
* combined filters
* retrieve specific ID

State transitions

* captured -> ready
* ready -> active
* active -> done
* ready -> blocked
* active -> blocked
* blocked -> ready
* abandonment
* rejection of illegal transitions

Persistence

* work survives process restart
* IDs remain stable
* detail references remain stable

Migration

* existing work converts correctly
* metadata is not silently lost

Agent/daemon integration

Where implemented:

* only executable work is returned
* blocked work is not selected
* transition guards are enforced

⸻

28. Documentation

Update ROS documentation to explain the new conceptual distinction:

ROS repository work is not project administration.

Explain:

* one work queue per repository
* cheap capture
* tags
* states
* detail files
* blocking
* agent execution
* daemon execution
* evidence
* project-administration boundary

Include practical command examples.

Keep the explanation concise enough that a new developer can understand the model quickly.

⸻

29. Backward Compatibility

Do not preserve legacy behavior merely because it exists.

However, avoid unnecessary breaking changes.

For each breaking change:

1. identify the old behavior
2. explain why it conflicts with the new architecture
3. determine whether a compatibility layer is cheap and safe
4. migrate where reasonable
5. remove obsolete concepts when they create semantic duplication

Prefer architectural clarity over indefinite compatibility with a flawed model.

⸻

30. Implementation Order

Use approximately this sequence:

1. Inspect existing ROS architecture.
2. Identify current work and project-administration coupling.
3. Define the canonical repository work representation.
4. Define work-item state and transition rules.
5. Implement persistence.
6. Implement work creation.
7. Implement listing and filtering.
8. Implement transition commands.
9. Implement optional detail records.
10. Implement migration.
11. Update repository initialization.
12. Update agent integration.
13. Update daemon integration.
14. Add tests.
15. Update documentation.
16. Remove obsolete or duplicated work-management paths.

Adjust the order if the repository architecture requires it.

⸻

31. Do Not Overbuild

Explicitly avoid turning this into:

* Jira
* Linear
* GitHub Projects
* a generalized workflow engine
* a portfolio management system
* a planning database
* a dependency graph platform
* a scheduling engine
* a ticketing system

ROS needs enough work management to coordinate repository execution.

Nothing more should be added without demonstrated need.

The target is:

capture
   |
classify
   |
determine executability
   |
execute
   |
record evidence
   |
transition

⸻

32. Architectural Success Criteria

The implementation is successful when all of these statements are true:

* Every ROS repository has one logical work queue.
* A work item can be captured in seconds.
* Humans do not manually create IDs or metadata files.
* Tags provide flexible classification.
* Lifecycle state remains small and meaningful.
* State transitions are explicit and constrained.
* Simple work remains simple.
* Complex work can carry additional context.
* Agents can deterministically find executable work.
* The daemon can consume the same work model.
* Blocked work is explicit.
* Evidence can be associated with execution.
* Repository work does not depend on project administration.
* Cloning the repository is enough to understand its executable work.
* There is one canonical source of truth.
* No unnecessary external dependency is introduced.
* Existing ROS functionality is preserved where it remains semantically appropriate.

⸻

33. Final Deliverables

After implementation, produce a concise implementation report containing:

Existing Architecture

What existed before the upgrade.

Changes Made

Files, modules, commands, schemas, and concepts changed.

Canonical Work Model

State explicitly where the source of truth now lives.

Lifecycle

List states and legal transitions.

CLI

Show the supported commands with examples.

Agent Integration

Explain how agents discover and act on repository work.

Daemon Integration

Explain how repository work enters execution.

Migration

Explain how old work data was handled.

Removed Concepts

Identify obsolete project-administration or duplicate work-management concepts removed or deprecated.

Tests

List tests added and their results.

Remaining Questions

Identify genuine unresolved architectural issues only.

Do not invent future work simply to populate this section.

⸻

Guiding Principle

The Repository Operating System is not responsible for administering projects.

It is responsible for making the repository executable.

The repository should expose its unresolved obligations, the legal work that can be performed, the evidence associated with that work, and the transitions that move it toward a resolved state.

Keep that distinction intact throughout the implementation.