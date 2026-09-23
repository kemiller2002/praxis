# Code change metrics and change health

Praxis records deterministic code-change metrics for every finalized telemetry execution that began from a clean Git baseline. The purpose is not to reward small diffs or punish large ones. It is to preserve evidence about how a repository changes, surface unusually risky updates, and identify files or regions that repeatedly absorb change over time.

## Principles

- Metrics are derived from Git and local file metadata, not agent self-report.
- A dirty execution baseline makes attribution unavailable rather than guessed.
- Threshold crossings are findings by default, not automatic blockers.
- Longitudinal history stores metadata only: paths, statuses, line ranges, and line buckets. Praxis never stores source text or snippets in change history.
- Generated Praxis telemetry is excluded from its own attribution metrics, so recording history cannot inflate the next update's measurements.
- Thresholds are repository policy and can be tuned without changing Praxis code.

## Policy

The default policy is `telemetry/change-health.json`. `ros.json` points to it through `telemetry.changeHealthPolicy`.

Default settings:

| Measure | Warning | Error |
| --- | ---: | ---: |
| Files changed | 25 | 60 |
| Lines changed (added + deleted) | 800 | 2,000 |
| Largest single-file churn | 300 | 800 |
| Largest changed file current size | 800 | 1,500 |
| Hunks changed | 30 | 80 |
| Maximum hunks in one file | 10 | 25 |
| Same file touched in recent updates | 5 | 10 |
| Same line region touched in recent updates | 3 | 6 |

The longitudinal window defaults to the last 20 finalized updates. History retains at most 200 updates. Line regions use 25-line buckets so repeated nearby edits remain visible even when line numbers drift slightly over time.

A threshold is crossed only when the measured value is greater than the configured value. Equal-to-threshold values are not flagged. If both warning and error bands are crossed, Praxis emits only the error finding for that metric.

Praxis 3.5.0 treats threshold crossings as findings, not automatic blockers. This is deliberate: a legitimate migration can be large while still needing its size recorded and reviewed. A future explicit CI-gating contract can build on these stable finding codes without changing what the measurements mean.

## Per-update metrics

When attribution is available, Praxis records:

- commits created;
- files added, modified, deleted, and renamed;
- total files changed;
- source/code files changed;
- test files changed;
- documentation files changed;
- binary files changed;
- lines added, deleted, total changed, and net line change;
- file-extension distribution;
- per-file added/deleted/churn counts;
- current line count for each changed text file that still exists;
- largest single-file churn;
- largest current changed-file size;
- zero-context Git hunk count;
- maximum hunks in one file;
- repeated file-touch count within the configured history window;
- repeated line-region touch count within the configured history window;
- warning/error threshold findings.

The execution record stores the current update under `repository.changeHealth`. Normalized scalar measurements are also added to the telemetry metric registry so summaries can aggregate them.

## Hunk and hotspot tracking

Praxis uses `git diff --unified=0` to derive changed line ranges. The Git diff is processed transiently; change history persists only metadata and never stores source text or diff bodies. Each hunk stores only:

- old start/count;
- new start/count;
- one or more numeric line buckets;
- file path and rename origin when applicable.

The bounded longitudinal record lives at `.ros/telemetry/change-history.json`. It is protected by a repository lock and written atomically.

For a current update, Praxis looks at the configured recent history window and reports:

- how many recent updates touched each current file;
- how many recent updates touched each current line bucket;
- the maximum repeated-file and repeated-region touch counts;
- hotspot rankings through `ros telemetry hotspots`.

A rename carries its prior path as an alias for the current update so history can still connect a renamed file to recent touches under its old name.

Line buckets are intentionally approximate. They are a stable metadata heuristic, not a claim that line 417 today is semantically identical to line 417 six months ago. The value is trend detection: repeated activity in the same neighborhood is a signal to inspect architecture, ownership, tests, or decomposition.

## Findings

Each threshold finding carries:

- stable code;
- metric name;
- severity (`warning` or `error`);
- actual value;
- threshold value;
- short explanation;
- remediation guidance.

Default finding codes:

| Code | Metric |
| --- | --- |
| `PRAXIS-CHG-001` | files changed |
| `PRAXIS-CHG-002` | lines changed |
| `PRAXIS-CHG-003` | largest file churn |
| `PRAXIS-CHG-004` | largest changed-file size |
| `PRAXIS-CHG-005` | hunks changed |
| `PRAXIS-CHG-006` | maximum hunks per file |
| `PRAXIS-CHG-007` | repeated file touches |
| `PRAXIS-CHG-008` | repeated line-region touches |

Praxis emits only the highest crossed band for a metric. If both warning and error thresholds are exceeded, the result is one error finding, not duplicate warning and error findings.

## Commands

Current execution/update detail remains available through:

    ros telemetry show [TARGET]

Change-health projection:

    ros telemetry change-health [TARGET]

Longitudinal hotspot projection:

    ros telemetry hotspots

The hotspot view is intended for agents and humans deciding where refactoring, decomposition, stronger tests, or architectural review may be worthwhile. It is evidence, not an automatic conclusion that highly changed code is bad.

## Interpretation

High churn is not inherently a defect. A migration, generated client update, or deliberate consolidation may legitimately exceed several thresholds. The important behavior is that Praxis records the fact consistently and makes repeated concentration visible.

Conversely, a small diff can still be risky. These metrics complement tests, type checks, architecture checks, Ordo evidence, Aegis findings, and human review. They do not replace those systems.
