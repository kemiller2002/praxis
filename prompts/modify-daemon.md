ROS Daemon -- Provider-Neutral Model Routing Implementation Mission

You are acting as a principal software architect, AI-agent infrastructure engineer, developer-tooling engineer, distributed-systems engineer, and skeptical maintainer.

Your task is to extend the Repository Operating System execution daemon so that LLMs are treated as interchangeable execution resources rather than being hard-coded to one provider or one model.

The objective is not merely to add DeepSeek, Qwen, GLM, or another inexpensive model.

The objective is to build a provider-neutral execution architecture that can:

* route bounded work to the least expensive permitted model likely to succeed
* escalate explicitly when necessary
* enforce repository/data governance before invocation
* preserve deterministic correctness boundaries
* isolate failed attempts
* produce comparable telemetry across providers
* learn model effectiveness empirically over time
* remain independent of any one model vendor
* maintain segregation of duties between ROS and the external project-management/telemetry system

Do not build a generic AI framework.

Do not redesign unrelated ROS architecture.

Prefer the smallest coherent implementation.

⸻

Existing Repository Context

Before making changes, inspect the current Repository Operating System repository carefully.

At minimum inspect:

* root AGENTS.md
* Agent Operating Manual
* architecture/context documents
* CLI implementation
* package structure
* schemas
* validation mechanisms
* bootstrap behavior
* tests
* GitHub Actions workflows
* existing execution/worker code, if it has been added since this prompt was written
* any daemon configuration
* work-item protocol
* project-management adapter contracts
* telemetry contracts
* repository isolation/worktree behavior

Do not assume the architecture described in this prompt exactly matches the current code.

Adapt to what actually exists.

Preserve existing good conventions.

⸻

First Requirement: Inspect Before Modifying

Before making code changes, produce a concise architectural assessment covering:

1. Existing execution abstractions.
2. Existing agent/worker lifecycle.
3. Existing provider or model assumptions.
4. Where provider-specific concepts leak into core code.
5. How work-item execution policy is currently represented.
6. How repository/data sensitivity is represented.
7. Existing telemetry/logging structures.
8. Existing execution identities and attempt identities.
9. Existing worktree/container/sandbox isolation behavior.
10. Existing deterministic validation boundaries.
11. Existing external PM/datastore integration.
12. Existing protocol/versioning strategy.

Then produce the smallest migration plan necessary.

Only after that should implementation begin.

⸻

Core Principle

Model selection belongs to work-item execution policy.

Do not select a model merely because it is configured globally.

A work item should define or derive enough information to determine appropriate execution resources.

Relevant dimensions include:

* task type
* semantic complexity
* risk level
* data sensitivity
* required capabilities
* permitted execution locations
* allowed providers
* prohibited providers
* maximum token budget
* maximum monetary budget
* required validation
* required review
* escalation policy

Do not assume the most capable model should execute every task.

The system should eventually optimize for:

The least expensive permitted execution path with sufficient demonstrated probability of producing an accepted work result.

This is different from selecting the cheapest API call.

Total expected cost includes:

* model inference
* retries
* validation loops
* review
* escalation
* rework
* escaped defects

⸻

Architectural Boundary

Maintain strict segregation of duties.

ROS owns

* execution protocol
* policy interpretation
* provider-neutral interfaces
* model routing rules
* governance checks
* legal execution transitions
* deterministic validation
* evidence requirements
* escalation protocol
* schema definitions
* telemetry event contracts
* versioning

Repository owns

* source code
* tests
* architecture constraints
* local execution policy
* repository-specific data classification
* acceptance criteria
* evidence
* actual implementation history

External Project Management / Telemetry System owns

* canonical work-item operational records
* work queues
* assignments
* priorities
* portfolio views
* execution history storage
* aggregated telemetry
* model effectiveness statistics
* dashboards
* Kanban/project-management state

ROS must not become the operational PM database.

⸻

Provider-Neutral Execution Interface

Create a small provider-neutral interface.

Conceptually:

AgentExecutor
    |
    +-- ModelProvider
          +-- OpenAI
          +-- Anthropic
          +-- DeepSeek
          +-- GLM / Z.AI
          +-- Qwen / Alibaba
          +-- Local / private inference

Do not implement all providers immediately.

The architecture must support them, but the first working milestone should require only enough providers to prove interchangeability.

Provider-specific behavior must remain behind adapters.

Core daemon and ROS code must not depend on:

