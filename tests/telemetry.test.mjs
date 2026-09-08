import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawn } from "node:child_process";
import test from "node:test";

import { initializeProject } from "../lib/bootstrap.mjs";
import { withFileLock } from "../tools/ros_persistence.mjs";
import { startExecution } from "../tools/ros_telemetry.mjs";

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-telemetry-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Telemetry Consumer" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function ros(root, args, options = {}) {
  try {
    return { status: 0, output: execFileSync(path.join(root, "ros"), args, { cwd: root, encoding: "utf8", ...options }) };
  } catch (error) {
    return { status: error.status, output: `${error.stdout ?? ""}${error.stderr ?? ""}` };
  }
}

function rosAsync(root, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(path.join(root, "ros"), args, { cwd: root, stdio: ["ignore", "pipe", "pipe"] });
    let output = "";
    child.stdout.on("data", (chunk) => { output += chunk; });
    child.stderr.on("data", (chunk) => { output += chunk; });
    child.on("error", reject);
    child.on("close", (status) => status === 0 ? resolve(output) : reject(new Error(output)));
  });
}

function executionFiles(root) {
  const directory = path.join(root, ".ros", "telemetry", "executions");
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory).filter((name) => name.endsWith(".json")).sort();
}

function writeJson(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

function executions(root, workItemId) {
  const directory = path.join(root, ".ros", "telemetry", "executions");
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory).filter((name) => name.endsWith(".json")).map((name) => JSON.parse(fs.readFileSync(path.join(directory, name), "utf8"))).filter((record) => !workItemId || record.workItemId === workItemId).sort((a, b) => a.startedAt.localeCompare(b.startedAt));
}

function metric(record, id) {
  return record.metrics.filter((item) => item.id === id);
}

test("persistence locks recover abandoned files and release after operation failure", (t) => {
  const root = fixture(t);
  const lockDirectory = path.join(root, ".ros", "locks");
  fs.mkdirSync(lockDirectory, { recursive: true });
  const abandonedName = crypto.createHash("sha256").update("abandoned").digest("hex");
  const abandoned = path.join(lockDirectory, `${abandonedName}.lock`);
  fs.writeFileSync(abandoned, "incomplete owner metadata", "utf8");
  const staleTime = new Date(Date.now() - 61_000);
  fs.utimesSync(abandoned, staleTime, staleTime);

  assert.equal(withFileLock(root, "abandoned", () => "recovered"), "recovered");
  assert.throws(() => withFileLock(root, "throwing", () => { throw new Error("operation failed"); }), /operation failed/);
  assert.equal(withFileLock(root, "throwing", () => "reacquired"), "reacquired");
});

test("work lifecycle automatically starts, Git-derives, and finalizes telemetry", (t) => {
  const root = fixture(t);
  const begun = ros(root, ["work", "begin", "FEAT-TELEMETRY", "--type", "feature", "--provider", "local", "--runtime", "test-agent", "--session", "s-1"]);
  assert.equal(begun.status, 0, begun.output);
  const active = executions(root, "FEAT-TELEMETRY")[0];
  assert.equal(active.status, "active");
  assert.equal(active.identity.provider, "local");
  assert.deepEqual(active.classification.types, ["development"]);

  fs.mkdirSync(path.join(root, "src"));
  fs.mkdirSync(path.join(root, "tests"));
  fs.writeFileSync(path.join(root, "src", "feature.js"), "export const enabled = true;\n");
  fs.writeFileSync(path.join(root, "tests", "feature.test.js"), "// deterministic fixture\n");
  const completed = ros(root, ["work", "complete", "FEAT-TELEMETRY", "--evidence", "implementation=src/feature.js", "--evidence", "tests=tests/feature.test.js"]);
  assert.equal(completed.status, 0, completed.output);

  const record = executions(root, "FEAT-TELEMETRY")[0];
  assert.equal(record.status, "finalized");
  assert.equal(metric(record, "git.files_added")[0].value, 2);
  assert.equal(metric(record, "git.files_modified")[0].value, 0, "ROS lifecycle files are excluded from development deltas");
  assert.equal(metric(record, "tests.added")[0].value, 1);
  assert.equal(metric(record, "git.files_deleted")[0].value, 0, "a legitimate zero is retained");
  assert.equal(metric(record, "time.wall_ms")[0].quality, "derived");
  assert.equal(ros(root, ["validate"]).status, 0);
});

test("telemetry records a Git rename once with origin provenance", (t) => {
  const root = fixture(t);
  fs.writeFileSync(path.join(root, "before.txt"), "rename me\n");
  execFileSync("git", ["add", "before.txt"], { cwd: root });
  execFileSync("git", ["commit", "-qm", "rename fixture"], { cwd: root });
  assert.equal(ros(root, ["work", "begin", "TASK-GIT-TELEMETRY", "--type", "mechanical"]).status, 0);
  execFileSync("git", ["mv", "before.txt", "after.txt"], { cwd: root });
  const completed = ros(root, ["work", "complete", "TASK-GIT-TELEMETRY"]);
  assert.equal(completed.status, 0, completed.output);
  const record = executions(root, "TASK-GIT-TELEMETRY")[0];
  assert.equal(metric(record, "git.files_renamed")[0].value, 1);
  assert.deepEqual(record.repository.changeSummary.paths.find((entry) => entry.status === "R"), {
    status: "R",
    path: "after.txt",
    from: "before.txt",
    lineStats: null
  });
});

