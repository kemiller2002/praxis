# Tutela Security Metrics

Praxis MUST support security engineering telemetry without collapsing it into a single security score.

Track over time:
- invariant state counts and transitions;
- unknown security effects created/resolved;
- evidence age and stale evidence;
- exceptions created/expired and time-to-expiry;
- recurring/reopened findings;
- security-sensitive file churn and repeatedly modified hotspots;
- boundary/capability changes;
- remediation lead time;
- independent-verification coverage.

Metrics MUST retain repository/ref/time provenance. Metrics are decision support, not certification. Missing telemetry MUST be represented as missing/unknown rather than zero.

## Implementation status

| ID | Requirement | Status | Evidence |
|---|---|---|---|
| TUT-1 | Support security telemetry without a single score | Met | `praxis tutela ingest` and `praxis tutela metrics` (PRAXIS-FND-06); `TutelaMetricsTests` |
| TUT-2 | Track the listed metrics over time | Met | `src/Praxis.Domain/Telemetry/TutelaHistory.fs`; see [`docs/tutela-security-metrics.md`](../docs/tutela-security-metrics.md) |
| TUT-3 | Repository/ref/time provenance | Met | Observation `repository`, `ref`, `commit`, `collectedAt`, `sourceSha256` |
| TUT-4 | Decision support, not certification | Met | Disclaimer in every output; no score field |
| TUT-5 | Missing telemetry is unknown, not zero | Met | `MetricValue.Unknown` with a reason; tested |
