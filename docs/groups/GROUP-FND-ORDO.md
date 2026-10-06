# Group analysis: GROUP-FND-ORDO

- Group: `GROUP-FND-ORDO`, recorded in the members' `group:GROUP-FND-ORDO` tags. The work-group commands (PRAXIS-GROUP-07..10) are not on main yet, so the grouping lives in item metadata and in this file (PRX-GRP-040/133). Machine-readable form: [`GROUP-FND-ORDO.group-analysis.json`](GROUP-FND-ORDO.group-analysis.json).
- Members: `PRAXIS-FND-01` (ORDO-CORE-PACKAGE), `PRAXIS-FND-02` (execution contract input).
- Execution repositories: `kemiller2002/ordo` (prerequisite: publish Ordo.Core), then `kemiller2002/praxis`.
- Base commit: `f242aae6`.

## 1. Members

| Member | Obligation | Acceptance criteria | Depends on |
|---|---|---|---|
| PRAXIS-FND-01 | PRX-EXEC-002, PRX-ARCH-001 | 1. Ordo release carries the Ordo.Core package with a sha256. 2. Praxis references that exact version from an immutable checksum-verified artifact. 3. Role capability sets come from Ordo.Core; no role table remains in Praxis. 4. Boundary classification and evaluator fingerprinting delegate to Ordo.Core. 5. Vocabulary-divergence test and vendored-package digest test. 6. foundations.json and `foundations verify` evidence the pin. 7. Governance header, docs and status updated. | Ordo release |
| PRAXIS-FND-02 | PRX-BND-001, PRX-SEQ-003, PRX-VER-010 | 1. `execution start --contract FILE` (ordo.execution-contract/1, parsed by Ordo.Core) supplies role, boundary and evaluator closure. 2. Envelope records contract source and sha256. 3. Contradicting flags are refused (exit 2). 4. Flags without a contract are assembled into the Ordo contract shape and validated by Ordo; envelope records operator-supplied. 5. Tests. 6. Docs and status. | PRAXIS-FND-01 |

## 2. Reuse inventory

| Existing element | Location | What it does | Disposition |
|---|---|---|---|
| `RoleAuthority.defaultFor` | `src/Praxis.Domain/Execution/Governance.fs:119` | Hard-coded copy of Ordo's role defaults | extended: becomes a delegation to `Ordo.Core.ExecutionRole.RoleAuthority.defaultFor` through the wire vocabulary |
| `Capability.toWire` | `src/Praxis.Domain/Execution/Governance.fs:79` | Praxis capability wire names | reused as the translation key to Ordo's `ExecutionCapability.toWire` |
| `EvaluatorIdentity.create` | `src/Praxis.Domain/Execution/Governance.fs:242` | Fingerprint over the evaluator closure | extended: fingerprint computed by `Ordo.Core.Evaluator` |
| `MutationBoundary.classify` | `src/Praxis.Domain/Execution/Governance.fs:556` | Classifies a mutated resource | extended: delegates to `Ordo.Core.MutationBoundary` |
| `ExecutionEnvelope.create` | `src/Praxis.Domain/Execution/Governance.fs:676` | Builds the envelope | extended with a contract-source field |
| `ExecutionStore.saveEnvelope` | `src/Praxis.Infrastructure/Execution/ExecutionStore.fs:39` | Persists the envelope | reused |
| `ExecutionJson` | `src/Praxis.Contracts/Execution/ExecutionJson.fs` | Envelope wire shape | extended with `contract` |
| `execution start` flag parsing | `src/Praxis.Cli/ExecutionCommands.fs:68-79, 198-209` | `--scope/--allow/--evaluator` | reused, routed through the Ordo contract shape |
| Ordo conformance vectors | `tests/Praxis.Tests/ExecutionGovernanceTests.fs:43` | Asserts byte-for-byte fingerprint equality with Ordo | reused; becomes structural once delegated |
| Ordo wire parsing | `ordo:src/Ordo.Core/ExecutionWire.fs:90-153` | `expectedFromJson`, `factFromJson` | extended in Ordo with the contract reader |
| Foundations verifier `verifyOrdo` | `src/Praxis.Cli/Foundations.fs:358` | Recognises the Ordo lifecycle (`.echelon/sde.json`) | extended to recognise the Ordo.Core package pin |

Searches: `grep -rn "defaultFor\|RoleAuthority" src`, `grep -rn "ORDO-CORE-PACKAGE" .`, `grep -rn "IsPackable" ordo/src`, `grep -n "FromJson" ordo/src/Ordo.Core/ExecutionWire.fs`.

New abstractions:

- `Ordo.Core.ExecutionContract` (in Ordo): considered `ExecutionWire.envelopeToJson` (an envelope is a running execution, not the authorizing contract) and the `ordo.execution/1` `mutationBoundary`/`evaluator` defs (reused as the contract's parts).
- Praxis `OrdoSemantics` translation (Domain): considered calling Ordo types throughout Praxis; not done now because the execution track is changing the same envelope code, so a wire-name translation keeps the change small while still removing every copied rule.

## 3. Group-level design

- One authority: Ordo.Core, consumed as the released package. Praxis keeps its host types and translates by wire name, never by its own rule tables.
- Conflict: Ordo's design says hosts exchange JSON instead of linking types; the requirement says consume rather than redefine. Resolution: link Ordo.Core for the semantics and keep the JSON wire for persisted shapes.
- Distribution: no nuget.org trusted publishing exists for Ordo, so the immutable artifact is the GitHub release asset `ordo-core.nupkg`, checksummed in `native-checksums.txt`. Praxis vendors that exact file under `packages/` with a lock recording URL and sha256, and restores it through a local NuGet source.
- Common tests: vocabulary bijection, role defaults equal Ordo's, digest of vendored package.
- Risk of solving separately: FND-02 would otherwise parse the contract with a second local parser.

## 4. Order

1. Ordo PR (packable Ordo.Core, contract reader, release asset), release through `release.yml`.
2. PRAXIS-FND-01 (consume package). 3. PRAXIS-FND-02 (contract input).

## 5. Verification pass

Recorded per member in `GROUP-FND-ORDO.group-verification.json` before each completion.