test("telemetry keeps unavailable ending Git state distinct from zero", (t) => {
  const root = fixture(t);
  assert.equal(ros(root, ["work", "begin", "TASK-GIT-END-UNAVAILABLE", "--type", "mechanical"]).status, 0);
  const execution = executions(root, "TASK-GIT-END-UNAVAILABLE")[0];
  const isolatedBin = path.join(root, "isolated-bin");
  fs.mkdirSync(isolatedBin);
  fs.symlinkSync(process.execPath, path.join(isolatedBin, "node"));
  const finalized = ros(root, ["telemetry", "finalize", execution.executionId], {
    env: { ...process.env, PATH: isolatedBin }
  });
  assert.equal(finalized.status, 0, finalized.output);
  const record = JSON.parse(finalized.output);
  assert.equal(record.repository.end.available, false);
  assert.deepEqual(metric(record, "git.ending_dirty_files"), []);
  assert.equal(record.capabilities.find((entry) => entry.metricId === "git.ending_dirty_files").status, "supported-unavailable");
  assert.equal(record.repository.changeSummary.available, false);
  assert.equal(record.repository.changeSummary.reason, "git-unavailable");
});

test("telemetry start reattaches one detached active execution instead of duplicating it", (t) => {
  const root = fixture(t);
  assert.equal(ros(root, ["work", "begin", "TASK-LINK-RETRY"]).status, 0);
  const original = executions(root, "TASK-LINK-RETRY")[0];
  const contextFile = path.join(root, ".ros", "context", "current.json");
  const context = JSON.parse(fs.readFileSync(contextFile, "utf8"));
  context.workItems.find((item) => item.id === "TASK-LINK-RETRY").telemetryExecutionIds = [];
  writeJson(contextFile, context);

  const conflicting = ros(root, ["telemetry", "start", "TASK-LINK-RETRY", "--execution-id", "EXE-new-link"]);
  assert.equal(conflicting.status, 1);
  assert.match(conflicting.output, /detached telemetry execution must be linked before creating 'EXE-new-link'/);
  assert.deepEqual(executions(root, "TASK-LINK-RETRY").map((record) => record.executionId), [original.executionId]);

  const retried = ros(root, ["telemetry", "start", "TASK-LINK-RETRY"]);
  assert.equal(retried.status, 0, retried.output);
  assert.equal(JSON.parse(retried.output).executionId, original.executionId);
  assert.deepEqual(executions(root, "TASK-LINK-RETRY").map((record) => record.executionId), [original.executionId]);
  const repaired = JSON.parse(fs.readFileSync(contextFile, "utf8"));
  assert.deepEqual(repaired.workItems.find((item) => item.id === "TASK-LINK-RETRY").telemetryExecutionIds, [original.executionId]);
});

test("work begin retry adopts an execution left before context/event commit", (t) => {
  const root = fixture(t);
  assert.equal(ros(root, ["work", "begin", "TASK-BEGIN-RETRY"]).status, 0);
  const original = executions(root, "TASK-BEGIN-RETRY")[0];
  const contextFile = path.join(root, ".ros", "context", "current.json");
  const context = JSON.parse(fs.readFileSync(contextFile, "utf8"));
  context.workItems = context.workItems.filter((item) => item.id !== "TASK-BEGIN-RETRY");
  writeJson(contextFile, context);

  const retried = ros(root, ["work", "begin", "TASK-BEGIN-RETRY"]);
  assert.equal(retried.status, 0, retried.output);
  assert.deepEqual(executions(root, "TASK-BEGIN-RETRY").map((record) => record.executionId), [original.executionId]);
  const repaired = JSON.parse(fs.readFileSync(contextFile, "utf8"));
  assert.deepEqual(repaired.workItems.find((item) => item.id === "TASK-BEGIN-RETRY").telemetryExecutionIds, [original.executionId]);
});

test("ambiguous detached telemetry evidence rejects automatic recovery", (t) => {
  const root = fixture(t);
  assert.equal(ros(root, ["work", "begin", "TASK-LINK-AMBIGUOUS"]).status, 0);
  assert.equal(ros(root, ["telemetry", "start", "TASK-LINK-AMBIGUOUS", "--execution-id", "EXE-second-link"]).status, 0);
  const contextFile = path.join(root, ".ros", "context", "current.json");
  const context = JSON.parse(fs.readFileSync(contextFile, "utf8"));
  context.workItems.find((item) => item.id === "TASK-LINK-AMBIGUOUS").telemetryExecutionIds = [];
  writeJson(contextFile, context);

  const retried = ros(root, ["telemetry", "start", "TASK-LINK-AMBIGUOUS"]);
  assert.equal(retried.status, 1);
  assert.match(retried.output, /multiple detached telemetry executions require explicit selection/);
  assert.equal(executions(root, "TASK-LINK-AMBIGUOUS").length, 2);
  assert.equal(fs.existsSync(path.join(root, ".ros", "transactions", "work-state.json")), false);

  const selected = ros(root, ["telemetry", "start", "TASK-LINK-AMBIGUOUS", "--execution-id", "EXE-second-link"]);
  assert.equal(selected.status, 0, selected.output);
  assert.equal(JSON.parse(selected.output).executionId, "EXE-second-link");
  const repaired = JSON.parse(fs.readFileSync(contextFile, "utf8"));
  assert.deepEqual(repaired.workItems.find((item) => item.id === "TASK-LINK-AMBIGUOUS").telemetryExecutionIds, ["EXE-second-link"]);
});

