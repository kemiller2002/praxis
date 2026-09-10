import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { startWork, transition } from "../tools/ros_cli.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-work-complete-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
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
  const nodeRoot = fixture(t, "clean-node");
  const fsharpRoot = fixture(t, "clean-fsharp");

  startWork(nodeRoot, ["WI-DONE"], { type: "task" });
  runFsharp(fsharpRoot, "start", ["--id", "WI-DONE", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  // Identical real changes in both fixtures: two new (untracked) evidence
  // files and one modification to an already-tracked, already-committed
  // file -- exercising every branch of `ChangeSummaryParser.compute`
  // (added/modified counts, untracked line counting, documentation
  // classification) against real `git diff`/`git status` output.
  for (const root of [nodeRoot, fsharpRoot]) {
    fs.writeFileSync(path.join(root, "IMPLEMENTATION-NOTES.md"), "Implemented the feature.\n");
    fs.writeFileSync(path.join(root, "TESTS-NOTES.md"), "Covered by new tests.\n");
    fs.appendFileSync(path.join(root, "README.md"), "Additional context line.\n");
  }

  const evidence = [
    { type: "implementation", path: "IMPLEMENTATION-NOTES.md" },
    { type: "tests", path: "TESTS-NOTES.md" }
  ];

  transition(nodeRoot, "complete", ["WI-DONE"], { evidence });
  const fsharpResult = runFsharp(fsharpRoot, "complete", [
    "--id", "WI-DONE", "--occurred-at", "2026-09-09T18:05:00.000Z",
    "--evidence", "implementation=IMPLEMENTATION-NOTES.md", "--evidence", "tests=TESTS-NOTES.md"
  ]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const nodeContext = readContext(nodeRoot);
  const fsharpContext = readContext(fsharpRoot);
  assert.deepEqual(stripVolatile(nodeContext), stripVolatile(fsharpContext));

  const nodeItem = nodeContext.workItems.find((item) => item.id === "WI-DONE");
  const fsharpItem = fsharpContext.workItems.find((item) => item.id === "WI-DONE");
  assert.equal(nodeItem.semanticState, "complete");
  assert.equal(fsharpItem.semanticState, "complete");
  assert.deepEqual(nodeItem.evidence, evidence);
  assert.deepEqual(fsharpItem.evidence, evidence);
  assert.ok(nodeItem.completedAt);
  assert.ok(fsharpItem.completedAt);
  assert.equal(nodeItem.telemetryExecutionIds.length, 1);
  assert.equal(fsharpItem.telemetryExecutionIds.length, 1);

  const nodeExecution = readExecutions(nodeRoot)[0];
  const fsharpExecution = readExecutions(fsharpRoot)[0];
  assert.equal(nodeExecution.status, "finalized");
  assert.equal(fsharpExecution.status, "finalized");

  const nodeSummary = nodeExecution.repository.changeSummary;
  const fsharpSummary = fsharpExecution.repository.changeSummary;
  assert.equal(nodeSummary.available, true);
  assert.equal(fsharpSummary.available, true);
  assert.equal(nodeSummary.mechanism, "git-diff-from-clean-execution-baseline");
  assert.equal(fsharpSummary.mechanism, "git-diff-from-clean-execution-baseline");

  // Two untracked additions (the evidence files) plus one modification to
  // an already-tracked, already-committed file (README.md) -- deterministic
  // given the exact files touched above, and identical between both
  // fixtures.
  const expectedCounts = { added: 2, modified: 1, deleted: 0, renamed: 0 };
  assert.deepEqual(nodeSummary.counts, expectedCounts);
  assert.deepEqual(fsharpSummary.counts, expectedCounts);
  assert.equal(nodeSummary.commits, 0);
  assert.equal(fsharpSummary.commits, 0);
  assert.deepEqual(nodeSummary.tests, { added: 0, modified: 0, removed: 0 });
  assert.deepEqual(fsharpSummary.tests, { added: 0, modified: 0, removed: 0 });
  // isDocumentation matches any .md extension, not just README -- all
  // three touched files (README.md plus the two .md evidence files)
  // qualify.
  assert.equal(nodeSummary.documentationFilesChanged, 3);
  assert.equal(fsharpSummary.documentationFilesChanged, 3);

  // linesAdded/linesDeleted/binaryFiles depend on README.md's own diff
  // hunk, which is identical between fixtures but not hand-computed here --
  // cross-checked between Node and F# instead of hard-coded.
  assert.equal(nodeSummary.linesAdded, fsharpSummary.linesAdded);
  assert.equal(nodeSummary.linesDeleted, fsharpSummary.linesDeleted);
  assert.equal(nodeSummary.binaryFiles, fsharpSummary.binaryFiles);
  assert.ok(nodeSummary.linesAdded >= 2, "at least the two evidence files' own lines must be counted");

  for (const id of GIT_CHANGE_METRICS) {
    assert.equal(metricValue(nodeExecution, id), metricValue(fsharpExecution, id), `metric ${id} must match between Node and F#`);
  }

  assert.equal(metricValue(nodeExecution, "git.files_added"), 2);
  assert.equal(metricValue(nodeExecution, "git.files_modified"), 1);
  assert.equal(metricValue(nodeExecution, "documentation.files_changed"), 3);

  // time.wall_ms/time.blocked_ms are unconditional but depend on real wall
  // time, which differs between the two invocations -- present and
  // non-negative on both sides, never cross-compared.
  assert.equal(typeof metricValue(nodeExecution, "time.wall_ms"), "number");
  assert.ok(metricValue(nodeExecution, "time.wall_ms") >= 0);
  assert.equal(typeof metricValue(fsharpExecution, "time.wall_ms"), "number");
  assert.ok(metricValue(fsharpExecution, "time.wall_ms") >= 0);
  assert.equal(metricValue(nodeExecution, "time.blocked_ms"), 0);
  assert.equal(metricValue(fsharpExecution, "time.blocked_ms"), 0);
});

test("F# work complete's preexisting-dirty-worktree-at-start scenario produces the same unavailable change summary as production", (t) => {
  const nodeRoot = fixture(t, "dirty-node");
  const fsharpRoot = fixture(t, "dirty-fsharp");

  // A dirty working tree at the moment the telemetry execution is created
  // (i.e. at `work start`, since the item has no execution linked yet)
  // makes `repository.start.dirty` true, which `cleanBaselineChanges`
  // rejects before ever reading a diff.
  for (const root of [nodeRoot, fsharpRoot]) {
    fs.appendFileSync(path.join(root, "README.md"), "Uncommitted change before start.\n");
  }

  startWork(nodeRoot, ["WI-DIRTY"], { type: "task" });
  runFsharp(fsharpRoot, "start", ["--id", "WI-DIRTY", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  for (const root of [nodeRoot, fsharpRoot]) {
    fs.writeFileSync(path.join(root, "IMPLEMENTATION-NOTES.md"), "Implemented anyway.\n");
    fs.writeFileSync(path.join(root, "TESTS-NOTES.md"), "Tested anyway.\n");
  }

  const evidence = [
    { type: "implementation", path: "IMPLEMENTATION-NOTES.md" },
    { type: "tests", path: "TESTS-NOTES.md" }
  ];

  transition(nodeRoot, "complete", ["WI-DIRTY"], { evidence });
  const fsharpResult = runFsharp(fsharpRoot, "complete", [
    "--id", "WI-DIRTY", "--occurred-at", "2026-09-09T18:05:00.000Z",
    "--evidence", "implementation=IMPLEMENTATION-NOTES.md", "--evidence", "tests=TESTS-NOTES.md"
  ]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const nodeExecution = readExecutions(nodeRoot)[0];
  const fsharpExecution = readExecutions(fsharpRoot)[0];
  assert.equal(nodeExecution.status, "finalized");
  assert.equal(fsharpExecution.status, "finalized");

  const expectedSummary = { available: false, reason: "preexisting-dirty-worktree" };
  assert.deepEqual(nodeExecution.repository.changeSummary, expectedSummary);
  assert.deepEqual(fsharpExecution.repository.changeSummary, expectedSummary);

  const expectedReason = "execution attribution unavailable: preexisting-dirty-worktree";
  for (const id of GIT_CHANGE_METRICS) {
    const nodeCapability = capabilityFor(nodeExecution, id);
    const fsharpCapability = capabilityFor(fsharpExecution, id);
    assert.equal(nodeCapability.status, "supported-unavailable");
    assert.equal(nodeCapability.reason, expectedReason);
    assert.equal(fsharpCapability.status, "supported-unavailable");
    assert.equal(fsharpCapability.reason, expectedReason);
    assert.equal(metricValue(nodeExecution, id), undefined, `${id} must not be recorded as a real measurement`);
    assert.equal(metricValue(fsharpExecution, id), undefined, `${id} must not be recorded as a real measurement`);
  }
});

test("F# work complete rejects missing required completion evidence types before any effect, matching production's exact message", (t) => {
  const nodeRoot = fixture(t, "missing-type-node");
  const fsharpRoot = fixture(t, "missing-type-fsharp");

  startWork(nodeRoot, ["WI-NO-EVIDENCE"], { type: "task" });
  runFsharp(fsharpRoot, "start", ["--id", "WI-NO-EVIDENCE", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  let nodeMessage;
  try {
    transition(nodeRoot, "complete", ["WI-NO-EVIDENCE"], {});
  } catch (error) {
    nodeMessage = error.message;
  }

  const fsharpResult = runFsharp(fsharpRoot, "complete", ["--id", "WI-NO-EVIDENCE", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(nodeMessage, /completion evidence missing for 'WI-NO-EVIDENCE': implementation, tests/);
  assert.match(fsharpResult.stderr, /completion evidence missing for 'WI-NO-EVIDENCE': implementation, tests/);

  const nodeItem = readContext(nodeRoot).workItems.find((item) => item.id === "WI-NO-EVIDENCE");
  const fsharpItem = readContext(fsharpRoot).workItems.find((item) => item.id === "WI-NO-EVIDENCE");
  assert.equal(nodeItem.semanticState, "active");
  assert.equal(fsharpItem.semanticState, "active");
});

test("F# work complete rejects a nonexistent evidence path, matching production's exact message and real filesystem check", (t) => {
  const nodeRoot = fixture(t, "missing-path-node");
  const fsharpRoot = fixture(t, "missing-path-fsharp");

  startWork(nodeRoot, ["WI-GHOST-EVIDENCE"], { type: "task" });
  runFsharp(fsharpRoot, "start", ["--id", "WI-GHOST-EVIDENCE", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  const evidence = [
    { type: "implementation", path: "does-not-exist-impl.md" },
    { type: "tests", path: "does-not-exist-tests.md" }
  ];

  let nodeMessage;
  try {
    transition(nodeRoot, "complete", ["WI-GHOST-EVIDENCE"], { evidence });
  } catch (error) {
    nodeMessage = error.message;
  }

  const fsharpResult = runFsharp(fsharpRoot, "complete", [
    "--id", "WI-GHOST-EVIDENCE", "--occurred-at", "2026-09-09T18:05:00.000Z",
    "--evidence", "implementation=does-not-exist-impl.md", "--evidence", "tests=does-not-exist-tests.md"
  ]);
  assert.equal(fsharpResult.status, 1);
  assert.match(nodeMessage, /evidence path does not exist: does-not-exist-impl\.md/);
  assert.match(fsharpResult.stderr, /evidence path does not exist: does-not-exist-impl\.md/);
});

test("F# work complete writes a research item's conclusion, defaulting to 'inconclusive' and honoring an explicit --conclusion, matching production", (t) => {
  const nodeRoot = fixture(t, "conclusion-node");
  const fsharpRoot = fixture(t, "conclusion-fsharp");

  startWork(nodeRoot, ["WI-RESEARCH-DEFAULT"], { type: "research" });
  startWork(nodeRoot, ["WI-RESEARCH-EXPLICIT"], { type: "research" });
  runFsharp(fsharpRoot, "start", ["--id", "WI-RESEARCH-DEFAULT", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "research"]);
  runFsharp(fsharpRoot, "start", ["--id", "WI-RESEARCH-EXPLICIT", "--occurred-at", "2026-09-09T18:00:01.000Z", "--type", "research"]);

  for (const root of [nodeRoot, fsharpRoot]) {
    fs.writeFileSync(path.join(root, "RESEARCH-RECORD.md"), "n/a\n");
  }

  // ros.json's default scaffold overrides completion evidence for
  // "research" items to a single "research-record" type (never
  // "implementation"/"tests"), read via `FileWorkConfigRepository.
  // readCompletionEvidence`'s per-type map.
  const evidence = [{ type: "research-record", path: "RESEARCH-RECORD.md" }];

  transition(nodeRoot, "complete", ["WI-RESEARCH-DEFAULT"], { evidence });
  runFsharp(fsharpRoot, "complete", [
    "--id", "WI-RESEARCH-DEFAULT", "--occurred-at", "2026-09-09T18:05:00.000Z",
    "--evidence", "research-record=RESEARCH-RECORD.md"
  ]);

  transition(nodeRoot, "complete", ["WI-RESEARCH-EXPLICIT"], { evidence, conclusion: "confirmed: caching reduces latency" });
  runFsharp(fsharpRoot, "complete", [
    "--id", "WI-RESEARCH-EXPLICIT", "--occurred-at", "2026-09-09T18:06:00.000Z",
    "--evidence", "research-record=RESEARCH-RECORD.md",
    "--conclusion", "confirmed: caching reduces latency"
  ]);

  const nodeContext = readContext(nodeRoot);
  const fsharpContext = readContext(fsharpRoot);
  const nodeDefault = nodeContext.workItems.find((item) => item.id === "WI-RESEARCH-DEFAULT");
  const fsharpDefault = fsharpContext.workItems.find((item) => item.id === "WI-RESEARCH-DEFAULT");
  const nodeExplicit = nodeContext.workItems.find((item) => item.id === "WI-RESEARCH-EXPLICIT");
  const fsharpExplicit = fsharpContext.workItems.find((item) => item.id === "WI-RESEARCH-EXPLICIT");

  assert.equal(nodeDefault.conclusion, "inconclusive");
  assert.equal(fsharpDefault.conclusion, "inconclusive");
  assert.equal(nodeExplicit.conclusion, "confirmed: caching reduces latency");
  assert.equal(fsharpExplicit.conclusion, "confirmed: caching reduces latency");
});
