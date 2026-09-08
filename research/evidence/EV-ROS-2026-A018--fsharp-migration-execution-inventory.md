---
id: EV-ROS-2026-A018
title: ROS F# migration execution inventory at immutable baseline
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-07
updated: 2026-09-07
research_area: repository-operating-system
evidence_type: primary
supports:
  - EX-ROS-2026-A020
related_documents:
  - EV-ROS-2026-A015
  - RP-ROS-2026-A017
  - JR-ROS-2026-A019
  - DF-ROS-2026-A027
supersedes: []
superseded_by: []
tags: [fsharp, migration, inventory, execution, baseline, sde]
confidence: high
---

# Evidence summary

This is the canonical execution inventory for the implementation mission that
began from commit `6a188073474f5088decf9617f4539625e4bdb451` on 2026-09-07.
It rechecked every entry in the earlier architecture inventory against the
immutable tag `ros-fsharp-migration-baseline-20260907-202926` and corrects the
specific inaccuracies listed below. The inventory covers 28 production,
configuration, generated, and platform execution units plus six executable
verification units. No implementation source changed before this record and
the semantic decomposition in `DF-ROS-2026-A027` were frozen.

# Baseline and collection

| Field | Measured value |
|---|---|
| Repository identity | `repository-operating-system` |
| Remote | `origin https://github.com/kemiller2002/Repository-Operating-System.git` |
| Starting branch | `main` |
| Starting SHA | `6a188073474f5088decf9617f4539625e4bdb451` |
| Migration branch | `migration/ros-fsharp-application` |
| Immutable baseline tag | `ros-fsharp-migration-baseline-20260907-202926` (pushed) |
| Starting worktree | clean |
| Runtimes | Node `25.6.0`; npm `11.8.0`; .NET SDK `10.0.100` |
| Baseline verification | 84 Node tests and 7 Python tests passed; both TypeScript builds passed |
| Production source | 12 authored Node/JavaScript files, two Python files, two TypeScript clients |
| Executable-bit tracked files | `ros`, `bin/ros-bootstrap.mjs` |
| Shebang-bearing authored files | nine launchers/scripts, including copied launchers and both Python tools |
| Workflows | three YAML declarations, 150 lines total |
| Contracts/catalogs | 12 JSON schemas; 115 telemetry metric definitions |
| Profile plans | 84 greenfield entries; 63 project-administration entries |
| Hooks/task runners | no active Git hook, Makefile, Justfile, Taskfile, container entrypoint, shell file, or PowerShell file found |

Discovery used tracked/all-file enumeration, executable-bit and shebang scans,
extension scans, imports/exports, package scripts, workflow steps, profile
manifests, schemas, documented command references, direct callers, test
callers, Git history, state-store paths, environment access, subprocess calls,
and network boundaries. Direct external use cannot be disproved; therefore an
apparently uncalled file is deferred or deprecated rather than declared unused.

# Inventory field convention

Every entry records language/type and entry point; callers and dependencies;
inputs, reads, outputs, writes, environment/secrets, Git/network/subprocess
behavior; exit/error/retry/idempotency/concurrency/side effects; state and
semantic responsibility; duplication, tests, observability, failure and
platform constraints; and a migration classification. “None” means none was
found in source. File lists name authoritative or material families rather than
every static asset copied by a manifest.

# Launch and installation units

## I-01 — `ros`

- **Type/entry/callers/dependencies:** three-line executable POSIX Node ESM
  launcher; humans, agents, tests, workflows, servers, and hub spokes call it;
  it imports `main` from `tools/ros_cli.mjs`.
- **I/O and effects:** argv/cwd in; delegated stdout/stderr/exit out. It reads,
  writes, invokes Git/network/subprocesses, and consumes env/secrets only through
  the kernel. No direct state or side effect.
- **Operation:** dispatch is deterministic and retry behavior is the selected
  command's. Import/runtime errors propagate. Tested throughout Node suites and
  highly observable by exit/output; POSIX executable mode and Node are required.
- **Responsibility/classification:** compatibility entry point, no semantic
  authority. **KEEP AS THIN ADAPTER** because callers require a stable launcher.

## I-02 — `starter/greenfield/ros`

- **Type/entry/callers/dependencies:** installed three-line Node launcher;
  humans, agents, installed CI, and hub calls import installed
  `tools/ros_cli.mjs`.
- **I/O and effects:** same delegated argv/cwd/output/exit and indirect effects
  as I-01; no direct files, env, secrets, Git, network, or subprocess.
- **Operation:** deterministic dispatch, behavior-specific idempotency; package
  and bootstrap tests assert installation and executable mode. Requires Node
  and the profile-relative layout.