test("telemetry core rejects uncomposed context attachment before writing an execution", (t) => {
  const root = fixture(t);
  assert.throws(() => startExecution(root, "TASK-NOT-COMPOSED"), /must be composed under the work-protocol recovery boundary/);
  assert.equal(executionFiles(root).length, 0);
});

test("Codex adapter distinguishes zero, unavailable, and a future unknown field", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-CODEX", "--provider", "openai", "--runtime", "codex", "--session", "codex-session"]);
  const input = path.join(root, "codex.jsonl");
  fs.writeFileSync(input, `${JSON.stringify({
    type: "turn.completed",
    timestamp: "2026-09-05T01:00:00Z",
    usage: { input_tokens: 0, cached_input_tokens: 0, output_tokens: 7, quantum_cache_tokens: 19 },
    prompt: "must never persist",
    authorization: "Bearer secret"
  })}\n`);
  const ingested = ros(root, ["telemetry", "ingest", "TASK-CODEX", "--adapter", "openai-codex", "--input", input]);
  assert.equal(ingested.status, 0, ingested.output);
  const record = JSON.parse(ingested.output);
  assert.equal(metric(record, "tokens.input")[0].value, 0);
  assert.equal(record.capabilities.find((item) => item.metricId === "tokens.reasoning").status, "supported-unavailable");
  assert.equal(metric(record, "tokens.reasoning").length, 0, "unavailable is not represented as zero");
  assert.ok(record.rawTelemetry[0].discoveredFields.some((field) => field.includes("quantum_cache_tokens")));
  assert.ok(record.rawTelemetry[0].redactions.some((field) => field.endsWith(".prompt")));
  const serialized = JSON.stringify(record);
  assert.doesNotMatch(serialized, /must never persist|Bearer secret/);
  assert.match(serialized, /REDACTED_BY_ROS/);
});

test("generic adapter accepts every registered non-derived metric with provenance", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-FULL", "--provider", "future-ai", "--runtime", "future-cli", "--session", "future-session"]);
  const registry = JSON.parse(fs.readFileSync(path.join(root, "telemetry", "metrics.json"), "utf8"));
  const runtimeMetrics = registry.metrics.filter((item) => item.collection !== "ros-derived").map((definition) => ({
    id: definition.id,
    value: definition.unit === "ratio" ? 0.5 : 1,
    unit: definition.unit,
    ...(definition.unit === "currency" ? { currency: "USD" } : {}),
    quality: "observed",
    scope: definition.aggregation === "latest-per-session" ? "session" : "execution",
    source: { type: "runtime-api", name: "future-cli", mechanism: "usage-v2" },
    collectedAt: "2026-09-05T01:00:00Z"
  }));
  writeJson(path.join(root, "all.json"), {
    schemaVersion: "1.0.0",
    snapshotId: "all-known",
    identity: { provider: "future-ai", runtime: "future-cli", sessionId: "future-session" },
    metrics: runtimeMetrics,
    raw: {}
  });
  assert.equal(ros(root, ["telemetry", "ingest", "TASK-FULL", "--input", "all.json"]).status, 0);
  assert.equal(ros(root, ["validate"]).status, 0);
  const record = executions(root, "TASK-FULL")[0];
  assert.equal(runtimeMetrics.every((expected) => metric(record, expected.id).length === 1), true);
});

test("partial provider snapshots and changed provider capabilities remain explicit", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-PARTIAL", "--provider", "openai", "--runtime", "codex", "--session", "s-partial"]);
  writeJson(path.join(root, "first.json"), { type: "turn.completed", timestamp: "2026-09-05T01:00:00Z", usage: { input_tokens: 4, output_tokens: 2 } });
  ros(root, ["telemetry", "ingest", "TASK-PARTIAL", "--adapter", "openai-codex", "--input", "first.json"]);
  writeJson(path.join(root, "partial.json"), { type: "turn.completed", timestamp: "2026-09-05T02:00:00Z", usage: { output_tokens: 2 } });
  ros(root, ["telemetry", "ingest", "TASK-PARTIAL", "--adapter", "openai-codex", "--input", "partial.json"]);
  const record = executions(root, "TASK-PARTIAL")[0];
  const inputCapability = record.capabilities.find((item) => item.metricId === "tokens.input");
  assert.equal(inputCapability.status, "supported-unavailable", "the latest snapshot records that a previously observed field disappeared");
  assert.ok(inputCapability.history.some((item) => item.status === "supported-observed"), "the prior capability state remains auditable");
  assert.ok(inputCapability.history.some((item) => item.status === "supported-unavailable"), "the initial unavailable state remains auditable");
  assert.equal(inputCapability.lastAssessedAt, "2026-09-05T02:00:00Z");
  assert.equal(metric(record, "tokens.input")[0].value, 4, "the prior observation is preserved rather than rewritten");
  assert.equal(record.capabilities.find((item) => item.metricId === "tokens.cache_write").status, "supported-unavailable");
  assert.equal(record.capabilities.find((item) => item.metricId === "tokens.reasoning").status, "supported-unavailable");
  assert.equal(metric(record, "tokens.cache_write").length, 0);
  assert.equal(ros(root, ["validate"]).status, 0);
});

