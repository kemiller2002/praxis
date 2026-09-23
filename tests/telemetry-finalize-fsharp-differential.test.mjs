import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

// Golden masters below were captured once from production's own Node
// implementation (tools/ros_telemetry.mjs's finalizeExecution) with the exact
// same call sequence as each test, then frozen here. Node is retained in
// this repository only as the web server's internal dependency
// (DF-ROS-2026-A033) and is no longer executed as a live oracle by this test
// suite.
const GOLDEN = {
  test1Metrics: {
    "git.commits_created": 0,
    "git.files_added": 1,
    "git.files_modified": 0,
    "git.files_deleted": 0,
    "git.files_renamed": 0,
    "git.binary_files_changed": 0,
    "git.lines_added": 1,
    "git.lines_deleted": 0,
    "tests.added": 0,
    "tests.modified": 0,
    "tests.removed": 0,
    "documentation.files_changed": 1
  },
  test4Message: "telemetry target is ambiguous; provide a work-item or execution ID",
  test5Message: "telemetry target is ambiguous; provide a work-item or execution ID",
  test6Message: "telemetry execution 'WI-GHOST' was not found"
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-finalize-${label}-`));
  t.after(() => {
    try {
      fs.rmSync(root, { recursive: true, force: true });
    } catch {
      // Cleanup best-effort: a leftover temp dir under CI I/O contention isn't a test failure.
    }
  });
  initializeProject({ target: root, project: "Telemetry Finalize Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "telemetry", ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function readExecution(root, executionId) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "telemetry", "executions", `${executionId}.json`), "utf8"));
}

function metricValue(record, id) {
  const metric = record.metrics.find((entry) => entry.id === id);
  return metric ? metric.value : undefined;
}

const GIT_CHANGE_METRICS = [
  "git.commits_created", "git.files_added", "git.files_modified", "git.files_deleted", "git.files_renamed",
  "git.binary_files_changed", "git.lines_added", "git.lines_deleted", "tests.added", "tests.modified",
  "tests.removed", "documentation.files_changed"
];

function writeFixtureExecution(root, executionId, workItemId, status, startedAt) {
  const directory = path.join(root, ".ros", "telemetry", "executions");
  fs.mkdirSync(directory, { recursive: true });
  const record = {
    schemaVersion: "1.0.0",
    executionId,
    workItemId,
    status,
    startedAt,
    ...(status === "finalized" ? { finalizedAt: startedAt } : {}),
    identity: { provider: "anthropic", runtime: "claude-code", sessionId: null },
    capabilities: [],
    metrics: [],
    events: [],
    repository: { start: { available: false } },
    links: {}
  };
  fs.writeFileSync(path.join(directory, `${executionId}.json`), JSON.stringify(record, null, 2));
  return record;
}

test("F# telemetry finalize with no target resolves and finalizes the single active work item's execution, matching production's real effect", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharpRoot = fixture(t, "no-target-fsharp");

  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);

  fs.writeFileSync(path.join(fsharpRoot, "IMPLEMENTATION-NOTES.md"), "Implemented.\n");

  const fsharpResult = runFsharp(fsharpRoot, ["finalize"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.equal(fsharpRecord.status, "finalized");
  assert.equal(fsharpRecord.workItemId, "WI-A");

  for (const id of GIT_CHANGE_METRICS) {
    assert.equal(GOLDEN.test1Metrics[id], metricValue(fsharpRecord, id), `metric ${id} value mismatch`);
  }
  assert.equal(metricValue(fsharpRecord, "documentation.files_changed"), 1);

  assert.equal(fsharpRecord.repository.changeHealth.enabled, true);
  assert.equal(fsharpRecord.repository.changeHealth.metrics.filesChanged, 1);
  assert.equal(fsharpRecord.repository.changeHealth.metrics.documentationFilesChanged, 1);
  assert.equal(fsharpRecord.repository.changeHealth.metrics.repeatFileTouches, 1);
  assert.equal(metricValue(fsharpRecord, "code.files_changed"), 1);
  assert.equal(metricValue(fsharpRecord, "code.lines_changed"), 1);
  assert.equal(metricValue(fsharpRecord, "code.threshold_warnings"), 0);
  assert.equal(metricValue(fsharpRecord, "code.threshold_errors"), 0);

  const historyPath = path.join(fsharpRoot, ".ros", "telemetry", "change-history.json");
  assert.ok(fs.existsSync(historyPath));
  const history = JSON.parse(fs.readFileSync(historyPath, "utf8"));
  assert.equal(history.updates.length, 1);
  assert.equal(history.updates[0].executionId, fsharpRecord.executionId);
  assert.equal(history.updates[0].files[0].path, "IMPLEMENTATION-NOTES.md");

  const changeHealth = runFsharp(fsharpRoot, ["change-health", fsharpRecord.executionId]);
  assert.equal(changeHealth.status, 0, changeHealth.stderr);
  assert.deepEqual(JSON.parse(changeHealth.stdout), fsharpRecord.repository.changeHealth);
});

test("F# telemetry finalize with a work-item target matches by workItemId regardless of status, taking the latest startedAt", (t) => {
  const fsharpRoot = fixture(t, "workitem-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-OLD", "WI-A", "finalized", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-NEW", "WI-A", "active", "2026-01-01T00:05:00.000Z");

  const fsharpResult = runFsharp(fsharpRoot, ["finalize", "WI-A"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.equal(fsharpRecord.executionId, "EXE-NEW");
  assert.equal(fsharpRecord.status, "finalized");

  const fsharpOld = readExecution(fsharpRoot, "EXE-OLD");
  assert.equal(fsharpOld.status, "finalized");
  assert.equal(fsharpOld.finalizedAt, "2026-01-01T00:00:00.000Z");
});

test("F# telemetry finalize with an EXE-prefixed target resolves by exact execution id", (t) => {
  const fsharpRoot = fixture(t, "exe-target-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-2", "WI-B", "active", "2026-01-01T00:00:01.000Z");

  const fsharpResult = runFsharp(fsharpRoot, ["finalize", "EXE-1"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.equal(fsharpRecord.executionId, "EXE-1");
  assert.equal(fsharpRecord.status, "finalized");

  const fsharpOther = readExecution(fsharpRoot, "EXE-2");
  assert.equal(fsharpOther.status, "active");
});

test("F# telemetry finalize with no target and zero active-or-blocked work items rejects with production's exact ambiguity message", (t) => {
  const fsharpRoot = fixture(t, "ambiguous-zero-fsharp");

  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);

  fs.writeFileSync(path.join(fsharpRoot, "IMPLEMENTATION-NOTES.md"), "Implemented.\n");
  fs.writeFileSync(path.join(fsharpRoot, "TESTS-NOTES.md"), "Tested.\n");

  execFileSync("dotnet", [
    fsharpCli, "--root", fsharpRoot, "work", "complete", "--id", "WI-A", "--occurred-at", "2026-09-10T18:05:00.000Z",
    "--evidence", "implementation=IMPLEMENTATION-NOTES.md", "--evidence", "tests=TESTS-NOTES.md"
  ]);

  const fsharpResult = runFsharp(fsharpRoot, ["finalize"]);

  assert.equal(fsharpResult.status, 1);
  assert.match(GOLDEN.test4Message, /telemetry target is ambiguous; provide a work-item or execution ID/);
  assert.match(fsharpResult.stderr, /telemetry target is ambiguous; provide a work-item or execution ID/);
});

test("F# telemetry finalize with no target and multiple active-or-blocked work items rejects with the same ambiguity message", (t) => {
  const fsharpRoot = fixture(t, "ambiguous-many-fsharp");

  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-B", "--occurred-at", "2026-09-10T18:00:01.000Z", "--type", "task"]);

  const fsharpResult = runFsharp(fsharpRoot, ["finalize"]);

  assert.equal(fsharpResult.status, 1);
  assert.match(GOLDEN.test5Message, /telemetry target is ambiguous; provide a work-item or execution ID/);
  assert.match(fsharpResult.stderr, /telemetry target is ambiguous; provide a work-item or execution ID/);
});

test("F# telemetry finalize rejects an unknown target with production's exact message", (t) => {
  const fsharpRoot = fixture(t, "not-found-fsharp");

  const fsharpResult = runFsharp(fsharpRoot, ["finalize", "WI-GHOST"]);

  assert.equal(fsharpResult.status, 1);
  assert.match(GOLDEN.test6Message, /telemetry execution 'WI-GHOST' was not found/);
  assert.match(fsharpResult.stderr, /telemetry execution 'WI-GHOST' was not found/);
});

test("F# telemetry finalize returns an already-finalized resolved execution untouched, matching production's race-tolerant fast path", (t) => {
  const fsharpRoot = fixture(t, "already-finalized-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "finalized", "2026-01-01T00:00:00.000Z");

  const fsharpBefore = fs.readFileSync(path.join(fsharpRoot, ".ros", "telemetry", "executions", "EXE-1.json"), "utf8");

  const fsharpResult = runFsharp(fsharpRoot, ["finalize", "EXE-1"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.equal(fsharpRecord.status, "finalized");

  const fsharpAfter = fs.readFileSync(path.join(fsharpRoot, ".ros", "telemetry", "executions", "EXE-1.json"), "utf8");
  assert.equal(fsharpBefore, fsharpAfter);
});

test("F# telemetry finalize --input is rejected by this CLI, unlike production's real adapter ingestion", (t) => {
  const fsharpRoot = fixture(t, "input-rejected-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const fsharpResult = runFsharp(fsharpRoot, ["finalize", "EXE-1", "--input", "-"]);
  assert.equal(fsharpResult.status, 2);
  assert.match(fsharpResult.stderr, /telemetry finalize --input is not yet supported by this CLI/);

  const record = readExecution(fsharpRoot, "EXE-1");
  assert.equal(record.status, "active");
});


test("F# change-health history identifies a file and line region touched across multiple updates", (t) => {
  const root = fixture(t, "hotspots-fsharp");
  fs.mkdirSync(path.join(root, "src"), { recursive: true });
  fs.writeFileSync(path.join(root, "src", "Hot.fs"), "module Hot\n\nlet value = 1\n");
  execFileSync("git", ["add", "src/Hot.fs"], { cwd: root });
  execFileSync("git", ["commit", "-qm", "add hotspot fixture"], { cwd: root });

  execFileSync("dotnet", [fsharpCli, "--root", root, "work", "start", "--id", "WI-HOT-1", "--occurred-at", "2026-09-23T10:00:00.000Z", "--type", "task"]);
  fs.writeFileSync(path.join(root, "src", "Hot.fs"), "module Hot\n\nlet value = 2\n");
  const first = runFsharp(root, ["finalize", "WI-HOT-1"]);
  assert.equal(first.status, 0, first.stderr);
  assert.equal(JSON.parse(first.stdout).repository.changeHealth.metrics.repeatFileTouches, 1);

  execFileSync("git", ["add", "src/Hot.fs"], { cwd: root });
  execFileSync("git", ["commit", "-qm", "first hotspot update"], { cwd: root });

  execFileSync("dotnet", [fsharpCli, "--root", root, "work", "start", "--id", "WI-HOT-2", "--occurred-at", "2026-09-23T11:00:00.000Z", "--type", "task"]);
  fs.writeFileSync(path.join(root, "src", "Hot.fs"), "module Hot\n\nlet value = 3\n");
  const second = runFsharp(root, ["finalize", "WI-HOT-2"]);
  assert.equal(second.status, 0, second.stderr);
  const secondRecord = JSON.parse(second.stdout);
  assert.equal(secondRecord.repository.changeHealth.metrics.repeatFileTouches, 2);
  assert.equal(secondRecord.repository.changeHealth.metrics.repeatRegionTouches, 2);

  const hotspots = runFsharp(root, ["hotspots"]);
  assert.equal(hotspots.status, 0, hotspots.stderr);
  const report = JSON.parse(hotspots.stdout);
  const file = report.files.find((item) => item.path === "src/Hot.fs");
  const region = report.regions.find((item) => item.path === "src/Hot.fs");
  assert.equal(file.touches, 2);
  assert.equal(region.touches, 2);
  assert.ok(region.startLine <= 3 && region.endLine >= 3);
});


test("F# change-health emits coded findings when repository thresholds are exceeded", (t) => {
  const root = fixture(t, "threshold-fsharp");
  const policyPath = path.join(root, "telemetry", "change-health.json");
  const policy = JSON.parse(fs.readFileSync(policyPath, "utf8"));
  policy.thresholds.filesChanged = { warning: 0, error: 10 };
  fs.writeFileSync(policyPath, JSON.stringify(policy, null, 2) + "\n");

  execFileSync("dotnet", [fsharpCli, "--root", root, "work", "start", "--id", "WI-THRESHOLD", "--occurred-at", "2026-09-23T12:00:00.000Z", "--type", "task"]);
  fs.writeFileSync(path.join(root, "CHANGE.txt"), "one changed file\n");

  const result = runFsharp(root, ["finalize", "WI-THRESHOLD"]);
  assert.equal(result.status, 0, result.stderr);
  const record = JSON.parse(result.stdout);
  const finding = record.repository.changeHealth.findings.find((item) => item.code === "PRAXIS-CHG-001");

  assert.equal(record.repository.changeHealth.status, "warning");
  assert.equal(finding.metric, "filesChanged");
  assert.equal(finding.severity, "warning");
  assert.equal(finding.actual, 1);
  assert.equal(finding.threshold, 0);
  assert.equal(metricValue(record, "code.threshold_warnings"), 1);
  assert.equal(metricValue(record, "code.threshold_errors"), 0);
});
