---
id: JR-ROS-2026-A012
title: Adaptive execution telemetry archaeology and implementation journal
status: complete
version: 1.1.0
owners:
  - repository-governance
created: 2026-09-05
updated: 2026-09-05
supersedes: []
superseded_by: []
related_documents: [DF-ROS-2026-A010, EV-ROS-2026-A011, EV-ROS-2026-A014, docs/development-telemetry.md]
tags: [telemetry, implementation, journal]
confidence: high
---

# Objective

Make trustworthy, adaptive, provider-neutral development telemetry part of normal ROS execution while preserving history and the existing work/external-system boundary.

# Chronology

1. Read root governance, the operating manual, engineering standard, constitution, work protocol, repository tree, schemas, registries, bootstrap manifests, CLI, tests, prompts, context, and current dirty state.
2. Registered and began `WI-0003`; baseline was `main` at `aa06e6be9da363db2da33308634bbf5268b759e1`. Existing Project Administration Hub changes were left in place.
3. Ran the baseline suite. The sandboxed run failed 14 localhost server cases with `listen EPERM`; the same suite with localhost permission passed 60 Node and 7 Python tests.
4. Surveyed official Codex, Claude Code, Gemini CLI, and Copilot runtime surfaces. Confirming evidence favored an adapter edge plus normalized/raw layers. Falsifying checks rejected a universal token shape, automatic hook installation, dirty-tree attribution, and cumulative-counter summation.
5. Implemented the execution schema, normalized registry, segmented records, runtime discovery, adapters, privacy filter, classification/scope/quality fields, Git/clock collection, aggregation, validation, CLI commands, and automatic lifecycle integration.
6. Started this mission's own telemetry execution after the mechanism became runnable. Earlier elapsed time and edits are consequently not represented as mechanically observed execution metrics; the dirty baseline makes execution-attributed Git deltas unavailable by design.
7. Added focused tests and provider-neutral documentation. Initial focused telemetry testing found and corrected capability-source initialization, portable execution-ID formatting, first-path whitespace trimming, and cumulative-session test timestamp ambiguity.
8. Ran an adversarial review as architect, provider integrator, research/data-quality reviewer, security reviewer, and Git maintainer. It found and corrected ROS-internal path inflation, broad field matching that hid new nested provider fields, direct-file raw-secret bypass, unversioned calculated costs, invented numeric confidence, and contradictory same-snapshot capability/value declarations.
9. Re-ran the focused suite with 14 passing telemetry cases and the broad suite with 74 passing Node tests plus 7 passing Python tests, then closed the initial implementation as `WI-0003`.
10. A second post-close review found four material data-quality gaps: raw-disabled retries were not idempotent, equal session-ID strings across providers could collide, OTel aliases/timestamps could double-count or fail, and malformed nested records could crash validation. Recorded follow-up `WI-0004`, corrected them, and added regressions.
11. The final focused suite passed 16 telemetry cases. The final broad suite passed 76 Node and 7 Python tests.
12. Final aggregate inspection found that repository and context gauges inherited counter-like aggregation. Changed dirty-path snapshots to `maximum`, context gauges to non-aggregated per-session observations, and extended the multi-execution regression.
13. The user required continued iteration to diminishing returns. Opened `WI-0007` and argued against unlocked direct JSON. Added shared atomic persistence and resource locks; the first verification exposed missing bootstrap/package manifests and lock-path Git contamination, both corrected.
14. Added a parallel stress regression for eight execution starts and twelve callbacks. Challenged stale-lock ownership and final-envelope races, then added live-owner checks, random ownership tokens, and serialized late final-envelope ingestion.
15. Challenged capability overwrite and storage growth. Added bounded transition history with distinct source/ROS timestamps, explicit history collapse counts, per-execution raw count/byte budgets, raw-omission provenance, and regressions for flapping, delayed timestamps, count limits, and byte limits.
16. Challenged aggregate and link semantics. Separated work-item calendar span from overlapping execution-wall effort; clarified repeated test-result counts; added reverse-backlink, duplicate capability, duplicate measurement, history, and byte-count validation.
17. The focused suite passed 22 telemetry cases. The first broad run passed every non-server case and failed 14 localhost tests at sandbox `listen EPERM`; the authorized rerun passed 82 Node and 7 Python tests.
18. A final challenge found that retention values were constrained in JSON Schema but not mechanically rejected by the dependency-free runtime configuration path. Added focused runtime-policy validation and a regression for an invalid history bound. The final focused suite passed 23 cases and the full suite passed 83 Node and 7 Python tests. `EV-ROS-2026-A014` records alternatives, remaining weaknesses, and reopening conditions.
19. A lock failure-path review found that owner-metadata write failure could strand a new lock and that stale reclaim needed an ownership-data recheck. Hardened acquisition cleanup and reclamation, added a direct stale/release/reacquisition test, and reran 24 focused cases plus the full 84-Node/7-Python suite successfully.

# Live findings and debt

- Unknown raw fields can survive immediately, but promotion to normalized semantics still requires human/provider documentation review.
- Runtime hooks/OTel provide substantially better tool and timing telemetry than agent self-report, but enabling them is provider/project policy and cannot safely be assumed by ROS.
- Test/build outcome parsing remains an adapter/tool integration responsibility; ROS supplies normalized fields and explicit CLI recording rather than heuristically scraping arbitrary command output.
- Current local records are not signed, remotely published, centrally retained, or reconciled.
- Calculated cost now requires pricing source/version, while provider-reported cost retains the provider/runtime source without pretending ROS knows the underlying rate table.
- Repeated adversarial cycles now have executable regressions. A final review found no additional proportional local change: append-only ingestion, distributed transactions, automatic repair, schema-runtime dependencies, and metric/event compaction need scale or failure evidence. Live-provider calibration, central signing/reconciliation, and shared-working-tree attribution remain explicit future work.
