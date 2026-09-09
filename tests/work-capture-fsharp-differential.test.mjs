import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { captureWork } from "../tools/ros_cli.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-work-capture-differential-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Work Capture Differential" });

  const configFile = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(configFile, "utf8"));
  config.telemetry.enabled = false;
  fs.writeFileSync(configFile, `${JSON.stringify(config, null, 2)}\n`);
  return root;
}

function writeQueue(root, queue) {
  fs.writeFileSync(path.join(root, ".ros", "work", "queue.json"), `${JSON.stringify(queue, null, 2)}\n`);
}

function readQueue(root) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "work", "queue.json"), "utf8"));
}

function readMarkdown(root) {
  return fs.readFileSync(path.join(root, ".ros", "work", "queue.md"), "utf8");
}

function withoutClockFields(queue) {
  return { ...queue, items: queue.items.map(({ createdAt, updatedAt, ...rest }) => rest) };
}

function runFsharp(root, title, options = {}) {
  const args = [fsharpCli, "--root", root, "work", "capture", "--title", title, "--occurred-at", "2026-09-09T18:00:00.000Z"];
  if (options.id) args.push("--id", options.id);
  if (options.priority) args.push("--priority", options.priority);
  if (options.description) args.push("--description", options.description);
  for (const tag of options.tags ?? []) args.push("--tag", tag);
  if (options.actor) args.push("--actor", options.actor);
  const result = spawnSync("dotnet", args, { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, json: result.stdout ? JSON.parse(result.stdout) : null, stderr: result.stderr };
}

test("F# work capture matches production's real write for an auto-generated id, including untouched items and non-ASCII-safe escaping", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);

  const fixtureQueue = {
    schemaVersion: "1.0.0",
    repository: "repository",
    nextSeq: 3,
    items: [{ id: "WI-0001", title: 'It\'s a "quoted" title', tags: ["code"], priority: "high", status: "ready" }]
  };
  writeQueue(nodeRoot, fixtureQueue);
  writeQueue(fsharpRoot, fixtureQueue);

  captureWork(nodeRoot, "  My new task  ", { priority: "high", tags: ["alpha", "beta"], description: "  details  ", actor: "tester" });
  const fsharp = runFsharp(fsharpRoot, "  My new task  ", { priority: "high", tags: ["alpha", "beta"], description: "  details  ", actor: "tester" });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
  assert.equal(readMarkdown(fsharpRoot), readMarkdown(nodeRoot));
  assert.equal(readQueue(nodeRoot).items[1].id, "WI-0003");
});

test("F# work capture matches production's real write on a completely fresh (missing) queue.json", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  fs.rmSync(path.join(nodeRoot, ".ros", "work", "queue.json"), { force: true });
  fs.rmSync(path.join(nodeRoot, ".ros", "work", "queue.md"), { force: true });
  fs.rmSync(path.join(fsharpRoot, ".ros", "work", "queue.json"), { force: true });
  fs.rmSync(path.join(fsharpRoot, ".ros", "work", "queue.md"), { force: true });

  captureWork(nodeRoot, "First ever", {});
  const fsharp = runFsharp(fsharpRoot, "First ever", {});
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
  assert.equal(readMarkdown(fsharpRoot), readMarkdown(nodeRoot));
});

test("F# work capture matches production's real write for an explicit id, never advancing nextSeq", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  const fixtureQueue = { schemaVersion: "1.0.0", repository: "repository", nextSeq: 5, items: [] };
  writeQueue(nodeRoot, fixtureQueue);
  writeQueue(fsharpRoot, fixtureQueue);

  captureWork(nodeRoot, "Custom", { id: "TASK-CUSTOM" });
  const fsharp = runFsharp(fsharpRoot, "Custom", { id: "TASK-CUSTOM" });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
  assert.equal(readQueue(nodeRoot).nextSeq, 5);
});

test("F# work capture rejects an empty title exactly like production, writing nothing", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);

  let nodeError = null;
  try {
    captureWork(nodeRoot, "   ", {});
  } catch (error) {
    nodeError = error.message;
  }

  const fsharp = runFsharp(fsharpRoot, "   ", {});
  assert.equal(fsharp.status, 1);
  assert.equal(nodeError, "add requires a non-empty title");
  assert.equal(fsharp.stderr.trim(), `ERROR ${nodeError}`);
  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
});

test("F# work capture rejects an invalid priority exactly like production", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);

  let nodeError = null;
  try {
    captureWork(nodeRoot, "x", { priority: "urgent" });
  } catch (error) {
    nodeError = error.message;
  }

  const fsharp = runFsharp(fsharpRoot, "x", { priority: "urgent" });
  assert.equal(fsharp.status, 1);
  assert.equal(nodeError, "invalid priority 'urgent'; use high, medium, or low");
  assert.equal(fsharp.stderr.trim(), `ERROR ${nodeError}`);
});

test("F# work capture rejects a duplicate explicit id exactly like production", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  const fixtureQueue = { schemaVersion: "1.0.0", repository: "repository", nextSeq: 2, items: [{ id: "WI-9000", title: "Existing", tags: [], priority: null, status: "ready" }] };
  writeQueue(nodeRoot, fixtureQueue);
  writeQueue(fsharpRoot, fixtureQueue);

  let nodeError = null;
  try {
    captureWork(nodeRoot, "dup", { id: "WI-9000" });
  } catch (error) {
    nodeError = error.message;
  }

  const fsharp = runFsharp(fsharpRoot, "dup", { id: "WI-9000" });
  assert.equal(fsharp.status, 1);
  assert.equal(nodeError, "work item 'WI-9000' already exists");
  assert.equal(fsharp.stderr.trim(), `ERROR ${nodeError}`);
  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
});
