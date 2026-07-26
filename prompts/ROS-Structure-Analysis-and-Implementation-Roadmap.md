# ROS Structure Analysis and Implementation Roadmap

**Document type:** Architecture review, operating roadmap, and implementation handoff  
**ROS baseline:** 1.0.0  
**REP specification:** 2.0  
**Status:** Recommended next-stage plan  
**Primary audience:** Autonomous engineering and research agents

---

# 1. Executive Assessment

The newly created repository is a strong **scaffold**, but it is not yet a functioning Research Operating System.

It correctly establishes:

- a shared bootstrap entry point;
- canonical research artifact categories;
- stable identifier conventions;
- separation between canonical records and generated products;
- reusable templates;
- registries and schemas;
- mission, context, intake, work, tooling, test, and archive boundaries;
- an explicit expectation that agents leave reconstructable handoffs.

The design is strongest as an **information architecture**. Its weakest area is the **execution architecture**.

At present, the repository describes how work should be organized, but it does not yet reliably:

- create artifacts;
- allocate identifiers;
- validate metadata;
- update registries;
- enforce lifecycle transitions;
- detect broken references;
- distinguish immutable from mutable records;
- generate compact agent context;
- process incoming documents;
- publish searchable outputs;
- measure research progress;
- prevent policy drift;
- prove that one agent can successfully hand work to another.

The next phase should therefore not add more folders or more prompt text. It should convert the scaffold into an **executable contract**.

## Primary conclusion

The highest-value next step is to implement a narrow vertical slice:

> Create one mission, execute one research cycle, generate one journal, evidence record, hypothesis record, experiment record, theory update, and REP, validate all relationships, update registries automatically, and prove that a second agent can continue from the repository alone.

Until that works, expanding the architecture would create additional ceremony without increasing reliability.

---

# 2. What the Current Structure Gets Right

## 2.1 It separates governance from work

The repository distinguishes:

- `framework/`: operating rules;
- `missions/`: bounded assignments;
- `context/`: compact current understanding;
- `research/`: canonical knowledge;
- `generated/`: derived products;
- `tools/`: automation;
- `registries/`: indexes;
- `schemas/`: machine-readable contracts.

This is a sound separation of concerns. It prevents a common failure mode where prompts, findings, generated reports, and temporary work all become mixed together.

## 2.2 It treats conversational context as disposable

`BOOTSTRAP.md` makes the repository, rather than a chat history, the source of continuity.

That is essential. Agents should be replaceable. The work should survive model changes, vendor changes, context-window limits, and interrupted sessions.

## 2.3 It introduces provenance and stable identity

Identifiers such as `EV-`, `HY-`, `TH-`, `EX-`, and `RP-` provide a basis for:

- cross-document references;
- supersession;
- graph construction;
- registry validation;
- reproducibility;
- generation of reports and websites;
- future retrieval systems.

This is more durable than linking only by filenames.

## 2.4 It separates canonical and generated artifacts

The distinction between `research/` and `generated/` is correct.

A website, report, summary, or presentation should be rebuildable. It must not silently become the only place where a conclusion exists.

## 2.5 It anticipates deterministic tooling

The scaffold includes validation, indexing, build, test, and intake directories. This creates the right architectural expectation:

> Agents should interpret evidence and make judgments; ordinary software should enforce formats, relationships, and repeatable transformations.

---

# 3. The Largest Architectural Gaps

## 3.1 There is no implemented artifact lifecycle

The templates contain fields such as `status`, `completion`, `supersedes`, and `superseded_by`, but the allowed transitions are undefined.

Questions currently unanswered include:

- When does a mission move from backlog to active?
- Who may mark a REP complete?
- Can evidence records be edited after publication?
- Is a hypothesis updated in place or superseded?
- What happens when a theory is contradicted?
- When is a journal entry immutable?
- Can a completed mission be reopened?
- Which artifact is authoritative when two records conflict?

Without explicit lifecycle rules, different agents will make incompatible choices.

## 3.2 Registries are placeholders rather than infrastructure

Empty JSON arrays do not yet provide meaningful indexing.

The system needs to decide whether registries are:

1. manually maintained canonical records;
2. automatically generated indexes;
3. partially generated and partially curated.

The strongest approach is:

- artifact files are canonical;
- registries are generated;
- curated state is stored in explicit records rather than hidden inside generated indexes.

