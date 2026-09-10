import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { startWork, transition } from "../tools/ros_cli.mjs";
import { finalizeExecution } from "../tools/ros_telemetry.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-finalize-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
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

function readExecutions(root) {
  const dir = path.join(root, ".ros", "telemetry", "executions");
  return fs.existsSync(dir) ? fs.readdirSync(dir).sort().map((name) => JSON.parse(fs.readFileSync(path.join(dir, name), "utf8"))) : [];
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
  const nodeRoot = fixture(t, "no-target-node");
  const fsharpRoot = fixture(t, "no-target-fsharp");

  startWork(nodeRoot, ["WI-A"], { type: "task" });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);

  for (const root of [nodeRoot, fsharpRoot]) {
    fs.writeFileSync(path.join(root, "IMPLEMENTATION-NOTES.md"), "Implemented.\n");
  }

  const nodeRecord = finalizeExecution(nodeRoot, undefined, {});
  const fsharpResult = runFsharp(fsharpRoot, ["finalize"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.equal(nodeRecord.status, "finalized");
  assert.equal(fsharpRecord.status, "finalized");
  assert.equal(nodeRecord.workItemId, "WI-A");
  assert.equal(fsharpRecord.workItemId, "WI-A");

  for (const id of GIT_CHANGE_METRICS) {
    assert.equal(metricValue(nodeRecord, id), metricValue(fsharpRecord, id), `metric ${id} value mismatch`);
  }
  assert.equal(metricValue(nodeRecord, "documentation.files_changed"), 1);
  assert.equal(metricValue(fsharpRecord, "documentation.files_changed"), 1);
});

