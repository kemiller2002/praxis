---
id: RP-ROS-2026-A017
title: ROS operational architecture inventory and staged F# migration plan
research_area: repository-operating-system
discipline:
  - software-architecture
  - software-engineering
  - research-systems
author_agent: openai-codex
version: 1.0.0
status: accepted
confidence: high
completion: complete
priority: high
related_projects:
  - Repository Operating System
related_documents:
  - EV-ROS-2026-A015
  - JR-ROS-2026-A016
  - DF-ROS-2026-A010
  - docs/work-protocol.md
  - docs/development-telemetry.md
supersedes: []
superseded_by: []
tags: [architecture, inventory, scripts, automation, fsharp, migration, sde]
keywords: [operational-code, workflow, domain-boundary, interoperability, characterization]
created: 2026-09-07
updated: 2026-09-07
---

# Research State Snapshot

- **Repository snapshot:** branch `main`, commit `626181ecc40e4994994c91c44e5763e9c7f9237f`, plus the complete dirty working tree observed on 2026-09-07.
- **ROS/package version:** npm package `1.2.1`; root `ros.json` still declares `rosVersion: 1.0.0`.
- **Inventory coverage:** every authored executable source, generated browser runtime, package task, workflow and inline shell block, bootstrap manifest, executable configuration source, JSON contract, and operational test was traced.
- **Highest-confidence conclusions:** present-day execution paths, state owners, duplicated validation, migration boundaries, and the first vertical slice.
- **Lowest-confidence conclusions:** future central project-administration ownership, time-entry semantics, and the best F# distribution mechanism for installed consumer repositories.
- **Current architecture:** a dependency-free Node application kernel, two browser/HTTP edges, an npm bootstrapper, a local multi-repository hub, declarative contracts, and two independent Python legacy utilities.
- **Largest immediate risk:** related authoritative state is changed through multiple write disciplines without a shared transaction/recovery boundary.
- **Recommended next action:** freeze compatibility fixtures and pre-register the migration experiment before creating the F# solution.

# 1. Executive Summary

ROS has already become an application, but its application boundary is accidental. The durable behavior is concentrated mainly in `tools/ros_cli.mjs` and `tools/ros_telemetry.mjs`: together they implement work and execution state machines, evidence obligations, provenance, normalization, classification, validation, deterministic projections, adapters, and authoritative state mutation. Launchers, HTTP servers, TypeScript clients, npm bootstrap code, workflows, schemas, and older Python implementations surround that kernel.

The correct objective is not to replace every script with F#. Stable rules that change what ROS *means* should move behind typed F# boundaries. Invocation, provider-specific translation, npm distribution, browser interaction, and GitHub-native orchestration should remain thin edges until evidence shows that moving them produces a net benefit. In particular, GitHub event triggers, permissions, checkout, runtime installation, artifact upload, and `npm publish` remain YAML; project administration remains a separate bounded context; and time entry remains a future deterministic projection rather than a premature domain type.

The proposed target is a pure `Ros.Domain`, explicit versioned `Ros.Contracts`, use-case-oriented `Ros.Application`, side-effect implementations in `Ros.Infrastructure`, and a small `Ros.Cli`. `Ros.Domain` must not reference Git, GitHub, JSON, files, npm, providers, HTTP, or project administration. Provider adapters emit a provider-neutral observation contract and preserve bounded unknown data. Current JSON and CLI behavior are compatibility surfaces, not implementation details.

The first vertical slice should be artifact validation plus registry projection, exposed initially as an alternate `ros-fs` executable. It is deterministic, already has Node and Python duplicates, has substantial characterization coverage, and can be compared byte-for-byte without moving mutable work state. Persistence/recovery and Git provenance follow immediately because they are prerequisites for safely moving the higher-value work lifecycle and telemetry capabilities.

This recommendation is the result of five explicit challenge cycles. Those cycles rejected a one-for-one port, an immediate database/event-store change, an ASP.NET rewrite, core ownership of experimental provider/admin/time-entry concepts, and migration of GitHub-native mechanics. A final failure-oriented review produced backlog refinements but no further boundary changes, which is the stopping point for diminishing returns.

# 2. Original Objective

Inventory all ROS scripts and script-equivalent operational code; reconstruct their true execution flows and capabilities; distinguish domain meaning from orchestration and ecosystem glue; classify every item; design a coherent, staged F# architecture; preserve required behavior; make the migration a measurable SDE experiment; and produce an implementation-ready backlog without translating scripts mechanically.

# 3. Scope and Method

The inspection included hidden and ignored files, executable bits, shebangs, imports, exports, child processes, all file reads/writes, Git commands, environment-variable use, package scripts, npm package contents, starter manifests, root and installed workflow templates, inline shell, browser clients, generated JavaScript, schemas, the telemetry metric registry, tests, accepted decisions, operational documentation, and live `.ros`/`.sde` state.

Activity was established from callers, package membership, manifests, workflows, tests, and Git history. Filename and extension alone were not treated as evidence. No authored or tracked Bash, PowerShell, F#, C#, Makefile, Justfile, Taskfile, composite GitHub Action, or Git hook exists. Shell code appears only in GitHub workflow `run` blocks.

The inventory distinguishes:

- **Source of truth:** the authoritative state or rule set for a capability.
- **Projection:** reproducible output that can be rebuilt from a source of truth.
- **Adapter:** provider/ecosystem translation with a narrow contract.
- **Orchestration:** setup and invocation that owns no ROS semantics.
- **Disposition:** A Core, B CLI, C glue, D adapter, E retire, or F investigate.

Evidence and reproducibility details are preserved in `EV-ROS-2026-A015`; chronological revisions are in `JR-ROS-2026-A016`.

# 4. Quantitative Baseline

| Measure | Baseline |
|---|---:|
| Authored Node/JavaScript operational sources | 12 files / 4,104 lines |
| Authored Python operational sources | 2 files / 1,443 lines |
| Authored TypeScript clients | 2 files / 934 lines |
| Total authored executable source | 16 files / 6,481 lines |
| GitHub Actions workflows | 3 files / 150 lines |
| Executable tests | 6 files / 1,880 lines |
| Named tests | 84 Node + 7 Python |
| JSON schemas | 12 |
| Normalized metric definitions | 115 |
| Bootstrap manifest entries | 84 greenfield / 63 project administration |
| Current dry-run package | 112 entries / 136,149-byte archive / 540,506 bytes unpacked |
| One local full-test run | 13.1 seconds, all 91 tests passed |
| One local validation run | 0.09 seconds, passed |
| One local registry check | 0.06 seconds, passed |

These are starting observations, not benchmark distributions. No line or branch coverage is configured. The package observation is not hermetic because ignored compiled browser files were present and therefore included by npm. Only six historical execution records were available and the starting tree was already dirty, so current telemetry is insufficient for a longitudinal before/after claim.

# 5. Complete Operational Inventory

## 5.1 Inventory field conventions

Each entry states responsibility; caller/callee; inputs/reads; outputs/writes; environment, external, Git, and GitHub behavior; failure/retry/idempotency/logging/metrics; configuration and credentials; tests/determinism/domain/state; overlap/status/removal risk/confidence; and disposition. “None” means none found in source, not merely unobserved at runtime.

## 5.2 Launchers and installation

### I-01 — `ros`

- **Type/responsibility:** executable POSIX Node launcher; selects the installed ROS CLI kernel. No secondary domain responsibility.
- **Invoked by/invokes:** humans, agents, tests, hub spokes, workflows, and package tasks invoke it; it imports `tools/ros_cli.mjs` and passes arguments to `main`.
- **Inputs/outputs/state:** command-line arguments and process working directory; stdout/stderr and exit code. It reads or writes no files itself.
- **Environment/external/Git/GitHub:** inherited indirectly through the kernel; none directly. No secrets.
- **Failure/retry/idempotency/logging/metrics:** module/runtime failures propagate; no retry; idempotency belongs to the selected command; no logging or metrics of its own.
- **Tests/determinism/domain/state:** exercised throughout Node integration tests. Deterministic dispatch. No domain rules and no authoritative mutation.
- **Overlap/status/risk/confidence:** overlaps the starter launcher by deliberate copying; active and packaged. Removal breaks the primary interface. Confidence High.
- **Disposition:** **C**, keep as a tiny stable launcher during coexistence; later point it at `Ros.Cli` without changing callers.

### I-02 — `starter/greenfield/ros`

- **Type/responsibility:** installed executable launcher, behaviorally the same as I-01 but with a relative import appropriate to scaffolded repositories.
- **Invoked by/invokes:** installed users/workflows/hub; invokes `tools/ros_cli.mjs` in the target repository.
- **Inputs through metrics:** same surface as I-01; no direct file, network, Git, secret, retry, or metric behavior.
- **Tests/determinism/domain/state:** bootstrap and packaged-tarball tests execute or inspect it. Deterministic dispatch, no domain rules or direct authoritative writes.
- **Overlap/status/risk/confidence:** intentional profile copy; active, manifest-installed. Removal breaks installed projects. Confidence High.
- **Disposition:** **C**, retain as compatibility launcher; change implementation target only after dual-runtime acceptance.

### I-03 — `starter/project-administration/ros-hub`

- **Type/responsibility:** installed executable Node launcher for the local project-administration hub CLI.
- **Invoked by/invokes:** hub operators/package task; imports `tools/ros_hub_cli.mjs`.
- **Inputs/outputs:** hub CLI arguments/current directory to stdout/stderr/exit code; no direct file or network access.
- **Failure/retry/idempotency/logging/metrics:** propagates kernel failures; no retry or telemetry.
- **Tests/determinism/domain/state:** exercised by hub tests. Deterministic dispatch; no domain rules or direct mutation.
- **Overlap/status/risk/confidence:** deliberate counterpart of I-01, active in the project-administration profile. Removal loses its CLI. Confidence High.
- **Disposition:** **C**, keep thin; project administration should remain separately deployable.

### I-04 — `bin/ros-bootstrap.mjs`

- **Type/responsibility:** npm `ros-bootstrap` executable; wrapper around bootstrap initialization and verification.
- **Invoked by/invokes:** `npx`, npm global/tool invocation, package tests; invokes `lib/bootstrap.mjs` `main`.
- **Inputs/outputs:** CLI arguments and cwd; stdout/stderr/exit. All filesystem behavior is delegated.
- **Environment/external/Git/GitHub:** no direct environment, network, Git, GitHub, or credentials.
- **Failure/retry/idempotency/logging/metrics:** catches through delegated CLI behavior; no retry/metrics.
- **Tests/determinism/domain/state:** bootstrap/tarball tests. Deterministic dispatch; no domain logic/state.
- **Overlap/status/risk/confidence:** active npm bin. Removal breaks installation. Confidence High.
- **Disposition:** **D/C**, keep the npm ecosystem launcher while the F# distribution decision remains open.

### I-05 — `lib/bootstrap.mjs`

- **Type/responsibility:** Node installation application: profile selection, manifest rendering, collision preflight, safe destination checks, checksums/modes, initial attribution/state, and post-install validation. Secondary responsibility: installation verification.
- **Invoked by/invokes:** I-04 and tests; reads package metadata and `starter/*/bootstrap-manifest.json`, copies/render sources, invokes installed `./ros validate` through `spawnSync`.
- **Inputs/reads:** target, project name, profile, `--force`; package version, manifests, template/source files, target existence, prior `.ros/installation.json`.
- **Outputs/writes:** scaffolded repository, executable modes, `.ros/installation.json`, and directly created `.ros/context/current.json`, `.ros/events/events.jsonl`, `.ros/work/queue.json`, `.ros/work/queue.md`, plus hub state for that profile.
- **Environment/external/Git/GitHub:** substitutes `ROS_VERSION`; no network, Git, GitHub, or secrets.
- **Failure/retry/idempotency:** refuses declared-file collisions unless force; cleans newly written declared files on failure; does not preflight every generated `.ros` state file and is not a multi-file transaction. Verification is repeatable; forced initialization can overwrite declared targets.
- **Logging/metrics/config:** human stdout; no execution telemetry. Manifests and package metadata are configuration.
- **Tests/determinism/domain/state:** 14 bootstrap/package tests cover profiles, collision behavior, packaged invocation, cleanup, manifests, and release assertions. Rendering is deterministic except timestamps/target context. It contains installation policy and mutates authoritative ROS state, but most behavior is packaging/orchestration.
- **Overlap/status/risk/confidence:** overlaps `setup_ros_layout.py`, path/JSON helpers, work-state initialization, and validation invocation. Active and packaged. Removal prevents installation. Confidence High.
- **Disposition:** **D** initially. Move the definition of valid initial state and an eventual `ros migrate/upgrade` operation behind Core/CLI; retain npm acquisition/materialization until a measured F# distribution choice is made.

## 5.3 Current Node application kernel

### I-06 — `tools/ros_persistence.mjs`

- **Type/responsibility:** shared JSON/text persistence, atomic replacement, and process lock helper. Secondary responsibility: stale-lock recovery.
- **Invoked by/invokes:** CLI and telemetry kernels invoke it; it invokes filesystem operations and process-liveness probes.
- **Inputs/reads:** paths, fallback values, lock resource names; JSON/text files and lock metadata.
- **Outputs/writes:** temporary files renamed atomically to targets; `.ros/locks/*.lock` created/removed.
- **Environment/external/Git/GitHub/secrets:** none.
- **Failure/retry/idempotency:** malformed JSON and I/O fail; exclusive lock acquisition fails immediately except stale lock removal; no wait/backoff. Atomic per file, not across files. Repeating the same write is content-idempotent.
- **Logging/metrics/config:** no logs or metrics; lock location is convention.
- **Tests/determinism/domain/state:** concurrency/stale-lock behavior covered by telemetry/work tests. Deterministic aside from process and filesystem state. No business rules, but it protects authoritative state.
- **Overlap/status/risk/confidence:** queue/hub/bootstrap/registry paths bypass all or part of it; active, newly shared. Removal risks corruption. Confidence High.
- **Disposition:** **B infrastructure boundary**, implement as `Ros.Infrastructure.FileSystem` behind application repository/transaction ports, not in pure Core.

