import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const installWorkItemId = `ROS-INSTALL-${JSON.parse(fs.readFileSync(path.join(repositoryRoot, "package.json"), "utf8")).version.replaceAll(".", "-")}`;
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

// Golden masters below were captured once from production's own Node
// `work list`/`work show`/`work ready`/`add` commands with the exact same
// call sequence as each test, then frozen here. Node is retained in this
// repository only as the web server's internal dependency (DF-ROS-2026-A033)
// and is no longer executed as a live oracle by this test suite.
const ATTACH_ROW = {
  id: "WI-ATTACH",
  title: "Ready with attachment",
  description: null,
  tags: [],
  priority: "medium",
  status: "ready",
  backlogActions: ["abandon", "block", "start"],
  liveWorkItem: null,
  attachments: [{ id: "ATT-1", name: "note.txt", size: 7, contentType: null }]
};
const READY_ROW = {
  id: "WI-READY",
  title: "Ready item",
  description: "desc text",
  tags: ["alpha"],
  priority: "high",
  status: "active",
  backlogActions: [],
  liveWorkItem: { state: "active", semanticState: "active", allowedActions: ["block", "complete"] },
  attachments: []
};
const ACTIVE_ROW = {
  id: "WI-ACTIVE",
  title: "Active item",
  description: null,
  tags: [],
  priority: "medium",
  status: "blocked",
  blockedReason: "waiting on review",
  backlogActions: [],
  liveWorkItem: { state: "blocked", semanticState: "blocked", allowedActions: ["resume"] },
  attachments: []
};
const CAPTURED_ROW = {
  id: "WI-CAPTURED",
  title: "Captured only item",
  description: null,
  tags: [],
  priority: "medium",
  status: "captured",
  backlogActions: ["abandon", "ready"],
  liveWorkItem: null,
  attachments: []
};
const INSTALL_ROW = {
  id: installWorkItemId,
  title: installWorkItemId,
  description: null,
  tags: [],
  priority: null,
  status: "complete",
  backlogActions: [],
  liveWorkItem: { state: "complete", semanticState: "complete", allowedActions: [] },
  attachments: []
};

const GOLDEN = {
  mergedWorkView: [INSTALL_ROW, ACTIVE_ROW, ATTACH_ROW, CAPTURED_ROW, READY_ROW],
  bareWork: [INSTALL_ROW, ACTIVE_ROW, ATTACH_ROW, CAPTURED_ROW, READY_ROW],
  show: {
    "WI-READY": { detail: null, row: { ...READY_ROW, detail: null } },
    "WI-ACTIVE": { detail: null, row: { ...ACTIVE_ROW, detail: null } },
    "WI-CAPTURED": { detail: null, row: { ...CAPTURED_ROW, detail: null } },
    "WI-ATTACH": { detail: null, row: { ...ATTACH_ROW, detail: null } }
  },
  tagAlpha: [READY_ROW],
  statusReady: [ATTACH_ROW],
  statusReadyTagAlpha: [],
  readyView: [ATTACH_ROW],
  addItem: { id: "WI-NEW", title: "A new obligation", status: "captured", tags: ["alpha", "beta"], priority: "high" },
  addList: [
    { id: installWorkItemId, title: installWorkItemId, tags: [], priority: null, status: "complete" },
    { id: "WI-NEW", title: "A new obligation", tags: ["alpha", "beta"], priority: "high", status: "captured" }
  ]
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-work-list-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 }));
  initializeProject({ target: root, project: "Work List Differential" });
  execFileSync("git", ["-C", root, "init", "-q"]);
  return root;
}