This avoids registry drift and merge conflicts.

## 3.3 Identifier allocation is unsafe

The naming standard recommends sequential IDs, but no allocator exists.

Two parallel agents may independently create:

- `EV-VE-2026-0007`
- `EV-VE-2026-0007`

Sequential IDs are readable but require coordination.

Possible solutions:

- central allocator with a locked registry;
- timestamp-based IDs;
- random or content-derived suffixes;
- branch-aware allocation followed by collision detection.

For Git-based parallel research, a hybrid identifier is safer:

```text
EV-VE-2026-0724-A7F2
```

Human-readable area and date remain, while the suffix prevents most collisions.

## 3.4 “Immutable” is asserted but not operationally defined

The repository says prior findings should not be overwritten, but Git alone does not prevent agents from editing them.

A practical policy is needed:

- draft artifacts may be edited;
- accepted artifacts are immutable except for narrowly defined metadata corrections;
- substantive changes create a new version or superseding record;
- corrections preserve an amendment history;
- validation fails if accepted content changes without a version or amendment.

## 3.5 Context files could become hidden canonical sources

`context/CURRENT-STATE.md`, `DECISIONS.md`, and related files are useful, but they create a danger: an agent may add an important conclusion there without creating the corresponding evidence or theory record.

Context should be treated as a **derived operational summary**, not a separate knowledge base.

Every material statement in context should reference canonical artifact IDs.

## 3.6 The canonical hierarchy is conceptually ambiguous

The current documents list:

1. Journal
2. REP
3. Theory Registry
4. Evidence Registry

This may be interpreted as an authority ranking, a production sequence, or a storage hierarchy.

Those are not the same.

A better model is:

```text
Source evidence
    ↓
Evidence records
    ↓
Journaled investigation
    ↓
Hypotheses and experiments
    ↓
Theory updates
    ↓
REP synthesis and handoff
    ↓
Derived context and generated products
```

The REP is the synchronization package, but evidence remains the authority for factual claims. The journal is the process record, not necessarily the strongest source of truth.

## 3.7 The repository lacks a project/domain model

The tree includes `context/projects/` and `context/domains/`, but there is no standard explaining:

- what constitutes a project;
- what constitutes a domain;
- whether HelixNote, Clarity, Visual Engineering, and Framework Engineering are projects, programs, or research areas;
- how research shared across projects should be stored;
- whether artifacts live by type, project, or domain.

The current type-first layout is valid, but it will become hard to navigate at scale unless metadata and generated views are excellent.

## 3.8 There is no minimum viable artifact rule

The full REP template is comprehensive. It may be excessive for small investigations.

If every minor research task requires every section, agents may:

- produce boilerplate;
- fill sections with meaningless text;
- avoid recording small findings;
- spend more tokens documenting than researching.

The system needs artifact tiers.

Recommended tiers:

- **Research Note:** small bounded finding;
- **Research Cycle:** journal plus evidence and hypothesis updates;
- **Full REP:** major synthesis or handoff milestone.

## 3.9 No handoff test exists

The REP success criterion is excellent:

> Another agent should continue without conversational context.

But the repository does not test this claim.

A formal “cold-start handoff test” should assign a fresh agent to:

1. read only the repository;
2. explain the current state;
3. identify the next action;
4. cite the supporting artifacts;
5. continue a bounded task.

Failures should be recorded as architecture defects.

## 3.10 There is no ingestion workflow

`input-documents/` describes desired behavior, but not how to:

- inventory files;
- hash them;
- detect duplicates;
- preserve originals;
- extract metadata;
- classify relevance;
- record provenance;
- move accepted material;
- quarantine unsupported formats;
- handle sensitive information.

This is a high-value automation target because intake will otherwise become inconsistent and costly.

---

# 4. Assumptions and Hypotheses Tested

## Hypothesis A

**A standard directory layout will significantly improve agent efficiency.**

### Supporting reasoning

A standard layout reduces discovery cost and makes instructions reusable. Agents know where to find policies, missions, evidence, and outputs.

### Attempted falsification

A directory structure by itself may merely move complexity from prompts into files. If agents still read every framework and context file on every run, token use may remain high or increase.

### Revised conclusion

The layout helps only when paired with:

- targeted bootstrap manifests;
- generated indexes;
- bounded missions;
- selective retrieval;
- validation;
- compact current-state summaries.

