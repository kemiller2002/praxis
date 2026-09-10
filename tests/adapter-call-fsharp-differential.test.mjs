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

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-adapter-call-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Adapter Call Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function writeStore(root, store) {
  fs.writeFileSync(path.join(root, "adapter-store.json"), JSON.stringify(store, null, 2));
}

function baseStore() {
  return {
    schemaVersion: "1.0.0",
    protocolVersion: "1.0.0",
    repositories: ["protocol-consumer"],
    workItems: { "FEAT-900": { id: "FEAT-900", state: "ready", type: "feature" } },
    events: [],
    requests: {}
  };
}

function writeRequest(root, request) {
  fs.writeFileSync(path.join(root, "adapter-request.json"), JSON.stringify(request));
}

function readStore(root) {
  return JSON.parse(fs.readFileSync(path.join(root, "adapter-store.json"), "utf8"));
}

function nodeCall(root) {
  const result = spawnSync(path.join(root, "ros"), ["adapter", "call", "--store", "adapter-store.json", "--request", "adapter-request.json"], {
    cwd: root,
    encoding: "utf8"
  });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function fsharpCall(root) {
  const result = spawnSync(
    "dotnet",
    [fsharpCli, "--root", root, "adapter", "call", "--store", "adapter-store.json", "--request", "adapter-request.json"],
    { cwd: repositoryRoot, encoding: "utf8" }
  );
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

test("F# adapter call reads and transitions a work item without vendor assumptions, byte-identical to production", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const node = fixture(t, "read-transition-node");
  const fsharp = fixture(t, "read-transition-fsharp");
  writeStore(node, baseStore());
  writeStore(fsharp, baseStore());

  const readRequest = {
    protocolVersion: "1.0.0",
    requestId: "req-get-1",
    operation: "getWorkItem",
    repository: "protocol-consumer",
    principal: "agent:test",
    scopes: ["work:read"],
    workItem: "FEAT-900"
  };
  writeRequest(node, readRequest);
  writeRequest(fsharp, readRequest);
  const nodeRead = nodeCall(node);
  const fsharpRead = fsharpCall(fsharp);
  assert.equal(nodeRead.status, 0, nodeRead.stderr);
  assert.equal(fsharpRead.status, 0, fsharpRead.stderr);
  assert.equal(nodeRead.stdout.trim(), fsharpRead.stdout.trim());

  const transitionRequest = {
    protocolVersion: "1.0.0",
    requestId: "req-transition-1",
    operation: "transitionWorkItem",
    repository: "protocol-consumer",
    principal: "agent:test",
    scopes: ["work:transition"],
    workItem: "FEAT-900",
    expectedState: "ready",
    targetState: "active"
  };
  writeRequest(node, transitionRequest);
  writeRequest(fsharp, transitionRequest);
  const nodeTransition = nodeCall(node);
  const fsharpTransition = fsharpCall(fsharp);
  assert.equal(nodeTransition.status, 0, nodeTransition.stderr);
  assert.equal(fsharpTransition.status, 0, fsharpTransition.stderr);
  assert.equal(nodeTransition.stdout.trim(), fsharpTransition.stdout.trim());
  assert.deepEqual(readStore(node), readStore(fsharp));
});

test("F# adapter call retries are idempotent and state conflicts are explicit, matching production", (t) => {
  const node = fixture(t, "idempotent-node");
  const fsharp = fixture(t, "idempotent-fsharp");
  writeStore(node, baseStore());
  writeStore(fsharp, baseStore());

  const request = {
    protocolVersion: "1.0.0",
    requestId: "req-same",
    operation: "transitionWorkItem",
    repository: "protocol-consumer",
    principal: "agent:test",
    scopes: ["work:transition"],
    workItem: "FEAT-900",
    expectedState: "ready",
    targetState: "active"
  };
  writeRequest(node, request);
  writeRequest(fsharp, request);
  const nodeFirst = nodeCall(node);
  const fsharpFirst = fsharpCall(fsharp);
  const nodeRetry = nodeCall(node);
  const fsharpRetry = fsharpCall(fsharp);
  assert.equal(nodeRetry.stdout.trim(), nodeFirst.stdout.trim());
  assert.equal(fsharpRetry.stdout.trim(), fsharpFirst.stdout.trim());
  assert.equal(nodeFirst.stdout.trim(), fsharpFirst.stdout.trim());

  const conflictRequest = { ...request, requestId: "req-conflict", targetState: "complete" };
  writeRequest(node, conflictRequest);
  writeRequest(fsharp, conflictRequest);
  const nodeConflict = nodeCall(node);
  const fsharpConflict = fsharpCall(fsharp);
  assert.equal(nodeConflict.status, 1);
  assert.equal(fsharpConflict.status, 1);
  assert.equal(nodeConflict.stdout.trim(), fsharpConflict.stdout.trim());
  assert.deepEqual(readStore(node), readStore(fsharp));
});

test("F# adapter call enforces repository, authorization, protocol, and unknown outcomes, matching production", (t) => {
  const node = fixture(t, "enforce-node");
  const fsharp = fixture(t, "enforce-fsharp");
  writeStore(node, baseStore());
  writeStore(fsharp, baseStore());

  const base = {
    protocolVersion: "1.0.0",
    operation: "transitionWorkItem",
    repository: "protocol-consumer",
    principal: "agent:test",
    scopes: [],
    workItem: "FEAT-900",
    targetState: "active"
  };

  const scenarios = [
    { ...base, requestId: "req-forbidden" },
    { ...base, requestId: "req-repo", repository: "other" },
    { ...base, requestId: "req-version", protocolVersion: "2.0.0" },
    { ...base, requestId: "req-unknown", scopes: ["work:transition"], simulateOutcome: "unknown" }
  ];

  for (const scenario of scenarios) {
    writeRequest(node, scenario);
    writeRequest(fsharp, scenario);
    const nodeResult = nodeCall(node);
    const fsharpResult = fsharpCall(fsharp);
    assert.equal(nodeResult.status, fsharpResult.status, JSON.stringify(scenario));
    assert.equal(nodeResult.stdout.trim(), fsharpResult.stdout.trim(), JSON.stringify(scenario));
  }

  const nodeStore = readStore(node);
  const fsharpStore = readStore(fsharp);
  assert.equal(nodeStore.workItems["FEAT-900"].state, "ready");
  assert.equal(fsharpStore.workItems["FEAT-900"].state, "ready");
  assert.deepEqual(nodeStore, fsharpStore);
});

test("F# adapter call deduplicates published event IDs, matching production", (t) => {
  const node = fixture(t, "publish-node");
  const fsharp = fixture(t, "publish-fsharp");
  writeStore(node, baseStore());
  writeStore(fsharp, baseStore());

  const base = {
    protocolVersion: "1.0.0",
    operation: "publishRepositoryEvent",
    repository: "protocol-consumer",
    principal: "ci:test",
    scopes: ["event:publish"],
    event: { eventId: "evt-1", type: "work.completed" }
  };

  writeRequest(node, { ...base, requestId: "req-event-1" });
  writeRequest(fsharp, { ...base, requestId: "req-event-1" });
  assert.equal(nodeCall(node).status, 0);
  assert.equal(fsharpCall(fsharp).status, 0);

  writeRequest(node, { ...base, requestId: "req-event-2" });
  writeRequest(fsharp, { ...base, requestId: "req-event-2" });
  assert.equal(nodeCall(node).status, 0);
  assert.equal(fsharpCall(fsharp).status, 0);

  const nodeStore = readStore(node);
  const fsharpStore = readStore(fsharp);
  assert.equal(nodeStore.events.length, 1);
  assert.equal(fsharpStore.events.length, 1);
  assert.deepEqual(nodeStore, fsharpStore);
});

test("F# adapter call rejects a missing required field and a missing request file with production's exact messages", (t) => {
  const node = fixture(t, "missing-node");
  const fsharp = fixture(t, "missing-fsharp");
  writeStore(node, baseStore());
  writeStore(fsharp, baseStore());

  writeRequest(node, { requestId: "r1", operation: "getWorkItem", repository: "x", principal: "p" });
  writeRequest(fsharp, { requestId: "r1", operation: "getWorkItem", repository: "x", principal: "p" });
  const nodeMissing = nodeCall(node);
  const fsharpMissing = fsharpCall(fsharp);
  assert.equal(nodeMissing.status, 1);
  assert.equal(fsharpMissing.status, 1);
  assert.match(nodeMissing.stderr, /adapter request is missing 'protocolVersion'/);
  assert.equal(fsharpMissing.stderr.trim(), "ERROR adapter request is missing 'protocolVersion'");

  const nodeNotFound = spawnSync(path.join(node, "ros"), ["adapter", "call", "--store", "adapter-store.json", "--request", "nope.json"], {
    cwd: node,
    encoding: "utf8"
  });
  const fsharpNotFound = spawnSync(
    "dotnet",
    [fsharpCli, "--root", fsharp, "adapter", "call", "--store", "adapter-store.json", "--request", "nope.json"],
    { cwd: repositoryRoot, encoding: "utf8" }
  );
  assert.equal(nodeNotFound.status, 1);
  assert.equal(fsharpNotFound.status, 1);
  assert.match(nodeNotFound.stderr, /adapter request not found: nope\.json/);
  assert.equal(fsharpNotFound.stderr.trim(), "ERROR adapter request not found: nope.json");
});
