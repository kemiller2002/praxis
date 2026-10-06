# Affinity-aware AI execution requirements

Status: **Implementation-ready**

Evidence basis:
- EX-ROS-2026-A021
- EX-ROS-2026-A021-R2
- HY-ROS-2026-A028
- HY-ROS-2026-A029

These requirements operationalize the observed result that strongly related work executed in one shared reasoning context can materially reduce cost and elapsed time without an observed acceptance-quality penalty. The approximately 58.7% cost reduction and 61.1% elapsed-time reduction observed in A021-R2 are experimental observations, not guaranteed savings. Until broader replication exists, automatic policy is deliberately conservative.


## Immediate Operational V1

The first production increment MUST optimize for time-to-benefit. The complete execution-plan optimizer is not a prerequisite for using the experimentally supported high-affinity path.

- **PRX-AFF-V1-001** Operational V1 MUST reuse the existing WI-0064 grouping planner and existing work-group machinery. It MUST NOT introduce a second affinity engine merely to satisfy these requirements.
- **PRX-AFF-V1-002** V1 MUST provide an executable policy for clearly high-affinity, high-confidence cohorts. Such cohorts SHOULD use grouped execution with one reasoning owner unless a hard dependency, incompatibility, context limit, repository boundary, or operator override requires another topology.
- **PRX-AFF-V1-003** V1 MUST remain conservative for weak, mixed, or uncertain affinity. Those cases MAY continue through existing independent execution or explicit operator choice until later optimization slices are available.
- **PRX-AFF-V1-004** Before a V1 grouped executor performs substantive product mutation, it MUST commit the cohort architecture/reuse analysis required by PRX-AFF-GRP-001..003.
- **PRX-AFF-V1-005** Every grouped member MUST retain its own acceptance criteria and lifecycle. A group MUST NOT be declared successfully complete until every member has received the per-member acceptance verification required by the work-group contract.
- **PRX-AFF-V1-006** Independent sequential workers MUST use the predecessor-SHA start guard in V1. The R2 stale-checkout deviation MUST NOT remain an accepted operating mode.
- **PRX-AFF-V1-007** V1 MUST record an execution-policy identifier, initially `affinity-execution-v1`, with the execution so results remain attributable after policy evolution.
- **PRX-AFF-V1-008** V1 MUST provide an explicit operator topology choice or override for grouped, independent, or planner-recommended execution, and MUST preserve the override/provenance rather than silently changing the recommendation.
- **PRX-AFF-V1-009** V1 MUST record context/session boundaries. A replacement reasoning context is a continuation boundary and MUST NOT be reported as one uninterrupted shared context.
- **PRX-AFF-V1-010** V1 telemetry MUST capture enough information, when observable, to compute or reconstruct cost-to-accepted-state and time-to-accepted-state. At minimum preserve provider/model/runtime, topology/policy version, cohort/member identity, start/end timing, platform cost and token usage when available, verification/repair/integration executions, context boundaries, and final accepted/blocked state. Missing values remain unknown.
- **PRX-AFF-V1-011** Verification, repair, reconciliation, and integration work performed before acceptance MUST be included in the accepted-state execution history where attributable. First-pass implementation cost MUST NOT be presented as total cost when later work was required for acceptance.
- **PRX-AFF-V1-012** V1 MUST preserve accepted quality as a co-primary outcome. Cost or elapsed-time improvement MUST NOT weaken tests, acceptance criteria, required validation, or merge gates.
- **PRX-AFF-V1-013** V1 MUST be usable on ordinary governed Praxis work immediately after its own acceptance. Production observations MUST be labelled observational evidence and MUST NOT be represented as controlled counterfactual experiments.
- **PRX-AFF-V1-014** Advanced features including dynamic affinity, automatic replanning, budget optimization, causal-learning automation, task-scale normalization, and automatic policy rollback MAY ship after V1 unless needed to preserve correctness or governance.
- **PRX-AFF-V1-015** The V1 implementation SHOULD minimize new state and new abstractions. Prefer extending the existing grouping, execution, checkpoint, telemetry, and planning contracts.
- **PRX-AFF-V1-016** V1 rollout MUST be reversible by configuration/policy selection without deleting or rewriting execution history.
- **PRX-AFF-V1-017** Documentation MUST state that the A021-R2 savings are motivating evidence, not a promised savings percentage.


### V1 rollout safeguards

These are the final pre-implementation safeguards. After they are incorporated, requirement expansion SHOULD stop unless implementation discovers a correctness, safety, or governance gap.

