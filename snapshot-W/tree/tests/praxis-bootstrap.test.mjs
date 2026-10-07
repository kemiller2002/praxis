// Deterministic, verified Praxis bootstrap (PRAXIS-REMOTE-05,
// DF-ROS-2026-A041 section 9): the pinned version is installed or the run
// fails explicitly; nothing ever falls forward to another version.
import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const bootstrap = path.join(repositoryRoot, "scripts", "praxis-bootstrap.sh");

function rid() {
  const system = `${os.type()}-${os.arch()}`;
  return { "Linux-x64": "linux-x64", "Linux-arm64": "linux-arm64", "Darwin-arm64": "osx-arm64", "Darwin-x64": "osx-x64" }[system];
}

const RID = rid();
const skipUnsupported = RID ? false : "platform without a published native bundle";

function temporary(t, label) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), `praxis-bootstrap-${label}-`));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  return dir;
}

/** A fake release directory whose bundle's binary reports `reports`. */
function release(t, { version = "9.9.9", reports = version, corruptChecksum = false } = {}) {
  const root = temporary(t, "release");
  const stage = path.join(root, "stage", `praxis-${RID}`);
  fs.mkdirSync(stage, { recursive: true });
  fs.writeFileSync(path.join(stage, "praxis"), `#!/bin/sh\necho "ros-fs ${reports}"\n`, { mode: 0o755 });
  const asset = `praxis-${RID}.tar.gz`;
  execFileSync("tar", ["-czf", path.join(root, asset), "-C", path.join(root, "stage"), `praxis-${RID}`]);
  const digest = crypto.createHash("sha256").update(fs.readFileSync(path.join(root, asset))).digest("hex");
  const published = corruptChecksum ? "0".repeat(64) : digest;
  fs.writeFileSync(path.join(root, "native-checksums.txt"), `${published}  ${asset}\n`);
  return { url: `file://${root}`, digest };
}

function manifest(t, content) {
  const dir = temporary(t, "manifest");
  const file = path.join(dir, "toolchain.json");
  if (content !== undefined) fs.writeFileSync(file, typeof content === "string" ? content : JSON.stringify(content));
  return file;
}

/** A PATH directory holding a stub `gh` that succeeds or fails. */
function stubGh(t, succeeds) {
  const dir = temporary(t, "gh");
  fs.writeFileSync(path.join(dir, "gh"), `#!/bin/sh\n${succeeds ? "exit 0" : "echo 'no attestation' >&2; exit 1"}\n`, { mode: 0o755 });
  return dir;
}

function run(args, env = {}) {
  const result = spawnSync("sh", [bootstrap, ...args], { encoding: "utf8", env: { ...process.env, ...env } });
  return { status: result.status, stdout: result.stdout.trim(), stderr: result.stderr };
}

const pinned = { schemaVersion: 1, ordo: "1.4.0", praxis: "9.9.9" };

test("installs exactly the pinned version, verified, and reports it", { skip: skipUnsupported }, (t) => {
  const { url, digest } = release(t);
  const install = temporary(t, "install");
  const outputs = path.join(install, "github-output");
  const result = run(["--manifest", manifest(t, pinned), "--install-dir", install, "--attestation", "skip"], {
    PRAXIS_RELEASE_BASE_URL: url,
    GITHUB_OUTPUT: outputs
  });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stderr, /attestation verification explicitly skipped/);
  assert.equal(execFileSync(result.stdout, ["--version"], { encoding: "utf8" }).trim(), "ros-fs 9.9.9");
  const written = fs.readFileSync(outputs, "utf8");
  assert.match(written, /^version=9\.9\.9$/m);
  assert.match(written, new RegExp(`^digest=sha256:${digest}$`, "m"));
  assert.match(written, /^attestation=skip$/m);
});

test("a missing, malformed, or non-exact pin fails instead of falling forward", { skip: skipUnsupported }, (t) => {
  const { url } = release(t);
  const install = temporary(t, "install");
  const cases = [
    manifest(t, undefined),
    manifest(t, "{not json"),
    manifest(t, { schemaVersion: 1, ordo: "1.4.0" }),
    manifest(t, { schemaVersion: 1, praxis: "latest" }),
    manifest(t, { schemaVersion: 1, praxis: "3.4" }),
    manifest(t, { schemaVersion: 2, praxis: "9.9.9" })
  ];
  for (const file of cases) {
    const result = run(["--manifest", file, "--install-dir", install, "--attestation", "skip"], { PRAXIS_RELEASE_BASE_URL: url });
    assert.equal(result.status, 3, `${file}: ${result.stderr}`);
  }
  assert.deepEqual(fs.readdirSync(install), [], "nothing was installed");
});

test("an unavailable pinned release fails explicitly", { skip: skipUnsupported }, (t) => {
  const empty = temporary(t, "empty-release");
  const result = run(["--manifest", manifest(t, pinned), "--install-dir", temporary(t, "install"), "--attestation", "skip"], {
    PRAXIS_RELEASE_BASE_URL: `file://${empty}`
  });
  assert.equal(result.status, 4);
  assert.match(result.stderr, /9\.9\.9/);
});

