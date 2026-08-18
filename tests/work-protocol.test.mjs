import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-work-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Protocol Consumer" });
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

function adapterFixture(root) {
  writeJson(path.join(root, "adapter-store.json"), {
    schemaVersion: "1.0.0",
    protocolVersion: "1.0.0",
    repositories: ["protocol-consumer"],
    workItems: {"FEAT-900": {id: "FEAT-900", state: "ready", type: "feature"}},
    events: [],
    requests: {}
  });
}

function adapterCall(root, request) {
  writeJson(path.join(root, "adapter-request.json"), request);
  const result = ros(root, ["adapter", "call", "--store", "adapter-store.json", "--request", "adapter-request.json"]);
  return {...result, json: JSON.parse(result.output)};
}

test("valid attributed work completes with evidence", (t) => {
  const root = fixture(t);
  assert.equal(ros(root, ["work", "begin", "FEAT-142", "--type", "feature"]).status, 0);
  fs.mkdirSync(path.join(root, "src")); fs.writeFileSync(path.join(root, "src", "feature.js"), "export const ready = true;\n");
  fs.mkdirSync(path.join(root, "test")); fs.writeFileSync(path.join(root, "test", "feature.test.js"), "// passed by fixture\n");
  const completed = ros(root, ["work", "complete", "FEAT-142", "--evidence", "implementation=src/feature.js", "--evidence", "tests=test/feature.test.js"]);
  assert.equal(completed.status, 0, completed.output);
  assert.equal(ros(root, ["validate"]).status, 0);
});

test("unattributed meaningful change fails validation", (t) => {
  const root = fixture(t);
  fs.writeFileSync(path.join(root, "meaningful.js"), "changed\n");
  const result = ros(root, ["validate"], {env: {...process.env, ROS_BASE_REF: "missing-fixture-commit"}});
  assert.equal(result.status, 1);
  assert.match(result.output, /no active or completed work-item attribution/);
});

test("completion without configured evidence is rejected", (t) => {
  const root = fixture(t);
  assert.equal(ros(root, ["work", "begin", "BUG-031", "--type", "bug"]).status, 0);
  const result = ros(root, ["work", "complete", "BUG-031"]);
  assert.equal(result.status, 1);
  assert.match(result.output, /completion evidence missing/);
});

test("blocking records a reason and resume is legal", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-009"]);
  assert.equal(ros(root, ["work", "block", "TASK-009", "--reason", "dependency unavailable"]).status, 0);
  assert.equal(ros(root, ["work", "resume", "TASK-009"]).status, 0);
  const context = JSON.parse(ros(root, ["work", "context"]).output);
  const item = context.workItems.find((candidate) => candidate.id === "TASK-009");
  assert.equal(item.semanticState, "active");
  assert.equal(item.blockReason, "dependency unavailable");
});

test("work context reports legal actions and completion evidence", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "FEAT-200", "--type", "feature"]);
  const context = JSON.parse(ros(root, ["work", "context", "FEAT-200"]).output);
  assert.deepEqual(context.workItems[0].allowedActions, ["block", "complete"]);
  assert.deepEqual(context.workItems[0].requiredEvidenceForCompletion, ["implementation", "tests"]);
});

test("JSON validation and status provide deterministic repair guidance", (t) => {
  const root = fixture(t);
  fs.writeFileSync(path.join(root, "unattributed.js"), "change\n");
  const fixtureEnvironment = {env: {...process.env, ROS_BASE_REF: "missing-fixture-commit"}};
  const validation = ros(root, ["validate", "--json"], fixtureEnvironment);
  assert.equal(validation.status, 1);
  const report = JSON.parse(validation.output);
  assert.equal(report.valid, false);
  assert.match(report.findings[0].repair, /work begin/);
  const status = JSON.parse(ros(root, ["status"], fixtureEnvironment).output);
  assert.equal(status.validation, "failed");
  assert.ok(status.nextActions.some((action) => action.includes("work begin")));
});

test("multiple work items and adapter retry retain attribution once", (t) => {
  const root = fixture(t);
  assert.equal(ros(root, ["work", "begin", "FEAT-142", "OBL-009"]).status, 0);
  const first = ros(root, ["adapter", "publish", "--target", ".ros/mock/events.jsonl"]);
  const second = ros(root, ["adapter", "publish", "--target", ".ros/mock/events.jsonl"]);
  assert.match(first.output, /published 3 event/);
  assert.match(second.output, /published 0 event/);
  const receipts = JSON.parse(fs.readFileSync(path.join(root, ".ros", "publications.json"), "utf8"));
  assert.equal(Object.keys(receipts).length, 3);
  assert.ok(Object.values(receipts).every((receipt) => receipt.status === "success"));
});

