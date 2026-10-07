---
id: EV-ROS-2026-A015
title: ROS operational architecture inventory and migration baseline
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-07
updated: 2026-09-07
supersedes: []
superseded_by: []
research_area: repository-operating-system
evidence_type: primary
supports:
  - RP-ROS-2026-A017
related_documents:
  - JR-ROS-2026-A016
  - tools/ros_cli.mjs
  - tools/ros_telemetry.mjs
  - lib/bootstrap.mjs
  - .github/workflows/publish.yml
tags: [architecture, inventory, scripts, fsharp, migration, baseline]
confidence: high
---

# Evidence summary

This record preserves the repository-local evidence used to reconstruct the
current ROS operational system and plan a staged F# migration. The inspected
snapshot was branch `main` at commit
`626181ecc40e4994994c91c44e5763e9c7f9237f`, plus the complete working tree as
it existed on 2026-09-07. The working tree already contained substantial
uncommitted adaptive-telemetry changes and `.sde/` methodology inputs; those
were treated as observed current behavior and were not overwritten.

# Collection method

The inventory used five independent discovery paths:

1. all-file and extension discovery, including hidden and ignored files while
   excluding `.git/` and `node_modules/`;
2. executable-bit and shebang discovery;
3. package-script, workflow `run`/`uses`, manifest, schema, configuration, and
   inline-HTML behavior discovery;
4. import, exported-symbol, caller, file-I/O, environment-variable, Git-command,
   and external-process tracing through every authored executable source; and
5. tests, Git history, accepted decisions, current documentation, generated
   assets, and state-store consumers.

The source was read directly. Conclusions about activity were checked against
package contents, starter manifests, command documentation, imports, tests, and
Git history rather than inferred from filenames alone.

# Quantitative baseline

| Measure | Observed value | Method and limitation |
|---|---:|---|
| Authored Node/JavaScript executable sources | 12 files / 4,104 lines | Launchers, `lib/`, and operational `tools/`; excludes generated browser JavaScript. |
| Authored Python operational sources | 2 files / 1,443 lines | Legacy layout generator and legacy artifact validator. |
| Authored TypeScript clients | 2 files / 934 lines | Single-repository and project-administration browser clients. |
| Total authored executable source | 16 files / 6,481 lines | Does not count workflow YAML, tests, HTML, schemas, or generated files. |
| GitHub Actions workflows | 3 files / 150 lines | Two root workflows and one installed-template workflow. |
| Executable test sources | 6 files / 1,880 lines | Five Node test files and one Python test file. |
| Named tests | 84 Node + 7 Python | Counted from test declarations and confirmed by execution. |
| JSON schemas | 12 | Artifact, work, adapter, and telemetry contract documents. |
| Normalized telemetry metrics | 115 | `telemetry/metrics.json` schema `1.0.0`. |
| Bootstrap profile payload | 84 greenfield files / 63 project-administration files | Manifest entry counts, including shared sources. |
| Current npm dry-run package | 112 entries; 136,149-byte archive; 540,506 bytes unpacked | Workspace-state observation; ignored generated web assets can make this non-hermetic. |
| Current validation latency | 0.09 seconds | One local run; not a benchmark distribution. |
| Current registry-check latency | 0.06 seconds | One local run; not a benchmark distribution. |
| Current full test latency | 13.1 seconds wall time | One local run on Node `25.6.0`, npm `11.8.0`, Python `3.14.3`. |
| Available local F# toolchain | .NET SDK `10.0.100` | Availability in this workspace does not prove availability in consumer repositories. |

No authored or tracked Bash, PowerShell, F#, C#, Makefile, Justfile, Taskfile,
composite GitHub Action, or repository Git hook was found. Shell behavior exists
inside GitHub workflow `run` blocks. The only executable-bit source files are
`ros` and `bin/ros-bootstrap.mjs`; starter launchers receive executable mode
during installation from their manifests.

# Direct observations supporting the architecture assessment

- `tools/ros_cli.mjs` and `tools/ros_telemetry.mjs` contain 2,819 lines together
  and implement state machines, evidence rules, validation, Git attribution,
  telemetry normalization, aggregation, provider ingestion, projections, and
  authoritative file mutation. They are an application kernel, not merely CLI
  glue.