### I-07 — `tools/ros_cli.mjs`

- **Type/responsibility:** 1,282-line primary application kernel and CLI. It owns work capture/update/attachments, backlog and semantic transitions, completion evidence, Git path attribution, events, adapter store/publication, front-matter parsing, artifact validation, registry generation/checking, status, argument parsing, JSON/text output, and exit codes.
- **Invoked by/invokes:** I-01/I-02, HTTP server, hub child processes, workflows, tests; imports persistence and telemetry, invokes `git`, reads stdin/files, and coordinates every major local ROS store.
- **Inputs/reads:** CLI/options; `ros.json`; repository Git state and `ROS_BASE_REF`; canonical research/framework Markdown; registries; `.ros/context`, queue, events, attachments, adapter stores/requests, publications, execution telemetry; evidence paths.
- **Outputs/writes:** queue JSON/Markdown and item detail Markdown, attachments, context, events JSONL, registries, publications, file-adapter stores and published event streams; telemetry indirectly.
- **Environment/external/Git/GitHub:** `ROS_BASE_REF`, `ROS_ACTOR`; executes read-only Git diff/status/rev-parse operations. GitHub-specific only through base-ref semantics. File adapters stand in for external work systems; no live network. No direct secrets.
- **Failure/retry/idempotency:** top-level exceptions become stderr plus exit 1. Some operations are idempotent by event/request ID, but `add` and several transitions are not safe to blindly repeat. Work transition is locked; queue mutation/ID allocation is not. Related file writes can partially complete. Git failures become empty/null values and can fail open.
- **Logging/metrics/config:** JSON command output or human findings; events and telemetry lifecycle are audit evidence. Reads work/config path policies from `ros.json`.
- **Tests/determinism/domain/state:** 25 work tests, 13 server tests, 8 hub tests, and integration portions of other suites. Pure parsing/projection portions are deterministic; timestamps/IDs/Git/files are not. Contains extensive domain rules and mutates authoritative ROS state.
- **Overlap/status/risk/confidence:** overlaps Python validation, JSON schemas, docs, HTML vocabularies, telemetry Git/path helpers, bootstrap initialization, and hub argument helpers. Active source of truth. Removal changes ROS meaning and breaks nearly everything. Confidence High.
- **Disposition:** split. **A** for work/artifact/evidence/state/adapter outcome rules and deterministic projections; **B** for command use cases and CLI contract; **Infrastructure** for Git/files/clock/processes; retain only a compatibility shim after parity.

### I-08 — `tools/ros_telemetry.mjs`

- **Type/responsibility:** 1,537-line execution/telemetry kernel: identity discovery, execution lifecycle, Git/clock snapshots, repository-scope metrics, capability state, metric validation/normalization, provider adapters, sanitized raw retention, idempotent ingestion, classification, finalization, summaries, and telemetry validation.
- **Invoked by/invokes:** CLI/work transitions and tests; imports persistence; invokes Git and reads provider JSON/JSONL/stdin through CLI helpers.
- **Inputs/reads:** `ros.json`, `telemetry/metrics.json`, `.ros/context`, execution files, working tree/files, options, provider payloads; environment identity from `ROS_TELEMETRY_*`, `ROS_ACTOR`, Codex/Claude/Gemini/Copilot session IDs, `GITHUB_ACTIONS`, `GITHUB_RUN_ID`, and `OLLAMA_HOST`.
- **Outputs/writes:** `.ros/telemetry/executions/*.json`, context execution links, lifecycle events/metrics within records, bounded sanitized raw snapshots, derived summaries to stdout through CLI.
- **Environment/external/Git/GitHub:** uses the variables above; read-only Git commands and local filesystem inspection. GitHub Actions is an identity provider, not a network call. No live provider service or secret is required.
- **Failure/retry/idempotency:** execution-scoped locks plus atomic record writes; snapshot IDs deduplicate ingestion and latest-per-session aggregation avoids cumulative double counting. Payload/record limits fail explicitly. Multi-file context/record changes are not transactional. Git errors often degrade to unavailable/null rather than fail.
- **Logging/metrics/config:** the output *is* execution telemetry; no independent logger. `ros.json` controls capture/limits/path exclusions and the metric registry controls units/scopes/aggregation.
- **Tests/determinism/domain/state:** 24 focused telemetry tests cover adapters, unknown fields, privacy, locks, limits, aggregation, Git, and lifecycle; work tests cover integration. Normalization/projection can be pure; IDs/time/Git/files are effects. Contains stable provenance/quality/lifecycle rules plus experimental provider/classification observations. Mutates authoritative execution state.
- **Overlap/status/risk/confidence:** duplicates Git/path/glob/file classification and JSON helpers; provider adapters overlap semantically without a formal intermediate contract. Active source of truth. Removal destroys execution provenance. Confidence High.
- **Disposition:** **A** for execution, provenance, measurement quality, capability and aggregation invariants; **B** for capture/ingest/finalize/summary commands; **D** initially for provider mappings; **Infrastructure** for Git/clock/files. Do not port the monolith intact.

## 5.4 HTTP, browser, and project-administration edges

### I-09 — `tools/http_body.mjs`

- **Type/responsibility:** dependency-free HTTP body reader and JSON/multipart parser with byte limits.
- **Invoked by/invokes:** both HTTP servers; consumes Node request streams and buffer/string parsers.
- **Inputs/outputs:** request headers/body and configured limits to parsed JSON or `{fields, files}` buffers. It writes no files.
- **Environment/external/Git/GitHub:** none; request traffic only. Uploaded content can be sensitive but no credential is required.
- **Failure/retry/idempotency/logging/metrics:** rejects missing/unsupported content type, malformed bodies, and oversized requests; no retry/logging/metrics. Parsing is deterministic.
- **Tests/domain/state:** covered through server/hub multipart and limit tests. No ROS domain rules and no authoritative mutation.
- **Overlap/status/risk/confidence:** shared intentionally; active. Removal breaks uploads/APIs. Confidence High.
- **Disposition:** **D/C**, keep with the Node HTTP edge while it exists; define a versioned request contract before changing implementations.

### I-10 — `tools/ros_server.mjs`

- **Type/responsibility:** local unauthenticated HTTP/static-file adapter over the primary kernel. Routes work list/detail/create/update/attach/transitions, validation, status, and attachment download.
- **Invoked by/invokes:** `npm run web`, users/browser client, tests; invokes exported functions from I-07 and I-09 and serves `web/` assets.
- **Inputs/reads:** `--root/--host/--port`, HTTP query/JSON/multipart, static assets, attachment files; default `127.0.0.1:4310`.
- **Outputs/writes:** JSON/bytes/HTTP status; all state mutation delegated to the kernel.
- **Environment/external/Git/GitHub/secrets:** no environment or Git directly; local HTTP only; no authentication/authorization or credentials.
- **Failure/retry/idempotency:** route exceptions mostly collapse to HTTP 400; missing routes/assets 404; no retry or request idempotency key. POST create can duplicate on retry.
- **Logging/metrics/config:** startup warning only; no access log, correlation ID, or metrics. Body limits are source constants.
- **Tests/determinism/domain/state:** 13 tests cover API, attachments, limits, traversal, and state parity. Thin overall, though HTTP status/error mapping is application policy. No direct authoritative state write.
- **Overlap/status/risk/confidence:** route/option/tag patterns overlap hub; active and packaged. Removal loses web UI/API, not underlying meaning. Confidence High.
- **Disposition:** **D**, keep initially; point handlers at versioned CLI/application contracts. Add authentication or enforce loopback before any broader exposure.

### I-11 — `web/app.ts`

- **Type/responsibility:** browser client for one repository: filtering, rendering, dialogs, mutations, attachments, validation/status presentation.
- **Invoked by/invokes:** loaded from `web/index.html` through generated `app.js`; calls I-10 HTTP endpoints.
- **Inputs/reads:** DOM/forms/files and JSON API; embedded assumptions about statuses, work types, priorities, actions, and evidence fields.
- **Outputs/writes:** DOM and HTTP requests; server owns persistent writes.
- **Environment/external/Git/GitHub/secrets:** browser-local HTTP; no Git/GitHub/credentials.
- **Failure/retry/idempotency/logging/metrics:** catches fetch errors for display; no retry/idempotency key/telemetry. Deterministic rendering over state.
- **Tests/domain/state:** no direct client tests; server integration indirectly covers contracts. Mostly presentation, but duplicated enumerations/action visibility can drift from domain rules. No authoritative mutation directly.
- **Overlap/status/risk/confidence:** overlaps `web/index.html` option values and kernel allowed actions. Active source; compiled output is ignored. Removal loses UI only. Confidence High.
- **Disposition:** **D**, keep TypeScript. Replace embedded domain choices with server-supplied capabilities/schema-derived options where practical.

### I-12 — generated `web/app.js`, `web/app.js.map`, `web-hub/app.js`, `web-hub/app.js.map`

- **Type/responsibility:** ignored TypeScript build outputs served by the two HTTP servers.
- **Invoked by/invokes:** browser loads JS; `tsc` generates from I-11/I-15. Source maps aid debugging.
- **Inputs/outputs/state:** no build-independent responsibility; runtime API/DOM behavior mirrors source.
- **Failure/retry/idempotency/logging/metrics:** generation is repeatable for pinned compiler/source; stale or absent files break UI. No tests ensure freshness.
- **External/tests/domain:** no domain ownership. Their mere presence changes `npm pack` contents because `web/` and `web-hub/` are packaged.
- **Overlap/status/risk/confidence:** generated/ignored but operationally used; uncertain build-artifact policy. Risk is stale/non-hermetic distribution, not lost meaning. Confidence High.
- **Disposition:** **E as hand-maintained artifacts**; build them deterministically in packaging or track a deliberate distribution artifact. Do not treat them as independent migration units.

### I-13 — `tools/ros_hub_cli.mjs`

- **Type/responsibility:** local project-administration registry and cross-repository work facade. Registers paths, generates a registry Markdown projection, invokes each spoke's `./ros`, and aggregates results.
- **Invoked by/invokes:** I-03, hub server/client, package task, tests; uses synchronous child processes in registered repositories.
- **Inputs/reads:** root/CLI args; `.ros/hub/registry.json`; registered repo `ros.json`; spoke JSON stdout; tags/status/files.
- **Outputs/writes:** hub registry JSON and Markdown; spoke work state only through child `./ros add/list` commands.
- **Environment/external/Git/GitHub/secrets:** local filesystem/process boundary only; no network/Git/GitHub/credentials.
- **Failure/retry/idempotency:** registry writes are direct and unlocked; concurrent updates can be lost. Registration by repository ID is replacement-like; create is not retry-idempotent. Aggregation records per-repo errors instead of failing the entire list. Child command failures surface stderr.
- **Logging/metrics/config:** JSON/human CLI output, no metrics. Registry paths and spoke CLI JSON are implicit contracts.
- **Tests/determinism/domain/state:** 8 hub tests cover registration, aggregation, command routing, server, and uploads. Contains project-registration policy but delegates ROS work semantics. Mutates authoritative hub state.
- **Overlap/status/risk/confidence:** duplicates CLI parsing, JSON persistence, tags/files, projections. Active project-administration profile. Removal loses multi-repo administration, not individual ROS operation. Confidence High.
- **Disposition:** **F/D now**. Treat as a separate bounded context consuming public ROS contracts. Move its stable registry rules only after project identity, portability, and central-authority requirements are known.

### I-14 — `tools/ros_hub_server.mjs`

- **Type/responsibility:** unauthenticated HTTP/static adapter for I-13, including temporary-file bridging of uploads into spoke CLI calls.
- **Invoked by/invokes:** `npm run hub`, browser client, tests; invokes hub CLI functions, I-09, filesystem temp operations, and indirectly registered `./ros` executables.
- **Inputs/reads:** HTTP/JSON/multipart, `--root/--host/--port`, `web-hub/` assets; default `127.0.0.1:4320`.
- **Outputs/writes:** HTTP responses and temporary files; hub/spoke mutation delegated. Best-effort temp deletion.
- **Environment/external/Git/GitHub/secrets:** local HTTP/process boundary; no auth. If bound externally, callers can register paths and run commands across registered repositories.
- **Failure/retry/idempotency:** handler failures mostly HTTP 400; no request retry/idempotency. Multipart filename is included in a temp path without basename sanitization, permitting path escape; cleanup is best effort.
- **Logging/metrics/config:** startup security warning only; no access/security log or metrics.
- **Tests/determinism/domain/state:** hub tests cover normal routes and upload, not malicious multipart filename traversal. No ROS domain ownership, direct transient side effects only.
- **Overlap/status/risk/confidence:** overlaps I-10 routing/static handling. Active. Removal loses hub web access. Confidence High.
- **Disposition:** **D**, keep narrowly separated but fix the confirmed filename/trust-boundary issue before broader use.

### I-15 — `web-hub/app.ts`

- **Type/responsibility:** browser client for repository registration, multi-repository filtering, work creation, and upload.
- **Invoked by/invokes:** `web-hub/index.html`/generated JavaScript; calls I-14.
- **Inputs/outputs:** DOM/forms/files and API JSON to DOM/HTTP mutations. Embedded status, priority, tag, and form assumptions.
- **Environment/external/Git/GitHub/secrets:** local browser HTTP only.
- **Failure/retry/idempotency/logging/metrics:** user-visible errors; no retries, request IDs, audit log, or metrics.
- **Tests/determinism/domain/state:** no direct client tests; hub server integration covers part of contract. Presentation plus duplicated vocabulary; no direct authoritative writes.
- **Overlap/status/risk/confidence:** overlaps I-11 patterns and HTML. Active source. Removal loses hub UI. Confidence High.
- **Disposition:** **D**, keep TypeScript; consume explicit contracts and server-supplied allowable values.

## 5.5 Legacy/independent Python utilities

### I-16 — `tools/ros_cli.py`

