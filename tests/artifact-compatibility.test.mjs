import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { buildRegistries, validate } from "../tools/ros_cli.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fixtureRoot = path.join(repositoryRoot, "tests", "fixtures", "artifacts");
const fixtureManifest = JSON.parse(fs.readFileSync(path.join(fixtureRoot, "manifest.json"), "utf8"));
const registryNames = Object.keys(fixtureManifest.cases[0].registrySha256).sort();

function temporaryFixture(t, name) {
  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), `ros-artifact-${name}-`));
  const root = path.join(temporaryRoot, name);
  fs.cpSync(path.join(fixtureRoot, name), root, { recursive: true });
  t.after(() => {
    try {
      fs.rmSync(temporaryRoot, { recursive: true, force: true });
    } catch {
      // Cleanup best-effort: a leftover temp dir under CI I/O contention isn't a test failure.
    }
  });
  return root;
}

function sha256(file) {
  return crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex");
}

function artifactFindings(findings) {
  return findings
    .filter(({ path: findingPath }) => findingPath.startsWith("research/") || findingPath.startsWith("missions/"))
    .map(({ path: findingPath, field, message }) => ({ path: findingPath, field, message }));
}

function pythonFindings(stderr) {
  return stderr
    .split(/\r?\n/)
    .filter((line) => line.startsWith("ERROR research/") || line.startsWith("ERROR missions/"))
    .map((line) => {
      const match = /^ERROR (.*?):([^:]*): (.*)$/.exec(line);
      assert.ok(match, `unexpected Python finding: ${line}`);
      return { path: match[1], field: match[2], message: match[3] };
    });
}

test("frozen valid fixture has the preregistered registry bytes", () => {
  const valid = fixtureManifest.cases.find(({ id }) => id === "valid-all-kinds");
  for (const name of registryNames) {
    assert.equal(sha256(path.join(fixtureRoot, valid.root, "registries", name)), valid.registrySha256[name], name);
  }
});

test("Node artifact characterization matches the preregistered invalid findings", (t) => {
  const invalid = fixtureManifest.cases.find(({ id }) => id === "invalid-mixed");
  const root = temporaryFixture(t, invalid.root);
  assert.deepEqual(artifactFindings(validate(root, { checkRegistries: false })), invalid.expectedFindings);
});

test("Node and Python generate the frozen bytes and agree on invalid artifact findings", (t) => {
  const valid = fixtureManifest.cases.find(({ id }) => id === "valid-all-kinds");
  const nodeRoot = temporaryFixture(t, valid.root);
  fs.rmSync(path.join(nodeRoot, "registries"), { recursive: true, force: true });
  const nodeBuild = buildRegistries(nodeRoot);
  assert.equal(nodeBuild.findings.length, 0);
  assert.equal(nodeBuild.changed, registryNames.length);
  for (const name of registryNames) {
    assert.equal(
      fs.readFileSync(path.join(nodeRoot, "registries", name), "utf8"),
      fs.readFileSync(path.join(fixtureRoot, valid.root, "registries", name), "utf8"),
      `Node ${name}`
    );
  }

  const pythonRoot = temporaryFixture(t, valid.root);
  fs.rmSync(path.join(pythonRoot, "registries"), { recursive: true, force: true });
  const pythonBuild = spawnSync(
    "python3",
    [path.join(repositoryRoot, "tools", "ros_cli.py"), "--root", pythonRoot, "registry", "build"],
    { cwd: repositoryRoot, encoding: "utf8" }
  );
  assert.equal(pythonBuild.status, 0, pythonBuild.stderr);
  for (const name of registryNames) {
    assert.equal(
      fs.readFileSync(path.join(pythonRoot, "registries", name), "utf8"),
      fs.readFileSync(path.join(fixtureRoot, valid.root, "registries", name), "utf8"),
      `Python ${name}`
    );
  }

  const invalid = fixtureManifest.cases.find(({ id }) => id === "invalid-mixed");
  const invalidRoot = temporaryFixture(t, invalid.root);
  const pythonValidation = spawnSync(
    "python3",
    [path.join(repositoryRoot, "tools", "ros_cli.py"), "--root", invalidRoot, "validate"],
    { cwd: repositoryRoot, encoding: "utf8" }
  );
  assert.equal(pythonValidation.status, 1);
  assert.deepEqual(pythonFindings(pythonValidation.stderr), invalid.expectedFindings);
});

test("Node registry build reclaims a stale F#-compatible artifact lease", (t) => {
  const root = temporaryFixture(t, "valid-all-kinds");
  const resource = "artifact-registries";
  const hash = crypto.createHash("sha256").update(resource).digest("hex");
  const lock = path.join(root, ".ros", "locks", `${hash}.lock`);
  fs.mkdirSync(path.dirname(lock), { recursive: true });
  fs.writeFileSync(lock, JSON.stringify({
    pid: 999999,
    ownerToken: "fsharp-stale-owner",
    resource,
    acquiredAt: "2026-09-08T00:00:00.0000000Z"
  }) + "\n");
  const stale = new Date(Date.now() - 61_000);
  fs.utimesSync(lock, stale, stale);

  const result = buildRegistries(root, { dryRun: true });
  assert.equal(result.changed, 0);
  assert.equal(result.findings.length, 0);
  assert.equal(fs.existsSync(lock), false);
});

test("Node registry build replays a pending F#-compatible artifact transaction", (t) => {
  const root = temporaryFixture(t, "valid-all-kinds");
  const registryPath = "registries/evidence.json";
  const expected = fs.readFileSync(path.join(root, registryPath), "utf8");
  const transaction = path.join(root, ".ros", "transactions", "artifact-registries.json");
  fs.mkdirSync(path.dirname(transaction), { recursive: true });
  fs.writeFileSync(transaction, JSON.stringify({
    schemaVersion: "1.0.0",
    resource: "artifact-registries",
    writes: [{ path: registryPath, content: expected }]
  }) + "\n");
  fs.writeFileSync(path.join(root, registryPath), "[]\n");

  const result = buildRegistries(root);
  assert.equal(result.changed, 0);
  assert.equal(result.findings.length, 0);
  assert.equal(fs.readFileSync(path.join(root, registryPath), "utf8"), expected);
  assert.equal(fs.existsSync(transaction), false);
});
