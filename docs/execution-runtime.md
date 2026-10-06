# Execution runtime (`praxis execution`)

Praxis is the runtime of Ordo's execution-governance contract
(`ordo.execution/1`; Ordo `method/EXECUTION-GOVERNANCE-REQUIREMENTS.md`).
Ordo owns what execution states, capabilities, receipts and transitions
*mean*; Praxis creates, persists and orchestrates executions and never
invents conflicting semantics. Requirements: `requirements/EXECUTION-ORCHESTRATION.md`.

## Execution envelope

```
praxis execution start --work-item WI-7 --role implementation \
  --scope feature:installation --allow 'feature:installation=src/Installation/**' \
  --evaluator gate-code=tests/gate.sh --evaluator configuration=ros.json \
  --worktree
```

```
work item:   kemiller2002/example:WI-7
execution:   EXE-20260929T121805470Z-e5ce5808
baseline:    3f23b85…
branch:      praxis/WI-7/EXE-20260929T121805470Z-e5ce5808
workspace:   worktree:EXE-20260929T121805470Z-e5ce5808 (../example.worktrees/EXE-…)
actor:       anthropic/claude-code (agent)
role:        implementation
containment: semantic-only (not a security sandbox)
evaluator:   sha256:…
```

The envelope is durable at `.ros/executions/<execution-id>/envelope.json`
and carries execution ID, repository-qualified work item, actor (kind,
provider, model, runtime), role, effective capabilities and prohibitions,
baseline and candidate revisions, workspace identity, mutation boundary,
evaluator identity, human-only transitions, parent execution, start time and
state. Provider identity and role are separate: a human, Claude, Codex,
Gemini or CI performs the same role with the same capabilities.

- **Roles**: `specification`, `implementation`, `verification`, `review`,
  `integration`, `administration`, with Ordo's default capability matrix. No
  role holds evaluator or acceptance authority; verification cannot modify
  the candidate; review cannot implement; integration cannot change acceptance.
- **Evaluator identity** is the sha256 fingerprint of the declared closure
  (gate code, configuration, test selection, schemas, fixtures, generated
  inputs, policies, dependencies) — byte-for-byte Ordo's algorithm. The
  closure is excluded from the writable boundary; an envelope whose boundary
  admits it is refused.
- **Work identity** is repository-qualified (`owner/repo:WORK-ID`) from the
  Git remote, so it is globally unambiguous.

## Workspaces

`--worktree` creates branch `praxis/<work-item>/<execution-id>` in a new
worktree (default `../<repo>.worktrees/<execution-id>`; the envelope stores
only the relative path). Two executions of the same work item get two
identities, branches and worktrees. A worktree is an isolation *mechanism*,
recorded as `containment: semantic-only`, never as a security sandbox;
host-enforced containment needs the host evidence described below. `execution cleanup` removes the worktree only after a terminal state
and keeps the execution record and branch.

Completion records the workspace HEAD as the candidate revision and is
refused while the workspace holds uncommitted changes, so every candidate
revision traces back to its execution (commit messages may add a
`Praxis-Execution: <id>` trailer).

## Work-bound executions

Every governed work execution has an envelope (PRX-EXEC-030), not only those
started with `praxis execution start`:

- **`work begin`/`start`** binds an envelope for each begun item. When
  telemetry is enabled it shares the telemetry execution's ID, so one
  identity names the execution in telemetry, provenance and governance. It
  records the actor, the role (`--role ROLE`, default `implementation`), the
  current HEAD as baseline, the current checkout and branch as the workspace
  (`working-directory`, never a new branch or worktree, so
  `plan execute-group`, which begins members through the same transition,
  binds them too) and the host's containment profile. Its origin is
  `work-transition`, or `remote` when `praxis remote execute` runs the
  transition. A work-bound envelope declares no mutation boundary, so no
  scope effect is computed for it.
- **`work block`, `resume`, `abandon`, `complete`** mirror the outcome onto
  the item's open work-bound envelopes with an append-only transition record.
  Completion records the candidate (HEAD) and the commits
  `baseline..candidate` as candidate lineage (PRX-EXEC-053); lineage is
  never attribution, which still needs recorded execution and provenance
  evidence (PRX-EXEC-054).
- **`work resume`** first re-verifies each bound workspace (PRX-EXEC-014/055):
  it must exist, be on the recorded branch, and contain the baseline and any
  recorded candidate in its history. A divergence refuses the resume (exit 3,
  nothing changes) and names it; `--rebind-reason TEXT` rebinds explicitly and
  records a `workspace-rebound` entry. `praxis execution rebind EXE-…
  --reason TEXT` does the same for an explicit execution, whose `resume` and
  `complete` are blocked while it has diverged.
