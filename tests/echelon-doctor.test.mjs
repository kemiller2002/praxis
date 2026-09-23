import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";

const repository = path.resolve(import.meta.dirname, "..");
const doctorScript = path.join(repository, "bin", "echelon.sh");

function makeEnvironment(t, { manifestPraxis = "3.3.0", includeBinOnPath = true } = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "echelon-doctor-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));

  const home = path.join(root, "home");
  const bin = path.join(home, "bin");
  const tools = path.join(home, "tools");
  const project = path.join(root, "project");

  fs.mkdirSync(bin, { recursive: true });
  fs.mkdirSync(project, { recursive: true });

  for (const [tool, version] of [["ordo", "1.4.0"], ["praxis", "3.3.0"]]) {
    const versionRoot = path.join(tools, tool, version);
    fs.mkdirSync(versionRoot, { recursive: true });
    fs.writeFileSync(path.join(versionRoot, "VERSION"), version + "\n");
    fs.symlinkSync(versionRoot, path.join(tools, tool, "current"), "dir");
  }

  fs.mkdirSync(path.join(tools, "limen", "0.9.0"), { recursive: true });

  const commands = {
    ordo: "1.4.0",
    sde: "1.4.0",
    praxis: "3.3.0",
    ros: "3.3.0"
  };

  for (const [name, version] of Object.entries(commands)) {
    const script = path.join(bin, name);
    fs.writeFileSync(script, "#!/bin/sh\nprintf '%s\\n' '" + version + "'\n", { mode: 0o755 });
  }

  fs.writeFileSync(path.join(bin, "echelon"), "#!/bin/sh\nexit 0\n", { mode: 0o755 });

  fs.mkdirSync(path.join(project, ".echelon"), { recursive: true });
  fs.writeFileSync(
    path.join(project, ".echelon", "toolchain.json"),
    JSON.stringify({ schemaVersion: 1, ordo: "1.4.0", praxis: manifestPraxis }, null, 2) + "\n"
  );

  spawnSync("git", ["init", "-q"], { cwd: project });

  const basePath = process.env.PATH ?? "";
  const env = {
    ...process.env,
    ECHELON_HOME: home,
    PATH: includeBinOnPath ? bin + path.delimiter + basePath : basePath
  };

  return { root, home, bin, tools, project, env };
}

function runDoctor(environment, args = []) {
  return spawnSync("sh", [doctorScript, "doctor", ...args], {
    cwd: environment.project,
    env: environment.env,
    encoding: "utf8"
  });
}

test("echelon doctor reports a healthy pinned toolchain", { skip: process.platform === "win32" }, (t) => {
  const environment = makeEnvironment(t);
  const result = runDoctor(environment);

  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.match(result.stdout, /Echelon Doctor/);
  assert.match(result.stdout, /ordo active\s+1\.4\.0/);
  assert.match(result.stdout, /praxis active\s+3\.3\.0/);
  assert.match(result.stdout, /Other installed tools\s+limen/);
  assert.match(result.stdout, /Ordo requirement\s+1\.4\.0/);
  assert.match(result.stdout, /Praxis requirement\s+3\.3\.0/);
  assert.match(result.stdout, /Errors:\s+0/);
  assert.match(result.stdout, /Warnings:\s+0/);
  assert.match(result.stdout, /Environment healthy\./);
});

test("echelon doctor treats PATH absence as a warning, not an error", { skip: process.platform === "win32" }, (t) => {
  const environment = makeEnvironment(t, { includeBinOnPath: false });
  const result = runDoctor(environment);

  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.match(result.stdout, /PATH\s+.*is not on PATH/);
  assert.match(result.stdout, /Errors:\s+0/);
  assert.match(result.stdout, /Warnings:\s+1/);
  assert.match(result.stdout, /Environment usable with warnings\./);
});

test("echelon doctor fails when the active version violates the repository manifest", { skip: process.platform === "win32" }, (t) => {
  const environment = makeEnvironment(t, { manifestPraxis: "9.9.9" });
  const result = runDoctor(environment);

  assert.equal(result.status, 1, result.stderr || result.stdout);
  assert.match(result.stdout, /Praxis requirement\s+required 9\.9\.9; active 3\.3\.0/);
  assert.match(result.stdout, /Environment requires attention\./);
  assert.match(result.stdout, /echelon doctor --fix/);
});

test("echelon doctor --verbose exposes activation and working paths", { skip: process.platform === "win32" }, (t) => {
  const environment = makeEnvironment(t);
  const result = runDoctor(environment, ["--verbose"]);

  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.match(result.stdout, /Paths/);
  assert.match(result.stdout, /Binary directory/);
  assert.match(result.stdout, /Tools directory/);
  assert.match(result.stdout, /Working directory/);
  assert.match(result.stdout, /Ordo activation/);
  assert.match(result.stdout, /Praxis activation/);
});

test("echelon doctor --fix is a safe no-op when the toolchain is already healthy", { skip: process.platform === "win32" }, (t) => {
  const environment = makeEnvironment(t);
  const result = runDoctor(environment, ["--fix"]);

  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.match(result.stdout, /Repairs/);
  assert.match(result.stdout, /Ordo\s+no mechanical repair needed/);
  assert.match(result.stdout, /Praxis\s+no mechanical repair needed/);
  assert.match(result.stdout, /Environment healthy\./);
});


test("echelon doctor resolves manifest and repository state from a nested directory", { skip: process.platform === "win32" }, (t) => {
  const environment = makeEnvironment(t);
  const nested = path.join(environment.project, "src", "feature");
  fs.mkdirSync(nested, { recursive: true });

  const result = spawnSync("sh", [doctorScript, "doctor"], {
    cwd: nested,
    env: environment.env,
    encoding: "utf8"
  });

  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.match(result.stdout, /Toolchain manifest\s+.*\.echelon\/toolchain\.json/);
  assert.match(result.stdout, /Praxis requirement\s+3\.3\.0/);
  assert.match(result.stdout, /Environment healthy\./);
});
