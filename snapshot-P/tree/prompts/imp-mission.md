Repository Operating System — Work Protocol Implementation Mission

You are acting as a principal software architect, developer-tooling engineer, AI-agent systems engineer, and skeptical maintainer.

Your task is to extend the existing Repository Operating System (ROS) so that any software repository can adopt ROS and immediately participate in a consistent, machine-readable work protocol for humans and AI agents.

Do not redesign the repository unnecessarily.

First inspect the existing Repository Operating System repository carefully:

* repository structure
* documentation
* existing conventions
* agent instructions
* workflows
* scripts
* schemas
* configuration
* tests
* architectural decisions
* naming conventions

Preserve good existing structure.

Prefer the smallest coherent implementation that fits the current repository.

Architectural Boundary

Repository Operating System is NOT the project-management datastore.

Do not turn ROS into a centralized issue tracker, Kanban database, portfolio database, or project-management application.

There must be clear segregation of duties.

Repository Operating System owns

* protocol definitions
* schemas
* legal work-state transitions
* validation
* agent operating rules
* repository integration
* CLI/tooling required to follow the protocol
* Git/CI enforcement
* adapter contracts
* event/publication contracts
* work-item attribution rules
* evidence requirements
* versioning of the ROS protocol

Individual repositories own

* their source code
* documentation
* research artifacts
* tests
* evidence
* repository-specific configuration
* repository-specific process mappings
* the relationship between changes and work items
* local records necessary to reconstruct repository activity

Separate Project Management system owns

* canonical work-item records
* projects
* portfolio views
* Kanban boards
* assignments
* priorities
* scheduling
* cross-repository aggregation
* project-management queries
* user-facing global work status

ROS must communicate with that system through an explicit adapter or protocol boundary.

Do not hard-code ROS to GitHub Projects, Jira, Linear, Azure DevOps, or another project-management vendor.

The initial implementation should remain provider-agnostic.

⸻

Core Principle

Every meaningful repository mutation must be attributable to a work item.

The foundational invariant is:

No meaningful mutation without attributable intent.

A work item establishes that intent.

Examples include:

* feature
* bug
* research
* technical debt
* maintenance
* incident
* decision
* obligation
* documentation work
* infrastructure work
* security remediation

The work-item system itself is external to ROS.

ROS references work items through stable identifiers.

Example:

FEAT-142
BUG-031
RES-017
OBL-009

ROS must not need to know which project-management product stores them.

⸻

Required Architecture

Implement a provider-agnostic work protocol approximately following:

Project Management Store
        │
        │ work-item identity/state
        ▼
Repository OS protocol
        │
        │ provides rules/capabilities
        ▼
Individual Repository
        │
        │ implementation/evidence
        ▼
ROS validation
        │
        │ validated state/event
        ▼
Project Management Adapter
        │
        ▼
Project Management Store

The central project-management store is authoritative for project-management state.

The individual repository is authoritative for its implementation and repository evidence.

ROS is authoritative for the protocol governing the relationship between the two.

⸻

Work Context

Agents and humans should establish work context before performing meaningful repository mutations.

Design a command or equivalent capability such as:

ros work begin FEAT-142

This should establish explicit repository work context.

Possible implementation approaches include:

.ros/context/current.json

or another simple mechanism appropriate to the existing ROS architecture.

Do not over-engineer this.

The work context should minimally identify:

* work-item ID
* repository
* actor/session if available
* start timestamp if useful
* ROS protocol version

The implementation must support multiple work items where legitimately necessary.

Example:

ros work begin FEAT-142 OBL-009

Do not silently infer work-item relationships solely from file changes or commit messages.

⸻

Work Completion

Provide an explicit completion capability.

Conceptually:

ros work complete FEAT-142

Completion should not merely set a flag.

ROS should validate whatever evidence the repository’s configured process requires.

Examples might include:

* implementation exists
* tests pass
* required documentation exists
* required research artifact exists
* required review occurred
* acceptance evidence exists

Not every repository or work type requires identical evidence.

Design the protocol so evidence requirements can evolve without redesigning the system.

⸻

Other Required Transitions

Support a minimal useful set of work transitions such as:

begin
complete
block
resume

Potential future transitions may include:

request-review
approve
reject
abandon
reopen
supersede

Do not implement every possible workflow now unless the existing architecture makes it trivial.

Favor the smallest stable semantic core.

⸻

Repository-Specific Process

Each repository may have its own development process.

Examples:

Software repository:

backlog
ready
development
review
complete

Research repository:

proposed
open
investigating
peer_review
completed

ROS must not require every repository to use the same local workflow.

However, ROS should support common semantic categories where useful:

backlog
ready
active
review
blocked
complete

Example:

local state: investigating
semantic state: active

This allows aggregation without forcing identical processes.

Design this mapping as configuration, not hard-coded logic.

