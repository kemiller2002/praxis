import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import http from "node:http";
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
const fsharpDll = path.join(repository, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function temporaryDirectory(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "ros-bootstrap-"));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 }));
  return directory;
}

// The scaffolded ./ros is now the F# launcher (DF-ROS-2026-A032) for both
// profiles; Node's CLI modules are retained in this repository only as the
// web server's internal dependency (DF-ROS-2026-A033) and are no longer
// scaffolded into greenfield projects. For tests whose own purpose is "the
// bootstrapped content validates correctly" rather than "the F# launcher
// itself works" (that is covered by the dedicated ros_fs_launcher.mjs test
// below, and the real end-to-end npm-exec test exercises the launcher for
// real), invoke this repository's own already-built F# CLI directly against
// the bootstrapped target's --root as a reliable, network-free verifier --
// this is exactly what a real installed ./ros does once it has acquired a
// real release binary.
function fsharpCli(target, args, options = {}) {
  return spawnSync("dotnet", [fsharpDll, "--root", target, ...args], { cwd: repository, encoding: "utf8", ...options });
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
  assert.ok(fs.existsSync(path.join(target, "tools", "ros_fs_launcher.mjs")));
  assert.equal(fs.existsSync(path.join(target, "tools", "ros_cli.mjs")), false, "greenfield no longer scaffolds Node's CLI modules (DF-ROS-2026-A033)");
  assert.ok(fs.existsSync(path.join(target, ".ros", "installation.json")));
  const workContext = JSON.parse(fs.readFileSync(path.join(target, ".ros", "context", "current.json"), "utf8"));
  assert.equal(workContext.workItems[0].semanticState, "complete");
  assert.match(workContext.workItems[0].id, /^ROS-INSTALL-/);
  assert.ok(fs.existsSync(path.join(target, ".ros", "events", "events.jsonl")));
  assert.ok(fs.existsSync(path.join(target, ".github", "workflows", "ros-validation.yml")));

  const registry = fsharpCli(target, ["registry", "check"]);
  assert.equal(registry.status, 0, registry.stderr || registry.stdout);
  assert.match(registry.stdout, /registries are current/);

  const validation = fsharpCli(target, ["validate"]);
  assert.equal(validation.status, 0, validation.stderr || validation.stdout);
  assert.match(validation.stdout, /validation passed/);
  const workflow = fs.readFileSync(path.join(target, ".github", "workflows", "ros-validation.yml"), "utf8");
  assert.match(workflow, /ROS_BASE_REF/);
  assert.doesNotMatch(workflow, /npm test/);
  assert.deepEqual(verifyProject({ target }).findings, []);
});