test("research completion does not imply a supported conclusion", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "RES-017", "--type", "research"]);
  fs.writeFileSync(path.join(root, "finding.md"), "inconclusive\n");
  const result = ros(root, ["work", "complete", "RES-017", "--conclusion", "inconclusive", "--evidence", "research-record=finding.md"]);
  assert.equal(result.status, 0, result.output);
  const context = JSON.parse(ros(root, ["work", "context"]).output);
  assert.equal(context.workItems.find((item) => item.id === "RES-017").conclusion, "inconclusive");
});

test("mechanical mutation is allowed only with explicit system attribution", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "SYS-FORMAT", "--type", "mechanical"]);
  fs.writeFileSync(path.join(root, "formatted.txt"), "deterministic\n");
  assert.equal(ros(root, ["work", "complete", "SYS-FORMAT"]).status, 0);
  assert.equal(ros(root, ["validate"]).status, 0);
});

test("adapter write failure is not reported as success", (t) => {
  const root = fixture(t);
  ros(root, ["work", "begin", "TASK-FAILURE"]);
  fs.writeFileSync(path.join(root, "not-a-directory"), "occupied\n");
  const result = ros(root, ["adapter", "publish", "--target", "not-a-directory/events.jsonl"]);
  assert.equal(result.status, 1);
  assert.doesNotMatch(result.output, /published \d/);
  const events = fs.readFileSync(path.join(root, ".ros", "events", "events.jsonl"), "utf8");
  assert.match(events, /"status":"pending"/);
  assert.equal(fs.existsSync(path.join(root, ".ros", "publications.json")), false);
});

test("adapter contract reads and transitions without vendor assumptions", (t) => {
  const root = fixture(t); adapterFixture(root);
  const read = adapterCall(root, {protocolVersion: "1.0.0", requestId: "req-get-1", operation: "getWorkItem", repository: "protocol-consumer", principal: "agent:test", scopes: ["work:read"], workItem: "FEAT-900"});
  assert.equal(read.status, 0); assert.equal(read.json.data.workItem.state, "ready");
  const transition = adapterCall(root, {protocolVersion: "1.0.0", requestId: "req-transition-1", operation: "transitionWorkItem", repository: "protocol-consumer", principal: "agent:test", scopes: ["work:transition"], workItem: "FEAT-900", expectedState: "ready", targetState: "active"});
  assert.equal(transition.status, 0); assert.equal(transition.json.data.workItem.state, "active");
});

test("adapter retries are idempotent and state conflicts are explicit", (t) => {
  const root = fixture(t); adapterFixture(root);
  const request = {protocolVersion: "1.0.0", requestId: "req-same", operation: "transitionWorkItem", repository: "protocol-consumer", principal: "agent:test", scopes: ["work:transition"], workItem: "FEAT-900", expectedState: "ready", targetState: "active"};
  const first = adapterCall(root, request); const retry = adapterCall(root, request);
  assert.deepEqual(retry.json, first.json);
  const conflict = adapterCall(root, {...request, requestId: "req-conflict", targetState: "complete"});
  assert.equal(conflict.status, 1); assert.equal(conflict.json.error.code, "state_conflict");
});

test("adapter enforces repository, authorization, protocol, and unknown outcomes", (t) => {
  const root = fixture(t); adapterFixture(root);
  const base = {protocolVersion: "1.0.0", operation: "transitionWorkItem", repository: "protocol-consumer", principal: "agent:test", scopes: [], workItem: "FEAT-900", targetState: "active"};
  const forbidden = adapterCall(root, {...base, requestId: "req-forbidden"});
  assert.equal(forbidden.status, 1); assert.equal(forbidden.json.error.code, "forbidden");
  const wrongRepository = adapterCall(root, {...base, requestId: "req-repo", repository: "other"});
  assert.equal(wrongRepository.json.error.code, "repository_unknown");
  const mismatch = adapterCall(root, {...base, requestId: "req-version", protocolVersion: "2.0.0"});
  assert.equal(mismatch.json.error.code, "protocol_mismatch");
  const unknown = adapterCall(root, {...base, requestId: "req-unknown", scopes: ["work:transition"], simulateOutcome: "unknown"});
  assert.equal(unknown.status, 2); assert.equal(unknown.json.outcome, "unknown");
  const store = JSON.parse(fs.readFileSync(path.join(root, "adapter-store.json"), "utf8"));
  assert.equal(store.workItems["FEAT-900"].state, "ready");
});

test("adapter event publication deduplicates semantic event IDs", (t) => {
  const root = fixture(t); adapterFixture(root);
  const base = {protocolVersion: "1.0.0", operation: "publishRepositoryEvent", repository: "protocol-consumer", principal: "ci:test", scopes: ["event:publish"], event: {eventId: "evt-1", type: "work.completed"}};
  assert.equal(adapterCall(root, {...base, requestId: "req-event-1"}).status, 0);
  assert.equal(adapterCall(root, {...base, requestId: "req-event-2"}).status, 0);
  const store = JSON.parse(fs.readFileSync(path.join(root, "adapter-store.json"), "utf8"));
  assert.equal(store.events.length, 1);
});