test("capability transition history remains bounded while preserving omission provenance", (t) => {
  const root = fixture(t);
  const configFile = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(configFile, "utf8"));
  config.telemetry.maxCapabilityHistoryEntries = 2;
  writeJson(configFile, config);
  ros(root, ["work", "begin", "TASK-CAP-HISTORY", "--provider", "future-ai", "--runtime", "future-cli"]);
  for (let index = 0; index < 6; index += 1) {
    writeJson(path.join(root, `capability-${index}.json`), {
      snapshotId: `capability-${index}`,
      capabilities: [{
        metricId: "tokens.input",
        status: index % 2 ? "supported-unavailable" : "supported-observed",
        reason: index % 2 ? "usage omitted" : "usage present",
        source: { type: "runtime-api", name: "future-cli", mechanism: "capability-probe" }
      }],
      raw: {}
    });
    assert.equal(ros(root, ["telemetry", "ingest", "TASK-CAP-HISTORY", "--input", `capability-${index}.json`]).status, 0);
  }
  const capability = executions(root, "TASK-CAP-HISTORY")[0].capabilities.find((item) => item.metricId === "tokens.input");
  assert.equal(capability.history.length, 2);
  assert.equal(capability.historyOmitted, 4);
  assert.equal(capability.status, "supported-unavailable");
  assert.equal(ros(root, ["validate"]).status, 0);
});

test("parallel execution starts and telemetry callbacks do not lose updates", async (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-CONCURRENT", "--provider", "local", "--runtime", "parallel-test"]);
  const executionIds = Array.from({ length: 8 }, (_, index) => `EXE-parallel-${index}`);
  await Promise.all(executionIds.map((id) => rosAsync(root, ["telemetry", "start", "TASK-CONCURRENT", "--execution-id", id, "--provider", "local", "--runtime", "parallel-test", "--quiet"])));

  const context = JSON.parse(fs.readFileSync(path.join(root, ".ros", "context", "current.json"), "utf8"));
  const linked = context.workItems.find((item) => item.id === "TASK-CONCURRENT").telemetryExecutionIds;
  assert.equal(new Set(linked).size, 9, "the lifecycle execution and all parallel executions remain linked");

  const target = executions(root, "TASK-CONCURRENT").find((record) => !executionIds.includes(record.executionId));
  const snapshots = Array.from({ length: 12 }, (_, index) => {
    const file = path.join(root, `parallel-${index}.json`);
    writeJson(file, {
      snapshotId: `parallel-snapshot-${index}`,
      collectedAt: `2026-09-05T03:00:${String(index).padStart(2, "0")}Z`,
      metrics: [{
        id: "tool.calls",
        value: 1,
        source: { type: "runtime-hook", name: "parallel-test", mechanism: "callback" }
      }],
      raw: { provider_usage: { callback_sequence: index } }
    });
    return file;
  });
  await Promise.all(snapshots.map((file) => rosAsync(root, ["telemetry", "ingest", target.executionId, "--input", file, "--quiet"])));

  const updated = executions(root, "TASK-CONCURRENT").find((record) => record.executionId === target.executionId);
  assert.equal(updated.rawTelemetry.length, 12);
  assert.equal(metric(updated, "tool.calls").length, 12);
  assert.equal(updated.events.filter((event) => event.type === "telemetry.snapshot.ingested").length, 12);
  assert.equal(ros(root, ["validate"]).status, 0);
});

test("a final provider snapshot survives competing finalization", async (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-FINAL-RACE", "--provider", "future-ai", "--runtime", "future-cli"]);
  const execution = executions(root, "TASK-FINAL-RACE")[0];
  writeJson(path.join(root, "final-snapshot.json"), {
    snapshotId: "provider-final",
    metrics: [{
      id: "tokens.output", value: 11,
      source: { type: "runtime-api", name: "future-cli", mechanism: "final-usage" }
    }],
    raw: { usage: { output_tokens: 11 } }
  });
  await Promise.all([
    rosAsync(root, ["telemetry", "finalize", execution.executionId, "--quiet"]),
    rosAsync(root, ["telemetry", "finalize", execution.executionId, "--input", "final-snapshot.json", "--quiet"])
  ]);
  const record = executions(root, "TASK-FINAL-RACE")[0];
  assert.equal(record.status, "finalized");
  assert.equal(metric(record, "tokens.output")[0].value, 11);
  assert.ok(record.events.some((event) => event.snapshotId === "provider-final"));
  assert.equal(ros(root, ["validate"]).status, 0);
});

