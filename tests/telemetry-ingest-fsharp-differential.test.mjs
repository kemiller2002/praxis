import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { startWork } from "../tools/ros_cli.mjs";
import { ingestTelemetry } from "../tools/ros_telemetry.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-ingest-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Telemetry Ingest Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function writeInputFile(t, name, value) {
  const file = path.join(os.tmpdir(), `${name}-${process.pid}-${Math.random().toString(36).slice(2)}.json`);
  fs.writeFileSync(file, typeof value === "string" ? value : JSON.stringify(value));
  t.after(() => fs.rmSync(file, { force: true }));
  return file;
}

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "telemetry", "ingest", ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function readExecution(root, executionId) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "telemetry", "executions", `${executionId}.json`), "utf8"));
}

function readExecutions(root) {
  const dir = path.join(root, ".ros", "telemetry", "executions");
  return fs.readdirSync(dir).sort().map((name) => JSON.parse(fs.readFileSync(path.join(dir, name), "utf8")));
}

function writeFixtureExecution(root, executionId, workItemId, status, startedAt, capabilities = []) {
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
    capabilities,
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

function metricValue(record, id) {
  const metric = record.metrics.find((entry) => entry.id === id);
  return metric ? metric.value : undefined;
}

function capabilityFor(record, metricId, providerField) {
  return record.capabilities.find((entry) => entry.metricId === metricId && entry.providerField === providerField);
}

test("F# telemetry ingest merges identity/metrics/events, redacts sensitive raw fields, and records derived quality metrics, matching production's real effect", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const nodeRoot = fixture(t, "basic-node");
  const fsharpRoot = fixture(t, "basic-fsharp");

  startWork(nodeRoot, ["WI-A"], { type: "task" });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);

  const input = {
    identity: { model: "claude-opus-5" },
    metrics: [{ id: "tokens.input", value: 100 }],
    events: [{ type: "custom.thing", occurredAt: "2026-01-01T00:00:00.000Z" }],
    raw: { authorization: "secret-token", nested: { password: "hunter2" }, safe: "value" },
    collectedAt: "2026-01-01T00:00:00.000Z"
  };

  const nodeRecord = ingestTelemetry(nodeRoot, "WI-A", input, { adapter: "generic" });
  const inputFile = writeInputFile(t, "basic", input);
  const fsharpResult = runFsharp(fsharpRoot, ["WI-A", "--input", inputFile]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.equal(nodeRecord.identity.model, "claude-opus-5");
  assert.equal(fsharpRecord.identity.model, "claude-opus-5");
  assert.equal(metricValue(nodeRecord, "tokens.input"), 100);
  assert.equal(metricValue(fsharpRecord, "tokens.input"), 100);
  assert.equal(capabilityFor(nodeRecord, "tokens.input", undefined).status, "supported-observed");
  assert.equal(capabilityFor(fsharpRecord, "tokens.input", undefined).status, "supported-observed");
  assert.ok(nodeRecord.events.some((e) => e.type === "custom.thing"));
  assert.ok(fsharpRecord.events.some((e) => e.type === "custom.thing"));

  const nodePayload = nodeRecord.rawTelemetry[0].payload;
  const fsharpPayload = fsharpRecord.rawTelemetry[0].payload;
  assert.deepEqual(nodePayload, fsharpPayload);
  assert.equal(nodePayload.authorization, "[REDACTED_BY_ROS]");

  assert.equal(metricValue(nodeRecord, "telemetry.redactions"), 2);
  assert.equal(metricValue(fsharpRecord, "telemetry.redactions"), 2);
  assert.equal(metricValue(nodeRecord, "telemetry.unknown_fields"), 3);
  assert.equal(metricValue(fsharpRecord, "telemetry.unknown_fields"), 3);

  // The custom event's eventId (a content digest of the caller-supplied
  // object) and the snapshotId/ingestion-eventId (a content digest of
  // {adapter, input}) are both deterministic given identical input text --
  // independent of executionId or wall-clock time -- so they must match
  // byte-for-byte between Node and F#.
  const nodeCustomEvent = nodeRecord.events.find((e) => e.type === "custom.thing");
  const fsharpCustomEvent = fsharpRecord.events.find((e) => e.type === "custom.thing");
  assert.equal(nodeCustomEvent.eventId, fsharpCustomEvent.eventId);

  const nodeIngestionEvent = nodeRecord.events.find((e) => e.type === "telemetry.snapshot.ingested");
  const fsharpIngestionEvent = fsharpRecord.events.find((e) => e.type === "telemetry.snapshot.ingested");
  assert.equal(nodeIngestionEvent.snapshotId, fsharpIngestionEvent.snapshotId);
  assert.equal(nodeIngestionEvent.eventId, fsharpIngestionEvent.eventId);
});

