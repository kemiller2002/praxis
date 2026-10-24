# Requirements preservation

Implements `RQ-ROS-2026-A034` (intake `REQ-PRES-001`, defect DC-008); the contracts are recorded in `DF-ROS-2026-A075` (draft).
`praxis requirements preservation verify` is a deterministic gate that checks a
derived requirements package against the authoritative requirements it was
derived from. It refuses a derivation that silently drops, weakens or falsely
equates a mandatory source obligation. It uses no LLM, network or clock: the
same inputs always produce byte-identical evidence.

## Why

Causa Rehearsal 3 derived its requirements from kemiller2002/indy-init. The
mandatory P0 requirement "UI-016: Causa semantic color system" disappeared in
that derivation, and nothing detected it. UI-016 demanded:

- an exact palette (`#14283F`, `#087F8C`, `#E9A23B`, `#D66B58`, `#F5F3ED`)
  through Forma tokens;
- WCAG AA contrast;
- color never the only indicator;
- grayscale-legible print;
- screenshots from the deployed exact SHA at desktop, iPad portrait/landscape
  and mobile sizes;
- independent visual review.

The package's closest criterion, RQ-CAUSA-2026-0008 UI-03 ("Status is never
conveyed by colour alone"), is a generic accessibility criterion. It carries
none of those obligations. The regression fixture `tests/fixtures/
requirements-preservation/r3` reproduces that package byte for byte, and the
gate rejects it (scenario L below).

## Inputs

The gate reads three kinds of document and two directories. Every document is
JSON and names its versioned schema in `schema`. Schemas live in `schemas/`.

| Input | Schema | Owner |
|---|---|---|
| `--baseline FILE` | `praxis.requirements-baseline/1` (`schemas/praxis-requirements-baseline.schema.json`) | Praxis intake: the inventory of the authoritative source |
| `--disposition FILE` | `praxis.requirements-disposition/1` (`schemas/praxis-requirements-disposition.schema.json`) | The derivation: what happened to each source requirement |
| `--approvals FILE` (optional) | `praxis.requirements-approvals/1` (`schemas/praxis-requirements-approvals.schema.json`) | The approving authority, never the deriver and never Praxis |
| `--derived DIR` | Praxis RQ markdown (front matter `id`, criteria tables) | The derived package |
| `--source DIR` | The source documents at the paths the baseline records | The authoritative repository, checked out |

### Baseline

```json
{
  "schema": "praxis.requirements-baseline/1",
  "id": "BL-CAUSA-UI-2026-10-24",
  "source": {
    "repository": "kemiller2002/indy-init",
    "commit": "fc5df1c9fb268db61ee96e59523826d72f824492",
    "documents": [
      {
        "path": "docs/CAUSA-UI-EXPERIENCE-REQUIREMENTS.md",
        "sha256": "6c590e5f2de9e9889df998ba90ed8eb13d3a805e5da77823171b0a659f3c13b7",
        "requirementPattern": "^## (UI-\\d{3}):"
      }
    ]
  },
  "requirements": [
    {
      "id": "UI-016",
      "title": "Causa semantic color system",
      "priority": "P0",
      "document": "docs/CAUSA-UI-EXPERIENCE-REQUIREMENTS.md",
      "heading": "## UI-016: Causa semantic color system [P0]",
      "sectionSha256": "53e5178f38722dcbcbabb33bf6fbbacba8e91e4db0330bbb9aaf80d4600f5685",
      "obligations": [
        { "id": "UI-016-O4", "kind": "value",
          "text": "Ensure WCAG AA contrast for normal text (4.5:1), large text (3:1), and relevant non-text indicators (3:1).",
          "tokens": ["WCAG AA", "4.5:1", "3:1"] },
        { "id": "UI-016-O7", "kind": "acceptance",
          "text": "Acceptance MUST include actual screenshots from the deployed exact SHA at desktop, iPad portrait/landscape and mobile sizes",
          "tokens": ["screenshots", "deployed", "exact SHA", "desktop", "iPad portrait", "landscape", "mobile"] }
      ]
    }
  ],
  "conflicts": []
}
```

- **Requirement.** Each inventoried requirement has these fields:
  - a stable `id`, a `title` and a `priority`;
  - the verbatim `heading` line that opens its section;
  - `sectionSha256`, the hash of that section;
  - one or more mandatory **obligations**.
- **Section.** A section runs from the heading line to the line before the
  next heading of the same or a higher level. Trailing blank lines are
  removed.
