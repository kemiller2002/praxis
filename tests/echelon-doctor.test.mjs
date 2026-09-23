import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const doctorScript = path.join(repository, "bin", "echelon.sh");

function makeEnvironment(t, { manifestPraxis = "3.4.0", includeBinOnPath = true } = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "echelon-doctor-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));

  const home = path.join(root, "home");
  const bin = path.join(home, "bin");
  const tools = path.join(home, "tools");
  const project = path.join(root, "project");

  fs.mkdirSync(bin, { recursive: true });
  fs.mkdirSync(project, { recursive: true });

  for (const [tool, version] of [["ordo", "1.4.0"], ["praxis", "3.4.0"]]) {
    const versionRoot = path.join(tools, tool, version);
    fs.mkdirSync(versionRoot, { recursive: true });
    fs.writeFileSync(path.join(versionRoot, "VERSION"), version + "\n");
    fs.symlinkSync(versionRoot, path.join(tools, tool, "current"), "dir");
  }

  fs.mkdirSync(path.join(tools, "limen", "0.9.0"), { recursive: true });

  const commands = {
    ordo: "1.4.0",
    sde: "1.4.0",
    praxis: "3.4.0",
    ros: "3.4.0"
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

  fs.writeFileSync(
    path.join(project, ".echelon", "visual-engineering.json"),
    JSON.stringify({ schemaVersion: 1, tool: "visual-engineering", installedVersion: "1.0.0" }, null, 2) + "\n"
  );
  fs.writeFileSync(
    path.join(project, ".echelon", "limen.json"),
    JSON.stringify({ schemaVersion: 1, tool: "limen", package: "@echelon-foundry/typescript-wasm-kernel", installedVersion: "0.6.2" }, null, 2) + "\n"
  );

  const npmPackage = path.join(project, "node_modules", "@echelon-foundry", "typescript-wasm-kernel");
  fs.mkdirSync(npmPackage, { recursive: true });
  fs.writeFileSync(
    path.join(npmPackage, "package.json"),
    JSON.stringify({ name: "@echelon-foundry/typescript-wasm-kernel", version: "0.6.2" }, null, 2) + "\n"
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

function runInventory(environment, args = []) {
  return spawnSync("sh", [doctorScript, "inventory", ...args], {
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
  assert.match(result.stdout, /praxis active\s+3\.4\.0/);
  assert.match(result.stdout, /Other installed tools\s+limen \(0\.9\.0\)/);
  assert.match(result.stdout, /Ordo requirement\s+1\.4\.0/);
  assert.match(result.stdout, /Praxis requirement\s+3\.4\.0/);
  assert.match(result.stdout, /Repository components/);
  assert.match(result.stdout, /visual-engineering\s+1\.0\.0/);
  assert.match(result.stdout, /limen\s+0\.6\.2/);
  assert.match(result.stdout, /Installed Echelon npm packages/);
  assert.match(result.stdout, /@echelon-foundry\/typescript-wasm-kernel\s+0\.6\.2/);
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
  assert.match(result.stdout, /Praxis requirement\s+3\.4\.0/);
  assert.match(result.stdout, /Environment healthy\./);
});


test("echelon doctor delegates repository validation from the Git root", { skip: process.platform === "win32" }, (t) => {
  const environment = makeEnvironment(t);
  fs.mkdirSync(path.join(environment.project, ".sde"), { recursive: true });
  fs.mkdirSync(path.join(environment.project, ".ros"), { recursive: true });

  fs.writeFileSync(
    path.join(environment.bin, "ordo"),
    "#!/bin/sh\nif [ \"$1\" = verify ]; then [ \"$PWD\" = \"" + environment.project + "\" ]; exit $?; fi\nprintf '%s\\n' '1.4.0'\n",
    { mode: 0o755 }
  );
  fs.writeFileSync(
    path.join(environment.bin, "praxis"),
    "#!/bin/sh\nif [ \"$1\" = validate ]; then [ \"$PWD\" = \"" + environment.project + "\" ]; exit $?; fi\nprintf '%s\\n' '3.4.0'\n",
    { mode: 0o755 }
  );

  const nested = path.join(environment.project, "src", "nested");
  fs.mkdirSync(nested, { recursive: true });
  const result = spawnSync("sh", [doctorScript, "doctor"], {
    cwd: nested,
    env: environment.env,
    encoding: "utf8"
  });

  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.match(result.stdout, /Ordo repository\s+verify passed/);
  assert.match(result.stdout, /Praxis repository\s+validation passed/);
});


test("echelon doctor --json emits the stable agent-readable health contract", { skip: process.platform === "win32" }, (t) => {
  const environment = makeEnvironment(t);
  const result = runDoctor(environment, ["--json"]);

  assert.equal(result.status, 0, result.stderr || result.stdout);
  const report = JSON.parse(result.stdout);
  assert.equal(report.schemaVersion, 1);
  assert.equal(report.tool, "echelon");
  assert.equal(report.command, "doctor");
  assert.equal(report.health, "healthy");
  assert.equal(report.exitCode, 0);
  assert.equal(report.summary.errors, 0);
  assert.equal(report.summary.warnings, 0);
  assert.deepEqual(report.findings, []);
  assert.equal(report.machine.pathConfigured, true);
  assert.equal(report.repository.requirements.ordo, "1.4.0");
  assert.equal(report.repository.requirements.praxis, "3.4.0");

  const praxis = report.nativeTools.find((item) => item.name === "praxis");
  assert.equal(praxis.activeVersion, "3.4.0");
  assert.ok(praxis.installedVersions.includes("3.4.0"));

  const visual = report.repository.components.find((item) => item.tool === "visual-engineering");
  assert.equal(visual.installedVersion, "1.0.0");

  const limen = report.repository.components.find((item) => item.tool === "limen");
  assert.equal(limen.installedVersion, "0.6.2");

  const npmLimen = report.repository.npmPackages.find(
    (item) => item.package === "@echelon-foundry/typescript-wasm-kernel"
  );
  assert.equal(npmLimen.version, "0.6.2");
});

test("echelon doctor --json carries stable finding codes for warnings and errors", { skip: process.platform === "win32" }, (t) => {
  const warningEnvironment = makeEnvironment(t, { includeBinOnPath: false });
  const warningResult = runDoctor(warningEnvironment, ["--json"]);
  assert.equal(warningResult.status, 0, warningResult.stderr || warningResult.stdout);
  const warningReport = JSON.parse(warningResult.stdout);
  assert.equal(warningReport.health, "warning");
  assert.ok(warningReport.findings.some((finding) => finding.code === "ECHELON-DOC-001"));

  const errorEnvironment = makeEnvironment(t, { manifestPraxis: "9.9.9" });
  const errorResult = runDoctor(errorEnvironment, ["--json"]);
  assert.equal(errorResult.status, 1, errorResult.stderr || errorResult.stdout);
  const errorReport = JSON.parse(errorResult.stdout);
  assert.equal(errorReport.health, "error");
  assert.ok(errorReport.findings.some((finding) => finding.code === "ECHELON-DOC-034"));
});

test("echelon doctor detects a command alias that reports the wrong active version", { skip: process.platform === "win32" }, (t) => {
  const environment = makeEnvironment(t);
  fs.writeFileSync(
    path.join(environment.bin, "ros"),
    "#!/bin/sh\nprintf '%s\\n' '2.0.0'\n",
    { mode: 0o755 }
  );

  const result = runDoctor(environment, ["--json"]);
  assert.equal(result.status, 1, result.stderr || result.stdout);
  const report = JSON.parse(result.stdout);
  const ros = report.commands.find((item) => item.name === "ros");
  assert.equal(ros.healthy, false);
  assert.equal(ros.version, "2.0.0");
  assert.ok(report.findings.some((finding) => finding.code === "ECHELON-DOC-021"));
});

test("echelon inventory reports native, repository and npm installations without health validation", { skip: process.platform === "win32" }, (t) => {
  const environment = makeEnvironment(t);

  const human = runInventory(environment);
  assert.equal(human.status, 0, human.stderr || human.stdout);
  assert.match(human.stdout, /Echelon Inventory/);
  assert.match(human.stdout, /visual-engineering\s+1\.0\.0/);
  assert.match(human.stdout, /@echelon-foundry\/typescript-wasm-kernel\s+0\.6\.2/);

  const machine = runInventory(environment, ["--json"]);
  assert.equal(machine.status, 0, machine.stderr || machine.stdout);
  const report = JSON.parse(machine.stdout);
  assert.equal(report.schemaVersion, 1);
  assert.equal(report.command, "inventory");
  assert.ok(report.nativeTools.some((item) => item.name === "ordo"));
  assert.ok(report.repository.components.some((item) => item.tool === "limen"));
  assert.ok(report.repository.npmPackages.some(
    (item) => item.package === "@echelon-foundry/typescript-wasm-kernel"
  ));
});

test("echelon doctor refuses to mix repair side effects with JSON stdout", { skip: process.platform === "win32" }, (t) => {
  const environment = makeEnvironment(t);
  const result = runDoctor(environment, ["--fix", "--json"]);

  assert.equal(result.status, 2);
  assert.match(result.stderr, /cannot be combined/);
  assert.equal(result.stdout, "");
});
