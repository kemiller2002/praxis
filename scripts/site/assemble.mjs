#!/usr/bin/env node
// Assembles the deployable public-site artifact (docs/public-site.md).
//
//   node scripts/site/assemble.mjs [OUT]     copy site/ to OUT (default _site/) and check the copy
//
// The artifact is exactly the files under site/: nothing is generated, bundled
// or fetched at this stage. The copy is checked again with the same rules as
// the source, so what is uploaded is what was verified.

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { checkSite, requiredFiles } from "./check.mjs";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");

export const assemble = (out) => {
  const target = path.resolve(repoRoot, out);
  if (!target.startsWith(repoRoot + path.sep)) throw new Error(`refusing to assemble outside the repository: ${out}`);
  fs.rmSync(target, { recursive: true, force: true });
  fs.cpSync(path.join(repoRoot, "site"), target, { recursive: true });
  const problems = checkSite(target);
  const missing = requiredFiles.filter((file) => !fs.existsSync(path.join(target, file)));
  return { target, problems: [...problems, ...missing.map((file) => `artifact is missing ${file}`)] };
};

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { target, problems } = assemble(process.argv[2] ?? "_site");
  problems.forEach((problem) => console.error(problem));
  console.log(problems.length === 0 ? `assembled ${path.relative(repoRoot, target)}` : `artifact check failed: ${problems.length} problem(s)`);
  process.exitCode = problems.length === 0 ? 0 : 1;
}
