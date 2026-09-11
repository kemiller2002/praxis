import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { internal as launcherInternal } from "../starter/greenfield/tools/ros_fs_launcher.mjs";
import {
  createWorkInRepo,
  listRepos,
  listWorkAcrossRepos,
  registerRepo,
  unregisterRepo
} from "../tools/ros_hub_cli.mjs";
import { createServer } from "../tools/ros_hub_server.mjs";

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const packageVersion = JSON.parse(fs.readFileSync(path.join(repository, "package.json"), "utf8")).version;
const fsharpCli = path.join(repository, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

// Every spoke repo's own ./ros is now the F# launcher (DF-ROS-2026-A032),
// which ros_hub_cli.mjs's runRepoCli shells out to. Node's CLI modules are
// retained in this repository only as the web server's internal dependency
// (DF-ROS-2026-A033) and are no longer scaffolded into new spoke repos, so
// the delegator seeded here execs the real, already-built F# CLI binary
// directly (inheriting execFileSync's cwd, exactly like the real launcher
// would after acquiring a real release binary) rather than standing in with
// Node -- this is a real spoke's real backend, not a Node stand-in.
const spokeCacheDir = fs.mkdtempSync(path.join(os.tmpdir(), "ros-hub-spoke-cache-"));
process.env.ROS_FS_CACHE_DIR = spokeCacheDir;
test.after(() => fs.rmSync(spokeCacheDir, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 }));
const spokeRid = launcherInternal.resolveRid();
assert.ok(spokeRid, "this test host's platform/arch must resolve to a known RID");
assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
const spokeBinaryPath = launcherInternal.cacheDirectory(packageVersion, spokeRid);
fs.mkdirSync(spokeBinaryPath, { recursive: true });
fs.writeFileSync(
  path.join(spokeBinaryPath, launcherInternal.binaryName(spokeRid)),
  `#!/bin/sh\nexec dotnet "${fsharpCli}" "$@"\n`,
  { mode: 0o755 }
);

function spokeRepo(t, project) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-spoke-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 }));
  initializeProject({ target: root, project });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function hubRoot(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-hub-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 }));
  return root;
}

test("registering a repo reads its repository.id and rejects duplicates by path or id", (t) => {
  const hub = hubRoot(t);
  const repoA = spokeRepo(t, "Repo A");
  const entry = registerRepo(hub, repoA, { name: "Alpha" });
  assert.equal(entry.name, "Alpha");
  assert.equal(entry.path, repoA);
  assert.ok(entry.id, "id derived from the spoke's ros.json repository.id");

  assert.throws(() => registerRepo(hub, repoA), /already registered/);
  assert.deepEqual(listRepos(hub).map((r) => r.path), [repoA]);
});

test("registration rejects a second repo whose repository.id collides with an already-registered one", (t) => {
  const hub = hubRoot(t);
  const repoA = spokeRepo(t, "Same Name");
  const repoB = spokeRepo(t, "Same Name"); // deriveProjectName + slugify produce the same repository.id
  registerRepo(hub, repoA);
  assert.throws(() => registerRepo(hub, repoB), /already registered/);
});

test("registration rejects a non-ROS directory and a directory without ./ros", (t) => {
  const hub = hubRoot(t);
  const plain = fs.mkdtempSync(path.join(os.tmpdir(), "not-ros-"));
  t.after(() => fs.rmSync(plain, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 }));
  assert.throws(() => registerRepo(hub, plain), /not a ROS repository/);
});

test("create shells out to the spoke's own ros add and the item lands in that repo's real queue.json", (t) => {
  const hub = hubRoot(t);
  const repoA = spokeRepo(t, "Repo A");
  const entry = registerRepo(hub, repoA);

  const design = path.join(repoA, "design-note.md");
  fs.writeFileSync(design, "hello from the hub\n");

  const item = createWorkInRepo(hub, entry.id, {
    title: "Investigate payload growth",
    tags: ["wasm", "perf"],
    priority: "high",
    description: "Grows superlinearly.",
    files: [{ sourcePath: design, name: "notes.md" }]
  });

  assert.equal(item.id, "WI-0001");
  assert.equal(item.repoId, entry.id);
  assert.equal(item.attachments[0].name, "notes.md");

  const queue = JSON.parse(fs.readFileSync(path.join(repoA, ".ros", "work", "queue.json"), "utf8"));
  assert.equal(queue.items[0].title, "Investigate payload growth");
  assert.equal(queue.items[0].priority, "high");
  assert.deepEqual(queue.items[0].tags, ["wasm", "perf"]);
});

