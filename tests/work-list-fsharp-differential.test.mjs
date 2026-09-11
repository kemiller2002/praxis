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
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-work-list-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Work List Differential" });
  execFileSync("git", ["-C", root, "init", "-q"]);
  return root;
}

function ros(root, args) {
  const result = spawnSync(path.join(root, "ros"), args, { cwd: root, encoding: "utf8" });
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

function seedVariety(root) {
  assert.equal(ros(root, ["add", "Ready item", "--id", "WI-READY", "--tag", "alpha", "--priority", "high", "--description", "desc text"]).status, 0);
  assert.equal(ros(root, ["work", "ready", "WI-READY"]).status, 0);
  assert.equal(ros(root, ["work", "start", "WI-READY"]).status, 0);
  assert.equal(ros(root, ["add", "Active item", "--id", "WI-ACTIVE"]).status, 0);
  assert.equal(ros(root, ["work", "ready", "WI-ACTIVE"]).status, 0);
  assert.equal(ros(root, ["work", "start", "WI-ACTIVE"]).status, 0);
  assert.equal(ros(root, ["work", "block", "WI-ACTIVE", "--reason", "waiting on review"]).status, 0);
  assert.equal(ros(root, ["add", "Captured only item", "--id", "WI-CAPTURED"]).status, 0);

  const attachmentSource = path.join(os.tmpdir(), `ros-work-list-attachment-${process.pid}.txt`);
  fs.writeFileSync(attachmentSource, "attach\n");
  assert.equal(ros(root, ["add", "Ready with attachment", "--id", "WI-ATTACH", "--file", `${attachmentSource}=note.txt`]).status, 0);
  assert.equal(ros(root, ["work", "ready", "WI-ATTACH"]).status, 0);
}

test("F# work list matches production's mergedWorkView across backlog-only, live-only, blocked, and attachment cases", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const node = fixture(t, "list-node");
  const fsharp = fixture(t, "list-fsharp");

  seedVariety(node);
  seedVariety(fsharp);

  const nodeResult = ros(node, ["work", "list"]);
  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  const fsharpResult = runFsharp(fsharp, ["list"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  assert.deepEqual(normRows(JSON.parse(nodeResult.stdout)), normRows(JSON.parse(fsharpResult.stdout)));
});

test("F# bare `work` (no subcommand) matches production's bare `work`, identical to `work list`", (t) => {
  const node = fixture(t, "bare-node");
  const fsharp = fixture(t, "bare-fsharp");

  seedVariety(node);
  seedVariety(fsharp);

  const nodeResult = ros(node, ["work"]);
  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  const fsharpResult = runFsharp(fsharp, []);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  assert.deepEqual(normRows(JSON.parse(nodeResult.stdout)), normRows(JSON.parse(fsharpResult.stdout)));
});

test("F# work show matches production's showWork for a backlog-only, a live, and a blocked item", (t) => {
  const node = fixture(t, "show-node");
  const fsharp = fixture(t, "show-fsharp");

  seedVariety(node);
  seedVariety(fsharp);

  for (const id of ["WI-READY", "WI-ACTIVE", "WI-CAPTURED", "WI-ATTACH"]) {
    const nodeResult = ros(node, ["work", "show", id]);
    assert.equal(nodeResult.status, 0, nodeResult.stderr);
    const fsharpResult = runFsharp(fsharp, ["show", id]);
    assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

    const nodeParsed = JSON.parse(nodeResult.stdout);
    const fsharpParsed = JSON.parse(fsharpResult.stdout);
    assert.equal(nodeParsed.detail, fsharpParsed.detail);
    assert.deepEqual(normRows([nodeParsed]), normRows([fsharpParsed]));
  }
});

test("F# work show rejects an unknown ID with production's exact message", (t) => {
  const node = fixture(t, "unknown-node");
  const fsharp = fixture(t, "unknown-fsharp");

  const nodeResult = ros(node, ["work", "show", "WI-NOPE"]);
  const fsharpResult = runFsharp(fsharp, ["show", "WI-NOPE"]);

  assert.equal(nodeResult.status, 1);
  assert.equal(fsharpResult.status, 1);
  const expected = "work item 'WI-NOPE' was not found";
  assert.match(nodeResult.stderr, new RegExp(expected.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  assert.equal(fsharpResult.stderr.trim(), `ERROR ${expected}`);
});

test("F# work list --tag matches production's every-tag-must-match filter", (t) => {
  const node = fixture(t, "tag-node");
  const fsharp = fixture(t, "tag-fsharp");

  seedVariety(node);
  seedVariety(fsharp);

  const nodeResult = ros(node, ["work", "list", "--tag", "alpha"]);
  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  const fsharpResult = runFsharp(fsharp, ["list", "--tag", "alpha"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const nodeRows = normRows(JSON.parse(nodeResult.stdout));
  assert.deepEqual(nodeRows, normRows(JSON.parse(fsharpResult.stdout)));
  assert.deepEqual(nodeRows.map((row) => row.id), ["WI-READY"]);
});

test("F# work list --status matches production's exact-status filter, and combines with --tag", (t) => {
  const node = fixture(t, "status-node");
  const fsharp = fixture(t, "status-fsharp");

  seedVariety(node);
  seedVariety(fsharp);

  const nodeResult = ros(node, ["work", "list", "--status", "ready"]);
  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  const fsharpResult = runFsharp(fsharp, ["list", "--status", "ready"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  assert.deepEqual(normRows(JSON.parse(nodeResult.stdout)), normRows(JSON.parse(fsharpResult.stdout)));

  // WI-READY ends the fixture in "active" state (add -> ready -> start) and
  // only WI-ATTACH ends "ready" with no tags, so the combination below is a
  // real, non-trivial empty-result case, not a placeholder assertion.
  const nodeCombined = ros(node, ["work", "list", "--status", "ready", "--tag", "alpha"]);
  const fsharpCombined = runFsharp(fsharp, ["list", "--status", "ready", "--tag", "alpha"]);
  const combinedRows = normRows(JSON.parse(nodeCombined.stdout));
  assert.deepEqual(combinedRows, normRows(JSON.parse(fsharpCombined.stdout)));
  assert.deepEqual(combinedRows.map((row) => row.id), []);
});

test("F# `work ready` (no ID) matches production's status-filtered read view", (t) => {
  const node = fixture(t, "readyview-node");
  const fsharp = fixture(t, "readyview-fsharp");

  seedVariety(node);
  seedVariety(fsharp);

  const nodeResult = ros(node, ["work", "ready"]);
  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  const fsharpResult = runFsharp(fsharp, ["ready"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  assert.deepEqual(normRows(JSON.parse(nodeResult.stdout)), normRows(JSON.parse(fsharpResult.stdout)));
});

test("F# `work ready ID` (with an ID) is rejected with a clear redirect rather than silently misbehaving", (t) => {
  const fsharp = fixture(t, "readyid-fsharp");
  seedVariety(fsharp);

  const fsharpResult = runFsharp(fsharp, ["ready", "WI-CAPTURED"]);
  assert.equal(fsharpResult.status, 2);
  assert.match(fsharpResult.stderr, /work backlog-transition --action ready --id ID/);
});

test("F# `add` matches production's add for the fields both sides produce", (t) => {
  const node = fixture(t, "add-node");
  const fsharp = fixture(t, "add-fsharp");

  const nodeResult = ros(node, ["add", "A new obligation", "--tag", "alpha", "--tag", "beta", "--priority", "high", "--id", "WI-NEW", "--description", "desc"]);
  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  const fsharpResult = spawnSync(
    "dotnet",
    [fsharpCli, "--root", fsharp, "add", "A new obligation", "--tag", "alpha", "--tag", "beta", "--priority", "high", "--id", "WI-NEW", "--description", "desc", "--occurred-at", "2026-01-01T00:00:00.000Z"],
    { cwd: repositoryRoot, encoding: "utf8" }
  );
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const nodeItem = JSON.parse(nodeResult.stdout);
  const fsharpItem = JSON.parse(fsharpResult.stdout);
  assert.deepEqual(
    { id: nodeItem.id, title: nodeItem.title, status: nodeItem.status, tags: nodeItem.tags, priority: nodeItem.priority },
    { id: fsharpItem.id, title: fsharpItem.title, status: fsharpItem.status, tags: fsharpItem.tags, priority: fsharpItem.priority }
  );

  const nodeList = normRows(JSON.parse(ros(node, ["work", "list"]).stdout));
  const fsharpList = normRows(JSON.parse(runFsharp(fsharp, ["list"]).stdout));
  assert.deepEqual(
    nodeList.map(({ id, title, tags, priority, status }) => ({ id, title, tags, priority, status })),
    fsharpList.map(({ id, title, tags, priority, status }) => ({ id, title, tags, priority, status }))
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
    assert.equal(ros(root, ["add", "Alias test item", "--id", "WI-ALIAS"]).status, 0);
    assert.equal(ros(root, ["work", "ready", "WI-ALIAS"]).status, 0);
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
