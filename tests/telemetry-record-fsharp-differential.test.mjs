import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { startWork } from "../tools/ros_cli.mjs";
import { recordTelemetryMetric } from "../tools/ros_telemetry.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-record-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Telemetry Record Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "telemetry", "record", ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function readExecution(root, executionId) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "telemetry", "executions", `${executionId}.json`), "utf8"));
}

function readExecutions(root) {
  const dir = path.join(root, ".ros", "telemetry", "executions");
  return fs.readdirSync(dir).sort().map((name) => JSON.parse(fs.readFileSync(path.join(dir, name), "utf8")));
}

// The same defaults production's `ros_cli.mjs` CLI dispatch applies before
// calling `recordTelemetryMetric` directly -- that defaulting lives at the
// CLI layer, not inside `recordTelemetryMetric`/`normalizeMetric`
// themselves, so a direct call (matching this suite's own established
// convention of importing effects rather than spawning the Node CLI) must
// supply them explicitly to reproduce real `./ros telemetry record` output.
function cliDefaultedInput(overrides = {}) {
  return {
    unit: undefined,
    currency: undefined,
    quality: "observed",
    confidence: null,
    scope: "execution",
    source: { type: "agent-report", name: "ros-telemetry-cli", mechanism: "explicit-metric-record" },
    pricing: null,
    collectedAt: undefined,
    ...overrides
  };
}

function lastMetricOf(record) {
  return record.metrics.at(-1);
}

function writeFixtureExecution(root, executionId, workItemId, status, startedAt, capabilities = []) {
  const directory = path.join(root, ".ros", "telemetry", "executions");
  fs.mkdirSync(directory, { recursive: true });
  const record = { schemaVersion: "1.0.0", executionId, workItemId, status, startedAt, events: [], metrics: [], capabilities };
  fs.writeFileSync(path.join(directory, `${executionId}.json`), JSON.stringify(record, null, 2));
}

test("F# telemetry record with no target resolves the single active work item's execution, matching production's real effect", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const nodeRoot = fixture(t, "no-target-node");
  const fsharpRoot = fixture(t, "no-target-fsharp");

  startWork(nodeRoot, ["WI-A"], { type: "task" });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);

  const nodeRecord = recordTelemetryMetric(
    nodeRoot,
    undefined,
    cliDefaultedInput({ id: "tokens.input", value: 42, collectedAt: "2026-01-01T00:00:00.000Z" })
  );
  const fsharpResult = runFsharp(fsharpRoot, ["--metric", "tokens.input", "--value", "42", "--collected-at", "2026-01-01T00:00:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpMetric = JSON.parse(fsharpResult.stdout);
  const nodeMetric = lastMetricOf(nodeRecord);

  assert.deepEqual(nodeMetric, fsharpMetric);
  assert.equal(nodeMetric.measurementId.startsWith("MEAS-"), true);
});

