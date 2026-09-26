# Dual-entry execution, reconciliation, and input inbox

Status: approved
Work item: WI-0064
Branch invariant: WI-0064

## Objective
Praxis MUST remain usable when an agent cannot execute the native Praxis runtime. Native execution and a runtime-free JSON envelope are two entry paths into the same canonical work/event model.

## Requirements
1. Every work item MUST execute on a distinct branch whose name is the work-item ID. A claimed work-item/branch mismatch MUST fail reconciliation.
2. Agents MUST attempt the native Praxis executable first. If unavailable or unusable, they MAY submit a JSON envelope without installing a local runtime.
3. The envelope is untrusted proposed input, never canonical state. It MUST contain schema version, transaction ID, work-item ID, claimed branch, base commit, structured agent identity, ordered timeline, requested transitions/events, evidence and artifact references.
4. CI MUST independently observe the actual ref/commit and reconcile the envelope through the same domain rules used by native Praxis. JSON MUST NOT bypass transition, evidence, provenance, identity, attribution, or validation rules.
5. Reconciliation MUST be deterministic and idempotent. Replaying a transaction after partial failure MUST not duplicate events, telemetry, provenance, or transitions.
6. Accepted envelope contents MUST become durable canonical Praxis history before transient input is removed. Rejected input MUST remain available with machine-readable diagnostics and MUST NOT partially mutate canonical state.
7. Successful reconciliation MUST commit canonical state, create a checkpoint tag tied to the transaction, and remove the transient envelope. Work-item branch deletion is separate and occurs only under the normal completion/merge lifecycle.
8. Praxis MUST provide an input-documents inbox for unprocessed human/agent material. Inputs may include requirements, research, decisions, notes and other supported documents.
9. Inbox classification is advisory, not trusted. Processing MUST discover requirements, decisions, constraints, evidence, risks, open questions and references, preserving provenance to the original input.
10. An input MUST remain unprocessed until every derived canonical mutation is durably reconciled. Claiming/processing MUST be crash-safe and idempotent.
11. An agent entering a repository MUST be able to discover pending inputs and fallback-envelope instructions without a Praxis runtime.
12. Agent identity is mandatory for native and envelope paths and MUST propagate to derived requirements, events, timeline entries, provenance and telemetry.
13. Cross-work-item contamination MUST be rejected unless an explicit multi-work-item protocol is introduced later.
14. The protocol MUST be sufficient to bootstrap participation in a repository where Praxis cannot yet execute locally.
15. CI is the authoritative reconciliation boundary.

## Required repository layout
```
.praxis/
  inbox/
    documents/
  outbox/
    events/
  processing/
  rejected/
```

The physical layout MAY evolve only if the same lifecycle and discoverability guarantees remain.

## Acceptance criteria
- Native and envelope paths produce semantically equivalent canonical outcomes for the same legal work.
- Invalid schema, missing identity, branch mismatch, stale/invalid base, illegal transition, invalid evidence and duplicate transaction tests exist.
- Crash/retry tests prove idempotency before and after canonical commit.
- Inbox claim/retry/reconcile tests prove no source is lost.
- Validation rejects meaningful work on a branch other than its work-item ID.
- Successful reconciliation leaves no transient accepted envelope and leaves durable provenance plus a checkpoint.
- Documentation includes a runtime-free agent bootstrap example.
