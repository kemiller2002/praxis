import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const starterRoot = path.join(packageRoot, "starter");

function availableProfiles() {
  return fs
    .readdirSync(starterRoot, { withFileTypes: true })
    .filter((entry) => entry.isDirectory() && fs.existsSync(path.join(starterRoot, entry.name, "manifest.json")))
    .map((entry) => entry.name)
    .sort();
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, "utf8"));
}

function normalizeTarget(target) {
  return path.resolve(target || process.cwd());
}

function slugify(value) {
  return value
    .normalize("NFKD")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "") || "project";
}

export function deriveProjectName(target) {
  const basename = path.basename(normalizeTarget(target)).replace(/\.git$/i, "");
  const words = basename
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[-_]+/g, " ")
    .trim()
    .split(/\s+/)
    .filter(Boolean);
  if (!words.length) {
    throw new Error("could not derive a project name from --target; provide --project");
  }
  return words
    .map((word) =>
      word === word.toUpperCase() && /[A-Z]/.test(word)
        ? word
        : `${word.charAt(0).toUpperCase()}${word.slice(1).toLowerCase()}`
    )
    .join(" ");
}

function render(content, variables) {
  return content.replace(/\{\{([A-Z_]+)\}\}/g, (match, key) => {
    if (!(key in variables)) {
      throw new Error(`unknown template variable ${match}`);
    }
    return variables[key];
  });
}

function sha256(content) {
  return crypto.createHash("sha256").update(content).digest("hex");
}

function safeDestination(root, relative) {
  const destination = path.resolve(root, relative);
  const prefix = root.endsWith(path.sep) ? root : `${root}${path.sep}`;
  if (destination !== root && !destination.startsWith(prefix)) {
    throw new Error(`unsafe destination path: ${relative}`);
  }
  return destination;
}

const stableVersionPath = path.join(packageRoot, "lib", "stable-ros-version.json");

// A main-branch snapshot install's own package.json version (e.g.
// "2.0.1-main.78.1") never has a matching GitHub Release, so ./ros can never
// fetch a binary for it (DF-ROS-2026-A029's launcher only ever resolves a
// stable vX.Y.Z tag). publish.yml bundles lib/stable-ros-version.json into
// every tarball it publishes, naming the real committed version at build
// time -- always a plain, binary-backed release, even for a snapshot build
// -- so a scaffolded project's ros.json points at a version ./ros can
// actually run, regardless of which exact snapshot was installed to
// bootstrap it. A source checkout (this repository itself, or a
// github:...#<ref> install, neither of which goes through publish.yml) has
// no such file, so this always falls back to the real installed version.
export function resolveRosVersion(packageMetadata, stableVersionOverride) {
  return stableVersionOverride?.version ?? packageMetadata.version;
}

// Reading the override is a separate, injectable step so a caller can state
// which case it means rather than inherit whatever is on disk. publish.yml
// writes lib/stable-ros-version.json into the working tree before it
// publishes, and its snapshot publish then re-runs this suite through
// `prepack`, so "no override present" is not something a test can assume.
export function readStableVersionOverride() {
  return fs.existsSync(stableVersionPath) ? readJson(stableVersionPath) : null;
}

function loadPlan({
  target,
  project,
  profile = "greenfield",
  stableVersionOverride = readStableVersionOverride()
}) {
  const profiles = availableProfiles();
  if (!profiles.includes(profile)) {
    throw new Error(`unsupported profile '${profile}'; available profiles: ${profiles.join(", ")}`);
  }
  const definition = readJson(path.join(starterRoot, profile, "manifest.json"));
  const packageMetadata = readJson(path.join(packageRoot, "package.json"));
  const created = new Date().toISOString().slice(0, 10);
  const variables = {
    PROJECT_NAME: project,
    PROJECT_SLUG: slugify(project),
    CREATED_DATE: created,
    ROS_VERSION: resolveRosVersion(packageMetadata, stableVersionOverride)
  };
  const files = definition.files.map((entry) => {
    const source = safeDestination(packageRoot, entry.source);
    const destination = safeDestination(target, entry.destination);
    const raw = fs.readFileSync(source);
    const content = entry.template
      ? Buffer.from(render(raw.toString("utf8"), variables), "utf8")
      : raw;
    return {
      source,
      destination,
      relative: entry.destination,
      policy: entry.policy ?? "managed",
      mode: entry.executable ? 0o755 : 0o644,
      content,
      checksum: sha256(content),
      preexisting: fs.existsSync(destination),
      preexistingChecksum: fs.existsSync(destination)
        ? sha256(fs.readFileSync(destination))
        : null
    };
  });
  return { definition, packageMetadata, variables, files };
}

