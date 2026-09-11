import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const installWorkItemId = `ROS-INSTALL-${JSON.parse(fs.readFileSync(path.join(repositoryRoot, "package.json"), "utf8")).version.replaceAll(".", "-")}`;
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

// Golden masters below were captured once from production's own Node
// implementation (tools/ros_cli.mjs's captureWork) with the exact same call
// sequence as each test, then frozen here. Node is retained in this
// repository only as the web server's internal dependency
// (DF-ROS-2026-A033) and is no longer executed as a live oracle by this
// test suite.
const GOLDEN = {
  test1Queue: {
    schemaVersion: "1.0.0",
    repository: "repository",
    nextSeq: 4,
    items: [
      { id: "WI-0001", title: "It's a \"quoted\" title", tags: ["code"], priority: "high", status: "ready" },
      {
        id: "WI-0003",
        title: "My new task",
        description: "details",
        tags: ["alpha", "beta"],
        priority: "high",
        status: "captured",
        attachments: [],
        createdBy: "tester",
        source: "manual",
        sourceReference: null
      }
    ]
  },
  test1Markdown: `# Work Queue\n\n| ID | Work | Status | Tags | Priority |\n|---|---|---|---|---|\n| ${installWorkItemId} | ${installWorkItemId} | complete |  |  |\n| WI-0001 | It's a "quoted" title | ready | code | high |\n| WI-0003 | My new task | captured | alpha, beta | high |\n`,
  test1SecondId: "WI-0003",
  test2Queue: {
    schemaVersion: "1.0.0",
    repository: "work-capture-differential",
    nextSeq: 2,
    items: [
      {
        id: "WI-0001",
        title: "First ever",
        description: null,
        tags: [],
        priority: "medium",
        status: "captured",
        attachments: [],
        createdBy: "unknown",
        source: "manual",
        sourceReference: null
      }
    ]
  },
  test2Markdown: `# Work Queue\n\n| ID | Work | Status | Tags | Priority |\n|---|---|---|---|---|\n| ${installWorkItemId} | ${installWorkItemId} | complete |  |  |\n| WI-0001 | First ever | captured |  | medium |\n`,
  test3Queue: {
    schemaVersion: "1.0.0",
    repository: "repository",
    nextSeq: 5,
    items: [
      {
        id: "TASK-CUSTOM",
        title: "Custom",
        description: null,
        tags: [],
        priority: "medium",
        status: "captured",
        attachments: [],
        createdBy: "unknown",
        source: "manual",
        sourceReference: null
      }
    ]
  },
  test3NextSeq: 5,
  test4Message: "add requires a non-empty title",
  test4Queue: {
    schemaVersion: "1.0.0",
    repository: "work-capture-differential",
    nextSeq: 1,
    items: []
  },
  test5Message: "invalid priority 'urgent'; use high, medium, or low",
  test6Message: "work item 'WI-9000' already exists",
  test6Queue: {
    schemaVersion: "1.0.0",
    repository: "repository",
    nextSeq: 2,
    items: [
      { id: "WI-9000", title: "Existing", tags: [], priority: null, status: "ready" }
    ]
  }
};

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-work-capture-differential-"));
  t.after(() => {
    try {
      fs.rmSync(root, { recursive: true, force: true });
    } catch {
      // Cleanup best-effort: a leftover temp dir under CI I/O contention isn't a test failure.
    }
  });
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
  const fsharpRoot = fixture(t);

  const fixtureQueue = {
    schemaVersion: "1.0.0",
    repository: "repository",
    nextSeq: 3,
    items: [{ id: "WI-0001", title: 'It\'s a "quoted" title', tags: ["code"], priority: "high", status: "ready" }]
  };
  writeQueue(fsharpRoot, fixtureQueue);

  const fsharp = runFsharp(fsharpRoot, "  My new task  ", { priority: "high", tags: ["alpha", "beta"], description: "  details  ", actor: "tester" });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), GOLDEN.test1Queue);
  assert.equal(readMarkdown(fsharpRoot), GOLDEN.test1Markdown);
  assert.equal(GOLDEN.test1SecondId, "WI-0003");
});

test("F# work capture matches production's real write on a completely fresh (missing) queue.json", (t) => {
  const fsharpRoot = fixture(t);
  fs.rmSync(path.join(fsharpRoot, ".ros", "work", "queue.json"), { force: true });
  fs.rmSync(path.join(fsharpRoot, ".ros", "work", "queue.md"), { force: true });

  const fsharp = runFsharp(fsharpRoot, "First ever", {});
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), GOLDEN.test2Queue);
  assert.equal(readMarkdown(fsharpRoot), GOLDEN.test2Markdown);
});

test("F# work capture matches production's real write for an explicit id, never advancing nextSeq", (t) => {
  const fsharpRoot = fixture(t);
  const fixtureQueue = { schemaVersion: "1.0.0", repository: "repository", nextSeq: 5, items: [] };
  writeQueue(fsharpRoot, fixtureQueue);

  const fsharp = runFsharp(fsharpRoot, "Custom", { id: "TASK-CUSTOM" });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), GOLDEN.test3Queue);
  assert.equal(GOLDEN.test3NextSeq, 5);
});

test("F# work capture rejects an empty title exactly like production, writing nothing", (t) => {
  const fsharpRoot = fixture(t);

  const fsharp = runFsharp(fsharpRoot, "   ", {});
  assert.equal(fsharp.status, 1);
  assert.equal(fsharp.stderr.trim(), `ERROR ${GOLDEN.test4Message}`);
  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), GOLDEN.test4Queue);
});

test("F# work capture rejects an invalid priority exactly like production", (t) => {
  const fsharpRoot = fixture(t);

  const fsharp = runFsharp(fsharpRoot, "x", { priority: "urgent" });
  assert.equal(fsharp.status, 1);
  assert.equal(fsharp.stderr.trim(), `ERROR ${GOLDEN.test5Message}`);
});

test("F# work capture rejects a duplicate explicit id exactly like production", (t) => {
  const fsharpRoot = fixture(t);
  const fixtureQueue = { schemaVersion: "1.0.0", repository: "repository", nextSeq: 2, items: [{ id: "WI-9000", title: "Existing", tags: [], priority: null, status: "ready" }] };
  writeQueue(fsharpRoot, fixtureQueue);

  const fsharp = runFsharp(fsharpRoot, "dup", { id: "WI-9000" });
  assert.equal(fsharp.status, 1);
  assert.equal(fsharp.stderr.trim(), `ERROR ${GOLDEN.test6Message}`);
  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), GOLDEN.test6Queue);
});