- **Responsibility/classification:** deliberate copy of I-01. **KEEP AS THIN
  ADAPTER** until the installed runtime distribution has comparative evidence.

## I-03 — `starter/project-administration/ros-hub`

- **Type/entry/callers/dependencies:** installed Node launcher; operators and
  npm aliases call it; imports `tools/ros_hub_cli.mjs`.
- **I/O and effects:** argv/cwd to delegated JSON/text/exit; no direct files,
  env, secrets, Git, network, subprocess, state, or retry.
- **Operation:** deterministic dispatch; hub/bootstrap integration tests cover
  it. Requires Node and the installed hub layout.
- **Responsibility/classification:** project-administration compatibility edge.
  **KEEP AS THIN ADAPTER**; this bounded context is not folded into ROS core.

## I-04 — `bin/ros-bootstrap.mjs`

- **Type/entry/callers/dependencies:** five-line executable npm bin; `npx`, npm
  global/tool execution, and packaging tests call it; delegates to
  `lib/bootstrap.mjs` `main`.
- **I/O and effects:** argv/cwd in, delegated output/exit and filesystem effects
  out; no direct env, secrets, Git, network, or subprocess.
- **Operation:** errors propagate from the library; no retry or telemetry;
  bootstrap tests exercise the packaged binary. Requires Node/npm acquisition.
- **Responsibility/classification:** ecosystem acquisition edge. **KEEP AS THIN
  ADAPTER** because npm is a mature distribution capability ROS should not
  replace without portability evidence.

## I-05 — `lib/bootstrap.mjs`

- **Type/entry/callers/dependencies:** Node installation application with
  `initializeProject`, `verifyProject`, and CLI `main`; called by I-04 and 14
  bootstrap tests; depends on package metadata, profile manifests, templates,
  filesystem, path, and crypto.
- **I/O/state/effects:** target/project/profile/force/dry-run and cwd in; reads
  `package.json`, `starter/{greenfield,project-administration}/manifest.json`,
  sources, target collisions, and prior installation metadata; writes declared
  profile files, modes, `.ros/installation.json`, and initial context/event/
  queue/hub state. It uses `ROS_VERSION` only as a render variable, no secrets,
  Git, network, or subprocess.
- **Operation:** `0` on init/verify/help success, `1` on caught failure;
  collision preflight and safe destination guards; verifies checksums/modes;
  cleanup covers newly written declared files but not a cross-file transaction.
  Dry run/verify are repeatable; force may overwrite. Human logs only, no
  telemetry. Platform constraint is Node and writable local filesystem.
- **Responsibility/classification:** package materialization plus duplicated
  initial-state construction. **KEEP AS THIN ADAPTER** for acquisition, while
  later **MIGRATE TO F# COMMAND** the valid initial/upgrade state decisions.
  Contrary to A017, this file does not spawn installed `./ros validate`; it
  prints registry/validation commands as the next step.

# Current application kernel and boundaries

## I-06 — `tools/ros_persistence.mjs`

- **Type/entry/callers/dependencies:** Node helper exporting `readJson`,
  `writeJson`, `writeTextAtomic`, and `withFileLock`; work/telemetry kernels call
  it; depends only on filesystem/path/process/random identity.
- **I/O/state/effects:** file/resource paths in; JSON/text or callback result
  out; reads targets and lock metadata; writes same-directory temporary files,
  atomic renames, and transient `.ros/locks/*.lock`. No env, secrets, Git,
  network, or subprocess.
- **Operation:** malformed/I/O errors propagate; lock retries up to ten seconds
  and reclaims a proven stale/dead owner; writes are file-level idempotent, not
  multi-file transactions. Per-resource serialization only. Lock failures are
  observable as errors; concurrency tests cover acquisition/recovery.
- **Responsibility/classification:** effect discipline, not domain semantics;
  queue, bootstrap, registry, and hub paths duplicate or bypass it. **MERGE WITH
  ANOTHER RESPONSIBILITY** as an infrastructure filesystem/lock adapter behind
  typed application ports.

## I-07 — `tools/ros_cli.mjs`

- **Type/entry/callers/dependencies:** 1,282-line Node CLI/application kernel;
  I-01/I-02, HTTP, hub spokes, workflows, tests, and humans call it. Imports
  persistence/telemetry and invokes Git through synchronous child processes.
- **Commands:** `validate [--json]`; `status`; `registry build [--dry-run]` and
  `check`; `add`; work `list`, `ready`, `show`, `start`, `block`, `abandon`,
  `update`, `attach`, `begin`, `resume`, `complete`/`done`, `context`; telemetry
  dispatch listed in I-08; adapter `call` and `publish`. Unknown syntax returns
  `2`; semantic/operational failure returns `1`; success returns `0`; adapter
  call also uses `2` for an explicit unknown outcome.
