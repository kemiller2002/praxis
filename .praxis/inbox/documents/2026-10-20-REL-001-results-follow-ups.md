# REL-001 results: follow-up requirements from the reliability experiment

Priority: mixed (each finding gives its own)
Owner system: Praxis (F1 also Dokimos)
Intake: Praxis document ingestion
Source: kemiller2002/cohort-based-engineering `research/experiments/REL-001` (run-01: gate, Experiments A–D, `analysis/`)
Pins: Praxis 3.16.0 (treatment) vs 3.12.0 (baseline), Dokimos 0.6.0, Ordo 1.17.0
Duplicate check (2026-10-20): `.ros/work/queue.json` and `.praxis/processed/*` were searched for
REL-GATE-*, PRAXIS-REL-*, "attest", "session", "price table", "worktree" and "validate-run".
Existing items are linked below rather than re-captured.
- **PRAXIS-REL-007 is satisfied by this run.** The pilot ran with preregistered replicates, a frozen
  task set, both arms, and captured transcripts. Close it with that evidence.
- **F2 and F5 are new.** F2 extends PRAXIS-REL-004; F5 extends requirement PRX-REL-009.

## F1 (P1): Validation evidence can be forged by an actor with write access to the repository (C3b)
**Observed.** Experiment C case C3b: a fabricated passing `praxis.validation-result/1` file, plus a
matching hand-written `work.validation.observed` event, is accepted as verified completion on 3.16.0.
`ValidatedEvidence` and `BoundCompletionAuthority` make it impossible to *construct* a passing packet
without a recorded result. However, the record itself lives in the agent-writable worktree, so an agent
that writes both files bypasses the check. See `results/c/treatment.jsonl` (C3b).

**Expected.** A passing validation is accepted only when it carries an attestation the agent cannot
produce, such as a CI run attestation or a signature from a host-held key bound to the commit, the
command and the result digest. Dokimos verifies the attestation independently.

**Acceptance criteria.**
- C3b is rejected.
- GATE-5 (a genuine `validate-run -- true`) still verifies.
- The attestation key or source is outside the agent's write scope.
- A negative test exists for each forged part: result, event, and attestation.

## F2 (P1): Executions carry no provider-session binding, so usage cannot be attributed to runs
**Observed.**
- `telemetry reliability-report` left every provider observation of every Experiment D run unattributed
  (`no-execution-for-session`).
- The execution envelope records `actor.provider/model/runtime = null` and no session or agent ID.
- Sub-agent transcripts carry the parent session's ID, so session-window attribution (R3) cannot
  separate concurrent or nested agents.
- The experiment had to attribute by transcript directory instead (protocol amendment A4).

**Expected.** An execution records the provider session ID and agent ID of the agent that runs it, and
the report attributes by agent ID before session ID.

**Acceptance criteria.** Given a transcript directory holding several sub-agents of one parent session,
each execution's tokens equal exactly its own agent's deduplicated observations, and nothing is left
unattributed.

**Link.** Extends PRAXIS-REL-004 (automatic ingestion); this is a separate defect in attribution
identity.

## F3 (P2): Verified completion leaves the worktree dirty or moves HEAD past the verified commit
**Observed.** In all 6 treatment runs, Praxis wrote claim, validation and event state under `.ros/`
after the commit being verified.
- In 4 runs the agents left that state uncommitted.
- In 2 runs the agents committed it, so HEAD no longer equals the verified commit.
- `validate-run` refuses a dirty worktree, so the state Praxis itself writes conflicts with the
  clean-commit rule.

**Expected.** Claim and validation state that Praxis writes is stored outside the tracked tree (for
example under the git ref store already used by `--store git`), or is excluded from the cleanliness and
commit-identity checks.

**Acceptance criteria.**
- After `complete-verified`, `git status` is clean.
- HEAD equals the verified commit.
- No information is lost.

## F4 (P2): The verified-completion workflow costs measurably more effort per verified requirement
**Observed.** This is exploratory; the preregistered analysis was inconclusive. On 6 paired task blocks
the treatment used, relative to the baseline:
- **Tokens per verified requirement:** +32%, bootstrap 95% interval +14% to +56%. The harness count
  gives +8%.
- **Tool calls:** +36%.
- **Test runs:** 2.17 vs 1 per run.
- **Elapsed time:** +58%.

Verified requirements, rework and escaped defects were identical (12/12, 0, 0). Most of the extra
effort is protocol steps: claim, commit before `validate-run`, a second test run under `validate-run`,
and a long `complete-verified` command line.

**Expected.**
- `validate-run` is the agent's only test run, and its result is reusable.
- `complete-verified` derives commit, verification revision, requirement and decision IDs from the
  claim, contract and authority instead of requiring them on the command line.

**Acceptance criteria.**
- The REL-001 D treatment brief shrinks to claim, validate-run and complete.
- A re-run of D shows a smaller token overhead. No target is set before measuring.

## F5 (P3): Cost per verified requirement is unmeasurable without a versioned price table
**Observed.** Every run reports estimated and billed cost as `unavailable`, because Praxis ships no
prices and no provider reports billed cost in transcripts. The preregistered REL-001 analysis therefore
admitted no runs.

**Expected.** A versioned, sourced price table is used with `--price-table`, and cost is reported as
estimated. Cost is never inferred without that table.

**Acceptance criteria.** The `reliability-report` cost is available, with `basis = estimated` and the
table version recorded.

**Link.** Requirement PRX-REL-009 (provider-usage reporting) defines the cost fields.

## F6 (P3): Turn on required verified completion only after F1–F3
**Link.** PRAXIS-REL-006 (`workProtocol.requireVerifiedCompletion` in Echelon repositories) should
depend on F1–F3. Otherwise every repository inherits the forged-evidence gap, the attribution gap and
the dirty-worktree conflict.
