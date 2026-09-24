# Echelon application-foundation verification

Praxis owns the executable cross-capability verification contract for Echelon
applications.

An application declares applicability in `.echelon/foundations.json`. The
declaration is intentionally separate from implementation discovery: Praxis
must not infer that a capability is optional merely because an implementation
is currently missing.

Run:

```bash
./ros foundations verify
./ros foundations verify --json
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
