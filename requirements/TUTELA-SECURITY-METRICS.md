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
