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
  t.after(() => {
    try {
      fs.rmSync(root, { recursive: true, force: true });
    } catch {
      // Cleanup best-effort: a leftover temp dir under CI I/O contention isn't a test failure.
    }
  });
  initializeProject({ target: root, project: "Adapter Publish Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function fsharpWork(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", ...args], { cwd: repositoryRoot, encoding: "utf8" });
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

// Real (not backdated) timestamps: a new execution's own startedAt is always
// production's real wall clock, so a synthetic --occurred-at earlier than
// "now" on a later transition could spuriously fail a chronological-order
// check elsewhere.
const at = () => new Date().toISOString();

test("F# adapter publish appends events and retains attribution once, matching production", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharp = fixture(t, "fsharp");

  assert.equal(fsharpWork(fsharp, ["begin", "--id", "FEAT-142", "--id", "OBL-009", "--occurred-at", at()]).status, 0);

  const fsharpFirst = runFsharp(fsharp, fsharpCliArgs(fsharp, ".ros/mock/events.jsonl"));
  assert.equal(fsharpFirst.status, 0, fsharpFirst.stderr);
  assert.match(fsharpFirst.stdout, /published 3 event/);

  const fsharpSecond = runFsharp(fsharp, fsharpCliArgs(fsharp, ".ros/mock/events.jsonl"));
  assert.match(fsharpSecond.stdout, /published 0 event/);

  assert.equal(eventCount(fsharp, ".ros/mock/events.jsonl"), 3);

  const fsharpReceipts = readReceipts(fsharp);
  assert.equal(Object.keys(fsharpReceipts).length, 3);
  assert.ok(Object.values(fsharpReceipts).every((r) => r.status === "success"));
});

test("F# adapter publish rejects a write failure without creating publications.json, matching production", (t) => {
  const fsharp = fixture(t, "failure-fsharp");

  assert.equal(fsharpWork(fsharp, ["begin", "--id", "TASK-FAILURE", "--occurred-at", at()]).status, 0);
  fs.writeFileSync(path.join(fsharp, "not-a-directory"), "occupied\n");

  const fsharpResult = runFsharp(fsharp, fsharpCliArgs(fsharp, "not-a-directory/events.jsonl"));
  assert.equal(fsharpResult.status, 1);
  assert.doesNotMatch(fsharpResult.stdout, /published \d/);
  assert.equal(fs.existsSync(path.join(fsharp, ".ros", "publications.json")), false);
});

test("F# adapter publish rejects a missing --target with production's exact message", (t) => {
  const fsharp = fixture(t, "missing-fsharp");

  const fsharpResult = runFsharp(fsharp, ["dotnet", fsharpCli, "--root", fsharp, "adapter", "publish"]);
  assert.equal(fsharpResult.status, 1);
  assert.equal(fsharpResult.stderr.trim(), "ERROR adapter publish requires --target");
});

test("F# adapter publish with zero source events still creates an empty publications.json and the target directory, matching production", (t) => {
  const fsharp = fixture(t, "zero-fsharp");
  fs.rmSync(path.join(fsharp, ".ros", "events", "events.jsonl"), { force: true });

  const fsharpResult = runFsharp(fsharp, fsharpCliArgs(fsharp, ".ros/mock/events.jsonl"));
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  assert.match(fsharpResult.stdout, /published 0 event\(s\); 0 duplicate/);
  assert.deepEqual(readReceipts(fsharp), {});
  assert.equal(fs.existsSync(path.join(fsharp, ".ros", "mock", "events.jsonl")), false);
});
