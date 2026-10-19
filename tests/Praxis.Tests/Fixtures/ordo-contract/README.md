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
- Stale references: with an expected digest, a mismatch is
  `ORDO.CONTRACT.STALE_REFERENCE` (subject `expected->actual`); a malformed pin
  is `ORDO.CONTRACT.INVALID_REFERENCE_DIGEST`.
- Compare outcome, identity, and the ordered `(code, subject)` diagnostic list.
  Messages are informative only.

CLI: `sde ecir check --blueprint FILE [--expected-digest sha256:HEX] [--json]`
(exit 0 admitted, 1 refused, 2 usage).
