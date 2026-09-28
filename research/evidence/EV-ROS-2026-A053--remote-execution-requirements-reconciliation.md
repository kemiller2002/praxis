---
id: EV-ROS-2026-A053
title: "Issue #90 (PRX-REMOTE-001..044) reconciled against the current Praxis implementation"
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-28
updated: 2026-09-28
research_area: repository-operating-system
evidence_type: primary
supports: [RQ-ROS-2026-A021]
related_documents:
  - https://github.com/kemiller2002/praxis/issues/90
  - https://github.com/kemiller2002/praxis/issues/80
  - RQ-ROS-2026-A020
  - DF-ROS-2026-A007
  - DF-ROS-2026-A036
  - DF-ROS-2026-A040
  - docs/work-adapter-contract.md
  - docs/agent-provenance.md
  - docs/development-telemetry.md
  - docs/native-installation.md
tags: [remote-execution, reconciliation, requirements, gh-90, security, provenance, telemetry]
confidence: high
provenance:
  contributions:
    EXE-20260928T073932249Z-d48161b9:
      operations: [created]
      at: 2026-09-28T07:44:27.575Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Reconciliation of issue #90 PRX-REMOTE-001..044 against the implementation (work item GH-90)"
      evidence: [https://github.com/kemiller2002/praxis/issues/90]
---

# Evidence summary

Every requirement in issue #90 was checked against the code, tests,
workflows and release mechanism of this checkout at `59b4e03` (branch
`claude/remote-execution-capability-2i5s9u`). Nothing was classified as
satisfied on documentation alone.

| Status | Count | Requirements |
|---|---|---|
| Satisfied | 0 | none |
| Partially satisfied | 22 | 001, 004, 006, 007, 008, 009, 010, 016, 017, 019, 022, 025, 026, 027, 028, 029, 031, 032, 034, 035, 039, 040 |
| Missing | 18 | 002, 003, 011, 013, 014, 015, 018, 020, 021, 023, 033, 036, 037, 038, 041, 042, 043, 044 |
| Conflicting | 3 | 005, 012, 024 |
| Deferred by dependency | 1 | 030 |

No requirement is satisfied because no inbound remote path exists at all.
A lot of what #90 needs already exists locally, though, and the remote
design must reuse it rather than rebuild it.

## How the evidence was gathered

- The F# CLI was built from this checkout with .NET SDK 10.0.112 (Ubuntu
  package). That SDK bundles an older FSharp.Core than CI, so the local build
  passed `-p:FSharpCoreImplicitPackageVersion=10.1.400`. The repository was
  not changed for this.
