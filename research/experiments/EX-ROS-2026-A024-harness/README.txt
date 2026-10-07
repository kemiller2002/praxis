# EX-ROS-2026-A024 harness

Execution support for
[EX-ROS-2026-A024](../EX-ROS-2026-A024--context-continuity-and-externalized-handoff.md).
Everything that decides an outcome (prompts, schema, telemetry extractor,
criteria, rubric, blinding, analysis) is hashed in `manifest.json` before any
arm runs; `python3 make_manifest.py --check` detects later drift.

| File | Role |
| --- | --- |
| `handoff.schema.json` | Preregistered arm-C handoff schema (unchanged). |
| `harness-note.txt` | Instrumentation note appended verbatim to every implementation prompt. |
| `session_telemetry.py` | Versioned copy of the A021 transcript extractor plus subagents, executor configuration, isolation audit, handoff counters; prints the metrics (gzip+base64 with SHA-256) for the orchestrator to read from the platform event record; never commits or pushes, so nothing reaches the arm branch. |
| `decode_telemetry.py` | Decodes and digest-verifies the printed telemetry. |
| `validate_handoff.py` | Dependency-free validator for exactly the schema keywords used, plus repository facts. |
| `build_start.sh`, `verify_start.sh` | Reproducible sanitized start commit (parent = frozen baseline) and identical-start verification for the arm branches. |
| `make_prompts.py`, `prompts/` | One template; arms differ only in the context-strategy block. |
| `acceptance-criteria.json` | 36 atomic criteria decomposed from the five items' frozen acceptance text, with item hashes. |
| `rubric.txt` | The preregistered five-dimension 0-2 architecture rubric. |
| `run_checks.sh` | Deterministic build, full F# suite and `./ros validate` at a commit. |
| `prepare_blind.py` | Seeded neutral labels, scrubbed packages on orphan branches, mapping commitment. |
| `analysis.py` | Frozen recovery formulas, thresholds and support rule. |
| `make_manifest.py`, `manifest.json` | Frozen manifest. |
| `fixtures/` | Validator and analysis self-test inputs (synthetic; not results). |

Only `handoff.schema.json`, `harness-note.txt`, `session_telemetry.py` and
`validate_handoff.py` enter the arm start tree (`build_start.sh`); prompts,
criteria, rubric and analysis stay outside the arms so arm agents cannot see
the experimental design.
