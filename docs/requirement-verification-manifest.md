# Requirement IDs and the verification manifest

`praxis requirements manifest` (RQ-ROS-2026-A037) gives every individually
verifiable application requirement a stable ID, and compiles those IDs into a
deterministic **verification manifest**. The manifest is the source of truth
for what an application must prove. It is compiled from the requirements
alone, never from the application, so an application cannot reduce its
obligations by leaving out a field, a component or a test.

It sits beside the [requirements-preservation gate](requirements-preservation.md),
which checks that a derived package keeps every authoritative requirement.
The catalog decomposes those authoritative requirements (a `SCREEN-CONTRACTS`
section, an `R1.1`) into field-level IDs. Each catalog requirement must trace
to at least one of them through `sources`.

## Documents

| Schema | File | Written by |
|---|---|---|
| `praxis.requirement-catalog/1` | [`schemas/praxis-requirement-catalog.schema.json`](../schemas/praxis-requirement-catalog.schema.json) | people, through the requirements governance process |
| `praxis.requirement-implementation-map/1` | `{ "schema", "mappings": [{ "requirement", "location" }] }` | the application (optional) |
| `praxis.verification-manifest/1` | [`schemas/praxis-verification-manifest.schema.json`](../schemas/praxis-verification-manifest.schema.json) | the compiler only |
| `praxis.requirement-catalog-findings/1` | written with `--findings` when a catalog is rejected | the compiler only |

A catalog requirement:

```json
{
  "id": "INV-002",
  "type": "ui-field",
  "screen": "investigation-start",
  "name": "Problem statement",
  "component": "textarea",
  "required": true,
  "visible": true,
  "editable": true,
  "validation": { "minLength": 1, "maxLength": 4000 },
  "verification": ["mapped", "exists", "visible", "accessible", "editable", "enter", "validation", "rejects", "save", "reload"],
  "sources": [{ "document": "docs/SCREEN-CONTRACTS.md", "requirement": "S01" }]
}
```

### Requirement types and checks

Each check belongs to one verification level. Each type allows only the
checks that make sense for it; anything else is `unsupported-verification`.

| Level | Meaning | Checks |
|---|---|---|
| V1 | structural existence | `mapped`, `exists` |
| V2 | rendered existence | `visible`, `accessible` |
| V3 | interaction | `editable`, `enter`, `select`, `activate` |
| V4 | behavioural correctness | `validation`, `rejects`, `outcome` |
| V5 | lifecycle correctness | `save`, `reload`, `update`, `isolation` |

Types: `ui-field`, `ui-control`, `action`, `navigation`, `validation-rule`,
`data-contract`, `state-transition`, `workflow-outcome`, `accessibility`. The
allowed checks per type are in `VerificationCheck.allowedFor`
(`src/Praxis.Domain/Requirements/RequirementCatalog.fs`).

`required` makes a requirement's obligations mandatory; it does not make the
value non-empty. Whether an empty value is valid is a value rule, `minLength`
(at least 1 means non-empty), so a `validation` or `rejects` check needs at
least one of `minLength`, `maxLength`, `pattern` or `options`.

Obligation IDs are `<requirement>/<level>/<check>`, for example
`INV-002/V3/editable`. Every obligation of a `required` requirement is
mandatory.

### Applicability

A requirement applies always, unless it names declared facts in
`appliesWhen`, for example `[{ "fact": "ai-provider-configured", "equals": true }]`.
A verifier may report `NOT_APPLICABLE` only when a declared fact has a
different value. It never infers inapplicability from missing implementation.

### Repeated controls

A requirement with `"instances": "<key meaning>"` may render many elements.
Each element carries the ID plus a distinct `data-requirement-instance`. A
requirement without `instances` must render exactly one element per active
scope; two elements carrying it are ambiguous.

## Stability and retirement

- IDs are authored, never derived from labels. Renaming `name` keeps every
  requirement and obligation ID; the manifest's `contentHash` still changes.
- An ID leaves the catalog only through a `retired` entry stating its reason
  and the governance record that authorised it (`authority`). A retired ID
  cannot be reused.
- `--previous MANIFEST` rejects a catalog that drops an ID the previous
  manifest named without retiring it (`removed-without-retirement`).

## Compile

```bash
praxis requirements manifest compile \
  --catalog requirements/catalog.json \
  --source-commit "$(git rev-parse HEAD)" \
  [--mappings implementation-map.json] [--previous previous-manifest.json] \
  --output verification-manifest.json [--findings findings.json]
```

Exit codes: `0` compiled, `3` the catalog is invalid (nothing is written to
`--output`, and every finding is printed and written to `--findings`), `2`
bad arguments or unreadable input.

The compiler rejects, with one finding code each: `duplicate-id`, `missing-id`,
`invalid-id`, `invalid-reference` (screen, dependency, fact or replacement),
`missing-verification`, `unsupported-verification`,
`unsupported-requirement-type`, `conflicting-requirement`, `orphaned-mapping`,
`retired-id-reused`, `unauthorised-retirement`, `missing-source`,
`invalid-source-commit` and `removed-without-retirement`. A required
requirement without a `mapped` check is a warning (`no-structural-obligation`).

The manifest:

- lists requirements by ordinal ID and obligations in level order;
- records `source.commit` and `source.catalogSha256`, the SHA-256 of the
  catalog with line endings normalised to LF;
- records the compiler version;
- has no timestamp, so the same inputs always give byte-identical bytes;
- ends with `contentHash`.

## Check for a stale manifest

```bash
praxis requirements manifest check --catalog requirements/catalog.json --manifest verification-manifest.json
```

Exit `0` (`CURRENT`) only when the manifest is exactly what the catalog
compiles to today. Exit `3` (`STALE`) names each problem:

- `tampered`: the content no longer matches `contentHash`.
- `catalog-changed`: the catalog bytes changed.
- `content-differs`: recompiling gives different content.
- `catalog-invalid`: the catalog no longer compiles.

## Not claimed

The compiler proves only that the obligations are complete, consistent and
current. Whether the application meets them is decided by a verifier against
the manifest, for example `dokimos verification requirements`. A catalog's
`status` is recorded, never set: compiling a catalog does not ratify it.
