#!/usr/bin/env node
// Checks the public site (docs/public-site.md): required files, structural
// HTML, accessibility rules decidable from markup, relative references that
// resolve inside site/, and the public/private boundary. Exit 1 on problems.

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { accessibilityProblems, boundaryProblems, referenceProblems, structureProblems } from "./lib.mjs";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const siteRoot = path.join(repoRoot, "site");

export const requiredFiles = ["index.html", "assets/css/site.css", "robots.txt", ".nojekyll"];

const walk = (directory) =>
  fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(directory, entry.name);
    return entry.isDirectory() ? walk(full) : [full];
  });

export const checkSite = (root = siteRoot) => {
  const missing = requiredFiles.filter((file) => !fs.existsSync(path.join(root, file))).map((file) => `missing required file site/${file}`);
  const files = fs.existsSync(root) ? walk(root) : [];
  const relative = (file) => path.relative(root, file).split(path.sep).join("/");
  const textFiles = files.filter((file) => /\.(html|css|js|json|txt|svg)$/.test(file));
  const pages = files.filter((file) => file.endsWith(".html"));
  const perPage = pages.flatMap((file) => {
    const html = fs.readFileSync(file, "utf8");
    const exists = (reference) => fs.existsSync(path.resolve(path.dirname(file), reference));
    return [...structureProblems(html), ...accessibilityProblems(html), ...referenceProblems(html, exists)].map(
      (problem) => `site/${relative(file)}: ${problem}`,
    );
  });
  const cssReferences = files
    .filter((file) => file.endsWith(".css"))
    .flatMap((file) =>
      [...fs.readFileSync(file, "utf8").matchAll(/url\(\s*["']?([^"')]+)["']?\s*\)/g)]
        .map((m) => m[1])
        .filter((reference) => !/^(https?:|data:|#)/.test(reference))
        .filter((reference) => reference.startsWith("/") || !fs.existsSync(path.resolve(path.dirname(file), reference)))
        .map((reference) => `site/${relative(file)}: url(${reference}) is root-relative or missing`),
    );
  const boundary = textFiles.flatMap((file) => boundaryProblems(`site/${relative(file)}`, fs.readFileSync(file, "utf8")));
  return [...missing, ...perPage, ...cssReferences, ...boundary];
};

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const problems = checkSite();
  problems.forEach((problem) => console.error(`ERROR ${problem}`));
  console.log(problems.length === 0 ? "site check passed" : `site check failed with ${problems.length} problem(s)`);
  process.exitCode = problems.length === 0 ? 0 : 1;
}