- **Type/responsibility:** original Python artifact front-matter parser, validator, and registry generator/checker.
- **Invoked by/invokes:** Python unit tests and direct manual use; not imported by the installed Node runtime and excluded from npm `files`.
- **Inputs/reads:** canonical Markdown and generated registries; command/path arguments.
- **Outputs/writes:** registry JSON and validation diagnostics/exit code.
- **Environment/external/Git/GitHub/secrets:** none; no network or Git.
- **Failure/retry/idempotency:** parse/validation failures reported; no retry. Projection is deterministic and repeated build is idempotent.
- **Logging/metrics/config:** human CLI output only; no metrics or external config beyond filesystem conventions.
- **Tests/determinism/domain/state:** 7 Python tests. Contains artifact identity/lifecycle/relationship rules and mutates derived registries, not canonical Markdown.
- **Overlap/status/risk/confidence:** substantially duplicates the active Node validator/projection and shares its `medium-high` behavior despite schema drift. Compatibility oracle rather than installed runtime. Removal now loses an independent characterization surface. Confidence High.
- **Disposition:** **E after migration**, but first convert its fixtures into language-neutral golden/contract tests and demonstrate F# parity with both implementations.

### I-17 — `setup_ros_layout.py`

- **Type/responsibility:** 1,095-line standalone legacy greenfield scaffold generator with embedded template bodies and force mode.
- **Invoked by/invokes:** only direct manual invocation is evidenced; no package, manifest, workflow, or test caller. It writes a fixed ROS 1.0.0 layout.
- **Inputs/reads:** target/project arguments and existing paths; templates are embedded in source.
- **Outputs/writes:** governance/docs/templates/schemas/config/agent instructions and directory layout, potentially overwriting under force.
- **Environment/external/Git/GitHub/secrets:** none; no network/Git.
- **Failure/retry/idempotency:** collision/force behavior; no transaction or rollback and no post-generation validation. Repeated force rewrites content.
- **Logging/metrics/config:** progress stdout; version/template configuration embedded in code; no metrics.
- **Tests/determinism/domain/state:** no tests. Mostly materialization, but it freezes obsolete policy/content. Deterministic apart from target context.
- **Overlap/status/risk/confidence:** superseded functionally by npm bootstrap/manifests and visibly stale at 1.0.0. Activity uncertain because direct external use cannot be disproved. Immediate removal could break undocumented users. Confidence Medium-High.
- **Disposition:** **E with evidence-gated deprecation**: warn/document replacement, preserve for one compatibility window, then remove after usage/search and parity confirmation.

## 5.6 GitHub workflows and inline shell

### I-18 — `.github/workflows/ros-validation.yml`

- **Type/responsibility:** root CI workflow on push/PR: checkout, Node/Python setup, dependency install, full tests, base-aware ROS validation.
- **Invoked by/invokes:** GitHub events; official checkout/setup actions, `npm ci`, `npm test`, `./ros validate` with `ROS_BASE_REF`.
- **Inputs/outputs/state:** repository/ref/event SHA; check status and logs. No repository writes are persisted.
- **Environment/external/Git/GitHub/secrets:** GitHub Actions service and action/npm downloads; default token used by checkout. No custom secret. Git comparison behavior is delegated to ROS.
- **Failure/retry/idempotency/logging/metrics:** step failure stops job; GitHub rerun is safe; no custom retry. Logs only, no ROS execution telemetry artifact.
- **Tests/determinism/domain/state:** workflow itself has no local harness. Contains orchestration only. Active. Removal removes CI enforcement. Confidence High.
- **Disposition:** **C**, keep minimal; eventually setup .NET and invoke `ros ci validate`, retaining GitHub-native mechanics.

### I-19 — `starter/greenfield/.github/workflows/ros-validation.yml`

- **Type/responsibility:** installed consumer validation workflow: checkout, Node/Python setup, tests when package metadata exists, base-aware `./ros validate`.
- **Invoked by/invokes:** consumer GitHub push/PR; actions and installed ROS launcher.
- **Inputs through metrics:** same general surface as I-18; no direct state persistence, credentials, retry, or domain rules.
- **Tests/determinism/domain/state:** manifest/bootstrap tests assert installation, not live execution. Active template. Removal leaves consumers without default validation. Confidence High.
- **Disposition:** **C**, retain and ultimately reduce to checkout/runtime setup plus one versioned ROS CLI invocation.

### I-20 — `.github/workflows/publish.yml`

- **Type/responsibility:** main-branch validation and npm publication. Inline shell detects `package.json` version changes, queries npm, selects stable versus main-snapshot version, rewrites local package version, and publishes with provenance.
- **Invoked by/invokes:** push to `main`; checkout/setup-node/setup-python, `npm ci/test`, registry/ROS validation, Git diff/show, `npm view`, `npm version --no-git-tag-version`, `npm publish`.
- **Inputs/reads:** before/current commits, package version, run number/attempt, npm registry state, GitHub event.
- **Outputs/writes:** GitHub outputs/logs, registry publication; snapshot path mutates workspace `package.json`/lock before publish but does not commit.
- **Environment/external/Git/GitHub/secrets:** `GITHUB_OUTPUT`, `GITHUB_RUN_NUMBER`, `GITHUB_RUN_ATTEMPT`, npm registry/OIDC trusted publishing and GitHub token/permissions. Network is required.
- **Failure/retry/idempotency:** failures stop job. Reruns avoid duplicate stable versions by registry lookup; unknown `npm view` failure can be conflated with absence unless exit semantics are handled precisely. Snapshot run/attempt makes rerun versions unique.
- **Logging/metrics/config:** step logs/outputs, no structured release decision artifact. Package version and workflow shell are configuration/source of truth.
- **Tests/determinism/domain/state:** bootstrap test textually asserts current latest-channel policy; actual workflow is not executed locally and `CI-LATEST-ON-VERSION-BUMP` remains blocked. Contains ROS release policy mixed with GitHub orchestration. Active; removal stops publication. Confidence High for source, Medium for unexecuted external behavior.
- **Disposition:** split **B/C**. Move release eligibility/version planning to `ros release plan` returning versioned JSON; retain npm authentication, registry call/publish, permissions, and event orchestration in YAML.

## 5.7 Task runners, declarative executable inputs, and hidden logic

### I-21 — root `package.json` scripts

- **Type/responsibility:** npm task runner for `test`, browser/hub compilation, local servers, pack inspection, release check, and `prepack`; npm bin/files define distribution.
- **Invoked by/invokes:** humans, CI, npm packaging; Node test runner, Python unittest, TypeScript compiler, servers, `npm pack`, registry/validation CLI.
- **Inputs/outputs:** source/tests/package config to compiled ignored JavaScript, test/build logs, package archive/dry-run.
- **Environment/external/Git/GitHub/secrets:** npm can access registry during install/publish outside these scripts; scripts themselves need no secret. `prepack` runs tests.
- **Failure/retry/idempotency:** shell chaining stops on failure; no retry. Builds are overwrite-idempotent. `test` omits TypeScript compilation; `pack` may include locally present ignored build output.
- **Logging/metrics/config:** process logs, no structured metrics. Active and packaged. No ROS domain meaning except which gates define a releasable package.
- **Tests/risk/confidence:** package tests inspect scripts/package contents. Removal breaks development/release ergonomics. Confidence High.
- **Disposition:** **C**, retain as ecosystem task glue; make clean deterministic build/package checks explicit and have tasks invoke the F# CLI where appropriate.

### I-22 — `starter/project-administration/package.json` scripts

- **Type/responsibility:** copied task aliases for TypeScript builds and local web/hub servers.
- **Invoked by/invokes:** installed project administrators; TypeScript compiler and Node server modules.
- **Operational characteristics:** same build/server side effects and limitations as relevant I-21 tasks; no domain rules, credentials, metrics, retry, or authoritative state itself.
- **Overlap/status/risk/confidence:** deliberate installed subset; active manifest input. Removal reduces usability. Confidence High.
- **Disposition:** **C**, retain thin.

### I-23 — `starter/*/bootstrap-manifest.json`

- **Type/responsibility:** declarative installation plans: source/destination, render flag, mode, directories, and profile payload.
- **Invoked by/invokes:** I-05; indirectly selects nearly all installed runtime, schema, workflow, docs, web, and config components.
- **Inputs/outputs:** reads package-local source paths and determines target filesystem writes. No direct execution/network/Git.
- **Failure/idempotency/config:** invalid/missing entries fail bootstrap; duplicates or omissions can make an incomplete product. Manifest order participates in installation and cleanup. Deterministic.
- **Tests/domain/state:** bootstrap tests verify paths, existence, permissions, package invocation, and payload. It contains product/profile composition policy, not ROS state rules.
- **Overlap/status/risk/confidence:** profile manifests overlap substantially by design. Active source of packaged topology. Removal breaks installation. Confidence High.
- **Disposition:** **C**, retain declarative packaging; generate/check shared portions from one inventory if drift becomes costly.

### I-24 — `ros.json` and starter `ros.json` files

- **Type/responsibility:** executable policy configuration for semantic state mapping, completion evidence obligations, meaningful/ignored paths, repository identity, telemetry capture, record/raw limits, and classification extensions.
- **Invoked by/invokes:** work CLI, telemetry, validation, bootstrap-installed runtime, tests, and Git attribution.
- **Inputs/outputs/state:** read-only configuration; influences authoritative transitions and metrics. No network/secrets.
- **Failure/idempotency:** defaults can mask missing fields; invalid combinations are only partly validated. Root and profile variants intentionally differ, but `rosVersion` drift and project-admin completion defaults are potential ambiguity.
- **Tests/determinism/domain/state:** many behaviors are test-covered; no complete config-schema evaluation. Contains domain policy and must be boundary-validated.
- **Overlap/status/risk/confidence:** overlaps docs, code defaults, HTML choices, telemetry registry, and JSON schemas. Active source of configured truth. Removal changes behavior. Confidence High.
- **Disposition:** stable meanings to **A**, loading/validation to **Contracts/Application**; keep versioned JSON as public configuration.

### I-25 — `schemas/*.schema.json` (12 contracts)

- **Type/responsibility:** published JSON contracts for artifact metadata, work context/queue/events/adapters, and execution telemetry.
- **Invoked by/invokes:** installed for interoperability/documentation; current Node/Python validators do not execute them as schemas.
- **Inputs/outputs:** declarative validation vocabulary only; no side effects/network/Git/secrets.
- **Failure/idempotency:** drift is silent unless tests compare semantics. Concrete drift: artifact schema excludes `medium-high`, while both validators accept it and a test requires it.
- **Tests/determinism/domain/state:** selected shapes are asserted indirectly; no full schema conformance suite. They express domain and wire rules but do not mutate state.
- **Overlap/status/risk/confidence:** overlap hand-written validation and docs; active public contract with uncertain enforcement. Removal harms interoperability. Confidence High.
- **Disposition:** **A/Contracts**, retain as deliberate versioned boundaries; validate them and generated/hand-written codecs against shared fixtures rather than relying on default F# serialization.

### I-26 — `telemetry/metrics.json`

- **Type/responsibility:** versioned normalized metric registry defining 115 metric IDs, units, scopes, kinds, aggregation, provenance expectations, and descriptions.
- **Invoked by/invokes:** I-08 validation/normalization/capability discovery and tests.
- **Inputs/outputs:** read-only domain catalog; affects execution record acceptance and summaries. No external effects/secrets.
- **Failure/idempotency:** missing/duplicate/inconsistent definitions cause runtime findings; provider growth can require additions. Deterministic.
- **Tests/domain/state:** extensively exercised through telemetry tests but not generated from types. Stable metric semantics plus extensible registry are domain-relevant.
- **Overlap/status/risk/confidence:** overlaps provider adapter maps and documentation. Active source of metric truth. Removal breaks normalization. Confidence High.
- **Disposition:** **A/Contracts**, keep as data loaded and validated into F# domain definitions; do not hard-code every future provider field as a union case.

### I-27 — `web/index.html`, `web-hub/index.html`, and TypeScript configs

- **Type/responsibility:** static UI shell/form option lists and compiler inputs/output settings.
- **Invoked by/invokes:** HTTP servers, browsers, and npm build tasks.
- **Inputs/outputs:** HTML/TS sources to DOM and generated JavaScript/source maps. No authoritative filesystem state.
- **Failure/idempotency:** stale hard-coded status/priority/work-type values can contradict the kernel; compiler config can create package-state variance. No retry/logging/metrics.
- **Tests/domain/status:** no direct UI compilation/contract tests in `npm test`. Active. Contains duplicated presentation vocabulary, not authoritative rules.
- **Disposition:** **D/C**, keep; replace duplicated allowed choices with capability metadata or generated checked constants, and make compilation part of CI/package reproducibility.

### I-28 — `.sde/manifest.json`, `.sde/methodology/*`, and `.sde/experiments/*`

- **Type/responsibility:** current working-tree methodology, construction, verification, experiment, and metric definitions used to structure SDE work; no executable runner was found.
- **Invoked by/invokes:** agents/humans by instruction rather than a repository command; references ROS work/evidence and expected verification.
- **Inputs/outputs:** declarative Markdown/JSON records; no direct effects, network, Git, retry, logging, metrics emission, or credentials.
- **Tests/domain/status:** not runtime-tested and currently untracked/pre-existing. It defines research method, not operational ROS semantics. Confidence Medium because its future integration is active experimentation.
- **Disposition:** **F/Contracts later**. Preserve as extensible observation/methodology input; do not freeze it into stable F# domain types during early migration.

## 5.8 Test and support inventory

The six executable test files are operational safety infrastructure rather than production behavior:

| Component | Coverage supplied | Important gap | Disposition |
|---|---|---|---|
| `tests/npm-bootstrap.test.mjs` | 14 bootstrap, manifest, tarball, cleanup, publish-policy assertions | no clean-room F# distribution or live workflow | C; retain and extend as compatibility harness |
| `tests/work-protocol.test.mjs` | 25 backlog, transition, evidence, Git, registry, adapter cases | fault injection and queue concurrency incomplete | C; make language-neutral fixtures where possible |
| `tests/telemetry.test.mjs` | 24 identity, adapters, raw/privacy, limits, locks, Git, aggregation | live provider/version fixtures absent | C; retain as old-runtime oracle during shadowing |
| `tests/ros-server.test.mjs` | 13 HTTP/work/attachment/limit/static cases | no auth, request-id, or UI compilation test | D/C edge tests |
| `tests/ros-hub.test.mjs` | 8 registry/spoke/server/upload cases | no concurrent registry or malicious filename case | D/C edge tests |
| `tests/test_ros_cli.py` | 7 legacy artifact validator/registry cases | independent obsolete runtime only | E after fixture migration and parity |

# 6. Reconstructed Execution Flows

## 6.1 System context

```text
human / agent / GitHub / browser
              |
      launchers, npm tasks, YAML, HTTP
              |
       Node CLI/application kernel
        /          |           \
 canonical MD   .ros state    telemetry JSON
      |             |             |
 registries      events/queue   summaries/evidence
              \
               Git working tree

project-admin browser -> hub -> registered repository ./ros -> same kernel
provider JSON/stdin -> telemetry adapter -> provider-neutral execution record
```

## 6.2 F-01 Bootstrap installation

1. **Trigger:** `npx`/npm/user invokes `ros-bootstrap init` or verification.
2. **Components:** I-04 -> I-05 -> profile manifest/templates/package metadata -> installed I-02 or I-03 -> `./ros validate`.
3. **State read:** profile manifest, source payload, target filesystem, prior installation manifest, package version.
4. **Decisions:** profile, rendered variables, safe target containment, collision policy, overwrite authorization, executable modes.
5. **State written:** scaffolded files; installation attribution; directly synthesized queue/context/events/hub records.
6. **External effects:** target filesystem and child validation process; no network/Git.
7. **Failure:** declared collisions abort; partial declared writes are cleaned when possible; generated `.ros` state is outside complete preflight/transaction coverage.
8. **Recovery:** rerun verify, remove/fix collisions, or explicitly use force; no versioned upgrade/migration command.
9. **Source of truth:** manifest plus package source; initial-state semantics are duplicated in bootstrap code.
10. **Ownership ambiguity:** materialization is npm glue, but valid initial ROS state and upgrade rules belong behind the ROS application boundary.

## 6.3 F-02 Canonical artifact validation and registry projection

1. **Trigger:** author edits canonical Markdown; CLI/workflow invokes registry build/check or validate.
2. **Components:** I-07, optionally I-16, canonical paths, registries, schemas/documentation.
3. **State read:** YAML-like front matter and cross-document references.
4. **Decisions:** ID/path/prefix uniqueness, required metadata, lifecycle/relationship/reference legality, stable sorting and projection.
5. **State written:** generated registry JSON; canonical Markdown remains authoritative.
6. **External effects:** filesystem only.
7. **Failure:** findings/exit 1; direct sequential projection writes could leave a mixed registry set after interruption.
8. **Recovery:** correct documents and rebuild; registries are reconstructible.
9. **Source of truth:** canonical Markdown; registry JSON is a deterministic projection.
10. **Ownership ambiguity:** rules exist independently in Node, Python, schemas, and prose.

## 6.4 F-03 Backlog to active work execution

1. **Trigger:** `ros add`, `work ready`, `work start/begin`, HTTP, or hub-spoke invocation.
2. **Components:** I-07 -> I-06/I-08 -> Git/files; queue/context/events/telemetry stores.
3. **State read:** `ros.json`, queue, context, evidence/path/Git state, metric registry, identity environment.
4. **Decisions:** next ID, backlog legality, semantic state, work type, actor, classification, meaningful path scope, execution identity/capabilities.
5. **State written:** queue JSON/Markdown/detail/attachments; active context; event JSONL; execution record and backlink.
6. **External effects:** read-only Git; local files.
7. **Failure:** invalid transitions/evidence throw; work transition is locked, but queue capture/ID allocation is not and multi-file changes can be partial.
8. **Recovery:** validation/status findings, block/resume, manual repair where no recovery command exists.
9. **Source of truth:** queue before promotion; context for active/completed semantic lifecycle; events/execution records as audit evidence.
10. **Ownership ambiguity:** queue and context represent different phases, but projections/merging make the transition boundary implicit.

## 6.5 F-04 Block, resume, complete, and finalize

1. **Trigger:** agent/user transitions one or more work IDs.
2. **Components:** I-07 transition kernel, I-08 lifecycle/finalization, persistence locks, Git snapshot, configured evidence rules.
3. **State read:** context, active executions, evidence files, Git current/baseline state, config.
4. **Decisions:** transition legality, reason/conclusion, required evidence, blocked duration, clean-baseline attribution, final metrics/status.
5. **State written:** finalized execution records, context state/timestamps/evidence, event log.
6. **External effects:** read-only Git.
7. **Failure:** missing evidence/illegal state fails before some writes, but finalizing multiple executions plus context/event is not one transaction.
8. **Recovery:** fix evidence, resume/retry, validate backlinks; no general transaction journal/replay.
9. **Source of truth:** context for current lifecycle; execution record for execution facts; events for chronology.
10. **Ownership ambiguity:** lifecycle spans work and execution modules without an explicit application unit-of-work contract.

## 6.6 F-05 Telemetry ingestion and aggregation

1. **Trigger:** execution start, provider JSON/JSONL/stdin ingest, explicit metric/classification, hook lifecycle, or finalization.
2. **Components:** CLI -> provider adapter in I-08 -> metric/config registries -> locked execution file.
3. **State read:** payload, identity environment, execution record, registry/config, repository/Git/clock.
4. **Decisions:** adapter mapping, capability present/unavailable, units/scope/source/quality, sensitive-key redaction, raw size/retention, snapshot idempotency, latest-per-session aggregate.
5. **State written:** normalized metrics, capabilities, classification, sanitized raw snapshots, lifecycle events, final Git/clock metrics.
6. **External effects:** local files/read-only Git; no live provider call.
7. **Failure:** invalid/oversize payload fails; lock contention fails immediately; unknown fields are retained rather than rejected.
8. **Recovery:** repeat identical snapshot safely, repair input, retry after lock; Git unavailable remains an explicit capability only in some paths and null/empty in others.
9. **Source of truth:** individual execution files; summaries are projections.
10. **Ownership ambiguity:** stable measurement semantics and experimental provider parsing coexist in one module.

## 6.7 F-06 Local repository web interface

1. **Trigger:** `npm run web` and browser requests.
2. **Components:** TypeScript build -> I-10/I-09 -> I-07/I-08 -> local state; I-11 in browser.
3. **State read:** static assets, API input, work/state files.
4. **Decisions:** route/body/status mapping at HTTP edge; all work legality in kernel; UI locally duplicates action/vocabulary presentation.
5. **State written:** delegated work/state changes and attachments.
6. **External effects:** unauthenticated local HTTP.
7. **Failure:** API errors mostly 400 and displayed; POST retries may duplicate.
8. **Recovery:** user retry/manual CLI; no request replay token.
9. **Source of truth:** kernel/state files, never DOM.
10. **Ownership ambiguity:** error classification and advertised allowed values lack a versioned API capability contract.

## 6.8 F-07 Project-administration aggregation

1. **Trigger:** `ros-hub`, `npm run hub`, or hub browser.
2. **Components:** I-13/I-14/I-15 -> hub registry -> registered repository `./ros` processes.
3. **State read:** local path registry and each spoke's `ros.json`/CLI JSON.
4. **Decisions:** repository identity/registration, target routing, aggregate filter, best-effort error inclusion.
5. **State written:** hub registry/projection and spoke work state via spoke CLI; transient upload files.
6. **External effects:** local cross-repository process execution; optional HTTP exposure.
7. **Failure:** one broken spoke is captured during aggregate list; registry race can lose updates; upload filename can escape temp directory.
8. **Recovery:** repair/unregister path and retry; best-effort temp cleanup; no reconciliation protocol.
9. **Source of truth:** each spoke for work; hub registry for local membership.
10. **Ownership ambiguity:** current local convenience is not evidence for a future central project-administration source of truth.

## 6.9 F-08 GitHub validation

1. **Trigger:** push or pull request.
2. **Components:** I-18/I-19 -> checkout/runtime setup -> npm tests -> ROS validation.
3. **State read:** checkout, dependencies, base/current SHAs.
4. **Decisions:** test/validation pass and base-aware meaningful path findings.
5. **State written:** GitHub check/log only.
6. **External effects:** GitHub Actions and package/action downloads.
7. **Failure/recovery:** failing step blocks check; rerun is safe.
8. **Source of truth:** ROS validation kernel for meaning; YAML for trigger/permissions/environment.
9. **Ownership ambiguity:** none material in validation workflow, apart from runtime setup during migration.

## 6.10 F-09 Release and versioning

1. **Trigger:** push to main.
2. **Components:** I-20 -> tests/validation -> Git/version shell -> npm registry -> stable or snapshot publish.
3. **State read:** two Git revisions, package version, registry package versions, run identity.
4. **Decisions:** version changed, stable already exists, publish stable or `-main.<run>.<attempt>`.
5. **State written:** temporary version rewrite, npm publication, workflow outputs/logs.
6. **External effects:** GitHub/OIDC/npm registry.
7. **Failure:** job stops; unknown registry failures risk ambiguous classification; no locally executable end-to-end harness.
8. **Recovery:** rerun; stable version lookup prevents duplicate publish; snapshot versions are unique per attempt.
9. **Source of truth:** package version plus npm registry; policy is embedded in shell.
10. **Ownership ambiguity:** release decision is ROS/package policy; authentication/publishing is GitHub/npm orchestration.

## 6.11 F-10 File adapter call and event publication

1. **Trigger:** `ros adapter call/publish`.
2. **Components:** I-07 -> request/store JSON or event JSONL/destination/receipts.
3. **State read:** versioned request, configured local store, work events, prior destination entries.
4. **Decisions:** request validation/authorization/outcome, request-ID dedupe, event-ID dedupe.
5. **State written:** store JSON, destination JSONL, `.ros/publications.json` receipts.
6. **External effects:** local filesystem only; models an external administration adapter.
7. **Failure:** explicit failure/unknown exit codes for calls; publication append plus receipts is not atomic or locked.
8. **Recovery:** request/event identity makes sequential retry mostly safe; unknown outcome requires reconciliation.
9. **Source of truth:** local conformance store for the simulated target; events for outbound activity.
10. **Ownership ambiguity:** adapter outcomes/contracts are durable; file transport is test/conformance infrastructure, not a production integration.

# 7. Capability Map

| Behavioral capability | Current implementation/source of truth | Duplication and hidden coupling | Gap | Appropriate future owner |
|---|---|---|---|---|
| Artifact identity/lifecycle/relations | canonical Markdown + I-07 | I-16, schemas, docs | schemas not enforced | `Ros.Domain.Artifacts` + Contracts |
| Deterministic registry projection | I-07 | I-16; direct sequential writes | set-level atomicity | Application projection + FS infrastructure |
| Work capture/backlog | queue JSON via I-07 | hub/bootstrap direct paths; HTML vocabularies | unlocked ID allocation | Domain/Application + CLI |
| Work semantic lifecycle | context/events via I-07 | docs/config defaults | multi-file unit of work unclear | `Ros.Domain.Work` + Application workflow |
| Completion/evidence obligations | `ros.json` + I-07 | prose/profile defaults | config drift/version mismatch | Domain policy loaded through Contracts |
| Attachments | I-07/I-10 | hub temp bridge | content trust/scanning absent | Application port + FS infrastructure |
| Execution context/lifecycle | I-08 + context links | bootstrap direct initialization | transaction/recovery incomplete | `Ros.Domain.Execution` + Application |
| Git provenance/scope | I-07 and I-08 | two parsers/glob implementations | rename bug, fail-open ambiguity | one Infrastructure Git port + explicit result |
| Telemetry capability discovery | I-08 + environment | provider assumptions embedded | live-provider calibration absent | Application + edge adapters |
| Metric normalization/aggregation | metric registry + I-08 | provider maps/docs | experimental catalog evolution | Domain stable semantics + extensible registry |
| Raw telemetry privacy/retention | I-08 + `ros.json` | validation also scans stored files | novel sensitive keys possible | Application policy + Infrastructure storage |
| Classification/R&D observations | I-08/config | work-type default mapping | concepts still experimental | small stable DU plus extension values |
| Validation | I-07 | I-16, schemas, docs | multiple authorities | Application validation over Domain/Contracts |
| File-adapter conformance | I-07 | request/result schemas | no production transport | Contract + external adapter |
| Local web access | I-09-I-11 | HTML/TS vocabulary | no auth/request IDs/client tests | external adapter |
| Local project administration | I-13-I-15 | shared helpers, spoke CLI contract | no central identity/reconciliation | separate bounded context |
| Bootstrap/materialization | I-04/I-05/manifests | I-17 and direct initial-state rules | no upgrade command | npm adapter + ROS migration CLI |
| GitHub validation | YAML + CLI | root/template workflow | no workflow harness | GitHub glue -> ROS CLI |
| Release/version selection | inline shell | package tests assert text | external outcome ambiguity | CLI release plan + YAML publisher |
| Requirements sync/lifecycle | not implemented; optional telemetry links only | none | entire capability absent | investigate before modeling |
| Central administration sync | file adapter/hub are local approximations | competing implied futures | authority/conflict model absent | investigate/separate integration |
| Time-entry derivation | not implemented | none | legal/approval/allocation semantics absent | future external deterministic projection |

The capability map supports a critical distinction: requirement synchronization, central administration, and time entry are expected directions, but they are not secretly implemented today. They should shape extensibility and ports, not be represented as finished domain models.

# 8. Accidental Architecture, Duplication, and Ranked Risk