* OpenAI-specific message formats
* Anthropic-specific cache features
* DeepSeek-specific parameters
* Qwen-specific concepts
* vendor-specific tool schemas

unless a concept is explicitly represented as provider capability metadata.

⸻

Minimal Provider Contract

Design the provider contract around normalized input and normalized output.

Conceptually:

class ModelProvider {
  async execute(request) {
    throw new Error("Not implemented");
  }
}

A provider-neutral execution request should contain concepts such as:

{
  executionId,
  attemptId,
  workItem,
  model,
  workingDirectory,
  context: {
    stable,
    variable
  },
  capabilities: {
    tools: true,
    structuredOutput: true
  },
  limits: {
    maxInputTokens,
    maxOutputTokens,
    maxCostUsd
  }
}

Exact structure may differ based on existing code.

Every provider should return normalized execution data including:

{
  status,
  usage: {
    inputTokens,
    cachedInputTokens,
    outputTokens
  },
  toolCalls,
  terminationReason,
  providerMetadata
}

Provider-specific metadata may remain under providerMetadata.

Do not leak vendor-specific fields into core routing logic unless they become a formally supported capability.

⸻

Separate Provider and Model Definitions

Do not treat a model identifier as a provider.

Keep these concepts separate.

Example:

providers:
  openai:
    adapter: openai
    credential_env: OPENAI_API_KEY
  deepseek:
    adapter: deepseek
    credential_env: DEEPSEEK_API_KEY

Models:

models:
  economical-code-model:
    provider: deepseek
    model_id: configured-provider-model-name
    tier: economical
  frontier-code-model:
    provider: openai
    model_id: configured-provider-model-name
    tier: frontier

Actual model identifiers belong in configuration.

Do not encode volatile model names throughout the source code.

⸻

Model Capability Metadata

Each configured model should expose normalized capability metadata.

Example:

models:
  economical-code-model:
    provider: deepseek
    tier: economical
    capabilities:
      tools: true
      structured_output: true
      long_context: true
      code_execution_agent: true
    limits:
      context_tokens: 128000
    cost:
      input_per_million: ...
      cached_input_per_million: ...
      output_per_million: ...
    governance:
      external_processing: true
      execution_region: ...

Pricing must be configuration/data.

It changes too frequently to belong in architecture.

⸻

Model Tiers

Support policy-oriented resource tiers.

Initial conceptual tiers:

economical
standard
frontier
human

These tiers are routing/resource classes.

They are not correctness guarantees.

Example:

model_tiers:
  economical:
    candidates:
      - economical-code-model
  standard:
    candidates:
      - standard-code-model
  frontier:
    candidates:
      - frontier-code-model-a
      - frontier-code-model-b

Do not scatter specific model IDs throughout routing code.

⸻

Routing Algorithm

Routing must be constraint filtering first, optimization second.

Conceptually:

configured models
      |
      v
provider permitted?
      |
      v
data governance satisfied?
      |
      v
execution region allowed?
      |
      v
required capabilities present?
      |
      v
risk policy allows model/tier?
      |
      v
context/token requirements satisfied?
      |
      v
budget permits model?
      |
      v
eligible candidate set
      |
      v
choose best permitted candidate

Do not:

choose cheapest model
      |
      v
check whether it was allowed

Governance and capability checks must happen before provider invocation.

⸻

V1 Routing Strategy

Do not over-engineer statistical routing initially.

For V1:

1. Derive execution policy.
2. Determine allowed tier.
3. Obtain configured candidates for that tier.
4. Filter by governance/capabilities.
5. Select the first eligible configured candidate.
6. Record the routing decision.

Later, empirical effectiveness data may influence candidate ordering.

⸻

Work-Item Execution Policy

Introduce or extend an explicit execution policy.

Conceptually:

execution_policy:
  task_type: engineering
  semantic_complexity: low
  risk: medium
  data:
    classification: internal
  required_capabilities:
    - code_execution_agent
    - tools
  budget:
    max_cost_usd: 2.50
    max_attempts: 3
    max_input_tokens: ...
    max_output_tokens: ...
  model_policy:
    allowed_tiers:
      - economical
      - standard
      - frontier
  review:
    policy: independent_model
  escalation:
    order:
      - economical
      - standard
      - frontier
      - human

This should generally be derived from:

repository policy
+
work-item characteristics
+
risk
+
data sensitivity

Do not require every work item to repeat all repository defaults.

⸻

Data Governance

Provider eligibility must be enforceable before any repository content is sent externally.

Support repository/work-item classification such as:

data_policy:
  classification: confidential
  allowed_execution_locations:
    - local
    - us
  allowed_providers:
    - openai
    - local
  prohibited_providers:
    - deepseek-hosted
    - qwen-hosted

Another repository may permit broader use.

Governance should be able to account for:

* regulated data
* source-code sensitivity
* secrets
* customer contracts
* IP restrictions
* export restrictions
* geographic processing requirements
* provider contractual status
* repository-specific prohibitions

The normal execution API must refuse prohibited provider invocation.

Test this deterministically.

⸻

Local and Private Models

The architecture must support local/private inference.

Do not make local models a separate conceptual system.

They should implement the same ModelProvider contract.

Potential future examples may include:

* open-weight Qwen
* DeepSeek-derived models
* GLM-family models
* other local coding models

The first implementation does not need to run a real local model.

But the interface must not make local execution awkward or impossible.

⸻

Cheap Models Are Workers, Not Authorities

Do not allow a model to determine that its own work is correct.

The required execution pattern is:

work item
    |
    v
model proposes changes
    |
    v
deterministic validation
    |
    +-- build
    +-- tests
    +-- static checks
    +-- architecture rules
    +-- invariants
    +-- acceptance conditions
    |
    v
evidence produced
    |
    v
legal work-item transition

The model may request completion.

The environment determines whether completion requirements are satisfied.

Preserve this architectural rule regardless of model tier.

⸻

Review Policy

Review requirements must be policy-driven.

Conceptually:

low risk
    deterministic validation may suffice
medium risk
    independent model review may be required
high risk
    frontier-model review may be required
critical
    human authorization may be required

Do not hard-code these exact rules.

Implement a review-policy abstraction that allows repository/work-item configuration.

⸻

Execution, Attempt, and Work-Item Identity

Keep these separate.

Example:

Work Item
  FEAT-142
Execution
  EXEC-812
Attempts
  ATTEMPT-1
  ATTEMPT-2
  ATTEMPT-3

A model escalation creates a new attempt, not a new work item.

A work-item state should not be confused with execution state.

An execution failure does not necessarily mean the work item failed.

⸻

Execution State

Define explicit execution state.

A reasonable conceptual model:

created
  |
  v
routing
  |
  v
preparing
  |
  v
executing
  |
  v
validating
  |
  +--> accepted
  |
  +--> escalating
  |
  +--> needs_human
  |
  +--> failed

Exact states should fit the existing daemon architecture.

Do not represent execution progress as uncontrolled strings.

⸻

Explicit Escalation

Support explicit model escalation:

economical
    |
    v
standard
    |
    v
frontier
    |
    v
human

Escalation triggers may include:

* validation failure
* repeated validation failure
* inability to parse/understand task
* missing capability
* context/token exhaustion
* architecture-sensitive work
* security-sensitive work
* work-item policy
* repository policy
* model/provider failure
* exceeded attempt budget

Do not silently switch providers or models.

Every escalation must produce a durable normalized event.

Example:

{
  "event": "execution.model_escalated",
  "executionId": "EXEC-812",
  "workItemId": "FEAT-142",
  "fromTier": "economical",
  "toTier": "standard",
  "fromModel": "economical-code-model",
  "toModel": "standard-code-model",
  "reason": "validation_failure",
  "attempt": 2
}

⸻

Failure Isolation

Each attempt must execute in isolation.

Do not allow an unsuccessful inexpensive-model attempt to contaminate the next model’s workspace automatically.

Preferred model:

EXEC-812
  |
  +-- ATTEMPT-1
  |      isolated worktree/container
  |
  +-- ATTEMPT-2
  |      fresh isolated worktree/container
  |
  +-- ATTEMPT-3
         fresh isolated worktree/container

On failure:

preserve
    logs
    diff
    execution record
    validation evidence
discard
    mutable workspace

A subsequent model may receive previous attempt information only through explicit context.

Examples:

* previous diff
* validation failure summary
* tool logs
* attempt result

Do not implicitly inherit modified filesystem state.

⸻

Worktree / Sandbox Requirements

Reuse existing isolation behavior if present.

Otherwise implement the smallest safe mechanism.

At minimum:

* fresh branch/worktree per attempt
* clean base commit
* no uncontrolled previous attempt state
* cleanup after completion/failure
* durable preservation of diff/log metadata before cleanup

Container isolation may be deferred if worktree isolation is sufficient for V1.

Document the security limitations of worktree-only execution.

⸻

Stable and Variable Context

Represent context in two semantic sections.

