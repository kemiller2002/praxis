Repository OS + Project Management Roadmap

Phase 1 — Establish the ROS Work Protocol

Goal: Make one repository capable of operating under explicit work-item attribution.

Build:

* ros work begin
* work-item identity
* repository work context
* meaningful-change attribution
* ros validate
* ros work complete
* basic evidence requirements
* blocked/resume transitions
* protocol versioning

Do not build aggregation yet.

Success criterion

This should fail:

Agent changes meaningful code
→ no work item
→ CI rejects change

This should succeed:

Agent begins FEAT-142
→ changes code
→ produces evidence
→ requests completion
→ ROS validates
→ CI succeeds

⸻

Phase 2 — Make Agent Compliance Automatic

Goal: Agents follow ROS because ROS is part of their normal operating environment.

Add:

* canonical agent instructions
* ros work context
* allowed-action reporting
* repository initialization
* standard validation output
* repair instructions when validation fails

Agent experience becomes:

receive work item
→ ask ROS for context
→ execute
→ validate
→ transition

Success criterion

An agent should not need a large prompt explaining ROS every session.

The repository itself provides enough machinery and instructions.

⸻

Phase 3 — Reusable ROS Distribution

Goal: Add ROS to another repo with minimal effort.

Create:

ros init

or equivalent.

Add:

* reusable CI workflow
* version pinning
* standard config
* default schemas
* upgrade procedure
* example repository

Desired onboarding should be approximately:

initialize ROS
configure repository workflow
commit

not dozens of copied files.

Success criterion

Take a clean repository and put it under ROS control without modifying ROS itself.

⸻

Phase 4 — External Work-System Contract

Goal: Separate ROS cleanly from project-management storage.

Define the minimum adapter contract.

Potential capabilities:

get work item
query work item state
request transition
publish evidence reference
publish repository event

Address explicitly:

* IDs
* repository identity
* authentication
* authorization
* idempotency
* retries
* protocol versions
* success/failure/unknown outcomes

Create a simple test adapter.

It can initially be file-backed or in-memory.

Success criterion

ROS can operate against the adapter without knowing what product or database implements it.

⸻

Phase 5 — Project Management Datastore

Separate repository. Separate responsibility.

Create the project-management system only after the adapter contract stabilizes.

Its canonical model starts small:

Project
WorkItem
Relationship
Repository
Transition
EvidenceReference

Work-item types:

Feature
Bug
Research
Task
Technical Debt
Decision
Obligation
Incident
Maintenance

Do not create separate research and development systems.

Success criterion

The datastore can represent work spanning:

one project
one repository
one project
many repositories
many projects
many repositories

⸻

Phase 6 — Basic Work UI

Build the smallest useful UI:

Kanban
List
Item detail
Filters
Search

Filters:

project
repository
work type
state
owner
blocked
tags

Do not implement Jira.

No initial need for:

sprints
story points
velocity charts
Gantt charts
chat
complex permissions
workflow designer
custom dashboards

Success criterion

We can manage a normal software project entirely through the basic UI.

⸻

Phase 7 — Research View

Research is now simply a specialized projection of work.

Provide:

Open research
Active research
Blocked research
Completed research
Research by repository
Research across all repositories

Separately display:

work state

and:

research conclusion

For example:

RES-17
Completed
Conclusion: Inconclusive

Success criterion

We can immediately answer:

What research is still unresolved across everything we’re building?

without searching repositories manually.

⸻

Phase 8 — Cross-Repository Software Portfolio

Add aggregated views such as:

All active development
All blocked work
Work awaiting review
Work by project
Work by repository
Work spanning repositories
Unresolved obligations

Relationships begin becoming powerful:

FEAT-142
depends_on → RES-017
RES-017
led_to → DEC-031
DEC-031
enables → FEAT-142

Success criterion

We can explain not only what is being worked on, but why the work exists.

⸻

Phase 9 — Provenance

Connect the development graph:

Work Item
    ↓
Commit
    ↓
Files
    ↓
Tests
    ↓
Evidence
    ↓
Release

And the semantic graph:

Research
    ↓
Evidence
    ↓
Decision
    ↓
Feature
    ↓
Implementation

Do this incrementally.

Avoid building a generalized graph database merely because the model resembles a graph.

Success criterion

Given an important piece of code, we can trace back to the work and reasoning that caused it to exist.

⸻

Phase 10 — Agent Work Queues

Now exploit the structure for AI.

Instead of:

Agent
→ inspect everything
→ determine what needs doing
→ infer priority
→ infer whether it is permitted

provide:

Agent
→ request available work
→ receive legal work items
→ receive required evidence
→ receive permitted transitions
→ execute

Example:

{
  “workItem”: “FEAT-142”,
  “state”: “ready”,
  “allowedActions”: [
    “begin”,
    “block”
  ]
}

Success criterion

Agent action-space reduction becomes measurable.

We can begin comparing:

tokens
tool calls
repair loops
context size
completion accuracy

against repositories without ROS.

⸻

Recommended Build Order

Do not begin with the Kanban board.

Begin with:

1. Work identity
2. Work attribution
3. Legal transitions
4. Evidence
5. CI enforcement
6. Agent behavior
7. Distribution to multiple repos
8. External datastore contract
9. Project-management datastore
10. UI

This ordering prevents us from accidentally designing the semantic system around whatever is easiest to render as a board.

⸻

Architectural Boundary to Preserve

Keep these three systems conceptually distinct:

┌─────────────────────────────┐
│ Repository Operating System │
│                             │
│ Rules                       │
│ Protocol                    │
│ Validation                  │
│ Agent capabilities          │
│ Enforcement                 │
└──────────────┬──────────────┘
               │
               │ protocol
               ▼
┌─────────────────────────────┐
│ Project Management System   │
│                             │
│ Work items                  │
│ Projects                    │
│ Relationships               │
│ Portfolio state             │
│ Kanban                      │
└─────────────────────────────┘
Individual repositories retain:
code
tests
research
documentation
evidence
commit history

That segregation of duties should remain an architectural invariant.

ROS governs how repository work is performed.

The project-management system records and presents what work exists and its organizational state.

Repositories contain the actual evidence of what was done.