test("one snapshot cannot report a metric while declaring that metric unavailable", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-CONFLICT", "--provider", "future-ai", "--runtime", "future-cli"]);
  writeJson(path.join(root, "conflict.json"), {
    snapshotId: "contradictory-snapshot",
    capabilities: [{
      metricId: "tokens.input",
      status: "supported-unavailable",
      source: { type: "runtime-api", name: "future-cli", mechanism: "usage-response" }
    }],
    metrics: [{
      id: "tokens.input",
      value: 0,
      source: { type: "runtime-api", name: "future-cli", mechanism: "usage-response" }
    }]
  });
  const result = ros(root, ["telemetry", "ingest", "TASK-CONFLICT", "--input", "conflict.json"]);
  assert.equal(result.status, 1);
  assert.match(result.output, /declaring it unavailable or unsupported/);
  assert.equal(metric(executions(root, "TASK-CONFLICT")[0], "tokens.input").length, 0);
});

test("new nested Claude fields survive discovery without hiding known siblings", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-CLAUDE-FUTURE", "--provider", "anthropic", "--runtime", "claude-code", "--session", "claude-future"]);
  writeJson(path.join(root, "claude.json"), {
    session_id: "claude-future",
    model: { id: "claude-future-model" },
    context_window: { context_window_size: 200000, used_percentage: 5, future_counter: 17 },
    cost: { total_cost_usd: 0.25 }
  });
  const result = ros(root, ["telemetry", "ingest", "TASK-CLAUDE-FUTURE", "--adapter", "anthropic-claude-statusline", "--input", "claude.json"]);
  assert.equal(result.status, 0, result.output);
  const record = JSON.parse(result.output);
  assert.ok(record.rawTelemetry[0].discoveredFields.some((field) => field.endsWith("context_window.future_counter")));
  assert.equal(metric(record, "cost.session_cumulative")[0].confidence, "medium");
  assert.equal(ros(root, ["validate"]).status, 0);
});

test("estimated and derived metrics remain distinguishable and invalid provenance is rejected", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-QUALITY", "--provider", "anthropic", "--runtime", "claude-code", "--session", "claude-session"]);
  const estimated = ros(root, ["telemetry", "record", "TASK-QUALITY", "--metric", "cost.session_cumulative", "--value", "1.25", "--currency", "USD", "--quality", "estimated", "--confidence", "0.8", "--scope", "session", "--source-type", "runtime-output", "--source-name", "claude-statusline", "--mechanism", "client-estimate"]);
  assert.equal(estimated.status, 0, estimated.output);
  assert.equal(ros(root, ["validate"]).status, 0);

  const record = executions(root, "TASK-QUALITY")[0];
  const file = path.join(root, ".ros", "telemetry", "executions", `${record.executionId}.json`);
  record.metrics.find((item) => item.id === "git.baseline_dirty_files").quality = "observed";
  writeJson(file, record);
  const validation = ros(root, ["validate", "--json"]);
  assert.equal(validation.status, 1);
  assert.match(validation.output, /ROS-derived metric cannot be represented as observed/);
});

test("calculated costs require versioned pricing provenance", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-PRICING", "--provider", "local", "--runtime", "cost-calculator", "--session", "pricing-session"]);
  const recorded = ros(root, ["telemetry", "record", "TASK-PRICING", "--metric", "cost.session_cumulative", "--value", "2.5", "--currency", "USD", "--quality", "estimated", "--confidence", "low", "--scope", "session", "--source-type", "calculated", "--source-name", "cost-calculator", "--mechanism", "token-rate-table", "--pricing-source", "vendor-public-pricing", "--pricing-version", "2026-09-05"]);
  assert.equal(recorded.status, 0, recorded.output);
  assert.deepEqual(JSON.parse(recorded.output).pricing, { source: "vendor-public-pricing", version: "2026-09-05" });
  assert.equal(ros(root, ["validate"]).status, 0);

  const record = executions(root, "TASK-PRICING")[0];
  const file = path.join(root, ".ros", "telemetry", "executions", `${record.executionId}.json`);
  record.metrics.find((item) => item.id === "cost.session_cumulative").pricing = null;
  writeJson(file, record);
  const invalid = ros(root, ["validate", "--json"]);
  assert.equal(invalid.status, 1);
  assert.match(invalid.output, /calculated cost requires pricing source and version/);
});

test("validation catches sensitive raw fields introduced outside ingestion", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-RAW-TAMPER"]);
  const record = executions(root, "TASK-RAW-TAMPER")[0];
  const file = path.join(root, ".ros", "telemetry", "executions", `${record.executionId}.json`);
  record.rawTelemetry.push({
    snapshotId: "manual-tamper",
    adapter: "generic",
    schemaVersion: "1.0.0",
    collectedAt: "2026-09-05T01:00:00Z",
    source: { type: "runtime-output", name: "manual", mechanism: "direct-file-edit" },
    payload: { nested_api_key_value: "must-not-remain" },
    discoveredFields: ["$.nested_api_key_value"],
    redactions: []
  });
  record.capabilities.push({ ...record.capabilities[0] });
  record.metrics.push({ ...record.metrics[0] });
  record.metrics.push(null);
  writeJson(file, record);
  fs.writeFileSync(path.join(root, ".ros", "telemetry", "executions", "EXE-null.json"), "null\n");
  const invalid = ros(root, ["validate", "--json"]);
  assert.equal(invalid.status, 1);
  assert.match(invalid.output, /sensitive raw field is not redacted/);
  assert.match(invalid.output, /duplicate capability identity/);
  assert.match(invalid.output, /duplicate measurement ID/);
  assert.match(invalid.output, /metric must be an object/);
  assert.match(invalid.output, /execution telemetry record must be an object/);
});