**Confidence:** High.

---

## Hypothesis B

**More explicit templates will produce better research records.**

### Supporting reasoning

Templates reduce omission and standardize metadata.

### Attempted falsification

Large mandatory templates encourage performative completion. Agents may fill every heading without adding information, increasing token use and obscuring meaningful results.

### Revised conclusion

Templates improve quality when sections are conditionally required according to artifact tier and mission risk. Full REP templates should be reserved for major synthesis and handoff points.

**Confidence:** High.

---

## Hypothesis C

**Immutable artifacts prevent knowledge loss and make research scientific.**

### Supporting reasoning

Preserved records support provenance, correction history, and reconstruction.

### Attempted falsification

Unbounded immutability can preserve errors, create duplicate records, and make correction cumbersome. It can also lead agents to create new files for trivial changes.

### Revised conclusion

Immutability should begin at an explicit acceptance boundary. Drafts remain editable; accepted artifacts are superseded or amended.

**Confidence:** High.

---

## Hypothesis D

**JSON registries should be manually updated by agents.**

### Supporting reasoning

Manual updates are simple to implement initially.

### Attempted falsification

Manual dual-writing almost guarantees drift between artifacts and registries. Parallel agents also create merge conflicts.

### Revised conclusion

Registries should be generated from canonical artifact metadata. Validation should reject duplicate IDs, invalid references, and malformed front matter.

**Confidence:** Very high.

---

## Hypothesis E

**One universal ROS can serve every research and engineering project.**

### Supporting reasoning

Shared methods, evidence standards, and artifact contracts are reusable.

### Attempted falsification

Medical research, UI experimentation, software engineering, market research, and scientific literature review have different evidence, safety, and validation requirements. A single undifferentiated policy may become either vague or burdensome.

### Revised conclusion

ROS should have a small stable kernel plus domain profiles:

```text
ROS kernel
  + research profile
  + engineering profile
  + medical-information profile
  + visual-experiment profile
```

**Confidence:** High.

---

## Hypothesis F

**The repository should be expanded before it is used.**

### Attempted falsification

More design before execution risks solving imagined problems and hardening incorrect abstractions.

### Revised conclusion

The system should now be exercised through one complete vertical slice. Architecture changes should be driven by observed friction.

**Confidence:** Very high.

---

# 5. Target Operating Model

The repository should evolve into six interacting layers.

## Layer 1: Kernel

Stable contracts that all agents inherit:

- evidence policy;
- artifact identity;
- lifecycle rules;
- provenance;
- confidence vocabulary;
- supersession;
- completion and handoff criteria.

## Layer 2: Profiles

Task-specific extensions:

- research;
- engineering;
- evaluation;
- medical-information;
- visual research;
- product research.

Profiles should extend the kernel without copying it.

## Layer 3: Missions

Bounded work orders containing:

- objective;
- context manifest;
- hypotheses;
- constraints;
- deliverables;
- success criteria;
- stop conditions;
- expected artifact tier.

## Layer 4: Canonical records

Evidence, journals, hypotheses, experiments, theories, decisions, concepts, glossary items, and REPs.

## Layer 5: Automation

Commands to:

- initialize a mission;
- allocate an ID;
- create artifacts;
- validate metadata;
- check references;
- generate registries;
- build context packages;
- process intake;
- generate a site;
- run handoff tests.

## Layer 6: Views

Derived products:

- current-state summaries;
- project dashboards;
- evidence graphs;
- theory maps;
- searchable HTML;
- reports and presentations.

---

# 6. Recommended Artifact Lifecycle

## 6.1 Mission lifecycle

```text
proposed
  ↓
approved
  ↓
active
  ↓
blocked | completed | cancelled
  ↓
archived
```

A completed mission must reference its outputs and verification result.

## 6.2 Research artifact lifecycle

```text
draft
  ↓
review
  ↓
accepted
  ↓
superseded | withdrawn
```

Rules:

- Draft and review artifacts may be edited.
- Accepted artifacts are content-immutable.
- Metadata-only corrections require an amendment entry.
- Substantive correction creates a superseding artifact.
- Withdrawn records remain discoverable.
- Superseded records point forward; replacements point backward.

## 6.3 Theory lifecycle

```text
candidate
  ↓
supported
  ↓
established
  ↓
challenged
  ↓
superseded | rejected
```