test("F# telemetry ingest deduplicates a repeated identical snapshot, matching production's snapshotId-based dedup", (t) => {
  const nodeRoot = fixture(t, "dedup-node");
  const fsharpRoot = fixture(t, "dedup-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = { metrics: [{ id: "tokens.input", value: 1 }], raw: { safe: "value" }, collectedAt: "2026-01-01T00:00:00.000Z" };
  const inputFile = writeInputFile(t, "dedup", input);

  ingestTelemetry(nodeRoot, "EXE-1", input, { adapter: "generic" });
  const nodeFirst = readExecution(nodeRoot, "EXE-1");
  ingestTelemetry(nodeRoot, "EXE-1", input, { adapter: "generic" });
  const nodeSecond = readExecution(nodeRoot, "EXE-1");

  runFsharp(fsharpRoot, ["EXE-1", "--input", inputFile]);
  const fsharpFirst = readExecution(fsharpRoot, "EXE-1");
  runFsharp(fsharpRoot, ["EXE-1", "--input", inputFile]);
  const fsharpSecond = readExecution(fsharpRoot, "EXE-1");

  assert.equal(nodeSecond.metrics.length, nodeFirst.metrics.length);
  assert.equal(fsharpSecond.metrics.length, fsharpFirst.metrics.length);
  assert.equal(nodeSecond.rawTelemetry.length, 1);
  assert.equal(fsharpSecond.rawTelemetry.length, 1);
  assert.equal(nodeFirst.metrics.length, fsharpFirst.metrics.length);
});

test("F# telemetry ingest merges a directly-declared capability into history, and rejects a metric declared unavailable while reporting a value, with production's exact message", (t) => {
  const nodeRoot = fixture(t, "capability-node");
  const fsharpRoot = fixture(t, "capability-fsharp");

  const seededCapabilities = [
    {
      metricId: "tool.calls",
      status: "unknown",
      reason: "runtime capability not reported or mapped",
      discoveredAt: "2025-12-31T00:00:00.000Z",
      source: { type: "environment", name: "runtime-identity", mechanism: "whitelisted-claude-environment" }
    }
  ];
  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z", seededCapabilities);
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z", seededCapabilities);

  const input = {
    capabilities: [{ metricId: "tool.calls", status: "supported-observed", reason: "custom", discoveredAt: "2026-01-01T00:00:00.000Z" }],
    collectedAt: "2026-01-01T00:00:00.000Z"
  };
  const inputFile = writeInputFile(t, "capability", input);

  ingestTelemetry(nodeRoot, "EXE-1", input, { adapter: "generic" });
  const nodeRecord = readExecution(nodeRoot, "EXE-1");
  runFsharp(fsharpRoot, ["EXE-1", "--input", inputFile]);
  const fsharpRecord = readExecution(fsharpRoot, "EXE-1");

  const nodeCap = capabilityFor(nodeRecord, "tool.calls", undefined);
  const fsharpCap = capabilityFor(fsharpRecord, "tool.calls", undefined);
  assert.equal(nodeCap.status, "supported-observed");
  assert.equal(fsharpCap.status, "supported-observed");
  assert.equal(nodeCap.history.length, 1);
  assert.equal(fsharpCap.history.length, 1);
  assert.equal(nodeCap.history[0].status, fsharpCap.history[0].status);

  const conflictInput = {
    metrics: [{ id: "tool.calls", value: 3 }],
    capabilities: [{ metricId: "tool.calls", status: "unsupported" }],
    collectedAt: "2026-02-01T00:00:00.000Z"
  };
  const conflictFile = writeInputFile(t, "conflict", conflictInput);

  let nodeMessage;
  try {
    ingestTelemetry(nodeRoot, "EXE-1", conflictInput, { adapter: "generic" });
  } catch (error) {
    nodeMessage = error.message;
  }
  const fsharpConflictResult = runFsharp(fsharpRoot, ["EXE-1", "--input", conflictFile]);

  assert.equal(fsharpConflictResult.status, 1);
  assert.match(nodeMessage, /declaring it unavailable or unsupported/);
  assert.match(fsharpConflictResult.stderr, /declaring it unavailable or unsupported/);
  assert.equal(nodeMessage.match(/'(SNAP-[a-f0-9]+)'/)[1], fsharpConflictResult.stderr.match(/'(SNAP-[a-f0-9]+)'/)[1]);
});

test("F# telemetry ingest shallow-merges classification/scope and union-dedups links, matching production exactly", (t) => {
  const nodeRoot = fixture(t, "merge-node");
  const fsharpRoot = fixture(t, "merge-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const firstInput = { links: { commits: ["abc"] }, collectedAt: "2026-01-01T00:00:00.000Z" };
  const secondInput = { classification: { rationale: null }, scope: { operationId: "op-1" }, links: { commits: ["abc", "def"] }, collectedAt: "2026-01-02T00:00:00.000Z" };

  ingestTelemetry(nodeRoot, "EXE-1", firstInput, { adapter: "generic" });
  ingestTelemetry(nodeRoot, "EXE-1", secondInput, { adapter: "generic" });
  const nodeRecord = readExecution(nodeRoot, "EXE-1");

  runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "first", firstInput)]);
  runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "second", secondInput)]);
  const fsharpRecord = readExecution(fsharpRoot, "EXE-1");

  assert.equal(nodeRecord.classification.rationale, null);
  assert.equal(fsharpRecord.classification.rationale, null);
  assert.equal(nodeRecord.scope.operationId, "op-1");
  assert.equal(fsharpRecord.scope.operationId, "op-1");
  assert.deepEqual(nodeRecord.links.commits, ["abc", "def"]);
  assert.deepEqual(fsharpRecord.links.commits, ["abc", "def"]);
});

