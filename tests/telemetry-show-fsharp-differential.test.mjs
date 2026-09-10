import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { startWork } from "../tools/ros_cli.mjs";
import { showTelemetry, TELEMETRY_ADAPTERS } from "../tools/ros_telemetry.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-show-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Telemetry Show Differential" });
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

const VOLATILE_KEYS = new Set([
  "executionId", "startedAt", "discoveredAt", "lastAssessedAt", "recordedAt", "collectedAt",
  "measurementId", "commit", "branch", "dirtyPaths", "dirty", "commits", "occurredAt",
  "eventId", "updatedAt", "telemetryExecutionIds", "telemetryExecutions", "completedAt",
  "createdAt", "finalizedAt", "startCommit", "endCommit", "sessionId"
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

test("F# telemetry adapters matches production's static catalog exactly", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-telemetry-adapters-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));

  const fsharpResult = runFsharp(root, ["adapters"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  assert.deepEqual(JSON.parse(fsharpResult.stdout), TELEMETRY_ADAPTERS);
});

test("F# telemetry show with no target lists every execution record, matching production's real showTelemetry", (t) => {
  const nodeRoot = fixture(t, "list-node");
  const fsharpRoot = fixture(t, "list-fsharp");

  startWork(nodeRoot, ["WI-A"], { type: "task" });
  startWork(nodeRoot, ["WI-B"], { type: "research" });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-B", "--occurred-at", "2026-09-10T18:00:01.000Z", "--type", "research"]);

  const nodeRecords = showTelemetry(nodeRoot, undefined);
  const fsharpResult = runFsharp(fsharpRoot, ["show"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecords = JSON.parse(fsharpResult.stdout);

  assert.equal(nodeRecords.length, 2);
  assert.equal(fsharpRecords.length, 2);
  assert.deepEqual(stripVolatile(nodeRecords), stripVolatile(fsharpRecords));
  assert.equal(nodeRecords[0].workItemId, "WI-A");
  assert.equal(fsharpRecords[0].workItemId, "WI-A");
  assert.equal(nodeRecords[1].workItemId, "WI-B");
  assert.equal(fsharpRecords[1].workItemId, "WI-B");
});

test("F# telemetry show with no executions yet lists an empty array, matching production", (t) => {
  const nodeRoot = fixture(t, "empty-node");
  const fsharpRoot = fixture(t, "empty-fsharp");

  const nodeRecords = showTelemetry(nodeRoot, undefined);
  const fsharpResult = runFsharp(fsharpRoot, ["show"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  assert.deepEqual(nodeRecords, []);
  assert.deepEqual(JSON.parse(fsharpResult.stdout), []);
});

test("F# telemetry show TARGET filters to a matching work item, and an unmatched id returns an empty array, matching production", (t) => {
  const nodeRoot = fixture(t, "workitem-node");
  const fsharpRoot = fixture(t, "workitem-fsharp");

  startWork(nodeRoot, ["WI-A"], { type: "task" });
  startWork(nodeRoot, ["WI-B"], { type: "task" });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-B", "--occurred-at", "2026-09-10T18:00:01.000Z", "--type", "task"]);

  const nodeRecords = showTelemetry(nodeRoot, "WI-A");
  const fsharpResult = runFsharp(fsharpRoot, ["show", "WI-A"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecords = JSON.parse(fsharpResult.stdout);

  assert.equal(nodeRecords.length, 1);
  assert.equal(fsharpRecords.length, 1);
  assert.deepEqual(stripVolatile(nodeRecords), stripVolatile(fsharpRecords));
  assert.equal(nodeRecords[0].workItemId, "WI-A");
  assert.equal(fsharpRecords[0].workItemId, "WI-A");

  const nodeGhost = showTelemetry(nodeRoot, "WI-GHOST");
  const fsharpGhostResult = runFsharp(fsharpRoot, ["show", "WI-GHOST"]);
  assert.equal(fsharpGhostResult.status, 0, fsharpGhostResult.stderr);
  assert.deepEqual(nodeGhost, []);
  assert.deepEqual(JSON.parse(fsharpGhostResult.stdout), []);
});

test("F# telemetry show EXE-ID resolves the single matching record, and an unknown execution id is rejected with production's exact message", (t) => {
  const nodeRoot = fixture(t, "execid-node");
  const fsharpRoot = fixture(t, "execid-fsharp");

  startWork(nodeRoot, ["WI-A"], { type: "task" });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);

  const nodeExecutionId = showTelemetry(nodeRoot, "WI-A")[0].executionId;
  const fsharpExecutionId = JSON.parse(runFsharp(fsharpRoot, ["show", "WI-A"]).stdout)[0].executionId;

  const nodeRecord = showTelemetry(nodeRoot, nodeExecutionId);
  const fsharpResult = runFsharp(fsharpRoot, ["show", fsharpExecutionId]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(stripVolatile(nodeRecord), stripVolatile(fsharpRecord));
  assert.equal(nodeRecord.workItemId, "WI-A");
  assert.equal(fsharpRecord.workItemId, "WI-A");

  let nodeMessage;
  try {
    showTelemetry(nodeRoot, "EXE-GHOST");
  } catch (error) {
    nodeMessage = error.message;
  }

  const fsharpGhostResult = runFsharp(fsharpRoot, ["show", "EXE-GHOST"]);
  assert.equal(fsharpGhostResult.status, 1);
  assert.match(nodeMessage, /telemetry execution 'EXE-GHOST' was not found/);
  assert.match(fsharpGhostResult.stderr, /telemetry execution 'EXE-GHOST' was not found/);
});