- **PRX-AFF-V1-018** V1 SHOULD support a shadow/recommendation-only rollout mode in which Praxis records the topology the policy would select without automatically changing execution. The actual operator-selected topology and the shadow recommendation MUST remain distinguishable.
- **PRX-AFF-V1-019** V1 MUST provide a global policy kill switch that disables automatic affinity-based topology selection and falls back to the established execution behavior without requiring code rollback or rewriting historical records.
- **PRX-AFF-V1-020** Automatic grouped selection MUST begin with a conservative, configurable maximum cohort size. Groups above the automatic limit MAY be executed together only through explicit operator approval. The limit and policy version MUST be recorded with the decision.
- **PRX-AFF-V1-021** Praxis MUST NOT silently fall back from grouped execution to independent execution, or vice versa, after an execution decision is recorded. If the selected topology cannot start or continue, Praxis MUST record the failure/reason and require an explicit governed replan or override so outcome telemetry remains attributable to the topology actually used.
- **PRX-AFF-V1-022** Every V1 topology decision MUST produce a durable decision receipt containing, where applicable: candidate work/cohort, selected topology, policy version, affinity evidence/classification, material exclusions or conflicts, expected predecessor SHA, provider/model/runtime constraints known at decision time, operator override and reason, shadow recommendation if enabled, and the resulting execution/group identity.
- **PRX-AFF-V1-023** Once the V1 release gate passes, Praxis SHOULD dogfood the capability on suitable remaining Praxis work. Dogfood executions remain ordinary observational production evidence unless separately preregistered as controlled experiments.
- **PRX-AFF-V1-024** V1 MUST support a conservative circuit-breaker state that disables automatic selection and returns the policy to recommendation-only mode when configured accepted-state quality or rework/integration indicators materially regress, or when an operator invokes the breaker.
- **PRX-AFF-V1-025** Circuit-breaker activation MUST be durable and attributable, MUST affect future topology decisions only, and MUST NOT relabel or delete prior outcomes. Automatic re-enablement MUST NOT occur without an explicit configured rule backed by sufficient observations or an operator decision.
- **PRX-AFF-V1-026** Shadow-mode observations MUST NOT be treated as counterfactual outcomes: they record what the policy would have selected, not what that unexecuted topology would have cost or how it would have performed.
- **PRX-AFF-V1-027** The first production rollout SHOULD proceed recommendation-only/shadow first, then conservative automatic grouping for clearly high-affinity cohorts after the release gate and initial decision receipts demonstrate correct policy behavior. Explicit operator grouping remains available throughout.

### V1 release gate

Operational V1 is ready for use when all of the following are demonstrated:

1. an existing high-confidence planner group can be selected for grouped execution;
2. the executor produces and commits architecture/reuse analysis before substantive implementation;
3. grouped member identities, lifecycle, acceptance criteria, evidence, and checkpoints remain distinct;
4. independent sequential execution refuses a stale predecessor SHA;
5. an operator can select/override grouped, independent, or planner-recommended topology with durable provenance;
6. execution records identify `affinity-execution-v1`, provider/model/runtime, topology, and context boundaries;
7. accepted-state history includes attributable verification/repair/integration work rather than stopping at first implementation;
8. every member receives individual acceptance verification;
9. required tests and repository validation pass before merge-ready status;
10. disabling or replacing the policy affects future planning without rewriting prior observations.

Once this gate passes, Praxis SHOULD begin using V1 for clearly high-affinity ordinary work while the remaining optimization requirements continue incrementally.

## Execution topology

- **PRX-AFF-EXEC-001** Praxis MUST represent grouped execution, independent execution, and affinity-clustered execution as distinct execution topologies.
- **PRX-AFF-EXEC-002** Grouped execution MUST assign a related cohort to one continuing reasoning context while preserving each member's independent lifecycle, acceptance criteria, checkpoints, evidence, and provenance.
- **PRX-AFF-EXEC-003** Independent execution MUST use a fresh reasoning context per item and MUST verify that the worker starts from the expected predecessor commit before it reads task material or performs implementation work.
- **PRX-AFF-EXEC-004** A mismatch between actual HEAD, remote branch head, tracking ref, and expected predecessor SHA MUST stop or repair the worker start and MUST be recorded as a deviation.
- **PRX-AFF-EXEC-005** Praxis SHOULD support affinity-clustered execution in which a mixed work set is partitioned into cohesive clusters, with shared context inside a cluster and independent or parallel execution across clusters when dependencies permit.