Confidence and lifecycle status must remain distinct. A theory can be `candidate` with high confidence or `established` with declining confidence.

---

# 7. Roadmap

# Phase 0: Preserve the Baseline

**Goal:** Establish a known starting point before modifying the structure.

## Actions

1. Commit the generated scaffold.
2. Tag it as `ros-v1.0.0-scaffold`.
3. Record the bootstrap script and REP specification as provenance.
4. Create an architecture decision explaining why artifact files are canonical.
5. Confirm no existing research was overwritten.

## Deliverables

- Git commit and tag;
- `research/decisions/DF-ROS-...--canonical-artifact-policy.md`;
- baseline inventory.

## Exit criteria

A clean diff can show every later change relative to the scaffold.

---

# Phase 1: Define the Executable Contract

**Goal:** Remove ambiguity before building tools.

## Actions

1. Define artifact lifecycle states and transitions.
2. Define immutability and amendment rules.
3. Define whether registries are generated.
4. Define identifier format and collision strategy.
5. Define confidence terms.
6. Define artifact tiers.
7. Define project, program, domain, and research-area terminology.
8. Define which context files are generated versus curated.
9. Define the minimum required references for every artifact type.

## Deliverables

- `framework/protocols/ARTIFACT-LIFECYCLE.md`
- `framework/protocols/SUPERSESSION.md`
- `framework/standards/IDENTIFIERS.md`
- `framework/standards/CONFIDENCE.md`
- `framework/standards/ARTIFACT-TIERS.md`
- `framework/standards/TAXONOMY.md`
- architecture decision records

## Exit criteria

Two independent agents interpret artifact creation, editing, acceptance, and supersession the same way.

---

# Phase 2: Build Validation and Registry Generation

**Goal:** Eliminate manual structural drift.

## Actions

Build a deterministic CLI, provisionally named `ros`.

Required initial commands:

```bash
ros validate
ros registry build
ros registry check
ros artifact new evidence
ros artifact new hypothesis
ros artifact new journal
ros artifact new rep
ros mission new
ros status
```

Validation must check:

- valid front matter;
- unique IDs;
- filename and ID consistency;
- allowed lifecycle values;
- required metadata by artifact tier;
- referenced IDs exist;
- supersession is reciprocal;
- no circular supersession;
- accepted artifact mutation policy;
- registry freshness;
- generated outputs do not masquerade as canonical artifacts.

## Deliverables

- CLI implementation;
- tests;
- JSON schemas for every artifact type;
- generated registries;
- CI workflow.

## Exit criteria

A pull request cannot merge with invalid metadata, duplicate IDs, broken references, or stale registries.

---

# Phase 3: Implement a Mission Context Manifest

**Goal:** Reduce unnecessary context loading.

Each mission should explicitly declare what an agent must read.

Example:

```yaml
context_manifest:
  required:
    - framework/kernel.md
    - context/projects/helixnote/current-state.md
    - TH-HN-2026-0012
  optional:
    - RP-HN-2026-0028
  retrieve_by_query:
    - "patient report information hierarchy"
context_budget:
  preferred_tokens: 12000
  maximum_tokens: 30000
```

## Actions

1. Add context-manifest fields to mission schema.
2. Build `ros context build <mission-id>`.
3. Resolve artifact IDs to files.
4. Produce a deterministic context packet.
5. Warn when context exceeds the budget.
6. Record exactly which artifacts were consumed.

## Exit criteria

An agent can begin a mission without traversing the whole repository.

---

# Phase 4: Run the First Vertical Slice

**Goal:** Test the operating model with real work.

Choose one bounded research topic. Avoid a mission that is either trivial or repository-wide.

Recommended candidate:

> Evaluate whether the current ROS artifact hierarchy and REP template provide sufficient handoff information for a second autonomous agent.

## Required outputs

- one active mission;
- at least three evidence records;
- two competing hypotheses;
- one experiment or structured evaluation;
- journal entries;
- one theory record or theory update;
- one REP;
- generated registries;
- current-state update;
- cold-start handoff test.

## Key experiment

A fresh agent receives only:

- `BOOTSTRAP.md`;
- the mission ID;
- repository access.

It must identify:

- what was learned;
- which claims are well supported;
- what remains uncertain;
- the next recommended action;
- relevant evidence IDs.

## Exit criteria

The handoff succeeds without conversational explanation. Any confusion becomes evidence for changing ROS.

