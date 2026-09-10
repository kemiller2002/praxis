import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { backlogTransition, blockWork, captureWork, startWork } from "../tools/ros_cli.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-work-block-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Work Block Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function readQueue(root) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "work", "queue.json"), "utf8"));
}

function readContext(root) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "context", "current.json"), "utf8"));
}

function runFsharp(root, command, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", command, ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

const VOLATILE_KEYS = new Set([
  "executionId", "startedAt", "discoveredAt", "lastAssessedAt", "recordedAt", "collectedAt",
  "measurementId", "commit", "branch", "dirtyPaths", "dirty", "commits", "occurredAt",
  "eventId", "updatedAt", "telemetryExecutionIds", "telemetryExecutions", "completedAt",
  "createdAt"
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

test("F# work block blocks a not-yet-started backlog item exactly like production's backlog branch", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const nodeRoot = fixture(t, "backlog-node");
  const fsharpRoot = fixture(t, "backlog-fsharp");

  captureWork(nodeRoot, "Backlog-only item", { id: "WI-BACKLOG" });
  runFsharp(fsharpRoot, "capture", ["--id", "WI-BACKLOG", "--title", "Backlog-only item", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  // block is only legal from "ready", never directly from "captured".
  backlogTransition(nodeRoot, "ready", "WI-BACKLOG");
  runFsharp(fsharpRoot, "backlog-transition", ["--id", "WI-BACKLOG", "--action", "ready", "--occurred-at", "2026-09-09T18:01:00.000Z"]);

  blockWork(nodeRoot, ["WI-BACKLOG"], { reason: "waiting on design" });
  const fsharpResult = runFsharp(fsharpRoot, "block", ["--id", "WI-BACKLOG", "--reason", "waiting on design", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const nodeQueue = readQueue(nodeRoot);
  const fsharpQueue = readQueue(fsharpRoot);
  assert.deepEqual(stripVolatile(nodeQueue), stripVolatile(fsharpQueue));

  const nodeItem = nodeQueue.items.find((item) => item.id === "WI-BACKLOG");
  const fsharpItem = fsharpQueue.items.find((item) => item.id === "WI-BACKLOG");
  assert.equal(nodeItem.status, "blocked");
  assert.equal(fsharpItem.status, "blocked");
  assert.equal(nodeItem.blockedReason, "waiting on design");
  assert.equal(fsharpItem.blockedReason, "waiting on design");

  // The CLI's own printed output is production's raw mixed array -- for a
  // backlog-only id this is the full raw queue item.
  const fsharpOutput = JSON.parse(fsharpResult.stdout);
  assert.equal(fsharpOutput.length, 1);
  assert.equal(fsharpOutput[0].id, "WI-BACKLOG");
  assert.equal(fsharpOutput[0].status, "blocked");
});

test("F# work block blocks an already-live work item exactly like production's live-context branch", (t) => {
  const nodeRoot = fixture(t, "live-node");
  const fsharpRoot = fixture(t, "live-fsharp");

  startWork(nodeRoot, ["WI-LIVE"], { type: "task" });
  runFsharp(fsharpRoot, "start", ["--id", "WI-LIVE", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  blockWork(nodeRoot, ["WI-LIVE"], { reason: "blocked on review" });
  const fsharpResult = runFsharp(fsharpRoot, "block", ["--id", "WI-LIVE", "--reason", "blocked on review", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const nodeContext = readContext(nodeRoot);
  const fsharpContext = readContext(fsharpRoot);
  assert.deepEqual(stripVolatile(nodeContext), stripVolatile(fsharpContext));

  const nodeItem = nodeContext.workItems.find((item) => item.id === "WI-LIVE");
  const fsharpItem = fsharpContext.workItems.find((item) => item.id === "WI-LIVE");
  assert.equal(nodeItem.semanticState, "blocked");
  assert.equal(fsharpItem.semanticState, "blocked");
  assert.equal(nodeItem.blockReason, "blocked on review");
  assert.equal(fsharpItem.blockReason, "blocked on review");

  const fsharpOutput = JSON.parse(fsharpResult.stdout);
  assert.equal(fsharpOutput.length, 1);
  assert.equal(fsharpOutput[0].id, "WI-LIVE");
  assert.equal(fsharpOutput[0].semanticState, "blocked");
});

test("F# work block splits ids between backlog and live context in a single call, matching production's own blockWork", (t) => {
  const nodeRoot = fixture(t, "split-node");
  const fsharpRoot = fixture(t, "split-fsharp");

  captureWork(nodeRoot, "Backlog item", { id: "WI-SPLIT-BACKLOG" });
  runFsharp(fsharpRoot, "capture", ["--id", "WI-SPLIT-BACKLOG", "--title", "Backlog item", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  backlogTransition(nodeRoot, "ready", "WI-SPLIT-BACKLOG");
  runFsharp(fsharpRoot, "backlog-transition", ["--id", "WI-SPLIT-BACKLOG", "--action", "ready", "--occurred-at", "2026-09-09T18:01:00.000Z"]);
  startWork(nodeRoot, ["WI-SPLIT-LIVE"], { type: "task" });
  runFsharp(fsharpRoot, "start", ["--id", "WI-SPLIT-LIVE", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  blockWork(nodeRoot, ["WI-SPLIT-BACKLOG", "WI-SPLIT-LIVE"], { reason: "batch block" });
  const fsharpResult = runFsharp(fsharpRoot, "block", ["--id", "WI-SPLIT-BACKLOG", "--id", "WI-SPLIT-LIVE", "--reason", "batch block", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  assert.deepEqual(stripVolatile(readQueue(nodeRoot)), stripVolatile(readQueue(fsharpRoot)));
  assert.deepEqual(stripVolatile(readContext(nodeRoot)), stripVolatile(readContext(fsharpRoot)));

  const fsharpOutput = JSON.parse(fsharpResult.stdout);
  assert.equal(fsharpOutput.length, 2);
  const backlogEntry = fsharpOutput.find((entry) => entry.id === "WI-SPLIT-BACKLOG");
  const liveEntry = fsharpOutput.find((entry) => entry.id === "WI-SPLIT-LIVE");
  assert.equal(backlogEntry.status, "blocked");
  assert.equal(liveEntry.semanticState, "blocked");
});

test("F# work block requires --reason once an item is actually eligible to be blocked, matching production's exact message and check order", (t) => {
  const nodeRoot = fixture(t, "reason-node");
  const fsharpRoot = fixture(t, "reason-fsharp");

  // Block requires --reason only once the item is legally blockable at all
  // (state "ready"): an illegal-transition rejection fires first for any
  // other state, matching production's own check order exactly.
  captureWork(nodeRoot, "Item", { id: "WI-REASON" });
  runFsharp(fsharpRoot, "capture", ["--id", "WI-REASON", "--title", "Item", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  backlogTransition(nodeRoot, "ready", "WI-REASON");
  runFsharp(fsharpRoot, "backlog-transition", ["--id", "WI-REASON", "--action", "ready", "--occurred-at", "2026-09-09T18:01:00.000Z"]);

  let nodeMessage;
  try {
    blockWork(nodeRoot, ["WI-REASON"], {});
  } catch (error) {
    nodeMessage = error.message;
  }

  assert.match(nodeMessage, /block requires --reason/);
  const fsharpResult = runFsharp(fsharpRoot, "block", ["--id", "WI-REASON", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(fsharpResult.stderr, /block requires --reason/);
  const queueBefore = readQueue(fsharpRoot);
  assert.equal(queueBefore.items.find((item) => item.id === "WI-REASON").status, "ready");
});

test("F# work block rejects an id in neither the queue nor the live context, matching production's exact message", (t) => {
  const nodeRoot = fixture(t, "ghost-node");
  const fsharpRoot = fixture(t, "ghost-fsharp");

  let nodeMessage;
  try {
    blockWork(nodeRoot, ["WI-GHOST"], { reason: "n/a" });
  } catch (error) {
    nodeMessage = error.message;
  }

  const fsharpResult = runFsharp(fsharpRoot, "block", ["--id", "WI-GHOST", "--reason", "n/a", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(nodeMessage, /work item 'WI-GHOST' is not in repository context/);
  assert.match(fsharpResult.stderr, /work item 'WI-GHOST' is not in repository context/);
});
