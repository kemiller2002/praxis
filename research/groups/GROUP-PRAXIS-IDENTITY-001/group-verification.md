# Verification pass: GROUP-PRAXIS-IDENTITY-001, first PR (PRAXIS-ID-01, 04, 05, 06)

PRX-GRP-045 pass, one row per acceptance criterion as recorded on each work
item (`./praxis work context ID`). Evidence names a test in
`tests/Praxis.Tests/IdentityTests.fs` (prefix `identity:`) unless stated.
Commands were run on `id/instance-identity` with the full suite run twice:
once plain and once with `GITHUB_ACTIONS=true GITHUB_REPOSITORY_ID=1309152643`
(1500 tests; the one CI-only failure found that way is fixed in e27795be).

Decision recorded here: PRAXIS-ID-01 criterion 5 originally asked `validate`
to warn when no repository identity is configured. Every fresh installation
would then warn (and every golden "validation passed" changed), so a
never-adopted identity is reported by `praxis repository identity` and
`work capture` instead; `validate` warns on legacy and locator-changed
identities and fails on a contradicted one. The item's description was
updated to say so before this pass.

| Member | Criterion | Status | Evidence |
|---|---|---|---|
| PRAXIS-ID-01 | 1. typed RepositoryIdentity / WorkItemIdentity; display derived, never parsed | met | `src/Praxis.Domain/Identity/Repository.fs`; "structured references round-trip without parsing the display string" |
| PRAXIS-ID-01 | 2. same local ID in two repos; same name different owners; rename/transfer keep identity | met | "the same local ID in two repositories...", "two repositories with the same name...", "a rename keeps the identity...", "a transfer to another owner keeps the identity" |
| PRAXIS-ID-01 | 3. unqualified resolves in context; ambiguous lookup fails closed | met | "an unqualified ID resolves inside its repository context", "an ambiguous unqualified lookup fails closed" |
| PRAXIS-ID-01 | 4. duplicate/conflicting identities fail closed | met | "duplicate and conflicting canonical identities fail closed" |
| PRAXIS-ID-01 | 5. ros.json repository.identity, legacy name kept, show/set, verification, validate findings (as amended) | met | "repository identity is recorded beside the legacy name and verified from the environment", "cli: repository identity set records the identity and validate rejects a copied one"; `ros.json` of this repository |
| PRAXIS-ID-01 | 6. work capture reports the canonical identity or why it is incomplete | met | `IdentityCommands.reportCanonical`; `./praxis work capture` in this repository prints `canonical identity: kemiller2002/praxis:ID (github:1309152643/ID)` |
| PRAXIS-ID-01 | 7. structured reference round-trips without the display string | met | "structured references round-trip without parsing the display string" |
| PRAXIS-ID-01 | 8. every PRX-REMOTE-050 case | met | `repositoryMatrix` in IdentityTests (12 cases: same ID two repos, same name two owners, rename, transfer, stable ID with changed locator, cross-repository reference, unqualified in context, ambiguous, foreign, duplicate/conflict, legacy migration, serialization); execution/telemetry referencing the canonical item is PRAXIS-ID-03 |
| PRAXIS-ID-04 | 1. init, upgrade, instance init create .praxis/instance.json before registration, never replace | met | "cli: init creates the instance identity and upgrade preserves it"; `Lifecycle.run` ensures before `selfRegister` |
| PRAXIS-ID-04 | 2. upgrade preserves the ID | met | same test (byte-identical after upgrade and repeated init) |
| PRAXIS-ID-04 | 3. clone keeps; template/fork foreign, not stamped, refused by reconciliation until reinitialize with predecessor; rename/transfer keep | met | "instance binding: a clone, rename or transfer is bound...", "cli: a foreign instance is refused by validate until an explicit reinitialization", "cli: native executions carry the local instance identity, never a foreign one"; EnvelopeReconciliationTests "reconciliation rejects a claimed instance the repository does not have or cannot verify" |
| PRAXIS-ID-04 | 4. telemetry executions and envelope history carry the instance ID | met | "cli: native executions carry...", `EnvelopeReconciliation.withLocalInstance` (same envelope test) |
| PRAXIS-ID-04 | 5. mismatch rejected end to end against a generated identity | met | "reconciliation rejects a claimed instance that differs from the generated local one, end to end" |
| PRAXIS-ID-04 | 6. malformed file is a typed failure | met | "the store reports a malformed record as unreadable and never overwrites it", "the instance record round-trips and a malformed one is a typed failure" |
| PRAXIS-ID-04 | 7. validate reports missing or foreign identity | met | "cli: upgrade gives an installation that predates instance identity one" (warning), "cli: a foreign instance is refused by validate..." (error) |
| PRAXIS-ID-04 | 8. this repository has its instance identity | met | `.praxis/instance.json` (`pxi-979d1acb7720455e9a50fa3b4e7b9259`, bound to github:1309152643) |
| PRAXIS-ID-05 | 1. instance projection document with all fields, unknown representable | met | "the projection is allow-listed..." (schema, protocols, `vigila: unknown`) |
| PRAXIS-ID-05 | 2. works with no Echelon component, network or credentials | met | same test and "cli: instance register is optional..." run with no administration integration |
| PRAXIS-ID-05 | 3. register via installation client, deterministic operation ID, unavailable exits 0, nothing local changes | met | "cli: instance register is optional, idempotent and changes nothing local when unavailable" |
| PRAXIS-ID-05 | 4. no credentials, secrets, paths or private data | met | "the projection is allow-listed..." seeds a token in configuration and a secret in the environment |
| PRAXIS-ID-06 | 1. workProtocol.branchPolicy work-item-id, off by default, validated | met | "cli: validate enforces workProtocol.branchPolicy..." (`loose` rejected, `none` passes) |
| PRAXIS-ID-06 | 2. meaningful changes on a non-work-item branch fail | met | same test |
| PRAXIS-ID-06 | 3. GITHUB_HEAD_REF in PR CI; detached HEAD is an error | met | same test (GITHUB_HEAD_REF case); "branch policy: meaningful work must run on a branch named for a work item" (unknown branch) |
| PRAXIS-ID-06 | 4. no meaningful change or policy off: no finding | met | both tests |
| PRAXIS-ID-06 | 5. pure domain rule with unit tests plus integration test | met | `BranchPolicy.check`; unit and CLI tests above |
| PRAXIS-ID-06 | 6. docs/work-protocol.md documents it | met | `docs/work-protocol.md` "Branch policy", `docs/identity.md` |