- **I/O/state/effects:** argv/options/stdin and repository root in; reads
  `ros.json`, Git, canonical artifacts, registries, work/context/event/
  attachment/adapter/publication/telemetry files; writes queue JSON/Markdown,
  item details/attachments, context, event JSONL, registries, adapter stores and
  publication receipts/streams. Uses `ROS_BASE_REF` and `ROS_ACTOR`; no direct
  secret or network. Git subprocesses are read-only.
- **Operation:** top-level exceptions are explicit; event/request IDs provide
  partial idempotency, while capture and some transitions are not blindly
  retryable. Work transitions lock; queue ID allocation and multi-file effects
  are not transactional. JSON/text/errors provide observability, telemetry is
  integrated. Tests: work, artifact, server, hub, bootstrap, and telemetry
  integration. Git failures sometimes collapse to null/empty; rename parsing
  and partial writes are failure risks. Requires Node, local Git/filesystem.
- **Responsibility/classification:** owns artifact policy/projection, work and
  adapter states, transition/evidence guards, Git attribution, orchestration,
  and CLI contracts; duplicates Python/schema/docs/UI/telemetry helpers.
  **MIGRATE TO F# CORE** for stable decisions and **MIGRATE TO F# COMMAND** for
  use cases, one characterized vertical slice at a time; retain this authority
  until parity and an explicit switch.

## I-08 — `tools/ros_telemetry.mjs`

- **Type/entry/callers/dependencies:** 1,537-line Node telemetry kernel called
  automatically by work transitions and explicitly through telemetry
  `start`, `ingest`, `record`, `classify`, `finalize`, `show`, `summary`, and
  `adapters`; imports persistence and invokes Git.
- **I/O/state/effects:** configuration, metric catalog, work context, provider
  JSON/JSONL/stdin, environment identity, Git and repository files in; writes
  locked atomic `.ros/telemetry/executions/*.json` and context backlinks.
  Identity env includes `ROS_TELEMETRY_*`, `ROS_ACTOR`, supported provider
  session IDs, `GITHUB_ACTIONS`, `GITHUB_RUN_ID`, and `OLLAMA_HOST`. It records
  metadata but must not consume secrets; no live network. Git is read-only.
- **Operation:** CLI success/failure is `0`/`1`; lifecycle state is `active` or
  `finalized`; snapshot/measurement IDs deduplicate; repeated finalization is
  safe; per-execution/index locks serialize local writers; context+execution is
  not transactional. Explicit errors cover malformed/oversize/invalid data;
  Git degradation is recorded as unavailable. Records themselves are
  observability. Twenty-four telemetry tests plus work integration cover
  adapters, privacy, state, aggregation, concurrency, and failure. Platform is
  Node/local Git/filesystem; provider capabilities vary.
- **Responsibility/classification:** execution identity/lifecycle, metric and
  capability semantics, provenance/quality, classification, aggregation, and
  provider mappings; duplicates Git/path/JSON logic. **MIGRATE TO F# CORE** the
  stable model/decisions and **MIGRATE TO F# COMMAND** lifecycle use cases;
  **KEEP AS THIN ADAPTER** provider mappings until their contracts stabilize.

## I-09 — `tools/http_body.mjs`

- **Type/entry/callers/dependencies:** dependency-free Node HTTP stream,
  JSON, and multipart parser; both servers call it.
- **I/O/effects:** request headers/body/limits in, parsed JSON or fields/files
  buffers out; no filesystem, env, secrets, Git, network client, subprocess, or
  authoritative state. Request content may be sensitive.
- **Operation:** deterministic; rejects missing/unsupported content types,
  malformed input, and byte-limit excess; no retry/metrics. Covered by server
  integration. Depends on Node HTTP stream behavior.
- **Responsibility/classification:** generic edge parsing with no ROS decision.
  **KEEP AS THIN ADAPTER** while Node HTTP exists; replace only with platform
  middleware if that host changes.

## I-10 — `tools/ros_server.mjs`

- **Type/entry/callers/dependencies:** Node HTTP/static adapter; `npm run web`,
  the repository browser, and 13 server tests call it; imports I-07 and I-09.
- **I/O/effects:** host/port/root, HTTP JSON/multipart/query in; JSON/bytes/status
  out; reads static `web/` and attachments; all authoritative mutations are
  delegated. No env, secrets, direct Git, external network client, or child
  process; it listens on local HTTP, default `127.0.0.1:4310`.
- **Operation:** most handler errors map to 400, missing resources to 404;
  create requests lack retry keys; startup warning only, no access metrics.
  Tests cover routes, limits, traversal, attachments, and parity. Binding beyond
  loopback is an unauthenticated trust-boundary risk.
