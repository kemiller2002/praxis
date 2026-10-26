---
id: RQ-CAUSA-2026-0002
title: Validated, authoritative reconstruction of persisted investigations
status: accepted
version: 1.0.0
owners: [causa-rehearsal-003]
created: 2026-10-24
updated: 2026-10-24
priority: high
tags: [integrity, storage, domain]
related_documents: [RQ-CAUSA-2026-0001, RQ-CAUSA-2026-0002, RQ-CAUSA-2026-0003, RQ-CAUSA-2026-0004, RQ-CAUSA-2026-0005, RQ-CAUSA-2026-0006, RQ-CAUSA-2026-0007, RQ-CAUSA-2026-0008]
provenance:
  contributions:
    EXE-20261024T072747754Z-a9afd8e7:
      operations: [created]
      at: 2026-10-24T07:28:43.090Z
      actor:
        kind: agent
        id: claude-code-lead
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Rehearsal 3 requirements package"
derived_from: [INPUT-ca94a3c778288076]
---

# RQ-CAUSA-2026-0002 — Data integrity and authoritative reconstruction

Source: brief §7.1 "Data integrity", §8 layering rule; mission tests C, D, E, H, I; CONTRACT v1.0.0.

| ID | Acceptance criterion |
|---|---|
| INT-01 | Each investigation's `manifest.json`, `events.ndjson` and `snapshot.json` are validated by the canonical v1.0.0 validator: `Causa.Contract` `Store.validate` at tag `causa-storage-v1.0.0`, with the embedded v1.0.0 schema bundle. It runs every stage: encoding, JSON, canonical bytes, versions, registered types, JSON Schema, manifest consistency, identity/sequence, `History.validate`, `Evolve.replay`, and snapshot projection equality. |
| INT-02 | The catalog digests are checked: `eventsSha256` (raw bytes), `manifestSha256` and `snapshotSha256` (canonical JSON digest), and `provenanceSha256` (canonical digest of the provenance index). Any mismatch gives the state **digest mismatch** for that investigation, naming the file. |
| INT-03 | The reconstructed state digest equals `expectedStateSha256`. The replayed version equals `expectedVersion`, and the lifecycle equals `expectedLifecycle`. The UI shows the verified digest. |
| INT-04 | An investigation that fails any check shows **validation failed** with the contract error code (e.g. `CAUSA.EVENTS_HASH_MISMATCH`) and detail. The other investigations stay listed, openable and unaffected. Nothing is silently dropped. |
| INT-05 | The application never interprets unvalidated persisted data as domain state. Only a value produced by successful validation can reach the investigation views; this is enforced by type (the view model is built only from a `Verified` record). |
| INT-06 | The historical source is read-only. The application issues only `GET` requests to GitHub. |
| INT-07 | The vendored domain engine and contract sources are byte-identical to the pinned sources (engine `55f49f6`, contract tag `causa-storage-v1.0.0`) and are checked by sha256 in CI. The frozen v1.0.0 compatibility corpus (golden fixtures and negative fixtures) passes through the application's own validator build in CI. |