Example:

stable context
    ROS protocol
    repository rules
    architecture constraints
    tool contracts
    coding standards
    state/transition rules
    validation requirements
variable context
    current work item
    acceptance criteria
    relevant evidence
    selected files
    previous attempt summary

This structure should remain provider-neutral.

Provider adapters may exploit provider-side caching when available.

Do not make ROS depend on any provider-specific prompt-caching mechanism.

⸻

Fresh Sessions

Do not use previous conversational agent sessions as operational truth.

Each attempt should start from canonical durable state.

Previous execution results may be supplied explicitly as structured evidence.

The model session itself is disposable.

Durable continuity belongs in:

* work item
* repository
* ROS state
* decisions
* evidence
* execution records
* telemetry

Not in the model’s conversational memory.

⸻

Execution Records

Every attempt must emit a normalized execution record.

At minimum record:

* work-item ID
* repository
* execution ID
* attempt ID
* attempt number
* selected tier
* provider
* model
* start time
* end time
* input token count
* cached input token count if known
* output token count
* estimated cost
* tools invoked
* tool-call count
* files changed
* diff statistics if practical
* validation results
* review result
* failure reason
* escalation reason
* final disposition

Example shape:

{
  "executionId": "EXEC-812",
  "attemptId": "ATTEMPT-2",
  "workItemId": "FEAT-142",
  "repository": "owner/repo",
  "model": {
    "provider": "provider-name",
    "id": "configured-model-name",
    "tier": "standard"
  },
  "usage": {
    "inputTokens": 31142,
    "cachedInputTokens": 20800,
    "outputTokens": 5521,
    "estimatedCostUsd": 0.14
  },
  "activity": {
    "toolCalls": 23,
    "filesChanged": 7
  },
  "validation": {
    "build": "passed",
    "tests": "passed",
    "architecture": "failed"
  },
  "disposition": "escalated",
  "escalationReason": "architecture_validation_failure"
}

Use schema versioning.

⸻

Telemetry

Create or extend a provider-neutral telemetry interface.

Conceptually:

TelemetrySink

Operations may include:

execution started
routing decision
attempt started
attempt finished
validation completed
review completed
model escalated
execution completed
execution failed
publication outcome

The central PM/telemetry system stores operational records.

ROS owns the event format and semantics.

Do not make ROS itself the central execution-history database.

⸻

Telemetry Reliability

Telemetry failure must not corrupt repository work.

If remote telemetry publication fails:

record locally
    |
    v
spool
    |
    v
retry later

Support idempotency.

Repeated publication of the same event must not create duplicate logical execution events.

Distinguish:

success
failure
unknown

Do not convert an unknown remote write into presumed failure or presumed success.

⸻

Model Effectiveness Data

Design the telemetry schema so future routing can use empirical evidence.

We ultimately want metrics such as:

task_class
model
attempts
successful_attempts
validation_failures
review_rejections
mean_input_tokens
mean_cached_tokens
mean_output_tokens
mean_cost
mean_tool_calls
mean_duration
escalation_rate
cost_per_accepted_work_item

Do not permanently encode assumptions such as:

economical model X is good enough for simple bugs.

Collect evidence instead.

⸻

Future Routing Objective

Do not implement sophisticated adaptive routing until sufficient execution data exists.

But preserve an interface where future routing can ask:

What is the least expensive permitted model with sufficient demonstrated probability of successfully completing this class of work?

Possible future factors:

* task type
* repository
* language
* risk
* semantic complexity
* prior success rate
* review rejection rate
* average cost
* escalation rate
* context size
* capability requirements

Do not introduce machine learning merely because this resembles a ranking problem.

A simple statistical decision function may be enough.

⸻

Deterministic Validation

Existing ROS validation must remain independent of selected model.

Execution acceptance should remain based on deterministic/environmental evidence wherever possible.

Potential checks include:

* build
* unit tests
* integration tests
* static analysis
* formatting where meaningful
* architecture constraints
* dependency policy
* security checks
* state invariants
* generated artifact consistency
* acceptance evidence
* work-item attribution

Models should not be able to override failed validation.

⸻

Repository Work Attribution

Preserve the rule:

Every meaningful repository update must be attributable to one or more work items.

Every model execution must already know its work-item identity.

Provider routing must not create unattributed repository changes.

Execution records, commits, PRs, and telemetry should share identifiers where possible.

Example linkage:

FEAT-142
    |
EXEC-812
    |
ATTEMPT-2
    |
commit
    |
PR