- **Responsibility/classification:** transport/presentation adapter with some
  error-mapping policy. **KEEP AS THIN ADAPTER** and point it to stable typed
  use cases; do not rewrite for language percentage.

## I-11 — `web/app.ts`

- **Type/entry/callers/dependencies:** TypeScript browser entry loaded by
  `web/index.html`; calls I-10 HTTP endpoints and browser DOM/fetch APIs.
- **I/O/effects:** DOM/form/file/API data in; DOM and HTTP mutations out; no
  filesystem/Git/subprocess/secret; local network only. Persistent state belongs
  to server. Errors display to user; no retry key, logs, or telemetry.
- **Operation:** render is deterministic for input state; direct UI tests are
  absent and root `npm test` does not compile it. Browser/compiled-JS presence
  is required. Embedded status/type/action choices can drift.
- **Responsibility/classification:** presentation plus duplicated vocabulary.
  **KEEP AS THIN ADAPTER**; make allowable actions/options contract-driven.

## I-12 — generated `web*/app.js` and maps

- **Type/entry/callers/dependencies:** ignored JavaScript/source-map outputs of
  the two TypeScript compilers; HTML/browser loads JavaScript.
- **I/O/effects:** TypeScript/config in, generated files out; no independent
  state, env, secrets, Git, network, subprocess, exit, retry, or semantics.
  `tsc` overwrites deterministically, while absent/stale output breaks UI and
  local presence changes npm package contents.
- **Operation:** compilation was separately verified at baseline; freshness is
  not in `npm test` and outputs are ignored/untracked.
- **Responsibility/classification:** generated platform artifact. **REPLACE WITH
  EXISTING PLATFORM CAPABILITY** by deterministic TypeScript build/package
  handling; never port generated code.

## I-13 — `tools/ros_hub_cli.mjs`

- **Type/entry/callers/dependencies:** 258-line Node hub kernel/CLI; I-03,
  server/UI, package task, and eight hub tests call it; spawns each registered
  repository's `./ros` synchronously.
- **Commands/I/O:** `register`, `unregister`, `repos`, `create`, and `work`;
  root/path/repository/filter/work data in; JSON/text/exit out. Reads/writes
  `.ros/hub/registry.json` and derived Markdown; reads spoke `ros.json`; mutates
  spoke work only through subprocess CLI. No env, secrets, Git, or external
  network.
- **Operation:** success/usage/failure exits `0`/`2`/`1`; registration replaces
  by repository ID, creation is not retry-idempotent, aggregation retains
  per-repo errors. Registry writes are direct/unlocked/non-atomic; subprocess
  stderr is observable. Tests cover registration, aggregation, routing, server,
  and installed spoke. Requires Node, local paths, compatible spoke JSON CLI.
- **Responsibility/classification:** project registry/facade, not ROS work
  authority; duplicates parsing/persistence/projection. **DEFER — INSUFFICIENT
  EVIDENCE** for F# ownership and **KEEP AS THIN ADAPTER** to the spoke contract.

## I-14 — `tools/ros_hub_server.mjs`

- **Type/entry/callers/dependencies:** Node HTTP/static adapter called by
  `npm run hub`, hub UI, and tests; imports I-09/I-13 and uses temporary files.
- **I/O/effects:** host/port/root plus HTTP JSON/multipart in; HTTP out; reads
  `web-hub/`, writes/removes temporary uploads, delegates hub/spoke state. No
  env, secrets, Git, or external client; local listener defaults to 4320 and
  indirect spoke subprocesses occur through I-13.
- **Operation:** handler failures generally 400; no retry/idempotency/logging;
  best-effort cleanup. Tests cover normal routes/upload but not malicious names.
  Unauthenticated wider binding and unsanitized multipart filenames are known
  boundary failures.
- **Responsibility/classification:** transport edge. **KEEP AS THIN ADAPTER**;
  remediate security separately before broader exposure.

## I-15 — `web-hub/app.ts`

- **Type/entry/callers/dependencies:** TypeScript browser entry from hub HTML;
  uses DOM/fetch and I-14 API.
- **I/O/effects:** forms/files/API in; DOM/HTTP out; no direct persistent file,
  env, secrets, Git, or subprocess. Errors are visible; no retry/audit metrics.
- **Operation:** no direct client tests; hub integration covers a subset;
  browser and compiled output required; embedded vocabulary can drift.
- **Responsibility/classification:** presentation only. **KEEP AS THIN ADAPTER**
  and consume explicit capability/contracts.

# Legacy and independent tools

## I-16 — `tools/ros_cli.py`

