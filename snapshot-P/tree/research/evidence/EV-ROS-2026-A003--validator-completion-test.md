---
id: EV-ROS-2026-A003
title: Validator completion test results
research_area: ros-executable-contract
evidence_type: test-result
status: accepted
source_title: Local unittest and CLI execution
source_author: codex
source_uri: tests/test_ros_cli.py
source_date: 2026-07-24
retrieved: 2026-07-24
created_by_agent: codex
confidence: high
supports: [RP-ROS-2026-A005]
contradicts: []
related_theories: []
tags: [validation, registry, cli]
supersedes: []
superseded_by: []
---

# Evidence Record

## Evidence summary

Six automated tests passed. They exercised a valid artifact set, malformed
front matter, duplicate IDs, a broken evidence reference, deterministic and
stale registry behavior, and nonreciprocal supersession.

## Exact claim supported or contradicted

The required Phase 2 validation slice behaves deterministically for the tested
success and failure paths.

## Source provenance

Local command output from:

```bash
python3 -m unittest discover -s tests -v
./ros registry build
./ros validate
./ros registry build --dry-run
./ros registry check
```

## Relevant excerpt or data

`Ran 6 tests ... OK`; final repository validation passed, dry-run reported no
changes, and registry check reported current registries.

## Interpretation

The completion-test behaviors are implemented. This does not establish full
YAML or full JSON Schema conformance.

## Limitations

One Python runtime and local filesystem were tested. CI is configured but was
not run remotely.

## Counterevidence

None observed in the executed scope.

## Reproduction or verification notes

Run the commands above from the repository root.
