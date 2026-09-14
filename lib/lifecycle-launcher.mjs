// Bootstrap for the `ros` executable. Its entire job is to start the F# CLI
// and get out of the way: detect the platform, find the CLI binary, tell the
// CLI where this package's own files are, forward the arguments and stdio, and
// return the CLI's exit code.
//
// There is deliberately no repository logic here. What to install, what the
// repository's state means, whether an installation is valid, which migrations
// apply, which files are stale -- every one of those decisions belongs to the
// F# core (src/Ros.Domain/Lifecycle) and none of them is made in this file.

import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { run as runPublishedBinary } from "./ros-fs-launcher.mjs";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

// Where this package's scaffold lives. The CLI reads the scaffold itself; this
// only tells it which directory to read.
export function withPackageRoot(argv, root = packageRoot) {
  return argv.includes("--package-root") ? argv : ["--package-root", root, ...argv];
}

// A source checkout (or a CI job that just built the CLI) runs the freshly
// built assembly; a published install downloads and caches the release binary
// for its platform. Locating the executable is all this chooses between.
export function locateLocalAssembly(root = packageRoot) {
  const override = process.env.ROS_FS_DLL_PATH_OVERRIDE;
  if (override) {
    return fs.existsSync(override) ? override : null;
  }
  const built = path.join(root, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");
  return fs.existsSync(built) ? built : null;
}

export async function run(argv, { log = (message) => process.stderr.write(`${message}\n`) } = {}) {
  const args = withPackageRoot(argv);
  const assembly = locateLocalAssembly();

  if (assembly) {
    const result = spawnSync("dotnet", [assembly, ...args], { stdio: "inherit" });
    if (result.error) {
      log(`ros: failed to invoke dotnet for ${assembly}: ${result.error.message}`);
      return 1;
    }
    return result.status ?? 1;
  }

  return runPublishedBinary(args, { log });
}