function collisionList(files) {
  return files
    .filter(
      (entry) =>
        entry.preexisting &&
        entry.policy !== "preserve-existing" &&
        entry.preexistingChecksum !== entry.checksum
    )
    .map((entry) => entry.relative);
}

function installationManifest({ plan, profile }) {
  return {
    schema_version: "1.0.0",
    package: plan.packageMetadata.name,
    package_version: plan.packageMetadata.version,
    profile,
    project: plan.variables.PROJECT_NAME,
    project_slug: plan.variables.PROJECT_SLUG,
    installed_on: plan.variables.CREATED_DATE,
    update_policy: "additive; collisions require explicit migration",
    files: plan.files.map((entry) => ({
      path: entry.relative,
      sha256:
        entry.policy === "preserve-existing" && entry.preexisting
          ? entry.preexistingChecksum
          : entry.checksum,
      managed: !(entry.policy === "preserve-existing" && entry.preexisting),
      disposition:
        entry.policy === "preserve-existing" && entry.preexisting
          ? "preserved-existing"
          : entry.preexisting
            ? "adopted-identical"
            : "installed"
    }))
  };
}

function writeInstallationAttribution({ root, plan, profile }) {
  const now = new Date().toISOString();
  const workItem = `ROS-INSTALL-${plan.packageMetadata.version.replaceAll(".", "-")}`;
  const paths = [...plan.files.map((entry) => entry.relative), ".ros/installation.json"].sort();
  const event = {
    schemaVersion: "1.0.0",
    type: "work.completed",
    workItem,
    repository: plan.variables.PROJECT_SLUG,
    protocolVersion: "1.0.0",
    occurredAt: now,
    evidence: [{ type: "installation", path: ".ros/installation.json" }],
    paths,
    // The installer is automation, not an agent or human; the F# installer
    // writes the identical canonical praxis.actor/1 (DF-ROS-2026-A036).
    actor: { kind: "automation", id: "ros-bootstrap", runtime: "ros-bootstrap", executionId: "unknown", assurance: "self-reported" },
    publication: { status: "pending" }
  };
  event.eventId = crypto.createHash("sha256").update(JSON.stringify(event)).digest("hex").slice(0, 24);
  writeJsonFile(path.join(root, ".ros", "context", "current.json"), {
    schemaVersion: "1.0.0",
    protocolVersion: "1.0.0",
    repository: plan.variables.PROJECT_SLUG,
    actor: "ros-bootstrap",
    startedAt: now,
    updatedAt: now,
    baselineDirtyPaths: [],
    workItems: [{
      id: workItem,
      type: "mechanical",
      state: "complete",
      semanticState: "complete",
      evidence: event.evidence,
      updatedAt: now,
      completedAt: now
    }]
  });
  fs.mkdirSync(path.join(root, ".ros", "events"), { recursive: true });
  fs.writeFileSync(path.join(root, ".ros", "events", "events.jsonl"), `${JSON.stringify(event)}\n`, "utf8");
  writeJsonFile(path.join(root, ".ros", "work", "queue.json"), {
    schemaVersion: "1.0.0",
    repository: plan.variables.PROJECT_SLUG,
    nextSeq: 1,
    items: []
  });
  fs.writeFileSync(
    path.join(root, ".ros", "work", "queue.md"),
    "# Work Queue\n\n| ID | Work | Status | Tags | Priority |\n|---|---|---|---|---|\n",
    "utf8"
  );
  if (profile === "project-administration") {
    writeJsonFile(path.join(root, ".ros", "hub", "registry.json"), {
      schemaVersion: "1.0.0",
      repos: []
    });
    fs.writeFileSync(
      path.join(root, ".ros", "hub", "registry.md"),
      "# Registered Repositories\n\n| ID | Name | Path |\n|---|---|---|\n",
      "utf8"
    );
  }
}

