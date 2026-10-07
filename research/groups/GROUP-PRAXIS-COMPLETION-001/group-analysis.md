# Group analysis: GROUP-PRAXIS-COMPLETION-001

- Group: `GROUP-PRAXIS-COMPLETION-001` (`./praxis work group show GROUP-PRAXIS-COMPLETION-001`)
- Members: `PRAXIS-QUAL-07`, `PRAXIS-QUAL-08`, `PRAXIS-QUAL-09`, `PRAXIS-QUAL-10`
- Execution repository: kemiller2002/praxis
- Base commit: `f242aae664b4601e106b6e33ebce2b95bedfae35`
- Group record: first declared at 2026-10-06T20:35Z (commit `aaba4aa`); when
  `origin/main` (group store v2) was merged in, the branch's Praxis state was
  taken from main and the declaration was re-recorded with `work group
  create` (commit `2723128`), so the store's `createdAt` is the re-recorded
  time. History is append-only from that commit on.
- Machine-readable form: [`group-analysis.json`](group-analysis.json) (`praxis.group-analysis/1`)

## 1. Members

| Member | Obligation | Acceptance criteria | Depends on |
|---|---|---|---|
| PRAXIS-QUAL-07 | Engineering-risk metadata on work items (PRX-QUAL-020) | 1. A work item may declare praxis.work-risk/1 metadata: change class, risk level, persistent-state impact, external protocol/provider impact, security/privacy impact, failure posture (fail-open, fail-closed, indeterminate), tier ownership and required live/integration proof, set by work capture/update flags and shown by work context. 2. Invalid values are refused at capture/update. 3. Completion obligations (design-debt declaration, verification-matrix dimensions) are derived from the metadata by a pure domain function. 4. Absent metadata keeps existing behaviour. 5. Tests cover parsing, validation and obligation derivation. | - |
| PRAXIS-QUAL-08 | Design-debt declaration as a completion-readiness facet (PRX-QUAL-021) | 1. Completion accepts --evidence design-debt=PATH, a praxis.design-debt/1 document declaring either no known design debt or debt entries referencing captured work items with rationale, risk and the compromised boundary. 2. A design-debt-declared facet joins the A052 completion-readiness record. 3. For high-risk work (from PRAXIS-QUAL-07 metadata), a missing declaration or debt referencing no recorded non-terminal work item refuses completion (exit 3, no state change). 4. A declared prototype cannot complete as canonical without a debt entry. 5. Tests cover each case. | PRAXIS-QUAL-07 |
| PRAXIS-QUAL-09 | Verification-matrix obligation as a completion-readiness facet (PRX-QUAL-022) | 1. Completion accepts --evidence verification-matrix=PATH, a praxis.verification-matrix/1 document with rows for happy path, negative/failure, corruption/partial state, concurrency, compatibility/version skew, recovery/rollback and representative live effect, each met, not-applicable (with reason) or not-met, with evidence. 2. A verification-matrix facet joins the A052 completion-readiness record. 3. For stateful control-plane, persistence, release/bootstrap, security or remote-execution work (from PRAXIS-QUAL-07 change class), a missing matrix or any required dimension not met refuses completion; unit-test or build evidence alone never satisfies it. 4. Tests cover each case. | PRAXIS-QUAL-07 |
| PRAXIS-QUAL-10 | Evidence-file digests and a release-readiness evidence contract (PRX-QUAL-023 leftovers) | 1. Every consumed evidence file (dokimos-ratchet, ordo-boundary and the new design-debt, verification-matrix and release-readiness documents) is recorded with its sha256 digest in the completion-readiness record. 2. A praxis.release-readiness/1 contract is documented and consumed through --evidence release-readiness=PATH: release identity, the checks run and their results; release-ready is satisfied only by a consistent document whose checks all passed, unavailable when absent, malformed or inconsistent. 3. requirements/CODE-QUALITY-HARDENING.md drops both items from its not-yet-done list. 4. Tests cover digest recording and each release-readiness outcome. | - |

## 2. Reuse inventory

