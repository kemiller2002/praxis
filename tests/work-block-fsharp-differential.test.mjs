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
// implementation (tools/ros_cli.mjs's backlogTransition/blockWork/captureWork/
// startWork) with the exact same call sequence as each test, then frozen here.
// Node is retained in this repository only as the web server's internal
// dependency (DF-ROS-2026-A033) and is no longer executed as a live oracle
// by this test suite.
const GOLDEN = {
  test1Queue: {
    schemaVersion: "1.0.0",
    repository: "work-block-differential",
    nextSeq: 1,
    items: [
      {
        id: "WI-BACKLOG",
        title: "Backlog-only item",
        description: null,
        tags: [],
        priority: "medium",
        status: "blocked",
        attachments: [],
        createdBy: "unknown",
        source: "manual",
        sourceReference: null,
        blockedReason: "waiting on design"
      }
    ]
  },
  test2Context: {
    schemaVersion: "1.0.0",
    protocolVersion: "1.0.0",
    repository: "work-block-differential",
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
        id: "WI-LIVE",
        type: "task",
        state: "blocked",
        semanticState: "blocked",
        evidence: [],
        blockReason: "blocked on review"
      }
    ]
  },
  test3Queue: {
    schemaVersion: "1.0.0",
    repository: "work-block-differential",
    nextSeq: 1,
    items: [
      {
        id: "WI-SPLIT-BACKLOG",
        title: "Backlog item",
        description: null,
        tags: [],
        priority: "medium",
        status: "blocked",
        attachments: [],
        createdBy: "unknown",
        source: "manual",
        sourceReference: null,
        blockedReason: "batch block"
      }
    ]
  },
  test3Context: {
    schemaVersion: "1.0.0",
    protocolVersion: "1.0.0",
    repository: "work-block-differential",
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
        id: "WI-SPLIT-LIVE",
        type: "task",
        state: "blocked",
        semanticState: "blocked",
        evidence: [],
        blockReason: "batch block"
      }
    ]
  },
  test4Message: "block requires --reason",
  test5Message: "work item 'WI-GHOST' is not in repository context"
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-work-block-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 }));
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
  const fsharpRoot = fixture(t, "backlog-fsharp");

  runFsharp(fsharpRoot, "capture", ["--id", "WI-BACKLOG", "--title", "Backlog-only item", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  // block is only legal from "ready", never directly from "captured".
  runFsharp(fsharpRoot, "backlog-transition", ["--id", "WI-BACKLOG", "--action", "ready", "--occurred-at", "2026-09-09T18:01:00.000Z"]);

  const fsharpResult = runFsharp(fsharpRoot, "block", ["--id", "WI-BACKLOG", "--reason", "waiting on design", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpQueue = readQueue(fsharpRoot);
  assert.deepEqual(stripVolatile(fsharpQueue), GOLDEN.test1Queue);

  const fsharpItem = fsharpQueue.items.find((item) => item.id === "WI-BACKLOG");
  assert.equal(fsharpItem.status, "blocked");
  assert.equal(fsharpItem.blockedReason, "waiting on design");

  // The CLI's own printed output is production's raw mixed array -- for a
  // backlog-only id this is the full raw queue item.
  const fsharpOutput = JSON.parse(fsharpResult.stdout);
  assert.equal(fsharpOutput.length, 1);
  assert.equal(fsharpOutput[0].id, "WI-BACKLOG");
  assert.equal(fsharpOutput[0].status, "blocked");
});

test("F# work block blocks an already-live work item exactly like production's live-context branch", (t) => {
  const fsharpRoot = fixture(t, "live-fsharp");

  runFsharp(fsharpRoot, "start", ["--id", "WI-LIVE", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  const fsharpResult = runFsharp(fsharpRoot, "block", ["--id", "WI-LIVE", "--reason", "blocked on review", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpContext = readContext(fsharpRoot);
  assert.deepEqual(stripVolatile(fsharpContext), GOLDEN.test2Context);

  const fsharpItem = fsharpContext.workItems.find((item) => item.id === "WI-LIVE");
  assert.equal(fsharpItem.semanticState, "blocked");
  assert.equal(fsharpItem.blockReason, "blocked on review");

  const fsharpOutput = JSON.parse(fsharpResult.stdout);
  assert.equal(fsharpOutput.length, 1);
  assert.equal(fsharpOutput[0].id, "WI-LIVE");
  assert.equal(fsharpOutput[0].semanticState, "blocked");
});

test("F# work block splits ids between backlog and live context in a single call, matching production's own blockWork", (t) => {
  const fsharpRoot = fixture(t, "split-fsharp");

  runFsharp(fsharpRoot, "capture", ["--id", "WI-SPLIT-BACKLOG", "--title", "Backlog item", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  runFsharp(fsharpRoot, "backlog-transition", ["--id", "WI-SPLIT-BACKLOG", "--action", "ready", "--occurred-at", "2026-09-09T18:01:00.000Z"]);
  runFsharp(fsharpRoot, "start", ["--id", "WI-SPLIT-LIVE", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  const fsharpResult = runFsharp(fsharpRoot, "block", ["--id", "WI-SPLIT-BACKLOG", "--id", "WI-SPLIT-LIVE", "--reason", "batch block", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  assert.deepEqual(stripVolatile(readQueue(fsharpRoot)), GOLDEN.test3Queue);
  assert.deepEqual(stripVolatile(readContext(fsharpRoot)), GOLDEN.test3Context);

  const fsharpOutput = JSON.parse(fsharpResult.stdout);
  assert.equal(fsharpOutput.length, 2);
  const backlogEntry = fsharpOutput.find((entry) => entry.id === "WI-SPLIT-BACKLOG");
  const liveEntry = fsharpOutput.find((entry) => entry.id === "WI-SPLIT-LIVE");
  assert.equal(backlogEntry.status, "blocked");
  assert.equal(liveEntry.semanticState, "blocked");
});

test("F# work block requires --reason once an item is actually eligible to be blocked, matching production's exact message and check order", (t) => {
  const fsharpRoot = fixture(t, "reason-fsharp");

  // Block requires --reason only once the item is legally blockable at all
  // (state "ready"): an illegal-transition rejection fires first for any
  // other state, matching production's own check order exactly.
  runFsharp(fsharpRoot, "capture", ["--id", "WI-REASON", "--title", "Item", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  runFsharp(fsharpRoot, "backlog-transition", ["--id", "WI-REASON", "--action", "ready", "--occurred-at", "2026-09-09T18:01:00.000Z"]);

  assert.match(GOLDEN.test4Message, /block requires --reason/);
  const fsharpResult = runFsharp(fsharpRoot, "block", ["--id", "WI-REASON", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(fsharpResult.stderr, /block requires --reason/);
  const queueBefore = readQueue(fsharpRoot);
  assert.equal(queueBefore.items.find((item) => item.id === "WI-REASON").status, "ready");
});

test("F# work block rejects an id in neither the queue nor the live context, matching production's exact message", (t) => {
  const fsharpRoot = fixture(t, "ghost-fsharp");

  const fsharpResult = runFsharp(fsharpRoot, "block", ["--id", "WI-GHOST", "--reason", "n/a", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(GOLDEN.test5Message, /work item 'WI-GHOST' is not in repository context/);
  assert.match(fsharpResult.stderr, /work item 'WI-GHOST' is not in repository context/);
});
