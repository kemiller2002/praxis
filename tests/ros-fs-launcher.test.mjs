import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { run, unsupportedPlatformMessage, internal } from "../lib/ros-fs-launcher.mjs";

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const packageVersion = JSON.parse(
  fs.readFileSync(path.join(repository, "package.json"), "utf8")
).version;

function temporaryDirectory(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "ros-fs-launcher-"));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 }));
  return directory;
}

async function startFakeReleaseServer(t, { assetContent, badChecksum = false, hitCounts }) {
  const rid = internal.resolveRid();
  const assetName = internal.releaseAssetName(rid);
  const realHash = crypto.createHash("sha256").update(assetContent).digest("hex");
  const checksumsText = `${badChecksum ? "0".repeat(64) : realHash}  ${assetName}\n`;

  const server = http.createServer((req, res) => {
    hitCounts[req.url] = (hitCounts[req.url] ?? 0) + 1;
    if (req.url === "/checksums.txt") {
      res.writeHead(200, { "content-type": "text/plain" });
      res.end(checksumsText);
      return;
    }
    if (req.url === `/${assetName}`) {
      res.writeHead(200, { "content-type": "application/octet-stream" });
      res.end(assetContent);
      return;
    }
    res.writeHead(404);
    res.end("not found");
  });

  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  t.after(() => new Promise((resolve) => server.close(resolve)));
  const { port } = server.address();
  return { baseUrl: `http://127.0.0.1:${port}`, assetName, rid };
}

test("resolveRid maps known platform/arch pairs and rejects unknown ones", () => {
  assert.equal(internal.resolveRid({ platform: "linux", arch: "x64" }), "linux-x64");
  assert.equal(internal.resolveRid({ platform: "linux", arch: "arm64" }), "linux-arm64");
  assert.equal(internal.resolveRid({ platform: "darwin", arch: "x64" }), "osx-x64");
  assert.equal(internal.resolveRid({ platform: "darwin", arch: "arm64" }), "osx-arm64");
  assert.equal(internal.resolveRid({ platform: "win32", arch: "x64" }), "win-x64");
  assert.equal(internal.resolveRid({ platform: "sunos", arch: "x64" }), null);
});

test("unsupportedPlatformMessage names every supported platform and a source-build fallback", () => {
  const message = unsupportedPlatformMessage({ platform: "sunos", arch: "sparc64" });
  assert.match(message, /sunos\/sparc64/);
  assert.match(message, /linux\/x64/);
  assert.match(message, /npm run build:fsharp/);
});

test("releaseAssetName adds .exe only for the win- RID family", () => {
  assert.equal(internal.releaseAssetName("linux-x64"), "ros-fs-linux-x64");
  assert.equal(internal.releaseAssetName("osx-arm64"), "ros-fs-osx-arm64");
  assert.equal(internal.releaseAssetName("win-x64"), "ros-fs-win-x64.exe");
});

test("parseChecksums reads sha256sum-style lines and ignores blanks", () => {
  const hash = "a".repeat(64);
  const parsed = internal.parseChecksums(`\n${hash}  ros-fs-linux-x64\n${hash} *ros-fs-win-x64.exe\n\n`);
  assert.equal(parsed.get("ros-fs-linux-x64"), hash);
  assert.equal(parsed.get("ros-fs-win-x64.exe"), hash);
  assert.equal(parsed.size, 2);
});

test("run downloads, verifies, caches, and executes the platform binary on a cache miss", async (t) => {
  const cacheDir = temporaryDirectory(t);
  const script = "#!/bin/sh\necho fake-ros-fs-output\nexit 7\n";
  const hitCounts = {};
  const { baseUrl, rid } = await startFakeReleaseServer(t, { assetContent: script, hitCounts });

  const previousBase = process.env.ROS_FS_RELEASE_BASE_URL;
  const previousCache = process.env.ROS_FS_CACHE_DIR;
  process.env.ROS_FS_RELEASE_BASE_URL = baseUrl;
  process.env.ROS_FS_CACHE_DIR = cacheDir;
  t.after(() => {
    if (previousBase === undefined) delete process.env.ROS_FS_RELEASE_BASE_URL;
    else process.env.ROS_FS_RELEASE_BASE_URL = previousBase;
    if (previousCache === undefined) delete process.env.ROS_FS_CACHE_DIR;
    else process.env.ROS_FS_CACHE_DIR = previousCache;
  });

  const logs = [];
  const status = await run([], { log: (message) => logs.push(message) });

  assert.equal(status, 7);
  assert.ok(logs.some((line) => line.includes("not cached; downloading")));

  const cachedBinary = path.join(cacheDir, packageVersion, rid, internal.binaryName(rid));
  assert.ok(fs.existsSync(cachedBinary));
  assert.equal(fs.statSync(cachedBinary).mode & 0o111, 0o111, "cached binary must be executable");

  assert.equal(hitCounts["/checksums.txt"], 1);
  assert.equal(hitCounts[`/${internal.releaseAssetName(rid)}`], 1);
});

