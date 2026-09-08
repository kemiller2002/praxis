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