test("F# telemetry record with a work-item target matches only the currently-active execution, ignoring a finalized sibling", (t) => {
  const nodeRoot = fixture(t, "workitem-node");
  const fsharpRoot = fixture(t, "workitem-fsharp");

  for (const root of [nodeRoot, fsharpRoot]) {
    writeFixtureExecution(root, "EXE-OLD", "WI-A", "finalized", "2026-01-01T00:00:00.000Z");
    writeFixtureExecution(root, "EXE-NEW", "WI-A", "active", "2026-01-01T00:05:00.000Z");
  }

  const nodeRecord = recordTelemetryMetric(
    nodeRoot,
    "WI-A",
    cliDefaultedInput({ id: "tokens.input", value: 1, collectedAt: "2026-01-01T00:00:00.000Z" })
  );
  const fsharpResult = runFsharp(fsharpRoot, ["WI-A", "--metric", "tokens.input", "--value", "1", "--collected-at", "2026-01-01T00:00:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  assert.equal(nodeRecord.executionId, "EXE-NEW");
  const fsharpNew = readExecution(fsharpRoot, "EXE-NEW");
  assert.equal(fsharpNew.executionId, "EXE-NEW");
  assert.equal(nodeRecord.metrics.length, 1);
  assert.equal(fsharpNew.metrics.length, 1);

  const nodeOld = readExecution(nodeRoot, "EXE-OLD");
  const fsharpOld = readExecution(fsharpRoot, "EXE-OLD");
  assert.equal(nodeOld.metrics.length, 0);
  assert.equal(fsharpOld.metrics.length, 0);
});

test("F# telemetry record with an EXE-prefixed target resolves by exact execution id", (t) => {
  const nodeRoot = fixture(t, "exe-target-node");
  const fsharpRoot = fixture(t, "exe-target-fsharp");

  for (const root of [nodeRoot, fsharpRoot]) {
    writeFixtureExecution(root, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
    writeFixtureExecution(root, "EXE-2", "WI-B", "active", "2026-01-01T00:00:01.000Z");
  }

  recordTelemetryMetric(nodeRoot, "EXE-1", cliDefaultedInput({ id: "tokens.input", value: 5, collectedAt: "2026-01-01T00:00:00.000Z" }));
  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--metric", "tokens.input", "--value", "5", "--collected-at", "2026-01-01T00:00:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const nodeTargeted = readExecution(nodeRoot, "EXE-1");
  const fsharpTargeted = readExecution(fsharpRoot, "EXE-1");
  assert.equal(nodeTargeted.metrics.length, 1);
  assert.equal(fsharpTargeted.metrics.length, 1);

  const nodeOther = readExecution(nodeRoot, "EXE-2");
  const fsharpOther = readExecution(fsharpRoot, "EXE-2");
  assert.equal(nodeOther.metrics.length, 0);
  assert.equal(fsharpOther.metrics.length, 0);
});

test("F# telemetry record with no target and zero or multiple active-or-blocked work items rejects with production's exact ambiguity message", (t) => {
  const nodeRootZero = fixture(t, "ambiguous-zero-node");
  const fsharpRootZero = fixture(t, "ambiguous-zero-fsharp");

  let nodeMessage;
  try {
    recordTelemetryMetric(nodeRootZero, undefined, cliDefaultedInput({ id: "tokens.input", value: 1 }));
  } catch (error) {
    nodeMessage = error.message;
  }
  const fsharpResultZero = runFsharp(fsharpRootZero, ["--metric", "tokens.input", "--value", "1"]);

  assert.equal(fsharpResultZero.status, 1);
  assert.equal(nodeMessage, "telemetry target is ambiguous; provide a work-item or execution ID");
  assert.match(fsharpResultZero.stderr, /telemetry target is ambiguous; provide a work-item or execution ID/);

  const nodeRootMany = fixture(t, "ambiguous-many-node");
  const fsharpRootMany = fixture(t, "ambiguous-many-fsharp");

  startWork(nodeRootMany, ["WI-A", "WI-B"], { type: "task" });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRootMany, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRootMany, "work", "start", "--id", "WI-B", "--occurred-at", "2026-09-10T18:00:01.000Z", "--type", "task"]);

  let nodeManyMessage;
  try {
    recordTelemetryMetric(nodeRootMany, undefined, cliDefaultedInput({ id: "tokens.input", value: 1 }));
  } catch (error) {
    nodeManyMessage = error.message;
  }
  const fsharpResultMany = runFsharp(fsharpRootMany, ["--metric", "tokens.input", "--value", "1"]);

  assert.equal(fsharpResultMany.status, 1);
  assert.equal(nodeManyMessage, "telemetry target is ambiguous; provide a work-item or execution ID");
  assert.match(fsharpResultMany.stderr, /telemetry target is ambiguous; provide a work-item or execution ID/);
});

test("F# telemetry record rejects a target whose only matching execution is not active, matching production's exact 'was not found or is already finalized' message", (t) => {
  const nodeRoot = fixture(t, "already-finalized-node");
  const fsharpRoot = fixture(t, "already-finalized-fsharp");

  for (const root of [nodeRoot, fsharpRoot]) {
    writeFixtureExecution(root, "EXE-1", "WI-A", "finalized", "2026-01-01T00:00:00.000Z");
  }

  let nodeMessage;
  try {
    recordTelemetryMetric(nodeRoot, "WI-A", cliDefaultedInput({ id: "tokens.input", value: 1 }));
  } catch (error) {
    nodeMessage = error.message;
  }
  const fsharpResult = runFsharp(fsharpRoot, ["WI-A", "--metric", "tokens.input", "--value", "1"]);

  assert.equal(fsharpResult.status, 1);
  assert.equal(nodeMessage, "telemetry execution 'WI-A' was not found or is already finalized");
  assert.match(fsharpResult.stderr, /telemetry execution 'WI-A' was not found or is already finalized/);
});

test("F# telemetry record rejects an unregistered metric id and a non-finite value with production's exact messages", (t) => {
  const nodeRoot = fixture(t, "invalid-input-node");
  const fsharpRoot = fixture(t, "invalid-input-fsharp");

  for (const root of [nodeRoot, fsharpRoot]) {
    writeFixtureExecution(root, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  }

  let nodeUnknownMessage;
  try {
    recordTelemetryMetric(nodeRoot, "EXE-1", cliDefaultedInput({ id: "bogus.metric", value: 1 }));
  } catch (error) {
    nodeUnknownMessage = error.message;
  }
  const fsharpUnknownResult = runFsharp(fsharpRoot, ["EXE-1", "--metric", "bogus.metric", "--value", "1"]);

  assert.equal(fsharpUnknownResult.status, 1);
  assert.equal(nodeUnknownMessage, "unknown normalized metric 'bogus.metric'; preserve it in raw telemetry until it is registered");
  assert.match(fsharpUnknownResult.stderr, /unknown normalized metric 'bogus\.metric'; preserve it in raw telemetry until it is registered/);

  let nodeValueMessage;
  try {
    recordTelemetryMetric(nodeRoot, "EXE-1", cliDefaultedInput({ id: "tokens.input", value: Number("not-a-number") }));
  } catch (error) {
    nodeValueMessage = error.message;
  }
  const fsharpValueResult = runFsharp(fsharpRoot, ["EXE-1", "--metric", "tokens.input", "--value", "not-a-number"]);

  assert.equal(fsharpValueResult.status, 1);
  assert.equal(nodeValueMessage, "metric 'tokens.input' requires a finite numeric value");
  assert.match(fsharpValueResult.stderr, /metric 'tokens\.input' requires a finite numeric value/);
});

test("F# telemetry record deduplicates an identical repeated call by its content-addressed measurementId", (t) => {
  const nodeRoot = fixture(t, "dedup-node");
  const fsharpRoot = fixture(t, "dedup-fsharp");

  for (const root of [nodeRoot, fsharpRoot]) {
    writeFixtureExecution(root, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  }

  const input = cliDefaultedInput({ id: "tokens.input", value: 1, collectedAt: "2026-01-01T00:00:00.000Z" });
  recordTelemetryMetric(nodeRoot, "EXE-1", input);
  const nodeSecond = recordTelemetryMetric(nodeRoot, "EXE-1", input);
  runFsharp(fsharpRoot, ["EXE-1", "--metric", "tokens.input", "--value", "1", "--collected-at", "2026-01-01T00:00:00.000Z"]);
  runFsharp(fsharpRoot, ["EXE-1", "--metric", "tokens.input", "--value", "1", "--collected-at", "2026-01-01T00:00:00.000Z"]);

  const fsharpSecond = readExecution(fsharpRoot, "EXE-1");
  assert.equal(nodeSecond.metrics.length, 1);
  assert.equal(fsharpSecond.metrics.length, 1);
  assert.equal(nodeSecond.metrics[0].measurementId, fsharpSecond.metrics[0].measurementId);
});

test("F# telemetry record upserts a real registry-seeded capability from unobserved into supported-observed, matching production's real effect", (t) => {
  const nodeRoot = fixture(t, "capability-node");
  const fsharpRoot = fixture(t, "capability-fsharp");

  startWork(nodeRoot, ["WI-A"], { type: "task" });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);

  recordTelemetryMetric(
    nodeRoot,
    "WI-A",
    cliDefaultedInput({ id: "tokens.input", value: 10, collectedAt: "2026-01-01T00:00:00.000Z" })
  );
  const fsharpResult = runFsharp(fsharpRoot, ["WI-A", "--metric", "tokens.input", "--value", "10", "--collected-at", "2026-01-01T00:00:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const nodeExecution = readExecutions(nodeRoot)[0];
  const fsharpExecution = readExecutions(fsharpRoot)[0];
  const nodeCapability = nodeExecution.capabilities.find((c) => c.metricId === "tokens.input");
  const fsharpCapability = fsharpExecution.capabilities.find((c) => c.metricId === "tokens.input");

  assert.equal(nodeCapability.status, "supported-observed");
  assert.equal(fsharpCapability.status, "supported-observed");
  assert.equal(nodeCapability.reason, "normalized measurement recorded");
  assert.equal(fsharpCapability.reason, "normalized measurement recorded");
  assert.equal(nodeCapability.history.length, 1);
  assert.equal(fsharpCapability.history.length, 1);
  assert.equal(nodeCapability.history[0].status, fsharpCapability.history[0].status);
});

test("F# telemetry record honors explicit --unit/--currency overrides and both numeric and text --confidence, matching production", (t) => {
  const nodeRoot = fixture(t, "overrides-node");
  const fsharpRoot = fixture(t, "overrides-fsharp");

  for (const root of [nodeRoot, fsharpRoot]) {
    writeFixtureExecution(root, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  }

  const nodeRecord = recordTelemetryMetric(
    nodeRoot,
    "EXE-1",
    cliDefaultedInput({
      id: "cost.input",
      value: 0.05,
      unit: "usd-cents",
      currency: "USD",
      quality: "estimated",
      confidence: 0.9,
      collectedAt: "2026-01-01T00:00:00.000Z"
    })
  );
  const fsharpResult = runFsharp(fsharpRoot, [
    "EXE-1", "--metric", "cost.input", "--value", "0.05", "--unit", "usd-cents", "--currency", "USD",
    "--quality", "estimated", "--confidence", "0.9", "--collected-at", "2026-01-01T00:00:00.000Z"
  ]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpMetric = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(lastMetricOf(nodeRecord), fsharpMetric);

  const nodeTextRecord = recordTelemetryMetric(
    nodeRoot,
    "EXE-1",
    cliDefaultedInput({ id: "tokens.output", value: 1, confidence: "high", collectedAt: "2026-01-01T00:00:00.000Z" })
  );
  const fsharpTextResult = runFsharp(fsharpRoot, [
    "EXE-1", "--metric", "tokens.output", "--value", "1", "--confidence", "high", "--collected-at", "2026-01-01T00:00:00.000Z"
  ]);
  assert.equal(fsharpTextResult.status, 0, fsharpTextResult.stderr);

  assert.deepEqual(lastMetricOf(nodeTextRecord), JSON.parse(fsharpTextResult.stdout));
});
