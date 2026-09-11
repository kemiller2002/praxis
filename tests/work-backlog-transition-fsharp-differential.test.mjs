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
// implementation (tools/ros_cli.mjs's backlogTransition) with the exact
// same call sequence as each test, then frozen here. Node is retained in
// this repository only as the web server's internal dependency
// (DF-ROS-2026-A033) and is no longer executed as a live oracle by this
// test suite.
const GOLDEN = {
  test1Queue: {
    schemaVersion: "1.0.0",
    repository: "repository",
    nextSeq: 3,
    items: [
      {
        id: "WI-0001",
        title: "It's a \"quoted\" title",
        description: null,
        tags: ["code", "testing"],
        priority: "high",
        status: "blocked",
        attachments: [{ id: "att-1", name: "x.txt", size: 12, contentType: "text/plain", uploadedAt: "2026-01-01T00:00:00.000Z" }],
        createdAt: "2026-01-01T00:00:00.000Z",
        createdBy: "unknown",
        source: "manual",
        sourceReference: null,
        blockedReason: "needs review"
      },
      {
        id: "WI-0002",
        title: "Second",
        tags: [],
        priority: null,
        status: "ready",
        createdAt: "2026-01-01T00:00:00.000Z"
      }
    ]
  },
  test1Markdown: `# Work Queue\n\n| ID | Work | Status | Tags | Priority |\n|---|---|---|---|---|\n| ${installWorkItemId} | ${installWorkItemId} | complete |  |  |\n| WI-0001 | It's a "quoted" title | blocked | code, testing | high |\n| WI-0002 | Second | ready |  |  |\n`,
  test2HasBlockedReason: false,
  test2Queue: {
    schemaVersion: "1.0.0",
    repository: "repository",
    nextSeq: 3,
    items: [
      {
        id: "WI-0001",
        title: "It's a \"quoted\" title",
        description: null,
        tags: ["code", "testing"],
        priority: "high",
        status: "ready",
        attachments: [{ id: "att-1", name: "x.txt", size: 12, contentType: "text/plain", uploadedAt: "2026-01-01T00:00:00.000Z" }],
        createdAt: "2026-01-01T00:00:00.000Z",
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
        createdAt: "2026-01-01T00:00:00.000Z"
      }
    ]
  },
  test2Markdown: `# Work Queue\n\n| ID | Work | Status | Tags | Priority |\n|---|---|---|---|---|\n| ${installWorkItemId} | ${installWorkItemId} | complete |  |  |\n| WI-0001 | It's a "quoted" title | ready | code, testing | high |\n| WI-0002 | Second | ready |  |  |\n`,
  test3Queue: {
    schemaVersion: "1.0.0",
    repository: "repository",
    nextSeq: 3,
    items: [
      {
        id: "WI-0001",
        title: "It's a \"quoted\" title",
        description: null,
        tags: ["code", "testing"],
        priority: "high",
        status: "ready",
        attachments: [{ id: "att-1", name: "x.txt", size: 12, contentType: "text/plain", uploadedAt: "2026-01-01T00:00:00.000Z" }],
        createdAt: "2026-01-01T00:00:00.000Z",
        createdBy: "unknown",
        source: "manual",
        sourceReference: null
      },
      {
        id: "WI-0002",
        title: "Second",
        tags: [],
        priority: null,
        status: "abandoned",
        createdAt: "2026-01-01T00:00:00.000Z",
        abandonedReason: "no longer needed"
      }
    ]
  },
  test3Markdown: `# Work Queue\n\n| ID | Work | Status | Tags | Priority |\n|---|---|---|---|---|\n| ${installWorkItemId} | ${installWorkItemId} | complete |  |  |\n| WI-0001 | It's a "quoted" title | ready | code, testing | high |\n| WI-0002 | Second | abandoned |  |  |\n`,
  test4Message: "cannot ready backlog item 'WI-0001' from 'abandoned'",
  test4Queue: {
    schemaVersion: "1.0.0",
    repository: "repository",
    nextSeq: 3,
    items: [
      {
        id: "WI-0001",
        title: "It's a \"quoted\" title",
        description: null,
        tags: ["code", "testing"],
        priority: "high",
        status: "abandoned",
        attachments: [{ id: "att-1", name: "x.txt", size: 12, contentType: "text/plain", uploadedAt: "2026-01-01T00:00:00.000Z" }],
        createdAt: "2026-01-01T00:00:00.000Z",
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
        createdAt: "2026-01-01T00:00:00.000Z"
      }
    ]
  },
  test5Message: "block requires --reason",
  test6Message: "'WI-9999' is not a captured local work item"
};

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
  const fsharpRoot = fixture(t);

  const fsharp = runFsharp(fsharpRoot, "WI-0001", "block", { reason: "needs review" });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), GOLDEN.test1Queue);
  assert.equal(readMarkdown(fsharpRoot), GOLDEN.test1Markdown);
  assert.ok(!readQueue(fsharpRoot).items[0].updatedAt.includes("\\u0027"), "sanity: updatedAt itself never contains an apostrophe");
});

test("F# work backlog-transition matches production's real write for ready, clearing blockedReason entirely", (t) => {
  const fsharpRoot = fixture(t);
  runFsharp(fsharpRoot, "WI-0001", "block", { reason: "needs review" });

  const fsharp = runFsharp(fsharpRoot, "WI-0001", "ready", {});
  assert.equal(fsharp.status, 0, fsharp.stderr);

  const fsharpQueue = readQueue(fsharpRoot);
  assert.ok(!GOLDEN.test2HasBlockedReason, "sanity: production itself removes blockedReason on ready");
  assert.deepEqual(withoutClockFields(fsharpQueue), GOLDEN.test2Queue);
  assert.equal(readMarkdown(fsharpRoot), GOLDEN.test2Markdown);
});

test("F# work backlog-transition matches production's real write for abandon with a reason", (t) => {
  const fsharpRoot = fixture(t);

  const fsharp = runFsharp(fsharpRoot, "WI-0002", "abandon", { reason: "no longer needed" });
  assert.equal(fsharp.status, 0, fsharp.stderr);

  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), GOLDEN.test3Queue);
  assert.equal(readMarkdown(fsharpRoot), GOLDEN.test3Markdown);
});

test("F# work backlog-transition rejects an illegal transition exactly like production, writing nothing", (t) => {
  const fsharpRoot = fixture(t);
  runFsharp(fsharpRoot, "WI-0001", "abandon", {});

  const fsharp = runFsharp(fsharpRoot, "WI-0001", "ready", {});
  assert.equal(fsharp.status, 1);
  assert.equal(fsharp.stderr.trim(), `ERROR ${GOLDEN.test4Message}`);
  assert.deepEqual(withoutClockFields(readQueue(fsharpRoot)), GOLDEN.test4Queue);
});

test("F# work backlog-transition rejects a reasonless block exactly like production", (t) => {
  const fsharpRoot = fixture(t);

  const fsharp = runFsharp(fsharpRoot, "WI-0001", "block", {});
  assert.equal(fsharp.status, 1);
  assert.equal(fsharp.stderr.trim(), `ERROR ${GOLDEN.test5Message}`);
});

test("F# work backlog-transition rejects an unknown id exactly like production", (t) => {
  const fsharpRoot = fixture(t);

  const fsharp = runFsharp(fsharpRoot, "WI-9999", "ready", {});
  assert.equal(fsharp.status, 1);
  assert.equal(fsharp.stderr.trim(), `ERROR ${GOLDEN.test6Message}`);
});