- **Type/entry/callers/dependencies:** 348-line Python CLI implementing
  `validate`, `build`, and `check`; direct users and seven Python unit tests call
  it. No production launcher/import/package caller was found.
- **I/O/effects:** root/artifact Markdown/registries in; diagnostics/exit and
  registry JSON writes out. No env, secrets, Git, network, or subprocess.
- **Operation:** success/failure/usage exits follow Python CLI convention;
  deterministic projection and repeatable build; parse/I/O failures explicit.
  Independently tested, Python required.
- **Responsibility/classification:** duplicates Node artifact semantics and
  currently serves as an independent oracle. **DEPRECATE** only after fixtures
  and F# differential parity; then **DELETE AFTER VERIFIED UNUSED**.

## I-17 — `setup_ros_layout.py`

- **Type/entry/callers/dependencies:** 1,095-line executable Python 3 legacy
  layout generator; only its own documented direct invocation was found; all
  templates are embedded.
- **I/O/effects:** repository path/force/dry-run in; stdout/exit and a ROS 1.0.0
  tree out. Reads target collisions; writes/overwrites many directories/files.
  No env, secrets, Git, network, subprocess, transaction, rollback, tests, or
  telemetry. Dry run/default collision guard is repeatable; force is destructive
  and interruption can leave partial state. Requires Python/local filesystem.
- **Responsibility/classification:** obsolete scaffold duplicated by npm
  bootstrap. Unused helpers `write_file` and `ensure_gitkeep` are dead;
  `write_file` would mislabel a newly created file as `overwrite`, but is not
  called. **DEPRECATE** now and **DELETE AFTER VERIFIED UNUSED** only after a
  documented compatibility window because unknown direct consumers may exist.

# Platform declarations and executable inputs

## I-18 — `.github/workflows/ros-validation.yml`

- **Type/entry/callers/dependencies:** GitHub Actions YAML on push/PR; checkout,
  Node 22, Python 3.11, `npm test`, and base-aware `./ros validate`.
- **I/O/effects:** GitHub ref/event/repository in; hosted logs/check conclusion
  out; network downloads actions/runtimes/dependencies at platform setup;
  checkout token only, no custom secret; validation invokes read-only Git via
  ROS. Runner state is disposable, no persisted repo writes.
- **Operation:** step failure stops job; rerun is safe; GitHub supplies
  concurrency. No local workflow harness or structured ROS telemetry.
- **Responsibility/classification:** platform orchestration, no domain rules.
  **KEEP AS PLATFORM DECLARATION**. Contrary to A017, it does not run `npm ci`.

## I-19 — `starter/greenfield/.github/workflows/ros-validation.yml`

- **Type/entry/callers/dependencies:** installed GitHub push/PR workflow;
  checkout and Node 22 setup, then `./ros registry check` and base-aware
  `./ros validate`.
- **I/O/effects:** GitHub refs in; logs/check out; action/runtime network at
  setup, checkout token only, ROS read-only Git indirectly, no persistent write.
- **Operation:** fail-fast and safe rerun; manifest tests inspect installation,
  but no hosted execution harness/telemetry. GitHub/Linux/Node required.
- **Responsibility/classification:** **KEEP AS PLATFORM DECLARATION**. Contrary
  to A017, it sets up neither Python nor tests and has no package conditional.

## I-20 — `.github/workflows/publish.yml`

- **Type/entry/callers/dependencies:** GitHub main-push workflow; checkout,
  Node/Python setup, global npm 11 install, license/test/ROS gates, Git version
  comparison, npm registry lookup, stable publish, snapshot versioning/publish.
- **I/O/effects:** GitHub before/SHA/run fields, Git/package metadata, npm
  registry in; logs/outputs and npm stable/`main` publications out. Uses
  `GITHUB_OUTPUT`, read-only Git subprocesses, network and OIDC trusted
  publishing (`id-token: write`), transient `package.json`/lock changes in the
  runner. No repository commit.
- **Operation:** fail-fast; concurrency group does not cancel; stable lookup
  prevents known duplicate publish and run/attempt makes snapshots retry-unique,
  but registry outage may be misread as absence. Textual policy assertions only;
  hosted path untested locally. Requires GitHub/Linux/Node/Python/npm registry.
- **Responsibility/classification:** GitHub/npm mechanics are **KEEP AS PLATFORM
  DECLARATION**; version eligibility/planning is **MIGRATE TO F# COMMAND** only
  when a characterized slice reaches release semantics. Contrary to A017, the
  workflow does not run `npm ci`.

## I-21 — root `package.json` and npm scripts

- **Type/entry/callers/dependencies:** npm manifest/task runner called by humans,
  CI, packaging, and npm lifecycle; scripts are `test`, `build:web`,
  `build:hub`, `web`, `hub`, `pack:inspect`, `release:check`, and `prepack`.
