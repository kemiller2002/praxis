import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { deriveProjectName, initializeProject, verifyProject } from "../lib/bootstrap.mjs";

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const packageVersion = JSON.parse(
  fs.readFileSync(path.join(repository, "package.json"), "utf8")
).version;

function temporaryDirectory(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "ros-bootstrap-"));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  return directory;
}

test("greenfield initialization is self-contained and immediately valid", (t) => {
  const target = temporaryDirectory(t);
  const result = initializeProject({
    target,
    project: "Communication Engineering"
  });

  assert.equal(result.packageVersion, packageVersion);
  assert.ok(result.files.length >= 60);
  assert.match(fs.readFileSync(path.join(target, "README.md"), "utf8"), /Communication Engineering/);
  assert.equal(fs.statSync(path.join(target, "ros")).mode & 0o777, 0o755);
  assert.ok(fs.existsSync(path.join(target, ".ros", "installation.json")));

  const registry = spawnSync(path.join(target, "ros"), ["registry", "check"], {
    cwd: target,
    encoding: "utf8"
  });
  assert.equal(registry.status, 0, registry.stderr || registry.stdout);
  assert.match(registry.stdout, /registries are current/);

  const validation = spawnSync(path.join(target, "ros"), ["validate"], {
    cwd: target,
    encoding: "utf8"
  });
  assert.equal(validation.status, 0, validation.stderr || validation.stdout);
  assert.match(validation.stdout, /validation passed/);
  assert.deepEqual(verifyProject({ target }).findings, []);
});

test("project name is derived from the target folder when omitted", (t) => {
  const parent = temporaryDirectory(t);
  const target = path.join(parent, "communication-engineering");
  fs.mkdirSync(target);
  const result = initializeProject({ target });

  assert.equal(result.project, "Communication Engineering");
  const installed = JSON.parse(
    fs.readFileSync(path.join(target, ".ros", "installation.json"), "utf8")
  );
  assert.equal(installed.project, "Communication Engineering");
  assert.match(fs.readFileSync(path.join(target, "PROJECT-CHARTER.md"), "utf8"), /Communication Engineering/);
});

test("project name derivation handles separators, camel case, and explicit acronyms", () => {
  assert.equal(deriveProjectName("/tmp/visual-engineering"), "Visual Engineering");
  assert.equal(deriveProjectName("/tmp/researchPublisher"), "Research Publisher");
  assert.equal(deriveProjectName("/tmp/AI Engineering"), "AI Engineering");
});

test("dry run does not create or mutate the target", (t) => {
  const parent = temporaryDirectory(t);
  const target = path.join(parent, "not-created");
  const result = initializeProject({
    target,
    project: "Communication Engineering",
    dryRun: true
  });
  assert.equal(result.dryRun, true);
  assert.equal(result.packageVersion, packageVersion);
  assert.ok(result.files.length >= 60);
  assert.equal(fs.existsSync(target), false);
});

test("collision aborts before any file is written", (t) => {
  const target = temporaryDirectory(t);
  const agents = path.join(target, "AGENTS.md");
  fs.writeFileSync(agents, "user work\n", "utf8");

  assert.throws(
    () => initializeProject({ target, project: "Communication Engineering" }),
    /installation would overwrite/
  );
  assert.equal(fs.readFileSync(agents, "utf8"), "user work\n");
  assert.equal(fs.existsSync(path.join(target, "BOOTSTRAP.md")), false);
});

test("common project-owned files are preserved and recorded as unmanaged", (t) => {
  const target = temporaryDirectory(t);
  fs.writeFileSync(path.join(target, "README.md"), "existing project readme\n", "utf8");
  fs.writeFileSync(path.join(target, ".gitattributes"), "* text=auto\n", "utf8");
  initializeProject({ target, project: "Communication Engineering" });

  assert.equal(fs.readFileSync(path.join(target, "README.md"), "utf8"), "existing project readme\n");
  assert.equal(fs.readFileSync(path.join(target, ".gitattributes"), "utf8"), "* text=auto\n");
  const installed = JSON.parse(
    fs.readFileSync(path.join(target, ".ros", "installation.json"), "utf8")
  );
  const readme = installed.files.find((entry) => entry.path === "README.md");
  assert.equal(readme.managed, false);
  assert.equal(readme.disposition, "preserved-existing");
  assert.deepEqual(verifyProject({ target }).findings, []);
});