test("raw-disabled ingestion remains idempotent and still surfaces unknown capability names", (t) => {
  const root = fixture(t);
  const configFile = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(configFile, "utf8"));
  config.telemetry.allowRawTelemetry = false;
  writeJson(configFile, config);
  ros(root, ["work", "begin", "TASK-NO-RAW", "--provider", "future-ai", "--runtime", "future-cli"]);
  writeJson(path.join(root, "no-raw.json"), {
    snapshotId: "no-raw-snapshot",
    metrics: [{
      id: "tokens.input", value: 3,
      source: { type: "runtime-api", name: "future-cli", mechanism: "usage-response" }
    }],
    raw: { usage: { future_counter: 9 } }
  });
  assert.equal(ros(root, ["telemetry", "ingest", "TASK-NO-RAW", "--input", "no-raw.json"]).status, 0);
  assert.equal(ros(root, ["telemetry", "ingest", "TASK-NO-RAW", "--input", "no-raw.json"]).status, 0);
  const record = executions(root, "TASK-NO-RAW")[0];
  assert.equal(metric(record, "tokens.input").length, 1);
  assert.equal(metric(record, "telemetry.unknown_fields")[0].value, 1);
  assert.equal(record.rawTelemetry.length, 0);
  assert.equal(record.events.filter((event) => event.type === "telemetry.snapshot.ingested").length, 1);
  assert.ok(record.capabilities.some((item) => item.providerField?.endsWith("usage.future_counter")));
});

test("per-execution raw budgets omit payloads without losing normalized telemetry or discovery", (t) => {
  const root = fixture(t);
  const configFile = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(configFile, "utf8"));
  config.telemetry.maxRawSnapshotsPerExecution = 1;
  config.telemetry.maxRawBytesPerExecution = 1024;
  writeJson(configFile, config);
  ros(root, ["work", "begin", "TASK-RAW-BUDGET", "--provider", "future-ai", "--runtime", "future-cli"]);
  for (let index = 0; index < 2; index += 1) {
    writeJson(path.join(root, `budget-${index}.json`), {
      snapshotId: `budget-${index}`,
      collectedAt: `2026-09-05T04:00:0${index}Z`,
      metrics: [{
        id: "tool.calls", value: 1,
        source: { type: "runtime-hook", name: "future-cli", mechanism: "callback" }
      }],
      raw: { usage: { [`future_counter_${index}`]: index } }
    });
    const result = ros(root, ["telemetry", "ingest", "TASK-RAW-BUDGET", "--input", `budget-${index}.json`]);
    assert.equal(result.status, 0, result.output);
  }
  const record = executions(root, "TASK-RAW-BUDGET")[0];
  assert.equal(record.rawTelemetry.length, 1);
  assert.equal(metric(record, "tool.calls").length, 2, "normalized measurements survive raw omission");
  assert.equal(metric(record, "telemetry.raw_snapshots_omitted")[0].value, 1);
  const omitted = record.events.find((event) => event.snapshotId === "budget-1");
  assert.equal(omitted.rawRetention.status, "omitted");
  assert.equal(omitted.rawRetention.reason, "execution-snapshot-limit");
  assert.equal(typeof omitted.rawRetention.payloadBytes, "number");
  assert.ok(record.capabilities.some((item) => item.providerField?.endsWith("future_counter_1")), "unmapped field names survive raw omission");

  config.telemetry.maxRawSnapshotsPerExecution = 10;
  writeJson(configFile, config);
  ros(root, ["work", "begin", "TASK-RAW-BYTES", "--provider", "future-ai", "--runtime", "future-cli"]);
  for (let index = 0; index < 2; index += 1) {
    writeJson(path.join(root, `bytes-${index}.json`), {
      snapshotId: `bytes-${index}`,
      raw: { future_blob: "x".repeat(700) }
    });
    assert.equal(ros(root, ["telemetry", "ingest", "TASK-RAW-BYTES", "--input", `bytes-${index}.json`]).status, 0);
  }
  const bytesRecord = executions(root, "TASK-RAW-BYTES")[0];
  assert.equal(bytesRecord.rawTelemetry.length, 1);
  assert.equal(bytesRecord.events.find((event) => event.snapshotId === "bytes-1").rawRetention.reason, "execution-byte-limit");
  assert.equal(ros(root, ["validate"]).status, 0);
});

test("invalid retention limits fail deterministically instead of changing runtime behavior", (t) => {
  const root = fixture(t);
  const configFile = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(configFile, "utf8"));
  config.telemetry.maxCapabilityHistoryEntries = 1;
  writeJson(configFile, config);
  const validation = ros(root, ["validate", "--json"]);
  assert.equal(validation.status, 1);
  assert.match(validation.output, /maxCapabilityHistoryEntries must be an integer greater than or equal to 2/);
  const begin = ros(root, ["work", "begin", "TASK-BAD-RETENTION"]);
  assert.equal(begin.status, 1);
  assert.match(begin.output, /maxCapabilityHistoryEntries/);
});

