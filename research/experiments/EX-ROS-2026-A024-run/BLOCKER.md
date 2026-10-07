# EX-ROS-2026-A024 blocker and resumption package

Status: **blocked (owner decision required)**. Arm execution is stopped. No
part of the frozen protocol (treatments, thresholds, recovery or correctness
margins, blinding, stop conditions, retry rule) was changed.

## Exact condition

1. Before any arm ran, the orchestrator froze and verified the protocol
   artifacts: manifest `91745d9` (harness hashes), sanitized start `ddda837`
   (parent `8b4ffa3`, reproducible), and identical start trees on
   `experiment/a024-arm-{1,2,3}` (`phase0/verify-start.txt`).
2. Arm A (`arm-1`) completed all five items in one session
   (`experiment/a024-arm-1` at `9a6c64f`). Telemetry is verified, the executor
   binding holds (claude-opus-5-5, effort medium, Claude Code 2.1.292 on all
   94 requests) and the isolation audit is clean.
3. Arms B and C completed item 01 (`arm-2-01` -> `3e535f3`,
   `arm-3-01` -> `c85eb3e`, with a schema-valid handoff). Both are clean on
   the same checks.
4. Every item-02 session was started by the platform from the branch's
   *first-seen* head `ddda837`, not the recorded head (deviations D1-D3).
   The cause is reproduced: `create_session` with `outcome_branch` pins that
   branch to the first head the platform saw. The frozen start check stopped
   each of these sessions before it changed anything.
5. The preregistered retry rule allows one retry of an orchestration-only
   failure. For the **arm-C item-02 slot** that retry is spent (`arm-3-02`,
   then `arm-3-02r`). `arm-3-02r` correctly declined an orchestrator resync
   sent through the cross-session channel. Launching a third arm-C item-02
   session would go beyond the frozen protocol. Arm C cannot reach a valid
   terminal state under the frozen rules, so neither can A024.
6. The arm-B item-02 retry (`arm-2-02r`) accepted the same resync note and was
   left to finish (see `sessions.json` for its terminal state).

This matches the owner's instruction: when a validity condition cannot be met,
stop rather than weaken the protocol.

## Decision the owner must make

Choose one and record it as a dated protocol amendment in
`EX-ROS-2026-A024` (below the preregistered text, never editing it):

- **Option 1 (recommended): amend the retry accounting for pre-start platform
  failures.** A session the platform starts on a commit other than the
  recorded head, which the frozen start check stops before any repository
  change, is a launcher defect, not an attempt at the task. It does not
  consume the slot's retry. This changes no treatment, threshold, blinding or
  stop rule, and it cannot select outcomes, because those sessions produced
  no implementation. Then resume as below.
- **Option 2: restart B and C from item 01 with the corrected launcher**
  under a new run (A024-R2). Arm A keeps its result or is re-run for
  concurrency symmetry. This is cleaner and costs about 2x.
- **Option 3: close A024 as not executed** (inconclusive for HY-ROS-2026-A030).
  Keep the run bundle as feasibility evidence.

## Corrected launcher (validated by probe P3)

For every B/C session after item 01, create the session **without
`outcome_branch`**:

```
create_session(model="claude-opus-5-5",
               source_url="https://github.com/kemiller2002/praxis",
               source_revision="<arm branch>",            # no outcome_branch
               prompt=<frozen prompt with START_SHA = recorded branch head>)
```

The checkout is then the recorded head (detached HEAD). Probe P3 showed that
`git checkout -B <branch> && git push origin <branch>` works from such a
session. The frozen prompts say the branch "is already checked out". Under
this launcher it is a detached HEAD at the same commit, so the start check
(`git rev-parse HEAD`) passes unchanged. Record the launcher change as an
orchestration deviation that applies equally to B and C.

## Executable resumption (Option 1)

From a clean checkout of the orchestration branch
(`claude/ex-ros-2026-a024-f5elvt` or main after merge):

1. `python3 research/experiments/EX-ROS-2026-A024-harness/make_manifest.py --check`
   must print `manifest hashes match`.
2. Read `research/experiments/EX-ROS-2026-A024-run/sessions.json`. Take the
   recorded heads from `git ls-remote origin refs/heads/experiment/a024-arm-2 refs/heads/experiment/a024-arm-3`.
   Each must equal the `headAfter` of that arm's last completed session.
3. Arm C item 02: launch `prompts/arm-3-02.txt` with `{START_SHA}` = arm-3
   head (`c85eb3e6e7f96d376613a18e9bdf8256bd92b870` unless it moved) and
   telemetry label `arm-3-02r2` (substitute `LABEL=arm-3-02` ->
   `LABEL=arm-3-02r2`), using the corrected launcher.
4. Then, serially in each of B and C, launch items 03, 04 and 05 the same way
   (`prompts/arm-{2,3}-0N.txt`, START_SHA = the branch head after the previous
   session is terminal). After each session:
   - `get_session` -> record `external_metadata.usage` and terminal time;
   - `list_events(kinds=["user"], limit=100)` saves to a file;
     `bash research/experiments/EX-ROS-2026-A024-run/collect.sh LABEL FILE`
     decodes and verifies the telemetry and prints the isolation audit;
   - for C, validate the new handoff with `validate_handoff.py` in a worktree
     of the branch head;
   - `python3 research/experiments/EX-ROS-2026-A024-run/ledger.py finish ...`
     records it; commit and push.
5. When all three arms are terminal, run the frozen phases 4 to 6:
   - `bash research/experiments/EX-ROS-2026-A024-harness/run_checks.sh <head> run/checks/<arm>` for each head;
   - draw a secret nonce (keep it outside the repository until unblinding)
     and run `prepare_blind.py score --seed 2026100724 --nonce N --start ddda83790b91d0f7e7c27b7b8cdc88bf330ad17d --heads HEADS.json --out DIR --publish`;
   - commit the printed mapping commitment;
   - launch one fresh scorer session per `experiment/a024-blind-score-<label>`
     with `prompts/acceptance-scorer.txt`, then the eval package
     (`prepare_blind.py eval ... --acceptance DIR --publish`) and one fresh
     evaluator session with `prompts/evaluator.txt`;
   - commit the evaluation, then reveal the nonce and mapping, assemble the
     analysis input and run `analysis.py`.

## State at stop

| Arm | Branch head | Completed items | Sessions |
| --- | --- | --- | --- |
| A continuous | `9a6c64f` | 01-05 | `arm-1` |
| B code-only | see `sessions.json` (`arm-2-02r`) | 01, 02 if `arm-2-02r` completed | `arm-2-01`, `arm-2-02` (stale), `arm-2-02r` |
| C handoff | `c85eb3e` | 01 | `arm-3-01`, `arm-3-02` (stale), `arm-3-02r` (stale, declined resync) |

Probe and preflight sessions are in `sessions.json` (`preflight`) and in
`deviations.md`. The throwaway probe branch `experiment/a024-launch-probe`
contains no product change.