test("verification detects installed snapshot drift", (t) => {
  const target = temporaryDirectory(t);
  initializeProject({ target, project: "Communication Engineering" });
  fs.appendFileSync(path.join(target, "BOOTSTRAP.md"), "\nchanged\n", "utf8");
  const result = verifyProject({ target });
  assert.equal(result.findings.length, 1);
  assert.match(result.findings[0], /BOOTSTRAP\.md: differs/);
});

test("installed Node validator catches broken lineage and accepts repaired lineage", (t) => {
  const target = temporaryDirectory(t);
  initializeProject({ target, project: "Communication Engineering" });
  const hypothesis = path.join(
    target,
    "research",
    "hypotheses",
    "HY-COMM-2026-A001--first-claim.md"
  );
  fs.writeFileSync(
    hypothesis,
    `---
id: HY-COMM-2026-A001
title: First claim
status: proposed
confidence: low
supporting_evidence: [EV-COMM-2026-A002]
---

# First claim
`,
    "utf8"
  );
  const broken = spawnSync(path.join(target, "ros"), ["validate"], {
    cwd: target,
    encoding: "utf8"
  });
  assert.equal(broken.status, 1);
  assert.match(broken.stderr, /broken reference 'EV-COMM-2026-A002'/);

  const evidence = path.join(
    target,
    "research",
    "evidence",
    "EV-COMM-2026-A002--first-observation.md"
  );
  fs.writeFileSync(
    evidence,
    `---
id: EV-COMM-2026-A002
title: First observation
status: accepted
confidence: medium
supports: [HY-COMM-2026-A001]
---

# First observation
`,
    "utf8"
  );
  const build = spawnSync(path.join(target, "ros"), ["registry", "build"], {
    cwd: target,
    encoding: "utf8"
  });
  assert.equal(build.status, 0, build.stderr || build.stdout);
  const repaired = spawnSync(path.join(target, "ros"), ["validate"], {
    cwd: target,
    encoding: "utf8"
  });
  assert.equal(repaired.status, 0, repaired.stderr || repaired.stdout);
});

test("npm tarball contains the executable and every scaffold source", (t) => {
  const destination = temporaryDirectory(t);
  const packed = spawnSync(
    "npm",
    ["pack", "--ignore-scripts", "--json", "--pack-destination", destination],
    {
      cwd: repository,
      encoding: "utf8",
      env: {
        ...process.env,
        npm_config_cache: path.join(destination, "npm-cache"),
        npm_config_dry_run: "false"
      }
    }
  );
  assert.equal(packed.status, 0, packed.stderr || packed.stdout);
  const report = JSON.parse(packed.stdout);
  const files = new Set(report[0].files.map((entry) => entry.path));
  assert.ok(files.has("bin/ros-bootstrap.mjs"));
  assert.ok(files.has("lib/bootstrap.mjs"));
  assert.ok(files.has("starter/greenfield/manifest.json"));
  assert.ok(files.has("framework/REP-SPECIFICATION.md"));
  assert.ok(files.has("tools/ros_cli.mjs"));
  assert.ok(files.has("starter/greenfield/ros"));

  const manifest = JSON.parse(
    fs.readFileSync(path.join(repository, "starter", "greenfield", "manifest.json"), "utf8")
  );
  for (const entry of manifest.files) {
    assert.ok(files.has(entry.source), `tarball is missing scaffold source ${entry.source}`);
  }

  const tarball = path.join(destination, report[0].filename);
  const target = path.join(destination, "installed-from-tarball");
  fs.mkdirSync(target);
  const executed = spawnSync(
    "npm",
    [
      "exec",
      "--yes",
      "--package",
      tarball,
      "--",
      "ros-bootstrap",
      "init",
      "--target",
      path.join(target, "communication-engineering")
    ],
    {
      cwd: target,
      encoding: "utf8",
      env: {
        ...process.env,
        npm_config_cache: path.join(destination, "npm-exec-cache"),
        npm_config_dry_run: "false"
      }
    }
  );
  assert.equal(executed.status, 0, executed.stderr || executed.stdout);
  assert.match(executed.stdout, new RegExp(`installed ROS ${packageVersion.replaceAll(".", "\\.")}`));
  assert.match(executed.stdout, /project: Communication Engineering/);

  const installedTarget = path.join(target, "communication-engineering");
  const validation = spawnSync(path.join(installedTarget, "ros"), ["validate"], {
    cwd: installedTarget,
    encoding: "utf8"
  });
  assert.equal(validation.status, 0, validation.stderr || validation.stdout);
});
