---
id: RP-ROS-2026-A030
title: ROS next-pass Ordo observation and empirical-learning requirements intake
research_area: repository-operating-system
discipline:
  - software-architecture
  - software-engineering
  - developer-tooling
  - empirical-software-engineering
author_agent: ChatGPT
version: 0.1.0
confidence: high
completion: complete
status: review
priority: high
created: 2026-09-20
updated: 2026-09-20
related_projects:
  - repository-operating-system
  - state-directed-engineering
source_repository: kemiller2002/research-documents
source_commit: 6f69ad93f5f410d19499cce9d49c5e15888b67aa
source_document: research/ordo-foundations/deep-research-program/requirement-candidates/ros-next-pass.md
supersedes: []
superseded_by: []
tags: [ordo, ros, next-pass, requirements, observations, outcomes, handoff, calibration]
---

# Intake status

This package is an **incoming validated requirements package**, not yet canonical ROS architecture or implementation authority.

ROS should process it under its normal work/research/governance protocol, after upgrading to the approved SDE/Ordo release that results from the corresponding SDE package.

The original research copy remains in `kemiller2002/research-documents` at commit `6f69ad93f5f410d19499cce9d49c5e15888b67aa` for provenance.

---

# ROS Next-Pass Requirements

Status: validated next-pass requirement candidates  
Source: `synthesis/ORDO-ROS-NEXT-PASS-DECISION-PACKAGE.md`  
Validation date: 2026-09-19

These requirements are the operational counterpart to the validated Ordo next pass.

# Cross-Cutting Architecture Guardrail - ROS Is Not Tier 5

ROS remains an observer of application and Ordo execution. It is not part of the four-tier application dependency stack and MUST NOT become a prerequisite for application correctness.

```text
             Application
       Tier 4 -> 3 -> 2 -> 1
                  |
                  | observation facts
                  v
                 ROS
```

## Requirements

ROS next-pass work MUST NOT:

- introduce a dependency from Tier 1 or Tier 2 to ROS;
- make a legal domain transition depend on ROS availability;
- let ROS observations become application authority merely because they are newer;
- let calibration/reflection mutate DecisionContracts, policy, capabilities, evidence requirements, state, or transitions automatically;
- execute application-specific effects or reconciliation on behalf of the domain;
- replace immutable application/Ordo history with a current projection.

ROS MAY:

- ingest observations;
- preserve and index history;
- derive rebuildable projections;
- compare outcomes;
- calculate calibration after sufficient data exists;
- surface recommendations and experiments.

## Four-tier compatibility

Any ROS integration with an application MUST occur through observation/export contracts at a boundary that does not cause domain code to import ROS.

The application MUST continue to build, test, decide, and execute legal transitions when ROS is unavailable.

Architecture tests or equivalent dependency checks SHOULD mechanically enforce this wherever a direct code dependency could otherwise be introduced.

# ROS-NEXT-01 - Ingest Ordo ResolutionObservation

## Requirement

ROS MUST be able to ingest versioned `ordo.resolution-observation` records.

ROS MUST preserve:

- the original raw record;
- schema/version;
- ResolutionId;
- CorrelationId;
- causal predecessor reference;
- ResolutionMode;
- DecisionContract ID/version;
- request ID;
- StateFingerprint;
- provider/model/model-version/adapter-version;
- start/end times;
- structured outcome;
- selected choice;
- confidence magnitude and provenance;
- evidence IDs;
- escalation;
- transition/policy facts;
- provider usage facts exactly as reported;
- retry count;
- experiment/work reference.

## Compatibility

- unsupported semantic schema versions MUST be rejected explicitly;
- unknown additive fields SHOULD be tolerated when required semantics remain intact;
- missing required semantic fields MUST NOT be guessed;
- ingestion MUST be idempotent;
- normalization MUST NOT convert unavailable metrics to zero.

## Rationale

Ordo already emits the observation. ROS is the appropriate historical observer.

Time Tracking Application's versioned TimeObservation processing provides a concrete compatibility/idempotency precedent.

# ROS-NEXT-02 - Retrospective resolution assessment

## Requirement

ROS MUST support a later assessment linked to a ResolutionId.

The assessment MUST keep semantic/epistemic outcome separate from operational outcome.

Candidate dimensions:

### SemanticAssessment
- Confirmed
- Incorrect
- Unresolved
- NotAssessable

### OperationalAssessment
- Succeeded
- Neutral
- RefusedOrDeadEnd
- HarmfulOrFailed
- OutcomeUnknown
- NotAssessable

Exact names remain subject to a schema spike.

## Provenance

Every assessment MUST carry:

- label/evidence references;
- assessment method/assessor;
- assessedAt;
- recordedAt;
- limitations.

