import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const installWorkItemId = `ROS-INSTALL-${JSON.parse(fs.readFileSync(path.join(repositoryRoot, "package.json"), "utf8")).version.replaceAll(".", "-")}`;
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

// Golden masters below were captured once from production's own Node
// implementation (tools/ros_cli.mjs's startWork/transition) with the exact
// same call sequence as each test, then frozen here. Node is retained in
// this repository only as the web server's internal dependency
// (DF-ROS-2026-A033) and is no longer executed as a live oracle by this
// test suite.
const GOLDEN = {
  test1Context: {
    schemaVersion: "1.0.0",
    protocolVersion: "1.0.0",
    repository: "work-complete-differential",
    actor: "ros-bootstrap",
    baselineDirtyPaths: [],
    workItems: [
      {
        id: installWorkItemId,
        type: "mechanical",
        state: "complete",
        semanticState: "complete",
        evidence: [{ type: "installation", path: ".ros/installation.json" }]
      },
      {
        id: "WI-DONE",
        type: "task",
        state: "complete",
        semanticState: "complete",
        evidence: [
          { type: "implementation", path: "IMPLEMENTATION-NOTES.md" },
          { type: "tests", path: "TESTS-NOTES.md" }
        ]
      }
    ]
  },
  test1LinesAdded: 3,
  test1LinesDeleted: 0,
  test1BinaryFiles: 0,
  test1Metrics: {
    "git.commits_created": 0,
    "git.files_added": 2,
    "git.files_modified": 1,
    "git.files_deleted": 0,
    "git.files_renamed": 0,
    "git.binary_files_changed": 0,
    "git.lines_added": 3,
    "git.lines_deleted": 0,
    "tests.added": 0,
    "tests.modified": 0,
    "tests.removed": 0,
    "documentation.files_changed": 3
  },
  test3Message: "completion evidence missing for 'WI-NO-EVIDENCE': implementation, tests",
  test4Message: "evidence path does not exist: does-not-exist-impl.md"
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-work-complete-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 }));
  initializeProject({ target: root, project: "Work Complete Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function readContext(root) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "context", "current.json"), "utf8"));
}

function readExecutions(root) {
  const dir = path.join(root, ".ros", "telemetry", "executions");
  return fs.existsSync(dir) ? fs.readdirSync(dir).sort().map((name) => JSON.parse(fs.readFileSync(path.join(dir, name), "utf8"))) : [];
}

function runFsharp(root, command, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", command, ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

const VOLATILE_KEYS = new Set([
  "executionId", "startedAt", "discoveredAt", "lastAssessedAt", "recordedAt", "collectedAt",
  "measurementId", "commit", "branch", "dirtyPaths", "dirty", "commits", "occurredAt",
  "eventId", "updatedAt", "telemetryExecutionIds", "telemetryExecutions", "completedAt",
  "createdAt", "finalizedAt", "startCommit", "endCommit"
]);

function stripVolatile(value) {
  if (Array.isArray(value)) return value.map(stripVolatile);
  if (value && typeof value === "object") {
    const result = {};
    for (const [key, child] of Object.entries(value)) {
      if (VOLATILE_KEYS.has(key)) continue;
      result[key] = stripVolatile(child);
    }
    return result;
  }
  return value;
}

const GIT_CHANGE_METRICS = [
  "git.commits_created", "git.files_added", "git.files_modified", "git.files_deleted", "git.files_renamed",
  "git.binary_files_changed", "git.lines_added", "git.lines_deleted", "tests.added", "tests.modified",
  "tests.removed", "documentation.files_changed"
];

function metricValue(record, id) {
  const metric = record.metrics.find((entry) => entry.id === id);
  return metric ? metric.value : undefined;
}

function capabilityFor(record, id) {
  return record.capabilities.find((entry) => entry.metricId === id);
}

test("F# work complete matches production's real completion effect with a real clean-baseline change summary", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharpRoot = fixture(t, "clean-fsharp");

  runFsharp(fsharpRoot, "start", ["--id", "WI-DONE", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  // Real changes in the fixture: two new (untracked) evidence files and one
  // modification to an already-tracked, already-committed file -- exercising
  // every branch of `ChangeSummaryParser.compute` (added/modified counts,
  // untracked line counting, documentation classification) against real
  // `git diff`/`git status` output.
  fs.writeFileSync(path.join(fsharpRoot, "IMPLEMENTATION-NOTES.md"), "Implemented the feature.\n");
  fs.writeFileSync(path.join(fsharpRoot, "TESTS-NOTES.md"), "Covered by new tests.\n");
  fs.appendFileSync(path.join(fsharpRoot, "README.md"), "Additional context line.\n");

  const evidence = [
    { type: "implementation", path: "IMPLEMENTATION-NOTES.md" },
    { type: "tests", path: "TESTS-NOTES.md" }
  ];

  const fsharpResult = runFsharp(fsharpRoot, "complete", [
    "--id", "WI-DONE", "--occurred-at", "2026-09-09T18:05:00.000Z",
    "--evidence", "implementation=IMPLEMENTATION-NOTES.md", "--evidence", "tests=TESTS-NOTES.md"
  ]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpContext = readContext(fsharpRoot);
  assert.deepEqual(stripVolatile(fsharpContext), GOLDEN.test1Context);

  const fsharpItem = fsharpContext.workItems.find((item) => item.id === "WI-DONE");
  assert.equal(fsharpItem.semanticState, "complete");
  assert.deepEqual(fsharpItem.evidence, evidence);
  assert.ok(fsharpItem.completedAt);
  assert.equal(fsharpItem.telemetryExecutionIds.length, 1);

  const fsharpExecution = readExecutions(fsharpRoot)[0];
  assert.equal(fsharpExecution.status, "finalized");

  const fsharpSummary = fsharpExecution.repository.changeSummary;
  assert.equal(fsharpSummary.available, true);
  assert.equal(fsharpSummary.mechanism, "git-diff-from-clean-execution-baseline");

  // Two untracked additions (the evidence files) plus one modification to
  // an already-tracked, already-committed file (README.md) -- deterministic
  // given the exact files touched above.
  const expectedCounts = { added: 2, modified: 1, deleted: 0, renamed: 0 };
  assert.deepEqual(fsharpSummary.counts, expectedCounts);
  assert.equal(fsharpSummary.commits, 0);
  assert.deepEqual(fsharpSummary.tests, { added: 0, modified: 0, removed: 0 });
  // isDocumentation matches any .md extension, not just README -- all
  // three touched files (README.md plus the two .md evidence files)
  // qualify.
  assert.equal(fsharpSummary.documentationFilesChanged, 3);

  // linesAdded/linesDeleted/binaryFiles depend on README.md's own diff hunk;
  // captured once as a golden literal from production's real git-backed
  // computation rather than hand-computed here.
  assert.equal(fsharpSummary.linesAdded, GOLDEN.test1LinesAdded);
  assert.equal(fsharpSummary.linesDeleted, GOLDEN.test1LinesDeleted);
  assert.equal(fsharpSummary.binaryFiles, GOLDEN.test1BinaryFiles);
  assert.ok(fsharpSummary.linesAdded >= 2, "at least the two evidence files' own lines must be counted");

  for (const id of GIT_CHANGE_METRICS) {
    assert.equal(metricValue(fsharpExecution, id), GOLDEN.test1Metrics[id], `metric ${id} must match production's golden master`);
  }

  assert.equal(metricValue(fsharpExecution, "git.files_added"), 2);
  assert.equal(metricValue(fsharpExecution, "git.files_modified"), 1);
  assert.equal(metricValue(fsharpExecution, "documentation.files_changed"), 3);

  // time.wall_ms/time.blocked_ms are unconditional but depend on real wall
  // time -- present and non-negative, never compared against a fixed value.
  assert.equal(typeof metricValue(fsharpExecution, "time.wall_ms"), "number");
  assert.ok(metricValue(fsharpExecution, "time.wall_ms") >= 0);
  assert.equal(metricValue(fsharpExecution, "time.blocked_ms"), 0);
});

test("F# work complete's preexisting-dirty-worktree-at-start scenario produces the same unavailable change summary as production", (t) => {
  const fsharpRoot = fixture(t, "dirty-fsharp");

  // A dirty working tree at the moment the telemetry execution is created
  // (i.e. at `work start`, since the item has no execution linked yet)
  // makes `repository.start.dirty` true, which `cleanBaselineChanges`
  // rejects before ever reading a diff.
  fs.appendFileSync(path.join(fsharpRoot, "README.md"), "Uncommitted change before start.\n");

  runFsharp(fsharpRoot, "start", ["--id", "WI-DIRTY", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  fs.writeFileSync(path.join(fsharpRoot, "IMPLEMENTATION-NOTES.md"), "Implemented anyway.\n");
  fs.writeFileSync(path.join(fsharpRoot, "TESTS-NOTES.md"), "Tested anyway.\n");

  const fsharpResult = runFsharp(fsharpRoot, "complete", [
    "--id", "WI-DIRTY", "--occurred-at", "2026-09-09T18:05:00.000Z",
    "--evidence", "implementation=IMPLEMENTATION-NOTES.md", "--evidence", "tests=TESTS-NOTES.md"
  ]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpExecution = readExecutions(fsharpRoot)[0];
  assert.equal(fsharpExecution.status, "finalized");

  const expectedSummary = { available: false, reason: "preexisting-dirty-worktree" };
  assert.deepEqual(fsharpExecution.repository.changeSummary, expectedSummary);

  const expectedReason = "execution attribution unavailable: preexisting-dirty-worktree";
  for (const id of GIT_CHANGE_METRICS) {
    const fsharpCapability = capabilityFor(fsharpExecution, id);
    assert.equal(fsharpCapability.status, "supported-unavailable");
    assert.equal(fsharpCapability.reason, expectedReason);
    assert.equal(metricValue(fsharpExecution, id), undefined, `${id} must not be recorded as a real measurement`);
  }
});

test("F# work complete rejects missing required completion evidence types before any effect, matching production's exact message", (t) => {
  const fsharpRoot = fixture(t, "missing-type-fsharp");

  runFsharp(fsharpRoot, "start", ["--id", "WI-NO-EVIDENCE", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  assert.match(GOLDEN.test3Message, /completion evidence missing for 'WI-NO-EVIDENCE': implementation, tests/);
  const fsharpResult = runFsharp(fsharpRoot, "complete", ["--id", "WI-NO-EVIDENCE", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(fsharpResult.stderr, /completion evidence missing for 'WI-NO-EVIDENCE': implementation, tests/);

  const fsharpItem = readContext(fsharpRoot).workItems.find((item) => item.id === "WI-NO-EVIDENCE");
  assert.equal(fsharpItem.semanticState, "active");
});

test("F# work complete rejects a nonexistent evidence path, matching production's exact message and real filesystem check", (t) => {
  const fsharpRoot = fixture(t, "missing-path-fsharp");

  runFsharp(fsharpRoot, "start", ["--id", "WI-GHOST-EVIDENCE", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  assert.match(GOLDEN.test4Message, /evidence path does not exist: does-not-exist-impl\.md/);
  const fsharpResult = runFsharp(fsharpRoot, "complete", [
    "--id", "WI-GHOST-EVIDENCE", "--occurred-at", "2026-09-09T18:05:00.000Z",
    "--evidence", "implementation=does-not-exist-impl.md", "--evidence", "tests=does-not-exist-tests.md"
  ]);
  assert.equal(fsharpResult.status, 1);
  assert.match(fsharpResult.stderr, /evidence path does not exist: does-not-exist-impl\.md/);
});

test("F# work complete writes a research item's conclusion, defaulting to 'inconclusive' and honoring an explicit --conclusion, matching production", (t) => {
  const fsharpRoot = fixture(t, "conclusion-fsharp");

  runFsharp(fsharpRoot, "start", ["--id", "WI-RESEARCH-DEFAULT", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "research"]);
  runFsharp(fsharpRoot, "start", ["--id", "WI-RESEARCH-EXPLICIT", "--occurred-at", "2026-09-09T18:00:01.000Z", "--type", "research"]);

  fs.writeFileSync(path.join(fsharpRoot, "RESEARCH-RECORD.md"), "n/a\n");

  // ros.json's default scaffold overrides completion evidence for
  // "research" items to a single "research-record" type (never
  // "implementation"/"tests"), read via `FileWorkConfigRepository.
  // readCompletionEvidence`'s per-type map.
  const evidence = [{ type: "research-record", path: "RESEARCH-RECORD.md" }];

  runFsharp(fsharpRoot, "complete", [
    "--id", "WI-RESEARCH-DEFAULT", "--occurred-at", "2026-09-09T18:05:00.000Z",
    "--evidence", "research-record=RESEARCH-RECORD.md"
  ]);

  runFsharp(fsharpRoot, "complete", [
    "--id", "WI-RESEARCH-EXPLICIT", "--occurred-at", "2026-09-09T18:06:00.000Z",
    "--evidence", "research-record=RESEARCH-RECORD.md",
    "--conclusion", "confirmed: caching reduces latency"
  ]);

  const fsharpContext = readContext(fsharpRoot);
  const fsharpDefault = fsharpContext.workItems.find((item) => item.id === "WI-RESEARCH-DEFAULT");
  const fsharpExplicit = fsharpContext.workItems.find((item) => item.id === "WI-RESEARCH-EXPLICIT");

  assert.equal(fsharpDefault.conclusion, "inconclusive");
  assert.equal(fsharpExplicit.conclusion, "confirmed: caching reduces latency");
});
