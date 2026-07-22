# Codex Execution Prompt: Populate ROS Phase 1 — Governance Foundation

## Role

You are acting as the founding repository architect, research systems engineer, technical writer, and autonomous-agent governance designer for this repository.

Your task is to populate the **first phase of the Repository Operating System (ROS)**.

The ROS is the governing system that tells future human and AI contributors:

- how the repository is organized
- how agents should behave
- how research is conducted
- how evidence is evaluated
- how engineering decisions are made
- how uncertainty is represented
- how canonical documents are created and updated
- how work is handed off without relying on conversation history
- how implementation remains traceable to research

This is not a brainstorming exercise.

Inspect the repository, synthesize the strongest existing material, resolve duplication, identify contradictions, make reasoned decisions, and create the actual canonical files.

Do not merely propose what should be written. Write it.

---

# Primary Objective

Create the first operational layer of the ROS under:

```text
/docs/00-governance/
```

Populate these canonical documents:

```text
/docs/00-governance/
    README.md
    AI-Repository-Operating-System.md
    Agent-Operating-Manual.md
    Engineering-Standards.md
    Research-Execution-Package-Specification.md
    Governance-Decision-Log.md
```

Also create or update:

```text
/AGENTS.md
```

The result must be usable immediately by Codex, Claude Code, ChatGPT, Gemini, human contributors, and future autonomous agents.

---

# Governing Principle

The repository must not depend on tribal knowledge, hidden conversation history, or undocumented assumptions.

A capable agent entering the repository for the first time should be able to:

1. Understand the mission and governing rules.
2. Locate canonical knowledge.
3. Determine which documents have authority.
4. Conduct research without inventing evidence.
5. make decisions under uncertainty.
6. modify code without violating research conclusions.
7. update the correct registries and records.
8. leave a complete handoff for the next agent.

If the resulting documents cannot accomplish this, Phase 1 is incomplete.

---

# Required Inputs

Before writing, inspect the entire repository for relevant materials, including:

- existing ROS documents
- REP specifications
- autonomous research prompts
- autonomous engineering prompts
- agent charters
- research journals
- theory registries
- evidence registries
- experiment registries
- decision records
- Clarity materials
- Visual Engineering materials
- architecture documents
- development standards
- website evaluation prompts
- README files
- prior governance proposals
- duplicated or conflicting instructions

Search broadly. Do not assume filenames are consistent.

Treat the repository content as evidence, not automatically as truth.

---

# Canonical REP Foundation

Use the existing Research Execution Package specification as a governing input.

At minimum, preserve and integrate these concepts:

## REP Purpose

The REP is the canonical artifact produced at the completion of a research effort. It serves as:

- permanent scientific record
- executable handoff
- theory update
- knowledge-transfer artifact
- synchronization point between autonomous agents

## Canonical Artifact Hierarchy

1. Scientific Research Journal
2. Research Execution Package
3. Theory Registry
4. Evidence Registry

Clarify how additional registries fit into this hierarchy, including:

- Hypothesis Registry
- Experiment Registry
- Decision Registry
- Concept Registry
- Glossary

Do not silently replace the canonical hierarchy. If you revise it, document the reason and migration path.

## Stable Identifiers

Preserve and formalize:

| Artifact | Prefix |
|---|---|
| Research Package | `RP-` |
| Journal Entry | `JR-` |
| Evidence | `EV-` |
| Hypothesis | `HY-` |
| Theory | `TH-` |
| Experiment | `EX-` |
| Decision Framework or Decision Record | `DF-` |
| Concept | `CN-` |
| Glossary | `GL-` |

Resolve any ambiguity around `DF-`. State whether it represents a decision framework, a decision record, or both. Prefer one canonical meaning and provide a migration rule for older uses.

## REP Required Metadata

Include:

- Identifier
- Title
- Research Area
- Discipline
- Author Agent
- Version
- Confidence
- Completion
- Priority
- Related Projects
- Related Documents
- Supersedes
- Superseded By
- Tags
- Keywords