test("OpenTelemetry aliases do not double-count and Unix-nanosecond timestamps normalize", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-OTEL", "--provider", "github", "--runtime", "copilot", "--session", "otel-session"]);
  writeJson(path.join(root, "otel.json"), {
    name: "gen_ai.client.operation",
    timeUnixNano: "1788566400000000000",
    input_tokens: 5,
    attributes: {
      "gen_ai.usage.input_tokens": 5,
      "session.id": "otel-session",
      "gen_ai.response.model": "served-model"
    }
  });
  const ingested = ros(root, ["telemetry", "ingest", "TASK-OTEL", "--adapter", "github-copilot-otel", "--input", "otel.json"]);
  assert.equal(ingested.status, 0, ingested.output);
  const record = JSON.parse(ingested.output);
  assert.equal(metric(record, "tokens.input").length, 1);
  assert.equal(metric(record, "tokens.input")[0].value, 5);
  assert.match(metric(record, "tokens.input")[0].collectedAt, /^20\d\d-\d\d-\d\dT/);
  assert.equal(record.identity.model, "served-model");
  assert.equal(ros(root, ["validate"]).status, 0);
});

test("multiple providers, subagents, and repeated session totals aggregate without double counting", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-MULTI", "--provider", "openai", "--runtime", "codex", "--session", "shared-session"]);
  const first = executions(root, "TASK-MULTI")[0];
  ros(root, ["telemetry", "record", first.executionId, "--metric", "cost.session_cumulative", "--value", "7", "--currency", "USD", "--quality", "estimated", "--confidence", "0.8", "--scope", "session", "--source-type", "runtime-output", "--source-name", "codex", "--mechanism", "session-total", "--collected-at", "2026-09-05T01:00:00Z"]);
  ros(root, ["telemetry", "record", first.executionId, "--metric", "context.utilization", "--value", "0.3", "--scope", "session", "--source-type", "runtime-output", "--source-name", "codex", "--mechanism", "context-gauge", "--collected-at", "2026-09-05T01:00:00Z"]);
  fs.appendFileSync(path.join(root, "README.md"), "\nworking tree change\n");

  const second = JSON.parse(ros(root, ["telemetry", "start", "TASK-MULTI", "--provider", "anthropic", "--runtime", "claude-code", "--session", "shared-session", "--subagent", "reviewer", "--parent-execution", first.executionId, "--classification", "development"]).output);
  ros(root, ["telemetry", "record", second.executionId, "--metric", "cost.session_cumulative", "--value", "10", "--currency", "USD", "--quality", "estimated", "--confidence", "0.8", "--scope", "session", "--source-type", "runtime-output", "--source-name", "claude", "--mechanism", "session-total", "--collected-at", "2026-09-05T01:30:00Z"]);
  ros(root, ["telemetry", "record", second.executionId, "--metric", "cost.session_cumulative", "--value", "12", "--currency", "USD", "--quality", "estimated", "--confidence", "0.8", "--scope", "session", "--source-type", "runtime-output", "--source-name", "claude", "--mechanism", "session-total-later", "--collected-at", "2026-09-05T02:00:00Z"]);
  ros(root, ["telemetry", "record", second.executionId, "--metric", "context.utilization", "--value", "0.4", "--scope", "session", "--source-type", "runtime-output", "--source-name", "claude", "--mechanism", "context-gauge", "--collected-at", "2026-09-05T02:00:00Z"]);

  const third = JSON.parse(ros(root, ["telemetry", "start", "TASK-MULTI", "--provider", "anthropic", "--runtime", "claude-code", "--session", "shared-session", "--parent-execution", second.executionId, "--classification", "testing-verification"]).output);
  ros(root, ["telemetry", "record", third.executionId, "--metric", "cost.session_cumulative", "--value", "15", "--currency", "USD", "--quality", "estimated", "--confidence", "0.8", "--scope", "session", "--source-type", "runtime-output", "--source-name", "claude", "--mechanism", "same-session-new-execution", "--collected-at", "2026-09-05T03:00:00Z"]);
  const summary = JSON.parse(ros(root, ["telemetry", "summary", "TASK-MULTI"]).output);
  const cost = summary.metrics.find((item) => item.id === "cost.session_cumulative");
  const context = summary.metrics.find((item) => item.id === "context.utilization");
  const dirty = summary.metrics.find((item) => item.id === "git.baseline_dirty_files");
  assert.equal(cost.value, 22, "provider/runtime namespaces prevent the equal session IDs from colliding, while Claude snapshots still aggregate once");
  assert.equal(context.value, null, "context gauges do not manufacture a cross-session scalar");
  assert.equal(context.measurements, 2);
  assert.equal(dirty.value, 1, "dirty-state snapshots use a maximum rather than an additive total");
  assert.equal(summary.executionCount, 3);
  assert.deepEqual(summary.providers, ["anthropic", "openai"]);
  assert.equal(executions(root, "TASK-MULTI")[1].identity.subagentId, "reviewer");
});