- **Hashes.** Every hash is lowercase hex SHA-256 of the UTF-8 text, with
  CRLF and CR normalized to LF. Use `praxis requirements preservation
  sections` to compute them; never write them by hand.
- **Obligation.** Each obligation has an `id`, a `kind`, verbatim `text` and
  optional exact `tokens`.
- **Kinds.** The `kind` is one of:
  - `value`: exact tokens such as hex values or thresholds. `tokens` is
    required.
  - `acceptance`: a verification obligation. It must map to a derived
    acceptance criterion.
  - `constraint`: any other mandatory constraint.
- **Tokens.** Tokens are matched case-insensitively and literally. Each token
  must occur in the obligation's text, so the baseline cannot demand more or
  less than the source says.
- **Mandatory.** Every inventoried requirement and obligation is mandatory.
  To drop one, use an approved disposition. Omitting it from the derivation
  is never enough.

### Disposition

```json
{
  "schema": "praxis.requirements-disposition/1",
  "baseline": { "id": "BL-CAUSA-UI-2026-10-24", "commit": "fc5df1c9fb268db61ee96e59523826d72f824492" },
  "package": "causa-rehearsal-004",
  "dispositions": [
    {
      "requirement": "UI-016",
      "disposition": "preserved",
      "mappings": [
        { "obligation": "UI-016-O4", "criteria": ["RQ-CAUSA-2026-0010#COL-03"] },
        { "obligation": "UI-016-O7", "criteria": ["RQ-CAUSA-2026-0010#COL-06", "RQ-CAUSA-2026-0010#COL-07"] }
      ]
    },
    { "requirement": "UI-004", "disposition": "excluded-with-approval",
      "approval": "APR-2026-001", "rationale": "Read-only rehearsal." }
  ],
  "conflicts": []
}
```

Each source requirement gets exactly one disposition:

| Disposition | Requires | Passes when |
|---|---|---|
| `preserved` | `mappings` | every obligation is covered (see checks) |
| `strengthened` | `mappings`, `rationale` | as `preserved`; the rationale says what was strengthened |
| `excluded-with-approval` | `approval` (a decision id), `rationale` | the approval record is valid and covers the requirement |
| `superseded` | `replacement` (criterion references), `approval` | the replacements exist in the derived package and the approval covers the requirement |
| `not-applicable` | `justification`, optional `approval` | a justification is given and an approval covers it; without an approval the requirement is `needs-review` |

- **Mapping targets.** A mapping names derived criteria as
  `REQUIREMENT-ID#CRITERION-ID`. One obligation may map to several criteria.
- **Partial exclusion.** Inside a `preserved` or `strengthened` disposition,
  one obligation may instead carry an `exclusion: { approval, rationale }`.
  Use this for a read-only scope that drops the write part of a mixed
  requirement. The approval must cover that obligation id or its requirement
  id.
- **Equivalence review.** An obligation without tokens can only be accepted
  by a `review: { reviewer, date, basis }` recorded by a person who judged
  the equivalence. A review never overrides missing tokens.

### Approval records

```json
{
  "schema": "praxis.requirements-approvals/1",
  "approvals": [
    {
      "decisionId": "APR-2026-001",
      "authority": "operator",
      "scope": "Read-only rehearsal: exclude write workflows; UI, security and accessibility stay mandatory.",
      "covers": ["UI-004", "UI-006-O1", "UI-006-O2"],
      "date": "2026-10-24",
      "ordoDecision": null
    }
  ]
}
```

- **Fields.** An approval is valid only when it has all of these:
  - a non-empty `authority`;
  - a non-empty `scope`;
  - an ISO `yyyy-MM-dd` date;
  - a `covers` entry naming the requirement, obligation or conflict id it
    decides.
- **Separate file.** Approvals live in their own file, so a disposition
  cannot approve itself.
- **Praxis never writes them.** Praxis never generates, writes or infers an
  approval or a review. A test checks that the gate writes nothing under its
  inputs.
- **Not authentication.** Who wrote an approval file is outside what the gate
  can prove; see Limits.

### Derived package

- **Files.** The gate reads every `*.md` file under `--derived`,
  recursively, in ordinal path order. It skips `README.md`.
- **Requirement id.** The id is the front-matter `id:`, or else the file name
  before `--`.
- **Criteria.** Each markdown table whose first header cell is `ID` holds
  criteria. A row's first cell is the criterion id and the remaining cells
  are its text.
- **Acceptance criteria.** When the second header cell contains
  "acceptance", the table holds acceptance criteria; otherwise its criteria
  are statements.
