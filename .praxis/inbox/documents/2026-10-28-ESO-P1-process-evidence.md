# ESO-P1: Process acceptance evidence captured during execution

Priority: P0
Owner system: Praxis (records). Acceptance verification belongs to Indy-init's tooling; decisions to the owner.
Target repositories: praxis (capture), Indy-init (verification)
Initiative: Echelon development speed optimization, Priority One (owner kemiller2002, 2026-10-28)
Intake: Praxis document ingestion. Source material, not a canonical requirement. Work item: WI-0103.

## Evidence

Rehearsal 5 (`kemiller2002/indy-init-rehearsal-005`, final candidate `1b9fb56873fe7a98dffd5172066d909449c17eae`,
CI run 40620099844) ended INCOMPLETE: 49 of 326 P0 obligations had no proving evidence, and about 35 of them could
only be proven by evidence about how the rehearsal was conducted (reviewer conduct AR-008; agent scope and
checkpoints AR-012, AR-015, AR-021; owner decisions and routing AR-017, AR-020, AR-024, VER-010; release
selection VER-001..VER-005). The run's Praxis log (`.ros/events/events.jsonl`) holds 532 events: 463
`artifact.contributed`, 58 post-hoc `work.attribution.reconciled`, 8 `work.started`, 2 `work.completed` and a
single `work.checkpointed`. The two owner decisions were transcribed by the coordinating agent and recorded as
agent contributions.

## Proposed requirements

| ID | Requirement |
|---|---|
| ESO-P1-01 | Praxis records assignment, dependency decisions, scope changes, owner approvals, independent reviews, release-candidate selection, test results, acceptance-evidence references and final disposition as events in the existing work event log, at the time they happen. Execution start, identity and checkpoints stay with the existing `work.started`, execution envelope and `work.checkpointed` records. |
| ESO-P1-02 | Each record separates what the caller asserted from what Praxis observed (HEAD, branch, the actor's active execution and its role, how the identity was established). |
| ESO-P1-03 | Each record carries a provenance class from tool-observed, agent-assertion, reviewer-finding, owner-decision and verified-acceptance. An agent's statement is an agent assertion whatever it claims; an owner decision transcribed by an agent is an agent assertion; only a human actor's approval is an owner decision, and it is unattested without an external attestation reference; Praxis never writes verified-acceptance. |
| ESO-P1-04 | A record cannot borrow another actor's execution; such a request is refused and nothing is recorded. |
| ESO-P1-05 | Argument errors (unknown kind, invalid outcome, a candidate that is not a full commit SHA, a missing candidate where the statement concerns one) exit 2 and record nothing. |
| ESO-P1-06 | No historical approval is manufactured: the change applies to records written after it ships. |
