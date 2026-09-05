import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";
import test from "node:test";

import { initializeProject } from "../lib/bootstrap.mjs";

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
  assert.equal(metric(record, "tests.added")[0].value, 1);
  assert.equal(metric(record, "git.files_deleted")[0].value, 0, "a legitimate zero is retained");
  assert.equal(metric(record, "time.wall_ms")[0].quality, "derived");
  assert.equal(ros(root, ["validate"]).status, 0);
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
  writeJson(path.join(root, "partial.json"), { type: "turn.completed", usage: { input_tokens: 4, output_tokens: 2 } });
  ros(root, ["telemetry", "ingest", "TASK-PARTIAL", "--adapter", "openai-codex", "--input", "partial.json"]);
  const record = executions(root, "TASK-PARTIAL")[0];
  assert.equal(record.capabilities.find((item) => item.metricId === "tokens.cache_write").status, "supported-unavailable");
  assert.equal(record.capabilities.find((item) => item.metricId === "tokens.reasoning").status, "supported-unavailable");
  assert.equal(metric(record, "tokens.cache_write").length, 0);
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

test("multiple providers, subagents, and repeated session totals aggregate without double counting", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-MULTI", "--provider", "openai", "--runtime", "codex", "--session", "openai-session"]);
  const first = executions(root, "TASK-MULTI")[0];
  ros(root, ["telemetry", "record", first.executionId, "--metric", "cost.session_cumulative", "--value", "7", "--currency", "USD", "--quality", "estimated", "--confidence", "0.8", "--scope", "session", "--source-type", "runtime-output", "--source-name", "codex", "--mechanism", "session-total", "--collected-at", "2026-09-05T01:00:00Z"]);

  const second = JSON.parse(ros(root, ["telemetry", "start", "TASK-MULTI", "--provider", "anthropic", "--runtime", "claude-code", "--session", "claude-session", "--subagent", "reviewer", "--parent-execution", first.executionId, "--classification", "development"]).output);
  ros(root, ["telemetry", "record", second.executionId, "--metric", "cost.session_cumulative", "--value", "10", "--currency", "USD", "--quality", "estimated", "--confidence", "0.8", "--scope", "session", "--source-type", "runtime-output", "--source-name", "claude", "--mechanism", "session-total", "--collected-at", "2026-09-05T01:30:00Z"]);
  ros(root, ["telemetry", "record", second.executionId, "--metric", "cost.session_cumulative", "--value", "12", "--currency", "USD", "--quality", "estimated", "--confidence", "0.8", "--scope", "session", "--source-type", "runtime-output", "--source-name", "claude", "--mechanism", "session-total-later", "--collected-at", "2026-09-05T02:00:00Z"]);

  const third = JSON.parse(ros(root, ["telemetry", "start", "TASK-MULTI", "--provider", "anthropic", "--runtime", "claude-code", "--session", "claude-session", "--parent-execution", second.executionId, "--classification", "testing-verification"]).output);
  ros(root, ["telemetry", "record", third.executionId, "--metric", "cost.session_cumulative", "--value", "15", "--currency", "USD", "--quality", "estimated", "--confidence", "0.8", "--scope", "session", "--source-type", "runtime-output", "--source-name", "claude", "--mechanism", "same-session-new-execution", "--collected-at", "2026-09-05T03:00:00Z"]);
  const summary = JSON.parse(ros(root, ["telemetry", "summary", "TASK-MULTI"]).output);
  const cost = summary.metrics.find((item) => item.id === "cost.session_cumulative");
  assert.equal(cost.value, 22, "7 from OpenAI plus only the latest 15 from the shared Claude session");
  assert.equal(summary.executionCount, 3);
  assert.deepEqual(summary.providers, ["anthropic", "openai"]);
  assert.equal(executions(root, "TASK-MULTI")[1].identity.subagentId, "reviewer");
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