## Affinity evidence

- **PRX-AFF-010** Affinity MUST be derived from evidence available before implementation.
- **PRX-AFF-011** Affinity analysis MUST be able to consider shared domain concepts, durable state, invariants, production modules/files, public surfaces, producer/consumer relationships, dependencies, ordering constraints, validation rules, error models, transaction boundaries, tests, and assumption propagation.
- **PRX-AFF-012** Praxis MUST retain inspectable evidence supporting an affinity classification or clustering decision. A scalar score MAY assist planning but MUST NOT be the sole explanation.
- **PRX-AFF-013** Absence of an explicit dependency edge MUST NOT be treated as proof of low affinity. Analysis SHOULD inspect requirement text, acceptance criteria, detailed work records, referenced requirements, architecture records, domain contracts, and predicted implementation surfaces where available.
- **PRX-AFF-014** Dependency and affinity MUST remain separate concepts. Dependency determines legal or useful ordering; affinity informs whether shared reasoning context is likely to reduce repeated work or architectural divergence.
- **PRX-AFF-015** Affinity analysis MUST distinguish strong, weak, mixed, and uncertain evidence rather than manufacturing certainty from missing metadata.

## Policy selection

- **PRX-AFF-POL-001** When a cohort has clearly strong architectural affinity, Praxis SHOULD recommend grouped execution and explain the evidence for the recommendation.
- **PRX-AFF-POL-002** Clearly weak-affinity work MUST NOT be automatically grouped merely because the items are simultaneously ready. Until HY-ROS-2026-A029 receives broader support, Praxis SHOULD prefer independent execution or separate clusters for clearly unrelated work.
- **PRX-AFF-POL-003** Mixed work SHOULD be partitioned into affinity clusters when the evidence supports a defensible partition.
- **PRX-AFF-POL-004** When affinity is uncertain, Praxis MUST expose the uncertainty and supporting observations. It MAY recommend a topology but MUST distinguish a recommendation from an established policy.
- **PRX-AFF-POL-005** Automatic topology selection MUST initially be conservative. Praxis MAY automatically select grouped execution only when configured high-affinity criteria are clearly satisfied.
- **PRX-AFF-POL-006** Execution-policy and affinity-model versions MUST be durable execution metadata so historical outcomes can be compared against the policy that selected them.

## Architecture-first grouped execution

- **PRX-AFF-GRP-001** Before substantive product implementation, a grouped executor MUST inspect the entire cohort and produce a committed cohort-level architecture analysis.
- **PRX-AFF-GRP-002** The architecture analysis MUST identify shared concepts, invariants, reusable abstractions, likely common implementation surfaces, integration boundaries, duplicated-implementation risks, concurrency/transaction concerns, and material cross-item decisions.
- **PRX-AFF-GRP-003** The architecture analysis MUST be committed before substantive implementation so prospective architecture decisions can be distinguished from retrospective explanation.
- **PRX-AFF-GRP-004** Grouping MUST NOT collapse work-item boundaries. Every member retains its ID, requirements, acceptance criteria, dependencies, state, checkpoints, completion evidence, and provenance.
- **PRX-AFF-GRP-005** Completion of a group MUST NOT imply completion of every member. The executor MUST perform a final acceptance pass against each member and complete or block each member truthfully.

## Continuity

- **PRX-AFF-CONT-001** Grouped execution MUST remain recoverable when the originating reasoning context disappears.
- **PRX-AFF-CONT-002** Durable state MUST identify the cohort, architecture analysis, material decisions, completed work, remaining work, repository SHA, active/current member state, relevant verification state, unresolved questions, and next action.
- **PRX-AFF-CONT-003** A successor MUST be able to continue without private conversation history, provider-local memory, or the predecessor's filesystem.

## Cost and execution telemetry

- **PRX-AFF-MET-001** Governed executions SHOULD capture, when observable: platform cost, model, model requests, input/output/cache-read/cache-write tokens, active execution time, wall-clock time, file reads, repeated reads, searches, builds, test runs, failed builds/tests, retries, context compactions, commits, changed files, source/test LOC, and CI executions.
- **PRX-AFF-MET-002** Unsupported or unavailable telemetry MUST be represented as unknown/null, never zero.
- **PRX-AFF-MET-003** Grouped execution MUST preserve total cohort measurements and MAY preserve attributable per-item measurements where attribution is meaningful. Shared operations MUST NOT be arbitrarily allocated to members.
- **PRX-AFF-MET-004** Praxis SHOULD retain observations sufficient to compare grouped, independent, and clustered execution while preserving relevant context such as repository, model, work complexity, cohort size, affinity, runtime, and environment.
- **PRX-AFF-MET-005** Praxis MUST distinguish observed historical savings, predicted savings, and actual current-run measurements.
- **PRX-AFF-MET-006** Experimental observations such as the A021-R2 58.7% cost reduction MUST NOT be presented as guaranteed savings for a future execution.

