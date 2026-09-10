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

function fixture(t, label, workItems) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-start-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Telemetry Start Differential" });
  execFileSync("git", ["-C", root, "init", "-q"]);

  const configFile = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(configFile, "utf8"));

  const contextFile = path.join(root, ".ros", "context", "current.json");
  fs.writeFileSync(
    contextFile,
    `${JSON.stringify(
      {
        schemaVersion: "1.0.0",
        protocolVersion: "1.0.0",
        repository: config.repository.id,
        actor: "actor",
        updatedAt: "2026-01-01T00:00:00.000Z",
        workItems
      },
      null,
      2
    )}\n`
  );

  return { root, config, configFile };
}

function writeFixtureExecution(root, executionId, workItemId, status, startedAt) {
  const directory = path.join(root, ".ros", "telemetry", "executions");
  fs.mkdirSync(directory, { recursive: true });
  const record = {
    schemaVersion: "1.0.0",
    executionId,
    workItemId,
    status,
    startedAt,
    identity: { provider: "anthropic", runtime: "claude-code" },
    provenance: { collector: "ros", collectorVersion: "1.0.0", discoveredAt: startedAt, sources: [] },
    classification: { types: ["development"], rationale: null, evidence: [], rd: null },
    capabilities: [],
    metrics: [],
    rawTelemetry: [],
    events: [],
    repository: { start: { available: false } },
    scope: {},
    qualitySignals: [],
    links: {}
  };
  fs.writeFileSync(path.join(directory, `${executionId}.json`), JSON.stringify(record, null, 2));
}

function contextItem(root, workItemId) {
  const contextFile = path.join(root, ".ros", "context", "current.json");
  const context = JSON.parse(fs.readFileSync(contextFile, "utf8"));
  return context.workItems.find((item) => item.id === workItemId);
}

