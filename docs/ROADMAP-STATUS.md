# ROS Roadmap Status

Updated: 2026-08-16

This record tracks `prompts/roadmap.md`. Daemon/model-routing work is explicitly out of scope and is being handled in another repository.

| Phase | Status | Evidence |
|---|---|---|
| 1 — Work protocol | Complete | `docs/work-protocol.md`, protocol tests, CI attribution enforcement |
| 2 — Automatic agent compliance | Complete | canonical agent/bootstrap instructions, enriched `work context`, `status`, structured validation and repair guidance |
| 3 — Reusable distribution | Complete | self-contained bootstrap, pinned installation manifest, default config/schema, consuming-repository CI, documented upgrade procedure, clean-repository integration test |
| 4 — External work-system contract | Complete | normalized request/result schemas, authority and security boundary, idempotent file conformance adapter, contract tests for success/failure/unknown outcomes |
| 5 onward | External/deferred | Project-management datastore and its UI belong in a separate repository after Phase 4 stabilizes |

## Current exit target

Phase 3 exits when a clean repository can be initialized, receives the default workflow and versioned configuration, validates without the ROS source checkout, and has an explicit upgrade path. The existing bootstrap integration test is the executable acceptance test.

## Next ROS-owned phase

ROS-owned roadmap work now stops at the stabilized Phase 4 boundary. Phase 5 creates the project-management datastore in its separate repository. Future ROS work should respond to concrete adapter-consumer feedback, add production transport adapters only when authorized, and keep daemon/model routing out of this repository.