| Existing element | Location | Reuse, extend, or not reused (why) |
|---|---|---|
| CompletionFacet / FacetStatus / FacetAssessment | `src/Praxis.Domain/Work/CompletionReadiness.fs:29` | extended: the A052 facet mechanism; design-debt-declared and verification-matrix are new facets beside the four existing ones (PRAXIS-GROUP-10 adds group-verified the same way) |
| EvidenceReading / SourceObservation / observationGap | `src/Praxis.Domain/Work/CompletionReadiness.fs:118` | reused: every new evidence document is observed and judged through the same reading/observation types, so unavailable is never a pass |
| CompletionReadinessOperations.observe/assess/gate | `src/Praxis.Application/Work/CompletionReadiness.fs:70` | extended: the one pure gate; new sources and the risk metadata are inputs to assess |
| FileCompletionReadiness.readText/sources | `src/Praxis.Infrastructure/Work/FileCompletionReadiness.fs:17` | extended: the one evidence-file reader; it also computes the sha256 digest of the bytes it decoded |
| QualityEvidenceJson decoders and readinessNode | `src/Praxis.Contracts/Work/QualityEvidenceJson.fs:152` | extended: the one contract module for consumed evidence; design-debt, verification-matrix and release-readiness decoders are added beside decodeDokimos/decodeOrdo |
| WorkUpdate.plan and FileBacklogQueueRepository.applyUpdate | `src/Praxis.Domain/Work/Update.fs:59` | extended: risk metadata is set through the existing work update transition and stored on the queue item |
| QualityEvidencePolicies.appliesTo (policy opt-in) | `src/Praxis.Application/Work/CompletionReadiness.fs:31` | extended: risk-derived obligations activate the gate for an item even when the repository policy is otherwise off, keeping one gate |
| QualityEvidenceTests fixtures | `tests/Praxis.Tests/QualityEvidenceTests.fs:1` | reused: existing gate/test helpers are extended with the new facets |

Searches that established the inventory:

- `grep -rn "risk\|changeClass\|failurePosture" src/Praxis.Domain/Work src/Praxis.Contracts/Work`: no work-item risk metadata exists (only the Ordo boundary risk level, which is an external tool's judgement)
- `grep -rn "designDebt\|verificationMatrix\|release-readiness" src`: none exist; release-ready is always unavailable (CompletionReadiness.releaseFacet)
- `grep -rn "SHA256\|sha256" src --include=*.fs -l`: digest helpers exist for lifecycle payloads and installation records; the readiness reader uses System.Security.Cryptography.SHA256 directly at the file edge

New abstractions:

- **WorkRisk (praxis.work-risk/1)**: considered OrdoBoundaryEvidence.Risk and work tags; Ordo's risk is an assessment of a diff; tags are untyped and carry no failure posture or obligations.
- **DesignDebtDeclaration, VerificationMatrix, ReleaseReadinessEvidence documents**: considered DokimosRatchetEvidence/OrdoBoundaryEvidence decoders; different contracts; they reuse the same EvidenceReading/SourceObservation path rather than a parallel one.

## 3. Group-level design

- Common architecture and shared invariants: the DF-ROS-2026-A052 completion-readiness gate. Every obligation is a facet; unavailable evidence is never a pass; a refusal exits 3 with no state change.
- Conflicting requirements: PRX-QUAL-020 obligations must bind high-risk items even when `workProtocol.qualityEvidence` is off; resolved by letting declared risk metadata make the gate applicable to that item only.
- One design serving several members: one evidence reader that decodes, digests (PRAXIS-QUAL-10) and classifies every consumed document.
- Compatibility and migration: items with no risk metadata and repositories with no policy keep existing behaviour; the readiness record gains additive fields.
- Common tests: QualityEvidenceTests.
- Risks of solving each member independently: three parallel evidence readers and three ways to express "unavailable".
- Shared group infrastructure: the facet additions and digest are attributed to PRAXIS-QUAL-10 (digest) and PRAXIS-QUAL-08/09 (facets).

Decisions:

- New facets are added as CompletionFacet cases and keep the facet-code pattern so PRAXIS-GROUP-10's group-verified facet can be merged beside them
- Risk-derived obligations bind only items that declared risk metadata; items without it keep existing behaviour

## 4. Order

- PRAXIS-QUAL-07 before PRAXIS-QUAL-08 (merged)
- PRAXIS-QUAL-07 before PRAXIS-QUAL-09 (merged)

A group checkpoint is recorded after each member's pull request merges.

## 5. Verification pass (before completing any member)

Exercised per member and criterion before completion; machine-readable form in [`group-verification.json`](group-verification.json) (`praxis.group-verification/1`).

| Member | Criterion | Status | Evidence |
|---|---|---|---|
| PRAXIS-QUAL-07 | 1. A work item may declare praxis.work-risk/1 metadata: change class, risk level, persistent-state impact, external protocol/provider impact, security/privacy impact, failure posture (fail-open, fail-closed, indeterminate), tier ownership and required live/integration proof, set by work capture/update flags and shown by work context. | met | test: `completion obligations: the work update risk options parse into a validated declaration; cli: risk metadata on a high-risk persistence item makes design debt and a matrix completion obligations (work update --change-class ... then work context shows risk and obligations)` -> passed (PRAXIS_TEST_FILTER='completion obligations', 28/28) |
| PRAXIS-QUAL-07 | 2. Invalid values are refused at capture/update. | met | test: `completion obligations: risk metadata needs a change class and, with impact, a failure posture; an invalid risk declaration rejects the update; praxis.work-risk/1 round-trips and refuses unknown values` -> passed |
| PRAXIS-QUAL-07 | 3. Completion obligations (design-debt declaration, verification-matrix dimensions) are derived from the metadata by a pure domain function. | met | test: `completion obligations: high-risk work owes a design-debt declaration; persistence work owes a matrix with concurrency but no live effect; remote-execution work and a declared live proof owe a live effect` -> passed |
| PRAXIS-QUAL-07 | 4. Absent metadata keeps existing behaviour. | met | test: `completion obligations: work without risk metadata has no obligations; cli: an item without risk metadata completes exactly as before` -> passed |
| PRAXIS-QUAL-07 | 5. Tests cover parsing, validation and obligation derivation. | met | location: `tests/Praxis.Tests/CompletionObligationsTests.fs` -> riskTests cover parsing, validation and obligation derivation |
| PRAXIS-QUAL-08 | 1. Completion accepts --evidence design-debt=PATH, a praxis.design-debt/1 document declaring either no known design debt or debt entries referencing captured work items with rationale, risk and the compromised boundary. | met | test: `completion obligations: praxis.design-debt/1 decoding is strict; a declaration of no known debt passes; debt tracked by a recorded open item passes` -> passed |
| PRAXIS-QUAL-08 | 2. A design-debt-declared facet joins the A052 completion-readiness record. | met | test: `completion obligations: the gate records every consumed evidence file with its digest (designDebtDeclared facet satisfied in the readiness record)` -> passed |
| PRAXIS-QUAL-08 | 3. For high-risk work (from PRAXIS-QUAL-07 metadata), a missing declaration or debt referencing no recorded non-terminal work item refuses completion (exit 3, no state change). | met | test: `completion obligations: untracked debt fails: the item itself or an unrecorded or closed item; cli: high-risk item refused (exit 3) without a declaration and with untracked debt, item stays active` -> passed |
| PRAXIS-QUAL-08 | 4. A declared prototype cannot complete as canonical without a debt entry. | met | test: `completion obligations: a prototype cannot become canonical without a debt entry` -> passed |
| PRAXIS-QUAL-08 | 5. Tests cover each case. | met | location: `tests/Praxis.Tests/CompletionObligationsTests.fs` -> debtTests and cliTests |
| PRAXIS-QUAL-09 | 1. Completion accepts --evidence verification-matrix=PATH, a praxis.verification-matrix/1 document with rows for happy path, negative/failure, corruption/partial state, concurrency, compatibility/version skew, recovery/rollback and representative live effect, each met, not-applicable (with reason) or not-met, with evidence. | met | test: `completion obligations: praxis.verification-matrix/1 decoding is strict; a complete matrix with real evidence passes` -> passed |
| PRAXIS-QUAL-09 | 2. A verification-matrix facet joins the A052 completion-readiness record. | met | test: `completion obligations: cli: matrix satisfied facet recorded as satisfied on completion` -> passed |
| PRAXIS-QUAL-09 | 3. For stateful control-plane, persistence, release/bootstrap, security or remote-execution work (from PRAXIS-QUAL-07 change class), a missing matrix or any required dimension not met refuses completion; unit-test or build evidence alone never satisfies it. | met | test: `completion obligations: a missing required dimension, a not-met row and a not-applicable required row fail; compilation or unit tests alone never satisfy the matrix; a met row needs evidence, a live effect needs live evidence, and locations must exist; cli refusal without a matrix` -> passed |
| PRAXIS-QUAL-09 | 4. Tests cover each case. | met | command: `dotnet tests/Praxis.Tests/bin/Release/net10.0/Praxis.Tests.dll (completion branch)` -> targeted suites passed: CompletionObligations, QualityEvidence, WorkUpdate*, CliBoundaryRatchet, Architecture, WorkGroup |
| PRAXIS-QUAL-10 | 1. Every consumed evidence file (dokimos-ratchet, ordo-boundary and the new design-debt, verification-matrix and release-readiness documents) is recorded with its sha256 digest in the completion-readiness record. | met | test: `completion obligations: the digest of a consumed evidence file is the sha256 of its bytes; the gate records every consumed evidence file with its digest; cli: consumedEvidence carries sha256 for design-debt and verification-matrix` -> passed |
| PRAXIS-QUAL-10 | 2. A praxis.release-readiness/1 contract is documented and consumed through --evidence release-readiness=PATH: release identity, the checks run and their results; release-ready is satisfied only by a consistent document whose checks all passed, unavailable when absent, malformed or inconsistent. | met | test: `completion obligations: a ready release whose checks all passed is release-ready; a ready verdict over a failed or skipped check is inconsistent; a not-ready release fails; a required release-ready facet is unavailable without evidence and satisfied with it; praxis.release-readiness/1 decoding is strict` -> passed |
| PRAXIS-QUAL-10 | 3. requirements/CODE-QUALITY-HARDENING.md drops both items from its not-yet-done list. | met | location: `requirements/CODE-QUALITY-HARDENING.md` -> PRX-QUAL-023 update note replaces the not-yet-done list |
| PRAXIS-QUAL-10 | 4. Tests cover digest recording and each release-readiness outcome. | met | location: `tests/Praxis.Tests/CompletionObligationsTests.fs` -> releaseTests |
