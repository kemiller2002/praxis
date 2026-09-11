import assert from "node:assert/strict";
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
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");
const valid = fixtureManifest.cases.find(({ id }) => id === "valid-all-kinds");
const invalid = fixtureManifest.cases.find(({ id }) => id === "invalid-mixed");
const registryNames = Object.keys(valid.registrySha256).sort();

function temporaryFixture(t, name) {
  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), `ros-fsharp-differential-${name}-`));
  const root = path.join(temporaryRoot, name);
  fs.cpSync(path.join(fixtureRoot, name), root, { recursive: true });
  t.after(() => fs.rmSync(temporaryRoot, { recursive: true, force: true }));
  return root;
}

function runFsharp(commandArgs) {
  return spawnSync("dotnet", [fsharpCli, ...commandArgs], {
    cwd: repositoryRoot,
    encoding: "utf8"
  });
}

function artifactFindings(findings) {
  return findings
    .filter(({ path: findingPath }) => findingPath.startsWith("research/") || findingPath.startsWith("missions/"))
    .map(({ path: findingPath, field, message }) => ({ path: findingPath, field, message }));
}

test("F# artifact slice matches Node registry bytes and characterized findings", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");

  const nodeRoot = temporaryFixture(t, valid.root);
  const fsharpRoot = temporaryFixture(t, valid.root);
  fs.rmSync(path.join(nodeRoot, "registries"), { recursive: true, force: true });
  fs.rmSync(path.join(fsharpRoot, "registries"), { recursive: true, force: true });

  const nodeBuild = buildRegistries(nodeRoot);
  assert.equal(nodeBuild.findings.length, 0);
  assert.equal(nodeBuild.changed, registryNames.length);

  const fsharpBuild = runFsharp(["--root", fsharpRoot, "registry", "build"]);
  assert.equal(fsharpBuild.status, 0, fsharpBuild.stderr);

  for (const name of registryNames) {
    const expected = fs.readFileSync(path.join(nodeRoot, "registries", name), "utf8");
    const actual = fs.readFileSync(path.join(fsharpRoot, "registries", name), "utf8");
    assert.equal(actual, expected, name);
  }

  const repeat = runFsharp(["--root", fsharpRoot, "registry", "build"]);
  assert.equal(repeat.status, 0, repeat.stderr);
  assert.match(repeat.stdout, /^0 registry file\(s\) changed\n$/);

  const nodeInvalid = artifactFindings(validate(temporaryFixture(t, invalid.root), { checkRegistries: false }));
  const fsharpInvalid = runFsharp(["--root", temporaryFixture(t, invalid.root), "artifacts", "validate", "--json"]);
  assert.equal(fsharpInvalid.status, 1, fsharpInvalid.stderr);
  assert.deepEqual(
    JSON.parse(fsharpInvalid.stdout).findings.map(({ path: findingPath, field, message }) => ({ path: findingPath, field, message })),
    nodeInvalid
  );
  assert.deepEqual(nodeInvalid, invalid.expectedFindings);
});

test("F# shadow smoke-checks the current repository without becoming its authority", () => {
  const node = spawnSync("node", [path.join(repositoryRoot, "tools", "ros_cli.mjs"), "registry", "check"], {
    cwd: repositoryRoot,
    encoding: "utf8"
  });
  assert.equal(node.status, 0, `${node.stdout}${node.stderr}`);
  assert.equal(node.stdout, "registries are current\n");

  const validation = runFsharp(["--root", repositoryRoot, "artifacts", "validate", "--json"]);
  assert.equal(validation.status, 0, validation.stderr);
  assert.deepEqual(JSON.parse(validation.stdout), { valid: true, findings: [] });

  const check = runFsharp(["--root", repositoryRoot, "registry", "check"]);
  assert.equal(check.status, 0, check.stderr);
  assert.equal(check.stdout, "registries are current\n");
});

test("F# shadow replays a Node-compatible pending artifact transaction", (t) => {
  const root = temporaryFixture(t, valid.root);
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

  const result = runFsharp(["--root", root, "registry", "build"]);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout, "0 registry file(s) changed\n");
  assert.equal(fs.readFileSync(path.join(root, registryPath), "utf8"), expected);
  assert.equal(fs.existsSync(transaction), false);
});

test("F# shadow rejects an unknown command with a usage exit code", () => {
  const result = runFsharp(["not-a-command"]);
  assert.equal(result.status, 2);
  assert.match(result.stderr, /Usage: ros-fs/);
});