- **`work complete`** first consults recorded receipts (PRX-EXEC-026): it is
  refused (exit 3, nothing changes) while any open execution of the item has
  a mismatched or unknown step receipt, an unresolved scope effect, a failed
  or stale verification, or a human-only completion and a non-human actor. A
  mismatch is resolved by governed rework, for example abandoning that
  execution and starting another; the ledger keeps it.
- **`work continue`** marks the predecessor's envelope `interrupted` and binds
  the successor's with `parentExecution` set to it.
- **Runtime-free fallback** (`praxis reconcile --envelope FILE`) binds the
  envelope's execution with origin `fallback`, its recorded base commit and
  branch, and its actor, then applies its block/resume/complete requests in
  order. A replay binds the same execution again, never a second one.

An invalid host containment report (`PRAXIS_CONTAINMENT_EVIDENCE`) or
execution policy refuses `work begin` before anything changes. Any other
failure to bind is reported as a `WARN` and never changes the transition's
exit code.

## Step ledger and receipts

```
praxis execution step declare EXE-… --step build --expect-command "dotnet build"
praxis execution step run     EXE-… --step build        # host-observed receipt
praxis execution step observe EXE-… --step publish --observed-json receipt.json
praxis execution step reconcile EXE-… --step publish --finding did-not-occur --detail "version absent from registry"
```

`.ros/executions/<id>/events.jsonl` is append-only: declarations, attempts,
observed receipts with their comparison (`match`, `mismatch`,
`indeterminate`; composites keep every constituent), reconciliations,
transitions, scope expansions and resolutions. A resumed execution is
reconstructed from this file alone. A matched step is reused; a step whose
effect is unknown must be reconciled (occurred / did not occur / still
unknown) before it may be retried, unless it declared `--retry-safe BASIS`
from the external contract. Self-reported observations never produce a
match; narrative is recorded and never compared.

## Mutation boundary

`praxis execution boundary EXE-…` compares the workspace with the baseline
and reports every mutation outside the declared semantic boundary (or inside
evaluator authority) as a scope effect. Effects block completion until
resolved: reverted (and observed reverted), admitted by a legal
`execution expand-scope` (justification + authorizing actor; never reaching
evaluator references; prohibited for verification/review/integration roles),
or transferred to another execution. An explanation never widens scope.

## Evaluation (runner mode)

```
praxis execution start --work-item WI-7 --role verification \
  --evaluator gate-code=tests/gate.sh --evaluator-command "sh tests/gate.sh"
praxis execution evaluate EXE-… [--evidence out/report.json]*
```

An execution may run exactly one verification command, the one declared at
start with `--evaluator-command` (PRX-VER-001). `execution evaluate` is the
legal transition `execution.evaluate`: it is unavailable when the execution
is not active, declares no evaluator or no evaluator command, or its role
lacks `evaluator.invoke` (specification, review, administration). Passing a
different `--command` is refused and nothing runs.

Each evaluation re-digests the evaluator closure in the workspace. If it
changed since the baseline the result is `evaluator-changed`, not pass and
not fail, and completion is blocked until a new verification under an
identified evaluator. The `verification` ledger record holds the outcome, the
command, the **candidate commit** it judged, the evaluator fingerprint, the
**exit code** (absent when the outcome is unknown), the **actor** and any
produced **evidence** references (PRX-VER-002). A verification of a candidate
other than the workspace HEAD is stale: `execution.complete` asks for a new
evaluation.

Every ledger entry Praxis appends (declare, start, observe, reconcile)
carries an `attribution`: actor id and kind, role, workspace revision and
evaluator fingerprint (PRX-EXEC-024, PRX-REC-007). `execution show --json`
lists them per step.

## Execution policy and launchers

`ros.json` may carry an `execution` section:

```json
"execution": {
  "launchers": {
    "verification": { "id": "ci-verifier", "command": "./scripts/run-verifier.sh",
                      "containmentEvidence": ".praxis/host/verifier-containment.json" }
  },
  "worktree": { "required": ["implementation"] },
  "containment": { "verification": { "require": ["network", "credential"] } }
}
```