## Rationale

Real systems demonstrate combinations such as:

- correct semantic decision + failed effect;
- safe rejection of incorrect proposal;
- deterministic mechanism + unproven business value;
- unknown durable write outcome.

One success/failure field is insufficient.

# ROS-NEXT-03 - Effective-current projections

## Requirement

Where canonical records have lifecycle/supersession semantics, ROS SHOULD expose a generated/validated projection identifying the effective current record.

Historical canonical records MUST remain unchanged.

A projection MUST be rebuildable from canonical history.

## Initial targets

- current governing decision/status;
- superseded architecture decision;
- active research hypothesis/decision;
- current handoff authority pointers.

## Rationale

Time Tracking Application contains an accepted historical Cloudflare authority decision while its later manifest documents direct GitHub persistence. The old record should remain historically true without continuing to masquerade as current authority.

# ROS-NEXT-04 - Handoff authority contract

## Requirement

A durable handoff MUST identify:

- repository and commit/ref it describes;
- current authoritative artifacts;
- superseded/historical decisions relevant to the task;
- facts observed;
- assumptions still in force;
- unresolved unknowns;
- outstanding obligations;
- completed verification;
- next legal action(s).

A handoff SHOULD link to authority instead of copying domain rules.

## Guardrail

Do not claim quantitative handoff superiority until the structured-handoff A/B experiment is run with independent successor sessions.

# ROS-NEXT-05 - Calibration-ready history

## Requirement

ROS MUST retain the facts needed for later contract-specific calibration:

- DecisionContract ID/version;
- provider/model/model-version/adapter-version;
- confidence magnitude and provenance;
- decision time;
- selected choice;
- context/coverage/evidence identity;
- later semantic outcome;
- label provenance;
- evaluation time window.

## Non-requirements

This next pass MUST NOT:

- create global provider reliability scores;
- automatically alter thresholds;
- automatically route providers based on learned scores;
- call provider-reported confidence a calibrated probability.

# ROS-NEXT-06 - Scoped negative/search observations

## Requirement

ROS research/history SHOULD support a scoped search/negative observation when an absence claim may be reused later.

Minimum fields:

- scope;
- method/query identity;
- observed/searchedAt;
- coverage;
- exclusions/errors;
- result.

A searched-not-found result MUST render with its scope and MUST NOT become an unqualified absence claim.

## Rationale

This prevents repeated dead-end work while preserving the difference between "not found here under this search" and "does not exist."

# ROS-NEXT-07 - Unknown effect outcome observation

## Requirement

ROS resolution/execution history SHOULD be able to record:

- effect attempted;
- effect outcome succeeded / failed / unknown;
- reconciliation requested;
- reconciliation result;
- time to resolution;
- whether retry/compensation was blocked.

This is observational. ROS does not own the application's effect state machine.

# ROS-NEXT-08 - Research recommendation record

## Status

Deferred implementation until ROS-NEXT-01 and ROS-NEXT-02 produce enough history.

When implemented, reflection MUST be read-only and recommendation-only.

A recommendation record should contain:

- recommendation ID;
- kind;
- target;
- population/time window;
- sample size;
- evidence references;
- finding;
- counterevidence/limitations;
- proposed experiment/requirement;
- status.

Reflection MUST NOT automatically mutate:

- Ordo contracts;
- policy;
- provider routing;
- thresholds;
- capabilities;
- requirements;
- application state.

# ROS-EXPERIMENT-01 - Structured handoff A/B

Run with independent successor-agent sessions.

Condition A:
repository + structured ROS handoff, no transcript.

Condition B:
repository + conventional narrative handoff.

Measure:

- time/context to first correct action;
- incorrect assumptions;
- duplicate work;
- rediscovery;
- rework;
- constraint violations;
- completion quality;
- cost/tokens where available.

# ROS-EXPERIMENT-02 - Scoped coverage representation

Compare:

- coverage represented purely as Evidence;
- first-class scoped ContextCoverageClaim.

Use Strata and Time Tracking cases.

Choose the smaller representation that preserves all necessary distinctions.

# ROS-EXPERIMENT-03 - Assumption dependency prototype

Use:

- Time Tracking DEC-0002;
- Strata offline-binding assumption;
- Chrona withdrawn verification assumption.

Evaluate whether stable assumption references materially improve:

- effective-current projection;
- impact review;
- handoff accuracy;
- research traceability.

# Data collection before calibration

Do not begin calibration analysis until at least one bounded contract has:

- stable contract version;
- sufficient labeled outcomes;
- meaningful confidence semantics;
- provider/model/version identity;
- adequate sample size.

Until then, collect rather than optimize.

