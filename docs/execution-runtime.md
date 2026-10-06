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
host-enforced containment needs enforcement evidence Praxis does not yet
collect. `execution cleanup` removes the worktree only after a terminal state
and keeps the execution record and branch.

Completion records the workspace HEAD as the candidate revision and is
refused while the workspace holds uncommitted changes, so every candidate
revision traces back to its execution (commit messages may add a
`Praxis-Execution: <id>` trailer).

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

## Evaluation

`praxis execution evaluate EXE-… --command "sh tests/gate.sh"` re-digests the
evaluator closure in the workspace. If it changed since the baseline the
result is `evaluator-changed` — not pass, not fail — and completion is
blocked until a new verification under an identified evaluator.

## Legal actions

`praxis execution actions EXE-… --json` is the single legal-action
computation (transition, target, availability, reasons, actor requirement).
Every `praxis execution` mutation checks it first; a local API, remote
execution or a Forma/Limen UI must consume the same list and never compute
legality itself. `--human-only execution.complete` makes a transition
human-required: an agent cannot satisfy it by reporting approval.

## Ordo.Core

Praxis consumes Ordo's execution semantics from the released Ordo.Core
package rather than a copy (ORDO-CORE-PACKAGE, PRAXIS-FND-01; PRX-ARCH-001,
PRX-EXEC-002). `EchelonFoundry.Ordo.Core` is the `ordo-core.nupkg` asset of
the Ordo release named in `vendor/nuget/ordo-core.lock`, vendored unmodified
with its sha256 and restored only from that folder (`NuGet.config` package
source mapping). Role capability sets (`RoleAuthority.defaultFor`), the
evaluator fingerprint, glob matching, mutation-boundary classification and
scope expansion are Ordo.Core's; `Praxis.Domain.Execution` keeps Praxis's
persisted wire shapes and translates by wire name. Tests fail when the
vocabularies diverge, when the vendored package's digest differs from the
lock, or when Governance.fs re-implements an Ordo rule. To move to a new Ordo
release, replace the package, the lock and the `PackageReference` version
together.

## Not yet implemented

The local control-plane API server (CTL-001..008), the operator UI, host
enforcement evidence (SEC-*) and remote-execution binding of envelopes. See
work item EXEC-INSTALL-109.

## Attribution of execution state

`.ros/executions/**` is Praxis bookkeeping, like `.ros/telemetry/**`. This
repository lists it in `ros.json` `workProtocol.ignoredPaths`. A project that
adopts `praxis execution` should add it too. The shipped defaults are left
unchanged so existing installations do not migrate.
