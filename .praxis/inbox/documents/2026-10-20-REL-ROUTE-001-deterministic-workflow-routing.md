# REL-ROUTE-001: Deterministic workflow routing by declared need

Priority: P2
Owner system: Praxis (work protocol); Ordo optional (decision admission flag)
Intake: Praxis document ingestion
Evidence: kemiller2002/cohort-based-engineering `research/experiments/REL-001/REPORT-run-02.md`, plus `run-02/analysis/results.json` (confirmatory) and `run-02/analysis/exploratory-results.json`.
Related: PRAXIS-REL-006 (rollout gate), REL-GATE-003.

## Finding
REL-001 run-02 compared four arms on 88 trials:
- A: bare Claude;
- B: Claude plus the directives pack;
- C: original Echelon;
- D: optimized Echelon.

On single-agent bounded tasks every arm was 39/39 correct. Optimized Echelon cost **+35.5%** per correct requirement over bare Claude (95% CI +24.9% to +49.5%, confirmatory H2). The same direction held in every category: simple +65%, moderate +24%, complex +26%, adversarial +39%.

Its mechanisms prevented failure classes that bare Claude does not guard against:
- concurrent ownership: 4/4 second claims refused in U6, and the deterministic experiment A;
- forged evidence: attested mode, gate cases GATE-A1 to A4;
- inadmissible decisions: deterministic experiment B, 0/11 invalid cases accepted.

So the expensive path is justified by the *properties of the work*, not by its inferred difficulty.

## Requirement
Praxis selects the work protocol for an item deterministically from declared, reviewable properties. It never selects it from heuristics or model judgement.
1. **Lightweight protocol (the default).** The agent receives the requirement and repository, works, runs its own tests and completes with `work complete`. There are no claims, no verifier and no directives pack.
2. **Assured protocol.** This is claim, then `work verify` with the attested host verifier, then `work complete-verified`. It is selected when any of these holds:
   - `workProtocol.concurrentAgents: true`, or the item is in a backlog that more than one agent can pull;
   - `workProtocol.requireVerifiedCompletion: true`, which requires attestation per REL-GATE-003;
   - the item declares decisions or conflicts that must be admitted (an Ordo contract is attached).
3. **Recording.** The selected protocol and the property that selected it are recorded on the work item and in the completion event.
4. **Fail closed.** A repository whose policy requires the assured protocol cannot fall back to the lightweight one. A malformed routing configuration also fails closed.

## Acceptance criteria
- Given the same declared properties, the same protocol is selected every time. A test covers each selector.
- An item in a `concurrentAgents` repository cannot be completed without a claim (PRX.CLAIM.REQUIRED).
- An item with an attached contract routes to the assured protocol, and admission refusals block completion.
- No routing input comes from task text, size or category.
- Docs state the run-02 evidence and its limits: the tasks hit a ceiling, there was one model, and U6 had n=2.
