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
// implementation (tools/ros_telemetry.mjs's ingestTelemetry) with the exact
// same call sequence as each test, then frozen here. Node is retained in
// this repository only as the web server's internal dependency
// (DF-ROS-2026-A033) and is no longer executed as a live oracle by this test
// suite. eventId/snapshotId values are deterministic content digests of the
// caller-supplied input, so they are still expected to match F#'s real
// output byte-for-byte.
const GOLDEN = {
  test1Payload: {
    authorization: "[REDACTED_BY_ROS]",
    nested: { password: "[REDACTED_BY_ROS]" },
    safe: "value"
  },
  test1CustomEventId: "TEVT-2541ad12ab6dd91030b33689",
  test1SnapshotId: "SNAP-8d6a5b7d21c803d74551b507",
  test1IngestionEventId: "TEVT-d97004e63ef02225e7a84dab",
  test2NodeFirstMetricsLength: 2,
  test3CapHistoryStatus: "unknown",
  test3ConflictSnapId: "SNAP-01ce3a53e3cc3b0792defcf0"
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-ingest-${label}-`));
  t.after(() => {
    try {
      fs.rmSync(root, { recursive: true, force: true });
    } catch {
      // Cleanup best-effort: a leftover temp dir under CI I/O contention isn't a test failure.
    }
  });
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
  const fsharpRoot = fixture(t, "basic-fsharp");

  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);

  const input = {
    identity: { model: "claude-opus-5" },
    metrics: [{ id: "tokens.input", value: 100 }],
    events: [{ type: "custom.thing", occurredAt: "2026-01-01T00:00:00.000Z" }],
    raw: { authorization: "secret-token", nested: { password: "hunter2" }, safe: "value" },
    collectedAt: "2026-01-01T00:00:00.000Z"
  };

  const inputFile = writeInputFile(t, "basic", input);
  const fsharpResult = runFsharp(fsharpRoot, ["WI-A", "--input", inputFile]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.equal(fsharpRecord.identity.model, "claude-opus-5");
  assert.equal(metricValue(fsharpRecord, "tokens.input"), 100);
  assert.equal(capabilityFor(fsharpRecord, "tokens.input", undefined).status, "supported-observed");
  assert.ok(fsharpRecord.events.some((e) => e.type === "custom.thing"));

  const fsharpPayload = fsharpRecord.rawTelemetry[0].payload;
  assert.deepEqual(GOLDEN.test1Payload, fsharpPayload);
  assert.equal(fsharpPayload.authorization, "[REDACTED_BY_ROS]");

  assert.equal(metricValue(fsharpRecord, "telemetry.redactions"), 2);
  assert.equal(metricValue(fsharpRecord, "telemetry.unknown_fields"), 3);

  // The custom event's eventId (a content digest of the caller-supplied
  // object) and the snapshotId/ingestion-eventId (a content digest of
  // {adapter, input}) are both deterministic given identical input text --
  // independent of executionId or wall-clock time -- so they must match
  // byte-for-byte between Node and F#.
  const fsharpCustomEvent = fsharpRecord.events.find((e) => e.type === "custom.thing");
  assert.equal(GOLDEN.test1CustomEventId, fsharpCustomEvent.eventId);

  const fsharpIngestionEvent = fsharpRecord.events.find((e) => e.type === "telemetry.snapshot.ingested");
  assert.equal(GOLDEN.test1SnapshotId, fsharpIngestionEvent.snapshotId);
  assert.equal(GOLDEN.test1IngestionEventId, fsharpIngestionEvent.eventId);
});

test("F# telemetry ingest deduplicates a repeated identical snapshot, matching production's snapshotId-based dedup", (t) => {
  const fsharpRoot = fixture(t, "dedup-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = { metrics: [{ id: "tokens.input", value: 1 }], raw: { safe: "value" }, collectedAt: "2026-01-01T00:00:00.000Z" };
  const inputFile = writeInputFile(t, "dedup", input);

  runFsharp(fsharpRoot, ["EXE-1", "--input", inputFile]);
  const fsharpFirst = readExecution(fsharpRoot, "EXE-1");
  runFsharp(fsharpRoot, ["EXE-1", "--input", inputFile]);
  const fsharpSecond = readExecution(fsharpRoot, "EXE-1");

  assert.equal(fsharpSecond.metrics.length, fsharpFirst.metrics.length);
  assert.equal(fsharpSecond.rawTelemetry.length, 1);
  assert.equal(GOLDEN.test2NodeFirstMetricsLength, fsharpFirst.metrics.length);
});

test("F# telemetry ingest merges a directly-declared capability into history, and rejects a metric declared unavailable while reporting a value, with production's exact message", (t) => {
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
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z", seededCapabilities);

  const input = {
    capabilities: [{ metricId: "tool.calls", status: "supported-observed", reason: "custom", discoveredAt: "2026-01-01T00:00:00.000Z" }],
    collectedAt: "2026-01-01T00:00:00.000Z"
  };
  const inputFile = writeInputFile(t, "capability", input);

  runFsharp(fsharpRoot, ["EXE-1", "--input", inputFile]);
  const fsharpRecord = readExecution(fsharpRoot, "EXE-1");

  const fsharpCap = capabilityFor(fsharpRecord, "tool.calls", undefined);
  assert.equal(fsharpCap.status, "supported-observed");
  assert.equal(fsharpCap.history.length, 1);
  assert.equal(GOLDEN.test3CapHistoryStatus, fsharpCap.history[0].status);

  const conflictInput = {
    metrics: [{ id: "tool.calls", value: 3 }],
    capabilities: [{ metricId: "tool.calls", status: "unsupported" }],
    collectedAt: "2026-02-01T00:00:00.000Z"
  };
  const conflictFile = writeInputFile(t, "conflict", conflictInput);

  const fsharpConflictResult = runFsharp(fsharpRoot, ["EXE-1", "--input", conflictFile]);

  assert.equal(fsharpConflictResult.status, 1);
  assert.match(fsharpConflictResult.stderr, /declaring it unavailable or unsupported/);
  assert.equal(GOLDEN.test3ConflictSnapId, fsharpConflictResult.stderr.match(/'(SNAP-[a-f0-9]+)'/)[1]);
});

test("F# telemetry ingest shallow-merges classification/scope and union-dedups links, matching production exactly", (t) => {
  const fsharpRoot = fixture(t, "merge-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const firstInput = { links: { commits: ["abc"] }, collectedAt: "2026-01-01T00:00:00.000Z" };
  const secondInput = { classification: { rationale: null }, scope: { operationId: "op-1" }, links: { commits: ["abc", "def"] }, collectedAt: "2026-01-02T00:00:00.000Z" };

  runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "first", firstInput)]);
  runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "second", secondInput)]);
  const fsharpRecord = readExecution(fsharpRoot, "EXE-1");

  assert.equal(fsharpRecord.classification.rationale, null);
  assert.equal(fsharpRecord.scope.operationId, "op-1");
  assert.deepEqual(fsharpRecord.links.commits, ["abc", "def"]);
});

test("F# telemetry ingest rejects an unregistered metric and a target that is not active, with production's exact messages", (t) => {
  const fsharpRoot = fixture(t, "rejects-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-2", "WI-B", "finalized", "2026-01-01T00:00:00.000Z");

  const unknownMetricInput = { metrics: [{ id: "bogus.metric", value: 1 }], collectedAt: "2026-01-01T00:00:00.000Z" };
  const fsharpUnknownResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "unknown", unknownMetricInput)]);
  assert.equal(fsharpUnknownResult.status, 1);
  assert.match(fsharpUnknownResult.stderr, /unknown normalized metric 'bogus\.metric'/);

  const emptyInput = { collectedAt: "2026-01-01T00:00:00.000Z" };
  const fsharpNotActiveResult = runFsharp(fsharpRoot, ["WI-B", "--input", writeInputFile(t, "notactive", emptyInput)]);
  assert.equal(fsharpNotActiveResult.status, 1);
  assert.match(fsharpNotActiveResult.stderr, /telemetry execution 'WI-B' was not found or is already finalized/);
});

test("F# telemetry ingest rejects an unknown adapter with production's exact message", (t) => {
  const fsharpRoot = fixture(t, "adapter-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = { collectedAt: "2026-01-01T00:00:00.000Z" };
  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "badadapter", input), "--adapter", "bogus-adapter"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(fsharpResult.stderr, /unknown telemetry adapter 'bogus-adapter'/);
});

test("F# telemetry ingest honors repository raw-telemetry retention config, matching production's byte-budget and disabled-policy branches", (t) => {
  const fsharpRoot = fixture(t, "retention-fsharp");

  const config = JSON.parse(fs.readFileSync(path.join(fsharpRoot, "ros.json"), "utf8"));
  config.telemetry = { ...(config.telemetry ?? {}), allowRawTelemetry: false };
  fs.writeFileSync(path.join(fsharpRoot, "ros.json"), JSON.stringify(config, null, 2));

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = { raw: { field: "value" }, collectedAt: "2026-01-01T00:00:00.000Z" };
  runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "retention", input)]);
  const fsharpRecord = readExecution(fsharpRoot, "EXE-1");

  assert.equal(fsharpRecord.rawTelemetry.length, 0);
  assert.equal(metricValue(fsharpRecord, "telemetry.raw_snapshots_omitted"), 1);

  const fsharpFieldCap = capabilityFor(fsharpRecord, undefined, "$.field");
  assert.equal(fsharpFieldCap.status, "unknown");
  assert.match(fsharpFieldCap.reason, /raw payload was omitted/);
});

test("F# telemetry ingest accepts input via stdin ('-'), matching production", (t) => {
  const fsharpRoot = fixture(t, "stdin-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = { metrics: [{ id: "tokens.input", value: 7 }], collectedAt: "2026-01-01T00:00:00.000Z" };

  const result = spawnSync("dotnet", [fsharpCli, "--root", fsharpRoot, "telemetry", "ingest", "EXE-1", "--input", "-", "--quiet"], {
    cwd: repositoryRoot,
    encoding: "utf8",
    input: JSON.stringify(input)
  });
  assert.equal(result.status, 0, result.stderr);
  const fsharpRecord = readExecution(fsharpRoot, "EXE-1");

  assert.equal(metricValue(fsharpRecord, "tokens.input"), 7);
});