## Quality guardrails

- **PRX-AFF-QUAL-001** Cost reduction MUST NOT be considered a successful execution-policy outcome when implementation quality materially declines.
- **PRX-AFF-QUAL-002** Comparative quality evidence SHOULD include acceptance defects, regressions, architecture consistency, duplicated abstractions, conflicting invariants, validation duplication, storage fragmentation, error-model consistency, transaction/concurrency correctness, maintainability, and tests.
- **PRX-AFF-QUAL-003** Praxis MUST preserve the distinction between cheaper execution and a better engineering outcome. Policy SHOULD optimize engineering value rather than minimum token use.
- **PRX-AFF-QUAL-004** Ready-to-merge work MUST satisfy the repository's normal validation and CI requirements regardless of topology. In-progress branches MAY follow the repository's configured intermediate-CI policy.

## Planner and operator surface

- **PRX-AFF-PLAN-001** Praxis planning output SHOULD expose affinity evidence and proposed execution clusters in machine-readable and human-readable forms.
- **PRX-AFF-PLAN-002** A proposed cluster SHOULD identify its members, affinity classification, recommended topology, evidence/reasons, and uncertainty where applicable.
- **PRX-AFF-PLAN-003** Planning MUST NOT silently convert an affinity recommendation into a dependency relationship or lifecycle transition.
- **PRX-AFF-PLAN-004** The exact CLI surface MAY evolve with existing Praxis conventions, but automation MUST have a stable typed representation of the same information.

## Evidence feedback

- **PRX-AFF-EV-001** Normal governed executions SHOULD be capable of contributing structured observations to the execution-policy evidence corpus when repository policy permits.
- **PRX-AFF-EV-002** Praxis SHOULD support analysis of realized grouped-versus-independent cost, time, repeated context acquisition, quality findings, and architectural consistency by affinity level.
- **PRX-AFF-EV-003** Operational observations MUST NOT automatically promote a research hypothesis to established fact. Default-policy changes MUST cite supporting evidence.
- **PRX-AFF-EV-004** Follow-on research for HY-ROS-2026-A029 SHOULD compare grouped and independent execution within repository blocks, measure task scale, preserve blind quality evaluation, retain raw usage/cost telemetry, and preregister analysis before execution.

## Initial evidence-calibrated policy

Until additional replication is available:

1. clearly high-affinity cohort: recommend grouped execution;
2. clearly low-affinity cohort: prefer independent execution or separate clusters;
3. mixed cohort: partition into defensible affinity clusters;
4. uncertain cohort: expose analysis and recommend rather than silently decide.

## Implementation acceptance

The capability is complete when:

1. Praxis can analyze affinity among multiple work items using inspectable evidence.
2. It can propose defensible execution clusters.
3. Strongly cohesive cohorts receive a grouped recommendation.
4. Weakly related items are not automatically grouped.
5. Dependency and affinity remain distinct.
6. Grouped execution requires prospective cohort architecture analysis.
7. Individual work-item acceptance and lifecycle state remain independently tracked.
8. Independent workers have mandatory predecessor-SHA start guards.
9. Interrupted grouped execution can resume from durable repository/Praxis state.
10. Cost, token, time, and execution telemetry are captured where available without converting unknowns to zero.
11. Historical observations, predictions, and actual current-run measurements are distinguishable.
12. Affinity-model and execution-policy versions are recorded.
13. Tests cover affinity classification, clustering, topology selection, start guards, continuity, telemetry semantics, and quality guardrails.
14. Existing workflows remain backward compatible unless an explicit migration is documented.
15. Documentation explains both the evidence supporting grouped execution and the current limits of that evidence.


## Execution-plan model

Grouped versus independent execution is one decision inside a larger execution plan. Praxis SHOULD plan the work graph, clustering, ordering, parallelism, context boundaries, integration points, and economic constraints together.

