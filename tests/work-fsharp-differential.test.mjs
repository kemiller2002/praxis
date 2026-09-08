import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { transition } from "../tools/ros_cli.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");
const states = ["ready", "active", "blocked", "complete"];
const actions = ["begin", "block", "resume", "complete"];

function fixture(t, state, type = "mechanical") {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-work-differential-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Work Differential" });
  execFileSync("git", ["-C", root, "init", "-q"]);
  const configFile = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(configFile, "utf8"));
  config.telemetry.enabled = false;
  fs.writeFileSync(configFile, `${JSON.stringify(config, null, 2)}\n`);
  const contextFile = path.join(root, ".ros", "context", "current.json");
  fs.writeFileSync(contextFile, `${JSON.stringify({
    schemaVersion: "1.0.0", repository: config.repository.id,
    workItems: [{ id: "TASK-MATRIX", type, state, semanticState: state, evidence: [] }]
  }, null, 2)}\n`);
  return root;
}

function nodeDecision(t, state, action, options = {}) {
  const root = fixture(t, state, options.type);
  try {
    const result = transition(root, action, ["TASK-MATRIX"], {
      reason: options.reason ?? "reason",
      evidence: options.evidence ?? []
    });
    return { allowed: true, targetState: result.context.workItems[0].semanticState };
  } catch (error) {
    return { allowed: false, message: error.message };
  }
}