⸻

Daemon Integration

Extend the daemon without turning model routing into the daemon itself.

The daemon remains a deterministic supervisor.

Its conceptual loop becomes:

poll configured repository/work source
        |
        v
claim work
        |
        v
derive execution policy
        |
        v
route model
        |
        v
create isolated attempt
        |
        v
launch provider
        |
        v
validate
        |
        +---- accepted
        |
        +---- escalate
        |
        +---- human
        |
        v
publish telemetry/result
        |
        v
cleanup

Keep routing logic behind a dedicated component.

⸻

Repository Polling

Preserve configurable repository polling.

Daemon configuration should continue to support a fixed allowlist of repositories and per-repository schedules.

Example:

repositories:
  - repository: owner/repo-a
    enabled: true
    schedule:
      every: 5m
  - repository: owner/repo-b
    enabled: true
    schedule:
      every: 30m

Model policy may also be overridden at repository scope.

Do not automatically execute against arbitrary accessible repositories.

⸻

Worker Authority

A worker’s configured repository allowlist and capabilities constrain what it may claim.

Example:

worker:
  id: worker-dev-01
capabilities:
  - engineering
  - research
repositories:
  - owner/repo-a
  - owner/repo-b

If the PM system returns unauthorized work, the daemon must not claim it.

⸻

Configuration

Keep configuration readable and explicit.

A possible high-level shape:

version: 1
worker:
  id: worker-01
execution:
  max_parallel: 1
  max_validation_repairs: 3
providers:
  openai:
    adapter: openai
    credential_env: OPENAI_API_KEY
  economical-provider:
    adapter: provider-adapter
    credential_env: PROVIDER_API_KEY
models:
  economy-code:
    provider: economical-provider
    model_id: configured-model-id
    tier: economical
    capabilities:
      tools: true
      structured_output: true
      code_execution_agent: true
  frontier-code:
    provider: openai
    model_id: configured-model-id
    tier: frontier
    capabilities:
      tools: true
      structured_output: true
      code_execution_agent: true
model_tiers:
  economical:
    candidates:
      - economy-code
  frontier:
    candidates:
      - frontier-code
repositories:
  - repository: owner/repo-a
    schedule:
      every: 5m
    data_policy:
      classification: internal
      allowed_providers:
        - openai
        - economical-provider

This is illustrative.

Fit the actual implementation to the repository’s established configuration conventions.

⸻

Minimal Dependencies

Use built-in platform capabilities whenever practical.

The repository currently favors simple deterministic tooling.

Prefer:

* Node built-in fetch
* filesystem APIs
* subprocess execution
* Git CLI
* standard JSON/YAML already supported by the project

Avoid adding a provider SDK for each vendor unless it materially simplifies a nontrivial protocol requirement.

Do not proliferate dependencies merely for convenience.

⸻

Implementation Phases

Implement incrementally.

Phase 1 -- Provider-neutral contracts

Add:

* ModelProvider
* model configuration
* provider configuration
* execution policy
* model router
* execution record
* telemetry sink abstraction
* schemas
* unit tests

Use at least two mock providers.

Success criterion:

same bounded work request
→ executed using either mock provider
→ normalized comparable execution records

No real model API required yet.

⸻

Phase 2 -- Attempt isolation

Add explicit:

* execution identity
* attempt identity
* isolated workspace per attempt
* cleanup
* diff/log preservation

Test:

attempt 1 modifies workspace
→ fails
→ attempt 2 begins from clean base

⸻

Phase 3 -- Deterministic escalation

Implement:

economical
→ standard
→ frontier
→ human

according to policy.

Escalation must produce an event.

Test:

economical mock fails validation
→ escalation recorded
→ standard mock invoked

⸻

Phase 4 -- Data-governance filtering

Implement deterministic eligibility filtering.

Test at least:

repository prohibits provider
→ provider execute() is never called

and:

required capability unavailable
→ model excluded before invocation

⸻

Phase 5 -- Two real provider adapters

Implement only enough real adapters to prove provider interchangeability.

Use one economical provider and one stronger provider.

Do not add every desired provider yet.

The goal is:

same bounded repository work item
→ can run through provider A
or
→ provider B

with identical core ROS execution flow and comparable records.

If current credentials or environment prevent real calls, implement adapters and integration tests with mocks/stubs, and clearly state what was not exercised.

Do not fabricate provider test results.

⸻

Phase 6 -- Telemetry publication

Add:

* normalized event publication
* external sink
* local spool
* retry
* idempotency
* unknown outcome handling