## Research State Snapshot

Every REP should begin with:

- Theory Version
- Knowledge Base Version
- Highest Confidence Areas
- Lowest Confidence Areas
- Largest Remaining Unknown
- Active Research Streams
- Recently Invalidated Ideas
- Priority Changes

## Mandatory REP Sections

Preserve, normalize, and explain:

- Executive Summary
- Original Objective
- Scope
- Repository Context
- Current Understanding
- Key Discoveries
- Evidence Registry
- Hypothesis Registry
- Failed Assumptions
- Open Questions
- Recommended Next Research
- Research Backlog
- Suggested Specialized Research Agents
- Parallel Research Opportunities
- Risks
- Cross-Discipline Opportunities
- Knowledge Relationships
- Repository Updates
- Website Updates
- AI Consumption Notes
- Handoff Instructions
- Research Journal
- Appendix
- Completion Checklist

## Theory Impact Assessment

Every REP must document:

- Affected Theory Records
- Affected Engineering Principles
- New Principle Candidates
- Deprecated Principles
- Confidence Changes
- Predictions Created
- Predictions Invalidated
- Required Theory Registry Updates

## Evidence Traceability

Important claims should reference:

- Evidence IDs
- Hypothesis IDs
- Theory IDs

## Research Quality Metrics

Track:

- Primary Sources
- Independent Sources
- Counterexamples Reviewed
- Competing Viewpoints Reviewed
- Hypotheses Tested
- Failed Hypotheses
- Research Completeness
- Confidence Gain
- Open Questions Reduced

## Research Debt

Record:

- Missing Evidence
- Missing Experiments
- Missing Disciplines
- Weak Areas
- Replication Needed
- Tool Limitations
- Assumptions Awaiting Evidence

## REP Success Criterion

A different capable agent must be able to:

1. reconstruct the investigation
2. understand the current theory
3. continue the research immediately
4. produce the next REP without additional context

---

# Work Method

Operate in deliberate phases.

## Phase A — Repository Discovery

Inventory all relevant files.

For each candidate document, determine:

- purpose
- current authority
- overlap
- contradictions
- freshness
- whether it is canonical, supporting, obsolete, or uncertain

Do not delete source material during discovery.

Create an internal synthesis before editing.

## Phase B — Governance Hypotheses

Treat major governance choices as hypotheses.

Examples:

- A single root `AGENTS.md` is sufficient to orient every agent.
- The REP should remain the primary handoff artifact.
- Registry files should be append-oriented.
- Canonical documents should use explicit authority metadata.
- Agents should be allowed to make reversible decisions autonomously.
- High-impact irreversible decisions should require escalation.
- Confidence should be represented categorically, numerically, or both.
- Every implementation change should not require a full REP.
- Governance documents should separate invariants from recommended practices.

For each important hypothesis:

1. state the hypothesis
2. identify supporting repository evidence
3. identify contradictory evidence
4. assess tradeoffs
5. choose a disposition:
   - accepted
   - provisionally accepted
   - rejected
   - unresolved
6. record the result in `Governance-Decision-Log.md`

Do not pretend an untested preference is a proven rule.

## Phase C — Canonical Synthesis

Create concise but complete governing documents.

Prefer consolidation over repetition.

Each rule should have one authoritative home.

Other documents should link to that rule rather than restating it unless a brief summary is necessary for execution.

## Phase D — Self-Critique

Before finalizing, challenge the system.

Look for:

- circular references
- contradictory authority
- vague agent instructions
- excessive ceremony
- missing escalation rules
- unbounded autonomy
- unclear stopping conditions
- weak evidence standards
- impossible maintenance burdens
- duplicate metadata
- identifiers without lifecycle rules
- registries with no update process
- governance that blocks useful work
- governance that allows reckless work
- documents that are too abstract to execute
- documents that are too long to be routinely read

Revise until the system is operational rather than aspirational.

---

# Required Document Specifications

## 1. `/docs/00-governance/README.md`