⸻

Work Types

Do not build separate systems for software and research.

Use a general work-item protocol.

Initial recognized categories may include:

feature
bug
research
technical-debt
task
decision
obligation
incident
maintenance
documentation

The architecture should allow additional types without modifying core logic whenever possible.

Different work types may have different:

* legal transitions
* completion requirements
* evidence requirements
* metadata

⸻

Research

Research is one work-item type, not a separate subsystem.

Research completion and research conclusion are separate concepts.

For example:

state: completed
conclusion: inconclusive

must be valid.

Possible research conclusions:

supported
supported-with-caveats
unsupported
contradicted
inconclusive
not-applicable

Do not encode a completed research item as necessarily having validated its original hypothesis.

⸻

Work Attribution

Every meaningful committed update must be attributable to at least one work item.

ROS validation should be capable of detecting an unattributed change.

Do not rely exclusively on commit-message conventions.

Provide a machine-readable association.

Determine the simplest durable representation compatible with the existing repository.

Possible example:

work_items:
  - FEAT-142

The exact representation should be chosen after inspecting the existing ROS design.

The system should eventually allow navigation in both directions:

commit
→ work item
→ acceptance criteria
→ evidence
→ decision/research

and:

work item
→ commits
→ files
→ tests
→ reviews
→ releases

Do not attempt to implement the entire graph in this mission.

Design the identifiers and records so that this remains possible.

⸻

Meaningful Changes

Do not require noisy ROS records for every trivial operation.

Differentiate repository mutations from semantic work transitions.

Examples that normally DO require work attribution:

* production code changes
* test behavior changes
* schema changes
* public API changes
* infrastructure changes
* research artifacts
* architectural documentation changes
* policy changes
* dependency changes affecting behavior
* configuration changes affecting runtime behavior

Examples that may be treated as system/mechanical operations:

* generated files
* deterministic formatting
* automated metadata refresh
* repository housekeeping

Even mechanical operations should have a known attribution category rather than becoming unexplained mutations.

Do not create hundreds of meaningless work items merely to satisfy the protocol.

⸻

Agent Behavior

Agents must not merely be told:

Remember to update progress when finished.

Instead, ROS participation must become part of the normal agent execution path.

Provide canonical repository instructions telling agents to:

1. identify the work item before meaningful mutation
2. establish ROS work context
3. inspect allowed operations
4. perform the work
5. gather evidence
6. request a legal ROS transition
7. validate repository consistency
8. commit changes with work attribution

Agents should interact through constrained commands/capabilities rather than hand-editing ROS state wherever practical.

The intended model is:

ROS exposes legal operations
        ↓
agent selects one
        ↓
ROS validates
        ↓
state changes

not:

agent edits arbitrary YAML

⸻

Agent Context

Design a command or interface that can eventually return a constrained work context.

Conceptually:

ros work context FEAT-142

Example conceptual output:

{
  “workItem”: “FEAT-142”,
  “repository”: “example”,
  “state”: “ready”,
  “allowedActions”: [
    “begin”,
    “block”
  ],
  “requiredEvidenceForCompletion”: [
    “implementation”,
    “tests”
  ]
}

Do not require the project-management integration to be implemented completely before the local protocol works.

Use interfaces/adapters where necessary.

⸻

Git and CI Enforcement

Agent compliance must not rely solely upon prompts.

Implement or prepare deterministic validation suitable for CI.

The validation system should be capable of rejecting changes such as:

meaningful repository mutation
+
no work-item attribution

or:

work item marked complete
+
required repository evidence missing

Local hooks may provide convenience, but they are not the ultimate enforcement mechanism.

CI should be the authoritative repository enforcement boundary.

Design validation so repositories can call a shared ROS workflow rather than copy substantial logic.

⸻

Reusable Repository Integration

The goal is for another repository to adopt ROS with minimal setup.

Prefer an adoption model roughly like:

1. add ROS configuration
2. add minimal workflow/caller
3. add canonical agent instruction reference
4. initialize repository process

The repository should consume versioned ROS behavior rather than copy large scripts.

Design for:

ROS v1
ROS v1.1
ROS v2

Repositories must be able to pin the ROS protocol version they use.

Do not introduce silent behavioral changes across all repositories.

⸻

Project Management Adapter Boundary

Define an interface for an external project-management datastore.

ROS must not depend upon a particular implementation.

Conceptually the boundary may eventually expose operations such as:

getWorkItem(id)
listWorkItems(...)
transitionWorkItem(id, transition, evidence)
publishRepositoryEvent(...)

The actual interface should be designed based on the existing repository architecture.

Keep it small.

The first implementation may use a local/mock/file-backed adapter for testing.

Do not build the project-management application itself in this mission.

⸻

Segregation of Duties

Preserve this invariant:

ROS
does not own project-management truth.
Project-management system
does not determine repository evidence truth.
Individual repo
does not redefine the ROS protocol.

Each layer must have explicit authority.

Where two systems disagree, the system should expose the inconsistency rather than silently overwrite one with the other.

⸻

Security and Trust

Treat external state updates as consequential operations.

Do not allow arbitrary repository code to silently modify central project-management state without validation.

Consider:

* authentication
* authorization
* repository identity
* work-item identity
* replay/idempotency
* duplicate events
* failed updates
* unknown outcomes
* version mismatch
* retries

Do not build an elaborate distributed system prematurely, but ensure the protocol does not assume remote writes always succeed.

External effects should support at minimum:

success
failure
unknown

Do not turn an unknown remote outcome into presumed success.

⸻

Idempotency

Repository workflows may retry.

Design publication/update operations to be idempotent.

A repeated processing of the same validated repository event must not create duplicate work transitions.

Use durable identifiers where appropriate.

⸻

Events

Evaluate whether a small immutable semantic event record is useful for repository history.

Possible examples:

work.started
work.completed
work.blocked
work.resumed

Do not create event sourcing for its own sake.

If events are used:

* keep them small
* make them machine-readable
* make them append-only where practical
* tie them to work items
* tie them to commits when appropriate
* version their schema

Current/projected state should be derivable where reasonably possible.

⸻

Configuration

Provide a minimal repository configuration format.

It should be capable of expressing concepts such as:

ros:
  version: 1
repository:
  type: application
workflow:
  states:
    - backlog
    - ready
    - development
    - review
    - complete
semantic_mapping:
  development: active
  review: review
  complete: complete

This is illustrative only.

Inspect existing conventions before choosing the actual schema.

Avoid configuration that duplicates information already available reliably from the repository.

⸻

Minimal Dependencies

Prefer standard platform capabilities and existing repository dependencies.

Do not add libraries unless they materially simplify a hard problem.

Avoid adopting frameworks merely to reduce a small amount of implementation effort.

Favor code that is understandable, deterministic, testable, and replaceable.

⸻

Deliverables

Produce the implementation and documentation necessary to establish the first stable version of this protocol.

At minimum:

1. documented architecture
2. explicit authority boundaries
3. work-item attribution model
4. repository configuration schema
5. work-context mechanism
6. legal transition mechanism
7. validation mechanism
8. reusable CI integration
9. agent instructions
10. project-management adapter interface
11. tests
12. example consuming repository
13. migration/adoption instructions
14. protocol versioning strategy
15. description of deferred capabilities

⸻

Testing

Test at least these cases:

Valid

work item established
→ code changed
→ required evidence present
→ completion requested
→ validation passes

Missing attribution

meaningful code changed
→ no work item
→ validation fails

Invalid completion

work item established
→ implementation changed
→ required evidence missing
→ completion rejected

Blocked work

active
→ blocked
→ reason recorded

Multiple work items

one change legitimately satisfies multiple work items
→ attribution retained

Mechanical change

recognized automated/mechanical operation
→ allowed under explicit system attribution

Retry

same event published twice
→ remote system receives one logical transition

External failure

repository transition validated
→ project-management publication fails
→ state remains explicit
→ no false success

⸻

Documentation for Consuming Repositories

Create concise onboarding instructions that another repository or agent can follow.

The desired experience should approach:

ros init
ros work begin FEAT-142
# perform work
ros validate
ros work complete FEAT-142

Exact commands may differ based on the existing architecture.

The important property is that adoption is simple and the resulting behavior is deterministic.

⸻

Non-Goals for This Mission

Do NOT build:

* a Kanban UI
* a portfolio dashboard
* sprint planning
* story points
* roadmap visualization
* notification infrastructure
* a Jira replacement
* GitHub Projects synchronization as the core architecture
* a centralized ROS work database

Those belong to the separate project-management system.

ROS should make those capabilities possible through its protocol.

⸻

Architectural Test

At the end, verify that all of the following statements remain true:

A repository can use ROS without using our project-management application.

Our project-management application can be replaced without redesigning ROS.

A repository’s implementation history remains meaningful if the central project-management system disappears.

ROS can be upgraded independently through explicit protocol versions.

An AI agent cannot legitimately complete tracked work merely by claiming that it is complete.

Meaningful repository changes cannot pass authoritative validation without work-item attribution.

The system preserves enough semantic information that a later agent does not have to reconstruct intent solely from surviving code.

If any of these statements are false, revise the architecture before considering the mission complete.

⸻

Final Output

When finished, provide:

1. architecture implemented
2. files added/changed
3. protocol definition
4. commands/capabilities available
5. validation/enforcement behavior
6. consuming-repository setup
7. tests performed
8. architectural compromises
9. unresolved questions
10. recommended next phase

Do not claim functionality that was not implemented or tested.