| Rank | Risk | Evidence | Consequence | Required response |
|---:|---|---|---|---|
| 1 | Split write disciplines and no multi-file transaction/recovery contract | bootstrap writes state directly; context/events/executions span files; queue/hub/registry bypass some locks | interruption or concurrency can leave internally inconsistent authoritative state | define application unit-of-work, lock order, atomic set/intent log, recovery and fault-injection tests before moving stateful slices |
| 2 | Multiple semantic authorities | Node/Python validators, schemas, config, docs, HTML values; `medium-high` concrete drift | callers can be valid to one surface and invalid to another | establish explicit contracts and one semantic implementation; generate/check projections |
| 3 | Git provenance can be wrong or silently absent | duplicate parsers; work parser mishandles second rename/copy pathname; failures become empty/null | false completion enforcement and misleading evidence | one typed Git port with `Available/Clean/Unavailable/Invalid`, rename fixtures, and no fail-open conflation |
| 4 | Application kernel hidden in two monoliths | 2,819 lines combine parsing, state, effects, providers, projections, CLI | changes have broad blast radius and boundaries stay implicit | extract behavior by capability/use case, not by source-file translation |
| 5 | Hub trust boundary and confirmed upload traversal | unauthenticated optional non-loopback server executes registered CLIs; filename influences temp path | arbitrary local write/process reach if exposed or malicious upload arrives | sanitize basename/create temp directory safely, enforce loopback/auth policy, threat-model before central use |
| 6 | Retry and concurrency semantics are inconsistent | locked telemetry/work transitions, unlocked queue/hub/publication; POST create lacks request ID | duplicate work, lost updates, uncertain side effects | versioned command/request IDs, optimistic version/check or locks, idempotent handlers |
| 7 | Release policy is embedded and externally untested | large inline shell; blocked latest-channel acceptance criterion | publication mistakes or inability to change policy safely | pure `release plan`, fixture tests, retain external publish edge |
| 8 | Package contents depend on ignored build artifacts | `web/*.js` present locally and npm includes `web/` | clean checkout and developer checkout can publish different packages | clean-room pack test and deterministic prepack build/artifact policy |
| 9 | Browser vocabulary/compilation drift | HTML/TS hard-code choices; root tests do not run `tsc` | UI can advertise invalid/missing operations or ship stale JS | compile in CI and expose checked capabilities/options |
| 10 | Bootstrap has a second state constructor | initial queue/context/events written outside kernel and not fully collision-preflighted | installed initial state can diverge from runtime invariants | domain initial-state factory plus migration/verify command |
| 11 | Public schemas are documentation, not enforcement | validators hand-code rules | compatibility claims cannot be proven from schema artifacts | schema/codec compatibility suite and version policy |
| 12 | Manual upgrade is an operational gap | migration docs but no executable upgrade command | consumers can remain on stale/inconsistent snapshots | add inspect/plan/apply/verify migration workflow with rollback |
| 13 | Errors and observability are too coarse at edges | HTTP mostly 400; Git fallback; no request/security/audit log | recovery and diagnosis require reading implementation | typed errors, structured CLI/API envelope, operation IDs, explicit external outcome |
| 14 | Local-filesystem assumptions are undocumented contracts | path registry, process locks, sync child calls | network/multi-host use can corrupt or hang | state supported concurrency model; add thresholds before introducing a database |

This ranking puts correctness and ownership ahead of language. An F# port that preserves the current write ambiguity would keep the highest risks intact.

# 9. Disposition Matrix

Every inventory entry has one primary disposition below. A large current file
may be decomposed when its responsibilities cross boundaries; the primary
classification describes the fate of the component as a whole, while the
following capability matrix states where extracted behavior belongs.

| Inventory item | Primary disposition | Component outcome |
|---|---|---|
| I-01 root `ros` | C | retain compatibility launcher |
| I-02 greenfield `ros` | C | retain installed compatibility launcher |
| I-03 `ros-hub` launcher | C | retain thin hub launcher |
| I-04 npm bootstrap executable | D | retain npm ecosystem adapter |
| I-05 bootstrap library | D | retain materializer; extract valid-state and migration semantics to A/B |
| I-06 persistence helper | A | move state-consistency/commit semantics behind Application; implement filesystem effects in Infrastructure |
| I-07 Node CLI/application kernel | A | decompose domain/application behavior into A and command surface into B; retire old kernel after parity |
| I-08 telemetry kernel | A | decompose stable semantics into A/B and keep provider mappings as D |
| I-09 HTTP body parser | D | retain with Node HTTP edge |
| I-10 repository HTTP server | D | retain narrow versioned adapter |
| I-11 repository browser client | D | retain TypeScript adapter |
| I-12 generated browser output | E | cease treating local ignored output as an independent artifact; generate deterministically |
| I-13 hub CLI/kernel | F | harden now; decide future project-administration ownership before migration |
| I-14 hub HTTP server | D | retain narrow adapter and remediate security issue |
| I-15 hub browser client | D | retain TypeScript adapter |
| I-16 Python validator | E | use as compatibility oracle, then retire |
| I-17 legacy layout generator | E | evidence-gated deprecation and retirement |
| I-18 root validation workflow | C | retain GitHub-native orchestration |
| I-19 starter validation workflow | C | retain minimal installed orchestration |
| I-20 publish workflow | C | retain publishing edge; extract release decision to B |
| I-21 root package tasks | C | retain ecosystem task glue |
| I-22 project-admin package tasks | C | retain ecosystem task glue |
| I-23 bootstrap manifests | C | retain declarative packaging plans |
| I-24 ROS configurations | A | keep versioned JSON; load stable policy through Core/Application |
| I-25 JSON Schemas | A | retain as versioned Contracts and enforce compatibility |
| I-26 metric registry | A | retain extensible data loaded into stable metric semantics |
| I-27 HTML/TypeScript build configuration | D | retain edge configuration; eliminate unchecked vocabulary duplication |
| I-28 SDE methodology/experiment inputs | F | preserve extensibly; integrate only after experimental semantics stabilize |

| Component/capability | Disposition | Destination or retained boundary | Retirement/equivalence gate |
|---|---|---|---|
| work transition/evidence rules | A | `Ros.Domain.Work` | old/new fixture and shadow equality |
| artifact rules/registry projection | A | `Ros.Domain.Artifacts`, Application projection | byte-for-byte or approved deltas |
| execution/provenance/metric-quality rules | A | `Ros.Domain.Execution/Metrics/Provenance` | contract/golden/property parity |
| classification known values | A | `Ros.Domain.Classification` | extension-value round trip |
| all `ros` operations | B | `Ros.Cli` over Application | CLI JSON/text/exit compatibility |
| persistence and Git effects | B-supporting infrastructure | Application ports + `Ros.Infrastructure` | concurrency/fault/recovery and Git fixtures |
| root/starter launchers | C | compatibility shims | F# runtime distribution verified |
| package tasks | C | npm setup/invocation | deterministic clean packaging |
| GitHub triggers/setup/artifact/publish | C | workflow YAML | workflow-level test and rollback |
| Node HTTP/body/static server | D | local external adapter | versioned API contract; replace only if justified |
| TypeScript clients | D | browser adapter | contract/client tests; no need for F# rewrite |
| provider telemetry parsers | D initially | edge adapter -> observation contract | live versioned fixtures before relocation |
| npm bootstrap acquisition/materialization | D initially | npm adapter + manifests | distribution decision and upgrade plan |
| local project-admin hub | D/F | separate bounded context | identity/authority/reconciliation research |
| generated browser JS as ad hoc local state | E | deterministic build artifacts | clean-room package test |
| Python validator | E after oracle use | golden fixtures then remove | F# parity with Node and Python |
| legacy layout generator | E after deprecation | npm bootstrap replacement | usage search, warning window, parity |
| requirements sync | F | future bounded capability | source-of-truth and conflict policy |
| central administration | F | future integration/application | authority, identity, reconciliation contract |
| time entry | F | future projection | approved semantics and audit/reversal model |
| F# packaging/distribution | F | tool/binary/hybrid decision | consumer-platform experiment |

# 10. Proposed F# Architecture

## 10.1 Final bounded structure

```text
ROS
├── Ros.Domain
│   ├── Common                 typed IDs, timestamps, domain errors
│   ├── Artifacts              identities, lifecycle, relationships
│   ├── Work                   backlog/semantic states, transitions, obligations
│   ├── Execution              execution identity and lifecycle
│   ├── Provenance             source, scope, availability, lineage
│   ├── Metrics                definition, quality, capability, aggregation
│   ├── Evidence               typed references and satisfaction rules
│   └── Classification         stable known values + extension values
│
├── Ros.Contracts
│   ├── V1                     explicit DTOs and codecs
│   ├── Json                    preserved unknown fields and canonical encoding
│   ├── Cli                    stdout/error/exit envelopes
│   └── Schemas                checked published JSON Schemas
│
├── Ros.Application
│   ├── Commands               one use case per operation
│   ├── Workflows              coordinated units of work
│   ├── Validation             cross-aggregate/boundary findings
│   ├── Projections            registries, queue views, summaries
│   └── Ports                  repositories, transactions, Git, clock, IDs, adapters
│
├── Ros.Infrastructure
│   ├── FileSystem             repositories, atomic sets, locks, recovery
│   ├── Git                    one provenance implementation
│   ├── Serialization          front matter and explicit JSON codecs
│   ├── Processes              spoke/external command boundary
│   └── ExternalAdapters       file conformance and future services
│
├── Ros.Cli                    parsing, composition, output, exit codes
└── compatibility edges
    ├── npm bootstrap/launchers
    ├── Node HTTP + TypeScript clients
    ├── provider telemetry adapters
    ├── GitHub Actions
    └── separate Ros.ProjectAdministration application (later)
```

Dependency direction is `edge/CLI -> Application -> Domain`. Infrastructure implements Application ports and depends inward on contracts/domain as required. Domain has no reference to Infrastructure, CLI, providers, Git, GitHub, JSON, HTTP, npm, or project administration.

## 10.2 Modeling rules

Use discriminated unions for closed, stable choices such as `WorkState`, `ExecutionStatus`, `MeasurementQuality`, `CapabilityStatus`, `ArtifactLifecycle`, and explicit `Available | Clean | Unavailable of reason` provenance results. Use private single-case identifiers or validated value objects where accidental ID mixing is costly. Make transitions pure:

```text
decide : WorkPolicy -> WorkState -> WorkCommand -> Result<WorkDecision, DomainError>
evolve : WorkState -> WorkEvent -> WorkState
```

Records hold structured values; `option` replaces null/sentinel behavior inside the domain; application results distinguish validation, conflict, unavailable dependency, authorization, and unknown external outcome. Exhaustive matching should make a new stable state a compiler-visible change.

Do not turn the entire metric registry, provider payload, SDE methodology, requirements future, or time-entry speculation into closed unions. Stable rules become types and invariants; experimental observations remain structured extensible data. Known classification values may use `Known of KnownClassification | Extension of ExtensionId`, requiring `x-` extension names at boundaries.

## 10.3 Sources of truth

| Concern | Authoritative source | Rebuildable projection |
|---|---|---|
| canonical research/governance | Markdown records | registries and indexes |
| pre-execution obligations | work queue item | queue Markdown/detail view |
| active/completed work lifecycle | typed context state + accepted events | merged work views/status |
| execution facts | per-execution versioned record | summary/aggregate |
| metric semantics | versioned metric definitions | capability views/docs |
| repository configuration | validated versioned `ros.json` | effective configuration display |
| project membership | current hub registry until redesigned | hub Markdown/list |
| release version | `package.json` plus registry observation | pure release plan |

The migration must resolve whether events are authoritative state or audit facts. The current implementation treats context as current truth and events as chronology. Preserve that model initially rather than claiming event sourcing; add a recovery intent/journal only for atomic coordination.

## 10.4 Persistence and side effects

Application workflows should load versioned state, compute a decision, and commit a declared write set through one port. File infrastructure should provide:

- explicit resource/version checks;
- a documented lock order and stale-lock policy;
- temporary writes with fsync/rename where supported;
- either an intent journal or generation directory/pointer for multi-file commits;
- crash recovery that is safe to repeat;
- idempotency keys for externally repeatable commands;
- an operation/result record distinguishing committed, rejected, conflict, dependency unavailable, and external outcome unknown.

This does not require a database. A database becomes justified only if measured contention, record volume, querying, multi-host writing, or central reconciliation exceeds documented file-store limits.

## 10.5 Project administration and time entry

Do not place `ProjectAdministration` or `TimeEntry` inside the first `Ros.Domain` simply because they appear in the roadmap. The current hub owns local membership and delegates work truth to spokes. A future administration service may have distinct identity, authorization, reconciliation, and availability rules. It should consume public ROS contracts and can become `Ros.ProjectAdministration.Domain/Application/Infrastructure` if those rules stabilize.

Time entry should begin as a deterministic, reversible projection from accepted activity/execution records with explicit allocation, rounding, approval, correction, timezone, and provenance. It must not infer billable/legal facts directly from token or elapsed-time metrics. No such rules exist today, so early work is requirements/evidence collection, not domain encoding.

# 11. Interoperability and Serialization Contracts

Current callers are not all F# and future providers will evolve independently. The architecture therefore treats wire contracts as first-class artifacts.

## 11.1 Required contract families

- `WorkContextV1`, `WorkQueueV1`, `WorkEventV1`
- `ExecutionTelemetryV1` and `ObservationBatchV1`
- `AdapterRequestV1`, `AdapterResultV1`, `PublicationReceiptV1`
- artifact metadata/registry entries
- CLI success/error envelopes and release-plan output

Common required fields should include `schemaVersion`, typed textual IDs, timestamps with UTC semantics, lifecycle/status, repository/work/execution linkage, and provenance. Optional fields include provider/runtime/model identity, scope, quality, R&D context, links, and provider-specific metrics. Extension fields and unknown JSON members must survive read-modify-write unchanged unless an explicit migration says otherwise.

## 11.2 Compatibility policy