---

# Phase 5: Build Intake Processing

**Goal:** Make `input-documents/` operational.

## Commands

```bash
ros intake scan
ros intake classify
ros intake process
ros intake report
```

## Required behavior

- calculate cryptographic hashes;
- detect exact duplicates;
- record source path and ingestion timestamp;
- preserve originals;
- classify format;
- extract front matter or basic metadata;
- identify probable project and domain;
- produce a proposed destination;
- require explicit rules for deletion or movement;
- leave an audit record;
- keep unsupported files in quarantine rather than silently discarding them.

## Exit criteria

An intake run is repeatable and leaves a complete manifest.

---

# Phase 6: Generate Current-State Views

**Goal:** Prevent context files from drifting away from canonical records.

Generate:

- active missions;
- recent accepted findings;
- current theories;
- unresolved contradictions;
- highest-priority research debt;
- superseded records;
- artifact counts;
- orphaned evidence;
- stale missions.

Curated prose may remain, but generated sections should be clearly marked and rebuilt automatically.

## Exit criteria

`context/CURRENT-STATE.md` can be rebuilt from canonical metadata plus an intentionally curated interpretation section.

---

# Phase 7: Build Searchable Human and Agent Views

**Goal:** Make the system usable at scale.

Generate a static site with:

- search;
- artifact pages;
- project views;
- domain views;
- evidence-to-hypothesis-to-theory graph;
- supersession history;
- mission progress;
- research debt;
- source provenance;
- backlinks.

The site is a view, not a source of truth.

## Exit criteria

A user can find an artifact by ID, topic, project, tag, theory, or supporting evidence.

---

# Phase 8: Add Model Routing and Research Economics

**Goal:** Improve token and model efficiency after the workflow is measured.

Track per mission:

- model;
- agent;
- files read;
- context size;
- output size;
- number of retries;
- tool calls;
- human corrections;
- accepted artifacts;
- validation failures;
- handoff success;
- cost where available.

Use the data to decide which tasks belong to:

- deterministic tools;
- small or local models;
- lower-cost hosted models;
- frontier models;
- human review.

Do not design routing solely from intuition.

---

# 8. Priority Order

## Immediate

1. Commit and tag the scaffold.
2. Define lifecycle, identity, and registry policies.
3. Build `ros validate`.
4. Generate registries automatically.
5. Run one vertical-slice mission.
6. conduct a cold-start handoff test.

## Next

7. Build mission context manifests.
8. Build intake processing.
9. Generate current-state views.
10. Add CI.

## Later

11. Build searchable HTML.
12. Add evidence graphs.
13. Add model routing.
14. Add multi-agent scheduling.
15. Add external service integrations.

---

# 9. What Not to Build Yet

Avoid these until the vertical slice is proven:

- a general autonomous scheduler;
- a complex agent marketplace;
- a vector database;
- a graph database;
- automatic theory merging;
- automatic acceptance of findings;
- a large web application;
- elaborate permissions;
- self-modifying framework policies;
- model-based validation where deterministic validation is possible;
- dozens of specialized agent templates.

These may eventually be valuable, but none address the current largest uncertainty: whether the artifact and handoff model works in practice.

---

# 10. Recommended Repository Adjustments

The existing layout should mostly remain intact. Change behavior before changing structure.

Recommended additions:

```text
framework/
  kernel/
    KERNEL.md
  profiles/
    research.md
    engineering.md
    evaluation.md
  protocols/
    ARTIFACT-LIFECYCLE.md
    SUPERSESSION.md
    HANDOFF.md
    INTAKE.md
  standards/
    IDENTIFIERS.md
    CONFIDENCE.md
    ARTIFACT-TIERS.md
    TAXONOMY.md

research/
  amendments/

schemas/
  mission.schema.json
  journal.schema.json
  evidence.schema.json
  hypothesis.schema.json
  experiment.schema.json
  theory.schema.json
  rep.schema.json

tools/
  cli/
  migrations/

generated/
  indexes/
  context/
```

Possible removal or deferment:

- `agents/` should remain nearly empty until agent profiles prove necessary.
- `archive/` should not contain superseded canonical artifacts.
- `logs/` should not become a second journal system.
- `work/` should have an automatic cleanup policy.

---

# 11. How to Use This Roadmap

## For the repository owner

Use this document as the architecture backlog.

