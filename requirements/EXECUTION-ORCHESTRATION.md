# Praxis execution orchestration requirements

Status: **Proposed**

Tracked by GitHub issues #94 and #96.

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