- `tools/ros_cli.py` independently implements the original artifact parser,
  validator, and registry generator. It is still executed by `npm test`, but is
  not the installed `./ros` runtime and is excluded from the npm package.
- The JSON Schemas are published and installed but are not evaluated by either
  runtime validator. Rules are restated in code. A concrete drift already
  exists: `artifact-metadata.schema.json` excludes `medium-high` confidence,
  while both validators accept it and compatibility tests require it.
- `setup_ros_layout.py` is a standalone, force-capable legacy scaffold with no
  caller outside its own usage text. The npm package uses `lib/bootstrap.mjs`
  and declarative profile manifests instead.
- Work lifecycle, telemetry lifecycle, and bootstrap installation all write
  related authoritative state, but bootstrap constructs `.ros/context`, event,
  queue, and hub records directly rather than invoking the work/persistence
  kernel.
- Work context and telemetry mutations use atomic file replacement and scoped
  locks. Backlog queue mutations and hub-registry mutations do not share that
  locking discipline. Hub registry writes are direct, non-atomic writes.
- Multi-file operations are not transactions. Validation detects some broken
  execution/context backlinks, but not every event/context/projection partial
  state possible after interruption.
- Git path discovery is implemented twice and differs. The work-attribution
  version does not correctly consume the second NUL-delimited pathname emitted
  by porcelain-v1 rename/copy records; telemetry has separate handling.
- Git failures are generally converted to an empty result or `null`, so some
  attribution enforcement can fail open instead of distinguishing “clean” from
  “Git unavailable.”
- The publish workflow contains durable release decisions in inline shell:
  version-change detection, registry existence interpretation, stable-release
  eligibility, and main-snapshot version construction. The actual workflow
  behavior has no local execution harness; `CI-LATEST-ON-VERSION-BUMP` is
  recorded as blocked for that reason.
- Both HTTP servers are unauthenticated and can be bound beyond loopback. The
  project-administration server can execute every registered repository's
  `./ros`. This is documented, but it is a material trust boundary.
- The hub upload temporary pathname incorporates an untrusted multipart
  filename without basename/sanitization. A direct `path.join` probe showed
  that a value such as `../../../escape` resolves outside the temporary
  directory. No test covers that case.
- Browser clients are thin projections over server decisions, but HTML embeds
  priority, status, and work-type vocabularies. TypeScript compilation is not
  part of the root `npm test` or validation workflow.
- Requirements and acceptance-criteria identifiers are optional telemetry
  links and metric counts only. No requirement lifecycle or synchronization is
  implemented. Time-entry derivation, central ingestion/reconciliation, and
  cross-machine project administration are absent, not hidden in another
  script.

# Validation evidence

`npm test` completed successfully with 84 Node tests and 7 Python tests. The
Node suite includes bootstrap/tarball, work protocol, telemetry/concurrency,
HTTP, multipart upload, and hub integration tests. The Python suite covers its
independent artifact validator and registry generator. Expected error text from
negative child-process fixtures appeared in the test log but no test failed.

`./ros validate` and `./ros registry check` both passed before adding the new
research artifacts. These checks establish the starting baseline, not the
final state after this report.

# Limitations

- The source tree was dirty before this mission began, so Git cannot attribute
  the pre-existing changes to this inventory and ROS correctly records
  execution-level change metrics as unavailable.
- No external consumer repository, GitHub-hosted workflow run, npm publication,
  live provider telemetry stream, network filesystem, or multi-host writer was
  exercised.
- No code-coverage tool is configured, so test count is known but line/branch
  coverage is not.
- Git history is short and uneven; commit-touch counts indicate hotspots but
  are not a stable long-term change-frequency distribution.
- Generated `web/*.js` and source maps were present as ignored local files and
  served by the runtime, but are not tracked source. Their presence also
  affected the local npm dry-run package contents.

# Reproduction notes

From the repository root, a successor can reproduce the principal checks with:

```bash
rg --files -uu -g '!node_modules/**' -g '!.git/**'
find . -type f -perm -111 -not -path './.git/*' -not -path './node_modules/*'
rg -n -uu -g '!node_modules/**' -g '!.git/**' '^#!' .
npm test
./ros registry check
./ros validate
npm pack --dry-run --ignore-scripts --json
```

The complete component-by-component analysis, flows, dispositions, target
architecture, SDE experiment, and implementation backlog are in
`RP-ROS-2026-A017`.