- **I/O/effects:** source/config in; process logs, ignored compiled JS, servers,
  and dry-run/package output. Invokes Node test runner, Python unittest, `tsc`,
  Node servers, npm pack, and ROS CLI. Network is needed by install/publish, not
  these task bodies; no custom secrets/Git. Shell chaining is fail-fast; build
  repeatable; no structured metrics.
- **Operation:** Node/npm/Python/.NET-independent today; test omits TS builds.
  Package is version `1.2.1`, but both root lockfile version fields remain
  `1.1.1`, a distribution drift discovered at T1.
- **Responsibility/classification:** ecosystem glue and distribution topology.
  **KEEP AS THIN ADAPTER**; tasks may compose F# gates without owning semantics.

## I-22 — `starter/project-administration/package.json`

- **Type/entry/callers/dependencies:** installed npm task manifest with
  `build:web`, `build:hub`, `web`, and `hub`; operators call TypeScript and Node
  servers.
- **I/O/effects/operation:** source to ignored JS/server listeners/logs; no
  direct authoritative state, env, secrets, Git, or network client. Builds are
  repeatable and process exit is the gate; bootstrap/hub tests cover packaging.
  Requires Node/npm/TypeScript install.
- **Responsibility/classification:** duplicated convenience glue. **KEEP AS THIN
  ADAPTER**.

## I-23 — `starter/{greenfield,project-administration}/manifest.json`

- **Type/entry/callers/dependencies:** declarative JSON installation plans read
  only by I-05 and package tests; entries define source/destination/render/
  preserve/executable behavior.
- **I/O/effects:** package source tree in; target materialization plan out; no
  direct file write, env, secret, Git, network, subprocess, exit, retry, or
  mutable state. Order affects installation/cleanup. Invalid/missing/duplicate
  entries fail through bootstrap; deterministic; 84/63 entries.
- **Responsibility/classification:** profile composition rather than domain
  decisions. **KEEP AS PLATFORM DECLARATION**. Earlier records incorrectly call
  these `bootstrap-manifest.json`; the actual names are `manifest.json`.

## I-24 — root and starter `ros.json`

- **Type/entry/callers/dependencies:** versioned JSON policy/config consumed by
  work, telemetry, validation, bootstrap-installed kernels, Git attribution,
  and tests.
- **I/O/effects:** read-only configuration defining identity, roots, state
  mapping, completion evidence, meaningful/ignored paths, and telemetry
  retention. No direct output, write, env, secret, Git, network, subprocess,
  exit, retry, or concurrency.
- **Operation:** defaults mask some omission and full schema enforcement is
  absent; behavior is deterministic. Root `rosVersion: 1.0.0` differs from npm
  package `1.2.1`, while templates render package version.
- **Responsibility/classification:** configured semantic policy plus wire
  contract. **MIGRATE TO F# CORE** stable meanings and typed loading/validation;
  retain JSON as source of truth and compatibility boundary.

## I-25 — `schemas/*.schema.json`

- **Type/entry/callers/dependencies:** 12 JSON Schema declarations packaged for
  consumers; current runtime validators do not execute them as schemas.
- **I/O/effects:** declarative contract only; no runtime inputs/effects/env/
  secrets/Git/network/subprocess/exit/retry/concurrency. Tests assert selected
  shapes indirectly, not whole-schema conformance.
- **Operation:** deterministic but drift can be silent. The artifact schema
  excludes `medium-high`, while both runtime validators and tests accept it.
- **Responsibility/classification:** public wire authority. **MIGRATE TO F# CORE**
  as explicit contract codecs without replacing the JSON contracts; compare
  through fixtures.

## I-26 — `telemetry/metrics.json`

- **Type/entry/callers/dependencies:** versioned 115-entry metric catalog loaded
  and validated by I-08 and telemetry tests.
- **I/O/effects:** read-only IDs/units/scopes/kinds/aggregation/collection/
  descriptions in; normalized semantic definitions out. No write/env/secrets/
  Git/network/subprocess/exit/retry/concurrency.
- **Operation:** duplicate/invalid definitions fail explicitly; provider growth
  is additive; deterministic and well tested.
- **Responsibility/classification:** canonical extensible metric vocabulary.
  **MIGRATE TO F# CORE** typed stable semantics while retaining this data
  catalog and provider-extension boundary.

## I-27 — HTML, CSS, and TypeScript configuration

- **Type/entry/callers/dependencies:** static browser shells/styles and two
  `tsconfig.json` build declarations; servers and TypeScript compiler consume
  them.
- **I/O/effects:** source/config in; DOM appearance and compiled outputs out;
  no authoritative state/env/secrets/Git/network client/subprocess by
  themselves. Compiler owns exit/error behavior and deterministic overwrite.
