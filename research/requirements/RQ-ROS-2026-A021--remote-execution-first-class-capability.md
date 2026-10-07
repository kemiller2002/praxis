---
id: RQ-ROS-2026-A021
title: Praxis governance is independent of the execution environment; remote/cloud-agent execution is a first-class capability
status: accepted
version: 1.4.0
owners:
  - repository-governance
created: 2026-09-28
updated: 2026-10-07
research_area: repository-operating-system
priority: high
depends_on: [RQ-ROS-2026-A020]
evidence_ids: [EV-ROS-2026-A053]
related_documents:
  - DF-ROS-2026-A041
  - EV-ROS-2026-A053
  - RQ-ROS-2026-A020
  - DF-ROS-2026-A007
  - DF-ROS-2026-A036
  - DF-ROS-2026-A040
  - https://github.com/kemiller2002/praxis/issues/90
  - https://github.com/kemiller2002/praxis/issues/80
tags: [remote-execution, protocol, provenance, idempotency, security, gh-90]
provenance:
  contributions:
    EXE-20260928T073932249Z-d48161b9:
      operations: [created, modified]
      at: 2026-09-28T07:44:28.080Z
      last: 2026-09-28T09:40:46.882Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from GitHub issue #90 (work item GH-90): remote/cloud-agent execution as a first-class capability"
      evidence: [https://github.com/kemiller2002/praxis/issues/90, EV-ROS-2026-A053]
    EXE-20260928T200030786Z-eca55c92:
      operations: [modified]
      at: 2026-09-28T20:02:59.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRX-REMOTE-038 delivered by PRAXIS-REMOTE-13: adapter classifies GitHub rate limiting as rate-limited"
      evidence: [scripts/praxis-remote-persist.sh, tests/praxis-remote-adapter.test.mjs]
    EXE-20260929T054321231Z-0f9f1e9a:
      operations: [modified]
      at: 2026-09-29T05:44:18.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRX-REMOTE-001/011/029/042 demonstrated live (PRAXIS-REMOTE-11); gaps recorded as PRAXIS-REMOTE-15 and PRAXIS-REMOTE-16"
      evidence: [EV-ROS-2026-A054]
    EXE-20261006T203816433Z-d8de3cb4:
      operations: [modified]
      at: 2026-10-06T20:40:43.202Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-MISC-09: replace citations of deleted Node tests with the F# tests that cover the behaviour; keep the Node result as history"
    EXE-20261007T053405002Z-37591719:
      operations: [modified]
      at: 2026-10-07T05:34:26.023Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Adopt PRX-REMOTE-045..050 and the order amendment; record their implementation (GROUP-PRAXIS-IDENTITY-001); correct the deleted Node test citations and the abandoned PRAXIS-NPM-BIN deferral"
      evidence: [docs/identity.md]
derived_from: [RQ-ROS-2026-A020]
---

# Requirement

Praxis MUST govern an agent that has GitHub repository/API access but no
.NET, F#, or local Praxis runtime as rigorously as an agent running Praxis
locally. Remote execution MUST reach the **same** Praxis domain rules,
legal transitions, validation, provenance, attribution, and telemetry
semantics as the local CLI through a typed, versioned, transport-independent
request/response protocol; there MUST NOT be a second governance
implementation. GitHub Actions is the first execution adapter and MUST stay
a thin host: it bootstraps a pinned, verified Praxis release, forwards the
request, persists only Praxis-owned state, and returns the structured
result. Praxis remains the authority.

The detailed, normative requirement list is issue #90, `PRX-REMOTE-001`
through `PRX-REMOTE-050` (045-050, global work-item identity, and the
implementation-order amendment were added to the issue after this record
was first written), which this record adopts by reference rather than
copying. Their reconciliation against the implementation, with the gap for
each, is `EV-ROS-2026-A053`; the design and decomposition is
`DF-ROS-2026-A041`. (Note 2026-10-06: issue #90 has since been amended with
`PRX-REMOTE-045` through `PRX-REMOTE-050`, global work-item and repository
identity, and an implementation-order amendment. This record's adoption
scope and status are unchanged by this note; those IDs are tracked by the
repository-identity work, not by the increments listed below.)

## Invariants every implementation increment preserves

1. **One authority.** Local and remote requests execute the same command
   implementation; remote validation can never be weaker than local.
2. **Explicit operations, never a shell.** The remote surface is an
   allow-listed, typed operation catalog. Request values are untrusted data
   and never become shell text.
3. **Identity is not inferred from the host.** The requesting actor is
   *asserted* by the request, the runner/executor is *observed* by the
   adapter, and the authenticated transport principal is a third fact.
   None is collapsed into another; unknown stays `unknown`; a runner never
   becomes the author of an agent's work.
4. **Idempotent mutation.** Every mutating request carries a stable request
   ID; a replay with the same semantic payload recovers the recorded
   outcome; the same ID with a different payload fails closed.
5. **Repository-state binding.** A mutating request names the ref and the
   commit SHA it was formed against; Praxis refuses to apply it to any other
   commit.
6. **Honest outcomes.** Praxis-decided failures (invalid, unsupported,
   unauthorized, stale, conflicting, domain-rejected, validation-failed) are
   distinct from transport/executor failures (bootstrap, write, network,
   rate limit, timeout, cancellation), and an unconfirmed effect is
   `unknown`, never success or failure.
7. **Durable record.** Every accepted mutating request leaves a
   repository-persisted journal entry linking request -> execution ->
   events -> resulting state; logs and workflow artifacts are supporting
   evidence only.
8. **Evidence quality survives.** Telemetry keeps its quality/source and
   missing values stay unavailable, never zero.
9. **Compatibility with #80.** Post-hoc reconciliation remains the path for
   already-committed work, preserving the Git author/committer and
   recording the reconciliation actor separately; nothing is touched or
   recommitted to manufacture attribution.

## Acceptance criteria

The capability is complete only when the `PRX-REMOTE-042` end-to-end proof
passes: a caller with no local .NET/Praxis runtime, using GitHub access
only, discovers the remote capability, starts or resumes governed work
under its own execution identity, records step evidence and telemetry,
has a pinned and verified Praxis release validate and persist the state,
retrieves the structured result, and a later agent can inspect and
continue without impersonating the first.

## Verification

Tracked per increment by the `PRAXIS-REMOTE-NN` work items under `GH-90`
(see `DF-ROS-2026-A041`, "Decomposition"). Each increment names its own
tests.

## Implementation status (2026-09-28, branch `claude/remote-execution-capability-2i5s9u`, PR #91)

This section is a status snapshot. `EV-ROS-2026-A053` remains the baseline
as of `59b4e03`. Each entry below is backed by the named work item's
completion evidence and tests.

**Local verification.** Everything below ran locally:

- `npm run test:all`: node 104 passed, python 7 passed, F# 579 passed, and
  the F#/Node differential and remote suites 237 passed. (Historical: the
  Node suites, including `tests/praxis-remote-adapter.test.mjs` (historical),
  were removed when the repository became F#/.NET only, `RQ-ROS-2026-A024`.
  The remote adapter is now covered by the F# `RemotePersistScriptTests`
  ("praxis remote adapter: ..."), `RemoteExecuteCliTests` and
  `RemoteProtocolTests`, run by
  `dotnet tests/Praxis.Tests/bin/Release/net10.0/Praxis.Tests.dll`.)
