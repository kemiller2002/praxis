# EX-ROS-2026-A021-R2 — control-arm run log

Orchestrator session: `session_01HySizJjio1tGqfvPAXp3WV` (model `claude-opus-5-5`, permission mode `auto`).
Branch: `experiment/a021-r2-control`. Baseline: `8b4ffa392e93b19bf39f6672a608954c934cb815`.

Each item was executed by a fresh Claude Code Remote session created with `create_session`
(`source_url` https://github.com/kemiller2002/praxis, `source_revision` and `outcome_branch`
`experiment/a021-r2-control`, model `claude-opus-5-5`, permission mode `auto`), using the
prescribed worker prompt verbatim with only `<ID>`/`<NN>` substituted. Workers ran serially; each
was started only after the previous session was idle and its commits were visible on
`origin/experiment/a021-r2-control`. The orchestrator did not read implementation code, did not
add hints, and did not modify any worker's code or metrics file.

## Workers

Times are UTC on 2026-09-30. "Created" is `get_session.created_at`; "Last update" is
`get_session.updated_at` observed after the session went idle (end of its final turn).
Usage is `get_session.external_metadata.usage` as observed by the orchestrator after the worker
went idle (whole session, including the steps after the worker captured its own metrics).

| Worker | Item | Session ID | Created | Last update | Head SHA after worker | configured / session_context / last_served model |
|---|---|---|---|---|---|---|
| C1 | PRAXIS-GROUP-01 | `session_01TBSowZ5EdWBAfJky3MEpP2` | 15:23:28 | 15:41:26 | `4f122334b082f774ac0526b28b04cc6d67c25ac7` | claude-opus-5-5 / claude-opus-5-5 / claude-opus-5-5 |
| C2 | PRAXIS-GROUP-02 | `session_01T6C9t1bNaqfAXEckAxu7Zz` | 15:54:08 | 16:19:17 | `7b55af43179bd72d30cc6ab449d0c0b163482d96` | claude-opus-5-5 / claude-opus-5-5 / claude-opus-5-5 |
| C3 | PRAXIS-GROUP-03 | `session_019PbeHJSP9f4A8L9QXKyQRX` | 16:29:07 | 16:50:37 | `98b621b2a092879b0b5203c37af3733933c1adac` | claude-opus-5-5 / claude-opus-5-5 / claude-opus-5-5 |
| C4 | PRAXIS-GROUP-04 | `session_01KfJswoaJGxqcviM6kiTr2i` | 16:52:52 | 17:08:23 | `d728cd6496c147cddfba484bd650ce57e52a2849` | claude-opus-5-5 / claude-opus-5-5 / claude-opus-5-5 |
| C5 | PRAXIS-GROUP-05 | `session_01CMeWM1tj4PBwoRA9tJYiSc` | 17:16:19 | 17:36:44 | `0b9e81f139789bae5f5921594b3599d49570731a` | claude-opus-5-5 / claude-opus-5-5 / claude-opus-5-5 |

| Worker | input tokens | output tokens | cache-read tokens | cache-write tokens | cost (USD) |
|---|---|---|---|---|---|
| C1 | 152 | 48,711 | 10,005,927 | 166,742 | 4.3099494 |
| C2 | 206 | 44,024 | 16,107,298 | 190,816 | 5.6292916 |
| C3 | 174 | 53,228 | 8,415,319 | 258,780 | 4.4210508 |
| C4 | 124 | 34,504 | 7,455,994 | 125,433 | 3.1852388 |
| C5 | 162 | 56,187 | 12,503,731 | 173,211 | 5.0108222 |

### Commits per worker (`sha parents | subject`)

- C1: `7e01fb9 8b4ffa3` work group create · `d2a0d50` metrics · `4f12233` checkpoint and completion
- C2: `962b1f4 8b4ffa3` work group show · `fa821cc 962b1f4 4f12233` merge PRAXIS-GROUP-01 · `33ee8d0` metrics · `7b55af4` checkpoint and completion
- C3: `b05fd8b 8b4ffa3` start work item · `5a03452 b05fd8b 7b55af4` merge PRAXIS-GROUP-01/02 · `dae9788` work group add · `65c7a85` metrics · `98b621b` checkpoint and completion
- C4: `4e0b70f 98b621b` start work item · `a3566ad` work group remove · `342d66e` metrics · `d728cd6` checkpoint and completion
- C5: `c28009f d728cd6` start work item · `2d55157` work group checkpoint · `6f824ae` metrics · `0b9e81f` checkpoint and completion

All worker commits carry their item ID as prefix. After each worker the orchestrator fetched the
branch, built it, and confirmed `./ros work show <ID>` reports `"status": "complete"`.
No worker opened a pull request (GitHub PR list for head `experiment/a021-r2-control`: empty); nothing was merged.

## Metrics

- `metrics/control-01.json` … `metrics/control-05.json` — written, committed and pushed by each
  worker itself. Each contains `sessionId` and model fields. The orchestrator did not edit them.
  Worker metrics were captured by the worker before its final completion/commit steps, so they
  may undercount relative to the whole-session `get_session` usage above.

## Final mechanical pass (orchestrator, head `0b9e81f`)

- Build: `dotnet build Ros.slnx -c Release … -p:FSharpCoreImplicitPackageVersion=10.1.400` — 0 warnings, 0 errors.
- F# unit tests (`tests/Ros.Tests`): 839 passed, 0 failed.
- Node tests from `npm test`: 104 passed, 0 failed.
- Python `unittest discover -s tests`: 7 passed.
- Node differential tests from `npm run test:fsharp`: 257 passed, 0 failed.
- `./ros validate`: validation passed.
- `./ros work show PRAXIS-GROUP-01..05`: all `complete`.

## Protocol deviations

1. **Toolchain / infrastructure (all workers and orchestrator).** `scripts/install-toolchain.sh`
   fails in this environment (Ordo release lookup returns HTTP 403); `dotnet-install.sh` is also
   blocked (403, per C1's metrics). .NET SDK 10.0.112 was installed from Ubuntu apt. That SDK's
   bundled FSharp.Core (10.0.112) is older than `EchelonFoundry.Aegis.Core` requires (>= 10.1.400),
   so `npm run build:fsharp` fails with NU1605. Builds were run as the equivalent `dotnet build`
   with `-p:FSharpCoreImplicitPackageVersion=10.1.400` (reported by C1 in `control-01.json`; used by
   the orchestrator for every verification and the final pass). `npm test` / `npm run test:fsharp`
   were therefore run as their constituent steps rather than via the npm scripts. No repository
   file was changed for this.
2. **Stale worker checkout (C2, C3).** Although each worker session was created after the previous
   worker's commits were on `origin/experiment/a021-r2-control`, C2's and C3's first commits are
   parented on the baseline `8b4ffa3` rather than on the then-current head. Each worker then merged
   the remote branch itself (C2: `fa821cc`, reporting merge-conflict resolution; C3: `5a03452`)
   before pushing. The branch history therefore contains two merge commits. C4 and C5 started from
   the current head (linear history). The orchestrator did not alter launch parameters mid-arm, did
   not rewrite history, and did not repair this.
3. **Worker metrics timing.** Worker-authored metrics were captured before each worker's final
   completion/commit steps (stated explicitly in `control-01.json` `endedAtNote`); whole-session
   platform usage is recorded above instead of overwriting worker files.
4. No replacement workers were needed; every worker completed its item on the first session.
