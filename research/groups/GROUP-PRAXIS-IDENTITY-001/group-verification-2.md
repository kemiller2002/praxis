# Verification pass: GROUP-PRAXIS-IDENTITY-001, second PR (PRAXIS-ID-02, 03)

PRX-GRP-045 pass. Evidence names a test in `tests/Praxis.Tests/IdentityTests.fs`
(prefix `identity:`) unless stated. Full suite on `id/remote-identity` with
`GITHUB_ACTIONS=true GITHUB_REPOSITORY_ID=1309152643`: 1512 tests, 1512 passed.

Decision recorded here: PRAXIS-ID-03 criterion 2 originally asked for a
structured reference on every planner item. The document-level `repository`
identity already qualifies every local ID structurally (PRX-PLAN-182 is a
SHOULD), and per-item references would change every planner document the
grouped-work track is extending; the description was amended before this pass.

| Member | Criterion | Status | Evidence |
|---|---|---|---|
| PRAXIS-ID-02 | 1. 1.4 accepts {repositoryId, repository, localId} wherever a work-item ID is accepted, including batches | met | "remote 1.4: a structured reference parses to its local ID...", "...inside a batch are normalised too" |
| PRAXIS-ID-02 | 2. a bare ID means the executing repository | met | same tests (bare `WI-0101` beside a structured one); `RequestScope` checks only structured references |
| PRAXIS-ID-02 | 3. another or contradicting repository refused fail-closed, nothing changes | met | "remote 1.4: a reference to another or unverifiable repository is refused before anything runs" (another stable ID, no established identity, legacy other locator) |
| PRAXIS-ID-02 | 4. structured reference in 1.0-1.3 refused; 1.3 executor refuses 1.4; old requests behave and fingerprint as before | met | "a 1.3 request may not use a structured reference", "bare-ID requests fingerprint exactly as before...", RemoteProtocolTests "a newer major or newer minor protocol version fails closed" (1.5 against 1.4; the same rule makes a 1.3 executor refuse 1.4); full remote and fence suites pass unchanged |
| PRAXIS-ID-02 | 5. describe advertises identity and shape; responses carry canonical identities | met | "remote 1.4: praxis.describe advertises...", "remote 1.4: responses carry the governed repository..." |
| PRAXIS-ID-02 | 6. remote-protocol.md and remote-agent-contract.md document it | met | `docs/remote-protocol.md` "Structured work-item references (version 1.4)", `docs/remote-agent-contract.md`; schemas updated |
| PRAXIS-ID-02 | 7. tests cover each case | met | rows above |
| PRAXIS-ID-03 | 1. telemetry records carry workItem beside workItemId; legacy records valid | met | "cli: native executions carry the local instance identity..." asserts `workItem`; telemetry goldens updated; existing records without it still validate (full suite) |
| PRAXIS-ID-03 | 2. plan documents carry the repository identity (as amended) | met | "planner documents carry the repository their work-item IDs belong to"; PlanningCliTests pass (parsers ignore it) |
| PRAXIS-ID-03 | 3. docs/planning.md no longer claims 182 met because no scheme exists | met | `docs/planning.md` JSON contract and status table |
| PRAXIS-ID-03 | 4. RQ-A021 names 001..050, no PRAXIS-NPM-BIN deferral, no deleted Node citations as current | met | `research/requirements/RQ-ROS-2026-A021...md` (merged with PRAXIS-MISC-09's history note) |
| PRAXIS-ID-03 | 5. round-trip tests | met | "structured references round-trip...", response and plan tests above |