- **Format.** This is the Praxis RQ format that Causa Rehearsal 3 used
  (Praxis 3.16.0). The gate parses its eight original files as 56 criteria.

## Checks

The checks are deterministic. Each finding has one status:

- `fail`: a deterministic violation.
- `drift`: the source changed against the pinned baseline.
- `blocked`: pending a decision.
- `needs-review`: only semantic judgement can decide.
- `info`: accepted, and reported so it stays visible.

| Code | Status | When |
|---|---|---|
| `disposition-missing` | fail | an inventoried mandatory requirement has no disposition |
| `obligation-unmapped` | fail | a `preserved`/`strengthened` disposition omits an obligation, or maps it to nothing |
| `value-token-missing` | fail | a `value` obligation's exact tokens do not all appear in its mapped criteria (weakening or false equivalence) |
| `token-missing` | fail | a non-value obligation's declared tokens do not all appear |
| `acceptance-criterion-missing` | fail | an `acceptance` obligation maps only to non-acceptance criteria (its tokens are then matched against acceptance criteria only) |
| `criterion-not-found` | fail | a mapping or replacement names a criterion the derived package does not contain |
| `approval-missing`, `approval-invalid` | fail | an exclusion, supersession, not-applicable or conflict decision names no valid, covering approval |
| `not-applicable-unjustified` | fail | `not-applicable` without a justification |
| `source-document-missing`, `source-document-changed`, `source-section-missing`, `source-section-changed` | drift | the source text no longer hashes to the pinned baseline |
| `uninventoried-requirement` | drift | the document's `requirementPattern` finds a requirement id the inventory lacks |
| `conflict-unresolved` | blocked | a declared conflict (in the baseline or disposition) has no deciding approval |
| `equivalence-unverified`, `not-applicable-unapproved` | needs-review | only semantic judgement can decide; acceptance is blocked until a review or approval is recorded |
| `baseline-invalid`, `baseline-mismatch`, `disposition-invalid`, `disposition-duplicate`, `disposition-unknown-requirement`, `obligation-unknown`, `obligation-duplicate`, `derived-criterion-duplicate`, `conflict-invalid` | fail | the documents are inconsistent: an obligation text that is not verbatim in its section, a token not in its text, a disposition for another baseline, unknown or repeated ids |
| `exclusion-approved`, `supersession-approved`, `not-applicable-approved`, `equivalence-reviewed`, `conflict-resolved` | info | accepted, and listed in the report and the human summary |

The **verdict** follows from the findings:

- `fail` when any finding is `fail` or `drift`;
- `blocked` when none is, but any is `blocked` or `needs-review`;
- `pass` otherwise.

The gate never silently accepts:

- an obligation the tokens cannot decide stays `needs-review` until a person
  records a review;
- an approved exclusion is always listed.

## Output and exit codes

```
praxis requirements preservation verify --baseline FILE --disposition FILE --derived DIR --source DIR [--approvals FILE] [--output FILE] [--json]
praxis requirements preservation sections --source DIR --document PATH [--pattern REGEX] [--json]
```

- **JSON.** `--json` prints a `praxis.requirements-preservation/1` document
  (`schemas/praxis-requirements-preservation.schema.json`) to stdout. It
  records:
  - the verdict;
  - the path and SHA-256 of every input: the baseline, disposition,
    approvals and each derived file;
  - the pinned and observed hash of each source document;
  - per-requirement and per-obligation outcomes: the mapped criteria, each
    token found or missing, and `decidedBy` (`tokens`, `review`, `exclusion`
    or `undecided`);
  - every exclusion, with its full approval record;
  - every conflict;
  - every finding;
  - summary counts.
- **Human summary.** Without `--json`, the gate prints a summary instead:
  - the verdict;
  - one line per requirement;
  - an `EXCLUDED` line per exclusion;
  - every finding.
- **Evidence file.** `--output FILE` also writes the JSON document to a file.
  This is the evidence file a gate attaches.

| Exit | Meaning |
|---|---|
| `0` | `pass` |
| `3` | `fail` or `blocked` (read `verdict` for which) |
| `2` | invalid arguments, or an input that cannot be read or does not conform to its schema; no verdict |
| `1` | internal failure, including an unwritable `--output` |

These codes follow the CLI exit-code contract in [`cli.md`](cli.md): `3`
means verification failed. `blocked` deliberately shares `3`, because a gate
must not proceed in either case.

`sections` lists every heading section of one source document, with its hash
and the id the pattern captures (`praxis.requirements-sections/1` with
`--json`). Use it to pin a baseline.

## Ownership boundary

