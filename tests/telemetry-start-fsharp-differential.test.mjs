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
// `telemetry start` command with the exact same call sequence as each test,
// then frozen here. Node is retained in this repository only as the web
// server's internal dependency (DF-ROS-2026-A033) and is no longer executed
// as a live oracle by this test suite.
const GOLDEN = {
  fresh: {
    classification: { types: ["research"], rationale: null, evidence: [], rd: null },
    workItemId: "WI-A",
    status: "active"
  },
  identity: {
    provider: "acme",
    model: "acme-model",
    modelVersion: "v2",
    runtime: "acme-cli",
    runtimeVersion: "9.9.9",
    sessionId: "sess-123",
    conversationId: "conv-456",
    runId: "run-789",
    agentId: "agent-1",
    subagentId: "subagent-2",
    parentExecutionId: "EXE-PARENT"
  }
};

function fixture(t, label, workItems) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-start-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 }));
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

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "telemetry", "start", ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

test("F# telemetry start creates a fresh linked execution when the work item is active with no candidates, matching production", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const item = { id: "WI-A", type: "task", state: "active", semanticState: "active", evidence: [] };
  const fsharp = fixture(t, "fresh-fsharp", [item]);

  const fsharpResult = runFsharp(fsharp.root, ["WI-A", "--classification", "research"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(fsharpRecord.classification, GOLDEN.fresh.classification);
  assert.equal(fsharpRecord.workItemId, GOLDEN.fresh.workItemId);
  assert.equal(fsharpRecord.status, GOLDEN.fresh.status);

  const fsharpItem = contextItem(fsharp.root, "WI-A");
  assert.deepEqual(fsharpItem.telemetryExecutionIds, [fsharpRecord.executionId]);
});

test("F# telemetry start recovers a detached (unlinked) execution instead of creating a new one, matching production", (t) => {
  const item = { id: "WI-B", type: "task", state: "active", semanticState: "active", evidence: [] };
  const fsharp = fixture(t, "detached-fsharp", [item]);

  writeFixtureExecution(fsharp.root, "EXE-DETACHED", "WI-B", "active", "2020-01-01T00:00:00.000Z");

  const fsharpResult = runFsharp(fsharp.root, ["WI-B"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpRecord = JSON.parse(fsharpResult.stdout);
  assert.equal(fsharpRecord.executionId, "EXE-DETACHED");

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
  const fsharp = fixture(t, "relink-fsharp", [item]);

  writeFixtureExecution(fsharp.root, "EXE-LINKED", "WI-C", "active", "2020-01-01T00:00:00.000Z");

  const fsharpResult = runFsharp(fsharp.root, ["WI-C"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpRecord = JSON.parse(fsharpResult.stdout);
  assert.notEqual(fsharpRecord.executionId, "EXE-LINKED");

  assert.deepEqual(contextItem(fsharp.root, "WI-C").telemetryExecutionIds, ["EXE-LINKED", fsharpRecord.executionId]);
});

test("F# telemetry start rejects ambiguous detached candidates with production's exact rerun message", (t) => {
  const item = { id: "WI-D", type: "task", state: "active", semanticState: "active", evidence: [] };
  const fsharp = fixture(t, "ambiguous-fsharp", [item]);

  writeFixtureExecution(fsharp.root, "EXE-DETACHED-1", "WI-D", "active", "2020-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharp.root, "EXE-DETACHED-2", "WI-D", "active", "2020-01-01T00:00:01.000Z");

  const fsharpResult = runFsharp(fsharp.root, ["WI-D"]);

  assert.equal(fsharpResult.status, 1);
  const expected =
    "multiple detached telemetry executions require explicit selection for 'WI-D'; rerun with --execution-id one of: EXE-DETACHED-1, EXE-DETACHED-2";
  assert.equal(fsharpResult.stderr.trim(), `ERROR ${expected}`);
});

test("F# telemetry start rejects a work item that is not active or blocked with production's exact message", (t) => {
  const item = { id: "WI-E", type: "task", state: "ready", semanticState: "ready", evidence: [] };
  const fsharp = fixture(t, "notactive-fsharp", [item]);

  const fsharpResult = runFsharp(fsharp.root, ["WI-E"]);

  assert.equal(fsharpResult.status, 1);
  const expected = "work item 'WI-E' must be active or blocked before starting telemetry";
  assert.equal(fsharpResult.stderr.trim(), `ERROR ${expected}`);
});

test("F# telemetry start rejects an unknown work item with the same message as an inactive item", (t) => {
  const item = { id: "WI-A", type: "task", state: "active", semanticState: "active", evidence: [] };
  const fsharp = fixture(t, "unknown-fsharp", [item]);

  const fsharpResult = runFsharp(fsharp.root, ["WI-NOPE"]);

  assert.equal(fsharpResult.status, 1);
  const expected = "work item 'WI-NOPE' must be active or blocked before starting telemetry";
  assert.equal(fsharpResult.stderr.trim(), `ERROR ${expected}`);
});

test("F# telemetry start returns null without mutating context when telemetry is disabled and there is no candidate, matching production", (t) => {
  const item = { id: "WI-F", type: "task", state: "active", semanticState: "active", evidence: [] };
  const fsharp = fixture(t, "disabled-fsharp", [item]);

  fsharp.config.telemetry.enabled = false;
  fs.writeFileSync(fsharp.configFile, `${JSON.stringify(fsharp.config, null, 2)}\n`);

  const fsharpContextBefore = fs.readFileSync(path.join(fsharp.root, ".ros", "context", "current.json"), "utf8");

  const fsharpResult = runFsharp(fsharp.root, ["WI-F"]);

  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  assert.equal(fsharpResult.stdout.trim(), "null");

  assert.equal(fs.readFileSync(path.join(fsharp.root, ".ros", "context", "current.json"), "utf8"), fsharpContextBefore);
});

test("F# telemetry start --execution-id recovers a matching detached candidate instead of creating a new one, matching production", (t) => {
  const item = { id: "WI-G", type: "task", state: "active", semanticState: "active", evidence: [] };
  const fsharp = fixture(t, "execid-match-fsharp", [item]);

  writeFixtureExecution(fsharp.root, "EXE-DETACHED-1", "WI-G", "active", "2020-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharp.root, "EXE-DETACHED-2", "WI-G", "active", "2020-01-01T00:00:01.000Z");

  const fsharpResult = runFsharp(fsharp.root, ["WI-G", "--execution-id", "EXE-DETACHED-2"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpRecord = JSON.parse(fsharpResult.stdout);
  assert.equal(fsharpRecord.executionId, "EXE-DETACHED-2");

  assert.deepEqual(contextItem(fsharp.root, "WI-G").telemetryExecutionIds, ["EXE-DETACHED-2"]);
});

test("F# telemetry start --execution-id rejects a non-matching id when other detached candidates exist, with production's exact rerun message", (t) => {
  const item = { id: "WI-H", type: "task", state: "active", semanticState: "active", evidence: [] };
  const fsharp = fixture(t, "execid-conflict-fsharp", [item]);

  writeFixtureExecution(fsharp.root, "EXE-DETACHED-1", "WI-H", "active", "2020-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharp.root, "EXE-DETACHED-2", "WI-H", "active", "2020-01-01T00:00:01.000Z");

  const fsharpResult = runFsharp(fsharp.root, ["WI-H", "--execution-id", "EXE-NOPE"]);

  assert.equal(fsharpResult.status, 1);
  const expected = "detached telemetry execution must be linked before creating 'EXE-NOPE' for 'WI-H'; rerun with --execution-id EXE-DETACHED-1";
  assert.equal(fsharpResult.stderr.trim(), `ERROR ${expected}`);
});

test("F# telemetry start --execution-id becomes the newly created execution's own id when no detached candidate exists, matching production", (t) => {
  const item = { id: "WI-I", type: "task", state: "active", semanticState: "active", evidence: [] };
  const fsharp = fixture(t, "execid-fresh-fsharp", [item]);

  const fsharpResult = runFsharp(fsharp.root, ["WI-I", "--execution-id", "EXE-CUSTOM-ID"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpRecord = JSON.parse(fsharpResult.stdout);
  assert.equal(fsharpRecord.executionId, "EXE-CUSTOM-ID");
});

test("F# telemetry start's 11 identity-override flags produce an identity object identical to production's own", (t) => {
  const item = { id: "WI-J", type: "task", state: "active", semanticState: "active", evidence: [] };
  const fsharp = fixture(t, "identity-fsharp", [item]);

  const identityArgs = [
    "--provider", "acme",
    "--model", "acme-model",
    "--model-version", "v2",
    "--runtime", "acme-cli",
    "--runtime-version", "9.9.9",
    "--session", "sess-123",
    "--conversation", "conv-456",
    "--run", "run-789",
    "--agent", "agent-1",
    "--subagent", "subagent-2",
    "--parent-execution", "EXE-PARENT"
  ];

  const fsharpResult = runFsharp(fsharp.root, ["WI-J", ...identityArgs]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  const normIdentity = (record) => {
    const { orchestration, ...rest } = record.identity;
    return rest;
  };
  assert.deepEqual(normIdentity(fsharpRecord), GOLDEN.identity);
});