test("scaffolded ros_fs_launcher.mjs downloads, verifies, caches, and execs a real binary, then runs offline on a cache hit", async (t) => {
  const target = temporaryDirectory(t);
  initializeProject({ target, project: "FSharp Launcher Sandbox" });
  // Exercises the scaffolded launcher module in-process (matching
  // tests/ros-fs-launcher.test.mjs's own pattern) rather than spawning
  // the ros-fs script as a subprocess: a subprocess fetch to a
  // same-machine ephemeral port does not complete in this sandbox
  // (unrelated to the launcher's own correctness -- the identical logic,
  // invoked directly, works; only a spawned-child's own network path
  // through this sandbox's outbound proxy setup does not), so this stays
  // a real, meaningful test of the download/verify/cache/exec logic
  // without depending on that sandbox quirk.
  const { run, internal } = await import(path.join(target, "tools", "ros_fs_launcher.mjs"));

  const rid = internal.resolveRid();
  assert.ok(rid, `this test host's platform/arch must resolve to a known RID`);
  const script = "#!/bin/sh\necho fake-ros-fs-scaffold-output\nexit 0\n";
  const assetName = internal.releaseAssetName(rid);
  const checksum = crypto.createHash("sha256").update(script).digest("hex");

  const hits = [];
  const server = http.createServer((req, res) => {
    hits.push(req.url);
    if (req.url === "/checksums.txt") {
      res.writeHead(200, { "content-type": "text/plain" });
      res.end(`${checksum}  ${assetName}\n`);
      return;
    }
    if (req.url === `/${assetName}`) {
      res.writeHead(200, { "content-type": "application/octet-stream" });
      res.end(script);
      return;
    }
    res.writeHead(404);
    res.end("not found");
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  t.after(() => new Promise((resolve) => server.close(resolve)));
  const { port } = server.address();

  const cacheDir = fs.mkdtempSync(path.join(os.tmpdir(), "ros-fs-scaffold-cache-"));
  t.after(() => fs.rmSync(cacheDir, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 }));

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
  const first = await run([], { log: (message) => logs.push(message) });
  assert.equal(first, 0, logs.join("\n"));
  assert.deepEqual(hits.sort(), ["/checksums.txt", `/${assetName}`]);

  process.env.ROS_FS_RELEASE_BASE_URL = "http://127.0.0.1:1";
  const secondLogs = [];
  const second = await run([], { log: (message) => secondLogs.push(message) });
  assert.equal(second, 0, secondLogs.join("\n"));
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

test("installation is automatically attributed in an existing git repository", (t) => {
  const target = temporaryDirectory(t);
  fs.writeFileSync(path.join(target, "README.md"), "existing\n", "utf8");
  spawnSync("git", ["init", "-q"], { cwd: target });
  spawnSync("git", ["config", "user.email", "test@example.invalid"], { cwd: target });
  spawnSync("git", ["config", "user.name", "ROS Test"], { cwd: target });
  spawnSync("git", ["add", "README.md"], { cwd: target });
  spawnSync("git", ["commit", "-qm", "baseline"], { cwd: target });
  initializeProject({ target, project: "Existing Repository" });
  const validation = fsharpCli(target, ["validate"]);
  assert.equal(validation.status, 0, validation.stderr || validation.stdout);
});

test("verification detects installed snapshot drift", (t) => {
  const target = temporaryDirectory(t);
  initializeProject({ target, project: "Communication Engineering" });
  fs.appendFileSync(path.join(target, "BOOTSTRAP.md"), "\nchanged\n", "utf8");
  const result = verifyProject({ target });
  assert.equal(result.findings.length, 1);
  assert.match(result.findings[0], /BOOTSTRAP\.md: differs/);
});

test("installed F# validator catches broken lineage and accepts repaired lineage", (t) => {
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
  const broken = fsharpCli(target, ["validate"]);
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
  const build = fsharpCli(target, ["registry", "build"]);
  assert.equal(build.status, 0, build.stderr || build.stdout);
  const repaired = fsharpCli(target, ["validate"]);
  assert.equal(repaired.status, 0, repaired.stderr || repaired.stdout);
});

test("installed validator accepts preserved legacy REP identity and confidence", (t) => {
  const target = temporaryDirectory(t);
  initializeProject({ target, project: "Compatibility Pilot" });
  fs.writeFileSync(
    path.join(target, "research", "packages", "RP-2026-07-30-NHE-COMPARATIVE-REVIEW.md"),
    `---\nidentifier: RP-2026-07-30-NHE-COMPARATIVE-REVIEW\ntitle: Legacy review\nstatus: draft\nconfidence: medium-high\n---\n`,
    "utf8"
  );
  const build = fsharpCli(target, ["registry", "build"]);
  assert.equal(build.status, 0, build.stderr || build.stdout);
  const validation = fsharpCli(target, ["validate"]);
  assert.equal(validation.status, 0, validation.stderr || validation.stdout);
});

test("npm tarball contains the executable and every scaffold source", async (t) => {
  assert.equal(
    fs.statSync(path.join(repository, "bin", "ros-bootstrap.mjs")).mode & 0o111,
    0o111,
    "npm executable must have executable permission bits"
  );
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
  assert.ok(files.has("tools/ros_persistence.mjs"));
  assert.ok(files.has("tools/ros_git.mjs"));
  assert.ok(files.has("starter/greenfield/ros"));
  assert.ok(files.has("starter/greenfield/tools/ros_fs_launcher.mjs"));

  for (const profile of ["greenfield", "project-administration"]) {
    const manifest = JSON.parse(
      fs.readFileSync(path.join(repository, "starter", profile, "manifest.json"), "utf8")
    );
    for (const entry of manifest.files) {
      assert.ok(files.has(entry.source), `tarball is missing scaffold source ${entry.source} (profile: ${profile})`);
    }
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

  // Proves the real npm-packed, npm-exec'd ./ros launcher genuinely runs
  // end to end (real RID resolution, real cache path, real exec) without
  // depending on network access to a real GitHub release in this test
  // suite: pre-seed the cache with a fake "binary" the same way
  // tests/ros-fs-launcher.test.mjs does. Bootstrap content correctness
  // itself is already covered by this file's fsharpCli(...)-based tests.
  const installedTarget = path.join(target, "communication-engineering");
  const { internal: installedLauncher } = await import(path.join(installedTarget, "tools", "ros_fs_launcher.mjs"));
  const installedRid = installedLauncher.resolveRid();
  assert.ok(installedRid, "this test host's platform/arch must resolve to a known RID");
  const installedCacheDir = fs.mkdtempSync(path.join(os.tmpdir(), "ros-npm-exec-cache-"));
  t.after(() => fs.rmSync(installedCacheDir, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 }));

  const previousCacheDirEnv = process.env.ROS_FS_CACHE_DIR;
  process.env.ROS_FS_CACHE_DIR = installedCacheDir;
  t.after(() => {
    if (previousCacheDirEnv === undefined) delete process.env.ROS_FS_CACHE_DIR;
    else process.env.ROS_FS_CACHE_DIR = previousCacheDirEnv;
  });

  // The fake "binary" delegates to this repository's own already-built real
  // F# CLI rather than being an inert stub, so ./ros validate genuinely
  // re-validates the real bootstrapped content. Greenfield no longer
  // scaffolds Node's CLI modules (DF-ROS-2026-A033), so there is no
  // installed tools/ros_cli.mjs to delegate to here even as a stand-in.
  const installedBinaryPath = installedLauncher.cacheDirectory(packageVersion, installedRid);
  fs.mkdirSync(installedBinaryPath, { recursive: true });
  fs.writeFileSync(
    path.join(installedBinaryPath, installedLauncher.binaryName(installedRid)),
    `#!/bin/sh\nexec dotnet "${fsharpDll}" "$@"\n`,
    { mode: 0o755 }
  );

  const validation = spawnSync(path.join(installedTarget, "ros"), ["validate"], {
    cwd: installedTarget,
    encoding: "utf8",
    env: { ...process.env, ROS_FS_CACHE_DIR: installedCacheDir }
  });
  assert.equal(validation.status, 0, validation.stderr || validation.stdout);
});

test("main publishing workflow uses an OIDC-compatible npm CLI", () => {
  const workflow = fs.readFileSync(
    path.join(repository, ".github", "workflows", "publish.yml"),
    "utf8"
  );
  assert.match(workflow, /id-token: write/);
  assert.match(workflow, /npm install --global npm@11/);
  assert.match(workflow, /npm publish --access public --tag main/);
  assert.match(workflow, /kemiller2002\/repository-operating-system/);
  const manifest = JSON.parse(fs.readFileSync(path.join(repository, "package.json"), "utf8"));
  assert.equal(
    manifest.repository.url,
    "git+https://github.com/kemiller2002/repository-operating-system.git"
  );
});

test("project-administration profile installs a working hub, self-contained and immediately valid", async (t) => {
  const target = temporaryDirectory(t);
  const result = initializeProject({
    target,
    project: "Org Hub",
    profile: "project-administration"
  });

  assert.equal(result.packageVersion, packageVersion);
  assert.ok(fs.existsSync(path.join(target, "tools", "ros_hub_cli.mjs")));
  assert.ok(fs.existsSync(path.join(target, "tools", "ros_hub_server.mjs")));
  assert.ok(fs.existsSync(path.join(target, "tools", "http_body.mjs")));
  assert.ok(fs.existsSync(path.join(target, "web-hub", "app.ts")));
  assert.equal(fs.statSync(path.join(target, "ros-hub")).mode & 0o777, 0o755);

  const registry = JSON.parse(fs.readFileSync(path.join(target, ".ros", "hub", "registry.json"), "utf8"));
  assert.deepEqual(registry.repos, []);
  assert.match(fs.readFileSync(path.join(target, ".ros", "hub", "registry.md"), "utf8"), /Registered Repositories/);

  const validation = fsharpCli(target, ["validate"]);
  assert.equal(validation.status, 0, validation.stderr || validation.stdout);
  assert.match(validation.stdout, /validation passed/);

  const registryCheck = fsharpCli(target, ["registry", "check"]);
  assert.equal(registryCheck.status, 0, registryCheck.stderr || registryCheck.stdout);

  // The hub CLI itself works against the freshly installed, copied files --
  // not the source checkout.
  const otherSpoke = temporaryDirectory(t);
  initializeProject({ target: otherSpoke, project: "Spoke Repo" });
  spawnSync("git", ["init", "-q"], { cwd: otherSpoke });

  // ros-hub create shells out to the spoke's own ./ros (now the F#
  // launcher) to actually add the work item, so it needs a cached binary
  // too -- pre-seed one the same way as the npm-exec test above, via
  // process.env so execFileSync's inherited environment carries it down.
  const { internal: spokeLauncher } = await import(path.join(otherSpoke, "tools", "ros_fs_launcher.mjs"));
  const spokeRid = spokeLauncher.resolveRid();
  assert.ok(spokeRid, "this test host's platform/arch must resolve to a known RID");
  const spokeCacheDir = fs.mkdtempSync(path.join(os.tmpdir(), "ros-hub-spoke-cache-"));
  t.after(() => fs.rmSync(spokeCacheDir, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 }));
  const previousSpokeCacheEnv = process.env.ROS_FS_CACHE_DIR;
  process.env.ROS_FS_CACHE_DIR = spokeCacheDir;
  t.after(() => {
    if (previousSpokeCacheEnv === undefined) delete process.env.ROS_FS_CACHE_DIR;
    else process.env.ROS_FS_CACHE_DIR = previousSpokeCacheEnv;
  });
  // The fake "binary" delegates to this repository's own already-built real
  // F# CLI rather than being an inert stub, so ros-hub create's actual write
  // to the spoke's queue.json is genuinely exercised, not merely invoked.
  // The spoke is a greenfield-profiled bootstrap, which no longer scaffolds
  // Node's CLI modules (DF-ROS-2026-A033).
  const spokeBinaryPath = spokeLauncher.cacheDirectory(packageVersion, spokeRid);
  fs.mkdirSync(spokeBinaryPath, { recursive: true });
  fs.writeFileSync(
    path.join(spokeBinaryPath, spokeLauncher.binaryName(spokeRid)),
    `#!/bin/sh\nexec dotnet "${fsharpDll}" "$@"\n`,
    { mode: 0o755 }
  );

  const registered = spawnSync(path.join(target, "ros-hub"), ["register", otherSpoke, "--name", "Spoke"], { cwd: target, encoding: "utf8" });
  assert.equal(registered.status, 0, registered.stderr || registered.stdout);
  const repoEntry = JSON.parse(registered.stdout);

  const created = spawnSync(path.join(target, "ros-hub"), ["create", repoEntry.id, "Investigate from the hub", "--tag", "smoke"], { cwd: target, encoding: "utf8" });
  assert.equal(created.status, 0, created.stderr || created.stdout);
  assert.match(created.stdout, /Investigate from the hub/);

  const spokeQueue = JSON.parse(fs.readFileSync(path.join(otherSpoke, ".ros", "work", "queue.json"), "utf8"));
  assert.equal(spokeQueue.items[0].title, "Investigate from the hub");
});

test("unsupported profile lists available profiles in the error", (t) => {
  const target = temporaryDirectory(t);
  assert.throws(
    () => initializeProject({ target, project: "X", profile: "nonexistent" }),
    /unsupported profile 'nonexistent'; available profiles: greenfield, project-administration/
  );
});