| System | Owns | In this gate |
|---|---|---|
| **Praxis** | intake, provenance, derivation and disposition records | Implements the gate, the baseline/disposition/approval contracts and the evidence document. It decides nothing that needs authority. |
| **Ordo** | decision authority | Owns approvals. Today an approval record is a file supplied by the approving authority. `ordoDecision` is reserved for the Ordo decision that will back it, for example a resolution ingested with `praxis ordo ingest` (whose `ResolutionId` would be the reference). Praxis records that reference but does not verify it; this change adds no Ordo coupling. |
| **Dokimos** | verification gates | A Dokimos gate runs the command and branches on the exit code (see below). Praxis does not recompute a Dokimos verdict, and Dokimos does not reinterpret findings. |
| **Percepta** | semantic UI verification | Percepta checks that a built interface satisfies an obligation, for example that UI-016's palette appears in populated states on deployed screenshots. This gate proves only that the obligation survived derivation. Passing it is never evidence that the UI meets UI-016. |

A Dokimos gate would invoke the command as a required check:

```bash
praxis requirements preservation verify \
  --baseline requirements/baseline.json \
  --disposition requirements/disposition.json \
  --approvals requirements/approvals.json \
  --derived research/requirements \
  --source "$AUTHORITATIVE_CHECKOUT" \
  --output evidence/requirements-preservation.json --json
# exit 0 -> pass; exit 3 -> fail or blocked (attach the evidence, do not proceed);
# exit 2/1 -> the gate could not run: unavailable is never a pass.
```

## Rehearsal initialisation

A future rehearsal begins implementation only after the gate passes.

1. **Resolve the authoritative commit.** Record the source repository and the
   exact commit SHA, and check the source out at that commit (`--source`).
2. **Inventory.** Run `praxis requirements preservation sections` for each
   source document. Write the baseline, with:
   - every requirement in scope;
   - its verbatim obligations;
   - tokens for every value, threshold or named artifact.

   Set `requirementPattern` so that requirements added later are caught.
3. **Scope.** State the rehearsal's scope, for example read-only. Ask the
   approving authority for approval records covering every exclusion,
   supersession or not-applicable disposition the scope implies. Do not
   write them yourself.
4. **Derive.** Write the derived package in the canonical requirements
   location.
5. **Reconcile.** Write the disposition file: one disposition per source
   requirement, and a mapping per obligation. Declare any conflict found
   between mandatory requirements.
6. **Run the gate.** Resolve every `fail`. Resolve `drift` by re-pinning
   against the new commit and re-deriving. Get a decision for every
   `blocked`, and a person's review for every `needs-review`.
7. **Record the baseline.** Commit the baseline, disposition, approvals and
   `--output` evidence with the work item, and cite the evidence on
   completion.
8. **Begin implementation only after PASS.** Re-run the gate whenever the
   source, the derived package or the approvals change.

## Limits

- **Literal matching.** The gate decides literal presence, not meaning.
  - A token that appears in a criterion that negates or contradicts it
    ("never use `#14283F`") still matches.
  - A faithful paraphrase without the token ("colour alone" for "color
    alone") fails.
  - Choose tokens that are exact, such as values, thresholds and named
    artifacts, and leave judgement to reviews.
- **Obligations without tokens.** These are undecidable by construction. They
  stay `needs-review` until a person records an equivalence review, and the
  gate checks only that the review is complete, not that it is right.
- **Inventory completeness.** The gate checks that the inventory is verbatim,
  hash-pinned and complete at the level of requirement ids
  (`requirementPattern`). It cannot know whether every mandatory sentence of
  a section was inventoried as an obligation. That remains an intake
  responsibility, and it is reviewable in the baseline.
- **Commit SHA.** The commit is recorded provenance; the gate does not run
  Git. Drift is detected from the text under `--source`, so a checkout at
  the wrong commit with identical text is indistinguishable.
- **Approvals and reviews.** These are self-reported files. The gate verifies
  their completeness and scope, not the identity or authority of whoever
  wrote them. Backing them with Ordo decisions is the intended remedy.
- **Derived criteria.** Only criteria in markdown tables with an `ID` header
  are recognized; prose or list criteria are invisible to the gate.
- **Conflicts.** Conflicts are only those declared; the gate does not
  discover conflicts between requirements.
- **Schemas not scaffolded.** The four schemas live in this repository's
  `schemas/`. They are not yet installed into projects by `praxis init` or
  `praxis upgrade`, which would change the starter manifests. The CLI
  enforces the contracts itself, so a consumer does not need the files to
  run the gate.
