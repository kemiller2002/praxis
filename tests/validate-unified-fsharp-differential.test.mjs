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
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-validate-unified-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Validate Unified Differential" });
  execFileSync("git", ["-C", root, "init", "-q"]);
  execFileSync("git", ["-C", root, "-c", "user.email=a@b.c", "-c", "user.name=a", "add", "-A"]);
  execFileSync("git", ["-C", root, "-c", "user.email=a@b.c", "-c", "user.name=a", "commit", "-q", "-m", "init"]);
  return root;
}

function ros(root, args) {
  const result = spawnSync(path.join(root, "ros"), args, { cwd: root, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function fsharpValidate(root, extraArgs = []) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "validate", ...extraArgs], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

test("F# unified validate matches production for a clean bootstrap, both --json and text output", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const root = fixture(t, "clean");

  const nodeText = ros(root, ["validate"]);
  const fsharpText = fsharpValidate(root);
  assert.equal(nodeText.status, 0);
  assert.equal(fsharpText.status, 0);
  assert.equal(fsharpText.stdout, nodeText.stdout);

  const nodeJson = ros(root, ["validate", "--json"]);
  const fsharpJson = fsharpValidate(root, ["--json"]);
  assert.deepEqual(JSON.parse(fsharpJson.stdout), JSON.parse(nodeJson.stdout));
});

test("F# unified validate matches production combining a backlog-queue finding and a disabled-telemetry finding", (t) => {
  const root = fixture(t, "combined");

  const queuePath = path.join(root, ".ros", "work", "queue.json");
  const queue = JSON.parse(fs.readFileSync(queuePath, "utf8"));
  queue.items.push({
    id: "WI-BAD",
    title: "bad",
    status: "not-a-status",
    tags: [],
    priority: "medium",
    attachments: [],
    createdAt: "2026-01-01T00:00:00.000Z",
    updatedAt: "2026-01-01T00:00:00.000Z",
    createdBy: "unknown",
    source: "manual"
  });
  fs.writeFileSync(queuePath, JSON.stringify(queue, null, 2));

  const rosConfigPath = path.join(root, "ros.json");
  const rosConfig = JSON.parse(fs.readFileSync(rosConfigPath, "utf8"));
  rosConfig.telemetry = { enabled: false };
  fs.writeFileSync(rosConfigPath, JSON.stringify(rosConfig, null, 2));

  const nodeJson = JSON.parse(ros(root, ["validate", "--json"]).stdout);
  const fsharpJson = JSON.parse(fsharpValidate(root, ["--json"]).stdout);
  assert.deepEqual(fsharpJson, nodeJson);
  assert.equal(nodeJson.findings.length, 2);
});

test("F# unified validate matches production for a stale registry, sorted together with other findings", (t) => {
  const root = fixture(t, "stale");

  const decisionsRegistry = path.join(root, "registries", "decisions.json");
  fs.writeFileSync(decisionsRegistry, JSON.stringify({ broken: true }));

  const nodeJson = JSON.parse(ros(root, ["validate", "--json"]).stdout);
  const fsharpJson = JSON.parse(fsharpValidate(root, ["--json"]).stdout);
  assert.deepEqual(fsharpJson, nodeJson);
  assert.ok(nodeJson.findings.some((f) => f.message.includes("registry is stale")));
});

test("F# unified validate matches production combining an artifact parse finding, a work-attribution finding, and text-format rendering", (t) => {
  const root = fixture(t, "artifact-and-attribution");

  fs.writeFileSync(
    path.join(root, "research", "decisions", "DF-BROKEN--test.md"),
    "---\nid: not valid!!\ntitle: Broken\nstatus: accepted\n---\nBody.\n"
  );

  const nodeJson = JSON.parse(ros(root, ["validate", "--json"]).stdout);
  const fsharpJson = JSON.parse(fsharpValidate(root, ["--json"]).stdout);
  assert.deepEqual(fsharpJson, nodeJson);
  assert.ok(nodeJson.findings.length >= 3, "expected at least the artifact id/filename findings plus the work-attribution finding");

  const nodeText = ros(root, ["validate"]);
  const fsharpText = fsharpValidate(root);
  assert.equal(nodeText.status, 1);
  assert.equal(fsharpText.status, 1);
  assert.equal(fsharpText.stdout, nodeText.stdout);
  assert.equal(fsharpText.stderr, nodeText.stderr);
});
