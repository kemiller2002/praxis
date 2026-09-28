# Praxis execution orchestration requirements

Status: **Proposed**

Tracked by GitHub issue #94.

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