function ros(root, args) {
  const result = spawnSync(path.join(root, "ros"), args, { cwd: root, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "telemetry", "start", ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

test("F# telemetry start creates a fresh linked execution when the work item is active with no candidates, matching production", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const item = { id: "WI-A", type: "task", state: "active", semanticState: "active", evidence: [] };
  const node = fixture(t, "fresh-node", [item]);
  const fsharp = fixture(t, "fresh-fsharp", [item]);

  const nodeResult = ros(node.root, ["telemetry", "start", "WI-A", "--classification", "research"]);
  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  const nodeRecord = JSON.parse(nodeResult.stdout);

  const fsharpResult = runFsharp(fsharp.root, ["WI-A", "--classification", "research"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(nodeRecord.classification, fsharpRecord.classification);
  assert.equal(nodeRecord.workItemId, fsharpRecord.workItemId);
  assert.equal(nodeRecord.status, fsharpRecord.status);

  const nodeItem = contextItem(node.root, "WI-A");
  const fsharpItem = contextItem(fsharp.root, "WI-A");
  assert.deepEqual(nodeItem.telemetryExecutionIds, [nodeRecord.executionId]);
  assert.deepEqual(fsharpItem.telemetryExecutionIds, [fsharpRecord.executionId]);
});

test("F# telemetry start recovers a detached (unlinked) execution instead of creating a new one, matching production", (t) => {
  const item = { id: "WI-B", type: "task", state: "active", semanticState: "active", evidence: [] };
  const node = fixture(t, "detached-node", [item]);
  const fsharp = fixture(t, "detached-fsharp", [item]);

  writeFixtureExecution(node.root, "EXE-DETACHED", "WI-B", "active", "2020-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharp.root, "EXE-DETACHED", "WI-B", "active", "2020-01-01T00:00:00.000Z");

  const nodeResult = ros(node.root, ["telemetry", "start", "WI-B"]);
  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  const fsharpResult = runFsharp(fsharp.root, ["WI-B"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const nodeRecord = JSON.parse(nodeResult.stdout);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);
  assert.equal(nodeRecord.executionId, "EXE-DETACHED");
  assert.equal(fsharpRecord.executionId, "EXE-DETACHED");

  assert.deepEqual(contextItem(node.root, "WI-B").telemetryExecutionIds, ["EXE-DETACHED"]);
  assert.deepEqual(contextItem(fsharp.root, "WI-B").telemetryExecutionIds, ["EXE-DETACHED"]);
});

test("F# telemetry start creates a new execution when the only candidate is already linked, matching production", (t) => {
  const item = {
    id: "WI-C",
    type: "task",
    state: "active",
    semanticState: "active",
    evidence: [],
    telemetryExecutionIds: ["EXE-LINKED"]
  };
  const node = fixture(t, "relink-node", [item]);
  const fsharp = fixture(t, "relink-fsharp", [item]);

  writeFixtureExecution(node.root, "EXE-LINKED", "WI-C", "active", "2020-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharp.root, "EXE-LINKED", "WI-C", "active", "2020-01-01T00:00:00.000Z");

  const nodeResult = ros(node.root, ["telemetry", "start", "WI-C"]);
  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  const fsharpResult = runFsharp(fsharp.root, ["WI-C"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const nodeRecord = JSON.parse(nodeResult.stdout);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);
  assert.notEqual(nodeRecord.executionId, "EXE-LINKED");
  assert.notEqual(fsharpRecord.executionId, "EXE-LINKED");

  assert.deepEqual(contextItem(node.root, "WI-C").telemetryExecutionIds, ["EXE-LINKED", nodeRecord.executionId]);
  assert.deepEqual(contextItem(fsharp.root, "WI-C").telemetryExecutionIds, ["EXE-LINKED", fsharpRecord.executionId]);
});

test("F# telemetry start rejects ambiguous detached candidates with production's exact rerun message", (t) => {
  const item = { id: "WI-D", type: "task", state: "active", semanticState: "active", evidence: [] };
  const node = fixture(t, "ambiguous-node", [item]);
  const fsharp = fixture(t, "ambiguous-fsharp", [item]);

  for (const { root } of [node, fsharp]) {
    writeFixtureExecution(root, "EXE-DETACHED-1", "WI-D", "active", "2020-01-01T00:00:00.000Z");
    writeFixtureExecution(root, "EXE-DETACHED-2", "WI-D", "active", "2020-01-01T00:00:01.000Z");
  }

  const nodeResult = ros(node.root, ["telemetry", "start", "WI-D"]);
  const fsharpResult = runFsharp(fsharp.root, ["WI-D"]);

  assert.equal(nodeResult.status, 1);
  assert.equal(fsharpResult.status, 1);
  const expected =
    "multiple detached telemetry executions require explicit selection for 'WI-D'; rerun with --execution-id one of: EXE-DETACHED-1, EXE-DETACHED-2";
  assert.match(nodeResult.stderr, /multiple detached telemetry executions require explicit selection for 'WI-D'; rerun with --execution-id one of: EXE-DETACHED-1, EXE-DETACHED-2/);
  assert.equal(fsharpResult.stderr.trim(), `ERROR ${expected}`);
});

test("F# telemetry start rejects a work item that is not active or blocked with production's exact message", (t) => {
  const item = { id: "WI-E", type: "task", state: "ready", semanticState: "ready", evidence: [] };
  const node = fixture(t, "notactive-node", [item]);
  const fsharp = fixture(t, "notactive-fsharp", [item]);

  const nodeResult = ros(node.root, ["telemetry", "start", "WI-E"]);
  const fsharpResult = runFsharp(fsharp.root, ["WI-E"]);

  assert.equal(nodeResult.status, 1);
  assert.equal(fsharpResult.status, 1);
  const expected = "work item 'WI-E' must be active or blocked before starting telemetry";
  assert.match(nodeResult.stderr, new RegExp(expected.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  assert.equal(fsharpResult.stderr.trim(), `ERROR ${expected}`);
});

test("F# telemetry start rejects an unknown work item with the same message as an inactive item", (t) => {
  const item = { id: "WI-A", type: "task", state: "active", semanticState: "active", evidence: [] };
  const node = fixture(t, "unknown-node", [item]);
  const fsharp = fixture(t, "unknown-fsharp", [item]);

  const nodeResult = ros(node.root, ["telemetry", "start", "WI-NOPE"]);
  const fsharpResult = runFsharp(fsharp.root, ["WI-NOPE"]);

  assert.equal(nodeResult.status, 1);
  assert.equal(fsharpResult.status, 1);
  const expected = "work item 'WI-NOPE' must be active or blocked before starting telemetry";
  assert.match(nodeResult.stderr, new RegExp(expected.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  assert.equal(fsharpResult.stderr.trim(), `ERROR ${expected}`);
});

test("F# telemetry start returns null without mutating context when telemetry is disabled and there is no candidate, matching production", (t) => {
  const item = { id: "WI-F", type: "task", state: "active", semanticState: "active", evidence: [] };
  const node = fixture(t, "disabled-node", [item]);
  const fsharp = fixture(t, "disabled-fsharp", [item]);

  for (const { config, configFile } of [node, fsharp]) {
    config.telemetry.enabled = false;
    fs.writeFileSync(configFile, `${JSON.stringify(config, null, 2)}\n`);
  }

  const nodeContextBefore = fs.readFileSync(path.join(node.root, ".ros", "context", "current.json"), "utf8");
  const fsharpContextBefore = fs.readFileSync(path.join(fsharp.root, ".ros", "context", "current.json"), "utf8");

  const nodeResult = ros(node.root, ["telemetry", "start", "WI-F"]);
  const fsharpResult = runFsharp(fsharp.root, ["WI-F"]);

  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  assert.equal(nodeResult.stdout.trim(), "null");
  assert.equal(fsharpResult.stdout.trim(), "null");

  assert.equal(fs.readFileSync(path.join(node.root, ".ros", "context", "current.json"), "utf8"), nodeContextBefore);
  assert.equal(fs.readFileSync(path.join(fsharp.root, ".ros", "context", "current.json"), "utf8"), fsharpContextBefore);
});

test("F# telemetry start rejects the 12 identity/execution-id flags production exposes, as a loud not-yet-supported gap", (t) => {
  const item = { id: "WI-A", type: "task", state: "active", semanticState: "active", evidence: [] };
  const fsharp = fixture(t, "excluded-fsharp", [item]);

  const fsharpResult = runFsharp(fsharp.root, ["WI-A", "--execution-id", "EXE-SOMETHING"]);
  assert.equal(fsharpResult.status, 2);
  assert.equal(fsharpResult.stderr.trim(), "ERROR telemetry start --execution-id is not yet supported by this CLI");
});
