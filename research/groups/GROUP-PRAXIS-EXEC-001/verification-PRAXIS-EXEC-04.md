# Verification pass: PRAXIS-EXEC-04

Group `GROUP-PRAXIS-EXEC-001`, section 5. Criteria from `./praxis work show PRAXIS-EXEC-04`.
Tests: `tests/Praxis.Tests/ExecutionRuntimeTests.fs` (`ERT`).

| # | Criterion | Status | Evidence |
|---|---|---|---|
| 1 | Every envelope has a five-dimension profile (enforced/unavailable/unrestricted/unknown, mechanism, evidence); unreported stays unknown; nothing inferred | met | `ContainmentProfile` in `src/Praxis.Domain/Execution/Governance.fs`; `ExecutionEnvelope.create` sets `ContainmentProfile.unknown`; ERT "containment: unreported dimensions stay unknown; enforcement needs evidence"; ERT containment CLI test: `credential` = `unknown` when unreported, a plain worktree start stays `semantic-only` |
| 2 | Evidence as `praxis.containment-evidence/1` via `--containment-evidence` or `PRAXIS_CONTAINMENT_EVIDENCE`; recorded as host-reported | met | `ExecutionJson.readContainmentEvidence`, `FileExecutionPorts.readContainmentEvidence` (explicit file, else the variable); profile `source` = host; ERT containment CLI test uses `--containment-evidence`; ERT "containment: a launcher's host evidence and PRAXIS_CONTAINMENT_EVIDENCE are recorded…" covers the variable and a launcher's `containmentEvidence` |
| 3 | Host-enforced only with an enforced restriction and evidence | met | `ContainmentProfile.containment`; ERT "containment: host-enforced only with enforcement evidence…"; ERT CLI: enforced filesystem -> `containment` = `host-enforced`; enforced without evidence -> exit 2 "no evidence" |
| 4 | `execution.containment.<role>.require`; `execution launch` refused when the evidence does not show them (criterion amended 2026-10-06: a start runs no process, so it does not refuse) | met | ERT CLI: verification requires `network`, evidence shows it `unavailable`, `launch` -> 3 "'network' restriction"; ERT "legal actions: launch needs a configured launcher and the required host restrictions"; documented in docs/execution-runtime.md |
| 5 | `execution containment --json` emits a stable documented schema | met | ERT CLI asserts `schema` = `praxis.containment/1` and per-dimension status; documented in docs/execution-runtime.md "Containment profile and host evidence" |
| 6 | Tests | met | the five ERT containment tests above |
