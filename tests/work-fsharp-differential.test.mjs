import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { backlogTransition, captureWork, startWork, transition } from "../tools/ros_cli.mjs";
import { observeGitStatus } from "../tools/ros_git.mjs";

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

function contextFixture(t, workItems) {
  const root = fixture(t, "ready");
  const contextFile = path.join(root, ".ros", "context", "current.json");
  fs.writeFileSync(contextFile, `${JSON.stringify({
    schemaVersion: "1.0.0",
    repository: "work-differential",
    workItems
  }, null, 2)}\n`);
  return root;
}

function fsharpContextPlan(t, root, beforeContext, action, ids, production, options = {}) {
  const snapshot = path.join(os.tmpdir(), `ros-work-context-${process.pid}-${Math.random().toString(16).slice(2)}.json`);
  fs.writeFileSync(snapshot, beforeContext);
  t.after(() => fs.rmSync(snapshot, { force: true }));
  const args = [
    fsharpCli, "--root", root, "work", "context-plan", "--context", snapshot,
    "--action", action, "--occurred-at", options.occurredAt,
    "--repository", production.context.repository,
    "--protocol-version", production.context.protocolVersion,
    "--actor", production.context.actor,
    "--type", options.type ?? "task"
  ];
  for (const id of ids) args.push("--id", id);
  for (const changedPath of options.changedPaths ?? []) args.push("--path", changedPath);
  for (const observedPath of options.observedGitPaths ?? []) args.push("--observed-git-path", observedPath);
  for (const kind of options.required ?? []) args.push("--required", kind);
  for (const evidence of options.evidence ?? []) args.push("--evidence", `${evidence.type}=${evidence.path}`);
  if (options.reason !== undefined) args.push("--reason", options.reason);
  return spawnSync("dotnet", args, { cwd: repositoryRoot, encoding: "utf8" });
}

