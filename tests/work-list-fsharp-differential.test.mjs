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
