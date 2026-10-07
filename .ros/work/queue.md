# Work Queue

| ID | Work | Status | Tags | Priority |
|---|---|---|---|---|
| ACTOR-KIND-ANCHOR | ActorKind extension pattern accepts a trailing newline ($ anchor) | captured | provenance, security | low |
| ATTR-COMPLETE-BASE-REF-SWEEP | Stop work complete from sweeping ROS_BASE_REF committed-range paths into its completion event | captured | attribution, reconciliation | medium |
| ATTR-RECONCILE-SYMLINK-SUBMODULE | Content-match symbolic links and submodules for reconciled attribution | captured | attribution, reconciliation | low |
| BOOTSTRAP-ADOPTION-FIX | BOOTSTRAP-ADOPTION-FIX | complete |  |  |
| BUG-WORK-ID-COLLISION | Reject auto-generated backlog IDs already present in work context | captured | bug, work-protocol | medium |
| CI-BASE-REF-FIX | CI-BASE-REF-FIX | complete |  |  |
| CI-LATEST-ON-VERSION-BUMP | CI-LATEST-ON-VERSION-BUMP | blocked |  |  |
| CI-NPM-PUBLISH | CI-NPM-PUBLISH | complete |  |  |
| DOC-BACKLOG-USAGE-GUIDE | DOC-BACKLOG-USAGE-GUIDE | complete |  |  |
| DOC-PROJECT-ADMIN-README | DOC-PROJECT-ADMIN-README | complete |  |  |
| DOC-WEB-README | DOC-WEB-README | complete |  |  |
| EXEC-INSTALL-109 | Execution envelopes, step ledger, worktrees, legal actions, installation registration client | complete |  | high |
| EXEC-INSTALL-109-NPM-BIN | Keep npm bin as ros only; praxis stays canonical via native release and ./praxis | complete |  | high |
| FEAT-AGENT-PROVENANCE | FEAT-AGENT-PROVENANCE | complete |  |  |
| FSHARP-ONLY-MAIN-MERGE | After merging main (GH-90 remote execution): port its Node tests to F# and renumber the colliding DF/RQ records | complete | fsharp, node-removal, follow-up | high |
| FSHARP-ONLY-REPOSITORY | Remove repository-owned Node/JavaScript/TypeScript code and tooling; make Praxis F#/.NET only and enforce it | complete | fsharp, architecture, tooling, node-removal | high |
| GH-113 | GH-113 | complete |  |  |
| GH-154 | GH-154 | complete |  |  |
| GH-155 | GH-155 | complete |  |  |
| GH-167 | GH-167 | complete |  |  |
| GH-80 | Support auditable post-commit work-item attribution reconciliation (#80) | complete | attribution, reconciliation, provenance | high |
| GH-84 | GH-84 | abandoned |  |  |
| GH-90 | Make remote/cloud-agent Praxis execution a first-class capability (#90) | abandoned | remote-execution, gh-90 | high |
| MIG-05-BACKLOG-PERSISTENCE | Add bounded recovery for backlog queue and Markdown projection | complete | fsharp, migration, persistence | high |
| MIG-05-PERSISTENCE | MIG-05 characterize and shadow transactional persistence recovery | complete | fsharp, migration, persistence | high |
| MIG-05-TELEMETRY-RECOVERY | Recover telemetry execution backlinks without duplicating execution evidence | complete | fsharp, migration, telemetry, persistence | high |
| MIG-05-WORK-INTEGRATION | MIG-05 integrate recoverable work event and context persistence | complete | fsharp, migration, persistence, work | high |
| MIG-05-WORK-PERSISTENCE | MIG-05 characterize work-state persistence and recovery | complete | fsharp, migration, persistence, work | high |
| MIG-06-GIT-INTEGRATION | MIG-06-GIT-INTEGRATION | complete |  |  |
| MIG-06-GIT-PROVENANCE | MIG-06 characterize and shadow typed Git provenance | complete | fsharp, migration, git | high |
| MIG-07-BACKLOG-PLAN | MIG-07-BACKLOG-PLAN | complete |  |  |
| MIG-07-FOUR-TIER-AUDIT | Four-tier compliance audit and real telemetry-candidate parity for the F# shadow CLI | complete |  | high |
| MIG-07-GIT-PATH-COMPOSITION | Compose real Git observed/meaningful-path defaults into the F# work context-plan | complete |  | high |
| MIG-07-TELEMETRY-RESOLUTION | Compose telemetry execution-ID resolution into the F# work plan/context plan | complete |  | high |
| MIG-07-VERIFIED-CONTEXT | MIG-07-VERIFIED-CONTEXT | complete |  |  |
| MIG-07-WORK-CONTEXT | MIG-07-WORK-CONTEXT | complete |  |  |
| MIG-07-WORK-EVIDENCE | MIG-07-WORK-EVIDENCE | complete |  |  |
| MIG-07-WORK-LIFECYCLE | MIG-07 shadow typed live-work transitions and evidence guards | complete | fsharp, migration, work | high |
| MIG-07-WORK-ORCHESTRATION | MIG-07-WORK-ORCHESTRATION | complete |  |  |
| OIDC-REPO-IDENTITY | OIDC-REPO-IDENTITY | complete |  |  |
| PKG-BIN-EXECUTABLE | PKG-BIN-EXECUTABLE | complete |  |  |
| PKG-ECHELON-FOUNDRY | PKG-ECHELON-FOUNDRY | complete |  |  |
| PKG-FALLBACK-GOLDEN | Align F# payload differential golden | complete | fsharp, testing | high |
| PKG-FALLBACK-GUIDE | Ship fallback reconciliation guide in distributed package | complete | packaging, documentation | high |
| PKG-FALLBACK-PAYLOAD | Embed fallback reconciliation guide in native F# payload | complete | fsharp, packaging | high |
| PKG-MIT-LICENSE | PKG-MIT-LICENSE | complete |  |  |
| PKG-PUBLISH-READINESS | PKG-PUBLISH-READINESS | complete |  |  |
| PR79-FALLBACK-RECONCILIATION | Complete dual-entry reconciliation and merge PR #79 | complete | reconciliation, integration | high |
| PR79-MAIN-INTEGRATION | Integrate current main into PR 79 | complete | fsharp, integration | high |
| PR79-MAIN-INTEGRATION-2 | Integrate latest main into PR 79 after CI race | complete |  | medium |
| PR79-POSTMERGE-PROVENANCE | Fix PR #79 post-merge provenance validation | complete |  | high |
| PRAXIS-A021-EVAL-KIT | EX-ROS-2026-A021 blind evaluation kit | complete | experiment | medium |
| PRAXIS-CLI-RENAME | Finish the ROS to Praxis product/CLI rename: praxis canonical, ros only as compatibility alias, persisted state or history | complete | rename, praxis, cli | high |
| PRAXIS-CONT-00 | Durable work checkpoints and agent continuity (umbrella) | complete | continuity, durable-checkpoints | high |
| PRAXIS-CONT-01-DOMAIN | Durable checkpoint domain model and invariants | complete | continuity, durable-checkpoints | high |
| PRAXIS-CONT-02-GIT | Git remote/upstream/durability observations | complete | continuity, durable-checkpoints | high |
| PRAXIS-CONT-03-PERSIST | Checkpoint events, projection, persistence, schema compatibility | complete | continuity, durable-checkpoints | high |
| PRAXIS-CONT-04-CLI | work checkpoint, context/status presentation, JSON contract | complete | continuity, durable-checkpoints | high |
| PRAXIS-CONT-05-GUARDS | Completion/block lifecycle continuity guards | complete | continuity, durable-checkpoints | high |
| PRAXIS-CONT-06-CONTINUE | Cross-executor work continue / takeover semantics | complete | continuity, durable-checkpoints | high |
| PRAXIS-CONT-07-REMOTE | Remote-agent / GitHub Actions checkpoint capability | complete | continuity, durable-checkpoints | high |
| PRAXIS-CONT-08-RECOVERY | Two-clone recovery and agent-loss integration proof | complete | continuity, durable-checkpoints | high |
| PRAXIS-CONT-09-GOVERNANCE | AGENTS/governance/docs/starter propagation | complete | continuity, durable-checkpoints | high |
| PRAXIS-CONT-10-HARDEN | Compatibility, edge cases, validation, final end-to-end review | complete | continuity, durable-checkpoints | high |
| PRAXIS-CONT-11-SEGMENTATION | Effective-current telemetry segmentation and continuity proof gaps | complete | continuity, durable-checkpoints | high |
| PRAXIS-CONT-12 | work checkpoint attributes every meaningful path since the previous checkpoint to the checkpointing item, regardless of which work item produced it | complete | continuity, gh-90 | medium |
| PRAXIS-DIST-NATIVE-DOTNET-TOOL | Retire npm distribution; ship native bundles and a .NET global tool | complete | distribution | high |
| PRAXIS-EXEC-01 | Bind an execution envelope to every governed work execution (PRX-EXEC-030, 014, 055, 026, 053, 041) | complete | execution, binding | high |
| PRAXIS-EXEC-02 | Governed evaluation runner and attributed receipts (PRX-VER-001, 002, PRX-EXEC-024, PRX-REC-007) | complete | execution, evaluation | high |
| PRAXIS-EXEC-03 | Role-specific launchers and repository execution policy (PRX-EXEC-005, 040, 041, 042, 010) | complete | execution, launchers | medium |
| PRAXIS-EXEC-04 | Host containment profile and enforcement evidence (PRX-SEC-001, 003, 010, 011, 013, 014) | complete | execution, containment | medium |
| PRAXIS-EXEC-05 | Local control-plane execution API in web serve (PRX-CTL-001, 003, 005, 006, 011, 012, PRX-UI-008) | complete | execution, control-plane | high |
| PRAXIS-EXEC-06 | Operator UI execution views with legal actions and human-required boundaries (PRX-UI-001, 004, 007, 020, 021, 026) | complete | execution, operator-ui | high |
| PRAXIS-EXEC-07 | Execution runtime docs and requirement status per row (EXECUTION-ORCHESTRATION status, docs/execution-runtime.md) | complete | execution, docs | medium |
| PRAXIS-EXEC-08 | Consume the Ordo execution contract instead of a local copy of role, boundary and evaluator semantics (PRX-EXEC-002, PRX-ARCH-001, PRX-VER-010, PRX-BND-001, PRX-SEQ-003) | abandoned | execution, ordo | low |
| PRAXIS-EXEC-09 | Link execution step ledger to telemetry and analyse cost and reliability by role (PRX-STEP-006, PRX-STEP-008) | captured | execution, telemetry | medium |
| PRAXIS-FND-01 | ORDO-CORE-PACKAGE: consume Ordo.Core as a pinned released package for execution roles, capabilities, mutation-boundary and evaluator semantics (PRX-EXEC-002, PRX-ARCH-001) | active | ordo, group:GROUP-FND-ORDO | high |
| PRAXIS-FND-02 | Accept the mutation boundary and evaluator closure from the governing Ordo execution contract (PRX-BND-001, PRX-SEQ-003, PRX-VER-010) | ready | ordo, execution, group:GROUP-FND-ORDO | high |
| PRAXIS-FND-03 | Declare Praxis's own shared foundations and make foundations verify evidence them (SAF-DEP-1, SAF-DEP-2) | ready | foundations, group:GROUP-FND-FOUNDATIONS | high |
| PRAXIS-FND-04 | Classify operational failures with Aegis at Praxis's Git, process, filesystem, network and web boundaries, with redaction and sink tests (SAF-AEGIS-1, SAF-AEGIS-5, SAF-AEGIS-6) | active | foundations, aegis, group:GROUP-FND-FOUNDATIONS | high |
| PRAXIS-FND-05 | Present praxis web serve and hub serve with a pinned Forma release instead of local CSS (SAF-FORMA-1, 2, 5, 6; PRX-UI-030, PRX-UI-031) | ready | foundations, forma, web, group:GROUP-FND-FOUNDATIONS | high |
| PRAXIS-FND-06 | Ingest Tutela security assessments and query security metrics over time with repository, ref and time provenance (TUT-1, TUT-2, TUT-3) | ready | tutela, telemetry | medium |
| PRAXIS-GROUP-01 | praxis work group create: durable human-declared execution group | complete | work-group, cli | low |
| PRAXIS-GROUP-02 | praxis work group show: a declared group with member states and progress | complete | work-group, cli | low |
| PRAXIS-GROUP-03 | praxis work group add: add a member to a declared group | complete | work-group, cli | low |
| PRAXIS-GROUP-04 | praxis work group remove: remove a member from a declared group | complete | work-group, cli | low |
| PRAXIS-GROUP-05 | praxis work group checkpoint: a group checkpoint over members' own checkpoints | complete | work-group, cli | low |
| PRAXIS-GROUP-06 | Phase-two work groups: grouped arm as base with the control arm's strengths ported | complete | work-group, cli | low |
| PRAXIS-GROUP-07 | First-class cross-repository groups: GROUP-ECHELON IDs, home record, member references, derived status, cross-repo order (PRX-GRP-100..109) | complete | grouping | medium |
| PRAXIS-GROUP-08 | Group command surface: work group list, idempotent mutations, append-only audit, versioned contracts, derived completion (PRX-GRP-110..116) | complete | grouping | high |
| PRAXIS-GROUP-09 | plan execute-group and grouped execution by default for qualifying high-affinity groups (PRX-GRP-117, 130..132, 136..137) | ready | grouping | high |
| PRAXIS-GROUP-10 | Grouped-execution completion gates: machine-checkable reuse inventory and per-criterion verification (PRX-GRP-133..135, 040, 045) | ready | grouping | high |
| PRAXIS-INTERNAL-NAMESPACES | Migrate internal Ros.* projects, namespaces, assemblies and Ros.slnx to Praxis.* (deferred from PRAXIS-CLI-RENAME, DF-ROS-2026-A043) | complete | rename, follow-up | low |
| PRAXIS-NPM-BIN | Expose a praxis npm bin alongside ros and update the public site's ROS-to-Praxis transition copy | abandoned | rename, gh-90 | low |
| PRAXIS-PLAN-01 | Deterministic shadow planner: praxis plan analyze/simulate/compare/explain (PRX-PLAN-001..182) | complete | planning, architecture, cli | high |
| PRAXIS-PLAN-02 | Accept planner decision DF-ROS-2026-A046 on the owner's approval | complete | planning, governance | medium |
| PRAXIS-PLAN-03 | Accept DF-ROS-2026-A047 and triage the EX-ROS-2026-A021 cohort on the owner's approval | complete | planning, governance | medium |
| PRAXIS-PLAN-04 | Grouped-execution guidance: reuse inventory and per-criterion verification pass (EV-ROS-2026-A064) | complete | planning, grouping | medium |
| PRAXIS-PLAN-05 | Make context overhead and cost observable: session-metrics telemetry adapter and cost.execution_total (EV-ROS-2026-A064) | complete | planning, telemetry | medium |
| PRAXIS-PLAN-06 | Renumber the EX-ROS-2026-A021 results record to avoid an evidence ID collision and relate it to the parallel evaluation kit | complete | planning, governance | high |
| PRAXIS-PLAN-07 | Reconcile PR #130 (PGEI) with main after the A021 results | complete | planning, grouping | medium |
| PRAXIS-PLAN-08 | Land the EX-ROS-2026-A021 evaluation kit and second blind evaluation on main | complete | planning, grouping | medium |
| PRAXIS-PLAN-09 | Grouped-work requirements v2: cross-repository groups, mutation commands, grouped-by-default execution, context and cost measurement (DF-ROS-2026-A053) | complete | planning,grouping,requirements | high |
| PRAXIS-PLAN-10 | Measure context reuse and cost for grouped work and price it in the planner (PRX-GRP-150..158) | ready | grouping | medium |
| PRAXIS-PLAN-11 | Review grouped-by-default at its trigger and roll back if a threshold is crossed (PRX-GRP-138) | captured | grouping | low |
| PRAXIS-PLAN-EXP-01 | Run grouping experiment EX-ROS-2026-A021: control arm, grouped arm, blind evaluation, telemetry comparison | complete | planning, experiment | medium |
| PRAXIS-PLAN-EXP-02 | Preserve A021 R2 replication and pre-register the affinity falsification experiment | complete | planning,experiment | medium |
| PRAXIS-PLAN-EXP-03 | Record A022 target-gate result and design affinity follow-on experiment | complete | planning, experiment | medium |
| PRAXIS-PLAN-EXP-04 | Build and freeze the EX-ROS-2026-A023 affinity/scale instrument, then run its target inventory | captured | planning, experiment | medium |
| PRAXIS-PR92-ID-RENUMBER | Renumber PR #92's DF/RQ records that collide with main's (A042/A043, RQ A022/A023) | complete | pr92, decision | high |
| PRAXIS-PR92-POSTMERGE-FENCE | On the reconciled branch, write post-merge tests P1-P7 from EV-ROS-2026-A060 section 6 and re-point PraxisCli to praxis.dll | complete | pr92, testing | high |
| PRAXIS-PR92-PREMERGE-REGRESSION-FENCE | Regression fence before reconciling PR #92 with main | complete | testing, pr92 | high |
| PRAXIS-PR92-PREMERGE-RENAME-FENCE | Harden Praxis rename invariants on PR #92 before main is reconciled | complete | testing, pr92 | high |
| PRAXIS-PR92-RECONCILE | Reconcile PR #92 (F#-only cleanup, Praxis rename) with current main | complete | pr92 | high |
| PRAXIS-QUAL-01 | Per-window Stale/Unsupported pacing observation states and hard-hold creation evidence (PRX-QUAL-003, PRX-QUAL-004) | active | quality, pacing | high |
| PRAXIS-QUAL-02 | Typed provider, model, quota-bucket and scope identities for pacing (PRX-QUAL-005) | ready | quality, pacing | high |
| PRAXIS-QUAL-03 | Typed pacing telemetry events with stable codes replacing free-text pace.log (PRX-QUAL-008) | ready | quality, pacing | medium |
| PRAXIS-QUAL-04 | Adversarial pacing adapter tests: Keychain, HTTP errors and redirects, provider process timeout/EOF, live hook fixtures (PRX-QUAL-011) | ready | quality, pacing | high |
| PRAXIS-QUAL-05 | Provider-neutral capacity port consumed by the planner (PRX-QUAL-009) | ready | quality, planning | medium |
| PRAXIS-QUAL-06 | Declared protocol and state-schema compatibility instead of the release.json == toolchain pin fence (PRX-QUAL-010) | ready | quality, release | high |
| PRAXIS-QUAL-07 | Engineering-risk metadata on work items (PRX-QUAL-020) | ready | quality, completion | high |
| PRAXIS-QUAL-08 | Design-debt declaration as a completion-readiness facet (PRX-QUAL-021) | ready | quality, completion | high |
| PRAXIS-QUAL-09 | Verification-matrix obligation as a completion-readiness facet (PRX-QUAL-022) | ready | quality, completion | high |
| PRAXIS-QUAL-10 | Evidence-file digests and a release-readiness evidence contract (PRX-QUAL-023 leftovers) | ready | quality, completion | medium |
| PRAXIS-QUEUE-MD-LIVE-STATE | queue.md shows the pre-promotion backlog status after work start/complete until the next backlog write | captured | work, backlog | low |
| PRAXIS-RELEASE-BUMP-WORKFLOW | Release workflow: one-click version bump that publishes | complete | release | high |
| PRAXIS-REMOTE-01 | Remote protocol v1 contract: schemas, typed domain model, validation, fingerprint, decision order | complete | remote-execution, gh-90 | high |
| PRAXIS-REMOTE-02 | Remote provenance roles: asserted request actor, observed executor, transport principal | complete | remote-execution, gh-90 | high |
| PRAXIS-REMOTE-03 | praxis remote execute boundary with request journal, SHA binding and structured results | complete | remote-execution, gh-90 | high |
| PRAXIS-REMOTE-04 | Execution steps, step-scoped usage/cost and the evidence-quality projection | complete | remote-execution, gh-90 | medium |
| PRAXIS-REMOTE-05 | Deterministic verifiable Praxis bootstrap: immutable release assets, attestation, fail-closed pins | complete | remote-execution, gh-90 | high |
| PRAXIS-REMOTE-06 | GitHub Actions reusable workflow adapter for praxis.remote | complete | remote-execution, gh-90 | high |
| PRAXIS-REMOTE-07 | Remote discovery (praxis.describe) and concise agent contract | complete | remote-execution, gh-90 | medium |
| PRAXIS-REMOTE-08 | Ordered batch/session remote requests | complete | remote-execution, gh-90 | low |
| PRAXIS-REMOTE-09 | Remote reconciliation (#80), fallback records and successor continuation | complete | remote-execution, gh-90 | medium |
| PRAXIS-REMOTE-10 | Remote execution operator documentation | complete | remote-execution, gh-90 | medium |
| PRAXIS-REMOTE-11 | End-to-end no-.NET cloud-agent proof | complete | remote-execution, gh-90 | high |
| PRAXIS-REMOTE-11-PREP | Prepare the live remote proof: ignore the request journal for attribution and script release, pin, opt-in and dispatch | complete | remote-execution, gh-90 | high |
| PRAXIS-REMOTE-11-PREP-CI | Fix PR #93 CI: enable-script test commits without a git identity | complete | remote-execution, gh-90 | high |
| PRAXIS-REMOTE-11-PROOF | Live proof: a cloud agent without .NET is governed through remote Praxis | complete | remote-execution, gh-90 | high |
| PRAXIS-REMOTE-12 | Conditor installs and configures the Praxis remote surface | ready | remote-execution, gh-90 | low |
| PRAXIS-REMOTE-12-CONTINUE-PROOF | PRAXIS-REMOTE-12-CONTINUE-PROOF | complete |  |  |
| PRAXIS-REMOTE-13 | Adapter recognizes GitHub rate limiting on push and pull-request creation (PRX-REMOTE-038) | complete | remote-execution, gh-90 | medium |
| PRAXIS-REMOTE-14 | Pull-request persistence: a same-request retry after the state branch was pushed is misreported as concurrency-conflict | complete | remote-execution, gh-90 | medium |
| PRAXIS-REMOTE-15 | Agent contract: a handoff needs a block before the successor resumes | complete | remote-execution, gh-90 | high |
| PRAXIS-REMOTE-16 | work complete silently drops the conclusion for non-research work items | complete | remote-execution, gh-90 | medium |
| PRAXIS-REMOTE-17 | Record the live praxis.remote 1.3 continuation proof as evidence | complete |  | medium |
| PRAXIS-REMOTE-INBOX-01 | Remote request inbox: a contents-write path to praxis.remote for agents that cannot dispatch Actions | complete | remote-execution, continuity | high |
| PRAXIS-REMOTE-PROBE-COMPLETE-CROSS | PRAXIS-REMOTE-PROBE-COMPLETE-CROSS | complete |  |  |
| PRAXIS-REMOTE-PROBE-COMPLETE-NOEXEC | PRAXIS-REMOTE-PROBE-COMPLETE-NOEXEC | complete |  |  |
| PRAXIS-REMOTE-PROBE-COMPLETE-WITHEXEC | PRAXIS-REMOTE-PROBE-COMPLETE-WITHEXEC | complete |  |  |
| PRAXIS-SITE-01 | Establish public-site architecture | complete | gh-84, public-site | high |
| PRAXIS-SITE-02 | Implement Echelon Foundry design foundation | complete | gh-84, public-site | high |
| PRAXIS-SITE-03 | Build the hero | complete | gh-84, public-site | high |
| PRAXIS-SITE-04 | Build the "Done is a claim" interaction | complete | gh-84, public-site | high |
| PRAXIS-SITE-05 | Explain the execution chain | complete | gh-84, public-site | high |
| PRAXIS-SITE-06 | Build "Git knows what. Praxis knows why." | complete | gh-84, public-site | medium |
| PRAXIS-SITE-07 | Explain agent accountability | complete | gh-84, public-site | medium |
| PRAXIS-SITE-08 | Show the real GH-84 agent handoff | complete | gh-84, public-site | high |
| PRAXIS-SITE-09 | Explain unattributed-change protection | complete | gh-84, public-site | medium |
| PRAXIS-SITE-10 | Explain resilient/double-entry execution | complete | gh-84, public-site | medium |
| PRAXIS-SITE-11 | Explain repository-native engineering records | complete | gh-84, public-site | medium |
| PRAXIS-SITE-12 | Explain independent installation and integrations | complete | gh-84, public-site | medium |
| PRAXIS-SITE-13 | Create a realistic execution record component | complete | gh-84, public-site | high |
| PRAXIS-SITE-14 | Build the product principles section | complete | gh-84, public-site | medium |
| PRAXIS-SITE-15 | Build installation/get-started surface | complete | gh-84, public-site | high |
| PRAXIS-SITE-16 | Build navigation and footer | complete | gh-84, public-site | medium |
| PRAXIS-SITE-17 | Accessibility hardening | complete | gh-84, public-site | high |
| PRAXIS-SITE-18 | Responsive/mobile engineering | complete | gh-84, public-site | high |
| PRAXIS-SITE-19 | Performance and resilience | complete | gh-84, public-site | medium |
| PRAXIS-SITE-20 | Security/privacy review | complete | gh-84, public-site | high |
| PRAXIS-SITE-21 | GitHub Actions verification | complete | gh-84, public-site | high |
| PRAXIS-SITE-22 | GitHub Pages deployment | complete | gh-84, public-site | high |
| PRAXIS-SITE-23 | Make the site itself a Praxis case study | complete | gh-84, public-site | high |
| PRAXIS-SITE-24 | Final communication/polish pass | complete | gh-84, public-site | medium |
| PRAXIS-SITE-25 | Adversarial claim audit | complete | gh-84, public-site | high |
| PRAXIS-SITE-26 | Final verification and evidence | complete | gh-84, public-site | high |
| PRAXIS-SITE-27 | Align Pages deployment workflow with echelon-foundry deploy-pages.yml | complete | gh-84, public-site | medium |
| PRAXIS-STATE-MERGE-01 | Parallel work items conflict in single-document Praxis state files | captured | work-protocol | high |
| PRAXIS-TELEMETRY-CLASSIFY-VOCAB | telemetry classify accepts classifications that validate rejects | captured | telemetry | medium |
| PRAXIS-TELEMETRY-COST-UNIT | telemetry record accepts a cost metric with a non-'currency' unit (e.g. --unit USD) that validate then rejects | complete | telemetry, bug | medium |
| PRAXIS-WORK-ABANDON-01 | work abandon: cancel live (ready/active/blocked) work items truthfully | complete | work-protocol, cli | high |
| PROJECT-ADMIN-HUB | PROJECT-ADMIN-HUB | complete |  |  |
| RELEASE-3-5-0 | Release Praxis 3.5.0 with remote execution (GH-90) | complete |  | high |
| RELEASE-3-6-0 | Release Praxis 3.6.0 | complete |  | medium |
| RELEASE-3-7-0 | Release Praxis 3.7.0 | complete | release | high |
| RELEASE-3-7-1 | Release Praxis 3.7.1 | complete |  | medium |
| RELEASE-3-7-2 | Release Praxis 3.7.2 | complete |  | medium |
| REMOTE-ENABLE-3-5-0 | Pin Praxis 3.5.0 and enable remote execution (read,mutate,complete) (GH-90) | complete |  | high |
| REMOTE-ENABLE-3-6-0 | Pin Praxis 3.6.0 for remote execution (praxis.remote 1.3) | complete |  | high |
| ROADMAP-PHASE-2 | ROADMAP-PHASE-2 | complete |  |  |
| ROADMAP-PHASE-3 | ROADMAP-PHASE-3 | complete |  |  |
| ROADMAP-PHASE-4 | ROADMAP-PHASE-4 | complete |  |  |
| ROS-AUTHORITY-SWITCH-DECISION | Open the ./ros authority-switch decision track (command-parity evidence + phased plan) | complete |  | high |
| TASK-20260816-PROMPTS | TASK-20260816-PROMPTS | complete |  |  |
| WEB-INTERFACE | WEB-INTERFACE | complete |  |  |
| WI-0001 | This is a test entry. | abandoned | code, testing | high |
| WI-0002 | Register other ~/dev ROS repos in the project-administration hub | captured | hub, follow-up | medium |
| WI-0003 | Provider-neutral adaptive development telemetry | complete | telemetry, architecture, execution | high |
| WI-0004 | Post-close telemetry data-quality hardening | complete | telemetry, data-quality | high |
| WI-0005 | Telemetry root-record validation guard | complete | telemetry, validation | high |
| WI-0006 | Correct telemetry gauge aggregation semantics | complete | telemetry, aggregation | high |
| WI-0007 | Adversarial telemetry architecture revision | complete | telemetry, architecture, reliability | high |
| WI-0008 | Run SDE init, status, verify, and update | complete |  | medium |
| WI-0009 | Inventory ROS operational architecture and plan the F# migration | complete | architecture, fsharp, migration, sde | high |
| WI-0010 | Install or update ROS with SDE and verify | complete | maintenance, sde | high |
| WI-0011 | Migrate ROS toward a coherent F# application under SDE | complete | fsharp, migration, sde, research-development | high |
| WI-0012 | F# work-attribution validation slice (work validate) | complete |  | medium |
| WI-0013 | F# backlog-queue validation slice (work backlog-validate) | complete |  | medium |
| WI-0014 | F# backlog-transition real effect (DF-ROS-2026-A028 Phase A, increment 1) | complete |  | medium |
| WI-0015 | F# work-capture real effect (DF-ROS-2026-A028 Phase A, increment 2) | complete |  | medium |
| WI-0016 | F# work-update real effect (DF-ROS-2026-A028 Phase A, increment 3) | complete |  | medium |
| WI-0017 | F# work attach real effect: Phase A increment 4, backlog-only effects complete | complete |  | high |
| WI-0018 | F# work start real effect: Phase A increment 5, first live-work/telemetry-creating effect | complete |  | high |
| WI-0019 | F# work resume real effect: Phase A increment 6 | complete |  | high |
| WI-0020 | F# work block real effect: Phase A increment 7 | complete |  | high |
| WI-0021 | F# work complete real effect: Phase A increment 8 | complete |  | high |
| WI-0022 | F# telemetry adapters/show real effect: Phase A/MIG-08 increment 1 | complete |  | high |
| WI-0023 | F# telemetry lifecycle bookkeeping real effect: Phase A/MIG-08 increment 2 | complete |  | high |
| WI-0024 | Re-run EV-ROS-2026-A043 command-surface parity inventory | complete |  | high |
| WI-0025 | F# telemetry summary real effect: Phase A/MIG-08 increment 3 | complete |  | high |
| WI-0026 | F# telemetry finalize real effect: Phase A/MIG-08 increment 4 | complete |  | high |
| WI-0027 | F# telemetry record real effect: Phase A/MIG-08 increment 5 | complete |  | high |
| WI-0028 | F# telemetry ingest real effect (generic adapter): Phase A/MIG-08 increment 6 | complete |  | high |
| WI-0029 | F# telemetry classify real effect: Phase A/MIG-08 increment 7 | complete |  | high |
| WI-0030 | F# telemetry start real effect: Phase A/MIG-08 increment 8 | complete |  | high |
| WI-0031 | F# adapter call real effect: Phase A/MIG-08 increment 9 (DF-ROS-2026-A007) | complete |  | high |
| WI-0032 | F# adapter publish real effect: Phase A/MIG-08 increment 10 (DF-ROS-2026-A007) | complete |  | high |
| WI-0033 | F# work resume parentExecutionId correction: fixes confirmed production defect (Phase A/MIG-08) | complete |  | high |
| WI-0034 | F# telemetry ingest openai-codex adapter: Phase A/MIG-08 increment 11 | complete |  | high |
| WI-0035 | F# telemetry ingest hook adapters: Phase A/MIG-08 increment 12 | complete |  | high |
| WI-0036 | F# telemetry ingest anthropic-claude-statusline adapter: Phase A/MIG-08 increment 13 | complete |  | high |
| WI-0037 | F# telemetry ingest OTel adapter family: Phase A/MIG-08 increment 14 (final adapter, completes telemetry ingest) | complete |  | high |
| WI-0038 | F# telemetry start execution-id and identity-override flags: Phase A/MIG-08 increment 15 (final, closes MIG-08 scope) | complete |  | high |
| WI-0039 | Re-run F# command-surface parity inventory (EV-ROS-2026-A046): confirms MIG-08 fully closed | complete |  | high |
| WI-0040 | F# work context read-only view: Phase A increment 9 (EV-ROS-2026-A046 follow-on) | complete |  | high |
| WI-0041 | F# work/work list/work show read-only merged view: Phase A increment 10 (EV-ROS-2026-A046 follow-on) | complete |  | high |
| WI-0042 | F# telemetryFindings validation port: MIG-08 increment 24 (validate unification prerequisite) | complete |  | high |
| WI-0043 | Unify F# validate command: Phase A increment 11 (combines 5 already-real contributors) | complete |  | high |
| WI-0044 | F# status command: Phase A increment 12 (closes EV-ROS-2026-A046's full inventory) | complete |  | high |
| WI-0045 | Record Phase A closure evidence and point agents at F# for already-ported read-only commands | complete |  | high |
| WI-0046 | Distribute ros-fs via npm as a self-contained single-file binary from GitHub Releases (DF-ROS-2026-A029) | complete |  | high |
| WI-0047 | Redirect this repository's own ./ros dispatch to the F# CLI (DF-ROS-2026-A030) | complete |  | high |
| WI-0048 | Add additive F# ros-fs launcher to the greenfield starter template (DF-ROS-2026-A031) | complete |  | high |
| WI-0049 | Replace Node with F# by default in the greenfield starter template (DF-ROS-2026-A032) | complete |  | high |
| WI-0050 | Execute DF-ROS-2026-A032 Phase 2 with refined scope: Node internal-library-only, golden-master tests (DF-ROS-2026-A033) | complete | fsharp-migration | high |
| WI-0051 | Root-cause and document the session-long CI empty-output failure pattern (EV-ROS-2026-A049) | complete | ci-infra | medium |
| WI-0052 | Bump to 2.0.1 to actually publish the first working stable release (2.0.0 never published, EV-ROS-2026-A050) | complete |  | high |
| WI-0053 | Fix ./ros hitting an unhandled 404 for a main-branch snapshot rosVersion | complete |  | high |
| WI-0054 | Fix ros-bootstrap init pinning a scaffolded project to a binary-less @main snapshot version | complete |  | high |
| WI-0055 | Sync queue.json's own backlog status to complete when a promoted item finishes | complete |  | medium |
| WI-0056 | Add the standardized Echelon Foundry lifecycle CLI (init/status/verify/upgrade/doctor) in F#, distributed through npm | complete |  | high |
| WI-0057 | Embed the starter scaffold in the CLI binary so init/upgrade run standalone | complete |  | high |
| WI-0058 | Release 3.0.0: version bump plus refreshed README and instructions | complete |  | high |
| WI-0059 | Classify registries/theories.json as generated in both starter manifests | complete |  | high |
| WI-0060 | Bump npm package version to 3.0.3 | complete | release, npm | medium |
| WI-0061 | Pull-request persistence: a same-request retry after the state branch was pushed is misreported as concurrency-conflict | complete | remote-execution, gh-90 | medium |
| WI-0062 | Triage SDE 1.3.0 structural review findings | complete | sde, structural-review | high |
| WI-0063 | Implement ROS next-pass Ordo observation and structured handoff (#63) | complete | ordo,next-pass | high |
| WI-0064 | Evidence-based work groups for the advisory planner (plan groups/explain-group) and the frozen grouping A/B experiment protocol | complete | planning, architecture, cli | high |
| WI-0065 | Implement first-class step-level execution telemetry | complete | telemetry, execution | high |
| WI-0066 | Retire the Python artifact-validator oracle: add the missing F# supersession-reciprocity test, delete tools/ros_cli.py, tools/__init__.py and tests/test_ros_cli.py, and drop the oracle CI step and docs | complete |  | medium |
| WI-0067 | Remove the uncalled legacy Python layout generator setup_ros_layout.py (superseded by praxis init) and its SDE-MAP row | complete |  | medium |
| WI-0068 | Recognize Limen 0.7.0 package @echelon-foundry/limen in foundations verify and Echelon doctor fixtures | complete | limen | medium |
| WI-0069 | Doctor smoke fixtures name the Limen package by its 0.7.0 name @echelon-foundry/limen | complete | limen | medium |
| WI-0070 | Derive the seeded .echelon/toolchain.json Praxis pin from the running release instead of a hard-coded 3.4.0 | complete | toolchain, lifecycle | high |
| WI-0071 | Step commands record the caller's discovered identity instead of the owning execution's, so validate rejects the step | captured | telemetry, provenance | medium |
| WI-0072 | Port provenance follow-ups onto the merged model: collaboration aggregates in provenance audit, producedBy on ordo handoffs, installer event actor | complete | provenance | medium |
| WI-0073 | Fix 3.7.1 sweep findings: work-item ID reuse, consumer doc links, upgrade hygiene, launcher EOL | complete |  | high |
| WI-ACTIVE | Active item | blocked |  | medium |
| WI-READY | Ready item | active |  | medium |
| WI-UPSTREAM-SYNC-20261001 | Add elapsed-time upstream synchronization policy and drift reporting | complete | governance, git, agents | high |
| WORK-CAPTURE-ID-COLLISION | ros add can auto-allocate a WI-NNNN ID that already belongs to a live-context work item | captured | work-protocol, backlog | medium |
| WORK-ITEM-ATTACHMENTS | WORK-ITEM-ATTACHMENTS | complete |  |  |
| WORKQUEUE-BACKLOG-LAYER | WORKQUEUE-BACKLOG-LAYER | complete |  |  |