- **PRX-AFF-XPLAN-001** Praxis SHOULD represent a durable execution plan containing the selected work set, dependency graph, affinity clusters, incompatibilities/conflicts, topology per cluster, ordering constraints, permitted parallelism, integration points, policy/model versions, uncertainty, and relevant budget constraints.
- **PRX-AFF-XPLAN-002** The execution plan MUST distinguish observed facts, predictions, policy decisions, and operator overrides.
- **PRX-AFF-XPLAN-003** Planning SHOULD identify the critical path and SHOULD avoid serializing independent clusters when safe parallel execution can reduce time-to-accepted-state.
- **PRX-AFF-XPLAN-004** Parallelism decisions MUST consider integration contention in addition to logical dependency. Predicted overlap in production files, schemas, migrations, generated artifacts, shared state, or evaluator surfaces SHOULD be represented as merge/integration risk.
- **PRX-AFF-XPLAN-005** Praxis SHOULD be capable of selecting an execution plan under configured economic constraints such as maximum cost, elapsed-time target, concurrency limit, or context budget without weakening acceptance or quality gates.
- **PRX-AFF-XPLAN-006** An operator MUST be able to override a recommended topology, cluster, or scheduling choice. The override, actor, reason, affected plan decision, and resulting policy deviation MUST be recorded rather than hidden.
- **PRX-AFF-XPLAN-007** Execution-plan policy MUST be versioned and rollback-capable. Changing or rolling back policy MUST NOT rewrite historical plans or execution outcomes.

## Cohort size and context pressure

- **PRX-AFF-CTX-001** Strong affinity alone MUST NOT imply an unbounded grouped cohort.
- **PRX-AFF-CTX-002** Group planning MUST consider cohort size, estimated reasoning/context load, requirement volume, implementation surface, and provider/model context constraints.
- **PRX-AFF-CTX-003** When a cohesive cohort is too large for one reliable reasoning context, Praxis SHOULD partition it into the smallest defensible subclusters that preserve important shared invariants and ordering.
- **PRX-AFF-CTX-004** Context exhaustion, compaction pressure, or provider session termination MUST trigger a durable recovery boundary before continuation where possible.
- **PRX-AFF-CTX-005** Continuing grouped work in a replacement reasoning context MUST be represented truthfully as a continuation with a context boundary, not as uninterrupted single-context execution. Research telemetry MUST preserve the distinction.
- **PRX-AFF-CTX-006** Context-budget thresholds and cohort-size limits MUST be configurable/versioned rather than embedded as undocumented constants.

## Predicted, observed, and negative affinity

- **PRX-AFF-DYN-001** Praxis MUST distinguish pre-execution predicted affinity from relationships observed during implementation.
- **PRX-AFF-DYN-002** Discovery of materially stronger, weaker, or different coupling MAY trigger a controlled replan. The original prediction, new evidence, replan reason, and resulting execution-plan change MUST remain durable.
- **PRX-AFF-DYN-003** Replanning MUST NOT rewrite the original affinity evidence or make a prediction appear retrospectively correct.
- **PRX-AFF-DYN-004** Praxis SHOULD represent negative affinity or execution incompatibility separately from weak affinity. Examples include mutually exclusive approaches, conflicting migrations, incompatible evaluator changes, or changes that should not share a mutation window.
- **PRX-AFF-DYN-005** Negative-affinity/conflict evidence MAY prohibit grouping or parallel execution even when other affinity signals are strong.

## Economics to accepted state

- **PRX-AFF-ECO-001** The primary economic outcome SHOULD be cost-to-accepted-state, not cost of the initial implementation session alone.
- **PRX-AFF-ECO-002** The primary schedule outcome SHOULD be time-to-accepted-state, not first-agent elapsed time alone.
- **PRX-AFF-ECO-003** Cost-to-accepted-state SHOULD include observable implementation, verification, integration, conflict resolution, repair/rework, required reruns, and orchestration cost attributable to reaching accepted state.
- **PRX-AFF-ECO-004** Time-to-accepted-state SHOULD include the same lifecycle through the first accepted/release-ready state while separately reporting active execution time and external waiting time where observable.
- **PRX-AFF-ECO-005** Integration and reconciliation work caused by an execution topology SHOULD be attributable to that plan where evidence supports attribution; Praxis MUST NOT make arbitrary attribution when causality is unknown.
- **PRX-AFF-ECO-006** A low-cost initial implementation followed by substantial repair MUST NOT be reported as cheaper than an alternative using only first-pass execution cost.
- **PRX-AFF-ECO-007** Accepted quality is a co-primary outcome with cost-to-accepted-state and time-to-accepted-state. An execution policy MUST NOT optimize either economic measure by weakening acceptance criteria or required quality evidence.