test("run reuses the cached binary on a second invocation without hitting the server again", async (t) => {
  const cacheDir = temporaryDirectory(t);
  const script = "#!/bin/sh\necho cached-run\nexit 0\n";
  const hitCounts = {};
  const { baseUrl } = await startFakeReleaseServer(t, { assetContent: script, hitCounts });

  const previousBase = process.env.ROS_FS_RELEASE_BASE_URL;
  const previousCache = process.env.ROS_FS_CACHE_DIR;
  process.env.ROS_FS_RELEASE_BASE_URL = baseUrl;
  process.env.ROS_FS_CACHE_DIR = cacheDir;
  t.after(() => {
    if (previousBase === undefined) delete process.env.ROS_FS_RELEASE_BASE_URL;
    else process.env.ROS_FS_RELEASE_BASE_URL = previousBase;
    if (previousCache === undefined) delete process.env.ROS_FS_CACHE_DIR;
    else process.env.ROS_FS_CACHE_DIR = previousCache;
  });

  const first = await run([], { log: () => {} });
  assert.equal(first, 0);
  const totalHitsAfterFirst = Object.values(hitCounts).reduce((a, b) => a + b, 0);

  const second = await run([], { log: () => {} });
  assert.equal(second, 0);
  const totalHitsAfterSecond = Object.values(hitCounts).reduce((a, b) => a + b, 0);

  assert.equal(totalHitsAfterSecond, totalHitsAfterFirst, "no additional network requests on a cache hit");
});

test("run rejects a downloaded binary whose checksum does not match and does not cache it", async (t) => {
  const cacheDir = temporaryDirectory(t);
  const script = "#!/bin/sh\necho should-not-run\nexit 0\n";
  const hitCounts = {};
  const { baseUrl, rid } = await startFakeReleaseServer(t, { assetContent: script, badChecksum: true, hitCounts });

  const previousBase = process.env.ROS_FS_RELEASE_BASE_URL;
  const previousCache = process.env.ROS_FS_CACHE_DIR;
  process.env.ROS_FS_RELEASE_BASE_URL = baseUrl;
  process.env.ROS_FS_CACHE_DIR = cacheDir;
  t.after(() => {
    if (previousBase === undefined) delete process.env.ROS_FS_RELEASE_BASE_URL;
    else process.env.ROS_FS_RELEASE_BASE_URL = previousBase;
    if (previousCache === undefined) delete process.env.ROS_FS_CACHE_DIR;
    else process.env.ROS_FS_CACHE_DIR = previousCache;
  });

  const logs = [];
  const status = await run([], { log: (message) => logs.push(message) });

  assert.equal(status, 1);
  assert.ok(logs.some((line) => line.includes("checksum mismatch")));
  const cachedBinary = path.join(cacheDir, packageVersion, rid, internal.binaryName(rid));
  assert.ok(!fs.existsSync(cachedBinary), "a checksum-mismatched download must never be cached");
});

test("run reports a clear error when checksums.txt has no entry for this platform's asset", async (t) => {
  const cacheDir = temporaryDirectory(t);
  const hitCounts = {};
  const server = http.createServer((req, res) => {
    hitCounts[req.url] = (hitCounts[req.url] ?? 0) + 1;
    if (req.url === "/checksums.txt") {
      res.writeHead(200, { "content-type": "text/plain" });
      res.end("");
      return;
    }
    res.writeHead(404);
    res.end("not found");
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  t.after(() => new Promise((resolve) => server.close(resolve)));
  const { port } = server.address();

  const previousBase = process.env.ROS_FS_RELEASE_BASE_URL;
  const previousCache = process.env.ROS_FS_CACHE_DIR;
  process.env.ROS_FS_RELEASE_BASE_URL = `http://127.0.0.1:${port}`;
  process.env.ROS_FS_CACHE_DIR = cacheDir;
  t.after(() => {
    if (previousBase === undefined) delete process.env.ROS_FS_RELEASE_BASE_URL;
    else process.env.ROS_FS_RELEASE_BASE_URL = previousBase;
    if (previousCache === undefined) delete process.env.ROS_FS_CACHE_DIR;
    else process.env.ROS_FS_CACHE_DIR = previousCache;
  });

  const logs = [];
  const status = await run([], { log: (message) => logs.push(message) });

  assert.equal(status, 1);
  assert.ok(logs.some((line) => line.includes("no entry for")));
});