- Keep current `1.0` records readable throughout migration.
- Use explicit encoders/decoders; never let default F# serializer naming/union representation redefine a public contract.
- Additive optional fields are compatible minor changes. Required-field, meaning, or representation changes require a major schema plus lossless migration or an explicit rejected-record path.
- Retain bounded sanitized raw provider data separately from normalized comparable facts.
- Require metric source, scope, unit, quality, and capability state so absent is never encoded as numeric zero.
- Namespace provider extensions and `x-` experimental classifications.
- Version CLI JSON and keep stdout machine-readable; send diagnostics to stderr and preserve documented exit semantics.
- Test schema validation, old/new decoder compatibility, unknown-field round trips, canonical encoding/golden files, and migration idempotency.

The F# domain may have richer internal types than V1 DTOs. Mapping between the two is a boundary function whose errors are explicit and tested.

# 12. GitHub Actions Integration

The target pattern is:

```text
GitHub event
  -> permissions/concurrency/checkout/runtime/cache
  -> ROS CLI command
  -> Application + Domain
  -> validated JSON output/state/evidence
  -> GitHub-native artifact or npm publication step
```

Keep in YAML: event filters, permissions, concurrency, checkout depth, setup-dotnet/setup-node during coexistence, cache selection, artifact upload, OIDC configuration, `npm view`/`npm publish`, and mapping a small number of CLI JSON fields to GitHub outputs.

Move behind CLI boundaries:

- `ros ci validate --base-ref <sha>` for effective config, registry, state, schema, and base-aware checks;
- `ros release plan --before <sha> --run-number <n> --attempt <n>` returning a versioned decision such as `NoPublish`, `StableCandidate(version)`, or `SnapshotCandidate(version)` plus reasons;
- package/version/license/distribution validation and deterministic package inventory checks;
- eventually `ros execution capture-github-context` for provenance, without making the domain GitHub-specific.

The workflow should still perform the registry observation and publication because credentials and external side effects belong at the edge. Feed the observation back into a pure release-plan/finalization command as `Published`, `NotPublished`, or `Unknown`; do not interpret every `npm view` failure as “not published.”

A smallest practical validation job becomes checkout, setup the required runtime(s), restore/install distribution, run one `ros ci validate`, and upload its JSON evidence on failure. During migration, run Node and F# implementations in parallel but never dual-write the same state.

# 13. Staged Migration Strategy

## Stage 0 — Baseline and experiment registration

Freeze representative valid/invalid repositories, wire payloads, Git states, CLI outputs/exits, registries, and failure cases. Record static counts from this REP and collect a prospective baseline over either four weeks or ten accepted work items; choose and pre-register the window before seeing treatment data. Add clean-room package reproducibility and execution-time distributions.

**Stopping point:** migration may stop here with better tests/evidence and no production behavior change.

## Stage 1 — Distribution decision, solution skeleton, and contracts

Decide framework-dependent .NET tool versus self-contained per-platform binaries versus hybrid npm acquisition. Create architecture-enforced projects and explicit V1 DTO/codecs that round-trip the fixture corpus including unknown fields.

**Stopping point:** F# exists only as an independently runnable compatibility tool; current ROS remains authoritative.

## Stage 2 — First vertical slice: artifact validation and registries

Implement front-matter boundary parsing, pure artifact validation, deterministic registry projection, and `ros-fs registry build/check` plus validation output. Compare Node, Python, and F# against copied fixtures and the live repository without allowing F# to overwrite authoritative registries initially.

**Stopping point:** switch registry/validation reads to F# only after compatibility or documented intentional differences.

## Stage 3 — Persistence, recovery, and Git provenance seams

Implement the file repository/unit-of-work contract, operation IDs, locks/version checks, multi-file recovery, and one typed Git adapter. Add fault injection, process concurrency, NUL rename/copy, dirty baseline, missing Git, unborn branch, and ignored-path fixtures.

**Stopping point:** Node may call the new F# CLI for safe persistence/Git operations while work semantics remain Node.

## Stage 4 — Work lifecycle vertical slice

Move backlog identity/allocation, semantic transitions, evidence obligations, context/event decisions, and deterministic projections. Keep current launchers and HTTP server as compatibility edges. Shadow reads and commands on state copies, then switch one command family at a time.

**Stopping point:** all authoritative work mutation flows through Application/Core; Node adapter remains supported.

## Stage 5 — Execution and telemetry core

Move execution identity/lifecycle, capability/quality/provenance semantics, metric validation, normalization contracts, aggregation, privacy/retention policy, and summary projections. Keep provider mappings at the edge until versioned live fixtures demonstrate stability.

**Stopping point:** providers can still be Node/Python processes that produce `ObservationBatchV1`; core is provider-neutral.

## Stage 6 — Bootstrap, installation, and upgrade

Make initial state creation and schema migration executable Application commands. Update npm bootstrap to acquire/verify the selected F# distribution, invoke init/migrate/validate, record installed versions/checksums, and support inspect/plan/apply/verify/rollback. Remove package dependence on accidental ignored files.

**Stopping point:** new and existing consumer repositories have a documented, reversible upgrade path.

## Stage 7 — Thin GitHub workflows and release policy

Adopt `ros ci validate` and pure release planning; add a workflow harness or controlled branch/repository test for stable and snapshot cases. YAML retains credentials and publication.

**Stopping point:** GitHub YAML contains no ROS domain/release decision beyond invoking commands and mapping outputs.

## Stage 8 — Central project administration integration

Define repository/project identity, ownership, authorization, optimistic concurrency, idempotency, offline/online reconciliation, and event publication before replacing the local hub. Keep spoke ROS authoritative unless an accepted decision changes that rule.

**Stopping point:** local hub may coexist as a compatible client/adapter.

## Stage 9 — Derived time entry

After accepted activity records and administrative authority exist, research allocation/rounding/approval/correction rules and implement a deterministic projection with lineage and reversals. Never treat raw elapsed agent time as automatically billable time.

## Stage 10 — Evidence-gated retirement

Retire Node/Python paths capability by capability only after old/new equivalence, production observation, rollback drill, deprecation, documentation/manifest cleanup, and no unresolved high-severity regression. Keep a compatibility reader longer than the writer where necessary.

# 14. First Vertical Slice Recommendation

Select artifact validation and registry projection, not the largest script.

Why it is first:

- it is real ROS meaning, not glue;
- it has two independent implementations whose disagreement risk is already visible;
- input and output are deterministic and side effects are reconstructible;
- current tests and repository artifacts provide a rich fixture corpus;
- byte-for-byte projection comparison is feasible;
- it establishes F# project boundaries, error modeling, front-matter parsing, explicit JSON encoding, CLI compatibility, packaging, and CI without risking work/execution data;
- it removes a duplicate before the more experimental telemetry model is frozen.

Acceptance requires: every current valid fixture accepted; every current invalid fixture rejected with a stable machine-readable category; explicit adjudication of the `medium-high` schema discrepancy and all discovered Node/Python disagreements; canonical registry output equal by bytes or an approved versioned delta; unknown metadata behavior documented; dry-run cannot mutate; interrupted writes cannot leave a mixed projection set; current Node remains the rollback path.

Persistence/recovery is the next architectural slice even though artifact validation ships first. It addresses the highest systemic risk and creates the safe foundation for work lifecycle migration.

# 15. Testing and Behavioral-Equivalence Strategy

## 15.1 Test layers

- **Characterization:** capture current CLI arguments, stdout/stderr, exit codes, JSON shapes, files, ordering, timestamps/ID normalization rules, and known quirks before changing a capability.
- **Unit:** pure transition, validation, normalization, mapping, and projection functions.
- **Property-based:** illegal transition rejection; event evolution invariants; ID/path normalization; projection ordering; metric aggregation associativity only where semantically valid; serialize/decode round trip; unknown field preservation; retry idempotency.
- **Golden files:** canonical registries, context/queue/events/execution JSON, CLI result envelopes, release plans, and provider observation batches.
- **Contract:** validate all examples against JSON Schemas and both old/new codecs; read every supported historic version.
- **Integration:** real temporary repositories, Git rename/copy/dirty/unborn cases, file locks, crash recovery, bootstrap/upgrade, Node adapter to F# CLI.
- **Concurrency/fault:** simultaneous queue IDs, transition/finalization, hub/adapter publication; injected failure before/after each write and rename.
- **Workflow:** static lint plus controlled GitHub runs for PR base resolution, unchanged version, new stable, already-published stable, registry unavailable, snapshot, and rerun.
- **Security:** path traversal, symlink escape, multipart filename, oversized body, unauthenticated non-loopback bind, malicious repository path/output, raw secret redaction.
- **Old-versus-new:** run against isolated copies. Never let both implementations write one authoritative directory.

## 15.2 Compatibility adjudication

Observed behavior is not automatically correct. Each mismatch is labeled:

1. required compatibility;
2. documented bug with intentional corrected behavior and migration note;
3. undefined/unsupported behavior now rejected explicitly; or
4. experimental behavior retained behind an extension contract.

The original Node runtime remains the operational rollback until the slice passes fixtures, repository shadowing, an upgrade/rollback drill, and an observation period. The Python validator remains only until its independent cases are absorbed.

## 15.3 Test gaps to close before relevant migration

- full JSON Schema conformance and schema/code agreement;
- TypeScript compilation and API compatibility in root CI;
- clean-checkout deterministic `npm pack` contents;
- queue, hub, adapter-publication concurrency;
- multi-file crash/recovery fault injection;
- Git rename/copy parsing and Git-unavailable distinction;
- malicious hub filename/trust-boundary cases;
- workflow external outcome cases;
- versioned live provider fixtures;
- executable upgrade/migration rollback.

# 16. ROS Migration as an SDE Longitudinal Experiment

## 16.1 Experiment design

Use a prospective, pre-registered within-repository interrupted time-series with three phases: baseline, shadow/coexistence, and treatment/follow-up. The static baseline in Section 4 is supporting evidence only. Before treatment, choose either the next ten accepted comparable work items or four calendar weeks, define inclusion/exclusion rules, tag migration work, and freeze metric definitions. Match work by type/risk where possible and retain negative outcomes.

## 16.2 Hypotheses and falsifiers

| Hypothesis | Measure | Evidence that would weaken/falsify it |
|---|---|---|
| H1: one typed owner reduces duplicated semantic logic | count independent rule sites and files changed per semantic change | duplicate sites do not fall or adapter/core duplication grows |
| H2: typed transitions/contracts reduce invalid-state defects and silent omissions | compiler/boundary detections, invalid persisted states, validation escapes, repair loops | persisted invalid states/rework unchanged or worse |
| H3: staged shadowing preserves required behavior | golden/contract agreement, explicit deltas, rollback success | data loss, unexplained output drift, inability to revert |
| H4: centralization improves agent/human maintainability | accepted-item duration, handoff completeness, files inspected/touched, clarification/rework count | more cross-project navigation, higher rework, slower delivery without quality gain |
| H5: workflow thinning reduces operational fragility | YAML/script decision LOC, rerun outcomes, publication failures | policy merely moves into opaque CLI glue or external failures rise |
| H6: F# distribution cost is acceptable | cold/warm startup, install/package size, platform failures, bootstrap duration | portability/latency/size materially harms common consumer workflows |

## 16.3 Measures and provenance

Primary measures: semantic authority count; duplicated rule sites; change sites per accepted requirement; escaped P0/P1 state defects; validation findings by detection phase; rollback success; retry duplicates/conflicts; accepted-item lead time; rework/correction count; workflow decision LOC; packaging reproducibility; maintenance hotspot concentration.

Secondary measures: execution duration, test duration, CLI latency, bootstrap duration, package size, manual steps, handoff defects, agent tokens/cost. Tokens and cost are reportable only when capability and provenance are complete; otherwise label them `SELF-REPORT`, `HARNESS partial`, or `NOT OBSERVABLE`. Never substitute zero.

Sources include Git commits/diffs, versioned ROS work/execution records, test and validation JSON, CI artifacts, defect/work items, architecture-rule reports, and structured migration comparison results. Record metric definition version and collection capability with every observation.

## 16.4 Success and stop criteria

Per migrated capability:

- zero unexplained data loss;
- 100% characterization/golden compatibility or explicitly accepted versioned deltas;
- no P0/P1 state corruption during shadow/treatment;
- successful rollback drill;
- at least 30% reduction in independent semantic rule sites for that capability;
- no unsupported platform regression in the chosen distribution set;
- performance ceiling set from repeated baseline measurements before enforcement.

Pause or revise if F# adds more operational surfaces than it removes, unknown fields cannot round-trip, fallback is frequent, consumer installation becomes unreliable, or lead time rises materially without a corresponding quality improvement. Negative results are valid; do not tune implementation or inclusion rules to manufacture an SDE win.

# 17. Prioritized Conversion Queue

Scores are 1 (low) to 5 (high). Priority value is the sum of domain importance, defect risk, duplication, change frequency, state complexity, fragility, testability, F# benefit, and expected maintenance reduction, minus migration effort and external dependency risk. It is comparative, not a cost estimate.

| Candidate | Dom | Risk | Dup | Freq | State | Frag | Test | F# | Maint | Effort | Ext | Value | Queue note |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| persistence/recovery contract | 5 | 5 | 4 | 4 | 5 | 5 | 4 | 5 | 5 | 5 | 0 | 37 | highest systemic risk; second slice because first needs safer proof |
| work lifecycle/evidence | 5 | 5 | 4 | 5 | 5 | 4 | 5 | 5 | 5 | 5 | 2 | 36 | highest durable domain value |
| artifact validation/registry | 5 | 4 | 5 | 4 | 3 | 4 | 5 | 5 | 5 | 4 | 0 | 36 | first slice due determinism and duplicate removal |
| execution/telemetry core | 5 | 5 | 4 | 5 | 5 | 4 | 5 | 5 | 5 | 5 | 3 | 35 | high value, but protect experimental edges |
| Git provenance | 4 | 5 | 5 | 4 | 4 | 5 | 5 | 4 | 5 | 3 | 1 | 37 | implement with persistence seam |
| bootstrap/upgrade | 4 | 4 | 4 | 4 | 4 | 4 | 5 | 3 | 5 | 5 | 2 | 30 | distribution dependency |
| project-admin hub | 3 | 5 | 3 | 3 | 4 | 5 | 3 | 3 | 4 | 5 | 3 | 22 | security fix now; domain migration later |
| release planning | 3 | 4 | 2 | 3 | 3 | 4 | 4 | 4 | 4 | 3 | 4 | 24 | pure decision useful, external testing costly |
| HTTP/UI edge | 2 | 4 | 3 | 3 | 2 | 3 | 3 | 1 | 2 | 4 | 2 | 13 | secure/contract-test; do not rewrite for language consistency |
| requirements synchronization | 4 | 3 | 0 | 1 | 5 | 1 | 1 | 4 | 3 | 5 | 4 | 9 | research, not conversion; capability absent |
| time-entry projection | 4 | 4 | 0 | 1 | 5 | 1 | 1 | 4 | 3 | 5 | 5 | 8 | defer until administrative semantics exist |

