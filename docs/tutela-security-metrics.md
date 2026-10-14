# Tutela security metrics

Praxis records Tutela security assessments over time and derives security
engineering metrics from them
([`requirements/TUTELA-SECURITY-METRICS.md`](../requirements/TUTELA-SECURITY-METRICS.md)).
The metrics are decision support, not certification, and there is no single
security score.

## Commands

```
./praxis tutela ingest --input assessment.json [--collected-at 2026-10-06T12:00:00.000Z]
./praxis tutela metrics [--repository example/app] [--json]
```

- **Ingest** reads a Tutela security assessment (`schemaVersion` 1) and
  appends one observation to `.ros/telemetry/tutela/observations.jsonl`. It
  keeps identifiers, states and times only, never evidence content or
  rationale. Provenance (TUT-3): `repository` and `ref` from the assessment's
  subject, `commit` when the ref resolves in this repository, `collectedAt`
  (`--collected-at` or the current time) and the source document's sha256.
  The same document ingested twice is recorded once.
- **Metrics** are derived per observation, each compared with the earlier
  observations of the same repository. The JSON schema is
  `praxis.tutela-metrics/1`; every metric has `id`, `dimensions`, `unit`,
  `status` (`measured` or `unknown`) and either a `value` or a `reason`.

## Metrics (TUT-2)

| Requirement | Metric |
|---|---|
| Invariant state counts and transitions | `security.invariants{state}`, `security.invariant_transitions{from,to}` |
| Unknown security effects created/resolved | `security.unknown_effects{state=open|created|resolved}` |
| Evidence age and stale evidence | `security.evidence_age{statistic=max|mean}` (days), `security.evidence{state=stale}` |
| Exceptions created/expired and time-to-expiry | `security.exceptions{state=active|expired|created}`, `security.exception_time_to_expiry{statistic=min}` (days) |
| Recurring/reopened findings | `security.findings{state=open|recurring|reopened}` |
| Security-sensitive churn and hotspots | `security.sensitive_churn`, `security.hotspots` (paths changed in more than one interval) |
| Boundary/capability changes | `security.boundary_changes{source=trust-root|execution-scope-expansion}` |
| Remediation lead time | `security.remediated`, `security.remediation_lead_time{statistic=mean|max}` (days from first seen open to closed; findings, unknown effects and violated invariants) |
| Independent-verification coverage | `security.independent_verification` (ratio) |

Security-sensitive paths are configured as globs in `ros.json`:

```json
{ "tutela": { "sensitivePaths": ["src/auth/**", "src/**/Security/**"] } }
```

Churn is the set of those paths changed between the commits of consecutive
observations. Execution scope expansions are read from Praxis's own execution
ledger (`.ros/executions/*/events.jsonl`, `scope-expanded`).

## Missing is unknown (TUT-5)

A metric that cannot be derived is `unknown` with a reason, never `0`: the
first observation of a repository has no created/resolved/transition counts;
churn is unknown without configured sensitive paths or without both commits;
evidence age is unknown when the assessment records no observation times.