- **Launchers** (PRX-EXEC-005/040/041/042) map a role to the command the
  repository configured. Praxis never chooses a provider or model.
  `praxis execution launch EXE-… [--dry-run]` is the legal transition
  `execution.launch`; `execution start … --launch` accepts the start first and
  then launches (PRX-UI-025). The launcher runs in the execution's workspace
  with `PRAXIS_EXECUTION_ID`, `PRAXIS_EXECUTION_ROLE`,
  `PRAXIS_EXECUTION_ENVELOPE` (the envelope path), `PRAXIS_WORK_ITEM`,
  `PRAXIS_EXECUTION_CAPABILITIES` and `PRAXIS_EXECUTION_PROHIBITIONS`, so a
  verification worker receives only the verification capability set. The
  ledger records `launch-started` (launcher, command, actor) and
  `launch-finished` (exit code, or why the outcome is unknown).
- **`worktree.required`** (PRX-EXEC-010) makes `execution start` create the
  per-execution branch and worktree for the listed roles without `--worktree`.
- **`containment.<role>.require`** (PRX-SEC-013) lists restrictions that must
  be host-enforced before that role's launcher may run.

## Containment profile and host evidence

Every envelope carries a `containmentProfile` (`praxis.containment/1`,
PRX-SEC-010/011): one entry each for `filesystem`, `process`, `network`,
`credential` and `environment`, with status `enforced`, `unavailable`,
`unrestricted` or `unknown`, plus mechanism and evidence. Praxis never infers
enforcement from a worktree, branch, directory, prompt or provider permission
mode (PRX-SEC-012), so without a host report every entry is `unknown`.

A host, sandbox wrapper or launcher reports what it actually enforced
(PRX-SEC-001/003) in a `praxis.containment-evidence/1` file, passed with
`execution start --containment-evidence FILE`, through the
`PRAXIS_CONTAINMENT_EVIDENCE` environment variable, or as a launcher's
`containmentEvidence`:

```json
{ "schema": "praxis.containment-evidence/1", "host": "bubblewrap",
  "restrictions": [
    { "dimension": "network", "status": "enforced", "mechanism": "bwrap --unshare-net",
      "evidence": "launcher argv recorded in .praxis/host/verifier.log" },
    { "dimension": "credential", "status": "unavailable" } ] }
```

An `enforced` entry without evidence is refused. The report is recorded as
host-reported (its `source` names the host); Praxis does not verify it.
`containment` becomes `host-enforced` only when at least one restriction is
enforced with evidence; otherwise it stays `semantic-only`.
`praxis execution containment EXE-… --json` emits the profile in this stable
shape for Tutela or other security analysis (PRX-SEC-014).

## Legal actions

`praxis execution actions EXE-… --json` is the single legal-action
computation (transition, target, availability, reasons, actor requirement).
Every mutating `praxis execution` command is gated by it: `step declare|
start|run|observe|reconcile`, `evaluate` (`execution.evaluate`), `launch`
(`execution.launch`), `expand-scope`, `resolve-effect`, `transition`,
`rebind` and `cleanup`. A local API, remote execution or UI must consume the same list and never
compute legality itself. `--human-only execution.complete` makes a
transition human-required: an agent cannot satisfy it by reporting approval.
The rules live in `Praxis.Application.Execution.ExecutionService`; the CLI
only parses and renders.

## Control plane and operator UI

`praxis web serve` exposes executions through the same CLI path: `GET
/api/executions[?workItem=ID]`, `GET /api/executions/:id` (exactly `execution
show --json`), `POST /api/executions/:id/transitions`, and `GET
/api/control-plane` (the declared listen scope). Its pages list a work item's
executions and show each execution's receipts, obligations, unknown effects,
containment and legal actions with reasons and human-required markers. See
[`web-interface.md`](web-interface.md).

## Not yet implemented

The per-requirement status, with the work item covering each gap, is the
[Implementation status](../requirements/EXECUTION-ORCHESTRATION.md#implementation-status)
table. Open work: consuming Ordo.Core and the Ordo execution contract instead
of a local copy of roles, boundaries and evaluator closure (`PRAXIS-FND-01`,
`PRAXIS-FND-02`); linking execution steps to telemetry and analysing cost by
role (`PRAXIS-EXEC-09`); Forma and Limen in the browser UI (`PRAXIS-FND-05`).

## Attribution of execution state

`.ros/executions/**` is Praxis bookkeeping, like `.ros/telemetry/**`. It is
always ignored for work attribution, whatever a repository's
`workProtocol.ignoredPaths` says (like `.echelon/**`), because every
`work begin` now writes an envelope; no installation needs to migrate.