The delivery sequence is not a simple descending sort: deterministic artifact validation establishes the architecture and toolchain first; persistence/Git then remove systemic risk; work and execution build on those boundaries.

# 18. Implementation-Ready Migration Backlog

## MIG-00 — Freeze baseline, fixtures, and SDE preregistration

- **Objective:** create language-neutral valid/invalid repositories, JSON/golden files, Git fixtures, CLI transcripts, package inventory, metric definitions, and the chosen prospective baseline window.
- **Rationale/dependencies:** prevents retrospective success criteria; no dependency.
- **Areas:** `tests/fixtures/`, research experiment/evidence records, CI artifact output.
- **Acceptance:** all existing Node/Python cases represented or explicitly implementation-specific; baseline inclusion, measures, and falsifiers accepted before F# treatment.
- **Tests/evidence:** current suites, fixture checksums, repeated timings, clean `npm pack` inventory.
- **Rollback:** additive research/test material only; remove F#-independent fixture wiring if disruptive.

## MIG-01 — Decide F# distribution and support matrix

- **Objective:** prototype and decide .NET tool, self-contained binaries, or hybrid npm acquisition for macOS/Linux/Windows and installed repositories.
- **Rationale/dependencies:** bootstrap/workflow design depends on runtime availability; MIG-00.
- **Areas:** decision record, spike artifacts, package/bootstrap tests.
- **Acceptance:** cold/warm startup, size, install/offline/update/checksum, version selection, and rollback measured; supported platform matrix explicit.
- **Tests/evidence:** clean-machine/container installs and consumer fixture runs.
- **Rollback:** keep Node authoritative and discard spike distributions.

## MIG-02 — Create architecture-enforced F# skeleton

- **Objective:** create Domain, Contracts, Application, Infrastructure, and CLI projects with dependency tests and no behavior switch.
- **Rationale/dependencies:** creates enforceable seams; MIG-01.
- **Areas:** new solution/projects, build/test tasks, CI shadow job.
- **Acceptance:** Domain references only FSharp.Core/approved pure dependencies; CLI runs version/help; architecture checks reject outward dependency.
- **Tests/evidence:** compile, unit/architecture tests, distribution smoke tests.
- **Rollback:** remove shadow-only projects; production Node untouched.

## MIG-03 — Implement explicit V1 contracts and compatibility codecs

- **Objective:** read/write current artifact/work/event/execution/adapter shapes while preserving unknown fields.
- **Rationale/dependencies:** every slice depends on non-breaking boundaries; MIG-00/MIG-02.
- **Areas:** `Ros.Contracts`, schemas, golden fixtures.
- **Acceptance:** all supported records decode; canonical encoders match or have approved deltas; unknown nested fields round-trip; errors are path-specific.
- **Tests/evidence:** schema, golden, property, old-version, fuzz/bounds tests.
- **Rollback:** codecs remain unused by production.

## MIG-04 — Migrate artifact validation and registry projection

- **Objective:** deliver the first end-to-end `ros-fs registry build/check` and artifact validation slice.
- **Rationale/dependencies:** deterministic duplicate removal and architecture proof; MIG-02/MIG-03.
- **Areas:** Domain Artifacts, Application Validation/Projections, front-matter/FS infrastructure, CLI.
- **Acceptance:** criteria in Section 14; registry set commit/recovery defined.
- **Tests/evidence:** Node/Python/F# matrix, byte goldens, malformed/reference/property/fault cases.
- **Rollback:** switch launcher/workflow back to Node; registries rebuild from canonical Markdown.

## MIG-05 — Establish transactional file persistence and recovery

- **Objective:** one versioned repository/unit-of-work boundary for queue, context, events, executions, registries, and receipts.
- **Rationale/dependencies:** addresses highest corruption risk; MIG-02/MIG-03, informed by MIG-04 writes.
- **Areas:** Application ports, Infrastructure FileSystem, recovery CLI/status findings.
- **Acceptance:** documented lock order/version/conflict semantics; injected failure at every commit phase recovers to old or new complete state; retry uses operation ID.
- **Tests/evidence:** multi-process concurrency, kill/fault injection, stale-lock and recovery matrix.
- **Rollback:** retain old persistence adapter and restore from automatically retained pre-commit generation.

## MIG-06 — Unify Git provenance

- **Objective:** replace both Git implementations with one typed adapter and explicit availability.
- **Rationale/dependencies:** fixes known rename/fail-open risk; MIG-02, preferably MIG-05.
- **Areas:** Infrastructure Git, provenance domain values, application validation/telemetry.
- **Acceptance:** clean differs from unavailable; base refs/renames/copies/binary/ignored/dirty/unborn/non-Git cases correct; no shell parsing.
- **Tests/evidence:** temporary Git repositories and byte/NUL fixtures.
- **Rollback:** feature flag/launcher routes back to Node Git path with warning.

## MIG-07 — Migrate work lifecycle and evidence

- **Objective:** put capture, ready/start/block/resume/complete/abandon, obligations, attachments metadata, events, and projections behind Application/Core.
- **Rationale/dependencies:** central stable ROS meaning; MIG-03/MIG-05/MIG-06.
- **Areas:** Domain Work/Evidence, Application Commands/Workflows, FS repositories, CLI compatibility.
- **Acceptance:** all legal/illegal transitions and required evidence match or have approved deltas; concurrent add has unique IDs; multi-item failure semantics explicit; HTTP/hub can invoke new CLI.
- **Tests/evidence:** characterization, transition property model, golden state, concurrency/fault, old/new shadow copies.
- **Rollback:** route launcher/HTTP back to Node and recover previous generation.

## MIG-08 — Migrate execution and telemetry core

- **Objective:** move execution lifecycle, provenance, capability/metric quality, normalization, aggregation, raw policy, classification core, and summaries.
- **Rationale/dependencies:** high-value durable capability after persistence/work stabilize; MIG-03/MIG-05-MIG-07.
- **Areas:** Domain Execution/Metrics/Classification, Application commands, contract codecs.
- **Acceptance:** current records readable; absence never becomes zero; idempotent snapshots/latest-session aggregation preserved; limits/privacy/unknown fields enforced; work finalization atomic with executions.
- **Tests/evidence:** all 24 cases ported/generalized, property/golden/limit/fault tests, shadow summaries.
- **Rollback:** Node telemetry records remain V1-compatible and writable after downgrade.

## MIG-09 — Formalize provider adapter boundary

- **Objective:** define `ObservationBatchV1`, convert current generic/Codex/Claude/OTel/hook mappings into replaceable adapters, and collect versioned live fixtures.
- **Rationale/dependencies:** prevents provider churn entering Core; MIG-03/MIG-08.
- **Areas:** Node or separate adapter processes, contracts, adapter conformance runner.
- **Acceptance:** unmapped leaves preserved/visible; units/scopes/provenance explicit; adapter version recorded; core changes not required for unknown provider metrics.
- **Tests/evidence:** official semantics plus pinned payload fixtures, conformance and cross-version drift tests.
- **Rollback:** existing Node adapter selected by name.

## MIG-10 — Migrate bootstrap and add upgrade lifecycle

- **Objective:** retain npm-friendly acquisition while delegating valid initial state and schema changes to F# init/migrate/verify operations.
- **Rationale/dependencies:** eliminates duplicated constructors and manual upgrades; MIG-01/MIG-03/MIG-05/MIG-07/MIG-08.
- **Areas:** bootstrap library/manifests, CLI migration commands, installation manifest.
- **Acceptance:** clean/collision/force behavior characterized; init writes one valid state generation; upgrades are plan/apply/verify/idempotent/reversible; checksums/version recorded; clean package is hermetic.
- **Tests/evidence:** clean consumers for both profiles, downgrade/rollback, interrupted upgrade, supported platforms.
- **Rollback:** previous npm package/bootstrap plus installation backup.

## MIG-11 — Thin validation and publication workflows

- **Objective:** make YAML invoke `ros ci validate` and `ros release plan`, leaving GitHub/npm mechanics outside.
- **Rationale/dependencies:** removes hidden release rules; MIG-01/MIG-04/MIG-10.
- **Areas:** root/template workflows, CLI commands, workflow fixtures/harness.
- **Acceptance:** unchanged/stable/already-published/snapshot/unknown registry/rerun cases tested; permissions minimal; output contract versioned.
- **Tests/evidence:** local pure plan tests plus controlled GitHub runs; satisfy or revise blocked latest-channel criterion.
- **Rollback:** restore previous workflow revision; no source-state migration.

## MIG-12 — Harden and contract the HTTP/UI edges

- **Objective:** point Node/TypeScript edges at versioned contracts, remove duplicated rules, and fix current security/retry gaps.
- **Rationale/dependencies:** retain useful UI simplicity without allowing drift; MIG-03/MIG-07, hub subset independent for urgent traversal fix.
- **Areas:** HTTP servers/body parser, TS/HTML, API schemas.
- **Acceptance:** loopback enforced unless explicit secured mode; basename/temp containment fixed; typed status mapping; request IDs for mutations; UI compiled/tested in CI; server advertises allowed operations/values.
- **Tests/evidence:** contract/client/security/retry/path tests.
- **Rollback:** CLI remains fully functional; prior UI can be disabled rather than exposing unsafe service.

## MIG-13 — Research and design project-administration bounded context

- **Objective:** decide project/repository identity, authority, permissions, offline reconciliation, portability, and central event contracts before migration.
- **Rationale/dependencies:** current hub is local convenience, not proven central architecture; MIG-03/MIG-07/MIG-09.
- **Areas:** new REP/decision; hub compatibility adapter; prospective separate application.
- **Acceptance:** source-of-truth/conflict/idempotency/security model accepted; no direct spoke file mutation; failure/reconciliation tested.
- **Tests/evidence:** multi-repo/offline/conflict/security simulations.
- **Rollback:** retain hardened local hub.

## MIG-14 — Research time-entry projection

- **Objective:** define whether and how accepted activity can produce proposed administrative time entries.
- **Rationale/dependencies:** capability absent and potentially policy/legal sensitive; MIG-08/MIG-13.
- **Areas:** REP/decision, prototype projection outside Core.
- **Acceptance:** input authority, allocation, rounding, timezone, approval, correction/reversal, privacy, and audit rules accepted; output always traceable and reviewable.
- **Tests/evidence:** deterministic examples, property tests for totals/rounding, corrections, rejected/blocked work, DST/timezone cases.
- **Rollback:** disable projection; source execution/activity records unchanged.

## MIG-15 — Retire legacy paths

- **Objective:** remove superseded Python validator, layout generator, Node domain kernel portions, stale manifests/docs, and accidental generated artifacts.
- **Rationale/dependencies:** captures maintenance benefit only after every relevant slice gate.
- **Areas:** files listed in disposition E and compatibility shims.
- **Acceptance:** deprecation window complete; no package/workflow/caller references; historical contracts still readable; rollback package archived; registries/validate/test pass.
- **Tests/evidence:** `rg` caller audit, clean consumer install, old-record fixtures, full CI and rollback drill.
- **Rollback:** republish/reselect last compatible package and preserved reader.

# 19. Risks and Unresolved Decisions

## Must resolve before the relevant stage

1. F# distribution/runtime/version-selection across installed repositories and three platforms.
2. Multi-file commit and recovery semantics, including event/context/execution authority.
3. Explicit contract/version policy and unknown-field representation.
4. Whether current CLI human text is a public compatibility surface or only JSON/exit codes are stable.
5. Project/repository identity and central administration authority.
6. Time-entry allocation, approval, correction, privacy, and legal/accounting boundaries.
7. Provider fixture licensing/privacy and adapter version support policy.

## Accepted transitional risks

- File storage remains local and transparent while transaction/recovery is formalized.
- Node/TypeScript HTTP and provider edges coexist with F#.
- Two runtimes increase temporary distribution complexity; each coexistence stage therefore has a stopping point and retirement gate.
- Custom front-matter behavior is characterized before deciding whether to keep or replace its YAML subset.

# 20. Architectural Self-Challenge

## Challenge 1 — Are the proposed projects just renamed script files?

The first draft mirrored “CLI, telemetry, hub, bootstrap.” Rejected. Final boundaries follow pure semantic rules, use cases, contracts, and side-effect ports. Hub/bootstrap/provider code stays at edges or separate contexts.

## Challenge 2 — Are future concepts being frozen too early?

The first draft included project administration and time entry inside generic `Ros.Domain`. Rejected. Neither is presently implemented as a stable ROS capability. Project administration becomes a future bounded context; time entry starts as an external, reviewable projection. SDE/provider observations retain extension data.

## Challenge 3 — Does stronger typing accidentally close an evolving ecosystem?

A union case for every provider/metric would require Core releases whenever vendors add data. Rejected. Core types only stable capability/quality/scope/provenance semantics. A versioned observation batch, metric registry, extension IDs, and unknown-field preservation absorb growth.

## Challenge 4 — Should F# own every serialization and interface?

