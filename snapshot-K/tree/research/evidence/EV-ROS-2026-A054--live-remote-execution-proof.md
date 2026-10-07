---
id: EV-ROS-2026-A054
title: "Live end-to-end proof: cloud agents with only GitHub access governed through remote Praxis 3.5.0"
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-29
updated: 2026-09-29
research_area: repository-operating-system
evidence_type: primary
supports: [RQ-ROS-2026-A021]
related_documents:
  - https://github.com/kemiller2002/praxis/issues/90
  - RQ-ROS-2026-A021
  - EV-ROS-2026-A053
  - DF-ROS-2026-A041
  - docs/remote-agent-contract.md
  - docs/remote-protocol.md
  - docs/remote-execution-operations.md
tags: [remote-execution, e2e, provenance, telemetry, idempotency, gh-90]
confidence: high
provenance:
  contributions:
    EXE-20260929T054321231Z-0f9f1e9a:
      operations: [created]
      at: 2026-09-29T05:44:04.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Live end-to-end proof of remote Praxis execution (PRAXIS-REMOTE-11, PRX-REMOTE-042), verified against main and the GitHub Actions API"
      evidence: [https://github.com/kemiller2002/praxis/issues/90]
derived_from: [RQ-ROS-2026-A021]
---

# Live end-to-end proof of remote Praxis execution (PRAXIS-REMOTE-11, PRX-REMOTE-042)

## What was run

On 2026-09-29, work item `PRAXIS-REMOTE-11-PROOF` was started, continued and
completed entirely through `.github/workflows/praxis-remote.yml` on `main`
of `kemiller2002/praxis`. It was driven by cloud-agent sessions that had
only GitHub access and were instructed not to install .NET, F# or Praxis.
Every transition was executed by GitHub-hosted runners that installed the
pinned Praxis release.

Everything below was checked against the repository (`main` at `7047364`)
and the GitHub Actions API. It was not taken from the agents' own reports.

## Release and bootstrap (PRX-REMOTE-012, 024)

- `v3.5.0` was published by `native-release.yml` from `882ab57` (PR #99).
- `praxis-linux-x64.tar.gz` matches `native-checksums.txt`, with SHA-256
  `6d74f5c49a5e81709c3f681be7824ba4681a9041e25fb62bdd983c6bfee4c2bb`.
- GitHub holds one SLSA v1 provenance attestation for that digest. It was
  issued by `.github/workflows/native-release.yml` at `refs/heads/main`.
- `.echelon/toolchain.json` pins `praxis` 3.5.0, and `ros.json` opts in to
  `read`, `mutate` and `complete` (PR #100, `48e26cf`).
- Every run's `praxis-setup` step installed that digest with attestation
  `required`. Every response and journal entry records `praxisVersion`
  3.5.0.

## Sequence

| Request ID | Run | Result | Commit on `main` |
|---|---|---|---|
| `req-describe-20260929T0445Z-orchestrator` | 36522631443 | succeeded (read) | none |
| `req-proof-a-describe-1` | 36522785343 | succeeded (read) | none |
| `req-proof-a-start-1` | 36522823233 | succeeded | `69f1b6c` |
| `req-proof-a-start-1`, resent identically | 36522906661 | succeeded as a replay | none |
| `req-proof-a-batch-1` | 36523001929 | `validation-failed`; stopped at `req-proof-a-telemetry-1`; the successful `step.start` was kept, the rest undone | `ea50711` |
| `req-proof-a-batch-2` | 36523119339 | succeeded; `step.start` replayed | `6bce406` |
| `req-proof-a-validate-1` | 36523194912 | succeeded (read) | none |
| `req-proof-b-resume-1` | 36524551943 | `domain-rejected`: resume is legal only from `blocked` | none |
| `req-proof-b-resume-2` | 36525979339 | failed; not persisted | none |
| `req-proof-b-impersonate-1` | 36526140530 | `domain-rejected` | none |
| `req-proof-b-validate-1` | 36526144742 | succeeded (read) | none |
| `req-proof-b2-block-1` | 36526011882 | succeeded | `0b39564` |
| `req-proof-b2-resume-1` | 36526078418 | succeeded | `d0fe70f` |
| `req-proof-b2-batch-1` | 36526163691 | succeeded | `56cf34c` |
| `req-proof-b2-complete-1` | 36526247007 | succeeded; item `complete` | `7047364` |
| `req-proof-b2-validate-1` | 36526329794 | succeeded (read) | none |

For every mutation, the journal entry `.ros/remote/requests/<requestId>.json`
on `main` holds the full response. In each journalled response,
`repository.expectedSha` equals `observedSha`.

## Identity and provenance (PRX-REMOTE-004, 005, 006, 007, 034)

- **Requester and executor are separate.** Every journal entry records the
  requester as `asserted-by-request`: agent `anthropic/claude-code`, with
  model `unknown` and the caller's own `sessionId`. It records the executor
  as `observed-by-executor`: `github-actions` with its run ID, workflow ref,
  principal and Praxis version. The runner is never recorded as the
  requester.
- **Executions:**
  - `EXE-20260929T044302145Z-078a696b` was created for session
    `<session redacted>`.
  - `EXE-20260929T052538586Z-95bd6089` was created for session
    `<session redacted>`. Its `identity.parentExecutionId` is
    the first execution.
  - Both executions are finalized.
- **[record].** The successor blocked the item with the reason "predecessor
  execution EXE-20260929T044302145Z-078a696b (session
  <session redacted>) ended without a [record]; taking over as
  the successor", and then resumed it.
- **Impersonation was refused.** The request `req-proof-b-impersonate-1`
  named the first execution in `execution.id`. It came from a third session,
  `<session redacted>`, whose origin is not established here.
  Praxis refused it as `domain-rejected` and persisted nothing.

## Telemetry and evidence quality (PRX-REMOTE-008, 009, 010)

- Each execution carries one step-scoped measurement:
  - `tool.calls` = 17, for step `remote-proof-a`;
  - `tool.calls` = 19, for step `remote-proof-b2`.
- Both measurements have `source.type` `agent-report`. They are
  self-counted and not independently observed.
- No token metrics were recorded, because the agents cannot observe them.
  Both executions show `tokens.input` as `supported-unavailable`, not zero.
- The `unknown source type 'agent'` refusal in `req-proof-a-batch-1` shows
  that an invalid evidence label is rejected. It is not stored as a guess.

## Gaps the proof found

1. **[record] contract.** The agent contract told a successor to "resume"
   an item that its predecessor had left active. Resume is legal only from
   `blocked`. This is fixed by PRAXIS-REMOTE-15.
2. **Conclusions are dropped.** `work.complete`'s `conclusion` is persisted
   only for research-type items. The successor's conclusion was accepted
   and silently discarded, both locally and remotely. This is recorded as
   PRAXIS-REMOTE-16.
3. **Artifacts cannot be downloaded from the proving environment.** Its
   network policy denies `productionresultssa10.blob.core.windows.net`.
   Results were read from the repository journal, which is the contract's
   durable path.

## Limits of this evidence

- **The no-.NET claim is not in the repository.** The callers' lack of a
  local runtime rests on their session instructions and their own reports.
  The repository only shows that every transition ran on a GitHub-hosted
  runner. Because of gap 2, the successor's recorded environment check was
  not persisted.
- **One question is unresolved.** The first agent session ended with an
  unresolved question to its operator. Its transcript is not part of this
  record.
- **The agents were not independent products.** Both proof agents were the
  same agent product (`anthropic/claude-code`) in different sessions. They
  are distinguished by session, not by provider.