- The site suite: 99 passed.
- `praxis validate`: passed.

**Live run.** The first live GitHub dispatches ran on 2026-09-29, for the
end-to-end proof PRAXIS-REMOTE-11 (`EV-ROS-2026-A054`). See "Remaining
work" below.

**Implemented and tested:**

| PRX-REMOTE | Delivered by | Notes |
|---|---|---|
| 002, 003, 025, 031, 032, 040 | PRAXIS-REMOTE-01, 04 and 08 | Typed, versioned protocol 1.2. |
| 005, 006, 007 | PRAXIS-REMOTE-02 | The requester is recorded as asserted. The executor and principal are recorded as observed. |
| 004, 034 | PRAXIS-REMOTE-02 and 09 | Includes the core continuation fix (DF-ROS-2026-A041 1.2.0). |
| 008, 009, 010 | PRAXIS-REMOTE-04 | Steps, step-scoped usage, evidence-quality projection, usage by dimension. |
| 013, 014, 015, 016, 017, 018, 026, 027, 028 | PRAXIS-REMOTE-03 | `praxis remote execute`, the request journal and the SHA binding. Local/remote parity tests. |
| 019, 035 | PRAXIS-REMOTE-09 | Remote #80 reconciliation and the fallback path. |
| 020, 021, 022, 023 | PRAXIS-REMOTE-01, 02, 03 and 06 | Tested statically and against real Git remotes. |
| 012, 024, 036 (caching) | PRAXIS-REMOTE-05 | The release-side changes take effect at the next release. |
| 036, 037 | PRAXIS-REMOTE-08 | Batches. |
| 033, 044 | PRAXIS-REMOTE-07 | `praxis.describe`, the agent contract, and the AGENTS.md pointer. |
| 043 | PRAXIS-REMOTE-10 | Operator documentation. |

