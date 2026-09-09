import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { updateWork } from "../tools/ros_cli.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-work-update-differential-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Work Update Differential" });

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

function runFsharp(root, id, options = {}) {
  const args = [fsharpCli, "--root", root, "work", "update", "--id", id, "--occurred-at", "2026-09-09T18:00:00.000Z"];
  if (options.title !== undefined) args.push("--title", options.title);
  if (options.description !== undefined) args.push("--description", options.description);
  if (options.priority !== undefined) args.push("--priority", options.priority);
  for (const tag of options.tags ?? []) args.push("--tag", tag);
  const result = spawnSync("dotnet", args, { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, json: result.stdout ? JSON.parse(result.stdout) : null, stderr: result.stderr };
}

const fixtureQueue = {
  schemaVersion: "1.0.0",
  repository: "repository",
  nextSeq: 2,
  items: [
    {
      id: "WI-0001",
      title: 'It\'s a "quoted" title',
      description: null,
      tags: ["code"],
      priority: "high",
      status: "ready",
      attachments: [{ id: "att-1" }],
      createdAt: "2026-01-01T00:00:00.000Z",
      updatedAt: "2026-01-01T00:00:00.000Z",
      createdBy: "unknown",
      source: "manual",
      sourceReference: null
    }
  ]
};

test("F# work update matches production's real write for title/description/priority, preserving untouched fields and items", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  writeQueue(nodeRoot, fixtureQueue);
  writeQueue(fsharpRoot, fixtureQueue);

  updateWork(nodeRoot, "WI-0001", { title: "  New title  ", description: "  new desc  ", priority: "low" });
  const fsharp = runFsharp(fsharpRoot, "WI-0001", { title: "  New title  ", description: "  new desc  ", priority: "low" });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
  assert.equal(readMarkdown(fsharpRoot), readMarkdown(nodeRoot));
});

test("F# work update matches production's real write when clearing a description to whitespace", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  writeQueue(nodeRoot, fixtureQueue);
  writeQueue(fsharpRoot, fixtureQueue);

  updateWork(nodeRoot, "WI-0001", { description: "   " });
  const fsharp = runFsharp(fsharpRoot, "WI-0001", { description: "   " });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.equal(readQueue(nodeRoot).items[0].description, null);
  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
});

test("F# work update matches production's real write when omitting --tag entirely, leaving tags untouched", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  writeQueue(nodeRoot, fixtureQueue);
  writeQueue(fsharpRoot, fixtureQueue);

  updateWork(nodeRoot, "WI-0001", { title: "Only title changes" });
  const fsharp = runFsharp(fsharpRoot, "WI-0001", { title: "Only title changes" });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(readQueue(nodeRoot).items[0].tags, ["code"]);
  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
});

test("F# work update matches production's real upsert for an id known only to the live context", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  const emptyQueue = { schemaVersion: "1.0.0", repository: "repository", nextSeq: 1, items: [] };
  writeQueue(nodeRoot, emptyQueue);
  writeQueue(fsharpRoot, emptyQueue);

  updateWork(nodeRoot, "ROS-INSTALL-1-2-1", { tags: ["upserted"] });
  const fsharp = runFsharp(fsharpRoot, "ROS-INSTALL-1-2-1", { tags: ["upserted"] });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
  assert.equal(readMarkdown(fsharpRoot), readMarkdown(nodeRoot));
});

test("F# work update rejects an id in neither the queue nor the live context, exactly like production", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  writeQueue(nodeRoot, fixtureQueue);
  writeQueue(fsharpRoot, fixtureQueue);

  let nodeError = null;
  try {
    updateWork(nodeRoot, "WI-9999", { title: "x" });
  } catch (error) {
    nodeError = error.message;
  }

  const fsharp = runFsharp(fsharpRoot, "WI-9999", { title: "x" });
  assert.equal(fsharp.status, 1);
  assert.equal(nodeError, "work item 'WI-9999' was not found");
  assert.equal(fsharp.stderr.trim(), `ERROR ${nodeError}`);
  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
});

test("F# work update rejects an empty title exactly like production, writing nothing", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  writeQueue(nodeRoot, fixtureQueue);
  writeQueue(fsharpRoot, fixtureQueue);

  let nodeError = null;
  try {
    updateWork(nodeRoot, "WI-0001", { title: "   " });
  } catch (error) {
    nodeError = error.message;
  }

  const fsharp = runFsharp(fsharpRoot, "WI-0001", { title: "   " });
  assert.equal(fsharp.status, 1);
  assert.equal(nodeError, "title cannot be empty");
  assert.equal(fsharp.stderr.trim(), `ERROR ${nodeError}`);
  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
});
