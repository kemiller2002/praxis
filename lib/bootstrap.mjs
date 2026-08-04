import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const manifestPath = path.join(packageRoot, "starter", "greenfield", "manifest.json");

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

function loadPlan({ target, project, profile = "greenfield" }) {
  if (profile !== "greenfield") {
    throw new Error(`unsupported profile '${profile}'; available profiles: greenfield`);
  }
  const definition = readJson(manifestPath);
  const packageMetadata = readJson(path.join(packageRoot, "package.json"));
  const created = new Date().toISOString().slice(0, 10);
  const variables = {
    PROJECT_NAME: project,
    PROJECT_SLUG: slugify(project),
    CREATED_DATE: created,
    ROS_VERSION: packageMetadata.version
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

export function initializeProject({
  target = process.cwd(),
  project,
  profile = "greenfield",
  dryRun = false
}) {
  const root = normalizeTarget(target);
  const resolvedProject = project?.trim() || deriveProjectName(root);
  if (!fs.existsSync(root)) {
    if (!dryRun) fs.mkdirSync(root, { recursive: true });
  } else if (!fs.statSync(root).isDirectory()) {
    throw new Error(`target is not a directory: ${root}`);
  }

  const plan = loadPlan({ target: root, project: resolvedProject, profile });
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
  }
  return {
    root,
    dryRun,
    files: plan.files.map((entry) => entry.relative),
    manifest: ".ros/installation.json",
    packageVersion: plan.packageMetadata.version,
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