Do not ask an agent to “implement the entire ROS.” Assign one phase at a time. Require each phase to:

- inspect the repository first;
- propose the smallest coherent change;
- write tests;
- run validation;
- update decisions;
- leave a handoff;
- avoid unrelated restructuring.

## For research agents

Treat this roadmap as a set of hypotheses, not immutable truth.

When executing a phase:

1. identify its central assumption;
2. define how it could be wrong;
3. collect evidence from actual use;
4. implement the minimum mechanism needed;
5. measure friction;
6. update or reject the roadmap recommendation;
7. preserve the result in a REP or decision record.

## For engineering agents

Translate each phase into:

- behavior;
- interface;
- data contract;
- tests;
- migration implications;
- failure handling;
- documentation;
- acceptance criteria.

Do not infer that a folder name is a complete requirement.

---

# 12. Implementation Instructions for the Next Agent

## Role

You are an autonomous research-systems architect and senior software engineer.

Your objective is to turn the existing ROS scaffold into a minimally executable research operating system without overengineering it.

You must challenge this roadmap and may change it when repository evidence contradicts it.

## Required starting procedure

1. Read `BOOTSTRAP.md`.
2. Read the framework policies and standards.
3. Inspect the complete repository tree.
4. Inspect Git status and history.
5. Identify existing files not created by the bootstrap.
6. Compare the repository to this roadmap.
7. Record any incompatibilities before editing.

## First assigned implementation

Implement **Phase 1 and the smallest useful portion of Phase 2**:

- artifact lifecycle policy;
- supersession policy;
- identifier policy;
- confidence vocabulary;
- artifact tiers;
- generated-registry decision;
- deterministic validation for front matter, unique IDs, and broken references.

## Required design constraints

- Canonical artifacts remain plain text and Git-friendly.
- Do not require a database.
- Do not introduce a web framework.
- Do not use an LLM for deterministic validation.
- Do not overwrite existing research.
- Do not silently migrate existing artifacts.
- Prefer a single small CLI over multiple scripts.
- Support dry-run where changes may affect files.
- Return nonzero exit codes on validation failure.
- Produce actionable error messages with file paths and fields.
- Add tests before declaring completion.
- Avoid dependencies unless they materially reduce complexity.

## Required deliverables

1. Policy documents.
2. Schemas.
3. CLI implementation.
4. Automated tests.
5. CI configuration if the repository platform supports it.
6. Architecture decision records.
7. Updated README and bootstrap references.
8. A migration report.
9. A REP documenting:
   - assumptions;
   - rejected alternatives;
   - implementation;
   - validation evidence;
   - limitations;
   - next steps.

## Required challenge questions

Before implementation, answer:

1. Are sequential IDs safe under parallel Git branches?
2. Should registries be canonical or generated?
3. What content becomes immutable, and at what state?
4. Can a small research task avoid a full REP?
5. Are confidence and lifecycle status being conflated?
6. Can every material context statement trace back to a canonical artifact?
7. What breaks when two agents edit the same mission?
8. How will the system detect an accepted artifact changed?
9. Which rules are kernel rules versus domain-profile rules?
10. What is the smallest implementation that can be tested end to end?

## Completion test

The work is incomplete unless all of the following are true:

- `ros validate` or its equivalent runs successfully;
- an intentionally malformed fixture fails;
- a duplicate ID fails;
- a broken evidence reference fails;
- a valid small artifact set passes;
- registries can be rebuilt deterministically;
- another agent can understand how to create the next valid artifact;
- all changes are summarized with unresolved risks.

## Stop condition

Stop when Phase 1 and the validation vertical slice are complete and tested. Do not continue into intake, websites, model routing, or agent scheduling unless required to resolve a direct architectural blocker.

---

# 13. Final Recommendation

The repository should now move from **structure creation** to **behavior verification**.

The most important architecture decision is not which additional directories to add. It is this:

> Canonical records must be simple and durable, while all indexing, validation, context assembly, and presentation should be deterministic and reproducible.

The most important research experiment is:

> Can a new agent successfully continue meaningful work using only the repository?

The most important implementation milestone is:

> A validated, end-to-end mission that proves artifact creation, evidence traceability, theory evolution, registry generation, and cold-start handoff.

Once that vertical slice works, the ROS can expand with evidence. Before it works, further expansion would mostly increase apparent sophistication rather than actual capability.
