# REL-TOKEN-003: `praxis work finish`: one deterministic command for commit check, verify and complete-verified

Priority: P4 (deterministic work out of the LLM) with P5 (batching); does not change F1-F3
Owner system: Praxis
Intake: Praxis document ingestion (draft from Echelon research worker D; coordinator places it)
Existing work items: follows PRAXIS-REL-012 (derived completion, complete), which removed argument derivation from the
agent (`src/Praxis.Application/Work/DerivedCompletion.fs:98-185`). Not a duplicate: the agent still issues verify and
complete-verified as separate model turns.
Experimental finding: cohort `research/token-efficiency/OVERHEAD-ANALYSIS.md` ("P4/P5")

## Observed cost
Every agent turn re-reads the whole context. In run-02 Echelon trials the mean context per request is ~62k tokens, of
which a fixed ~40.6k is the runtime prefix (OVERHEAD-ANALYSIS.md, "Repeated context loading"). The mandated workflow
commands (claim, verify or validate-run, complete-verified) are a median 27% of tool calls; under a one-turn-per-command
model they account for ~28% of context tokens (estimate). The steps after the last commit are a fixed sequence with
no decision except "on refusal, read the codes".

## Expected behaviour
1. `praxis work finish --id ID --token TOKEN [--store ...]` refuses a dirty worktree (same code as verify), runs
   `work verify` at HEAD, and, when every receipt passed, runs `work complete-verified` with the same derivation.
2. Output is one compact JSON object: verify outcome, receipt digests, completion outcome, metadata commit, and on failure
   the refusal codes plus a bounded excerpt (for example the failing test names, at most N lines) taken from the receipt
   output that `TrustedVerifier` already keeps (`src/Praxis.Infrastructure/Work/TrustedVerifier.fs:279-312`, stored beside the receipt as `.output.txt` at `:446-458`), so the
   agent does not need its own extra test run to see what failed.
3. Each step keeps its own event and timing (`completeVerifiedTimings`), so measurement is unchanged.

## Acceptance criteria
- `finish` on a clean, passing HEAD produces the same events and records as `verify` followed by `complete-verified`.
- A refusal at either step exits non-zero with the step's codes; nothing is completed after a not-passed receipt.
- The REL-001 gate cases keep their results.
