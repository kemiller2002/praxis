# Praxis execution orchestration requirements

Status: **Accepted; partially implemented.** The status of every requirement is
in [Implementation status](#implementation-status) below; a test fails if a
requirement is missing from it.

Tracked by GitHub issues #94 and #96. Runtime documentation:
[`docs/execution-runtime.md`](../docs/execution-runtime.md).

These requirements capture runtime and operator-surface gaps identified while
comparing Praxis, Ordo, and Conditor with the Bang workflow on 2026-09-28.
Praxis remains the runtime authority for work execution; Ordo remains the
semantic authority for legal state, capabilities, obligations, and transition
meaning.

## Execution roles

- **PRX-EXEC-001** Praxis MUST support first-class execution roles, including at
  minimum specification, implementation, verification, review, and integration,
  with room for additional specialized roles.
- **PRX-EXEC-002** Every execution role MUST resolve to an explicit capability
  set supplied by the governing semantic authority rather than relying only on
  prompt wording.
- **PRX-EXEC-003** The selected role and effective capability set MUST be bound
  durably to the execution identity and provenance record.
- **PRX-EXEC-004** A verification or runner execution MUST be independently
  constrainable so it can execute and record verification without acquiring
  implementation authority.
- **PRX-EXEC-005** Phase-specific orchestration MUST be able to launch the
  configured worker/provider for the role associated with a legal transition.
- **PRX-EXEC-006** Human-authorization transitions MUST be configurable and
  explicit. An agent MUST NOT silently consume a transition reserved for a
  human actor.

## Isolated execution workspaces

- **PRX-EXEC-010** Praxis MUST be able to create an isolated Git branch and
  worktree automatically for each mutating execution when the repository
  execution policy requires it.
- **PRX-EXEC-011** Workspace identity MUST be recorded with the execution,
  including repository/ref, branch, worktree identity or path, baseline commit,
  and current candidate commit where applicable.
- **PRX-EXEC-012** Workspace creation MUST NOT be represented as a security
  sandbox unless a separate host boundary actually enforces filesystem/process
  isolation.
- **PRX-EXEC-013** Automatic workspace cleanup MUST occur only after a legal
  terminal state or explicit abandonment policy permits it and required
  evidence has been retained.
- **PRX-EXEC-014** Re-entry or resumed execution MUST bind to the intended
  durable workspace/candidate state rather than silently creating a competing
  implementation path.

## Step receipts

- **PRX-EXEC-020** Every executable Praxis step MUST be able to declare an
  expected receipt or postcondition before the action runs.
- **PRX-EXEC-021** Praxis MUST capture the observed receipt after the action
  independently from the expected receipt.
- **PRX-EXEC-022** Receipt comparison MUST be explicit and machine-readable.
- **PRX-EXEC-023** A mismatched receipt MUST stop or hold automatic advancement
  and create durable diagnostic, obligation, or blocked-state information.
- **PRX-EXEC-024** Receipts MUST be associated with the execution, step, actor,
  relevant input/version identity, time, and observed result.
- **PRX-EXEC-025** Repeated or resumed execution MUST preserve prior receipts
  rather than rewriting history.
- **PRX-EXEC-026** Completion and evaluation views MUST prefer recorded receipts
  and authoritative evidence over agent narrative claims when both exist.

## Operator state UI

- **PRX-UI-001** Praxis MUST provide or expose an operator-facing work-state
  surface capable of presenting the current state, evidence, obligations,
  unknowns, and legal next actions for a work item.
- **PRX-UI-002** The operator UI MUST derive legal actions from the same
  transition authority used by the CLI and remote execution path.
- **PRX-UI-003** The UI MUST NOT present an illegal transition as an available
  action.
- **PRX-UI-004** When a transition requires human authorization, the UI MUST
  make that boundary visible and MUST record the resulting human transition
  with normal Praxis provenance.
- **PRX-UI-005** A work-board visualization MAY group or sequence states for
  usability, but visual columns MUST NOT become an independent workflow
  authority.
- **PRX-UI-006** Moving or activating work in the UI MAY launch a configured
  role-specific execution only after the legal transition is accepted.
- **PRX-UI-007** The UI MUST surface receipt mismatch, unresolved obligations,
  unknown effects, and blocked state without collapsing them into generic
  failure.

## Verification authority

- **PRX-VER-001** Praxis MUST support a runner mode whose allowed operation can
  be restricted to a declared verification command or typed verification
  capability.
- **PRX-VER-002** A runner MUST record the command/capability identity,
  candidate commit, evaluator identity/version, exit/result state, and produced
  evidence required by policy.
- **PRX-VER-003** A runner MUST NOT automatically repair implementation or
  evaluator state after a failed verification unless a separate legal
  transition creates an implementation execution with that authority.

## Completion direction

The target experience is not a generic Kanban board. The operator surface
should be a projection of Praxis and Ordo state. It should make legal
transitions obvious while keeping execution, evidence, capability, unknowns,
and obligations authoritative outside the presentation layer.


## Evaluator integrity and repair boundary

- **PRX-VER-004** If the required evaluator, gate, configuration, or evidence
  tool is missing or cannot be identified, the verification execution MUST
  stop or block and report the missing authority rather than installing,
  copying, rewriting, or substituting it inside the candidate execution.
- **PRX-VER-005** Evaluator repair MUST occur through a separate authorized
  execution, after which the candidate MUST be verified again against the
  identified repaired evaluator.

## Host-enforced execution boundaries

- **PRX-SEC-001** Praxis SHOULD support execution policies that bind a role to
  host-enforced filesystem, process, network, credential, and other runtime
  restrictions when the selected host can provide them.
- **PRX-SEC-002** Host-enforced restrictions MUST be recorded separately from
  Ordo/Praxis semantic capabilities so a prompt, branch, worktree, or current
  working directory is never misrepresented as a security boundary.
- **PRX-SEC-003** Evidence about containment MUST identify which restrictions
  were actually enforced and which were unavailable or unknown.

## Persistent local control plane

- **PRX-UI-008** Praxis MUST be able to expose its operator state surface
  through a persistent local control-plane host without requiring a
  third-party project-management system or cloud UI.
- **PRX-UI-009** The local control plane MUST use the same durable Praxis state,
  legal transitions, receipts, provenance, and evidence as CLI and remote
  execution rather than maintaining a second workflow database.
- **PRX-UI-010** The control plane MAY remain local-only by policy and MUST make
  its listen/bind scope explicit when it exposes a network endpoint.


## Durable execution envelope

- **PRX-EXEC-030** Praxis MUST persist a durable execution envelope for every
  governed execution instead of deriving execution identity from the active
  terminal, chat session, process, or provider session.
- **PRX-EXEC-031** The execution envelope MUST record at minimum execution ID,
  work item/mission ID, actor identity, execution role, baseline revision,
  start time, effective capabilities, mutation boundary, evaluator identity
  when applicable, and workspace identity when one exists.
- **PRX-EXEC-032** Provider/model/runtime details MUST be recorded as execution
  provenance and telemetry rather than used as the semantic definition of the
  role.
- **PRX-EXEC-033** Praxis MUST allow different providers or humans to perform
  the same execution role without changing the role's semantic contract.
- **PRX-EXEC-034** Resuming the same execution MUST preserve its execution ID;
  creating an independent competing attempt MUST allocate a new execution ID.
- **PRX-EXEC-035** Praxis MUST expose the effective role/capability envelope to
  the executing host in machine-readable form before meaningful work begins.
- **PRX-EXEC-036** Praxis MUST NOT rely on prompt text alone to enforce or
  represent role authority.

## Role-specific orchestration

- **PRX-EXEC-040** Praxis MUST support repository policy that maps legal phases
  or transitions to role-specific execution launchers.
- **PRX-EXEC-041** Specification, implementation, verification, review, and
  integration executions MUST be launchable independently and MUST retain
  distinct execution identities even when performed by the same provider.
- **PRX-EXEC-042** The verification launcher MUST be capable of receiving a
  narrower capability set than the implementation launcher.
- **PRX-EXEC-043** Review MUST be representable as evidence/decision work
  without implicitly granting implementation mutation rights.
- **PRX-EXEC-044** Integration MUST be representable as a distinct execution
  whose authority is limited to declared integration work and does not inherit
  unrestricted acceptance-policy authority.
- **PRX-EXEC-045** An execution role change that materially changes authority
  MUST create a recorded transition or a new execution rather than silently
  mutating the active execution envelope.

## Workspace-per-execution model

- **PRX-EXEC-050** When Git workspaces are enabled, a mutating execution SHOULD
  receive a deterministic or uniquely identifiable branch and worktree derived
  from its execution identity and governed work item.
- **PRX-EXEC-051** Workspace metadata MUST include baseline commit, current
  candidate commit when known, branch/ref identity, and worktree location or
  remote-workspace identifier.
- **PRX-EXEC-052** Praxis MUST support multiple independent executions of the
  same work item without forcing them into the same branch or worktree.
- **PRX-EXEC-053** Commits produced by a governed execution MUST be traceable
  back to that execution without depending on conversation history.
- **PRX-EXEC-054** A commit, candidate revision, or evidence item MUST NOT be
  attributed to an execution solely because it appears in that execution's
  worktree; attribution still requires recorded execution/provenance evidence.
- **PRX-EXEC-055** Resuming a Git-backed execution MUST rebind to the intended
  candidate/workspace and detect if its baseline or branch identity has
  diverged unexpectedly.
- **PRX-EXEC-056** Workspace cleanup MUST preserve enough revision and receipt
  identity that later audit can reconstruct which candidate the execution
  produced.

## Evaluator identity and authority closure

- **PRX-VER-010** Before verification, Praxis MUST resolve and record the
  effective evaluator identity required by Ordo policy.
- **PRX-VER-011** Evaluator identity MUST be capable of representing a
  content-derived digest or immutable version over the effective evaluator
  closure, not just the root command name.
- **PRX-VER-012** Praxis MUST record enough dependency identity to detect when
  gate code, configuration, schemas, test-selection logic, fixtures, generated
  evaluator inputs, or other declared evaluator dependencies changed.
- **PRX-VER-013** A candidate verdict MUST identify the evaluator revision or
  fingerprint used to produce it.
- **PRX-VER-014** If the effective evaluator changes after the candidate's
  evaluation baseline is established, Praxis MUST represent the prior
  evaluation as stale/invalid for that context and require re-evaluation.
- **PRX-VER-015** Praxis MUST NOT downgrade an evaluator-identity mismatch into
  an ordinary test failure.

## Typed receipt ledger

- **PRX-REC-001** Praxis MUST persist expected receipts as typed step
  postconditions rather than free-form success messages.
- **PRX-REC-002** The receipt system MUST support at least artifact existence or
  identity, command/capability success, state equality, artifact-contract
  production, verification satisfaction, transition observation, and composite
  postconditions.
- **PRX-REC-003** Praxis MUST persist expected receipt, observed receipt, and
  comparison result as distinct fields or records.
- **PRX-REC-004** Receipt comparison MUST support at least `match`,
  `mismatch`, and `indeterminate`.
- **PRX-REC-005** `indeterminate` MUST preserve unknown-effect semantics and
  MUST NOT be treated as equivalent to failure or success.
- **PRX-REC-006** Composite receipts MUST expose constituent results so partial
  satisfaction is visible.
- **PRX-REC-007** Every durable receipt MUST be traceable to execution, step,
  actor/observer, relevant authority/version identities, time, and supporting
  evidence.
- **PRX-REC-008** Agent narrative MAY be retained alongside a receipt but MUST
  be labeled separately from observed evidence.
- **PRX-REC-009** Receipt history MUST be append-only or supersession-based; a
  later success MUST NOT erase an earlier mismatch or indeterminate
  observation.

## Step-level resume, reconciliation, and telemetry

- **PRX-STEP-001** Praxis MUST persist a durable ordered or dependency-aware
  step ledger for executions that declare multiple executable steps.
- **PRX-STEP-002** A step with a matching receipt MAY be skipped on resume only
  when its dependencies, evaluator/authority identity, and relevant inputs
  remain valid.
- **PRX-STEP-003** A step with an indeterminate external or repository effect
  MUST enter reconciliation before a potentially duplicating retry.
- **PRX-STEP-004** Reconciliation MUST record whether the earlier effect
  occurred, did not occur, or remains unknown.
- **PRX-STEP-005** Praxis MUST resume from durable execution/receipt state, not
  from an agent's prose summary of what happened.
- **PRX-STEP-006** Step records MUST be able to accumulate provider telemetry,
  token usage, cost, duration, retries, reconciliation attempts, receipts, and
  resulting evidence when those measurements are available.
- **PRX-STEP-007** Unavailable telemetry MUST remain unavailable/unknown rather
  than being inferred from neighboring steps or reported as zero.
- **PRX-STEP-008** Praxis MUST make it possible to analyze cost and reliability
  by execution role and step without conflating separate executions.
- **PRX-STEP-009** A resumed execution MUST preserve earlier telemetry and
  receipts rather than replacing them with resumed-session totals.

## Mutation-boundary projection and enforcement

- **PRX-BND-001** Praxis MUST accept a semantic mutation boundary from the
  governing Ordo execution contract and MAY project it into concrete repository
  paths, packages, resources, commands, or integration endpoints.
- **PRX-BND-002** The physical projection MUST be inspectable before mutation
  and traceable to the semantic authority that authorized it.
- **PRX-BND-003** Praxis MUST detect or reconcile observed repository mutations
  outside the effective boundary when the host can observe them.
- **PRX-BND-004** An out-of-bound mutation MUST create a durable scope
  violation, unresolved effect, or obligation and MUST block ordinary
  completion until reconciled.
- **PRX-BND-005** An agent explanation MUST NOT itself change the mutation
  boundary.
- **PRX-BND-006** Praxis MUST support an explicit scope-expansion request that
  is accepted only through an Ordo-legal transition/capability and is recorded
  before the expanded mutation is accepted.
- **PRX-BND-007** Evaluator/governance artifacts identified by the execution
  contract MUST remain non-writable to an implementation execution even when
  their physical paths overlap the same repository.

## Rich legal-action state surface

- **PRX-UI-020** A work-item detail view MUST be able to show current semantic
  state, active/current executions, obligations, unknowns, evidence,
  evaluator identity, receipt state, and legal next actions.
- **PRX-UI-021** The UI SHOULD explain why a transition is unavailable when the
  governing engine can provide a missing capability, evidence, obligation, or
  precondition reason.
- **PRX-UI-022** Dragging a card or selecting a visual destination MUST submit a
  transition request; it MUST NOT directly mutate a board-column field.
- **PRX-UI-023** The visual location of a work item MUST be a projection of the
  accepted semantic state after the transition succeeds.
- **PRX-UI-024** A refused transition MUST leave authoritative state unchanged
  and SHOULD display the structured refusal reason.
- **PRX-UI-025** When a transition can launch a role-specific execution, Praxis
  MUST accept the transition first and then create/launch the execution
  according to policy.
- **PRX-UI-026** Human-required transitions MUST be visually distinguishable
  from agent/automation-allowed transitions.
- **PRX-UI-027** Human authorization performed through the UI MUST create the
  same provenance and transition evidence as an equivalent CLI/API action.
- **PRX-UI-028** Board mode MUST remain optional; the canonical operator model
  is semantic state plus legal actions, not fixed Kanban columns.

## Local control-plane API and host

- **PRX-CTL-001** Praxis MUST provide a local control-plane host capable of
  exposing typed read and mutation operations over the same Praxis state used
  by CLI and remote execution.
- **PRX-CTL-002** A default local host SHOULD bind only to loopback unless
  configuration explicitly authorizes broader exposure.
- **PRX-CTL-003** The control-plane host MUST declare its effective bind/listen
  scope in status/diagnostics.
- **PRX-CTL-004** The control-plane host MUST NOT create a second canonical
  workflow database; repository/Praxis durable records remain authoritative.
- **PRX-CTL-005** The local API MUST expose structured work state, executions,
  receipts, evidence, obligations, unknowns, legal actions, and transition
  requests without requiring UI-specific scraping.
- **PRX-CTL-006** CLI, local API, remote execution, and UI MUST converge on the
  same underlying transition and validation paths rather than reimplementing
  policy independently.
- **PRX-CTL-007** A local control plane MAY aggregate multiple repositories for
  discovery and navigation, but aggregation MUST NOT silently move canonical
  repository work state into a central proprietary store.
- **PRX-CTL-008** Local-control-plane restart MUST reconstruct current state
  from durable Praxis/repository records without relying on in-memory session
  continuity.

## Forma and Limen operator UI

- **PRX-UI-030** The browser operator surface MUST use the shared Forma
  presentation system according to Praxis's shared application-foundation
  requirements.
- **PRX-UI-031** The Praxis browser application SHOULD use Limen as the F#/WASM
  browser boundary where the selected Praxis web architecture supports it,
  keeping workflow semantics in typed application/domain code rather than
  browser-specific JavaScript.
- **PRX-UI-032** Native HTML/accessibility semantics remain authoritative for
  interaction structure; Forma owns shared presentation and Praxis owns work
  meaning.
- **PRX-UI-033** The UI MUST remain usable for keyboard-only operation and MUST
  expose state without relying only on color.
- **PRX-UI-034** Client code MUST NOT independently calculate whether a
  transition is legal; it MUST render legal-action data returned by the
  authoritative Praxis/Ordo path.

## Execution containment profile

- **PRX-SEC-010** Every execution SHOULD expose an execution-containment
  profile that distinguishes semantic authority from actual host enforcement.
- **PRX-SEC-011** The containment profile MAY report filesystem, process,
  network, credential, environment, and other relevant restrictions as
  enforced, unavailable, unrestricted, or unknown.
- **PRX-SEC-012** Praxis MUST NOT infer host enforcement from a worktree,
  branch, current directory, prompt instruction, or provider permission mode.
- **PRX-SEC-013** When a host supports stronger sandboxing, Praxis SHOULD bind
  the execution's role/capability policy to the strongest practical host
  restrictions and record evidence of what was enforced.
- **PRX-SEC-014** Containment evidence SHOULD be consumable by Tutela or other
  security analysis without requiring Tutela to redefine Praxis execution
  semantics.

## Cross-system orchestration boundary

- **PRX-ARCH-001** Praxis owns execution orchestration, workspace binding,
  telemetry, receipt persistence, provenance, and host/API projection; it MUST
  consume rather than redefine Ordo's semantic state and legal-transition
  meaning.
- **PRX-ARCH-002** Conditor MAY initialize Praxis and create/activate the first
  governed mission or execution according to its declared bootstrap contract,
  but ongoing execution transitions MUST remain Praxis/Ordo governed.
- **PRX-ARCH-003** Forma/Limen UI code is a presentation/interaction host and
  MUST NOT become an alternate workflow authority.
- **PRX-ARCH-004** Agent providers are replaceable execution hosts; switching
  provider MUST NOT change the work item's governing semantic contract.
- **PRX-ARCH-005** The target end-to-end loop MUST support environment
  establishment by Conditor, semantic authorization by Ordo, execution and
  evidence by Praxis, independent verification, and legal next-action
  projection to humans or agents.


## Delivery dependency requirements

- **PRX-SEQ-001** The execution-role/capability model, typed receipt model, and
  evaluator-identity contract MUST exist as authoritative non-UI contracts
  before a Praxis operator UI may claim end-to-end enforcement of them.
- **PRX-SEQ-002** Automatic execution workspaces MUST bind to the durable
  execution envelope rather than introducing a competing execution identity.
- **PRX-SEQ-003** Mutation-boundary enforcement MUST consume the authoritative
  execution capability/boundary contract rather than hard-code UI or
  provider-specific file lists as policy.
- **PRX-SEQ-004** The legal-action API MUST exist before board drag/drop or
  other graphical transition controls are treated as authoritative user
  actions.
- **PRX-SEQ-005** The local control-plane UI MUST be implemented as a consumer
  of the legal-action/state API, not as the first or only implementation of
  workflow rules.
- **PRX-SEQ-006** Host sandbox enforcement MAY be added incrementally after
  semantic execution capabilities exist, but until then Praxis MUST report the
  weaker containment truthfully.
- **PRX-SEQ-007** A recommended implementation progression is role/envelope
  semantics, receipts, evaluator identity, workspace binding, mutation
  boundaries, legal-action API, local UI/control plane, and stronger host
  enforcement; deviations MUST preserve the dependency constraints above.


## Control-plane implementation constraints

- **PRX-CTL-009** The native Praxis control-plane host MUST be implemented in
  F# and MUST reuse the same typed Praxis domain/application contracts as the
  CLI rather than introducing a parallel JavaScript/Node workflow core.
- **PRX-CTL-010** Browser interop MAY use Limen according to the established
  Echelon boundary pattern, but JavaScript MUST remain limited to browser
  interop/event plumbing rather than owning Praxis workflow semantics.
- **PRX-CTL-011** The operator surface MUST be able to present repositories,
  work items, executions, actors/agents, receipts, evidence, telemetry/cost
  where available, unknowns, obligations, containment state, and legal
  transitions through typed control-plane data.
- **PRX-CTL-012** Provider/session availability in the control plane MUST be
  presented as execution-host information and MUST NOT make a provider the
  canonical owner of work state.


## Implementation status

Statuses: *Implemented* (behaviour exists; "Tested" when a test exercises it),
*Partial*, *Not implemented*, and *Not applicable* (the requirement constrains a
feature Praxis does not have, such as a board). Each gap names the work item
that covers it.

<!-- status:begin -->
| Requirement | Status | Evidence | Work item |
|---|---|---|---|
| PRX-EXEC-001 | Implemented | `ExecutionRole` has the five roles plus administration. Tested. | - |
| PRX-EXEC-002 | Partial | Capability sets come from a local copy of Ordo's `RoleAuthority.defaultFor`, not from the governing authority. | PRAXIS-FND-01 |
| PRX-EXEC-003 | Implemented | Role and effective capabilities are persisted in the envelope. Tested. | - |
| PRX-EXEC-004 | Implemented | Verification prohibits implementation changes. Tested. | - |
| PRX-EXEC-005 | Implemented | `execution launch` and `execution start --launch` run the repository-configured launcher for the role. Tested. | PRAXIS-EXEC-03 |
| PRX-EXEC-006 | Implemented | `--human-only TRANSITION`; legal actions refuse it to non-human actors. Tested. | - |
| PRX-EXEC-010 | Implemented | `ros.json` `execution.worktree.required` makes `execution start` create the worktree for listed roles. Tested. | PRAXIS-EXEC-03 |
| PRX-EXEC-011 | Implemented | Workspace ID, branch, path, baseline and candidate are in the envelope. Tested. | - |
| PRX-EXEC-012 | Implemented | A worktree is `semantic-only`, never a sandbox. Tested. | - |
| PRX-EXEC-013 | Implemented | `workspace.cleanup` is legal only in a terminal state. Tested. | - |
| PRX-EXEC-014 | Implemented | `work resume` and `execution resume` re-verify the bound workspace before re-entry. Tested. | PRAXIS-EXEC-01 |
| PRX-EXEC-020 | Implemented | `execution step declare --expect-*`. Tested. | - |
| PRX-EXEC-021 | Implemented | `step run` / `step observe` record the observed receipt separately. Tested. | - |
| PRX-EXEC-022 | Implemented | Receipt result `match`/`mismatch`/`indeterminate` as JSON. Tested. | - |
| PRX-EXEC-023 | Implemented | A mismatch blocks `step.start` and completion. Tested. | - |
| PRX-EXEC-024 | Implemented | Every appended ledger entry carries actor, role, workspace revision and evaluator fingerprint. Tested. | PRAXIS-EXEC-02 |
| PRX-EXEC-025 | Implemented | `events.jsonl` is append-only. Tested. | - |
| PRX-EXEC-026 | Implemented | `work complete` refuses while a bound execution has a mismatched or unknown receipt or a failed or stale verification. Tested. | PRAXIS-EXEC-01 |
| PRX-EXEC-030 | Implemented | `work begin` (also under remote execution and `plan execute-group`), `work continue` and fallback reconciliation bind a durable envelope sharing the telemetry execution ID. Tested. | PRAXIS-EXEC-01 |
| PRX-EXEC-031 | Implemented | All envelope fields are present. Tested. | - |
| PRX-EXEC-032 | Implemented | Provider, model and runtime are actor attributes, separate from the role. | - |
| PRX-EXEC-033 | Implemented | The same role yields the same capabilities for an agent and a human. Tested. | - |
| PRX-EXEC-034 | Implemented | Every start allocates a new ID. Tested. | - |
| PRX-EXEC-035 | Implemented | `execution start/show --json`; launched workers receive `PRAXIS_EXECUTION_*`. Tested. | - |
| PRX-EXEC-036 | Implemented | Authority is enforced by legal actions, not prompt text. | - |
| PRX-EXEC-040 | Implemented | `ros.json` `execution.launchers` maps roles to launchers; launching is the legal transition `execution.launch`. Tested. | PRAXIS-EXEC-03 |
| PRX-EXEC-041 | Implemented | Each role is launchable on its own execution with its own identity. Tested. | PRAXIS-EXEC-03 |
| PRX-EXEC-042 | Implemented | A verification launcher receives only the verification capability set. Tested. | PRAXIS-EXEC-03 |
| PRX-EXEC-043 | Implemented | Review records findings and may not implement. Tested. | - |
| PRX-EXEC-044 | Implemented | Integration combines candidates and may not change acceptance or specification. Tested. | - |
| PRX-EXEC-045 | Implemented | No legal action changes an envelope's role; another role is another execution. Tested. | - |
| PRX-EXEC-050 | Implemented | Branch `praxis/<work>/<execution>`. Tested. | - |
| PRX-EXEC-051 | Implemented | Workspace metadata in the envelope. Tested. | - |
| PRX-EXEC-052 | Implemented | Independent executions of one work item. Tested. | - |
| PRX-EXEC-053 | Implemented | Completion records the candidate and the baseline..candidate commits for explicit and work-bound executions. Tested. | PRAXIS-EXEC-01 |
| PRX-EXEC-054 | Implemented | Commits are recorded as candidate lineage, never as attribution; attribution stays with recorded execution and provenance evidence. Tested. | PRAXIS-EXEC-01 |
| PRX-EXEC-055 | Implemented | Branch, baseline and candidate divergence refuse resume until an explicit, recorded rebind. Tested. | PRAXIS-EXEC-01 |
| PRX-EXEC-056 | Implemented | Cleanup keeps the record and branch. Tested. | - |
| PRX-UI-001 | Implemented | The work detail page lists the item's executions; each execution page shows state, receipts, obligations, unknowns, evidence and legal actions. Tested. | PRAXIS-EXEC-06 |
| PRX-UI-002 | Implemented | Web actions come from the CLI's own allowed actions. Tested. | - |
| PRX-UI-003 | Implemented | Only allowed actions are offered. Tested. | - |
| PRX-UI-004 | Implemented | Human-required actions are marked in text and offered only through a form that records the operator as a human actor. Tested. | PRAXIS-EXEC-06 |
| PRX-UI-005 | Not applicable | There is no board. | - |
| PRX-UI-006 | Not applicable | MAY; the web UI does not launch executions. | - |
| PRX-UI-007 | Implemented | Receipt mismatch, unknown effects, unresolved scope effects, divergence and blocked state are listed separately. Tested. | PRAXIS-EXEC-06 |
| PRX-UI-008 | Implemented | `praxis web serve` serves work, backlog and execution state. Tested. | PRAXIS-EXEC-05 |
| PRX-UI-009 | Implemented | The web host runs the same CLI. Tested. | - |
| PRX-UI-010 | Implemented | Loopback by default; the bind address is printed. Tested. | - |
| PRX-UI-020 | Implemented | The execution page shows state, actor, role, evaluator, receipts, containment and legal actions. Tested. | PRAXIS-EXEC-06 |
| PRX-UI-021 | Implemented | Unavailable actions show the engine's reasons. Tested. | PRAXIS-EXEC-06 |
| PRX-UI-022 | Not applicable | There is no board or drag. | - |
| PRX-UI-023 | Not applicable | There is no board. | - |
| PRX-UI-024 | Implemented | A refused web action leaves state unchanged and shows the kernel error. Tested. | - |
| PRX-UI-025 | Implemented | `execution start --launch` accepts the start, then launches. Tested. | PRAXIS-EXEC-03 |
| PRX-UI-026 | Implemented | Human-required actions are labelled `human required`, separate from agent-allowed ones. Tested. | PRAXIS-EXEC-06 |
| PRX-UI-027 | Implemented | The web runs the CLI, so provenance is identical. Tested. | - |
| PRX-UI-028 | Not applicable | There is no board. | - |
| PRX-UI-030 | Not implemented | The web UI uses local stylesheets, not Forma. | PRAXIS-FND-05 |
| PRX-UI-031 | Not implemented | No Limen. | PRAXIS-FND-05 |
| PRX-UI-032 | Partial | Native HTML; Forma not adopted. | PRAXIS-FND-05 |
| PRX-UI-033 | Implemented | Server-rendered forms with no script; states shown as text. No dedicated accessibility test. | - |
| PRX-UI-034 | Implemented | No client script. Tested. | - |
| PRX-VER-001 | Implemented | `execution evaluate` runs only the declared `--evaluator-command`, gated by `execution.evaluate`. Tested. | PRAXIS-EXEC-02 |
| PRX-VER-002 | Implemented | The verification record holds command, candidate, evaluator fingerprint, exit code, actor and evidence. Tested. | PRAXIS-EXEC-02 |
| PRX-VER-003 | Implemented | Praxis performs no repair, by construction. | - |
| PRX-VER-004 | Implemented | `evaluator-unavailable` is neither pass nor fail. Tested. | - |
| PRX-VER-005 | Implemented | Evaluator change blocks completion until re-verified. Tested. | - |
| PRX-VER-010 | Partial | The evaluator closure comes from `--evaluator` flags, not Ordo policy. | PRAXIS-FND-02 |
| PRX-VER-011 | Implemented | sha256 closure fingerprint, byte-identical to Ordo. Tested. | - |
| PRX-VER-012 | Implemented | All closure kinds. Tested. | - |
| PRX-VER-013 | Implemented | The verdict carries the fingerprint. Tested. | - |
| PRX-VER-014 | Implemented | A stale verdict is invalidated, also for a new candidate. Tested. | - |
| PRX-VER-015 | Implemented | `evaluator-changed` is distinct from `failed`. Tested. | - |
| PRX-SEC-001 | Implemented | A host supplies `praxis.containment-evidence/1`; policy binds roles to required restrictions. Tested. | PRAXIS-EXEC-04 |
| PRX-SEC-002 | Implemented | Containment is recorded separately from capabilities. Tested. | - |
| PRX-SEC-003 | Implemented | Each restriction is enforced, unavailable, unrestricted or unknown, with mechanism and evidence. Tested. | PRAXIS-EXEC-04 |
| PRX-SEC-010 | Implemented | Every envelope carries a containment profile. Tested. | PRAXIS-EXEC-04 |
| PRX-SEC-011 | Implemented | Filesystem, process, network, credential and environment are reported per dimension. Tested. | PRAXIS-EXEC-04 |
| PRX-SEC-012 | Implemented | Host enforcement is never inferred; unreported stays unknown. Tested. | - |
| PRX-SEC-013 | Implemented | `execution.containment.<role>.require` and launcher evidence; launch is refused without the required enforcement. Tested. | PRAXIS-EXEC-04 |
| PRX-SEC-014 | Implemented | `execution containment --json` emits the stable `praxis.containment/1` profile. | PRAXIS-EXEC-04 |
| PRX-CTL-001 | Implemented | `/api/executions` read and transition routes run the CLI. Tested. | PRAXIS-EXEC-05 |
| PRX-CTL-002 | Implemented | Default host 127.0.0.1. Tested. | - |
| PRX-CTL-003 | Implemented | `GET /api/control-plane` declares host, port and loopback scope. Tested. | PRAXIS-EXEC-05 |
| PRX-CTL-004 | Implemented | No database; the host runs the CLI. | - |
| PRX-CTL-005 | Implemented | Executions, receipts, evidence, obligations, unknowns, legal actions and transition requests are typed JSON. Tested. | PRAXIS-EXEC-05 |
| PRX-CTL-006 | Implemented | The web host runs the same `praxis execution` legality path. Tested. | PRAXIS-EXEC-05 |
| PRX-CTL-007 | Implemented | The hub delegates to each repository's CLI. Tested. | - |
| PRX-CTL-008 | Implemented | The host is stateless; every request re-reads durable state. | - |
| PRX-CTL-009 | Implemented | F# host using the same CLI. | - |
| PRX-CTL-010 | Implemented | No JavaScript. Tested. | - |
| PRX-CTL-011 | Implemented | Executions, actors, receipts, evidence, unknowns, obligations, containment and legal transitions are presented; cost stays in `praxis telemetry`. Tested. | PRAXIS-EXEC-05 |
| PRX-CTL-012 | Implemented | Provider, model and runtime appear only as execution-host attributes of the actor. Tested. | PRAXIS-EXEC-05 |
| PRX-REC-001 | Implemented | Typed expected receipts. Tested. | - |
| PRX-REC-002 | Implemented | All receipt kinds plus composites. Tested. | - |
| PRX-REC-003 | Implemented | Expected, observed and result are separate. Tested. | - |
| PRX-REC-004 | Implemented | match, mismatch, indeterminate. Tested. | - |
| PRX-REC-005 | Implemented | Indeterminate keeps unknown-effect semantics. Tested. | - |
| PRX-REC-006 | Implemented | Composite constituents are exposed. Tested. | - |
| PRX-REC-007 | Implemented | Entries carry actor/observer, role, revision and evaluator identity. Tested. | PRAXIS-EXEC-02 |
| PRX-REC-008 | Implemented | Narrative is separate and never compared. Tested. | - |
| PRX-REC-009 | Implemented | Append-only ledger. Tested. | - |
| PRX-STEP-001 | Implemented | Ordered, dependency-aware step ledger. Tested. | - |
| PRX-STEP-002 | Implemented | A matched step is reused. Tested. | - |
| PRX-STEP-003 | Implemented | Unknown effects require reconciliation before retry. Tested. | - |
| PRX-STEP-004 | Implemented | Occurred / did not occur / still unknown. Tested. | - |
| PRX-STEP-005 | Implemented | Resume reconstructs from entries only. Tested. | - |
| PRX-STEP-006 | Partial | Execution steps and telemetry steps are separate models. | PRAXIS-EXEC-09 |
| PRX-STEP-007 | Implemented | Unavailable telemetry is never zero. Tested. | - |
| PRX-STEP-008 | Partial | No cost or reliability analysis by role. | PRAXIS-EXEC-09 |
| PRX-STEP-009 | Implemented | Resumed telemetry is preserved. Tested. | - |
| PRX-BND-001 | Partial | The boundary comes from CLI `--scope/--allow`, not an Ordo contract. | PRAXIS-FND-02 |
| PRX-BND-002 | Implemented | `execution boundary` shows the projection. Tested. | - |
| PRX-BND-003 | Implemented | Scope effects come from the Git diff. Tested. | - |
| PRX-BND-004 | Implemented | Effects block completion. Tested. | - |
| PRX-BND-005 | Implemented | Explanations never widen scope. Tested. | - |
| PRX-BND-006 | Implemented | `expand-scope` is a legal transition. Tested. | - |
| PRX-BND-007 | Implemented | Evaluator authority is never writable. Tested. | - |
| PRX-ARCH-001 | Partial | Praxis re-implements Ordo semantics locally. | PRAXIS-FND-01 |
| PRX-ARCH-002 | Implemented | Conditor may bootstrap; not exercised by tests. | - |
| PRX-ARCH-003 | Implemented | The UI holds no authority. Tested. | - |
| PRX-ARCH-004 | Implemented | Provider is an attribute. Tested. | - |
| PRX-ARCH-005 | Partial | No Conditor or Ordo integration in the loop. | PRAXIS-FND-01 |
| PRX-SEQ-001 | Implemented | Non-UI contracts exist before any UI claim. | - |
| PRX-SEQ-002 | Implemented | The worktree derives from the execution ID. Tested. | - |
| PRX-SEQ-003 | Partial | Same as BND-001. | PRAXIS-FND-02 |
| PRX-SEQ-004 | Not applicable | No graphical controls. | - |
| PRX-SEQ-005 | Implemented | The web consumes the CLI. Tested. | - |
| PRX-SEQ-006 | Implemented | Weaker containment is reported truthfully. Tested. | - |
| PRX-SEQ-007 | Implemented | Followed up to host enforcement evidence. | - |
<!-- status:end -->