test("F# telemetry ingest rejects an unregistered metric and a target that is not active, with production's exact messages", (t) => {
  const nodeRoot = fixture(t, "rejects-node");
  const fsharpRoot = fixture(t, "rejects-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(nodeRoot, "EXE-2", "WI-B", "finalized", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-2", "WI-B", "finalized", "2026-01-01T00:00:00.000Z");

  const unknownMetricInput = { metrics: [{ id: "bogus.metric", value: 1 }], collectedAt: "2026-01-01T00:00:00.000Z" };
  let nodeUnknownMessage;
  try {
    ingestTelemetry(nodeRoot, "EXE-1", unknownMetricInput, { adapter: "generic" });
  } catch (error) {
    nodeUnknownMessage = error.message;
  }
  const fsharpUnknownResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "unknown", unknownMetricInput)]);
  assert.equal(fsharpUnknownResult.status, 1);
  assert.equal(nodeUnknownMessage, "unknown normalized metric 'bogus.metric'; preserve it in raw telemetry until it is registered");
  assert.match(fsharpUnknownResult.stderr, /unknown normalized metric 'bogus\.metric'/);

  const emptyInput = { collectedAt: "2026-01-01T00:00:00.000Z" };
  let nodeNotActiveMessage;
  try {
    ingestTelemetry(nodeRoot, "WI-B", emptyInput, { adapter: "generic" });
  } catch (error) {
    nodeNotActiveMessage = error.message;
  }
  const fsharpNotActiveResult = runFsharp(fsharpRoot, ["WI-B", "--input", writeInputFile(t, "notactive", emptyInput)]);
  assert.equal(fsharpNotActiveResult.status, 1);
  assert.equal(nodeNotActiveMessage, "telemetry execution 'WI-B' was not found or is already finalized");
  assert.match(fsharpNotActiveResult.stderr, /telemetry execution 'WI-B' was not found or is already finalized/);
});