function fsharpDecision(state, action, options = {}) {
  const args = [fsharpCli, "work", "decide", "--state", state, "--action", action];
  if (options.reason !== undefined) args.push("--reason", options.reason);
  for (const kind of options.required ?? []) args.push("--required", kind);
  for (const kind of options.provided ?? []) args.push("--provided", kind);
  const result = spawnSync("dotnet", args, { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, json: JSON.parse(result.stdout), stderr: result.stderr };
}

function fsharpPlan(item, event, state, action) {
  const args = [
    fsharpCli, "work", "plan",
    "--id", item.id,
    "--type", item.type,
    "--state", state,
    "--action", action,
    "--occurred-at", event.occurredAt,
    "--repository", event.repository,
    "--protocol-version", event.protocolVersion
  ];
  if (event.reason !== undefined) args.push("--reason", event.reason);
  for (const evidence of event.evidence ?? []) args.push("--evidence", `${evidence.type}=${evidence.path}`);
  for (const changedPath of event.paths ?? []) args.push("--path", changedPath);
  const result = spawnSync("dotnet", args, { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, json: JSON.parse(result.stdout), stderr: result.stderr };
}

function normalizedItem(item) {
  return {
    id: item.id,
    type: item.type,
    state: item.state,
    semanticState: item.semanticState,
    evidence: item.evidence,
    blockReason: item.blockReason ?? null,
    updatedAt: item.updatedAt ?? null,
    completedAt: item.completedAt ?? null,
    telemetryExecutionIds: item.telemetryExecutionIds ?? []
  };
}

function normalizedEvent(event) {
  return {
    type: event.type,
    workItem: event.workItem,
    repository: event.repository,
    protocolVersion: event.protocolVersion,
    occurredAt: event.occurredAt,
    reason: event.reason ?? null,
    evidence: event.evidence ?? [],
    paths: event.paths ?? [],
    telemetryExecutions: event.telemetryExecutions ?? []
  };
}

test("F# live-work decision matrix matches the Node transition guard", (t) => {
  for (const state of states) {
    for (const action of actions) {
      const node = nodeDecision(t, state, action);
      const fsharp = fsharpDecision(state, action, { reason: "reason" });
      assert.equal(fsharp.status === 0, node.allowed, `${state}/${action}: ${node.message ?? fsharp.stderr}`);
      if (node.allowed) assert.equal(fsharp.json.targetState, node.targetState, `${state}/${action}`);
    }
  }
});

test("F# completion evidence-type guard matches Node missing evidence", (t) => {
  const node = nodeDecision(t, "active", "complete", {
    type: "feature", evidence: [{ type: "implementation", path: "ros.json" }]
  });
  const fsharp = fsharpDecision("active", "complete", {
    required: ["implementation", "tests"], provided: ["implementation"]
  });
  assert.equal(node.allowed, false);
  assert.match(node.message, /completion evidence missing.*tests/);
  assert.equal(fsharp.status, 1);
  assert.deepEqual(fsharp.json.rejection, { reason: "missing-evidence", missingEvidence: ["tests"] });
});

test("F# block-reason guard preserves Node empty versus whitespace behavior", (t) => {
  for (const reason of ["", "  "]) {
    const node = nodeDecision(t, "active", "block", { reason });
    const fsharp = fsharpDecision("active", "block", { reason });
    assert.equal(fsharp.status === 0, node.allowed, JSON.stringify({ reason, node, fsharp }));
  }
});

test("F# work plans match production item and event projections for every legal edge", (t) => {
  const cases = [
    ["ready", "begin"],
    ["ready", "block"],
    ["active", "block"],
    ["blocked", "resume"],
    ["active", "complete"]
  ];
  for (const [state, action] of cases) {
    const root = fixture(t, state);
    const result = transition(root, action, ["TASK-MATRIX"], { reason: action === "block" ? "reason" : undefined, evidence: [] });
    const item = result.context.workItems.find((entry) => entry.id === "TASK-MATRIX");
    const event = result.events[0];
    const fsharp = fsharpPlan(item, event, state, action);
    assert.equal(fsharp.status, 0, `${state}/${action}: ${fsharp.stderr}`);
    assert.deepEqual(fsharp.json.plan.item, normalizedItem(item), `${state}/${action} item`);
    assert.deepEqual(fsharp.json.plan.event, normalizedEvent(event), `${state}/${action} event`);
    assert.deepEqual(fsharp.json.plan.telemetry, []);
  }
});

test("F# work plan rejection retains missing-evidence obligations", () => {
  const result = spawnSync("dotnet", [
    fsharpCli, "work", "plan", "--id", "TASK-PLAN", "--type", "feature",
    "--state", "active", "--action", "complete", "--occurred-at", "2026-09-08T18:30:00Z",
    "--required", "tests"
  ], { cwd: repositoryRoot, encoding: "utf8" });
  assert.equal(result.status, 1, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), {
    schemaVersion: "1.0.0",
    outcome: "rejected",
    plan: null,
    rejection: { reason: "missing-evidence", missingEvidence: ["tests"] }
  });
});

test("F# evidence verification matches production file directory and absolute-path existence", (t) => {
  const outside = path.join(os.tmpdir(), `ros-work-evidence-${process.pid}.txt`);
  fs.writeFileSync(outside, "outside fixture\n");
  t.after(() => fs.rmSync(outside, { force: true }));
  for (const evidencePath of ["ros.json", ".", outside, "missing-evidence.txt"]) {
    const root = fixture(t, "active", "feature");
    const evidence = [
      { type: "implementation", path: evidencePath },
      { type: "tests", path: evidencePath }
    ];
    let nodeAllowed = true;
    try {
      transition(root, "complete", ["TASK-MATRIX"], { evidence });
    } catch {
      nodeAllowed = false;
    }
    const args = [
      fsharpCli, "--root", root, "work", "plan", "--verify-evidence",
      "--id", "TASK-MATRIX", "--type", "feature", "--state", "active", "--action", "complete",
      "--occurred-at", "2026-09-08T18:30:00Z", "--required", "implementation", "--required", "tests",
      "--evidence", `implementation=${evidencePath}`, "--evidence", `tests=${evidencePath}`
    ];
    const fsharp = spawnSync("dotnet", args, { cwd: repositoryRoot, encoding: "utf8" });
    assert.equal(fsharp.status === 0, nodeAllowed, evidencePath);
    const payload = JSON.parse(fsharp.stdout);
    if (nodeAllowed) assert.equal(payload.outcome, "planned", evidencePath);
    else {
      assert.equal(payload.outcome, "evidence-rejected", evidencePath);
      assert.deepEqual(payload.rejection.evidenceIssues.map((issue) => issue.outcome), ["missing", "missing"]);
    }
  }
});
