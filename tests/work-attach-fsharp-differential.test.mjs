import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { attachFile } from "../tools/ros_cli.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-work-attach-differential-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Work Attach Differential" });

  const configFile = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(configFile, "utf8"));
  config.telemetry.enabled = false;
  fs.writeFileSync(configFile, `${JSON.stringify(config, null, 2)}\n`);
  fs.writeFileSync(path.join(root, "upload.txt"), "hello world, with a weird name coming");
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
  return {
    ...queue,
    items: queue.items.map(({ createdAt, updatedAt, attachments, ...rest }) => ({
      ...rest,
      attachments: (attachments ?? []).map(({ uploadedAt, ...attachmentRest }) => attachmentRest)
    }))
  };
}

function runFsharp(root, id, files) {
  const args = [fsharpCli, "--root", root, "work", "attach", "--id", id, "--occurred-at", "2026-09-09T18:00:00.000Z"];
  for (const file of files) args.push("--file", file);
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
      attachments: [{ id: "ATT-1", seq: 1, name: "old.txt", file: "1-old.txt", size: 3, contentType: null, uploadedAt: "2026-01-01T00:00:00.000Z" }],
      createdAt: "2026-01-01T00:00:00.000Z",
      updatedAt: "2026-01-01T00:00:00.000Z"
    }
  ]
};

test("F# work attach matches production's real write for a named attachment, preserving prior attachments and non-ASCII-safe escaping", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  writeQueue(nodeRoot, fixtureQueue);
  writeQueue(fsharpRoot, fixtureQueue);

  attachFile(nodeRoot, "WI-0001", { sourcePath: "upload.txt", name: "My Cool File!.txt" });
  const fsharp = runFsharp(fsharpRoot, "WI-0001", ["upload.txt=My Cool File!.txt"]);
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
  assert.equal(readMarkdown(fsharpRoot), readMarkdown(nodeRoot));

  const nodeStoredFile = readQueue(nodeRoot).items[0].attachments[1].file;
  const fsharpStoredFile = readQueue(fsharpRoot).items[0].attachments[1].file;
  assert.equal(fsharpStoredFile, nodeStoredFile);
  assert.deepEqual(
    fs.readFileSync(path.join(fsharpRoot, ".ros", "work", "attachments", "WI-0001", fsharpStoredFile)),
    fs.readFileSync(path.join(nodeRoot, ".ros", "work", "attachments", "WI-0001", nodeStoredFile))
  );
});

test("F# work attach matches production's real write when no name is given, defaulting to the source basename", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  writeQueue(nodeRoot, fixtureQueue);
  writeQueue(fsharpRoot, fixtureQueue);

  attachFile(nodeRoot, "WI-0001", { sourcePath: "upload.txt" });
  const fsharp = runFsharp(fsharpRoot, "WI-0001", ["upload.txt"]);
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.equal(readQueue(nodeRoot).items[0].attachments[1].name, "upload.txt");
  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
});

test("F# work attach matches production's real upsert for an id known only to the live context", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  const emptyQueue = { schemaVersion: "1.0.0", repository: "repository", nextSeq: 1, items: [] };
  writeQueue(nodeRoot, emptyQueue);
  writeQueue(fsharpRoot, emptyQueue);

  attachFile(nodeRoot, "ROS-INSTALL-1-2-1", { sourcePath: "upload.txt" });
  const fsharp = runFsharp(fsharpRoot, "ROS-INSTALL-1-2-1", ["upload.txt"]);
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
  assert.equal(readMarkdown(fsharpRoot), readMarkdown(nodeRoot));
});

test("F# work attach rejects an id in neither the queue nor the live context, exactly like production", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  writeQueue(nodeRoot, fixtureQueue);
  writeQueue(fsharpRoot, fixtureQueue);

  let nodeError = null;
  try {
    attachFile(nodeRoot, "WI-9999", { sourcePath: "upload.txt" });
  } catch (error) {
    nodeError = error.message;
  }

  const fsharp = runFsharp(fsharpRoot, "WI-9999", ["upload.txt"]);
  assert.equal(fsharp.status, 1);
  assert.equal(nodeError, "work item 'WI-9999' was not found");
  assert.equal(fsharp.stderr.trim(), `ERROR ${nodeError}`);
  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), withoutClockFields(readQueue(nodeRoot)));
});

test("F# work attach rejects an invalidly-shaped id exactly like production", (t) => {
  const nodeRoot = fixture(t);
  const fsharpRoot = fixture(t);
  writeQueue(nodeRoot, fixtureQueue);
  writeQueue(fsharpRoot, fixtureQueue);

  let nodeError = null;
  try {
    attachFile(nodeRoot, "not-an-id", { sourcePath: "upload.txt" });
  } catch (error) {
    nodeError = error.message;
  }

  const fsharp = runFsharp(fsharpRoot, "not-an-id", ["upload.txt"]);
  assert.equal(fsharp.status, 1);
  assert.equal(nodeError, "invalid work-item ID 'not-an-id'");
  assert.equal(fsharp.stderr.trim(), `ERROR ${nodeError}`);
});