test("create against an unknown repo id fails clearly, and the hub never writes into an unregistered path", (t) => {
  const hub = hubRoot(t);
  assert.throws(() => createWorkInRepo(hub, "nope", { title: "x" }), /no registered repository/);
});

test("aggregated listing merges rows across repos and isolates one broken repo's error", (t) => {
  const hub = hubRoot(t);
  const repoA = spokeRepo(t, "Repo A");
  const repoB = spokeRepo(t, "Repo B");
  const entryA = registerRepo(hub, repoA);
  const entryB = registerRepo(hub, repoB);
  createWorkInRepo(hub, entryA.id, { title: "A item" });
  createWorkInRepo(hub, entryB.id, { title: "B item" });

  const combined = listWorkAcrossRepos(hub, { status: "captured" });
  const byRepo = Object.fromEntries(combined.map((row) => [row.repoId, row]));
  assert.equal(byRepo[entryA.id].title, "A item");
  assert.equal(byRepo[entryB.id].title, "B item");

  // Simulate a spoke that has since moved/vanished.
  const movedAway = `${repoB}-moved`;
  fs.renameSync(repoB, movedAway);
  t.after(() => fs.rmSync(movedAway, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 }));

  const afterMove = listWorkAcrossRepos(hub);
  const stillOk = afterMove.find((row) => row.repoId === entryA.id && !row.error);
  const broken = afterMove.find((row) => row.repoId === entryB.id);
  assert.ok(stillOk, "the healthy repo's rows are unaffected by the broken one");
  assert.ok(broken.error && /no longer exists/.test(broken.error));
});

test("unregister removes a repo and create against it fails afterward", (t) => {
  const hub = hubRoot(t);
  const repoA = spokeRepo(t, "Repo A");
  const entry = registerRepo(hub, repoA);
  unregisterRepo(hub, entry.id);
  assert.deepEqual(listRepos(hub), []);
  assert.throws(() => createWorkInRepo(hub, entry.id, { title: "x" }), /no registered repository/);
});

async function serve(t, root) {
  const server = createServer(root);
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  t.after(() => new Promise((resolve) => server.close(resolve)));
  const { port } = server.address();
  const base = `http://127.0.0.1:${port}`;
  return {
    get: (urlPath) => fetch(`${base}${urlPath}`),
    post: (urlPath, body) => fetch(`${base}${urlPath}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body ?? {}) }),
    postForm: (urlPath, formData) => fetch(`${base}${urlPath}`, { method: "POST", body: formData }),
    del: (urlPath) => fetch(`${base}${urlPath}`, { method: "DELETE" })
  };
}

test("hub server: register, create (JSON and multipart with a file), aggregate, unregister", async (t) => {
  const hub = hubRoot(t);
  const repoA = spokeRepo(t, "Repo A");
  const client = await serve(t, hub);

  const registered = await client.post("/api/repos", { path: repoA, name: "Alpha" });
  const repo = await registered.json();
  assert.equal(registered.status, 200, JSON.stringify(repo));

  const created = await client.post(`/api/repos/${repo.id}/work`, { title: "Investigate growth", tags: ["wasm"], priority: "high" });
  const createdBody = await created.json();
  assert.equal(created.status, 200, JSON.stringify(createdBody));
  assert.equal(createdBody.id, "WI-0001");

  const form = new FormData();
  form.append("title", "With an attachment");
  form.append("tags", "cleanup,wasm");
  form.append("file", new File(["hub upload content"], "spec.md", { type: "text/markdown" }));
  const createdWithFile = await client.postForm(`/api/repos/${repo.id}/work`, form);
  const withFileBody = await createdWithFile.json();
  assert.equal(createdWithFile.status, 200, JSON.stringify(withFileBody));
  assert.equal(withFileBody.attachments[0].name, "spec.md");
  assert.deepEqual(withFileBody.tags, ["cleanup", "wasm"]);

  const uploadedTemp = fs.readdirSync(os.tmpdir()).filter((name) => name.startsWith("ros-hub-upload-"));
  assert.equal(uploadedTemp.length, 0, "temp upload files must be cleaned up after use");

  const aggregated = await client.get("/api/work");
  assert.equal(aggregated.status, 200);
  const rows = await aggregated.json();
  assert.equal(rows.filter((row) => row.id.startsWith("WI-")).length, 2);

  const unregistered = await client.del(`/api/repos/${repo.id}`);
  assert.equal(unregistered.status, 200);
  assert.deepEqual(await (await client.get("/api/repos")).json(), []);
});
