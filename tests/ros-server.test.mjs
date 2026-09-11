import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { createServer } from "../tools/ros_server.mjs";

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-server-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 }));
  initializeProject({ target: root, project: "Server Consumer" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

async function serve(t, root) {
  const server = createServer(root);
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  t.after(() => new Promise((resolve) => server.close(resolve)));
  const { port } = server.address();
  const base = `http://127.0.0.1:${port}`;
  return {
    get: (urlPath) => fetch(`${base}${urlPath}`),
    post: (urlPath, body) => fetch(`${base}${urlPath}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body ?? {})
    }),
    postForm: (urlPath, formData) => fetch(`${base}${urlPath}`, { method: "POST", body: formData })
  };
}

function fileField(content, filename, type = "text/plain") {
  return new File([content], filename, { type });
}

test("GET /api/work lists the unified queue, including items with no backlog entry", async (t) => {
  const root = fixture(t);
  const client = await serve(t, root);
  const response = await client.get("/api/work");
  assert.equal(response.status, 200);
  const rows = await response.json();
  assert.ok(rows.some((row) => row.id.startsWith("ROS-INSTALL")));
});

test("POST /api/work captures a backlog item with an auto-generated ID", async (t) => {
  const root = fixture(t);
  const client = await serve(t, root);
  const response = await client.post("/api/work", { title: "Investigate payload growth", tags: ["wasm", "state"], priority: "high" });
  assert.equal(response.status, 200);
  const item = await response.json();
  assert.equal(item.id, "WI-0001");
  assert.deepEqual(item.tags, ["wasm", "state"]);
  assert.equal(item.status, "captured");
});

test("captured -> ready -> start requires the ready gate, same as the CLI", async (t) => {
  const root = fixture(t);
  const client = await serve(t, root);
  await client.post("/api/work", { title: "Too soon" });

  const tooSoon = await client.post("/api/work/WI-0001/start", { type: "feature" });
  assert.equal(tooSoon.status, 400);
  assert.match((await tooSoon.json()).error, /mark it ready first/);

  assert.equal((await client.post("/api/work/WI-0001/ready")).status, 200);
  const started = await client.post("/api/work/WI-0001/start", { type: "feature" });
  assert.equal(started.status, 200);
  const startedBody = await started.json();
  assert.equal(startedBody.status, "active");
  assert.equal(startedBody.liveWorkItem.semanticState, "active");
});

test("full lifecycle through the HTTP API matches the CLI's evidence rules", async (t) => {
  const root = fixture(t);
  const client = await serve(t, root);
  await client.post("/api/work", { title: "Ship it", id: "WI-SHIP" });
  await client.post("/api/work/WI-SHIP/ready");
  await client.post("/api/work/WI-SHIP/start", { type: "feature" });

  const missingEvidence = await client.post("/api/work/WI-SHIP/complete", { evidence: [] });
  assert.equal(missingEvidence.status, 400);
  assert.match((await missingEvidence.json()).error, /completion evidence missing/);

  fs.mkdirSync(path.join(root, "src"));
  fs.writeFileSync(path.join(root, "src", "ship.js"), "export const shipped = true;\n");
  fs.mkdirSync(path.join(root, "test"));
  fs.writeFileSync(path.join(root, "test", "ship.test.js"), "// passed by fixture\n");

  const completed = await client.post("/api/work/WI-SHIP/complete", {
    evidence: [{ type: "implementation", path: "src/ship.js" }, { type: "tests", path: "test/ship.test.js" }]
  });
  assert.equal(completed.status, 200);
  assert.equal((await completed.json()).status, "complete");
});

test("block dispatches per-ID to the backlog or the in-flight item, same as the CLI", async (t) => {
  const root = fixture(t);
  const client = await serve(t, root);
  await client.post("/api/work", { title: "Backlog item", id: "WI-BACKLOG" });
  await client.post("/api/work/WI-BACKLOG/ready");
  await client.post("/api/work", { title: "In-flight item", id: "WI-INFLIGHT" });
  await client.post("/api/work/WI-INFLIGHT/ready");
  await client.post("/api/work/WI-INFLIGHT/start", { type: "feature" });

  const blockedBacklog = await client.post("/api/work/WI-BACKLOG/block", { reason: "waiting on benchmark" });
  assert.equal(blockedBacklog.status, 200);
  assert.equal((await blockedBacklog.json()).status, "blocked");

  const blockedInFlight = await client.post("/api/work/WI-INFLIGHT/block", { reason: "waiting on benchmark" });
  assert.equal(blockedInFlight.status, 200);
  assert.equal((await blockedInFlight.json()).liveWorkItem.semanticState, "blocked");

  const resumed = await client.post("/api/work/WI-INFLIGHT/resume");
  assert.equal(resumed.status, 200);
  assert.equal((await resumed.json()).liveWorkItem.semanticState, "active");
});

test("abandonment is terminal over HTTP the same as over the CLI", async (t) => {
  const root = fixture(t);
  const client = await serve(t, root);
  await client.post("/api/work", { title: "Dead idea", id: "WI-DEAD" });
  await client.post("/api/work/WI-DEAD/ready");
  const abandoned = await client.post("/api/work/WI-DEAD/abandon", { reason: "no longer relevant" });
  assert.equal(abandoned.status, 200);
  const afterAbandon = await client.post("/api/work/WI-DEAD/start", { type: "feature" });
  assert.equal(afterAbandon.status, 400);
  assert.match((await afterAbandon.json()).error, /abandoned/);
});

test("GET /api/work/:id 404s cleanly and title text is stored, never executed", async (t) => {
  const root = fixture(t);
  const client = await serve(t, root);
  const missing = await client.get("/api/work/NOPE-0001");
  assert.equal(missing.status, 400);
  assert.match((await missing.json()).error, /not found/);

  const dangerousTitle = "; rm -rf / #`echo pwned`";
  await client.post("/api/work", { title: dangerousTitle, id: "WI-SAFE" });
  const shown = await client.get("/api/work/WI-SAFE");
  assert.equal((await shown.json()).title, dangerousTitle);
  assert.equal(fs.existsSync("/tmp/pwned"), false);
});

test("GET /api/validate reflects repository validation state", async (t) => {
  const root = fixture(t);
  const client = await serve(t, root);
  const response = await client.get("/api/validate");
  assert.equal(response.status, 200);
  const body = await response.json();
  assert.equal(body.valid, true);
  assert.deepEqual(body.findings, []);
});

test("unknown routes 404 and static assets are served from the shared web/ directory", async (t) => {
  const root = fixture(t);
  const client = await serve(t, root);
  const missingRoute = await client.get("/api/not-a-route");
  assert.equal(missingRoute.status, 404);

  const page = await client.get("/");
  assert.equal(page.status, 200);
  assert.match(page.headers.get("content-type") ?? "", /text\/html/);
  assert.match(await page.text(), /Work Backlog/);
});

test("POST /api/work accepts a description, and PATCH-style update changes it later", async (t) => {
  const root = fixture(t);
  const client = await serve(t, root);
  await client.post("/api/work", { title: "Investigate payload growth", description: "Grows superlinearly." });
  const shown = await client.get("/api/work/WI-0001");
  assert.equal((await shown.json()).description, "Grows superlinearly.");

  const updated = await client.post("/api/work/WI-0001/update", {
    title: "Investigate payload growth (root cause)",
    description: "Narrowed to serialization layer.",
    tags: ["wasm", "perf"],
    priority: "high"
  });
  assert.equal(updated.status, 200);
  const body = await updated.json();
  assert.equal(body.title, "Investigate payload growth (root cause)");
  assert.equal(body.description, "Narrowed to serialization layer.");
  assert.deepEqual(body.tags, ["wasm", "perf"]);
  assert.equal(body.priority, "high");
});

test("POST /api/work/:id/attachments uploads multiple files, supports a custom name, and files download byte-for-byte", async (t) => {
  const root = fixture(t);
  const client = await serve(t, root);
  await client.post("/api/work", { title: "Ship it", id: "WI-SHIP" });

  const form = new FormData();
  form.append("file", fileField("first content", "notes.md"));
  form.append("file", fileField("second content", "original-name.txt"), "renamed.txt");
  const uploaded = await client.postForm("/api/work/WI-SHIP/attachments", form);
  const body = await uploaded.json();
  assert.equal(uploaded.status, 200, JSON.stringify(body));
  assert.equal(body.attachments.length, 2);
  assert.equal(body.attachments[0].name, "notes.md");
  assert.equal(body.attachments[1].name, "renamed.txt");
  assert.equal(body.attachments[0].size, "first content".length);
  assert.ok(!("file" in body.attachments[0]), "internal storage filename must not leak over the API");

  const download = await client.get(`/api/work/WI-SHIP/attachments/${body.attachments[1].id}`);
  assert.equal(download.status, 200);
  assert.match(download.headers.get("content-disposition") ?? "", /renamed\.txt/);
  assert.equal(await download.text(), "second content");
});

test("attaching the same display name twice keeps both files distinct on disk", async (t) => {
  const root = fixture(t);
  const client = await serve(t, root);
  await client.post("/api/work", { title: "Dup names", id: "WI-DUP" });

  for (const content of ["version one", "version two"]) {
    const form = new FormData();
    form.append("file", fileField(content, "same-name.txt"));
    assert.equal((await client.postForm("/api/work/WI-DUP/attachments", form)).status, 200);
  }
  const shown = await (await client.get("/api/work/WI-DUP")).json();
  assert.equal(shown.attachments.length, 2);
  assert.deepEqual(shown.attachments.map((a) => a.name), ["same-name.txt", "same-name.txt"]);
  const contents = await Promise.all(shown.attachments.map((a) => client.get(`/api/work/WI-DUP/attachments/${a.id}`).then((r) => r.text())));
  assert.deepEqual(contents.sort(), ["version one", "version two"]);
});

test("downloading an unknown attachment 404s", async (t) => {
  const root = fixture(t);
  const client = await serve(t, root);
  await client.post("/api/work", { title: "No attachments", id: "WI-EMPTY" });
  const missing = await client.get("/api/work/WI-EMPTY/attachments/ATT-1");
  assert.equal(missing.status, 404);
});
