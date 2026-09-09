import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { startWork } from "../tools/ros_cli.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-work-start-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Work Start Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function writeQueue(root, queue) {
  fs.writeFileSync(path.join(root, ".ros", "work", "queue.json"), `${JSON.stringify(queue, null, 2)}\n`);
}

function readContext(root) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "context", "current.json"), "utf8"));
}

function readEvents(root) {
  const file = path.join(root, ".ros", "events", "events.jsonl");
  return fs.existsSync(file) ? fs.readFileSync(file, "utf8").split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line)) : [];
}

function readExecutions(root) {
  const dir = path.join(root, ".ros", "telemetry", "executions");
  return fs.readdirSync(dir).sort().map((name) => JSON.parse(fs.readFileSync(path.join(dir, name), "utf8")));
}

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", "start", ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

const VOLATILE_KEYS = new Set([
  "executionId", "startedAt", "discoveredAt", "lastAssessedAt", "recordedAt", "collectedAt",
  "measurementId", "commit", "branch", "dirtyPaths", "dirty", "commits", "occurredAt",
  "eventId", "updatedAt", "telemetryExecutionIds", "telemetryExecutions",
  // Pre-existing bootstrap-installation work item state (`ROS-INSTALL-*`),
  // unrelated to `work start`/`begin` -- its own `completedAt` is set once
  // at `initializeProject` time and naturally differs between the two
  // independently-created fixture directories.
  "completedAt"
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

const queueFixture = {
  schemaVersion: "1.0.0",
  repository: "repository",
  nextSeq: 4,
  items: [
    { id: "WI-READY", title: "Ready item", description: null, tags: [], priority: "medium", status: "ready", attachments: [], createdAt: "2026-01-01T00:00:00.000Z", updatedAt: "2026-01-01T00:00:00.000Z" },
    { id: "WI-CAPTURED", title: "Captured item", description: null, tags: [], priority: "medium", status: "captured", attachments: [], createdAt: "2026-01-01T00:00:00.000Z", updatedAt: "2026-01-01T00:00:00.000Z" },
    { id: "WI-ABANDONED", title: "Abandoned item", description: null, tags: [], priority: "medium", status: "abandoned", attachments: [], createdAt: "2026-01-01T00:00:00.000Z", updatedAt: "2026-01-01T00:00:00.000Z" }
  ]
};

test("F# work start matches production's real begin transition for a brand-new work item, including a freshly created telemetry execution", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const nodeRoot = fixture(t, "new-node");
  const fsharpRoot = fixture(t, "new-fsharp");

  startWork(nodeRoot, ["WI-NEW"], { type: "task" });
  const fsharpResult = runFsharp(fsharpRoot, ["--id", "WI-NEW", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const nodeContext = readContext(nodeRoot);
  const fsharpContext = readContext(fsharpRoot);
  assert.deepEqual(stripVolatile(nodeContext), stripVolatile(fsharpContext));
  const nodeItem = nodeContext.workItems.find((item) => item.id === "WI-NEW");
  const fsharpItem = fsharpContext.workItems.find((item) => item.id === "WI-NEW");
  assert.equal(nodeItem.telemetryExecutionIds.length, 1);
  assert.equal(fsharpItem.telemetryExecutionIds.length, 1);

  const nodeEvents = readEvents(nodeRoot);
  const fsharpEvents = readEvents(fsharpRoot);
  assert.deepEqual(stripVolatile(nodeEvents), stripVolatile(fsharpEvents));
  const nodeStarted = nodeEvents.find((event) => event.workItem === "WI-NEW");
  const fsharpStarted = fsharpEvents.find((event) => event.workItem === "WI-NEW");
  assert.equal(nodeStarted.telemetryExecutions.length, 1);
  assert.equal(fsharpStarted.telemetryExecutions.length, 1);

  const nodeExecutions = readExecutions(nodeRoot);
  const fsharpExecutions = readExecutions(fsharpRoot);
  assert.equal(nodeExecutions.length, 1);
  assert.equal(fsharpExecutions.length, 1);
  assert.deepEqual(stripVolatile(nodeExecutions[0]), stripVolatile(fsharpExecutions[0]));
  assert.equal(nodeExecutions[0].capabilities.length, fsharpExecutions[0].capabilities.length);

  const baseline = fsharpExecutions[0].capabilities.find((entry) => entry.metricId === "git.baseline_dirty_files");
  assert.equal(baseline.status, "derived");
  assert.ok(Array.isArray(baseline.history) && baseline.history.length === 1);
  assert.equal(baseline.historyOmitted, undefined);
});

test("F# work start promotes a ready backlog item exactly like production, without touching its own backlog status", (t) => {
  const nodeRoot = fixture(t, "ready-node");
  const fsharpRoot = fixture(t, "ready-fsharp");
  writeQueue(nodeRoot, queueFixture);
  writeQueue(fsharpRoot, queueFixture);

  startWork(nodeRoot, ["WI-READY"], {});
  const fsharpResult = runFsharp(fsharpRoot, ["--id", "WI-READY", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  assert.deepEqual(stripVolatile(readContext(nodeRoot)), stripVolatile(readContext(fsharpRoot)));

  const nodeQueue = JSON.parse(fs.readFileSync(path.join(nodeRoot, ".ros", "work", "queue.json"), "utf8"));
  const fsharpQueue = JSON.parse(fs.readFileSync(path.join(fsharpRoot, ".ros", "work", "queue.json"), "utf8"));
  const readyItem = fsharpQueue.items.find((item) => item.id === "WI-READY");
  assert.equal(readyItem.status, "ready");
  assert.deepEqual(nodeQueue, fsharpQueue);
});

test("F# work start rejects a captured (not-ready) backlog item with production's exact message, leaving context untouched", (t) => {
  const nodeRoot = fixture(t, "captured-node");
  const fsharpRoot = fixture(t, "captured-fsharp");
  writeQueue(nodeRoot, queueFixture);
  writeQueue(fsharpRoot, queueFixture);
  const contextBefore = readContext(fsharpRoot);

  let nodeMessage;
  try {
    startWork(nodeRoot, ["WI-CAPTURED"], {});
  } catch (error) {
    nodeMessage = error.message;
  }

  const fsharpResult = runFsharp(fsharpRoot, ["--id", "WI-CAPTURED", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(nodeMessage, /cannot start backlog item 'WI-CAPTURED' from 'captured'; mark it ready first/);
  assert.match(fsharpResult.stderr, /cannot start backlog item 'WI-CAPTURED' from 'captured'; mark it ready first/);
  assert.deepEqual(readContext(fsharpRoot), contextBefore);
});

test("F# work start rejects an abandoned backlog item with production's exact message", (t) => {
  const nodeRoot = fixture(t, "abandoned-node");
  const fsharpRoot = fixture(t, "abandoned-fsharp");
  writeQueue(nodeRoot, queueFixture);
  writeQueue(fsharpRoot, queueFixture);

  let nodeMessage;
  try {
    startWork(nodeRoot, ["WI-ABANDONED"], {});
  } catch (error) {
    nodeMessage = error.message;
  }

  const fsharpResult = runFsharp(fsharpRoot, ["--id", "WI-ABANDONED", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(nodeMessage, /cannot start backlog item 'WI-ABANDONED': it was abandoned/);
  assert.match(fsharpResult.stderr, /cannot start backlog item 'WI-ABANDONED': it was abandoned/);
});

test("F# work start rejects starting an already-active work item with production's exact illegal-transition message", (t) => {
  const nodeRoot = fixture(t, "active-node");
  const fsharpRoot = fixture(t, "active-fsharp");

  startWork(nodeRoot, ["WI-ACTIVE"], { type: "task" });
  runFsharp(fsharpRoot, ["--id", "WI-ACTIVE", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  let nodeMessage;
  try {
    startWork(nodeRoot, ["WI-ACTIVE"], {});
  } catch (error) {
    nodeMessage = error.message;
  }

  const fsharpResult = runFsharp(fsharpRoot, ["--id", "WI-ACTIVE", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(nodeMessage, /cannot begin 'WI-ACTIVE' from 'active'/);
  assert.match(fsharpResult.stderr, /cannot begin 'WI-ACTIVE' from 'active'/);
});