Purpose: Entry point to governance.

Include:

- what the governance layer is
- reading order
- authority order
- document map
- update policy
- how conflicts are resolved
- how a new agent should begin
- current phase and known gaps

Keep it concise.

---

## 2. `/docs/00-governance/AI-Repository-Operating-System.md`

Purpose: Repository constitution.

Define:

### Mission

What the ROS governs and why it exists.

### Scope

What is governed:

- research
- engineering
- documentation
- design
- agent behavior
- knowledge capture
- handoffs
- quality control

What is not yet governed.

### Authority Model

Define the precedence of:

1. explicit user instruction
2. repository safety and legal constraints
3. canonical governance documents
4. domain REPs and accepted theory
5. architecture and engineering decisions
6. current implementation
7. local convention
8. agent preference

Resolve conflicts explicitly.

### Canonical Artifact Model

Define the relationship among:

- Journal
- REP
- Theory Registry
- Evidence Registry
- Hypothesis Registry
- Experiment Registry
- Decision Registry
- Concepts
- Glossary
- code and websites generated from them

### Knowledge Lifecycle

Define:

- observation
- question
- hypothesis
- evidence collection
- evaluation
- experiment
- theory update
- decision
- implementation
- validation
- handoff
- supersession

### Confidence Model

Define a practical confidence system.

Prefer both:

- categorical labels: Low, Medium, High, Very High
- optional numerical estimate: `0.00–1.00`

Explain that confidence represents strength of justified belief, not importance or completion.

### Decision Classes

Classify decisions by:

- reversibility
- impact
- evidence strength
- cost
- risk

Define when an agent may decide autonomously and when escalation is required.

### Change Classes

Differentiate:

- research-only change
- documentation change
- reversible implementation change
- architectural change
- data migration
- destructive or irreversible change
- security or privacy-sensitive change

### Traceability

Define minimum traceability for:

- claims
- theories
- decisions
- experiments
- code changes
- generated artifacts

Avoid requiring maximum ceremony for trivial edits.

### Completion and Stopping Rules

Define when agents should stop:

- objective satisfied
- tests and quality gates pass
- no high-value unresolved issue within scope
- further work has diminishing returns
- blocker is documented
- unsafe or unauthorized action is required

### Supersession and Versioning

Define:

- canonical status
- draft status
- deprecated status
- archived status
- supersedes and superseded-by links
- semantic or document versioning
- migration expectations

### Failure and Recovery

Define behavior when:

- evidence conflicts
- tests fail
- repository state is unclear
- instructions conflict
- an agent makes a mistake
- prior work is incomplete
- a task cannot be completed safely

### Anti-Patterns

Include:

- hidden assumptions
- unsupported certainty
- research theater
- endless research without decisions
- implementation without traceability
- preserving bad systems because they exist
- rewriting canonical records without migration
- creating duplicate sources of truth
- claiming validation without testing
- excessive governance for trivial work

---

## 3. `/docs/00-governance/Agent-Operating-Manual.md`

Purpose: Executable rules for autonomous agents.

Define an agent startup protocol:

1. Read `AGENTS.md`.
2. Read governance README.
3. Identify task scope.
4. Locate canonical domain documents.
5. Inspect current repository state.
6. Establish knowns, unknowns, constraints, and risks.
7. Choose the smallest sufficient process.
8. Execute.
9. Validate.
10. update required artifacts.
11. leave a handoff.

Include:

### Operating Modes

- research
- engineering
- design
- documentation
- audit
- maintenance
- synthesis

Explain how behavior changes by mode.

### Autonomy Rules

Agents should make reasonable reversible decisions without unnecessary interruption.

Agents must not:

- fabricate evidence
- invent completed tests
- conceal uncertainty
- make destructive changes without authorization
- silently alter canonical policy
- ignore contradictory evidence
- expand scope without documenting it

### Research Cycle

Use:

