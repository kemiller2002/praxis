# REL-GATE-004: Attested completion trusts a caller-chosen authority and the worktree's own policy

Priority: P1 (blocks rollout of attested verified completion wherever the completing process is agent-controlled)
Owner system: Praxis (cohort run-02 harness for the experimental side)
Intake: Praxis document ingestion
Existing work items: related to PRAXIS-REL-009 (attested evidence, complete), PRAXIS-REL-006 (rollout gate, captured) and
REL-GATE-003 (non-attested forgery, queued). Not a duplicate: REL-GATE-003 concerns non-attested mode; this concerns where
attested mode takes its trust root from.
Experimental finding: kemiller2002/cohort-based-engineering `research/reliability/evidence/probes-v3.17.0.jsonl`
(script `research/reliability/scripts/probes.sh`, released Praxis 3.17.0 + Dokimos 0.7.0, binaries checked against the
gate-report sha256 pins).

## Observed failure
A process that can run `praxis work complete-verified` in the repository (the implementation agent in a local workflow,
and the agent in REL-001 run-02 arm D) completes an item as verified without any genuine test run:
- PROBE-3: the agent commits a `ros.json` that drops `workProtocol.requireAttestedEvidence`, passes a keyless authority,
  and cites a forged store result + forged `work.validation.observed` event. Exit 0, `verified`, completion identity
  `consistent`/clean. `FileWorkClaims.readProtocolFlag` (src/Praxis.Infrastructure/Work/FileWorkClaims.fs:251) reads
  `ros.json` from the working tree, i.e. from the agent's own commit, not from the protected branch.
- PROBE-4: with `requireAttestedEvidence: true` unchanged, the agent signs a receipt with its own P-256 key and names an
  authority file that pins that key (`--authority` or `PRAXIS_COMPLETION_AUTHORITY`). Exit 0, `verified` by Praxis and by
  Dokimos 0.7.0. The authority path is caller-chosen (src/Praxis.Infrastructure/Work/FileDerivedCompletion.fs:260-266, 308).
- Control PROBE-1: a keyless authority under an unmodified attested policy is refused (`PRX.COMPLETION.ATTESTATION_REQUIRED`).

docs/verified-completion-rollout.md states that `ros.json` "lives on the protected default branch, so either flag can change
only through a reviewed change" and that "no per-run file, environment variable or CLI flag can switch attestation off".
The code does not enforce either statement; they hold only when complete-verified runs on the host/CI with
protected-branch inputs (gate item 7), which is not how arm D or a local agent workflow runs.

## Expected behaviour
1. The policy that decides attestation is read from a protected source (the default branch's `ros.json` at the merge
   base, or a host-supplied policy path), not from the worktree being completed; a `ros.json` change in the verified
   range relative to that source fails closed (`PRX.COMPLETION.POLICY_CHANGED`).
2. The trusted verifier keys come from a host-held trust root that the completing caller cannot substitute: for example a
   host trust file outside every workspace whose digest is pinned in protected policy, or the key fetched from the verifier
   socket and matched against a pinned keyid. A caller-supplied authority that adds keys not in the trust root is refused
   (`PRX.COMPLETION.UNTRUSTED_AUTHORITY`).
3. The completion record names the receipt keyid, so a post-hoc audit can compare it with the host verifier's key.

## Acceptance criteria
- PROBE-3 and PROBE-4 are refused with stable codes; PROBE-1 and gate cases GATE-A1..A4, C8a-d keep their results.
- A test asserts that editing `ros.json` in the claimed branch cannot disable `requireAttestedEvidence`.
- Cohort side (run-02 harness, separate change): score X6 also checks the receipt keyid against `run.json` verifier.keyId,
  the effective authority path in the praxis shim log, and any `ros.json` change in the agent's commits; the verifier runs
  under a uid the agent cannot ptrace or read.
