// Exercises the real npm artifact, not the source tree: `npm pack` produces a
// tarball, the tarball is extracted the way `npx` would install it, and every
// documented lifecycle command is then run against throwaway repositories
// through the package's own `bin/ros.mjs`.
//
// `dotnet test` passing proves nothing about npm distribution, so nothing here
// reaches into src/ or lib/ except through the packed launcher.

import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const packageMetadata = JSON.parse(fs.readFileSync(path.join(repositoryRoot, "package.json"), "utf8"));
const builtAssembly = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

// The packed launcher runs the assembly this checkout just built rather than
// downloading the published release binary for this version: the download path
// has its own coverage in tests/ros-fs-launcher.test.mjs, and a test suite
// should not depend on network access to a GitHub Release.
const cliEnvironment = { ...process.env, ROS_FS_DLL_PATH_OVERRIDE: builtAssembly };

let installedPackage = null;

function temporaryDirectory(label) {
  return fs.mkdtempSync(path.join(os.tmpdir(), `ros-package-${label}-`));
}

// Packs once and installs the result; every test below shares it.
function packedPackage() {
  if (installedPackage) return installedPackage;

  const staging = temporaryDirectory("pack");

  // npm passes its own config to child processes through npm_config_* env
  // vars, so when this suite is reached through another npm command's
  // prepack -- `npm pack --dry-run` and `npm publish` both do that -- the
  // pack below would inherit `--dry-run` and print a filename while writing
  // nothing. Clearing the inherited settings that would change what this
  // call does keeps it a real pack however it was invoked.
  const packEnvironment = { ...process.env };
  for (const key of ["npm_config_dry_run", "npm_config_pack_destination", "npm_config_ignore_scripts"]) {
    delete packEnvironment[key];
  }

  // --ignore-scripts: `prepack` runs the test suite, and this test is part of it.
  // npm is a .cmd shim on Windows, which execFileSync cannot launch directly;
  // running it through a shell there means the destination needs quoting, since
  // a Windows temp path can contain spaces.
  const onWindows = process.platform === "win32";
  const destination = onWindows ? `"${staging}"` : staging;
  const output = execFileSync(
    onWindows ? "npm.cmd" : "npm",
    ["pack", "--ignore-scripts", "--dry-run=false", "--pack-destination", destination],
    { cwd: repositoryRoot, encoding: "utf8", shell: onWindows, env: packEnvironment }
  );
  const tarball = path.join(staging, output.trim().split("\n").pop().trim());
  assert.ok(fs.existsSync(tarball), `npm pack must produce a tarball; got ${tarball}`);

  const installed = path.join(staging, "installed");
  fs.mkdirSync(installed);
  execFileSync("tar", ["-xzf", tarball, "-C", installed]);

  installedPackage = { tarball, root: path.join(installed, "package") };
  return installedPackage;
}

function listTarball(tarball) {
  return execFileSync("tar", ["-tzf", tarball], { encoding: "utf8" })
    .split("\n")
    // Windows tar terminates lines with CRLF, so each entry keeps a trailing
    // \r unless it is trimmed here.
    .map((entry) => entry.trim())
    .filter(Boolean)
    .map((entry) => entry.replace(/^package\//, ""));
}

function ros(root, args) {
  const { root: packageRoot } = packedPackage();
  const result = spawnSync(
    process.execPath,
    [path.join(packageRoot, "bin", "ros.mjs"), "--root", root, ...args],
    { cwd: repositoryRoot, encoding: "utf8", env: cliEnvironment }
  );
  return { status: result.status, stdout: result.stdout ?? "", stderr: result.stderr ?? "" };
}

function json(root, args) {
  const result = ros(root, args);
  // stdout must be the document and nothing else; decoration belongs on stderr.
  return { ...result, parsed: JSON.parse(result.stdout) };
}

// Content-addressed snapshot of the whole repository, used to prove that a
// dry run changes nothing and that a second init changes nothing.
function snapshot(root) {
  const hash = crypto.createHash("sha256");
  const walk = (directory, prefix) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true }).sort((a, b) => (a.name < b.name ? -1 : 1))) {
      const absolute = path.join(directory, entry.name);
      const relative = prefix ? `${prefix}/${entry.name}` : entry.name;
      if (entry.isDirectory()) {
        hash.update(`D:${relative}\n`);
        walk(absolute, relative);
      } else {
        hash.update(`F:${relative}:${crypto.createHash("sha256").update(fs.readFileSync(absolute)).digest("hex")}\n`);
      }
    }
  };
  walk(root, "");
  return hash.digest("hex");
}

