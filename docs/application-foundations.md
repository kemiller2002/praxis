# Echelon application-foundation verification

Praxis owns the executable cross-capability verification contract for Echelon
applications.

An application declares applicability in `.echelon/foundations.json`. The
declaration is intentionally separate from implementation discovery: Praxis
must not infer that a capability is optional merely because an implementation
is currently missing.

Run:

```bash
./praxis foundations verify
./praxis foundations verify --json
```

Exit code `0` means every required capability passed. Exit code `3` means
the declaration is valid but one or more required foundations failed. Exit code
`2` means the verifier could not evaluate the repository, for example because
the declaration is missing or invalid.

For every required capability the verifier independently checks:

1. **installed/declared** - the canonical dependency or lifecycle component is
   present;
2. **pinned** - no floating application baseline is accepted;
3. **used** - source evidence shows the shared capability is actually consumed,
   so merely adding a package does not satisfy the contract;
4. **evidence/configuration** - required repository evidence exists, such as the
   Aegis boundary declaration or Limen configuration.

Stable finding codes use `ECHELON-FND-<CAPABILITY>-NNN`:

- `001`: required capability is absent;
- `002`: present but not pinned to the declared immutable baseline;
- `003`: installed/pinned but canonical usage is not present;
- `004`: implementation exists but required evidence/configuration is absent.

A capability marked `required: false` is reported as `N/A`, not PASS. That
preserves the difference between "not applicable" and "implemented correctly."

## Ownership

- **Praxis** owns verification semantics, finding codes, and the executable
  command.
- **Conditor** writes/updates the declaration during project initialization and
  invokes Praxis verification after installation.
- **Applications** own applicability and application-specific evidence.
- **Aegis, Forma, Folio, Limen, and Ordo** remain authoritative for their own
  implementation contracts. Praxis verifies consumption; it does not duplicate
  their behavior.

The JSON schema is `schemas/echelon-foundations-v1.schema.json`, and a starting
manifest is `templates/application-foundations.json`.

## Praxis's own foundations

Praxis is itself an Echelon application and is bound by
[`requirements/SHARED-APPLICATION-FOUNDATIONS.md`](../requirements/SHARED-APPLICATION-FOUNDATIONS.md).

### Aegis (SAF-AEGIS-1..6)

- One boundary module, `src/Praxis.Infrastructure/Boundary/AegisBoundary.fs`,
  configures Aegis (released `EchelonFoundry.Aegis.Core` 1.0.0, blocking
  delivery, standard-error sink) and classifies every escaped failure.
- The command line runs each command inside one capture. A failure is
  attributed to the boundary that raised it: thrown inside
  `Praxis.Infrastructure.Git` is `PRAXIS.GIT.FAILURE`; network, process,
  filesystem and repository-state failures are classified by type; anything
  else is `PRAXIS.CLI.UNEXPECTED`.
- `praxis web serve` and `praxis hub serve` capture each request and each
  connection (`PRAXIS.WEB.FAILURE` when nothing more specific applies) and
  answer HTTP 500 with the safe message and an `AG-` reference. The
  exception text never reaches the browser.
- Expected outcomes (refused transitions, failed verification, policy
  refusals) stay typed results with documented exit codes (SAF-AEGIS-2).
  Programming defects and cancellation are re-raised, not disguised.
- Redaction (SAF-AEGIS-5): Aegis's credential rules plus
  `praxis-repository-data` and `praxis-work-content` key rules; credentials
  in exception text (URL user-info, GitHub tokens, bearer/basic headers,
  `token=`/`password=`/`secret=` values) are replaced before the fault is
  recorded; only command words are public context and the repository root is
  masked.
- [`aegis-boundaries.json`](../aegis-boundaries.json) declares the codes;
  `tests/Praxis.Tests/AegisBoundaryTests.fs` drives the boundary with a
  replaceable collector sink and asserts the manifest equals the module
  (SAF-AEGIS-6).

### Requirement status

| Requirement | Status | Evidence or open item |
|---|---|---|
| SAF-AEGIS-1 | Met | Boundary module; CLI, web and hub captures; `AegisBoundaryTests` |
| SAF-AEGIS-2 | Met | Typed exit codes unchanged; "a completed command keeps its exit code and records nothing" |
| SAF-AEGIS-3 | Met | Shell launchers return non-zero diagnostics (no JavaScript edges, RQ-ROS-2026-A024) |
| SAF-AEGIS-4 | Met | Idempotent remote and reconcile paths (RemoteProtocolTests, WorkReconciliationTests) |
| SAF-AEGIS-5 | Met | Redaction rules and scrubber; "credentials, repository data and work content never reach a sink" |
| SAF-AEGIS-6 | Met | Released 1.0.0 pin; collector-sink tests |
| SAF-FORMA-1 | Met | `vendor/forma/forma.lock` pins the Forma 0.4.1 release tarball by URL and sha256; "forma: the vendored tarball is the pinned immutable release artifact" |
| SAF-FORMA-2 | Met | Web and hub markup use Forma patterns; shipped `styles.css` files carry no rules (guarded by test) |
| SAF-FORMA-3 | Met | No Forma CSS is copied; the stylesheet is extracted at runtime from the verified release tarball |
| SAF-FORMA-4 | Met | Native forms, tables, landmarks; Forma only presents |
| SAF-FORMA-5 | Met | Forma skip link, focus, responsive data grids; status lozenges carry the status word, not color alone |
| SAF-FORMA-6 | Met | Faults use Forma's inline fault and fault-banner patterns with the Aegis reference |
| PRX-UI-030 | Met | As SAF-FORMA-1..6 |
| PRX-UI-031 | Not applicable (decision) | Script-free, server-rendered UI has no browser runtime for Limen; recorded in `docs/web-interface.md` and `.echelon/foundations.json` |
| SAF-FOLIO-1..3 | Not applicable | No printable or PDF surface exists (conditional requirement) |
| SAF-DEP-1, SAF-DEP-2 | Open | PRAXIS-FND-03 |

### Forma (SAF-FORMA-1..6)

`praxis web serve` and `praxis hub serve` consume the pinned Forma release
in `vendor/forma/` (see [`web-interface.md`](web-interface.md)). Praxis has no
npm, so the verifier accepts that lock as Forma's pin when the tarball's
sha256 matches it.