test("work-item timing distinguishes calendar span from overlapping execution effort", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-TIMING", "--provider", "local", "--runtime", "test-agent"]);
  ros(root, ["telemetry", "start", "TASK-TIMING", "--execution-id", "EXE-timing-second", "--provider", "local", "--runtime", "test-agent"]);
  const records = executions(root, "TASK-TIMING");
  const spans = [
    ["2026-09-05T00:00:00Z", "2026-09-05T00:10:00Z"],
    ["2026-09-05T00:05:00Z", "2026-09-05T00:15:00Z"]
  ];
  records.forEach((record, index) => {
    record.startedAt = spans[index][0];
    record.finalizedAt = spans[index][1];
    record.status = "finalized";
    writeJson(path.join(root, ".ros", "telemetry", "executions", `${record.executionId}.json`), record);
  });
  const summary = JSON.parse(ros(root, ["telemetry", "summary", "TASK-TIMING"]).output);
  assert.deepEqual(summary.timing, {
    fullyFinalized: true,
    finalizedExecutionCount: 2,
    activeExecutionCount: 0,
    earliestStartedAt: "2026-09-05T00:00:00.000Z",
    latestFinalizedAt: "2026-09-05T00:15:00.000Z",
    calendarSpanMs: 900000,
    totalExecutionWallMs: 1200000,
    overlappingExecutionMs: 300000
  });
});

test("duplicate execution IDs are prevented and duplicate record content is detected", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-DUP"]);
  const id = "EXE-explicit-duplicate";
  assert.equal(ros(root, ["telemetry", "start", "TASK-DUP", "--execution-id", id]).status, 0);
  const duplicate = ros(root, ["telemetry", "start", "TASK-DUP", "--execution-id", id]);
  assert.equal(duplicate.status, 1);
  assert.match(duplicate.output, /duplicate execution ID/);

  const original = executions(root, "TASK-DUP").find((record) => record.executionId === id);
  writeJson(path.join(root, ".ros", "telemetry", "executions", "EXE-different-file.json"), original);
  const validation = ros(root, ["validate", "--json"]);
  assert.equal(validation.status, 1);
  assert.match(validation.output, /duplicate execution ID|filename must match/);
});

test("historical work without telemetry remains compatible while unknown telemetry schemas fail", (t) => {
  const root = fixture(t);
  assert.equal(ros(root, ["validate"]).status, 0, "bootstrap installation history predates execution telemetry and remains valid");
  ros(root, ["work", "begin", "TASK-VERSION"]);
  const record = executions(root, "TASK-VERSION")[0];
  const file = path.join(root, ".ros", "telemetry", "executions", `${record.executionId}.json`);
  record.schemaVersion = "2.0.0";
  writeJson(file, record);
  const validation = ros(root, ["validate", "--json"]);
  assert.equal(validation.status, 1);
  assert.match(validation.output, /unsupported telemetry schema version/);
});

test("Research, Development, combined R&D evidence, and extensible classifications validate", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-RD", "--classification", "research", "--classification", "development"]);
  writeJson(path.join(root, "rd.json"), {
    researchQuestion: "Can runtime-neutral adapters preserve unknown usage fields?",
    technicalUncertainty: "Provider payloads evolve independently of ROS.",
    alternativesEvaluated: ["strict provider schemas", "raw-only logs", "normalized plus raw"],
    uncertaintyResolved: true,
    resultingTechnicalKnowledge: "An additive raw boundary preserves future fields."
  });
  const classified = ros(root, ["telemetry", "classify", "TASK-RD", "--classification", "research-development", "--classification", "experiment", "--classification", "x-domain/measurement-study", "--rd-context", "rd.json", "--evidence-link", "EV-ROS-2026-A011"]);
  assert.equal(classified.status, 0, classified.output);
  assert.equal(ros(root, ["validate"]).status, 0);
  const record = executions(root, "TASK-RD")[0];
  assert.equal(record.classification.rd.uncertaintyResolved, true);

  record.classification.types.push("made-up-invalid");
  writeJson(path.join(root, ".ros", "telemetry", "executions", `${record.executionId}.json`), record);
  const invalid = ros(root, ["validate", "--json"]);
  assert.equal(invalid.status, 1);
  assert.match(invalid.output, /invalid work classification/);
});

test("completed work with a linked but unfinalized execution is rejected", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-FINAL"]);
  fs.writeFileSync(path.join(root, "implementation.txt"), "done\n");
  fs.writeFileSync(path.join(root, "tests.txt"), "passed\n");
  assert.equal(ros(root, ["work", "complete", "TASK-FINAL", "--evidence", "implementation=implementation.txt", "--evidence", "tests=tests.txt"]).status, 0);
  const record = executions(root, "TASK-FINAL")[0];
  const file = path.join(root, ".ros", "telemetry", "executions", `${record.executionId}.json`);
  record.status = "active";
  record.finalizedAt = null;
  writeJson(file, record);
  const validation = ros(root, ["validate", "--json"]);
  assert.equal(validation.status, 1);
  assert.match(validation.output, /unfinalized telemetry/);
});

test("validation rejects an execution detached from its work-item backlink", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-BACKLINK"]);
  const contextFile = path.join(root, ".ros", "context", "current.json");
  const context = JSON.parse(fs.readFileSync(contextFile, "utf8"));
  context.workItems.find((item) => item.id === "TASK-BACKLINK").telemetryExecutionIds = [];
  writeJson(contextFile, context);
  const validation = ros(root, ["validate", "--json"]);
  assert.equal(validation.status, 1);
  assert.match(validation.output, /is not linked back from work item/);
});
