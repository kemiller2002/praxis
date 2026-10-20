# Constructibility contract consumer fixtures (compatibility oracle)

Producer-side golden corpus for `ConstructibilityContract.admitContract`
(REL-ORDO-001: ORD-REL-003, ORD-REL-005, ORD-REL-006). Consumers that cannot
link Ordo (Praxis work admission, Dokimos authority) copy this directory and
must reproduce `expectations.json` exactly. `Ordo.Tests.ConstructibilityAdmissionTests`
proves Ordo itself still does.

- Contract = an `ecir/1` blueprint. Supported versions: exactly `ecir/1`; no
  automatic migrations. Any other `schemaVersion` is refused with
  `ORDO.CONTRACT.UNSUPPORTED_SCHEMA` before other fields are read.
- Decoding is strict: unknown members, duplicate members, blank names and
  malformed JSON, and escaped lone surrogates that cannot be decoded to text
  are `ORDO.CONTRACT.INVALID_BLUEPRINT_JSON`.
- Digest = `sha256:` + lowercase hex SHA-256 of the UTF-8 bytes of the
  canonical rendering (`valid-contract.canonical.json`): requirements sorted by
  `source.key`, nodes by `id`, `nodeIds`/`requirementKeys`/`dependsOn` sorted,
  every object's members sorted by ordinal key, two-space indentation, `\n`
  newlines, `": "` separators, no trailing newline, only `"` `\\` and control
  characters escaped (`\b \f \n \r \t`, otherwise `\u00xx` lowercase), all
  other characters written literally. Ordering is ordinal (UTF-16 code unit).
- Admission applies every manifest-independent rule of `ConstructionIr.validate`
  (dangling/non-reciprocal references, duplicate IDs, dependency cycles,
  missing disposition evidence). It is not intake equality against the source
  manifest and never grants execution authority.
- Admission also refuses a `modeled` requirement that links a `conflict` node
  (`ORDO.CONTRACT.UNRESOLVED_CONFLICT`, subject `requirementKey->conflictId`):
  ecir/1 carries no conflict resolution, so the conflict is open
  (`contradictory-decisions-contract.json`). An `unresolved` requirement with
  its conflict is well formed and admitted (`unresolved-requirement-contract.json`).
- Stale references: with an expected digest, a mismatch is
  `ORDO.CONTRACT.STALE_REFERENCE` (subject `expected->actual`); a malformed pin
  is `ORDO.CONTRACT.INVALID_REFERENCE_DIGEST`.
- Compare outcome, identity, and the ordered `(code, subject)` diagnostic list.
  Messages are informative only.

Readiness (`readinessCases`, `ConstructibilityContract.assessPinnedReadiness`):
admission, then execution readiness of the work completing `requirementKeys`
under the host-authorized `authorizedDecisionIds`. Outcome `ready` carries the
sorted scope (`ordo.constructibility-readiness/1`); `refused` carries the sorted
`(code, subject)` list: `BLOCKED_REQUIREMENT`, `UNRESOLVED_CONFLICT`,
`MISSING_DECISION_AUTHORIZATION`, `MISSING_RATIONALE_AUTHORIZATION`,
`UNKNOWN_AUTHORIZATION`, `AUTHORIZATION_KIND_MISMATCH`,
`AUTHORIZATION_OUTSIDE_SCOPE`, `UNKNOWN_SCOPE_REQUIREMENT`, `EMPTY_SCOPE`, or
any admission code. The authorized set must come from the consumer's own
authority, never from the agent whose work is judged.

CLI: `sde ecir check --blueprint FILE [--expected-digest sha256:HEX]
[--requirement KEY]... [--authorized-decision ID]... [--json]` (exit 0 admitted
or ready, 1 refused, 2 usage; any `--requirement` selects readiness).
