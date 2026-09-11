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

// Golden masters below were captured once from production's own Node
// implementation (tools/ros_cli.mjs's transition, which itself calls
// tools/ros_git.mjs's observeGitStatus) with the exact same fixture setup
// and call sequence as each test, then frozen here. Node is retained in
// this repository only as the web server's internal dependency
// (DF-ROS-2026-A033) and is no longer executed as a live oracle by this
// test suite. The F# side still performs its OWN real git observation
// against the real fixture repository at test time -- only Node's side of
// the comparison is now a frozen literal.
const GOLDEN = {
  test1BaselineDirtyPaths: ["README.md", "untracked.txt"],
  test2Paths: ["src.txt"],
  test3Paths: ["src/app.ts"],
  test4BaselineDirtyPaths: ["committed-change.txt"],
  test5BaselineDirtyPaths: ["untracked.txt"]
};

function contextFile(root) {
  return path.join(root, ".ros", "context", "current.json");
}

function readContext(root) {
  return JSON.parse(fs.readFileSync(contextFile(root), "utf8"));
}

function writeContext(root, context) {
  fs.writeFileSync(contextFile(root), `${JSON.stringify(context, null, 2)}\n`);
}

function fixture(t, options = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-git-paths-differential-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Git Paths Differential" });

  const configFile = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(configFile, "utf8"));
  config.telemetry.enabled = false;
  if (options.meaningfulPaths) config.workProtocol.meaningfulPaths = options.meaningfulPaths;
  if (options.ignoredPaths) config.workProtocol.ignoredPaths = options.ignoredPaths;
  fs.writeFileSync(configFile, `${JSON.stringify(config, null, 2)}\n`);

  // Start from a clean-slate context (no started-at, no work items) rather
  // than the bootstrap default (which already has an initial completed item
  // and a started-at timestamp) so the first-begin baseline-capture gate is
  // actually exercised.
  writeContext(root, { schemaVersion: "1.0.0", repository: config.repository.id, workItems: [] });

  execFileSync("git", ["-C", root, "init", "-q"]);
  execFileSync("git", ["-C", root, "config", "user.email", "test@example.invalid"]);
  execFileSync("git", ["-C", root, "config", "user.name", "ROS Test"]);
  execFileSync("git", ["-C", root, "add", "."]);
  execFileSync("git", ["-C", root, "commit", "-qm", "baseline"]);
  return root;
}

function fsharpContextPlan(root, context, action, ids, options = {}) {
  const snapshot = path.join(os.tmpdir(), `ros-git-paths-context-${process.pid}-${Math.random().toString(16).slice(2)}.json`);
  fs.writeFileSync(snapshot, JSON.stringify(context));
  const args = [
    fsharpCli, "--root", root, "work", "context-plan", "--context", snapshot,
    "--action", action, "--occurred-at", options.occurredAt ?? "2026-09-09T00:00:00Z",
    "--repository", options.repository ?? "repository",
    "--protocol-version", options.protocolVersion ?? "1.0.0",
    "--actor", options.actor ?? "unknown",
    "--type", options.type ?? "task"
  ];
  for (const id of ids) args.push("--id", id);
  for (const evidence of options.evidence ?? []) args.push("--evidence", `${evidence.type}=${evidence.path}`);
  const result = spawnSync("dotnet", args, {
    cwd: repositoryRoot,
    encoding: "utf8",
    env: options.env ?? process.env
  });
  fs.rmSync(snapshot, { force: true });
  return { status: result.status, json: result.stdout ? JSON.parse(result.stdout) : null, stderr: result.stderr };
}