- **Operation:** no UI/freshness gate in root tests. HTML duplicates status,
  priority, and work-type options.
- **Responsibility/classification:** platform presentation declarations.
  **KEEP AS PLATFORM DECLARATION**; replace duplicated semantic choices with
  checked/server-supplied capability data.

## I-28 — `.sde/` methodology bundle

- **Type/entry/callers/dependencies:** installed, tracked Markdown/JSON SDE
  authority read by agents/humans and checked by external SDE CLI; current
  layout is `.sde/MANIFEST.json`, `architecture/`, `method/`, `reference/`, and
  `templates/`, 18 files, SDE release `1.1.1`, method documents `0.2.0`.
- **I/O/effects:** declarative construction/navigation/architecture/
  verification inputs; no repository runtime write, env, secret, Git, network,
  subprocess, exit, retry, concurrency, or application state. External
  `npx @echelon-foundry/sde verify` supplies integrity observability.
- **Operation:** governed external input, not application code. Earlier records
  cited lowercase/stale paths and untracked status; those assertions are
  superseded by this observation.
- **Responsibility/classification:** engineering method, not ROS semantics.
  **KEEP AS PLATFORM DECLARATION** and **DEFER — INSUFFICIENT EVIDENCE** any
  runtime interpretation. Project code must not edit or duplicate it.

# Executable verification units

## I-29 — `tests/npm-bootstrap.test.mjs`

- **Profile:** Node test entry; 14 tests call npm/Node/bootstrap and temporary
  files, package dry-run, installed launchers, and hub spoke; no secrets/Git;
  network is not required after dependencies exist. Temporary side effects are
  isolated/cleaned; test runner exit is authoritative; deterministic except
  temp/time. **KEEP AS PLATFORM DECLARATION** and compatibility oracle.

## I-30 — `tests/work-protocol.test.mjs`

- **Profile:** Node test entry; 25 cases call ROS functions/processes and real
  controlled Git/filesystem repositories; observes JSON/files/exits/errors.
  No secrets/network; temp state isolated; concurrency/fault coverage remains
  incomplete. **KEEP AS PLATFORM DECLARATION** and legacy behavior oracle.

## I-31 — `tests/telemetry.test.mjs`

- **Profile:** Node test entry; 24 cases exercise provider fixtures, Git,
  filesystem, locks and subprocess CLI in temporary repositories; no live
  provider/network/secrets. It observes records/errors/exits, including
  concurrency and retention. **KEEP AS PLATFORM DECLARATION** and legacy oracle.

## I-32 — `tests/ros-server.test.mjs`

- **Profile:** Node integration entry; 13 cases start an ephemeral local HTTP
  server and use temporary files; no external network, Git, or secrets. It
  checks API/status/files and runner exit; auth/idempotency/UI build remain
  uncovered. **KEEP AS PLATFORM DECLARATION** as edge verification.

## I-33 — `tests/ros-hub.test.mjs`

- **Profile:** Node integration entry; eight cases use temporary hub/spoke
  files, local HTTP and child `./ros`; no external network/Git/secrets. It
  checks JSON/status/files; concurrent registry and malicious filename gaps
  remain. **KEEP AS PLATFORM DECLARATION** as edge verification.

## I-34 — `tests/test_ros_cli.py`

- **Profile:** Python unittest entry; seven cases use temporary artifacts and
  registries with no env/secrets/Git/network/subprocess. It checks values,
  bytes, errors and idempotent projection. **MERGE WITH ANOTHER RESPONSIBILITY**
  by moving cases into language-neutral compatibility fixtures; retain as an
  independent oracle until F# replacement is accepted.

# Semantic responsibility decomposition

| Semantic area | Current authority | State/source of truth | Major transitions or decisions | Principal effects |
|---|---|---|---|---|
| Artifact management | `tools/ros_cli.mjs`, decisions A001/A002 | Canonical Markdown; registries are projections | parse, validate identity/status/references, render/check registry | filesystem read/write |
| Work lifecycle | `tools/ros_cli.mjs`, decisions A006/A008 | queue before start; context after start; event evidence | capture/ready/block/abandon/start/begin/block/resume/complete | files, Git, clock, telemetry |
| Execution telemetry | `tools/ros_telemetry.mjs`, decision A010 | segmented execution JSON and metric catalog | start/ingest/record/classify/finalize/aggregate; capability transitions | files, locks, Git, environment, clock |
| Bootstrap/distribution | manifests plus `lib/bootstrap.mjs`, decision A003 | package/profile manifest and installation attribution | preflight/materialize/verify; valid initial snapshot is duplicated | filesystem/modes/checksums |
| Project administration | `tools/ros_hub_cli.mjs`, decision A009 | hub registry only; spokes own their work | register/unregister/aggregate/delegate | files and local subprocesses |
| Automation/release | workflow YAML/package metadata | GitHub event/history, package metadata, registry | validate, stable eligibility, registry presence, snapshot version | GitHub/npm/network/Git |