- The full F# suite ran before any change: `515 test(s); 515 passed;
  0 failed`.
- The published native release `v3.4.0` (`praxis-linux-x64.tar.gz`) was
  downloaded. Its SHA-256 `ff52078714875e4cc921a017da00e5a0204e79edec9d4c335d59e0be2a1a88fc`
  matched `native-checksums.txt`. It reported `ros-fs 3.4.0` and returned
  `validation passed` against this repository without a .NET runtime.
- `echelon.sh`'s pinned-install pipeline was run with the nonexistent
  version 99.99.99. `curl -fsSL .../v99.99.99/scripts/install-native.sh | sh -s -- --version 99.99.99`
  exited `0`. See PRX-REMOTE-012.
- Source was read directly. `file:line` references are approximate to
  within a few lines.

## Existing capabilities the remote design must reuse

1. **Outbound adapter contract (`DF-ROS-2026-A007`).** Sources:
   `docs/work-adapter-contract.md`, `schemas/work-adapter-request.schema.json`,
   `src/Ros.Domain/Work/AdapterContract.fs`.
   - It already has a const `protocolVersion`, a durable `requestId`, and
     separate `principal` (authenticated caller) and `actor` (who did the
     work).
   - It has `success | failure | unknown` outcomes, where `unknown` is never
     promoted, and an `expectedState` precondition.
   - It is outbound (Praxis to a work system) and is a conformance double.
   - Its replay is keyed on `requestId` alone, with no payload fingerprint.
2. **Execution identity.**
   - `EXE-<timestamp>-<hex>` IDs are created under the
     `telemetry-execution-index` lock, and duplicate IDs are refused
     (`FileTelemetryExecutionRepository.fs:34-37, ~508`).
   - `parentExecutionId` links continuation on resume.
   - Work items and events carry `telemetryExecutions`.
3. **Actor model (`DF-ROS-2026-A036`).**
   - `Actor.fs` keeps kind, id, provider, model and runtime separate.
     `unknown` is explicit, and a human's `None` is never confused with
     `Some "unknown"`.
   - `ProvenancePolicy.crossCheck` (`ProvenancePolicy.fs:95-117`) refuses a
     contribution whose actor contradicts its execution.
   - `ProvenanceEffectTests` prove that an agent cannot record into another
     agent's execution.
4. **Post-hoc reconciliation (#80, `RQ-ROS-2026-A020`, `DF-ROS-2026-A040`).**
   - The event is append-only and Git-evidenced, and keeps three identities:
     the Git author/committer, the work item, and the reconciliation actor.
   - It is idempotent per (commit, path), and a conflicting work item fails
     closed.
   - Validation accepts reconciled paths only while their content matches the
     recorded evidence.
   - Tests: `WorkReconciliationTests.fs`, `WorkReconciliationEffectTests.fs`.
5. **Telemetry evidence model.**
   - Token metrics (`tokens.input/.output/.total/...`) and cost metrics
     (`cost.*`) with currency. Quality is `observed | derived | estimated`,
     and estimated values must carry a confidence.
   - Source types include `runtime-api`, `runtime-output`, `agent-report`,
     `human-report` and `calculated`. A calculated cost requires
     `pricing.source` and `pricing.version`.
   - Unavailable is a capability status and is never a synthetic zero
     (`TelemetryValidation.fs:73-80, 346-356`, `Capability.fs:107-124`).
6. **Local serialization and crash recovery.**
   - `RegistryLock` (`.ros/locks/`) and `WorkStateTransaction` /
     `BacklogStateTransaction` write-ahead journals with before/after
     SHA-256 guards.
   - Event IDs are content-addressed, which gives append idempotency.
7. **Handoff.** `ordo handoff` records facts, assumptions, unknowns,
   obligations and legal next actions, plus `producedBy`, which names an
   execution only when it is unambiguous.
8. **Native release.**
   - The release ships self-contained binaries for six RIDs plus the package
     payload, a `native-checksums.txt` of SHA-256 sums, and
     `.echelon/toolchain.json` pins.
   - The installer verifies the checksum and fails on a missing asset.

## Architectural findings

- **A1: Transition orchestration lives in the CLI tier.**
  - `runWorkStart`, `runWorkResume`, `runWorkBlock` and `runWorkComplete` in
    `src/Ros.Cli/Program.fs` (1213-1790) do the lock, recovery, backlog guard,
    planning, telemetry resolution and apply steps inline, and print to
    stdout/stderr.
  - There is no typed repository-command union. Flags are parsed ad hoc and
    unknown flags are ignored.
  - So a second entry point cannot call a single Application function today.
    To keep one governance implementation, the remote boundary must either
    route typed requests into the *same* command implementations in-process,
    or first lift that orchestration into `Ros.Application`. This is the main
    structural constraint on the design (`DF-ROS-2026-A041`).
- **A2: The runner becomes the actor.**
  - In GitHub Actions with no agent session variables,
    `Identity.discover` resolves `github/github-actions` and
    `ActorKind.fromDiscoveryMechanism` gives `automation`.
  - Three historical execution records show exactly that (WI-0061/62/63).
  - This is correct for CI automation acting on its own behalf. It would be
    wrong for a runner executing an agent's request, which violates
    PRX-REMOTE-005 and 006.
- **A3: There is no step concept.** Metric `scope` has
  `operation | turn | tool | execution | session | work-item | repository`,
  but an execution has no steps that carry their own identity or outcome.
- **A4: No expected-SHA or caller precondition exists** on any work
  transition. Locks serialize processes on one machine only.
- **A5: The Praxis version is not recorded per execution.**
  `provenance.collectorVersion` is the schema-level `1.0.0`, not the binary
  version.
- **A6: Naming migration is incomplete.**
  - The native bundle and installer expose `praxis`, but the npm package has
    no `praxis` bin, the binary prints `ros-fs X`, and there is no rename
    decision record.
  - `lib/ros-fs-launcher.mjs:44` still downloads from the old
    `repository-operating-system` repository URL. This is recorded as a
    finding only.

## Security findings

- **S1: Silent success on a missing pinned version.**
  - `bin/echelon.sh install_praxis` pipes `curl -fsSL` into `sh` under
    `set -eu` without `pipefail`. A nonexistent pinned tag exits `0`
    (reproduced above).
  - A key missing from `.echelon/toolchain.json` falls forward to latest with
    no warning.
- **S2: Mutable release assets.**
  - `native-release.yml` runs on pushes to main that touch
    `scripts/**`, `bin/*` or the workflow, and uploads with `--clobber` into
    the existing `v${VERSION}` release. So the same version's binaries and
    checksums can change after publication.
  - The checksum file comes from the same release as the asset, so it guards
    integrity in transit but not authenticity.
  - There is no signature or build attestation.
- **S3: Actions pinned to tags.** Every action is pinned to a mutable major
  tag, with no SHA pins. `ros-validation.yml` has no `permissions` block.
  `native-release.yml` grants `contents: write` workflow-wide, including to
  PR runs, although it skips the release step on PRs.
- **S4: Key-based redaction only.** Telemetry redaction matches key names
  only. A token value under a benign key survives, and bare `token` and
  `github_token` keys match neither pattern.
- **S5: Local servers have no authentication.** The HTTP servers
  (`ros_server.mjs`, `ros_hub_server.mjs`) are localhost-only and
  unauthenticated, and they call the legacy Node kernel. They are **not** a
  basis for remote execution and must not be exposed.
- **S6: Adapter replay ignores the payload.** The adapter replay cache
  returns the cached result for the same `requestId` with a different
  payload. Remote idempotency must not copy that behaviour.

## Reconciliation matrix

Legend: **S** Satisfied, **P** Partially satisfied, **M** Missing,
**C** Conflicting, **D** Deferred by dependency. The "Action" column names
the work item in `PRAXIS-REMOTE-NN` (children of `GH-90`).

| ID | Status | Existing records | Implementation / tests (evidence) | Remaining gap | Action |
|---|---|---|---|---|---|
| 001 Execution independence | P | DF-A030, DF-A032; native release | The local F# CLI is supported. The native self-contained release runs with no .NET (verified here with `v3.4.0 validate`). | No remote path. The same rules can be guaranteed only through one boundary (A1). | 03 |
| 002 Transport-independent protocol | M | DF-A007 (outbound pattern) | `AdapterContract.fs` gives the precedent for version, requestId and outcomes. | No inbound versioned request/response protocol exists. | 01 |
| 003 Remote operation surface | M | none | Each operation exists locally as a CLI command (`work context/start/resume/block/complete/reconcile`, `telemetry record`, `provenance identity`, `validate`, `status`). | No typed remote representation. Step operations do not exist (A3). | 01, 03, 04 |
| 004 Execution/session identity | P | DF-A036 | `EXE-` IDs with duplicate refusal, `parentExecutionId`, cross-check (`ProvenanceEffectTests`). | No request/operation ID. Steps have no identity. Retries of `telemetry start` create new executions. | 01, 03, 04 |
| 005 No impersonation | C | DF-A036, RQ-A006 | Unknown is preserved (`Actor.unknown`). Impersonation is refused (`ProvenancePolicy.crossCheck`, tests `ProvenanceTests.fs:489`). | A runner with no declaration becomes the actor (A2), which contradicts "runner MUST NOT be mistaken for the agent". | 02 |
| 006 Provenance roles | P | RQ-A020, DF-A040, DF-A036 | Change author (Git), work-item attribution, reconciliation actor and execution actor are all distinct (`Reconciliation.fs`). | There is no request actor and no executor/runner role. | 02 |
| 007 Observed vs asserted | P | DF-A010, DF-A036 | Telemetry `source.type` (`agent-report` vs `runtime-*`, `ros-git`). `provenance identity` states "self-reported". The execution records the Git branch/commit at start. | No request ID, expected or actual SHA, workflow run identity as executor, or Praxis version (A5). Requester-supplied values are not labelled as asserted. | 02, 03 |
| 008 Usage telemetry | P | DF-A010 | Token and cost metrics with currency. Provider and model come from the execution identity. Missing values are never zero (`OrdoObservationTests.fs:86`, telemetry validation). | Nothing below execution level (A3). | 04 |
| 009 Evidence quality | P | DF-A010 | `quality` plus `source.type` plus capability status can express measured, provider-reported, agent-reported, calculated, estimated and unavailable. `pricing.source/version` is required for calculated cost. | That mapping is implicit, undocumented and untested as one projection. No step level. | 04 |
| 010 Aggregation | P | DF-A010 | `TelemetrySummary.summarize` aggregates overall or per work item (`TelemetrySummaryTests.fs`). | No per-execution, per-step, per-provider, per-model or per-time-period breakdown. | 04 |
| 011 Reusable workflow | M | none | `foundations-verify.yml` is a `workflow_call` precedent. | No remote workflow. | 06 |
| 012 Bootstrap/versioning | C | DF-A003, DF-A034 | `.echelon/toolchain.json` pin, checksum-verified native install. | S1 (silent success on a missing pin; fall-forward on a missing key). S2 (mutable version assets). No bootstrap for an Actions runner. | 05 |
| 013 Minimal invocation | M | none | none | No invocation contract. | 03, 06, 07 |
| 014 Result retrieval | M | none | none | No durable result record independent of logs. | 03, 06 |
| 015 Commit/ref binding | M | none | `WorkStateTransaction` hash guards cover crash recovery only (A4). | No expected-SHA precondition. | 01, 03 |
| 016 Concurrency | P | none | `RegistryLock` serializes processes on one host. Content-addressed event dedupe. | No cross-runner semantic protection. Delayed or out-of-order requests are undetected. | 03, 06 |
| 017 Idempotency | P | DF-A007 | Adapter `requestId` replay (outbound). Event-ID dedupe. Reconciliation is idempotent per (commit, path). | No inbound request journal. Same ID with a different payload is not detected (S6). Timeout-after-commit recovery is missing. | 01, 03 |
| 018 Safe persistence | M | DF-A040 | `meaningfulPaths`/`ignoredPaths` and `.echelon/ros.json` bookkeeping classification exist. | Nothing defines which remote operations may mutate, who commits, or push vs PR. | 03, 06 |
| 019 Post-commit attribution | P | RQ-A020, DF-A040, GH-80 | Fully implemented locally, with tests (see capability 4). | Not reachable remotely. Captured #80 follow-ups `ATTR-COMPLETE-BASE-REF-SWEEP` and `ATTR-RECONCILE-SYMLINK-SUBMODULE` remain open. | 09 |
| 020 Least privilege | M | none | Current workflows are not least-privilege (S3). | No remote workflow permission model. | 06 |
| 021 Untrusted input | M | none | Reconciliation refuses option-like refs (`Reconciliation.fs:37-61`), a precedent. | No request validation. Flags are parsed ad hoc and unknown flags are ignored (A1). | 01, 06 |
| 022 Secrets | P | DF-A010 | Key-based telemetry redaction. The identity environment allowlist never reads tokens. | Value-level secret detection (S4). Remote logs and artifacts are not covered. | 01, 06 |
| 023 Authorization/capabilities | M | DF-A007 (scopes) | Adapter scopes are caller-asserted. | No capability classes. There is no trusted principal to authorize against. | 01, 03, 06 |
| 024 Supply chain | C | none | SHA-256 checksum from the same origin. | S2, S3, A5 (version not recorded per execution). | 05, 02 |
| 025 Failure semantics | P | DF-A007 | Adapter `success/failure/unknown`. CLI exit-code contract (`docs/cli.md`). | No taxonomy separating Praxis failures from transport failures. | 01 |
| 026 Cancellation/timeouts | P | none | Write-ahead journals prevent partial local state. A transition is never falsely complete (`WorkStateTransaction.recover`). | Outcomes are not distinguished. No way to ask "was it written?" | 01, 03 |
| 027 Auditability | P | none | `events.jsonl` is an append-only audit of transitions. | No link from request to execution to step. Rejections leave no audit. | 03 |
| 028 Validation equivalence | P | DF-A030 | One `validate` implementation with a machine-readable `--json`. | Parity holds only if the remote path runs the same binary and command (A1). | 03, 11 |
| 029 Independent installation | P | DF-A003 | The native smoke test runs `praxis init/verify` in an empty project with no optional systems (`native-release.yml`). | Remote path not yet built. | 06, 11 |
| 030 Conditor | D | none | none | Conditor work must follow a stable Praxis contract (issue sequence step 10). | 12 |
| 031 Provider neutrality | P | DF-A036 | The actor vocabulary has `x-` extensions. Identity detection covers four providers. | Protocol not yet defined. | 01 |
| 032 GitHub is first adapter | P | none | The Domain has only the mechanism string `whitelisted-github-actions-environment`. Detection lives in Infrastructure. | Protocol, executor and host context are not yet behind an adapter boundary. | 01, 02, 06 |
| 033 Discovery | M | none | `status`/`work context` are local only. | No discovery document. | 07 |
| 034 Handoff/continuation | P | DF-A036 | `ordo handoff`, `producedBy`, `parentExecutionId`, cross-check. | No remote access. No explicit successor-to-predecessor relationship for a different agent. | 09 |
| 035 Offline/fallback reconciliation | P | RQ-A020 | Post-hoc reconciliation is labelled `attribution: post-hoc`. | No conforming fallback execution record. No idempotent reconciliation of asserted telemetry. | 09 |
| 036 Efficient invocation | M | none | The npm launcher caches by version. | No remote caching or batching. | 05, 08 |
| 037 Session/batch | M | none | none | none implemented | 08 |
| 038 Rate limits/transient failures | M | none | none | Retryability classification missing. | 01, 06 |
| 039 Existing repositories | P | DF-A030 | Existing state stays readable. The native `praxis` alias exists. | Opt-in model not yet defined. npm has no `praxis` bin (A6). | 07 |
| 040 Schema evolution | P | docs/cli.md, docs/work-protocol.md | Rules are documented for CLI JSON and the work protocol. | Four `$id` conventions. Strict and tolerant readers are inconsistent. No remote schema rules. | 01 |
| 041 Core tests | M | none | none | All remote invariants remain to be tested. | 01-11 |
| 042 E2E proof | M | none | none | Needs the adapter on the default branch and a live runner. | 11 |
| 043 Operator docs | M | none | none | none | 10 |
| 044 Agent contract | M | none | none | none | 07 |

## Negative findings (searched, not found)

- **Remote-execution terms.** "remote execution", "PRX-REMOTE", issue #90,
  "cloud agent" as an executor, `repository_dispatch`, "expected SHA" /
  "expectedHead", "idempotency key", "operationId": none of these appear in
  `research/`, `requirements/`, `docs/`, `src/`, `tests/`, `.github/`,
  `.ros/` or `missions/`.
- **Step concept.** No step start/complete/fail inside an execution. Checked
  `src/`, `tools/ros_telemetry.mjs`, `schemas/execution-telemetry.schema.json`
  and `telemetry/metrics.json`.
- **Aggregation breakdowns.** No per-model or per-time-bucket telemetry
  aggregation in `Summary.fs`.
- **Execution/work continuation.** No effective-current or continuation
  concept for executions or work items beyond `parentExecutionId`. The Ordo
  effective-current covers resolutions only.
- **Rename decision.** No decision record for the ROS to Praxis rename in
  `research/decisions/`.

"Not found" means these specific searches found nothing. It does not prove
that no equivalent concept exists under a name that was not searched.

## Assumptions and unknowns

- **Assumption: persistence model.** GitHub's `workflow_dispatch` can carry a
  JSON request as an input, and a dispatch needs write access to the
  repository. Authorization therefore starts from GitHub's own check.
  Praxis-side capability policy narrows it further.
- **Unknown: run correlation.** Whether the GitHub API this repository will
  use returns the run ID for a dispatch. The design must not depend on it:
  correlation uses the request ID (in `run-name`) and the durable journal
  record.
- **Unknown: branch protection.** How branch protection on `main` would treat
  a Praxis state commit from `GITHUB_TOKEN`. The adapter must support a
  PR-based persistence mode.
- **Hypothesis, not established behaviour.** Routing typed requests into the
  existing command implementations in-process preserves validation parity by
  construction. `PRAXIS-REMOTE-03` must prove it with differential tests.