function fsharpAdd(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "add", ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function fsharpWork(root, command, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", command, ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function normRows(rows) {
  return rows
    .map((row) => {
      const { attachments, ...rest } = row;
      const normAttachments = attachments.map(({ uploadedAt, ...attachmentRest }) => attachmentRest);
      return { ...rest, attachments: normAttachments };
    })
    .sort((a, b) => (a.id < b.id ? -1 : 1));
}

// Real (not backdated) timestamps: a new execution's own startedAt is always
// production's real wall clock, so a synthetic --occurred-at earlier than
// "now" on a later transition could spuriously fail a chronological-order
// check elsewhere.
const at = () => new Date().toISOString();

function seedVariety(root) {
  assert.equal(fsharpAdd(root, ["Ready item", "--id", "WI-READY", "--tag", "alpha", "--priority", "high", "--description", "desc text"]).status, 0);
  assert.equal(fsharpWork(root, "backlog-transition", ["--id", "WI-READY", "--action", "ready", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, "start", ["--id", "WI-READY", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpAdd(root, ["Active item", "--id", "WI-ACTIVE"]).status, 0);
  assert.equal(fsharpWork(root, "backlog-transition", ["--id", "WI-ACTIVE", "--action", "ready", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, "start", ["--id", "WI-ACTIVE", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, "block", ["--id", "WI-ACTIVE", "--reason", "waiting on review", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpAdd(root, ["Captured only item", "--id", "WI-CAPTURED"]).status, 0);

  const attachmentSource = path.join(os.tmpdir(), `ros-work-list-attachment-${process.pid}.txt`);
  fs.writeFileSync(attachmentSource, "attach\n");
  assert.equal(fsharpAdd(root, ["Ready with attachment", "--id", "WI-ATTACH"]).status, 0);
  assert.equal(fsharpWork(root, "attach", ["--id", "WI-ATTACH", "--occurred-at", at(), "--file", `${attachmentSource}=note.txt`]).status, 0);
  assert.equal(fsharpWork(root, "backlog-transition", ["--id", "WI-ATTACH", "--action", "ready", "--occurred-at", at()]).status, 0);
}

test("F# work list matches production's mergedWorkView across backlog-only, live-only, blocked, and attachment cases", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharp = fixture(t, "list-fsharp");

  seedVariety(fsharp);

  const fsharpResult = runFsharp(fsharp, ["list"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  assert.deepEqual(normRows(JSON.parse(fsharpResult.stdout)), GOLDEN.mergedWorkView);
});

test("F# bare `work` (no subcommand) matches production's bare `work`, identical to `work list`", (t) => {
  const fsharp = fixture(t, "bare-fsharp");

  seedVariety(fsharp);

  const fsharpResult = runFsharp(fsharp, []);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  assert.deepEqual(normRows(JSON.parse(fsharpResult.stdout)), GOLDEN.bareWork);
});

test("F# work show matches production's showWork for a backlog-only, a live, and a blocked item", (t) => {
  const fsharp = fixture(t, "show-fsharp");

  seedVariety(fsharp);

  for (const id of ["WI-READY", "WI-ACTIVE", "WI-CAPTURED", "WI-ATTACH"]) {
    const fsharpResult = runFsharp(fsharp, ["show", id]);
    assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

    const fsharpParsed = JSON.parse(fsharpResult.stdout);
    const golden = GOLDEN.show[id];
    assert.equal(fsharpParsed.detail, golden.detail);
    assert.deepEqual(normRows([fsharpParsed])[0], golden.row);
  }
});

test("F# work show rejects an unknown ID with production's exact message", (t) => {
  const fsharp = fixture(t, "unknown-fsharp");

  const fsharpResult = runFsharp(fsharp, ["show", "WI-NOPE"]);

  assert.equal(fsharpResult.status, 1);
  const expected = "work item 'WI-NOPE' was not found";
  assert.equal(fsharpResult.stderr.trim(), `ERROR ${expected}`);
});

test("F# work list --tag matches production's every-tag-must-match filter", (t) => {
  const fsharp = fixture(t, "tag-fsharp");

  seedVariety(fsharp);

  const fsharpResult = runFsharp(fsharp, ["list", "--tag", "alpha"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpRows = normRows(JSON.parse(fsharpResult.stdout));
  assert.deepEqual(fsharpRows, GOLDEN.tagAlpha);
  assert.deepEqual(fsharpRows.map((row) => row.id), ["WI-READY"]);
});

test("F# work list --status matches production's exact-status filter, and combines with --tag", (t) => {
  const fsharp = fixture(t, "status-fsharp");

  seedVariety(fsharp);

  const fsharpResult = runFsharp(fsharp, ["list", "--status", "ready"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  assert.deepEqual(normRows(JSON.parse(fsharpResult.stdout)), GOLDEN.statusReady);

  // WI-READY ends the fixture in "active" state (add -> ready -> start) and
  // only WI-ATTACH ends "ready" with no tags, so the combination below is a
  // real, non-trivial empty-result case, not a placeholder assertion.
  const fsharpCombined = runFsharp(fsharp, ["list", "--status", "ready", "--tag", "alpha"]);
  const combinedRows = normRows(JSON.parse(fsharpCombined.stdout));
  assert.deepEqual(combinedRows, GOLDEN.statusReadyTagAlpha);
  assert.deepEqual(combinedRows.map((row) => row.id), []);
});

test("F# `work ready` (no ID) matches production's status-filtered read view", (t) => {
  const fsharp = fixture(t, "readyview-fsharp");

  seedVariety(fsharp);

  const fsharpResult = runFsharp(fsharp, ["ready"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  assert.deepEqual(normRows(JSON.parse(fsharpResult.stdout)), GOLDEN.readyView);
});

test("F# `work ready ID` (with an ID) is rejected with a clear redirect rather than silently misbehaving", (t) => {
  const fsharp = fixture(t, "readyid-fsharp");
  seedVariety(fsharp);

  const fsharpResult = runFsharp(fsharp, ["ready", "WI-CAPTURED"]);
  assert.equal(fsharpResult.status, 2);
  assert.match(fsharpResult.stderr, /work backlog-transition --action ready --id ID/);
});

test("F# `add` matches production's add for the fields both sides produce", (t) => {
  const fsharp = fixture(t, "add-fsharp");

  const fsharpResult = spawnSync(
    "dotnet",
    [fsharpCli, "--root", fsharp, "add", "A new obligation", "--tag", "alpha", "--tag", "beta", "--priority", "high", "--id", "WI-NEW", "--description", "desc", "--occurred-at", "2026-01-01T00:00:00.000Z"],
    { cwd: repositoryRoot, encoding: "utf8" }
  );
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpItem = JSON.parse(fsharpResult.stdout);
  assert.deepEqual(
    { id: fsharpItem.id, title: fsharpItem.title, status: fsharpItem.status, tags: fsharpItem.tags, priority: fsharpItem.priority },
    GOLDEN.addItem
  );

  const fsharpList = normRows(JSON.parse(runFsharp(fsharp, ["list"]).stdout));
  assert.deepEqual(
    fsharpList.map(({ id, title, tags, priority, status }) => ({ id, title, tags, priority, status })),
    GOLDEN.addList
  );
});

test("F# `add` rejects a missing title exactly like production", (t) => {
  const fsharp = fixture(t, "add-missing-fsharp");
  const result = spawnSync("dotnet", [fsharpCli, "--root", fsharp, "add"], { cwd: repositoryRoot, encoding: "utf8" });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /add requires a title/);
});

test("F# `work begin`/`work done` behave identically to `work start`/`work complete`", (t) => {
  const beginRoot = fixture(t, "begin-fsharp");
  const startRoot = fixture(t, "start-fsharp");

  for (const root of [beginRoot, startRoot]) {
    assert.equal(fsharpAdd(root, ["Alias test item", "--id", "WI-ALIAS"]).status, 0);
    assert.equal(fsharpWork(root, "backlog-transition", ["--id", "WI-ALIAS", "--action", "ready", "--occurred-at", at()]).status, 0);
  }

  const beginResult = spawnSync("dotnet", [fsharpCli, "--root", beginRoot, "work", "begin", "--id", "WI-ALIAS", "--occurred-at", "2026-01-01T00:00:00.000Z"], { cwd: repositoryRoot, encoding: "utf8" });
  const startResult = spawnSync("dotnet", [fsharpCli, "--root", startRoot, "work", "start", "--id", "WI-ALIAS", "--occurred-at", "2026-01-01T00:00:00.000Z"], { cwd: repositoryRoot, encoding: "utf8" });
  assert.equal(beginResult.status, 0, beginResult.stderr);
  assert.equal(startResult.status, 0, startResult.stderr);
  assert.deepEqual(
    JSON.parse(beginResult.stdout).workItems.map(({ id, state }) => ({ id, state })),
    JSON.parse(startResult.stdout).workItems.map(({ id, state }) => ({ id, state }))
  );

  const doneResult = spawnSync("dotnet", [fsharpCli, "--root", beginRoot, "work", "done", "--id", "WI-ALIAS", "--occurred-at", "2026-01-01T00:01:00.000Z", "--evidence", "implementation=ros", "--evidence", "tests=ros"], { cwd: repositoryRoot, encoding: "utf8" });
  const completeResult = spawnSync("dotnet", [fsharpCli, "--root", startRoot, "work", "complete", "--id", "WI-ALIAS", "--occurred-at", "2026-01-01T00:01:00.000Z", "--evidence", "implementation=ros", "--evidence", "tests=ros"], { cwd: repositoryRoot, encoding: "utf8" });
  assert.equal(doneResult.status, 0, doneResult.stderr);
  assert.equal(completeResult.status, 0, completeResult.stderr);
  assert.deepEqual(
    JSON.parse(doneResult.stdout).workItems.map(({ id, state }) => ({ id, state })),
    JSON.parse(completeResult.stdout).workItems.map(({ id, state }) => ({ id, state }))
  );
});