test("F# telemetry ingest rejects an unknown adapter with production's exact message and rejects an unimplemented one at the CLI layer", (t) => {
  const nodeRoot = fixture(t, "adapter-node");
  const fsharpRoot = fixture(t, "adapter-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = { collectedAt: "2026-01-01T00:00:00.000Z" };
  let nodeMessage;
  try {
    ingestTelemetry(nodeRoot, "EXE-1", input, { adapter: "bogus-adapter" });
  } catch (error) {
    nodeMessage = error.message;
  }
  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "badadapter", input), "--adapter", "bogus-adapter"]);
  assert.equal(fsharpResult.status, 1);
  assert.equal(nodeMessage, "unknown telemetry adapter 'bogus-adapter'");
  assert.match(fsharpResult.stderr, /unknown telemetry adapter 'bogus-adapter'/);

  const fsharpUnsupportedResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "unsupported", input), "--adapter", "openai-codex"]);
  assert.equal(fsharpUnsupportedResult.status, 2);
  assert.match(fsharpUnsupportedResult.stderr, /telemetry ingest --adapter 'openai-codex' is not yet supported by this CLI/);
});

test("F# telemetry ingest honors repository raw-telemetry retention config, matching production's byte-budget and disabled-policy branches", (t) => {
  const nodeRoot = fixture(t, "retention-node");
  const fsharpRoot = fixture(t, "retention-fsharp");

  for (const root of [nodeRoot, fsharpRoot]) {
    const config = JSON.parse(fs.readFileSync(path.join(root, "ros.json"), "utf8"));
    config.telemetry = { ...(config.telemetry ?? {}), allowRawTelemetry: false };
    fs.writeFileSync(path.join(root, "ros.json"), JSON.stringify(config, null, 2));
  }

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = { raw: { field: "value" }, collectedAt: "2026-01-01T00:00:00.000Z" };
  ingestTelemetry(nodeRoot, "EXE-1", input, { adapter: "generic" });
  const nodeRecord = readExecution(nodeRoot, "EXE-1");
  runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "retention", input)]);
  const fsharpRecord = readExecution(fsharpRoot, "EXE-1");

  assert.equal(nodeRecord.rawTelemetry.length, 0);
  assert.equal(fsharpRecord.rawTelemetry.length, 0);
  assert.equal(metricValue(nodeRecord, "telemetry.raw_snapshots_omitted"), 1);
  assert.equal(metricValue(fsharpRecord, "telemetry.raw_snapshots_omitted"), 1);

  const nodeFieldCap = capabilityFor(nodeRecord, undefined, "$.field");
  const fsharpFieldCap = capabilityFor(fsharpRecord, undefined, "$.field");
  assert.equal(nodeFieldCap.status, "unknown");
  assert.equal(fsharpFieldCap.status, "unknown");
  assert.match(nodeFieldCap.reason, /raw payload was omitted/);
  assert.match(fsharpFieldCap.reason, /raw payload was omitted/);
});

test("F# telemetry ingest accepts input via stdin ('-'), matching production", (t) => {
  const nodeRoot = fixture(t, "stdin-node");
  const fsharpRoot = fixture(t, "stdin-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = { metrics: [{ id: "tokens.input", value: 7 }], collectedAt: "2026-01-01T00:00:00.000Z" };

  ingestTelemetry(nodeRoot, "EXE-1", input, { adapter: "generic" });
  const nodeRecord = readExecution(nodeRoot, "EXE-1");

  const result = spawnSync("dotnet", [fsharpCli, "--root", fsharpRoot, "telemetry", "ingest", "EXE-1", "--input", "-", "--quiet"], {
    cwd: repositoryRoot,
    encoding: "utf8",
    input: JSON.stringify(input)
  });
  assert.equal(result.status, 0, result.stderr);
  const fsharpRecord = readExecution(fsharpRoot, "EXE-1");

  assert.equal(metricValue(nodeRecord, "tokens.input"), 7);
  assert.equal(metricValue(fsharpRecord, "tokens.input"), 7);
});
