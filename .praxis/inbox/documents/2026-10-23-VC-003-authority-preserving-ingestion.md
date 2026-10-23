# VC-003: Authority-preserving requirement ingestion and handoff

## Goal
Ensure Praxis hands Ordo an unambiguous, versioned distinction between accepted requirements, authorized decisions, unresolved questions and observed legacy behavior.

## Requirements
1. Extend or map the existing Praxis IR to carry stable requirement IDs, accepted decision IDs, provenance, source revision, supersession, and explicit unresolved status without breaking existing ingestion.
2. Treat code-embedded rules as observations, not automatically accepted requirements. Preserve this distinction across work-item grouping, horizontal slices, agent handoff and verified-state reconstruction.
3. A proposed rule or agent interpretation cannot self-authorize. If policy authority is absent, the permitted outcome is to record an unresolved question and continue only with independent authorized work.
4. Preserve the frozen four-sentence minimal authority contract and its version metadata as specified in VAC-002-P3 R-PRAX-1; do not add an unproven mandatory checklist.
5. Export a deterministic, schema-validated handoff contract to Ordo; include requirement coverage mapping and unresolved-policy list. Define compatibility and migration behavior.
6. Capture incremental cost and tokens of the new IR and handoff; do not require full repeated verification at every intermediate commit.

## Acceptance
Versioned schema and examples, contract tests for provenance/supersession/unknown rules, no self-authorization, backward compatibility checks and a runnable integration fixture with Ordo. Stage behind experimental opt-in until VC-001 evidence.