## Complexity and comparison normalization

- **PRX-AFF-CMP-001** Execution observations SHOULD retain pre-execution task-scale indicators sufficient to avoid comparing materially different work as though it were equivalent.
- **PRX-AFF-CMP-002** Indicators MAY include cohort/member count, acceptance-criterion count, dependency structure, requirement volume, predicted implementation surfaces, affected subsystems, test/evaluator scope, and other deterministic repository-derived measures.
- **PRX-AFF-CMP-003** No single complexity proxy, including acceptance-criterion count or LOC, SHALL be treated as a complete measure of task size.
- **PRX-AFF-CMP-004** Historical policy comparisons SHOULD stratify or otherwise account for repository, task scale, affinity, model/provider/runtime, and environment when those factors are available.
- **PRX-AFF-CMP-005** Praxis MUST preserve provider/model/runtime identity for outcome analysis because execution-topology effects MAY vary by provider or model.

## Observational versus experimental evidence

- **PRX-AFF-CAUSAL-001** Praxis MUST distinguish controlled experimental evidence from observational production evidence.
- **PRX-AFF-CAUSAL-002** A normal production execution reveals the outcome of the selected plan but MUST NOT fabricate or infer an unobserved counterfactual cost for a topology that did not run.
- **PRX-AFF-CAUSAL-003** Predictions MAY use historical observational data, but their provenance, uncertainty, and non-experimental status MUST remain visible.
- **PRX-AFF-CAUSAL-004** Claims that one topology causes lower cost, faster completion, or better quality SHOULD rely on controlled comparisons or an explicitly documented causal design rather than production telemetry alone.

## Policy safety and learning

- **PRX-AFF-LEARN-001** New execution-policy versions SHOULD be evaluated against prior policy using cost-to-accepted-state, time-to-accepted-state, and accepted quality rather than token reduction alone.
- **PRX-AFF-LEARN-002** Praxis SHOULD support a configured rollback trigger or operator decision when a policy version materially worsens accepted outcomes.
- **PRX-AFF-LEARN-003** Rollback MUST affect future planning only and MUST preserve historical policy/version attribution.
- **PRX-AFF-LEARN-004** Provider-specific evidence MAY inform provider-specific planning, but Praxis MUST NOT silently generalize a measured topology effect from one provider/model to all providers/models.

## Additional implementation acceptance

The capability is not complete until the execution-planning layer also demonstrates that:

1. cohort size/context pressure can limit or split otherwise high-affinity groups;
2. context replacement is recorded as a boundary rather than hidden;
3. predicted affinity and observed affinity are independently retained;
4. controlled replanning preserves the original plan and evidence;
5. negative affinity/incompatibility can prevent unsafe grouping or parallelism;
6. critical-path and integration-contention information can influence scheduling;
7. operator overrides are durable and attributable;
8. cost-to-accepted-state and time-to-accepted-state can be represented without collapsing unavailable data to zero;
9. verification, repair, and integration cost can be included when attributable;
10. task-scale indicators accompany comparable execution observations;
11. provider/model/runtime remain comparison dimensions;
12. observational evidence is distinguishable from controlled experimental evidence;
13. execution-plan policy can be versioned and rolled back prospectively;
14. planning can represent configured budget/time/concurrency/context constraints without weakening acceptance quality.

## Suggested implementation slices

The requirements are intentionally separable:

1. **Affinity analysis and clustering**: PRX-AFF-010..015 and PRX-AFF-PLAN-001..004.
2. **Topology selection and orchestration**: PRX-AFF-EXEC-001..005, PRX-AFF-POL-001..006, PRX-AFF-GRP-001..005, PRX-AFF-CONT-001..003.
3. **Execution-plan optimization**: PRX-AFF-XPLAN-001..007, PRX-AFF-CTX-001..006, PRX-AFF-DYN-001..005.
4. **Economics, comparison, and learning**: PRX-AFF-MET-001..006, PRX-AFF-ECO-001..007, PRX-AFF-CMP-001..005, PRX-AFF-CAUSAL-001..004, PRX-AFF-QUAL-001..004, PRX-AFF-EV-001..004, PRX-AFF-LEARN-001..004.

An implementing agent SHOULD first inventory existing planning, work-group, orchestration, telemetry, and quality-gate capabilities and reuse them rather than introducing parallel state or duplicate abstractions.
