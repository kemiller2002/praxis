import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { observeGitStatus, parseGitStatus } from "../tools/ros_git.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

// Golden masters below were captured once from production's own Node
// observeGitStatus with the exact same fixture as each test, then frozen
// here. Node is retained in this repository only as the web server's
// internal dependency (DF-ROS-2026-A033) and is no longer executed as a
// live oracle by this test suite; the two "Node production boundary" tests
// below still exercise ros_git.mjs directly since it remains shipped code.
const GOLDEN = {
  clean: {
    schemaVersion: "1.0.0",
    outcome: "clean",
    changes: [],
    failure: null,
    source: { tool: "git", command: "status", format: "porcelain-v1-z" }
  },
  changed: {
    schemaVersion: "1.0.0",
    outcome: "changed",
    changes: [
      { code: " M", kind: "tracked", index: "unmodified", workTree: "modified", path: "modified.txt", originalPath: null },
      { code: "R ", kind: "tracked", index: "renamed", workTree: "unmodified", path: "renamed.txt", originalPath: "original.txt" },
      { code: "??", kind: "untracked", path: "untracked.txt", originalPath: null }
    ],
    failure: null,
    source: { tool: "git", command: "status", format: "porcelain-v1-z" }
  },
  unavailable: {
    schemaVersion: "1.0.0",
    outcome: "unavailable",
    changes: [],
    failure: {
      operation: "git status",
      reason: "not-repository",
      message: "fatal: not a git repository (or any of the parent directories): .git",
      exitCode: 128
    },
    source: { tool: "git", command: "status", format: "porcelain-v1-z" }
  }
};

function git(root, args) {
  return execFileSync("git", ["-C", root, ...args], { encoding: "utf8" });
}

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-git-differential-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 }));
  git(root, ["init", "-q"]);
  git(root, ["config", "user.email", "test@example.invalid"]);
  git(root, ["config", "user.name", "ROS Test"]);
  fs.writeFileSync(path.join(root, "original.txt"), "original\n");
  fs.writeFileSync(path.join(root, "modified.txt"), "baseline\n");
  git(root, ["add", "."]);
  git(root, ["commit", "-qm", "baseline"]);
  return root;
}

function runFsharp(root) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "git", "status", "--json"], {
    cwd: repositoryRoot,
    encoding: "utf8"
  });
  return { ...result, json: JSON.parse(result.stdout) };
}

function nodeStatus(root) {
  const fields = git(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"]).split("\0").filter(Boolean);
  const changes = [];
  for (let index = 0; index < fields.length; index += 1) {
    const entry = fields[index];
    const code = entry.slice(0, 2);
    const changedPath = entry.slice(3);
    const originalPath = /[RC]/.test(code) ? fields[++index] : null;
    changes.push({ code, path: changedPath, originalPath });
  }
  return changes.sort((left, right) => left.path.localeCompare(right.path));
}

function projected(changes) {
  return changes.map(({ code, path: changedPath, originalPath }) => ({ code, path: changedPath, originalPath }));
}

test("F# Git shadow matches clean porcelain status", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const root = fixture(t);
  const result = runFsharp(root);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.json.outcome, "clean");
  assert.deepEqual(result.json.changes, []);
  assert.deepEqual(result.json, GOLDEN.clean);
});

test("F# Git shadow matches changed paths, statuses, and rename origin", (t) => {
  const root = fixture(t);
  git(root, ["mv", "original.txt", "renamed.txt"]);
  fs.appendFileSync(path.join(root, "modified.txt"), "changed\n");
  fs.writeFileSync(path.join(root, "untracked.txt"), "new\n");

  const expected = nodeStatus(root);
  const result = runFsharp(root);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.json.outcome, "changed");
  assert.deepEqual(projected(result.json.changes), expected);
  assert.deepEqual(result.json, GOLDEN.changed);
  assert.deepEqual(result.json.changes.find(({ code }) => code.startsWith("R")), {
    code: "R ", kind: "tracked", index: "renamed", workTree: "unmodified",
    path: "renamed.txt", originalPath: "original.txt"
  });
});

test("F# Git shadow reports unavailable instead of clean outside a repository", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-git-unavailable-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 }));
  const result = runFsharp(root);
  assert.equal(result.status, 1);
  assert.equal(result.json.outcome, "unavailable");
  assert.equal(result.json.failure.reason, "not-repository");
  assert.equal(result.json.failure.exitCode, 128);
  assert.deepEqual(result.json, GOLDEN.unavailable);
});

test("Node production boundary rejects malformed porcelain output", () => {
  const result = parseGitStatus("?? missing-nul.txt");
  assert.equal(result.outcome, "unavailable");
  assert.equal(result.failure.reason, "malformed-output");
});

test("Node production boundary distinguishes a missing Git executable", (t) => {
  const root = fixture(t);
  const result = observeGitStatus(root, { executable: path.join(root, "missing-git") });
  assert.equal(result.outcome, "unavailable");
  assert.equal(result.failure.reason, "tool-unavailable");
  assert.equal(result.failure.exitCode, null);
});