Ensure telemetry failure cannot falsely mark repository work failed.

⸻

Phase 7 -- Independent review policy

Introduce policy-driven review capability.

Do not assume review always requires another model.

Support concepts such as:

none
deterministic-only
independent-model
frontier-model
human

Exact naming may differ.

⸻

Phase 8 -- Effectiveness-ready telemetry

Ensure records support future aggregation for:

success rate
validation failure
review rejection
mean cost
mean tokens
tool calls
duration
escalation rate
cost per accepted work item

Do not implement advanced routing yet unless trivial.

⸻

Tests

At minimum cover the following.

Provider interchangeability

same execution request
→ provider A
→ normalized result
same execution request
→ provider B
→ same normalized contract

Routing

economical eligible
→ economical selected

Governance exclusion

economical is cheapest
but prohibited by repository
→ never invoked

Capability exclusion

model lacks required tools
→ excluded

Budget exclusion

model exceeds execution budget
→ excluded

Escalation

economical validation failure
→ explicit escalation event
→ standard attempt

Fresh attempt

attempt 1 corrupts worktree
→ attempt discarded
→ attempt 2 starts clean

No silent switching

model changes between attempts
→ escalation event required

Telemetry

attempt completes
→ execution record emitted

Telemetry outage

remote sink unavailable
→ record spooled locally
→ repository work state remains explicit

Duplicate telemetry

same event retried
→ one logical event

Unknown external write

telemetry response uncertain
→ outcome = unknown
→ reconciliation possible

Human escalation

all permitted model tiers exhausted
→ execution requests human disposition

⸻

Benchmarkability

The first release should make it possible to run a controlled comparison.

Example:

Work Item FEAT-142
Attempt A:
    economical model
Attempt B:
    stronger model

Compare:

* validation outcome
* review outcome
* tokens
* cached tokens
* tool calls
* elapsed execution duration
* cost
* files changed
* repair loops
* escalation
* accepted/rejected result

Do not rely solely on public benchmark rankings.

The system should be designed to learn from actual repository work.

⸻

Important Non-Goals

Do NOT build:

* a generic multi-agent framework
* persistent model memory
* a provider marketplace
* a universal prompt abstraction layer beyond what is required
* cross-provider conversation migration
* model self-authorization
* AI-based final correctness authority
* a new PM database inside ROS
* automatic provider discovery
* every possible provider
* statistical/ML routing before data exists
* uncontrolled inheritance of failed model workspaces

⸻

Architectural Invariants

At completion verify all of these remain true:

The same bounded work item can be executed by different providers without changing core daemon behavior.

Provider-specific APIs are isolated behind adapters.

Model names are configuration, not architecture.

Repository data cannot be transmitted to a provider that policy prohibits.

Selecting a cheaper model never reduces deterministic validation requirements unless explicit policy says so.

A model cannot declare its own work correct.

Escalation is explicit and auditable.

Failed attempts cannot silently contaminate later attempts.

Previous agent conversations are not required to continue work.

Every attempt is tied to a work item.

Every attempt produces a comparable execution record.

ROS defines execution semantics but does not become the project-management datastore.

Operational telemetry can be stored externally and rebuilt/analyzed independently.

Local/private inference can implement the same provider interface.

The architecture can later incorporate empirical model-performance data without redesigning the execution subsystem.

If any invariant is false, revise the implementation before considering the mission complete.

⸻

First Required Deliverable

The first implementation milestone must be deliberately small.

Deliver enough to demonstrate:

one bounded work item
        |
        +--> Provider A
        |
        +--> Provider B

through the same execution abstraction, with:

* identical policy processing
* identical governance checks
* isolated attempts
* deterministic validation
* normalized execution records
* explicit provider/model identity
* comparable telemetry

Do not expand beyond this until the contract is proven.

⸻

Final Report

When complete, report:

1. Existing architecture discovered.
2. Provider/model assumptions found.
3. Migration plan used.
4. Files added.
5. Files modified.
6. Provider-neutral interfaces introduced.
7. Execution policy structure.
8. Routing behavior.
9. Governance enforcement.
10. Attempt isolation behavior.
11. Escalation behavior.
12. Validation behavior.
13. Telemetry/event format.
14. Providers implemented.
15. Tests run and actual results.
16. What was not tested.
17. Dependencies added and why.
18. Architectural compromises.
19. Known risks.
20. Deferred capabilities.
21. Recommended next experiment.

Do not claim implementation or testing that did not occur.