function repository(t, label) {
  const root = temporaryDirectory(label);
  t.after(() => {
    try {
      fs.rmSync(root, { recursive: true, force: true });
    } catch {
      // Best effort; a leftover temp dir is not a test failure.
    }
  });
  return root;
}

test("the packed artifact declares the documented executables and ships what they need", () => {
  const { tarball, root } = packedPackage();
  const entries = listTarball(tarball);

  for (const required of [
    "package.json",
    "README.md",
    "LICENSE",
    "bin/ros.mjs",
    "bin/ros-bootstrap.mjs",
    "bin/ros-fs.mjs",
    "lib/lifecycle-launcher.mjs",
    "lib/ros-fs-launcher.mjs",
    "lib/bootstrap.mjs",
    "starter/greenfield/manifest.json",
    "starter/project-administration/manifest.json",
    "docs/cli.md",
    "docs/installation.md",
    "docs/upgrading.md"
  ]) {
    assert.ok(entries.includes(required), `packed tarball is missing ${required}`);
  }

  // Every declared bin must actually exist in the artifact.
  for (const [name, relative] of Object.entries(packageMetadata.bin)) {
    const target = relative.replace(/^\.\//, "");
    assert.ok(entries.includes(target), `bin '${name}' points at ${target}, which is not packed`);
    assert.ok(fs.existsSync(path.join(root, target)), `bin '${name}' is missing from the installed package`);
  }

  // Nothing that should never be published.
  const forbidden = entries.filter((entry) =>
    /(^|\/)(\.git|node_modules|coverage|\.env|\.npmrc|obj|bin\/(Debug|Release))\//.test(entry) ||
    /\.(tgz|pem|key)$/.test(entry) ||
    entry.startsWith("src/") ||
    entry.startsWith("tests/")
  );
  assert.deepEqual(forbidden, [], `packed tarball contains files it should not publish: ${forbidden.join(", ")}`);

  // The registry seeds are compared byte-for-byte against the CLI's own LF
  // projection, so a CRLF copy makes every registry read as stale the moment
  // it is installed. A checkout without `eol=lf` in .gitattributes produces
  // exactly that on Windows, so assert the bytes rather than trusting config.
  for (const seed of entries.filter((entry) => /^starter\/.+\/registries\/.+\.json$/.test(entry))) {
    const content = fs.readFileSync(path.join(root, seed));
    assert.ok(!content.includes(0x0d), `${seed} must be packed with LF endings, not CRLF`);
  }
});

test("--version reports the package version and --help documents every command", () => {
  const root = temporaryDirectory("help");

  const version = ros(root, ["--version"]);
  assert.equal(version.status, 0, version.stderr);
  assert.equal(version.stdout.trim(), `ros-fs ${packageMetadata.version}`);

  const help = ros(root, ["--help"]);
  assert.equal(help.status, 0, help.stderr);
  for (const command of ["init", "status", "verify", "upgrade", "doctor"]) {
    assert.match(help.stdout, new RegExp(`^\\s+${command}\\s`, "m"), `--help must document '${command}'`);

    const commandHelp = ros(root, [command, "--help"]);
    assert.equal(commandHelp.status, 0, commandHelp.stderr);
    assert.match(commandHelp.stdout, new RegExp(`^ros ${command} --`), `'${command} --help' must describe itself`);
    assert.match(commandHelp.stdout, /Usage:/);
  }

  fs.rmSync(root, { recursive: true, force: true });
});

test("an unknown option is an argument error, not a partial run", (t) => {
  const root = repository(t, "badargs");
  const result = ros(root, ["init", "--nonsense"]);
  assert.equal(result.status, 2);
  assert.match(result.stderr, /unknown option '--nonsense'/);
  assert.deepEqual(fs.readdirSync(root), [], "an invalid invocation must not touch the repository");
});

test("init --dry-run reports a full plan and writes nothing", (t) => {
  const root = repository(t, "dryrun");
  const before = snapshot(root);

  const dry = json(root, ["init", "--dry-run", "--json"]);
  assert.equal(dry.status, 0, dry.stderr);
  assert.equal(dry.parsed.command, "init");
  assert.equal(dry.parsed.dryRun, true);
  assert.equal(dry.parsed.applied, false);
  assert.equal(dry.parsed.changesRequired, true);
  assert.ok(dry.parsed.changes.length > 50, "a fresh install should plan the whole scaffold");
  assert.deepEqual(dry.parsed.conflicts, []);
  assert.ok(dry.parsed.manifest.managedArtifacts.length > 50);

  assert.equal(snapshot(root), before, "a dry run must leave the repository byte-identical");
});

test("init installs, then a second init is a no-op: the repository is byte-identical", (t) => {
  const root = repository(t, "idempotent");

  const first = ros(root, ["init"]);
  assert.equal(first.status, 0, first.stderr);
  assert.ok(fs.existsSync(path.join(root, ".echelon", "ros.json")));

  const afterFirst = snapshot(root);

  const second = ros(root, ["init"]);
  assert.equal(second.status, 0, second.stderr);
  assert.match(second.stdout, /no changes needed/);
  assert.equal(snapshot(root), afterFirst, "a second init must not change a single byte");

  // The machine-readable form agrees with the human one.
  const asJson = json(root, ["init", "--json"]);
  assert.equal(asJson.parsed.changesRequired, false);
  assert.deepEqual(asJson.parsed.changes, []);

  // --check is the CI form of the same question.
  assert.equal(ros(root, ["init", "--check"]).status, 0);
});

test("status, verify and doctor agree on a freshly installed repository", (t) => {
  const root = repository(t, "healthy");
  assert.equal(ros(root, ["init"]).status, 0);

  const status = json(root, ["status", "--json"]);
  assert.equal(status.status, 0, status.stderr);
  assert.equal(status.parsed.installation.state, "installed");
  assert.equal(status.parsed.installation.cliVersion, packageMetadata.version);
  assert.equal(status.parsed.installation.installedVersion, packageMetadata.version);
  assert.equal(status.parsed.installation.verified, true);

  // Name the findings rather than reporting only 'failed' !== 'passed': this
  // assertion covers the repository validation a fresh install has to satisfy,
  // and which artifact it is unhappy about is the whole diagnosis.
  const validation = ros(root, ["validate", "--json"]);
  assert.equal(
    status.parsed.validation,
    "passed",
    `a fresh install must validate cleanly; validate reported: ${validation.stdout || validation.stderr}`
  );

  const verify = json(root, ["verify", "--json"]);
  assert.equal(verify.status, 0, verify.stderr);
  assert.equal(verify.parsed.valid, true);
  assert.deepEqual(verify.parsed.failures, []);

  const strict = json(root, ["verify", "--strict", "--json"]);
  assert.equal(strict.status, 0, strict.stderr);
  assert.equal(strict.parsed.strict, true);
  assert.equal(strict.parsed.valid, true);

  const doctor = json(root, ["doctor", "--json"]);
  assert.equal(doctor.status, 0, doctor.stderr);
  assert.equal(doctor.parsed.healthy, true);
  assert.equal(doctor.parsed.errorCount, 0);

  // status must never write.
  const before = snapshot(root);
  ros(root, ["status"]);
  ros(root, ["verify"]);
  ros(root, ["doctor"]);
  assert.equal(snapshot(root), before, "read-only commands must not mutate the repository");
});

test("a damaged installation fails verification and doctor explains how to repair it", (t) => {
  const root = repository(t, "damaged");
  assert.equal(ros(root, ["init"]).status, 0);

  const casualty = path.join(root, "framework", "REP-SPECIFICATION.md");
  fs.rmSync(casualty);

  const verify = json(root, ["verify", "--json"]);
  assert.equal(verify.status, 3, "verification failure must use the documented exit code");
  assert.equal(verify.parsed.valid, false);
  assert.equal(verify.parsed.failures.length, 1);
  assert.equal(verify.parsed.failures[0].code, "managed-artifact-missing");

  const doctor = json(root, ["doctor", "--json"]);
  assert.equal(doctor.status, 3);
  assert.equal(doctor.parsed.healthy, false);
  const finding = doctor.parsed.diagnoses.find((item) => item.code === "managed-artifact-missing");
  assert.ok(finding, "doctor must name the damaged artifact");
  assert.equal(finding.severity, "error");
  assert.equal(finding.path, "framework/REP-SPECIFICATION.md");
  assert.ok(finding.remedy, "doctor must say how to fix what it reports");

  // And the remedy it names actually works.
  assert.equal(ros(root, ["init"]).status, 0);
  assert.equal(ros(root, ["verify"]).status, 0);
});

test("a locally modified tool-owned file blocks init instead of being overwritten", (t) => {
  const root = repository(t, "conflict");
  assert.equal(ros(root, ["init"]).status, 0);

  const target = path.join(root, "framework", "REP-SPECIFICATION.md");
  fs.appendFileSync(target, "\nlocal change that must survive\n");
  const edited = fs.readFileSync(target, "utf8");

  const result = ros(root, ["init"]);
  assert.equal(result.status, 4, "a blocked installation uses the incompatible-installation exit code");
  assert.match(result.stderr, /tool-owned file was modified locally/);
  assert.equal(fs.readFileSync(target, "utf8"), edited, "the local change must still be there");
});

test("a user-owned file is never overwritten, and is recorded as the repository's own", (t) => {
  const root = repository(t, "userowned");
  fs.writeFileSync(path.join(root, "README.md"), "# My project\n");

  assert.equal(ros(root, ["init"]).status, 0);
  assert.equal(fs.readFileSync(path.join(root, "README.md"), "utf8"), "# My project\n");

  const manifest = JSON.parse(fs.readFileSync(path.join(root, ".echelon", "ros.json"), "utf8"));
  const readme = manifest.managedArtifacts.find((entry) => entry.path === "README.md");
  assert.equal(readme.ownership, "user-owned");
  assert.equal(
    readme.sha256,
    crypto.createHash("sha256").update("# My project\n").digest("hex"),
    "the manifest must record the repository's own content, not the package's"
  );

  // Re-running init still leaves it alone.
  assert.equal(ros(root, ["init"]).status, 0);
  assert.equal(fs.readFileSync(path.join(root, "README.md"), "utf8"), "# My project\n");
  assert.equal(ros(root, ["verify"]).status, 0);
});

test("a populated registry is generated output, not a local edit to a tool-owned file", (t) => {
  const root = repository(t, "registries");
  assert.equal(ros(root, ["init"]).status, 0);

  // Every registry is written by `ros registry build`. Classifying one as
  // tool-owned makes the tool's own output indistinguishable from a local
  // edit, so assert the classification for all of them rather than the one
  // that regressed.
  const manifest = JSON.parse(fs.readFileSync(path.join(root, ".echelon", "ros.json"), "utf8"));
  const registries = manifest.managedArtifacts.filter((entry) => /^registries\/.+\.json$/.test(entry.path));
  assert.ok(registries.length > 0, "the scaffold must install registry seeds");
  assert.deepEqual(
    registries.filter((entry) => entry.ownership !== "generated").map((entry) => entry.path),
    [],
    "every registry is produced by `ros registry build`, so every registry is generated"
  );

  // The regression this guards is a deadlock, not a warning: one artifact in
  // the corpus was enough to fail verification permanently, with init and
  // upgrade both refusing to run and doctor advising that the index be
  // reverted.
  const template = fs.readFileSync(path.join(root, "templates", "research", "THEORY-TEMPLATE.md"), "utf8");
  fs.mkdirSync(path.join(root, "research", "theories"), { recursive: true });
  fs.writeFileSync(
    path.join(root, "research", "theories", "TH-DEMO-2026-0001--registry-ownership.md"),
    template
      .replace(/^id: .*$/m, "id: TH-DEMO-2026-0001")
      .replace(/^title: .*$/m, "title: A theory that reaches the registry")
      .replace(/^research_area: .*$/m, "research_area: demo")
      .replaceAll("YYYY-MM-DD", "2026-09-17")
  );

  assert.equal(ros(root, ["registry", "build"]).status, 0);
  assert.notEqual(
    JSON.parse(fs.readFileSync(path.join(root, "registries", "theories.json"), "utf8")).length,
    0,
    "the theory must actually reach the registry, or the assertions below prove nothing"
  );

  assert.equal(ros(root, ["verify"]).status, 0, "generated output must not fail verification");
  assert.equal(ros(root, ["init"]).status, 0, "init must stay available once the corpus has content");
});

test("a legacy ros-bootstrap installation upgrades, keeping user edits and the legacy snapshot", (t) => {
  const root = repository(t, "upgrade");
  const { root: packageRoot } = packedPackage();

  // Install the old way, through the executable existing users already run.
  const legacy = spawnSync(
    process.execPath,
    [path.join(packageRoot, "bin", "ros-bootstrap.mjs"), "init", "--target", root, "--project", "Legacy Project"],
    { encoding: "utf8", env: cliEnvironment }
  );
  assert.equal(legacy.status, 0, legacy.stderr);
  assert.ok(fs.existsSync(path.join(root, ".ros", "installation.json")));
  assert.ok(!fs.existsSync(path.join(root, ".echelon", "ros.json")));

  const edited = "# Charter owned by the project\n";
  fs.writeFileSync(path.join(root, "PROJECT-CHARTER.md"), edited);

  function legacyVerify() {
    const result = spawnSync(
      process.execPath,
      [path.join(packageRoot, "bin", "ros-bootstrap.mjs"), "verify", "--target", root],
      { encoding: "utf8", env: cliEnvironment }
    );
    return { status: result.status, stderr: result.stderr ?? "" };
  }

  // `ros-bootstrap verify` is a snapshot-drift diagnostic: it already reports
  // the edit above, and did so before this upgrade existed. What matters for
  // compatibility is that upgrading does not change its answer.
  const legacyBefore = legacyVerify();

  // Before upgrading, status recognises it as a real installation one step behind.
  const before = json(root, ["status", "--json"]);
  assert.equal(before.parsed.installation.state, "upgrade-required");
  assert.equal(before.parsed.installation.upgradeAvailable, packageMetadata.version);

  // --check reports pending work without doing any.
  const snapshotBefore = snapshot(root);
  assert.equal(ros(root, ["upgrade", "--check"]).status, 3);

  const dry = json(root, ["upgrade", "--dry-run", "--json"]);
  assert.equal(dry.status, 0, dry.stderr);
  assert.equal(dry.parsed.applied, false);
  assert.equal(dry.parsed.migrations.length, 1, "a legacy install migrates through exactly one declared step");
  assert.equal(dry.parsed.migrations[0].fromVersion, 0);
  assert.equal(dry.parsed.migrations[0].toVersion, 1);
  assert.ok(dry.parsed.preserved.includes("PROJECT-CHARTER.md"));
  assert.equal(snapshot(root), snapshotBefore, "--check and --dry-run must not change anything");

  const upgraded = ros(root, ["upgrade"]);
  assert.equal(upgraded.status, 0, upgraded.stderr);

  assert.equal(fs.readFileSync(path.join(root, "PROJECT-CHARTER.md"), "utf8"), edited, "user data must survive upgrade");
  assert.ok(fs.existsSync(path.join(root, ".ros", "installation.json")), "the legacy snapshot must be left in place");

  const after = json(root, ["status", "--json"]);
  assert.equal(after.parsed.installation.state, "installed");
  assert.equal(after.parsed.installation.configurationVersion, 1);

  assert.equal(ros(root, ["verify", "--strict"]).status, 0);
  assert.equal(ros(root, ["upgrade", "--check"]).status, 0, "current -> current is a no-op");

  // The legacy executable reports exactly what it reported before.
  assert.deepEqual(legacyVerify(), legacyBefore, "upgrade must be invisible to ros-bootstrap verify");
});

test("upgrade adopts an older legacy snapshot using its recorded tool-owned hashes", (t) => {
  const root = repository(t, "upgrade-real-legacy");
  const { root: packageRoot } = packedPackage();

  const legacy = spawnSync(
    process.execPath,
    [path.join(packageRoot, "bin", "ros-bootstrap.mjs"), "init", "--target", root, "--project", "Older Legacy"],
    { encoding: "utf8", env: cliEnvironment }
  );
  assert.equal(legacy.status, 0, legacy.stderr);

  const legacyPath = path.join(root, ".ros", "installation.json");
  const snapshot = JSON.parse(fs.readFileSync(legacyPath, "utf8"));
  const bootstrap = snapshot.files.find((entry) => entry.path === "BOOTSTRAP.md");
  assert.ok(bootstrap && bootstrap.managed, "fixture must contain a managed BOOTSTRAP.md");

  const oldBytes = "# BOOTSTRAP from ROS 2.x\n";
  fs.writeFileSync(path.join(root, "BOOTSTRAP.md"), oldBytes);
  bootstrap.sha256 = crypto.createHash("sha256").update(oldBytes).digest("hex");
  snapshot.package_version = "2.0.0";
  fs.writeFileSync(legacyPath, `${JSON.stringify(snapshot, null, 2)}\n`);

  const upgraded = ros(root, ["upgrade"]);
  assert.equal(upgraded.status, 0, upgraded.stderr);
  assert.notEqual(fs.readFileSync(path.join(root, "BOOTSTRAP.md"), "utf8"), oldBytes);

  const manifest = JSON.parse(fs.readFileSync(path.join(root, ".echelon", "ros.json"), "utf8"));
  assert.equal(manifest.installedVersion, packageMetadata.version);
  assert.equal(manifest.configurationVersion, 1);
  assert.equal(ros(root, ["verify", "--strict"]).status, 0);
});

test("upgrade blocks a locally edited file from an older legacy snapshot", (t) => {
  const root = repository(t, "upgrade-real-legacy-local-edit");
  const { root: packageRoot } = packedPackage();

  const legacy = spawnSync(
    process.execPath,
    [path.join(packageRoot, "bin", "ros-bootstrap.mjs"), "init", "--target", root, "--project", "Older Legacy Edited"],
    { encoding: "utf8", env: cliEnvironment }
  );
  assert.equal(legacy.status, 0, legacy.stderr);

  const target = path.join(root, "BOOTSTRAP.md");
  const edited = fs.readFileSync(target, "utf8") + "\nlocal change after legacy installation\n";
  fs.writeFileSync(target, edited);

  const result = ros(root, ["upgrade"]);
  assert.equal(result.status, 5);
  assert.match(result.stderr, /tool-owned file was modified locally/);
  assert.equal(fs.readFileSync(target, "utf8"), edited);
  assert.ok(!fs.existsSync(path.join(root, ".echelon", "ros.json")), "blocked upgrade must not claim a current installation");
});

test("legacy profile is authoritative during adoption", (t) => {
  const root = repository(t, "upgrade-legacy-profile");
  const { root: packageRoot } = packedPackage();

  const legacy = spawnSync(
    process.execPath,
    [path.join(packageRoot, "bin", "ros-bootstrap.mjs"), "init", "--target", root, "--project", "Legacy Profile"],
    { encoding: "utf8", env: cliEnvironment }
  );
  assert.equal(legacy.status, 0, legacy.stderr);

  const legacyPath = path.join(root, ".ros", "installation.json");
  const snapshot = JSON.parse(fs.readFileSync(legacyPath, "utf8"));
  snapshot.profile = "project-administration";
  fs.writeFileSync(legacyPath, `${JSON.stringify(snapshot, null, 2)}\n`);

  const upgraded = ros(root, ["upgrade"]);
  assert.equal(upgraded.status, 0, upgraded.stderr);

  const manifest = JSON.parse(fs.readFileSync(path.join(root, ".echelon", "ros.json"), "utf8"));
  assert.equal(manifest.profile, "project-administration");
  assert.ok(fs.existsSync(path.join(root, "ros-hub")), "project-administration payload must be selected during upgrade");
});

test("upgrade leaves an unedited legacy installation verifying cleanly through ros-bootstrap", (t) => {
  const root = repository(t, "upgrade-clean");
  const { root: packageRoot } = packedPackage();

  const legacy = spawnSync(
    process.execPath,
    [path.join(packageRoot, "bin", "ros-bootstrap.mjs"), "init", "--target", root, "--project", "Clean Legacy"],
    { encoding: "utf8", env: cliEnvironment }
  );
  assert.equal(legacy.status, 0, legacy.stderr);

  const verifyLegacy = () =>
    spawnSync(
      process.execPath,
      [path.join(packageRoot, "bin", "ros-bootstrap.mjs"), "verify", "--target", root],
      { encoding: "utf8", env: cliEnvironment }
    );

  assert.equal(verifyLegacy().status, 0, "a fresh legacy install verifies cleanly");

  const upgraded = ros(root, ["upgrade"]);
  assert.equal(upgraded.status, 0, upgraded.stderr);

  assert.equal(verifyLegacy().status, 0, "and still verifies cleanly after the upgrade");
  assert.equal(ros(root, ["verify", "--strict"]).status, 0);
});

test("upgrade refuses an installation from a newer CLI rather than guessing", (t) => {
  const root = repository(t, "future");
  assert.equal(ros(root, ["init"]).status, 0);

  const manifestPath = path.join(root, ".echelon", "ros.json");
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  manifest.configurationVersion = 99;
  fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);

  const result = ros(root, ["upgrade"]);
  assert.equal(result.status, 4, "an unsupported configuration version is an incompatible installation");
  assert.match(result.stderr, /newer than this CLI supports/);
});