function writeJsonFile(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

export function initializeProject({
  target = process.cwd(),
  project,
  profile = "greenfield",
  dryRun = false,
  stableVersionOverride = readStableVersionOverride()
}) {
  const root = normalizeTarget(target);
  const resolvedProject = project?.trim() || deriveProjectName(root);
  if (!fs.existsSync(root)) {
    if (!dryRun) fs.mkdirSync(root, { recursive: true });
  } else if (!fs.statSync(root).isDirectory()) {
    throw new Error(`target is not a directory: ${root}`);
  }

  const plan = loadPlan({ target: root, project: resolvedProject, profile, stableVersionOverride });
  const manifestDestination = safeDestination(root, ".ros/installation.json");
  const collisions = [
    ...collisionList(plan.files),
    ...(fs.existsSync(manifestDestination) ? [".ros/installation.json"] : [])
  ];
  if (collisions.length) {
    throw new Error(
      `installation would overwrite ${collisions.length} file(s):\n${collisions
        .map((item) => `- ${item}`)
        .join("\n")}`
    );
  }

  if (!dryRun) {
    for (const entry of plan.files) {
      if (entry.preexisting) continue;
      fs.mkdirSync(path.dirname(entry.destination), { recursive: true });
      fs.writeFileSync(entry.destination, entry.content, { mode: entry.mode });
      fs.chmodSync(entry.destination, entry.mode);
    }
    const installed = installationManifest({ plan, profile });
    fs.mkdirSync(path.dirname(manifestDestination), { recursive: true });
    fs.writeFileSync(manifestDestination, `${JSON.stringify(installed, null, 2)}\n`, "utf8");
    writeInstallationAttribution({ root, plan, profile });
  }
  return {
    root,
    dryRun,
    files: plan.files.map((entry) => entry.relative),
    manifest: ".ros/installation.json",
    packageVersion: plan.packageMetadata.version,
    rosVersion: plan.variables.ROS_VERSION,
    project: resolvedProject
  };
}

export function verifyProject({ target = process.cwd() }) {
  const root = normalizeTarget(target);
  const installedPath = safeDestination(root, ".ros/installation.json");
  if (!fs.existsSync(installedPath)) {
    throw new Error(`installation manifest not found: ${installedPath}`);
  }
  const installed = readJson(installedPath);
  const findings = [];
  for (const entry of installed.files || []) {
    if (entry.managed === false) continue;
    const file = safeDestination(root, entry.path);
    if (!fs.existsSync(file)) {
      findings.push(`${entry.path}: missing`);
      continue;
    }
    const actual = sha256(fs.readFileSync(file));
    if (actual !== entry.sha256) {
      findings.push(`${entry.path}: differs from installed ${installed.package_version} snapshot`);
    }
  }
  return { root, installed, findings };
}

function usage() {
  return `Repository Operating System bootstrap

Usage:
  ros-bootstrap init [--project "Project Name"] [--target .] [--profile greenfield] [--dry-run]
  ros-bootstrap verify [--target .]

Available profiles: ${availableProfiles().join(", ")}

If --project is omitted, the display name is derived from the target folder.
The initializer is additive and refuses to overwrite existing managed files.`;
}

function parseArguments(argv) {
  const [command, ...rest] = argv;
  const options = {};
  for (let index = 0; index < rest.length; index += 1) {
    const argument = rest[index];
    if (argument === "--dry-run") {
      options.dryRun = true;
      continue;
    }
    if (["--project", "--target", "--profile"].includes(argument)) {
      const value = rest[index + 1];
      if (!value || value.startsWith("--")) {
        throw new Error(`${argument} requires a value`);
      }
      options[argument.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())] = value;
      index += 1;
      continue;
    }
    throw new Error(`unknown argument: ${argument}`);
  }
  return { command, options };
}

export async function main(argv) {
  try {
    if (!argv.length || argv.includes("--help") || argv.includes("-h")) {
      console.log(usage());
      return 0;
    }
    const { command, options } = parseArguments(argv);
    if (command === "init") {
      const result = initializeProject(options);
      const verb = result.dryRun ? "would install" : "installed";
      console.log(`${verb} ROS ${result.packageVersion} into ${result.root}`);
      console.log(`project: ${result.project}`);
      console.log(`${result.files.length} file(s); manifest: ${result.manifest}`);
      if (!result.dryRun) {
        if (result.rosVersion !== result.packageVersion) {
          console.log(
            `note: installed from a main-branch snapshot (${result.packageVersion}); ` +
              `this project's ./ros is pinned to ${result.rosVersion}, the newest stable release with ` +
              "published binaries, since main-branch snapshots never have one of their own."
          );
        }
        console.log("next: ./ros registry check && ./ros validate");
      }
      return 0;
    }
    if (command === "verify") {
      const result = verifyProject(options);
      if (result.findings.length) {
        for (const finding of result.findings) console.error(`ERROR ${finding}`);
        console.error(`verification failed with ${result.findings.length} finding(s)`);
        return 1;
      }
      console.log(
        `installed snapshot verified: ${result.installed.package}@${result.installed.package_version}`
      );
      return 0;
    }
    throw new Error(`unknown command '${command || ""}'`);
  } catch (error) {
    console.error(`ERROR ${error.message}`);
    return 1;
  }
}