1. review existing knowledge
2. identify largest uncertainty
3. generate hypotheses
4. define confirming and falsifying evidence
5. gather evidence
6. compare alternatives
7. update understanding
8. record findings
9. choose next highest-value action
10. stop at diminishing returns

### Engineering Cycle

Use:

1. inspect
2. reproduce or establish baseline
3. define acceptance criteria
4. generate solution alternatives
5. choose the smallest robust solution
6. implement
7. test
8. review
9. document
10. hand off

### Hypothesis Evaluation

Require:

- statement
- evidence for
- evidence against
- unknowns
- confidence
- disposition
- implications

### Tool Honesty

Never claim:

- a file was read when it was not
- a command succeeded when it failed
- a test passed when it was not run
- a user approved something they did not
- evidence exists when it does not

### Escalation Rules

Escalate only when necessary, including:

- irreversible destructive action
- security or privacy risk
- legal ambiguity
- conflicting user goals that materially change outcomes
- missing credentials or inaccessible required systems
- a decision whose consequences exceed authorized scope

When a safe reversible path exists, take it and document the assumption.

### Handoff Standard

Every substantial task should leave:

- objective
- work completed
- files changed
- decisions made
- tests run
- evidence added
- unresolved questions
- risks
- next recommended action

### Efficiency Principle

Use the least ceremony that preserves correctness, traceability, and continuity.

Not every edit needs a REP.

Define thresholds for:

- no governance artifact
- brief decision note
- journal entry
- experiment record
- full REP

---

## 4. `/docs/00-governance/Engineering-Standards.md`

Purpose: Baseline engineering constitution, independent of framework.

Do not invent stack-specific rules unless supported by the repository.

Include:

### General Principles

- clarity over cleverness
- simple before abstract
- explicit contracts
- reversible evolution
- evidence-driven architecture
- accessibility and security by default
- maintainability as a product requirement
- tests proportional to risk
- performance measured before optimized

### Repository Hygiene

- predictable structure
- no unexplained generated files
- no secrets
- no dead code without justification
- no duplicate canonical documents
- small cohesive changes
- preserve user work
- document migrations

### Code Quality

- clear naming
- cohesive modules
- limited hidden state
- explicit error handling
- avoid speculative abstractions
- comments explain why, not obvious syntax
- prefer readable local reasoning

### Architecture Decisions

Define when an architecture decision record is required.

Require:

- context
- decision
- alternatives
- evidence
- consequences
- reversibility
- status
- follow-up validation

### Testing

Define:

- baseline before modification when practical
- unit, integration, end-to-end, accessibility, visual, and performance tests according to risk
- no false claims of test coverage
- record skipped tests and why

### Accessibility

Establish accessibility as a core quality property.

Reference current repository standards if present. Do not fabricate compliance.

### Security and Privacy

Include:

- least privilege
- secret handling
- dependency caution
- input validation
- safe defaults
- no sensitive data in logs or examples
- explicit review for security-sensitive changes

### Documentation

Document:

- public behavior
- architectural intent
- non-obvious constraints
- migration steps
- operational procedures
- known limitations

### Definition of Done

A change is done when:

- acceptance criteria are met
- relevant tests pass
- failures are explained
- documentation is updated
- traceability is sufficient
- no known high-severity regression remains
- the next agent can understand the result

---

## 5. `/docs/00-governance/Research-Execution-Package-Specification.md`

Purpose: Canonical normalized REP v2.x specification.

Use the existing REP v2 specification as the baseline.

Improve it only where necessary for clarity, internal consistency, and operational use.

Include:

- status and version metadata
- purpose
- artifact hierarchy
- identifier rules
- metadata schema
- confidence model
- completion model
- mandatory sections
- evidence traceability
- theory impact
- quality metrics
- research debt
- handoff requirements
- completion checklist
- minimal REP example
- full REP template
- rules for partial or abandoned research
- rules for supersession
- rules for registry updates
- distinction between journal and REP
- distinction between research result and implementation result

Do not weaken the success criterion.

---

## 6. `/docs/00-governance/Governance-Decision-Log.md`