**Remaining work:**

- **001, 011, 029, 042: demonstrated live on 2026-09-29.** See
  `EV-ROS-2026-A054`. The attested `v3.5.0` release was published, pinned
  and opted in (PRs #99 and #100). Two cloud-agent sessions with only GitHub
  access started, continued and completed `PRAXIS-REMOTE-11-PROOF` through
  `praxis-remote.yml`. The run showed:
  - replay of an identical request;
  - an atomic, partially undone batch;
  - SHA binding on every mutation;
  - step-scoped `agent-report` telemetry, with tokens left unavailable;
  - a successor execution linked by `parentExecutionId`;
  - a refused impersonation attempt;
  - remote validation.

  The proof also found two gaps:
  - the handoff contract, fixed by PRAXIS-REMOTE-15;
  - `work.complete` silently dropping a non-research conclusion, tracked
    by PRAXIS-REMOTE-16.
- **041 (live parts).** The live run covered these parts of 041:
  - the no-runtime caller;
  - actor and executor separation;
  - duplicate replay;
  - stale-SHA binding;
  - the takeover refusal and the impersonation refusal;
  - missing telemetry;
  - validation parity.

  Rate limiting, timeout-after-commit, cancellation and concurrent requests
  remain covered by tests only.
- **030.** Conditor support, PRAXIS-REMOTE-12. It depends on PRAXIS-REMOTE-11.
- **038.** Delivered by PRAXIS-REMOTE-13. The GitHub adapter reports a push
  or pull-request creation that GitHub throttled (HTTP 429, or a primary or
  secondary API rate limit) as `rate-limited`, with `same-request` retry. It
  reports any other refused push or pull request as
  `repository-write-failed`, and a lost race as `concurrency-conflict`.
- **039.** Remote execution is opt-in, and existing state stays readable.
  The npm `praxis` bin is deferred to PRAXIS-NPM-BIN, because the public
  site's audited copy must change with it. (Update 2026-10-06: npm is no
  longer a distribution channel, `DF-ROS-2026-A044`, and PRAXIS-NPM-BIN was
  abandoned, so this clause is moot.)
- **045-050 and the implementation-order amendment (global work-item
  identity).** Implemented by `GROUP-PRAXIS-IDENTITY-001`
  ([`docs/identity.md`](../../docs/identity.md)):
  - 045, 046, 048, 049, 050: typed repository and work-item identity,
    `ros.json` `repository.identity`, the legacy migration rule and the
    PRX-REMOTE-050 test matrix (PRAXIS-ID-01, PR #185).
  - 047 and the amendment: protocol 1.4 carries structured
    `{repositoryId, repository, localId}` references and refuses any other
    repository; 1.0-1.3 requests keep their meaning and fingerprints
    (PRAXIS-ID-02). Execution telemetry and planner documents carry the
    canonical identity structurally (PRAXIS-ID-03).
  - The protocol shipped (1.0-1.3) before the identity model existed, so the
    amendment is met by making 1.4 consume the canonical model rather than by
    re-freezing 1.0.
