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

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-adapter-publish-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Adapter Publish Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function ros(root, args) {
  const result = spawnSync("node", [path.join(root, "tools", "ros_cli.mjs"), ...args], { cwd: root, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function fsharpCliArgs(root, target) {
  return ["dotnet", fsharpCli, "--root", root, "adapter", "publish", "--target", target];
}

function runFsharp(root, args) {
  const [cmd, ...rest] = args;
  const result = spawnSync(cmd, rest, { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function readReceipts(root) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "publications.json"), "utf8"));
}

function eventCount(root, target) {
  const file = path.join(root, target);
  if (!fs.existsSync(file)) return 0;
  return fs.readFileSync(file, "utf8").split(/\r?\n/).filter(Boolean).length;
}

test("F# adapter publish appends events and retains attribution once, matching production", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const node = fixture(t, "node");
  const fsharp = fixture(t, "fsharp");

  assert.equal(ros(node, ["work", "begin", "FEAT-142", "OBL-009"]).status, 0);
  assert.equal(ros(fsharp, ["work", "begin", "FEAT-142", "OBL-009"]).status, 0);

  const nodeFirst = ros(node, ["adapter", "publish", "--target", ".ros/mock/events.jsonl"]);
  const fsharpFirst = runFsharp(fsharp, fsharpCliArgs(fsharp, ".ros/mock/events.jsonl"));
  assert.equal(nodeFirst.status, 0, nodeFirst.stderr);
  assert.equal(fsharpFirst.status, 0, fsharpFirst.stderr);
  assert.match(nodeFirst.stdout, /published 3 event/);
  assert.match(fsharpFirst.stdout, /published 3 event/);

  const nodeSecond = ros(node, ["adapter", "publish", "--target", ".ros/mock/events.jsonl"]);
  const fsharpSecond = runFsharp(fsharp, fsharpCliArgs(fsharp, ".ros/mock/events.jsonl"));
  assert.match(nodeSecond.stdout, /published 0 event/);
  assert.match(fsharpSecond.stdout, /published 0 event/);

  assert.equal(eventCount(node, ".ros/mock/events.jsonl"), 3);
  assert.equal(eventCount(fsharp, ".ros/mock/events.jsonl"), 3);

  const nodeReceipts = readReceipts(node);
  const fsharpReceipts = readReceipts(fsharp);
  assert.equal(Object.keys(nodeReceipts).length, 3);
  assert.equal(Object.keys(fsharpReceipts).length, 3);
  assert.ok(Object.values(nodeReceipts).every((r) => r.status === "success"));
  assert.ok(Object.values(fsharpReceipts).every((r) => r.status === "success"));
});

test("F# adapter publish rejects a write failure without creating publications.json, matching production", (t) => {
  const node = fixture(t, "failure-node");
  const fsharp = fixture(t, "failure-fsharp");

  assert.equal(ros(node, ["work", "begin", "TASK-FAILURE"]).status, 0);
  assert.equal(ros(fsharp, ["work", "begin", "TASK-FAILURE"]).status, 0);
  fs.writeFileSync(path.join(node, "not-a-directory"), "occupied\n");
  fs.writeFileSync(path.join(fsharp, "not-a-directory"), "occupied\n");

  const nodeResult = ros(node, ["adapter", "publish", "--target", "not-a-directory/events.jsonl"]);
  const fsharpResult = runFsharp(fsharp, fsharpCliArgs(fsharp, "not-a-directory/events.jsonl"));
  assert.equal(nodeResult.status, 1);
  assert.equal(fsharpResult.status, 1);
  assert.doesNotMatch(nodeResult.stdout, /published \d/);
  assert.doesNotMatch(fsharpResult.stdout, /published \d/);
  assert.equal(fs.existsSync(path.join(node, ".ros", "publications.json")), false);
  assert.equal(fs.existsSync(path.join(fsharp, ".ros", "publications.json")), false);
});

test("F# adapter publish rejects a missing --target with production's exact message", (t) => {
  const node = fixture(t, "missing-node");
  const fsharp = fixture(t, "missing-fsharp");

  const nodeResult = ros(node, ["adapter", "publish"]);
  const fsharpResult = runFsharp(fsharp, ["dotnet", fsharpCli, "--root", fsharp, "adapter", "publish"]);
  assert.equal(nodeResult.status, 1);
  assert.equal(fsharpResult.status, 1);
  assert.match(nodeResult.stderr, /adapter publish requires --target/);
  assert.equal(fsharpResult.stderr.trim(), "ERROR adapter publish requires --target");
});

test("F# adapter publish with zero source events still creates an empty publications.json and the target directory, matching production", (t) => {
  const node = fixture(t, "zero-node");
  const fsharp = fixture(t, "zero-fsharp");
  fs.rmSync(path.join(node, ".ros", "events", "events.jsonl"), { force: true });
  fs.rmSync(path.join(fsharp, ".ros", "events", "events.jsonl"), { force: true });

  const nodeResult = ros(node, ["adapter", "publish", "--target", ".ros/mock/events.jsonl"]);
  const fsharpResult = runFsharp(fsharp, fsharpCliArgs(fsharp, ".ros/mock/events.jsonl"));
  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  assert.match(nodeResult.stdout, /published 0 event\(s\); 0 duplicate/);
  assert.match(fsharpResult.stdout, /published 0 event\(s\); 0 duplicate/);
  assert.deepEqual(readReceipts(node), {});
  assert.deepEqual(readReceipts(fsharp), {});
  assert.equal(fs.existsSync(path.join(node, ".ros", "mock", "events.jsonl")), false);
  assert.equal(fs.existsSync(path.join(fsharp, ".ros", "mock", "events.jsonl")), false);
});
