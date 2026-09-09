import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { backlogTransition } from "../tools/ros_cli.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

const fixtureItems = [
  {
    id: "WI-0001",
    title: 'It\'s a "quoted" title',
    description: null,
    tags: ["code", "testing"],
    priority: "high",
    status: "ready",
    attachments: [{ id: "att-1", name: "x.txt", size: 12, contentType: "text/plain", uploadedAt: "2026-01-01T00:00:00.000Z" }],
    createdAt: "2026-01-01T00:00:00.000Z",
    updatedAt: "2026-01-01T00:00:00.000Z",
    createdBy: "unknown",
    source: "manual",
    sourceReference: null
  },
  {
    id: "WI-0002",
    title: "Second",
    tags: [],
    priority: null,
    status: "ready",
    createdAt: "2026-01-01T00:00:00.000Z",
    updatedAt: "2026-01-01T00:00:00.000Z"
  }
];

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-backlog-transition-differential-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Backlog Transition Differential" });

  const configFile = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(configFile, "utf8"));
  config.telemetry.enabled = false;
  fs.writeFileSync(configFile, `${JSON.stringify(config, null, 2)}\n`);

  fs.writeFileSync(
    path.join(root, ".ros", "work", "queue.json"),
    `${JSON.stringify({ schemaVersion: "1.0.0", repository: "repository", nextSeq: 3, items: structuredClone(fixtureItems) }, null, 2)}\n`
  );
  return root;
}

function readQueue(root) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "work", "queue.json"), "utf8"));
}

function readMarkdown(root) {
  return fs.readFileSync(path.join(root, ".ros", "work", "queue.md"), "utf8");
}

function withoutClockFields(queue) {
  return { ...queue, items: queue.items.map(({ updatedAt, ...rest }) => rest) };
}

function runFsharp(root, id, action, options = {}) {
  const args = [
    fsharpCli, "--root", root, "work", "backlog-transition",
    "--id", id, "--action", action, "--occurred-at", "2026-09-09T18:00:00.000Z"
  ];
  if (options.reason) args.push("--reason", options.reason);
  const result = spawnSync("dotnet", args, { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, json: result.stdout ? JSON.parse(result.stdout) : null, stderr: result.stderr };
}

test("F# work backlog-transition matches production's real queue.json/queue.md write for a block, including untouched items and non-ASCII-safe escaping", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);

  backlogTransition(nodeRoot, "block", "WI-0001", { reason: "needs review" });
  const fsharp = runFsharp(fsharpRoot, "WI-0001", "block", { reason: "needs review" });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
  assert.equal(readMarkdown(fsharpRoot), readMarkdown(nodeRoot));
  assert.ok(!readQueue(fsharpRoot).items[0].updatedAt.includes("\\u0027"), "sanity: updatedAt itself never contains an apostrophe");
});

test("F# work backlog-transition matches production's real write for ready, clearing blockedReason entirely", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  backlogTransition(nodeRoot, "block", "WI-0001", { reason: "needs review" });
  runFsharp(fsharpRoot, "WI-0001", "block", { reason: "needs review" });

  backlogTransition(nodeRoot, "ready", "WI-0001", {});
  const fsharp = runFsharp(fsharpRoot, "WI-0001", "ready", {});
  assert.equal(fsharp.status, 0, fsharp.stderr);

  const nodeQueue = readQueue(nodeRoot);
  const fsharpQueue = readQueue(fsharpRoot);
  assert.ok(!("blockedReason" in nodeQueue.items[0]), "sanity: production itself removes blockedReason on ready");
  assert.deepEqual(withoutClockFields(fsharpQueue), withoutClockFields(nodeQueue));
  assert.equal(readMarkdown(fsharpRoot), readMarkdown(nodeRoot));
});

test("F# work backlog-transition matches production's real write for abandon with a reason", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);

  backlogTransition(nodeRoot, "abandon", "WI-0002", { reason: "no longer needed" });
  const fsharp = runFsharp(fsharpRoot, "WI-0002", "abandon", { reason: "no longer needed" });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
  assert.equal(readMarkdown(fsharpRoot), readMarkdown(nodeRoot));
});

test("F# work backlog-transition rejects an illegal transition exactly like production, writing nothing", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  backlogTransition(nodeRoot, "abandon", "WI-0001", {});
  runFsharp(fsharpRoot, "WI-0001", "abandon", {});

  let nodeError = null;
  try {
    backlogTransition(nodeRoot, "ready", "WI-0001", {});
  } catch (error) {
    nodeError = error.message;
  }

  const fsharp = runFsharp(fsharpRoot, "WI-0001", "ready", {});
  assert.equal(fsharp.status, 1);
  assert.equal(nodeError, "cannot ready backlog item 'WI-0001' from 'abandoned'");
  assert.equal(fsharp.stderr.trim(), `ERROR ${nodeError}`);
  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
});

test("F# work backlog-transition rejects a reasonless block exactly like production", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);

  let nodeError = null;
  try {
    backlogTransition(nodeRoot, "block", "WI-0001", {});
  } catch (error) {
    nodeError = error.message;
  }

  const fsharp = runFsharp(fsharpRoot, "WI-0001", "block", {});
  assert.equal(fsharp.status, 1);
  assert.equal(nodeError, "block requires --reason");
  assert.equal(fsharp.stderr.trim(), `ERROR ${nodeError}`);
});

test("F# work backlog-transition rejects an unknown id exactly like production", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);

  let nodeError = null;
  try {
    backlogTransition(nodeRoot, "ready", "WI-9999", {});
  } catch (error) {
    nodeError = error.message;
  }

  const fsharp = runFsharp(fsharpRoot, "WI-9999", "ready", {});
  assert.equal(fsharp.status, 1);
  assert.equal(nodeError, "'WI-9999' is not a captured local work item");
  assert.equal(fsharp.stderr.trim(), `ERROR ${nodeError}`);
});