test("a checksum mismatch is refused and nothing is installed", { skip: skipUnsupported }, (t) => {
  const { url } = release(t, { corruptChecksum: true });
  const install = temporary(t, "install");
  const result = run(["--manifest", manifest(t, pinned), "--install-dir", install, "--attestation", "skip"], { PRAXIS_RELEASE_BASE_URL: url });
  assert.equal(result.status, 5);
  assert.match(result.stderr, /checksum mismatch/);
  assert.deepEqual(fs.readdirSync(install), []);
});

test("a binary reporting another version is a version mismatch", { skip: skipUnsupported }, (t) => {
  const { url } = release(t, { reports: "9.9.8" });
  const result = run(["--manifest", manifest(t, pinned), "--install-dir", temporary(t, "install"), "--attestation", "skip"], {
    PRAXIS_RELEASE_BASE_URL: url
  });
  assert.equal(result.status, 6);
  assert.match(result.stderr, /reports version '9\.9\.8', not the pinned 9\.9\.9/);
});

test("attestation is required by default and a failed verification is refused", { skip: skipUnsupported }, (t) => {
  const { url } = release(t);
  const install = temporary(t, "install");
  const refused = run(["--manifest", manifest(t, pinned), "--install-dir", install], {
    PRAXIS_RELEASE_BASE_URL: url,
    PATH: `${stubGh(t, false)}:${process.env.PATH}`
  });
  assert.equal(refused.status, 5);
  assert.match(refused.stderr, /no valid build-provenance attestation/);

  const verified = run(["--manifest", manifest(t, pinned), "--install-dir", install], {
    PRAXIS_RELEASE_BASE_URL: url,
    PATH: `${stubGh(t, true)}:${process.env.PATH}`
  });
  assert.equal(verified.status, 0, verified.stderr);
});

test("a cache populated without attestation never satisfies a run that requires it", { skip: skipUnsupported }, (t) => {
  const { url } = release(t);
  const install = temporary(t, "install");
  const skipped = run(["--manifest", manifest(t, pinned), "--install-dir", install, "--attestation", "skip"], { PRAXIS_RELEASE_BASE_URL: url });
  assert.equal(skipped.status, 0, skipped.stderr);

  const required = run(["--manifest", manifest(t, pinned), "--install-dir", install], {
    PRAXIS_RELEASE_BASE_URL: url,
    PATH: `${stubGh(t, false)}:${process.env.PATH}`
  });
  assert.equal(required.status, 5);

  const cachedAgain = run(["--manifest", manifest(t, pinned), "--install-dir", install, "--attestation", "skip"], { PRAXIS_RELEASE_BASE_URL: url });
  assert.match(cachedAgain.stderr, /using cached Praxis 9\.9\.9/);
});

test("a tampered cache entry is re-verified and replaced, never trusted", { skip: skipUnsupported }, (t) => {
  const { url, digest } = release(t);
  const install = temporary(t, "install");
  assert.equal(run(["--manifest", manifest(t, pinned), "--install-dir", install, "--attestation", "skip"], { PRAXIS_RELEASE_BASE_URL: url }).status, 0);
  fs.appendFileSync(path.join(install, `9.9.9-${digest}`, `praxis-${RID}.tar.gz`), "tampered");
  const again = run(["--manifest", manifest(t, pinned), "--install-dir", install, "--attestation", "skip"], { PRAXIS_RELEASE_BASE_URL: url });
  assert.equal(again.status, 0, again.stderr);
  assert.doesNotMatch(again.stderr, /using cached/);
});

test("echelon install fails when the pinned installer cannot be downloaded (no silent success)", (t) => {
  const home = temporary(t, "echelon-home");
  const result = spawnSync("sh", [path.join(repositoryRoot, "bin", "echelon.sh"), "install", "praxis", "99.99.99"], {
    encoding: "utf8",
    env: { ...process.env, ECHELON_HOME: home, ECHELON_INSTALLER_BASE_URL: `file://${temporary(t, "no-installers")}` }
  });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /pinned version 99\.99\.99/);
  assert.equal(fs.existsSync(path.join(home, "tools", "praxis")), false);
});

test("released native assets are immutable, attested, and the setup action pins its dependencies", () => {
  const workflow = fs.readFileSync(path.join(repositoryRoot, ".github", "workflows", "native-release.yml"), "utf8");
  assert.doesNotMatch(workflow, /--clobber/);
  assert.match(workflow, /actions\/attest-build-provenance@[0-9a-f]{40}/);
  assert.match(workflow, /attestations: write/);

  const action = fs.readFileSync(path.join(repositoryRoot, ".github", "actions", "praxis-setup", "action.yml"), "utf8");
  const uses = [...action.matchAll(/uses:\s*(\S+)/g)].map((match) => match[1]);
  assert.ok(uses.length > 0);
  for (const reference of uses) assert.match(reference, /@[0-9a-f]{40}$/, `${reference} must be pinned to a commit SHA`);
  assert.doesNotMatch(action, /\$\{\{\s*inputs\.[a-z]+\s*\}\}"?\s*--/, "inputs reach the script through the environment, not interpolated into run");
});