Physical files are not treated as semantic boundaries. The F# design follows
semantic model -> domain execution -> application orchestration -> host/effects,
while the browser, npm, GitHub, provider, Git, and filesystem edges remain
explicit adapters.

# Explicit state discovered

## Local backlog

`captured -> ready -> start`, `captured|ready -> abandoned`, `ready -> blocked`,
and `blocked -> ready|abandoned`. Starting requires ready. Abandoned is terminal.
Queue status stops being authoritative after a live context item exists.

## Live work item

Configured local states map to semantic `ready`, `active`, `blocked`, and
`complete`; the kernel also recognizes `backlog` and `review` as semantic
values. Legal actions are `ready -> begin|block`, `active -> complete|block`,
`blocked -> resume`, and none from complete. Block requires a reason; completion
requires type-specific existing evidence paths. External/local state mapping is
versioned configuration. Retrying begin/complete is rejected rather than
treated as already-complete.

## Agent execution and measurement capability

Execution state is `active -> finalized`; finalization is idempotent and a
resumed work item receives a child execution only when no active execution
exists. Capability observation state is `supported-observed`,
`supported-unavailable`, `unsupported`, `unknown`, `derived`, or `estimated`,
with bounded history. Measurement quality separately permits `observed`,
`derived`, or `estimated`. Unknown/unavailable is never encoded as zero.

## Artifact lifecycle and publication

Artifact status vocabularies are kind-specific and validated, not one generic
state machine. Event publication has `pending` receipts and adapter outcomes
`success`, `failure`, or `unknown`; a complete acknowledgement/retry lifecycle
is not implemented. No invented state is added during this migration slice.

# Duplicated, hidden, coupled, obsolete, and ambiguous behavior

1. Node and Python independently define artifact parsing, status, reference,
   confidence, and registry projection; schemas are a third unenforced account.
2. Work and telemetry contain separate Git/path discovery; Git failure often
   loses the distinction between unavailable and a real empty result.
3. Queue/hub/bootstrap mutation bypasses the common lock/atomic-write discipline
   and related multi-file operations have no transaction/recovery protocol.
4. Bootstrap constructs work state rather than requesting a typed initial-state
   operation; UI/HTML and docs duplicate domain vocabularies.
5. GitHub publication YAML contains release eligibility decisions beside
   necessary platform mechanics.
6. `setup_ros_layout.py` is obsolete but direct external use is unknowable;
   generated JavaScript is ignored yet operational and affects package contents.
7. Public JSON schemas are not executed and already disagree on confidence;
   root package, lockfile, and `rosVersion` declarations also drift.
8. Hub HTTP trust and upload boundaries are wider than their verification;
   project administration and future Time Entry must not become hidden ROS
   authorities.

# Corrections to prior evidence

The prior A015/A017 records remain useful historical evidence but these exact
claims are corrected for this baseline:

- bootstrap does not invoke installed validation;
- root validation and publish workflows do not execute `npm ci`;
- starter validation configures Node only and runs registry check plus validate;
- profile files are named `manifest.json`, not `bootstrap-manifest.json`;
- the SDE bundle uses the current uppercase/layout paths, is tracked, and is
  release `1.1.1` with method `0.2.0` documents;
- package-lock version `1.1.1` is stale against package version `1.2.1`;
- two layout-generator helpers are dead and one contains an unreachable output
  labeling defect.

# Limitations

- Absence of a repository caller cannot prove absence of direct external use.
- No live GitHub workflow, npm publication, external consumer, provider session,
  multi-host writer, or network filesystem was exercised at T1.
- Generated ignored browser files existed locally; clean-package reproducibility
  is not inferred from that presence.
- The first inventory reviewer later exhausted its separate execution quota;
  its already returned corrections were independently checked in source. The
  remaining inventory was completed by the primary agent, so this is not
  represented as a fully independent review.

# Reproduction

```bash
git show ros-fsharp-migration-baseline-20260907-202926^{commit}
git ls-files -s
rg --files -uu -g '!node_modules/**' -g '!.git/**'
rg -n '^#!' -uu -g '!node_modules/**' -g '!.git/**' .
rg -n 'spawn|exec|run:|scripts|process\.env|readFile|writeFile|rename|appendFile' \
  package.json .github starter bin lib tools web web-hub tests
npm test
npm run build:web
npm run build:hub
```