Default serializer-generated formats and an immediate ASP.NET rewrite were rejected. Existing JSON, CLI, npm, HTTP, and TypeScript are interoperability surfaces. Explicit codecs/goldens protect them; HTTP/UI replacement needs independent evidence.

## Challenge 5 — Is a database/event store required for durability?

No. Current evidence shows inconsistent locking and partial-write risk, not scale that requires a database. A file unit-of-work and recovery protocol is the smaller solution. Events remain audit chronology rather than falsely claiming event sourcing.

## Challenge 6 — Should all workflow shell move into F#?

No. GitHub-native triggers, permissions, checkout, tool setup, output plumbing, and npm publication are clearer in YAML. Only ROS-specific release and validation decisions move to pure CLI commands.

## Challenge 7 — Should the largest/highest-value script migrate first?

No. Telemetry and work are stateful and telemetry is still evolving. Artifact validation/registry projection proves boundaries and compatibility with low mutation risk, then persistence/Git remove systemic risk before work/telemetry move.

## Challenge 8 — Is this solving observed problems?

The design maps to concrete duplicated validators, schema drift, a confirmed Git rename issue, fail-open Git results, uncoordinated writes, an upload traversal, workflow policy, absent client builds, and manual upgrades. Speculative central administration/time entry are explicitly deferred. A last review of partial failure, retry, future providers, portability, and rollback changed backlog detail but not boundaries; further revision had diminishing return.

# 21. Evidence Registry

- `EV-ROS-2026-A015`: repository-local inventory, counts, runtime/test baseline, direct observations, limitations, and reproduction commands.
- `JR-ROS-2026-A016`: chronological investigation and eight challenge cycles.
- Executable evidence: 84 Node and 7 Python tests passed; pre-report `./ros validate` and `./ros registry check` passed.
- Primary implementation evidence: all I-01 through I-28 paths and grouped contracts/configuration described above.
- Prior architecture evidence: `DF-ROS-2026-A010` and `RP-ROS-2026-A013` define the accepted provider-neutral telemetry direction that this migration preserves rather than freezes prematurely.

# 22. Hypothesis Registry

No separate `HY-` record is created yet because the experiment window and treatment have not begun. H1-H6 in Section 16 are candidate preregistered hypotheses for MIG-00. They must receive stable IDs, denominators, inclusion rules, and falsifiers before the first production behavior switches to F#.

# 23. Failed Assumptions

- All named future capabilities should be F# Core: rejected.
- Every Node/Python/TypeScript surface should be replaced: rejected.
- A database is necessary to solve the observed consistency risks: rejected.
- Provider payloads can share one closed model: rejected.
- Generated F# serialization is an acceptable public contract: rejected.
- Telemetry should migrate first because it is largest: rejected.
- Passing unit tests means workflow/package behavior is covered: rejected; workflow and clean-pack gaps remain.
- Empty Git results safely mean no change: rejected; unavailability requires an explicit state.

# 24. Open Questions

1. What consumer platform/runtime matrix must the F# distribution support, including offline use?
2. Which CLI textual outputs are parsed externally beyond the hub's JSON contract?
3. Should a multi-file commit use an intent journal or immutable generation plus pointer?
4. How long must V1 writers remain supported after a new schema, versus read-only compatibility?
5. What is the accepted source of truth if a future central administration service and offline spoke disagree?
6. Which activity facts are permissible inputs to a proposed time entry, and who approves them?
7. Which live provider payloads can be retained as fixtures without content/privacy leakage?
8. Is `medium-high` the intended confidence vocabulary, or should code/tests migrate to the published schema?

# 25. Recommended Next Research

Complete MIG-00 and MIG-01 before implementation. In parallel, resolve the `medium-high` contract discrepancy, design two competing file commit/recovery prototypes, and run clean consumer distribution experiments. Do not begin central administration or time-entry implementation; first write narrower research packages for their authority and policy models.

# 26. Research Backlog

1. File transaction strategy comparison: intent journal versus immutable generations.
2. Consumer distribution/platform experiment for .NET tool, self-contained, and hybrid npm.
3. Versioned provider fixture and privacy study.
4. Central project/repository identity and reconciliation model.
5. Time-entry policy and provenance study with administrative/accounting review.
6. GitHub workflow harness for publication outcomes.
7. Front-matter compatibility and YAML-library replacement study if custom parsing becomes a maintenance hotspot.

# 27. Suggested Specialized Research Agents

No subagents were used for this mission. Future execution can benefit from independent focused reviews by: an F#/.NET packaging engineer for MIG-01; a filesystem/crash-consistency engineer for MIG-05; an application-security reviewer for the hub/HTTP boundary; and an accounting/project-administration domain reviewer before MIG-14. Their findings should be separate evidence, not assumed independent if they share the same fixtures and model lineage.

# 28. Parallel Research Opportunities

Distribution spikes, file-transaction prototypes, provider fixture collection, workflow harness design, and hub threat modeling can proceed in parallel because they touch separate evidence/spike areas. Contract V1 decisions must converge before production slices. Work lifecycle implementation must wait for persistence/Git seams; time-entry work must wait for central activity/authority semantics.

# 29. Cross-Discipline Opportunities

Domain-driven design contributes bounded contexts and explicit invariants; functional programming contributes pure decisions and property testing; distributed-systems practice contributes idempotency and reconciliation; filesystem/database engineering contributes crash consistency; observability contributes provenance and capability-state semantics; security contributes path/process/authentication boundaries; software measurement contributes SDE construct validity; accounting and project administration must constrain any later time-entry projection.

# 30. Knowledge Relationships

```text
canonical Markdown -> artifact validation -> registry projection
work command -> domain decision -> unit of work -> context/event/execution evidence
provider payload -> edge adapter -> ObservationBatchV1 -> metric/provenance core
GitHub event -> YAML orchestration -> ROS CLI -> application decision -> external publish
spoke activity -> accepted administrative contract -> future deterministic time projection
```

`EV-ROS-2026-A015 -> this inventory/design -> MIG-00/MIG-01 -> contracts/first slice -> persistence/Git -> work/execution -> workflow/admin/derived systems`

# 31. Theory Impact Assessment

## Affected Theory Records

None. The mission produced an architecture recommendation, not a canonical theory change.

## Affected Engineering Principles

It strengthens: deterministic transformations; explicit uncertainty; provider-neutral boundaries; authoritative source/projection separation; state mutation through one tested seam; and preservation of simple ecosystem-native orchestration.

## New Principle Candidates

- A language migration should reduce semantic authorities, not only change their implementation language.
- Compatibility contracts must be independent of host-language default serialization.
- A local-file system earns a database only from measured scale/coordination needs, but must still define transactions and recovery.

## Deprecated Principles

None canonically. The implicit practice of treating schemas as documentation while code defines separate rules should be retired through an accepted decision.

## Confidence Changes

Confidence that Node contains the present application kernel rose to High after caller/state/test tracing. Confidence in a staged typed-core architecture is High. Confidence in specific future project-administration/time-entry models remains Low by design.

## Predictions Created

- The first slice will expose additional Node/Python/schema discrepancies.
- Centralized persistence/Git seams will remove more systemic risk than a direct telemetry port.
- Provider adapters will continue to add fields that should not require Core changes.
- Distribution/upgrade complexity will be the main counterweight to F# benefits.

## Predictions Invalidated

None through implementation because no migration code was authorized or created.

## Required Theory Registry Updates

None. Reconsider the principle candidates after Stage 4 supplies comparative evidence.

# 32. Research Quality Metrics

- **Repository components traced:** 28 inventory entries, including 16 authored executable sources and grouped executable/declarative surfaces.
- **Execution flows reconstructed:** 10.
- **Named tests executed:** 91, all passed at baseline.
- **Independent discovery methods:** 5.
- **Concrete high-risk discrepancies:** schema/code confidence vocabulary, duplicate Git path parsers with rename handling difference, non-transactional write paths, and unsafe hub temp filename.
- **Architecture challenge cycles:** 8.
- **Formal experimental hypotheses:** 6 candidates, not yet preregistered.
- **Confidence:** High for current repository behavior and staged boundary design; Medium/Low where future requirements are absent.
- **Coverage limitation:** no line/branch coverage, live workflow, external provider, publication, multi-host writer, or consumer fleet was exercised.

# 33. Research Debt

- **High:** no prospective SDE baseline or preregistered denominator.
- **High:** no crash-consistent multi-file prototype or failure injection.
- **High:** no live provider/version fixtures.
- **High:** F# consumer distribution has not been tested.
- **Medium:** workflow publication remains externally unexecuted in this repository harness.
- **Medium:** schema files are not runtime-enforced.
- **Medium:** no UI compilation/client contract gate in the default suite.
- **Medium:** short Git history makes change-frequency scores directional.
- **Low:** direct users of the legacy layout generator cannot be disproved from repository evidence.

# 34. Repository Updates

- Added `EV-ROS-2026-A015--operational-architecture-inventory-baseline.md`.
- Added `JR-ROS-2026-A016--fsharp-migration-architecture-investigation.md`.
- Added this REP.
- Required generated registries and ROS work/execution records are updated during closeout; no operational implementation was translated or removed.

# 35. Website Updates

None. The existing web clients were inspected, but this was an analysis/planning mission. The report recommends contract/security/test changes before any UI migration.

# 36. AI Consumption Notes

An implementing agent should begin with MIG-00, not create projects immediately. Treat current Node behavior, fixtures, schemas, and CLI output as evidence requiring adjudication, not as automatically correct specifications. Do not modify authoritative `.ros` state with both runtimes. Preserve unknown JSON fields. Keep project administration/time entry out of the initial generic Domain. Record every behavior difference as compatible, corrected bug, explicitly unsupported, or experimental extension.

# 37. Handoff Instructions

1. Read this REP, `EV-ROS-2026-A015`, `JR-ROS-2026-A016`, `DF-ROS-2026-A010`, and current work/telemetry docs.
2. Inspect current Git status because this mission began amid pre-existing telemetry changes.
3. Capture a new external work item for MIG-00 and begin it through `./ros work begin`.
4. Freeze fixtures and preregister H1-H6 before switching production behavior.
5. Make MIG-01 an evidence-backed distribution decision.
6. Keep Node authoritative through MIG-03; expose F# as `ros-fs` or an explicit shadow command.
7. After every slice, run old/new on isolated copies, execute rollback, rebuild registries, and validate.

# 38. Research Journal

The detailed chronology and design revisions are in `JR-ROS-2026-A016`. The key sequence was governance/baseline, broad discovery, complete source/config/test tracing, execution-flow reconstruction, baseline execution, capability/risk classification, target design, repeated failure-oriented challenge, and implementation-backlog synthesis.

# 39. Appendix A — Environment and Secrets Matrix

| Variable/credential | Consumer | Meaning | Required? |
|---|---|---|---|
| `ROS_BASE_REF` | I-07/workflows | comparison base for meaningful path/Git validation | optional locally; set in CI |
| `ROS_ACTOR` | I-07/I-08 | work creator/execution agent identity fallback | optional |
| `ROS_TELEMETRY_PROVIDER` | I-08 | provider identity | optional |
| `ROS_TELEMETRY_RUNTIME` | I-08 | runtime identity | optional |
| `ROS_TELEMETRY_MODEL` / `_MODEL_VERSION` | I-08 | model identity | optional |
| `ROS_TELEMETRY_RUNTIME_VERSION` | I-08 | runtime version | optional |
| `ROS_TELEMETRY_SESSION_ID` / `_CONVERSATION_ID` / `_RUN_ID` | I-08 | correlation identities | optional |
| `CODEX_SESSION_ID` / `CODEX_THREAD_ID` | I-08 | Codex discovery | optional |
| `CLAUDE_CODE_SESSION_ID` | I-08 | Claude discovery | optional |
| `GEMINI_SESSION_ID` | I-08 | Gemini discovery | optional |
| `COPILOT_SESSION_ID` | I-08 | Copilot discovery | optional |
| `GITHUB_ACTIONS` / `GITHUB_RUN_ID` | I-08 | Actions discovery/run identity | optional |
| `OLLAMA_HOST` | I-08 | Ollama runtime discovery | optional |
| `GITHUB_OUTPUT` / run number / attempt | I-20 | workflow output and snapshot version | provided by Actions |
| GitHub token/OIDC npm trust | I-18-I-20/actions | checkout and package publication | platform-provided; publish needs configured trust |

No operational source directly reads an application secret. Provider payloads may contain sensitive values, which is why bounded sanitization and upstream content suppression remain required.

# 40. Appendix B — Removal Risk Summary

- **Critical if removed now:** I-01/I-02, I-05, I-07, I-08, I-18/I-20, current configs/contracts/metric registry.
- **High:** I-06, HTTP/body/browser assets for users relying on UI, hub components for project-admin profile.
- **Medium:** Python validator because it is a useful independent oracle, generated JS because current packaging/runtime expects build output, starter validation workflow.
- **Low but uncertain:** legacy layout generator; evidence supports deprecation, not immediate deletion.

# 41. Completion Checklist

- [x] Complete broad inventory, including hidden configuration and generated/runtime surfaces.
- [x] Responsibility, callers/callees, I/O, environment, side effects, failure/retry/idempotency, tests, domain/state, activity, risk, confidence, and disposition recorded.
- [x] Major execution flows and sources of truth reconstructed.
- [x] Behavior-based capability map completed.
- [x] Duplication and accidental architecture ranked.
- [x] Every component classified A-F.
- [x] Target F# boundaries and dependency direction defined.
- [x] GitHub orchestration boundary defined.
- [x] Versioned interoperability approach defined.
- [x] Staged, reversible migration with stopping points defined.
- [x] First vertical slice selected and justified.
- [x] Characterization, property, integration, workflow, security, and equivalence testing specified.
- [x] SDE longitudinal experiment and falsifiers defined.
- [x] Conversion candidates scored and sequenced.
- [x] Architecture challenged until further review changed backlog detail rather than boundaries.
- [x] Implementation-ready backlog with acceptance, evidence, dependencies, and rollback produced.
- [x] No current script was mechanically translated, retired, or disabled.