test("F# telemetry finalize with a work-item target matches by workItemId regardless of status, taking the latest startedAt", (t) => {
  const nodeRoot = fixture(t, "workitem-node");
  const fsharpRoot = fixture(t, "workitem-fsharp");

  for (const root of [nodeRoot, fsharpRoot]) {
    writeFixtureExecution(root, "EXE-OLD", "WI-A", "finalized", "2026-01-01T00:00:00.000Z");
    writeFixtureExecution(root, "EXE-NEW", "WI-A", "active", "2026-01-01T00:05:00.000Z");
  }

  const nodeRecord = finalizeExecution(nodeRoot, "WI-A", {});
  const fsharpResult = runFsharp(fsharpRoot, ["finalize", "WI-A"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.equal(nodeRecord.executionId, "EXE-NEW");
  assert.equal(fsharpRecord.executionId, "EXE-NEW");
  assert.equal(nodeRecord.status, "finalized");
  assert.equal(fsharpRecord.status, "finalized");

  const nodeOld = readExecution(nodeRoot, "EXE-OLD");
  const fsharpOld = readExecution(fsharpRoot, "EXE-OLD");
  assert.equal(nodeOld.status, "finalized");
  assert.equal(fsharpOld.status, "finalized");
  assert.equal(nodeOld.finalizedAt, "2026-01-01T00:00:00.000Z");
  assert.equal(fsharpOld.finalizedAt, "2026-01-01T00:00:00.000Z");
});

test("F# telemetry finalize with an EXE-prefixed target resolves by exact execution id", (t) => {
  const nodeRoot = fixture(t, "exe-target-node");
  const fsharpRoot = fixture(t, "exe-target-fsharp");

  for (const root of [nodeRoot, fsharpRoot]) {
    writeFixtureExecution(root, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
    writeFixtureExecution(root, "EXE-2", "WI-B", "active", "2026-01-01T00:00:01.000Z");
  }

  const nodeRecord = finalizeExecution(nodeRoot, "EXE-1", {});
  const fsharpResult = runFsharp(fsharpRoot, ["finalize", "EXE-1"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.equal(nodeRecord.executionId, "EXE-1");
  assert.equal(fsharpRecord.executionId, "EXE-1");
  assert.equal(nodeRecord.status, "finalized");
  assert.equal(fsharpRecord.status, "finalized");

  const nodeOther = readExecution(nodeRoot, "EXE-2");
  const fsharpOther = readExecution(fsharpRoot, "EXE-2");
  assert.equal(nodeOther.status, "active");
  assert.equal(fsharpOther.status, "active");
});

test("F# telemetry finalize with no target and zero active-or-blocked work items rejects with production's exact ambiguity message", (t) => {
  const nodeRoot = fixture(t, "ambiguous-zero-node");
  const fsharpRoot = fixture(t, "ambiguous-zero-fsharp");

  startWork(nodeRoot, ["WI-A"], { type: "task" });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);

  for (const root of [nodeRoot, fsharpRoot]) {
    fs.writeFileSync(path.join(root, "IMPLEMENTATION-NOTES.md"), "Implemented.\n");
    fs.writeFileSync(path.join(root, "TESTS-NOTES.md"), "Tested.\n");
  }

  const evidence = [
    { type: "implementation", path: "IMPLEMENTATION-NOTES.md" },
    { type: "tests", path: "TESTS-NOTES.md" }
  ];

  transition(nodeRoot, "complete", ["WI-A"], { evidence });
  execFileSync("dotnet", [
    fsharpCli, "--root", fsharpRoot, "work", "complete", "--id", "WI-A", "--occurred-at", "2026-09-10T18:05:00.000Z",
    "--evidence", "implementation=IMPLEMENTATION-NOTES.md", "--evidence", "tests=TESTS-NOTES.md"
  ]);

  let nodeMessage;
  try {
    finalizeExecution(nodeRoot, undefined, {});
  } catch (error) {
    nodeMessage = error.message;
  }
  const fsharpResult = runFsharp(fsharpRoot, ["finalize"]);

  assert.equal(fsharpResult.status, 1);
  assert.equal(nodeMessage, "telemetry target is ambiguous; provide a work-item or execution ID");
  assert.match(fsharpResult.stderr, /telemetry target is ambiguous; provide a work-item or execution ID/);
});

test("F# telemetry finalize with no target and multiple active-or-blocked work items rejects with the same ambiguity message", (t) => {
  const nodeRoot = fixture(t, "ambiguous-many-node");
  const fsharpRoot = fixture(t, "ambiguous-many-fsharp");

  startWork(nodeRoot, ["WI-A", "WI-B"], { type: "task" });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-B", "--occurred-at", "2026-09-10T18:00:01.000Z", "--type", "task"]);

  let nodeMessage;
  try {
    finalizeExecution(nodeRoot, undefined, {});
  } catch (error) {
    nodeMessage = error.message;
  }
  const fsharpResult = runFsharp(fsharpRoot, ["finalize"]);

  assert.equal(fsharpResult.status, 1);
  assert.equal(nodeMessage, "telemetry target is ambiguous; provide a work-item or execution ID");
  assert.match(fsharpResult.stderr, /telemetry target is ambiguous; provide a work-item or execution ID/);
});

test("F# telemetry finalize rejects an unknown target with production's exact message", (t) => {
  const nodeRoot = fixture(t, "not-found-node");
  const fsharpRoot = fixture(t, "not-found-fsharp");

  let nodeMessage;
  try {
    finalizeExecution(nodeRoot, "WI-GHOST", {});
  } catch (error) {
    nodeMessage = error.message;
  }
  const fsharpResult = runFsharp(fsharpRoot, ["finalize", "WI-GHOST"]);

  assert.equal(fsharpResult.status, 1);
  assert.equal(nodeMessage, "telemetry execution 'WI-GHOST' was not found");
  assert.match(fsharpResult.stderr, /telemetry execution 'WI-GHOST' was not found/);
});

test("F# telemetry finalize returns an already-finalized resolved execution untouched, matching production's race-tolerant fast path", (t) => {
  const nodeRoot = fixture(t, "already-finalized-node");
  const fsharpRoot = fixture(t, "already-finalized-fsharp");

  for (const root of [nodeRoot, fsharpRoot]) {
    writeFixtureExecution(root, "EXE-1", "WI-A", "finalized", "2026-01-01T00:00:00.000Z");
  }

  const nodeBefore = fs.readFileSync(path.join(nodeRoot, ".ros", "telemetry", "executions", "EXE-1.json"), "utf8");
  const fsharpBefore = fs.readFileSync(path.join(fsharpRoot, ".ros", "telemetry", "executions", "EXE-1.json"), "utf8");

  const nodeRecord = finalizeExecution(nodeRoot, "EXE-1", {});
  const fsharpResult = runFsharp(fsharpRoot, ["finalize", "EXE-1"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.equal(nodeRecord.status, "finalized");
  assert.equal(fsharpRecord.status, "finalized");

  const nodeAfter = fs.readFileSync(path.join(nodeRoot, ".ros", "telemetry", "executions", "EXE-1.json"), "utf8");
  const fsharpAfter = fs.readFileSync(path.join(fsharpRoot, ".ros", "telemetry", "executions", "EXE-1.json"), "utf8");
  assert.equal(nodeBefore, nodeAfter);
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