// Real F# effect-application call (not the pure "context-plan" preview),
// used only to advance a fixture's real on-disk state (context file plus
// the real housekeeping writes under .ros/) the same way production's own
// begin would, so the *next* context-plan's own real git observation sees
// a realistic working tree.
function fsharpBegin(root, id) {
  const result = spawnSync("dotnet", [
    fsharpCli, "--root", root, "work", "start", "--id", id, "--occurred-at", new Date().toISOString(), "--type", "task"
  ], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

test("F# context-plan captures the real Git baseline on first begin, matching production", (t) => {
  const root = fixture(t);
  const before = readContext(root);
  fs.appendFileSync(path.join(root, "README.md"), "\nmore\n");
  fs.writeFileSync(path.join(root, "untracked.txt"), "new\n");

  const fsharp = fsharpContextPlan(root, before, "begin", ["TASK-GIT"]);
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual([...fsharp.json.plan.baselineDirtyPaths].sort(), [...GOLDEN.test1BaselineDirtyPaths].sort());
  assert.ok(GOLDEN.test1BaselineDirtyPaths.includes("README.md"));
  assert.ok(GOLDEN.test1BaselineDirtyPaths.includes("untracked.txt"));
});

test("F# context-plan excludes ROS housekeeping paths from completion paths, matching production", (t) => {
  const root = fixture(t);
  const begin = fsharpBegin(root, "TASK-GIT");
  assert.equal(begin.status, 0, begin.stderr);
  assert.equal(readContext(root).workItems.length, 1);
  // Production's own begin just dirtied .ros/context and .ros/events -- both
  // ignored patterns. The context snapshot after begin is what a subsequent
  // completion plans against.
  const afterBegin = readContext(root);
  fs.writeFileSync(path.join(root, "src.txt"), "meaningful change\n");

  const fsharp = fsharpContextPlan(root, afterBegin, "complete", ["TASK-GIT"], {
    evidence: [
      { type: "implementation", path: "src.txt" },
      { type: "tests", path: "src.txt" }
    ]
  });
  assert.equal(fsharp.status, 0, fsharp.stderr);
  const fsharpEvent = fsharp.json.plan.events[0];

  assert.deepEqual(GOLDEN.test2Paths, ["src.txt"]);
  assert.deepEqual(fsharpEvent.paths, GOLDEN.test2Paths);
});

test("F# context-plan applies configured meaningful/ignored patterns, matching production", (t) => {
  const root = fixture(t, { meaningfulPaths: ["src/**"], ignoredPaths: ["src/generated/**"] });
  const begin = fsharpBegin(root, "TASK-GIT");
  assert.equal(begin.status, 0, begin.stderr);
  assert.equal(readContext(root).workItems.length, 1);
  const afterBegin = readContext(root);

  fs.mkdirSync(path.join(root, "src", "generated"), { recursive: true });
  fs.writeFileSync(path.join(root, "src", "app.ts"), "meaningful\n");
  fs.writeFileSync(path.join(root, "src", "generated", "out.js"), "ignored\n");
  fs.writeFileSync(path.join(root, "docs.md"), "not meaningful (outside src/**)\n");

  const fsharp = fsharpContextPlan(root, afterBegin, "complete", ["TASK-GIT"], {
    evidence: [
      { type: "implementation", path: "src/app.ts" },
      { type: "tests", path: "src/app.ts" }
    ]
  });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(GOLDEN.test3Paths, ["src/app.ts"]);
  assert.deepEqual(fsharp.json.plan.events[0].paths, GOLDEN.test3Paths);
});

test("F# context-plan includes a resolvable ROS_BASE_REF committed range, matching production", (t) => {
  const root = fixture(t);
  execFileSync("git", ["-C", root, "checkout", "-qb", "feature"]);
  fs.writeFileSync(path.join(root, "committed-change.txt"), "committed\n");
  execFileSync("git", ["-C", root, "add", "committed-change.txt"]);
  execFileSync("git", ["-C", root, "commit", "-qm", "feature commit"]);
  const baseRef = execFileSync("git", ["-C", root, "rev-parse", "HEAD~1"], { encoding: "utf8" }).trim();

  const before = readContext(root);
  const env = { ...process.env, ROS_BASE_REF: baseRef };

  const fsharp = fsharpContextPlan(root, before, "begin", ["TASK-GIT"], { env });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.ok(GOLDEN.test4BaselineDirtyPaths.includes("committed-change.txt"));
  assert.deepEqual([...fsharp.json.plan.baselineDirtyPaths].sort(), [...GOLDEN.test4BaselineDirtyPaths].sort());
});

test("F# context-plan silently skips an unresolvable ROS_BASE_REF, matching production", (t) => {
  const root = fixture(t);
  const before = readContext(root);
  fs.writeFileSync(path.join(root, "untracked.txt"), "new\n");
  const env = { ...process.env, ROS_BASE_REF: "refs/does-not-exist" };

  const fsharp = fsharpContextPlan(root, before, "begin", ["TASK-GIT"], { env });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual([...fsharp.json.plan.baselineDirtyPaths].sort(), [...GOLDEN.test5BaselineDirtyPaths].sort());
  assert.ok(GOLDEN.test5BaselineDirtyPaths.includes("untracked.txt"));
});
