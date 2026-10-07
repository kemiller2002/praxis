#!/usr/bin/env node
// Runs every public-site check (docs/public-site.md) in order and stops at the
// first failure:
//
//   node site-tools/verify.mjs
//
// 1. check.mjs     structure, references, accessibility rules, boundary
// 2. evidence.mjs  the snapshot matches the Praxis records; the page matches the snapshot
// 3. node --test   tests/site/*.test.mjs
//
// The site tooling lives in site-tools/, outside scripts/ and package.json,
// so that no change to the site can trigger the npm or native release
// workflows (tests/site/workflow.test.mjs enforces this).

import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const tests = fs
  .readdirSync(path.join(repoRoot, "tests", "site"))
  .filter((name) => name.endsWith(".test.mjs"))
  .sort()
  .map((name) => path.join("tests", "site", name));

const steps = [
  ["site-tools/check.mjs"],
  ["site-tools/evidence.mjs", "--check"],
  ["--test", ...tests],
];

const failed = steps.find((args) => spawnSync(process.execPath, args, { cwd: repoRoot, stdio: "inherit" }).status !== 0);
process.exitCode = failed ? 1 : 0;