function fsharpBacklogDecision(state, action, reason = "reason") {
  const args = [fsharpCli, "work", "backlog-decide", "--state", state, "--action", action];
  if (reason !== undefined) args.push("--reason", reason);
  const result = spawnSync("dotnet", args, { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, json: JSON.parse(result.stdout), stderr: result.stderr };
}

function configureBacklogState(root, state) {
  captureWork(root, "Differential backlog", { id: "WI-MATRIX" });
  if (state === "ready" || state === "blocked") backlogTransition(root, "ready", "WI-MATRIX");
  if (state === "blocked") backlogTransition(root, "block", "WI-MATRIX", { reason: "prior" });
  if (state === "abandoned") backlogTransition(root, "abandon", "WI-MATRIX", { reason: "prior" });
}

function projectedField(change, current) {
  if (change.kind === "keep") return current ?? null;
  if (change.kind === "clear") return null;
  if (change.kind === "set") return change.value;
  throw new Error(`unknown backlog field change '${change.kind}'`);
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

test("F# context plan matches production multi-item begin order and metadata", (t) => {
  const root = contextFixture(t, [
    { id: "TASK-EXIST", type: "task", state: "ready", semanticState: "ready", evidence: [] }
  ]);
  const contextFile = path.join(root, ".ros", "context", "current.json");
  const before = fs.readFileSync(contextFile, "utf8");
  const observation = observeGitStatus(root);
  assert.notEqual(observation.outcome, "unavailable");
  const observedGitPaths = observation.changes.map((change) => change.path);
  const ids = ["TASK-NEW", "TASK-EXIST"];
  const production = transition(root, "begin", ids, { type: "task", actor: "differential" });
  const fsharp = fsharpContextPlan(t, root, before, "begin", ids, production, {
    occurredAt: production.events[0].occurredAt,
    observedGitPaths
  });
  assert.equal(fsharp.status, 0, fsharp.stderr);
  const plan = JSON.parse(fsharp.stdout).plan;
  assert.deepEqual(plan.workItems, production.context.workItems.map(normalizedItem));
  assert.deepEqual(plan.events, production.events.map(normalizedEvent));
  assert.equal(plan.repository, production.context.repository);
  assert.equal(plan.protocolVersion, production.context.protocolVersion);
  assert.equal(plan.actor, production.context.actor);
  assert.equal(plan.startedAt, production.context.startedAt);
  assert.equal(plan.updatedAt, production.context.updatedAt);
  assert.deepEqual(plan.baselineDirtyPaths, production.context.baselineDirtyPaths);
});

test("F# context plan and production both reject a later illegal item without context writes", (t) => {
  const workItems = [
    { id: "TASK-READY", type: "task", state: "ready", semanticState: "ready", evidence: [] },
    { id: "TASK-DONE", type: "task", state: "complete", semanticState: "complete", evidence: [], completedAt: "earlier" }
  ];
  const root = contextFixture(t, workItems);
  const contextFile = path.join(root, ".ros", "context", "current.json");
  const before = fs.readFileSync(contextFile, "utf8");
  const ids = ["TASK-READY", "TASK-DONE"];
  let productionError;
  try {
    transition(root, "begin", ids, { actor: "differential" });
  } catch (error) {
    productionError = error;
  }
  assert.match(productionError?.message ?? "", /cannot begin 'TASK-DONE' from 'complete'/);
  assert.equal(fs.readFileSync(contextFile, "utf8"), before);
  const productionShape = { context: { repository: "work-differential", protocolVersion: "1.0.0", actor: "differential" } };
  const fsharp = fsharpContextPlan(t, root, before, "begin", ids, productionShape, {
    occurredAt: "2026-09-08T23:45:00Z"
  });
  assert.equal(fsharp.status, 1, fsharp.stderr);
  assert.deepEqual(JSON.parse(fsharp.stdout), {
    schemaVersion: "1.0.0",
    outcome: "rejected",
    plan: null,
    rejection: {
      reason: "item-transition-rejected",
      workItem: "TASK-DONE",
      transition: { reason: "illegal-transition", state: "complete", action: "begin", missingEvidence: [] }
    }
  });
});

test("F# backlog decision matrix matches production and keeps start as promotion", (t) => {
  const backlogStates = ["captured", "ready", "blocked", "abandoned"];
  const backlogActions = ["ready", "block", "abandon", "start"];
  for (const state of backlogStates) {
    for (const action of backlogActions) {
      const root = fixture(t, "ready");
      configureBacklogState(root, state);
      const beforeQueue = JSON.parse(fs.readFileSync(path.join(root, ".ros", "work", "queue.json"), "utf8"));
      const beforeItem = beforeQueue.items.find((item) => item.id === "WI-MATRIX");
      let productionAllowed = true;
      try {
        if (action === "start") startWork(root, ["WI-MATRIX"], { type: "task" });
        else backlogTransition(root, action, "WI-MATRIX", { reason: "reason" });
      } catch {
        productionAllowed = false;
      }
      const fsharp = fsharpBacklogDecision(state, action);
      assert.equal(fsharp.status === 0, productionAllowed, `${state}/${action}: ${fsharp.stderr}`);
      if (!productionAllowed) continue;
      if (action === "start") {
        assert.equal(fsharp.json.effect.kind, "promote-to-live-work");
        const queue = JSON.parse(fs.readFileSync(path.join(root, ".ros", "work", "queue.json"), "utf8"));
        assert.equal(queue.items.find((item) => item.id === "WI-MATRIX").status, "ready");
      } else {
        const queue = JSON.parse(fs.readFileSync(path.join(root, ".ros", "work", "queue.json"), "utf8"));
        const item = queue.items.find((entry) => entry.id === "WI-MATRIX");
        assert.equal(fsharp.json.effect.kind, "change-state");
        assert.equal(fsharp.json.effect.state, item.status);
        assert.equal(projectedField(fsharp.json.effect.blockedReason, beforeItem.blockedReason), item.blockedReason ?? null);
        assert.equal(projectedField(fsharp.json.effect.abandonedReason, beforeItem.abandonedReason), item.abandonedReason ?? null);
      }
    }
  }
});

test("F# backlog promotion preflight matches production batch rejection and direct-ID allowance", (t) => {
  const root = fixture(t, "ready");
  captureWork(root, "Ready", { id: "WI-READY" });
  backlogTransition(root, "ready", "WI-READY");
  captureWork(root, "Captured", { id: "WI-CAPTURED" });
  assert.throws(
    () => startWork(root, ["WI-READY", "WI-CAPTURED"], { type: "feature" }),
    /cannot start backlog item 'WI-CAPTURED' from 'captured'/
  );
  const rejected = spawnSync("dotnet", [
    fsharpCli, "work", "backlog-promotion-plan",
    "--id", "WI-READY", "--id", "WI-CAPTURED", "--type", "feature",
    "--queue-state", "WI-READY=ready", "--queue-state", "WI-CAPTURED=captured"
  ], { cwd: repositoryRoot, encoding: "utf8" });
  assert.equal(rejected.status, 1, rejected.stderr);
  assert.deepEqual(JSON.parse(rejected.stdout).rejection, {
    reason: "backlog-item-not-ready", workItem: "WI-CAPTURED", state: "captured"
  });

  const directRoot = fixture(t, "ready");
  const production = startWork(directRoot, ["EXT-DIRECT"], { type: "feature" });
  assert.equal(production.context.workItems.find((item) => item.id === "EXT-DIRECT").semanticState, "active");
  const planned = spawnSync("dotnet", [
    fsharpCli, "work", "backlog-promotion-plan", "--id", "EXT-DIRECT", "--type", "feature"
  ], { cwd: repositoryRoot, encoding: "utf8" });
  assert.equal(planned.status, 0, planned.stderr);
  assert.deepEqual(JSON.parse(planned.stdout).plan, { workItems: ["EXT-DIRECT"], workType: "feature" });
});