Purpose: Record the decisions made while creating Phase 1.

For every material decision, include:

- ID
- Date
- Status
- Context
- Hypothesis or question
- Evidence considered
- Alternatives
- Decision
- Confidence
- Consequences
- Revisit trigger

At minimum, document decisions about:

- authority order
- canonical artifact hierarchy
- meaning of `DF-`
- confidence representation
- when a full REP is required
- agent escalation thresholds
- registry update expectations
- versioning and supersession
- relationship between research and implementation
- role of `AGENTS.md`

---

## 7. `/AGENTS.md`

Purpose: High-signal startup file for every coding agent.

Keep it concise enough to read at every session.

Include:

- repository mission
- first files to read
- canonical authority order
- core operating rules
- research and engineering workflow summary
- no-fabrication rule
- testing honesty
- decision and escalation summary
- documentation and handoff requirements
- links to detailed governance documents

Do not duplicate the entire ROS.

---

# Required Metadata for Canonical Governance Documents

At the top of each canonical document, use consistent YAML front matter:

```yaml
---
id: <stable identifier>
title: <document title>
status: canonical
version: 1.0.0
owners:
  - repository-governance
created: <YYYY-MM-DD>
updated: <YYYY-MM-DD>
review_cycle: quarterly
supersedes: []
superseded_by: []
related_documents: []
tags: []
---
```

Use a stable governance identifier convention, such as:

```text
GV-ROS-001
GV-AGENT-001
GV-ENG-001
GV-REP-001
GV-DEC-001
```

Document this convention in the ROS.

Do not retrofit unrelated legacy identifiers without evidence.

---

# Repository Changes

You are authorized to:

- create the Phase 1 governance directory
- create the required documents
- update the root `AGENTS.md`
- add cross-links
- normalize clearly duplicated governance material
- mark obsolete documents as superseded when evidence is sufficient

Do not:

- delete research history
- rewrite domain REPs as part of this phase
- redesign the website
- implement unrelated application features
- create empty registries merely to satisfy a directory plan
- claim unresolved issues are settled
- fabricate sources or prior decisions

When older documents conflict with the new canonical files, preserve them and add a clear supersession notice unless deletion is explicitly safe and justified.

---

# Validation

Before completion:

## Structural Validation

Confirm:

- every required file exists
- links resolve
- headings are consistent
- identifiers are unique
- metadata is valid
- no canonical circular dependency exists

## Content Validation

Confirm:

- each document has a distinct purpose
- rules do not materially contradict
- authority order is explicit
- agents have executable startup instructions
- REP requirements remain complete
- governance is proportional rather than bureaucratic
- uncertainty and contradictory evidence are represented
- destructive actions have safeguards
- reversible work is not blocked unnecessarily

## Repository Validation

Run any available:

- markdown lint
- link checker
- documentation tests
- repository test suite affected by the changes

If tools are unavailable, record that honestly.

---

# Final Output

After modifying the repository, provide a concise execution report containing:

## Completed

- files created
- files updated
- major governance decisions
- contradictions resolved
- materials superseded

## Validation

- checks run
- results
- checks not run
- reasons

## Remaining Gaps

- unresolved governance questions
- missing source material
- areas deferred to ROS Phase 2

## Recommended Next Phase

Recommend the next highest-value ROS phase.

Do not merely print the documents in the response.

Create and update the files in the repository.

---

# Success Criteria

Phase 1 is complete only when:

1. A new agent can enter through `AGENTS.md` and orient itself without conversation history.
2. Canonical governance documents have clear authority and distinct responsibilities.
3. Research, evidence, hypotheses, theories, decisions, implementation, and handoff form one coherent lifecycle.
4. The REP specification is operational and internally consistent.
5. Agents know what they may decide, what they must record, and when they must escalate.
6. Governance is strong enough to preserve integrity but lightweight enough to support real work.
7. Every material governance choice made during this task is recorded with evidence and confidence.
8. The repository is left ready for Phase 2 without requiring reconstruction of this task.
