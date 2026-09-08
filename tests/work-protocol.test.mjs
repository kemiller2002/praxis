import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFile, execFileSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

import { initializeProject } from "../lib/bootstrap.mjs";

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const execFileAsync = promisify(execFile);

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

function sha256Text(text) {
  return crypto.createHash("sha256").update(text, "utf8").digest("hex");
}

function writeWorkStateTransaction(root, beforeEvent, afterEvent, beforeContext, afterContext) {
  writeJson(path.join(root, ".ros", "transactions", "work-state.json"), {
    schemaVersion: "1.0.0",
    resource: "work-state",
    writes: [
      {
        path: ".ros/events/events.jsonl",
        beforeSha256: sha256Text(beforeEvent),
        afterSha256: sha256Text(afterEvent),
        content: afterEvent
      },
      {
        path: ".ros/context/current.json",
        beforeSha256: sha256Text(beforeContext),
        afterSha256: sha256Text(afterContext),
        content: afterContext
      }
    ]
  });
}

function writeBacklogStateTransaction(root, beforeQueue, afterQueue, beforeProjection, afterProjection) {
  writeJson(path.join(root, ".ros", "transactions", "backlog-state.json"), {
    schemaVersion: "1.0.0",
    resource: "backlog-state",
    writes: [
      {
        path: ".ros/work/queue.json",
        beforeSha256: sha256Text(beforeQueue),
        afterSha256: sha256Text(afterQueue),
        content: afterQueue
      },
      {
        path: ".ros/work/queue.md",
        beforeSha256: sha256Text(beforeProjection),
        afterSha256: sha256Text(afterProjection),
        content: afterProjection
      }
    ]
  });
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

test("normal work transitions commit event and context with no pending journal", (t) => {
  const root = fixture(t);
  const begun = ros(root, ["work", "begin", "TASK-JOURNALED", "--type", "mechanical"]);
  assert.equal(begun.status, 0, begun.output);
  assert.equal(fs.existsSync(path.join(root, ".ros", "transactions", "work-state.json")), false);
  const context = JSON.parse(fs.readFileSync(path.join(root, ".ros", "context", "current.json"), "utf8"));
  const events = fs.readFileSync(path.join(root, ".ros", "events", "events.jsonl"), "utf8");
  assert.equal(context.workItems.find((item) => item.id === "TASK-JOURNALED").semanticState, "active");
  assert.match(events, /"workItem":"TASK-JOURNALED"/);
});

test("a transition recovers a partially applied F#-compatible work-state journal first", (t) => {
  const root = fixture(t);
  const eventFile = path.join(root, ".ros", "events", "events.jsonl");
  const contextFile = path.join(root, ".ros", "context", "current.json");
  const beforeEvent = fs.readFileSync(eventFile, "utf8");
  const beforeContext = fs.readFileSync(contextFile, "utf8");
  const afterEvent = `${beforeEvent}\n`;
  const afterContext = `${JSON.stringify({ ...JSON.parse(beforeContext), recoveryMarker: "replayed" }, null, 2)}\n`;
  writeWorkStateTransaction(root, beforeEvent, afterEvent, beforeContext, afterContext);
  fs.writeFileSync(eventFile, afterEvent);

  const begun = ros(root, ["work", "begin", "TASK-AFTER-RECOVERY", "--type", "mechanical"]);
  assert.equal(begun.status, 0, begun.output);
  const context = JSON.parse(fs.readFileSync(contextFile, "utf8"));
  assert.equal(context.recoveryMarker, "replayed");
  assert.equal(context.workItems.find((item) => item.id === "TASK-AFTER-RECOVERY").semanticState, "active");
  assert.equal(fs.existsSync(path.join(root, ".ros", "transactions", "work-state.json")), false);
});

test("work-state recovery rejects divergence before starting another transition", (t) => {
  const root = fixture(t);
  const eventFile = path.join(root, ".ros", "events", "events.jsonl");
  const contextFile = path.join(root, ".ros", "context", "current.json");
  const transactionFile = path.join(root, ".ros", "transactions", "work-state.json");
  const beforeEvent = fs.readFileSync(eventFile, "utf8");
  const beforeContext = fs.readFileSync(contextFile, "utf8");
  const afterEvent = `${beforeEvent}\n`;
  const afterContext = `${JSON.stringify({ ...JSON.parse(beforeContext), recoveryMarker: "expected" }, null, 2)}\n`;
  writeWorkStateTransaction(root, beforeEvent, afterEvent, beforeContext, afterContext);
  fs.writeFileSync(contextFile, `${JSON.stringify({ ...JSON.parse(beforeContext), divergent: true }, null, 2)}\n`);

  const begun = ros(root, ["work", "begin", "TASK-MUST-NOT-START", "--type", "mechanical"]);
  assert.equal(begun.status, 1);
  assert.match(begun.output, /target changed after preparation.*context\/current\.json/);
  assert.equal(fs.readFileSync(eventFile, "utf8"), beforeEvent, "preflight must prevent the earlier event write");
  assert.equal(JSON.parse(fs.readFileSync(contextFile, "utf8")).divergent, true);
  assert.equal(fs.existsSync(transactionFile), true, "conflicted journal remains for inspection");
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

test("add captures a backlog item with an auto-generated ID and tags", (t) => {
  const root = fixture(t);
  const first = ros(root, ["add", "Investigate WASM state payload growth", "--tag", "wasm,state", "--priority", "high"]);
  assert.equal(first.status, 0, first.output);
  const item = JSON.parse(first.output);
  assert.equal(item.id, "WI-0001");
  assert.deepEqual(item.tags, ["wasm", "state"]);
  assert.equal(item.priority, "high");
  assert.equal(item.status, "captured");
  const second = ros(root, ["add", "Rename WasmStateStore", "-t", "cleanup,wasm"]);
  assert.equal(JSON.parse(second.output).id, "WI-0002");
  assert.equal(JSON.parse(second.output).priority, "medium");
});

test("normal backlog mutations commit queue and projection with no pending journal", (t) => {
  const root = fixture(t);
  const added = ros(root, ["add", "Journaled backlog item", "--id", "WI-JOURNALED"]);
  assert.equal(added.status, 0, added.output);
  assert.equal(fs.existsSync(path.join(root, ".ros", "transactions", "backlog-state.json")), false);
  const queue = JSON.parse(fs.readFileSync(path.join(root, ".ros", "work", "queue.json"), "utf8"));
  const projection = fs.readFileSync(path.join(root, ".ros", "work", "queue.md"), "utf8");
  assert.equal(queue.items.find((item) => item.id === "WI-JOURNALED").status, "captured");
  assert.match(projection, /\| WI-JOURNALED \| Journaled backlog item \| captured \|/);
});

test("a backlog mutation recovers a partially applied F#-compatible journal first", (t) => {
  const root = fixture(t);
  const queueFile = path.join(root, ".ros", "work", "queue.json");
  const projectionFile = path.join(root, ".ros", "work", "queue.md");
  const beforeQueue = fs.readFileSync(queueFile, "utf8");
  const beforeProjection = fs.readFileSync(projectionFile, "utf8");
  const recoveredItem = {
    id: "WI-RECOVERED", title: "Recovered", description: null, tags: [], priority: "medium",
    status: "captured", attachments: [], createdAt: "2026-09-08T00:00:00.000Z",
    updatedAt: "2026-09-08T00:00:00.000Z", createdBy: "test", source: "manual", sourceReference: null
  };
  const afterQueue = `${JSON.stringify({ ...JSON.parse(beforeQueue), items: [recoveredItem] }, null, 2)}\n`;
  const afterProjection = `${beforeProjection}| WI-RECOVERED | Recovered | captured |  | medium |\n`;
  writeBacklogStateTransaction(root, beforeQueue, afterQueue, beforeProjection, afterProjection);
  fs.writeFileSync(queueFile, afterQueue);

  const added = ros(root, ["add", "After recovery", "--id", "WI-AFTER-RECOVERY"]);
  assert.equal(added.status, 0, added.output);
  const queue = JSON.parse(fs.readFileSync(queueFile, "utf8"));
  assert.ok(queue.items.some((item) => item.id === "WI-RECOVERED"));
  assert.ok(queue.items.some((item) => item.id === "WI-AFTER-RECOVERY"));
  assert.equal(fs.existsSync(path.join(root, ".ros", "transactions", "backlog-state.json")), false);
});

test("backlog recovery rejects divergence before starting another mutation", (t) => {
  const root = fixture(t);
  const queueFile = path.join(root, ".ros", "work", "queue.json");
  const projectionFile = path.join(root, ".ros", "work", "queue.md");
  const transactionFile = path.join(root, ".ros", "transactions", "backlog-state.json");
  const beforeQueue = fs.readFileSync(queueFile, "utf8");
  const beforeProjection = fs.readFileSync(projectionFile, "utf8");
  const afterQueue = `${JSON.stringify({ ...JSON.parse(beforeQueue), recoveryMarker: true }, null, 2)}\n`;
  const afterProjection = `${beforeProjection}\n`;
  writeBacklogStateTransaction(root, beforeQueue, afterQueue, beforeProjection, afterProjection);
  fs.writeFileSync(projectionFile, "third-party projection\n");

  const added = ros(root, ["add", "Must not be added", "--id", "WI-MUST-NOT-ADD"]);
  assert.equal(added.status, 1);
  assert.match(added.output, /target changed after preparation.*queue\.md/);
  assert.equal(fs.readFileSync(queueFile, "utf8"), beforeQueue, "preflight must prevent the earlier queue write");
  assert.equal(fs.readFileSync(projectionFile, "utf8"), "third-party projection\n");
  assert.equal(fs.existsSync(transactionFile), true, "conflicted journal remains for inspection");
});

test("parallel backlog captures retain every queue entry under the shared work lease", async (t) => {
  const root = fixture(t);
  const executable = path.join(root, "ros");
  const ids = Array.from({ length: 8 }, (_, index) => `WI-PARALLEL-${index + 1}`);
  await Promise.all(ids.map((id) => execFileAsync(executable, ["add", `Parallel ${id}`, "--id", id], { cwd: root, encoding: "utf8" })));

  const queue = JSON.parse(fs.readFileSync(path.join(root, ".ros", "work", "queue.json"), "utf8"));
  assert.deepEqual(queue.items.map((item) => item.id).sort(), ids.sort());
  assert.equal(fs.existsSync(path.join(root, ".ros", "transactions", "backlog-state.json")), false);
});

test("add rejects an explicit ID that collides with an existing backlog or context item", (t) => {
  const root = fixture(t);
  assert.equal(ros(root, ["add", "First", "--id", "WI-CUSTOM"]).status, 0);
  const duplicate = ros(root, ["add", "Second", "--id", "WI-CUSTOM"]);
  assert.equal(duplicate.status, 1);
  assert.match(duplicate.output, /already exists/);
  ros(root, ["work", "begin", "FEAT-500"]);
  const contextCollision = ros(root, ["add", "Third", "--id", "FEAT-500"]);
  assert.equal(contextCollision.status, 1);
  assert.match(contextCollision.output, /already exists in repository context/);
});

test("work list and work ready filter the unified backlog by tag and status", (t) => {
  const root = fixture(t);
  ros(root, ["add", "A", "--tag", "wasm"]);
  ros(root, ["add", "B", "--tag", "cleanup"]);
  ros(root, ["work", "ready", "WI-0001"]);
  const readyOnly = JSON.parse(ros(root, ["work", "ready"]).output);
  assert.deepEqual(readyOnly.map((row) => row.id), ["WI-0001"]);
  const byTag = JSON.parse(ros(root, ["work", "list", "--tag", "cleanup"]).output);
  assert.deepEqual(byTag.map((row) => row.id), ["WI-0002"]);
  const bare = JSON.parse(ros(root, ["work"]).output);
  assert.deepEqual(bare.map((row) => row.id).filter((id) => id.startsWith("WI-")), ["WI-0001", "WI-0002"]);
});

test("captured item must become ready before it can start, and abandonment is terminal", (t) => {
  const root = fixture(t);
  ros(root, ["add", "Idea", "--id", "WI-IDEA"]);
  const tooSoon = ros(root, ["work", "start", "WI-IDEA"]);
  assert.equal(tooSoon.status, 1);
  assert.match(tooSoon.output, /mark it ready first/);
  ros(root, ["work", "ready", "WI-IDEA"]);
  assert.equal(ros(root, ["work", "abandon", "WI-IDEA", "--reason", "no longer relevant"]).status, 0);
  const afterAbandon = ros(root, ["work", "start", "WI-IDEA"]);
  assert.equal(afterAbandon.status, 1);
  assert.match(afterAbandon.output, /abandoned/);
});

test("start promotes a ready backlog item and live context state wins over backlog status", (t) => {
  const root = fixture(t);
  ros(root, ["add", "Ship it", "--id", "WI-SHIP", "--tag", "wasm"]);
  ros(root, ["work", "ready", "WI-SHIP"]);
  assert.equal(ros(root, ["work", "start", "WI-SHIP", "--type", "feature"]).status, 0);
  const active = JSON.parse(ros(root, ["work", "show", "WI-SHIP"]).output);
  assert.equal(active.status, "active");
  assert.equal(active.liveWorkItem.semanticState, "active");
  fs.mkdirSync(path.join(root, "src")); fs.writeFileSync(path.join(root, "src", "ship.js"), "export const shipped = true;\n");
  fs.mkdirSync(path.join(root, "test")); fs.writeFileSync(path.join(root, "test", "ship.test.js"), "// passed by fixture\n");
  assert.equal(ros(root, ["work", "done", "WI-SHIP", "--evidence", "implementation=src/ship.js", "--evidence", "tests=test/ship.test.js"]).status, 0);
  const done = JSON.parse(ros(root, ["work", "show", "WI-SHIP"]).output);
  assert.equal(done.status, "complete");
});

test("block dispatches per-ID to the backlog or the in-flight work item in a single call", (t) => {
  const root = fixture(t);
  ros(root, ["add", "Backlog item", "--id", "WI-BACKLOG"]);
  ros(root, ["work", "ready", "WI-BACKLOG"]);
  ros(root, ["work", "begin", "FEAT-700"]);
  const blocked = ros(root, ["work", "block", "WI-BACKLOG", "FEAT-700", "--reason", "waiting on benchmark"]);
  assert.equal(blocked.status, 0, blocked.output);
  const rows = JSON.parse(ros(root, ["work", "list"]).output);
  assert.equal(rows.find((row) => row.id === "WI-BACKLOG").status, "blocked");
  assert.equal(rows.find((row) => row.id === "FEAT-700").status, "blocked");
  assert.equal(ros(root, ["work", "ready", "WI-BACKLOG"]).status, 0);
  assert.equal(ros(root, ["work", "resume", "FEAT-700"]).status, 0);
});

test("show surfaces a detail file's content when one exists", (t) => {
  const root = fixture(t);
  ros(root, ["add", "Needs detail", "--id", "WI-DETAIL"]);
  const detailDir = path.join(root, ".ros", "work", "items");
  fs.mkdirSync(detailDir, {recursive: true});
  fs.writeFileSync(path.join(detailDir, "WI-DETAIL.md"), "# WI-DETAIL\n\n## Objective\nRename without changing behavior.\n");
  const shown = JSON.parse(ros(root, ["work", "show", "WI-DETAIL"]).output);
  assert.match(shown.detail, /Objective/);
});

test("the human-readable queue projection is regenerated on every backlog change", (t) => {
  const root = fixture(t);
  ros(root, ["add", "Readable item", "--tag", "docs"]);
  const markdown = fs.readFileSync(path.join(root, ".ros", "work", "queue.md"), "utf8");
  assert.match(markdown, /\| WI-0001 \| Readable item \| captured \| docs \|/);
});

test(".ros/work changes do not themselves require separate work-item attribution", (t) => {
  const root = fixture(t);
  const result = ros(root, ["add", "Should not trip attribution"], {env: {...process.env, ROS_BASE_REF: "missing-fixture-commit"}});
  assert.equal(result.status, 0, result.output);
  assert.equal(ros(root, ["validate"]).status, 0);
});

test("add accepts a description and files, and update/attach work on IDs that were only ever begun directly", (t) => {
  const root = fixture(t);
  const design = path.join(root, "design.md");
  fs.writeFileSync(design, "notes\n");

  const added = JSON.parse(ros(root, ["add", "Investigate payload growth", "--description", "Grows superlinearly.", "--file", `${design}=notes.md`]).output);
  assert.equal(added.description, "Grows superlinearly.");
  assert.equal(added.attachments.length, 1);
  assert.equal(added.attachments[0].name, "notes.md");
  assert.ok(!("file" in added.attachments[0]), "internal storage filename must not leak");

  const updated = JSON.parse(ros(root, ["work", "update", "WI-0001", "--title", "Investigate payload growth (root cause)", "--priority", "high"]).output);
  assert.equal(updated.title, "Investigate payload growth (root cause)");
  assert.equal(updated.priority, "high");
  assert.equal(updated.description, "Grows superlinearly.", "fields not passed to update are left unchanged");

  ros(root, ["work", "begin", "FEAT-900"]);
  const attached = JSON.parse(ros(root, ["work", "attach", "FEAT-900", "--file", `${design}=spec.md`]).output);
  assert.equal(attached.attachments[0].name, "spec.md");
  assert.equal(attached.liveWorkItem.semanticState, "active", "attaching a file must not disturb in-flight state");

  const describedInFlight = JSON.parse(ros(root, ["work", "update", "FEAT-900", "--description", "Now documented."]).output);
  assert.equal(describedInFlight.description, "Now documented.");
  assert.equal(describedInFlight.liveWorkItem.semanticState, "active");
});

test("attaching two files under the same display name keeps both distinct on disk", (t) => {
  const root = fixture(t);
  const first = path.join(root, "a.txt");
  const second = path.join(root, "b.txt");
  fs.writeFileSync(first, "version one\n");
  fs.writeFileSync(second, "version two\n");
  ros(root, ["add", "Dup names", "--id", "WI-DUP"]);
  ros(root, ["work", "attach", "WI-DUP", "--file", `${first}=same-name.txt`]);
  const result = JSON.parse(ros(root, ["work", "attach", "WI-DUP", "--file", `${second}=same-name.txt`]).output);
  assert.equal(result.attachments.length, 2);
  assert.deepEqual(result.attachments.map((a) => a.name), ["same-name.txt", "same-name.txt"]);
  const files = fs.readdirSync(path.join(root, ".ros", "work", "attachments", "WI-DUP"));
  assert.equal(files.length, 2);
  assert.equal(new Set(files).size, 2, "stored filenames must not collide");
});