test("a malformed manifest is diagnosed, not thrown", (t) => {
  const root = repository(t, "malformed");
  assert.equal(ros(root, ["init"]).status, 0);
  fs.writeFileSync(path.join(root, ".echelon", "ros.json"), "{ not json");

  const doctor = json(root, ["doctor", "--json"]);
  assert.equal(doctor.status, 3);
  const finding = doctor.parsed.diagnoses.find((item) => item.code === "manifest-unreadable");
  assert.ok(finding, `expected a manifest-unreadable diagnosis, got ${JSON.stringify(doctor.parsed.diagnoses)}`);
  assert.ok(finding.remedy);
});

test("the project-administration profile installs and verifies through the packed artifact", (t) => {
  const root = repository(t, "hub");

  const result = ros(root, ["init", "--profile", "project-administration", "--project", "Project Administration"]);
  assert.equal(result.status, 0, result.stderr);

  const manifest = JSON.parse(fs.readFileSync(path.join(root, ".echelon", "ros.json"), "utf8"));
  assert.equal(manifest.profile, "project-administration");

  assert.equal(ros(root, ["verify"]).status, 0);
  assert.equal(ros(root, ["init"]).status, 0);
});

// The scaffold is compiled into the CLI assembly, so a released binary can
// install, heal and upgrade a repository on its own. This is what makes
// `./ros init` and `./ros upgrade` work inside a scaffolded project, whose own
// launcher downloads that binary and has no npm package to point at.
//
// Everything below runs the assembly from a directory outside this checkout,
// with the working directory outside it too, so neither the package-root walk
// nor --package-root can supply the scaffold: only the embedded copy can.
test("a binary on its own, with no npm package reachable, can install heal and upgrade", (t) => {
  const detached = temporaryDirectory("detached");
  const project = temporaryDirectory("standalone");
  t.after(() => {
    for (const directory of [detached, project]) {
      try {
        fs.rmSync(directory, { recursive: true, force: true });
      } catch {
        // Best effort.
      }
    }
  });

  fs.cpSync(path.dirname(builtAssembly), detached, { recursive: true });
  const detachedAssembly = path.join(detached, path.basename(builtAssembly));

  const standalone = (args) => {
    const result = spawnSync("dotnet", [detachedAssembly, "--root", project, ...args], {
      // Outside the repository, so the package-root walk finds nothing.
      cwd: detached,
      encoding: "utf8",
      env: process.env
    });
    assert.ok(
      !(result.stderr ?? "").includes("packaged scaffold"),
      `'${args.join(" ")}' fell back to needing the npm package: ${result.stderr}`
    );
    return { status: result.status, stdout: result.stdout ?? "", stderr: result.stderr ?? "" };
  };

  const installed = standalone(["init", "--project", "Standalone Service"]);
  assert.equal(installed.status, 0, installed.stderr);
  assert.ok(fs.existsSync(path.join(project, ".echelon", "ros.json")));
  assert.ok(fs.existsSync(path.join(project, "framework", "REP-SPECIFICATION.md")));

  // Idempotent from the embedded copy too.
  const again = standalone(["init"]);
  assert.equal(again.status, 0, again.stderr);
  assert.match(again.stdout, /no changes needed/);

  assert.equal(standalone(["verify", "--strict"]).status, 0);

  // Heal: the installed repository repairs itself with no package present.
  fs.rmSync(path.join(project, "framework", "REP-SPECIFICATION.md"));
  assert.equal(standalone(["verify"]).status, 3, "a deleted tool-owned file must fail verification");
  assert.equal(standalone(["init"]).status, 0, "init must restore it from the embedded scaffold");
  assert.equal(standalone(["verify"]).status, 0);
  assert.ok(fs.existsSync(path.join(project, "framework", "REP-SPECIFICATION.md")));

  // Update: already current, so a clean no-op rather than exit 6.
  assert.equal(standalone(["upgrade"]).status, 0);
  assert.equal(standalone(["upgrade", "--check"]).status, 0);

  // And the installed repository is valid on its own terms.
  assert.equal(standalone(["registry", "check"]).status, 0);
  assert.equal(standalone(["validate"]).status, 0);
});

test("an unsupported profile names the profiles that exist", (t) => {
  const root = repository(t, "profile");
  const result = ros(root, ["init", "--profile", "nope"]);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /unsupported profile 'nope'/);
  assert.match(result.stderr, /greenfield/);
});
