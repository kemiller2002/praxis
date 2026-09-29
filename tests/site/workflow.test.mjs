// PRAXIS-SITE-21/22: the site's CI and deployment stay isolated from releases.
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { assemble } from "../../site-tools/assemble.mjs";

const read = (file) => readFileSync(new URL(`../../${file}`, import.meta.url), "utf8");

test("the site workflow checks, assembles and uploads with read-only permissions", () => {
  const workflow = read(".github/workflows/site.yml");
  assert.match(workflow, /permissions:\n  contents: read/);
  assert.match(workflow, /run: node site-tools\/verify\.mjs/);
  assert.match(workflow, /run: node site-tools\/assemble\.mjs _site/);
  assert.match(workflow, /actions\/upload-artifact@v4/);
  assert.ok(!/npm (ci|install)|dotnet|npm publish|release/.test(workflow.replace(/release workflows/g, "")), "no installs, builds or releases");
});

test("no site file can trigger a release workflow on main", () => {
  // native-release.yml republishes release assets on pushes to main that touch
  // its paths, so the site must live entirely outside them.
  const sitePaths = ["site/index.html", "site-tools/check.mjs", "tests/site/site.test.mjs", "docs/site/site-deployment.md", "docs/public-site.md", ".github/workflows/site.yml", ".github/workflows/deploy-pages.yml"];
  const glob = (pattern) => new RegExp(`^${pattern.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*\*/g, "\u0000").replace(/\*/g, "[^/]*").replace(/\u0000/g, ".*")}$`);
  ["native-release.yml", "ros-fs-assets.yml", "release.yml"].forEach((file) => {
    const workflow = read(`.github/workflows/${file}`);
    const push = workflow.split(/\n  push:\n/)[1] ?? "";
    const pushPaths = (push.split(/\n  [a-z_]+:/)[0].match(/^\s+- "([^"]+)"$/gm) ?? []).map((line) => line.trim().slice(3, -1));
    if (pushPaths.length === 0) return; // no push path filter (dispatch- or call-only workflows)
    sitePaths.forEach((file_) => assert.ok(!pushPaths.some((pattern) => glob(pattern).test(file_)), `${file_} matches ${file} push path`));
  });
  assert.ok(!existsSync(new URL("../../scripts/site", import.meta.url)), "site tooling must not live under scripts/");
  const manifest = JSON.parse(read("package.json"));
  assert.ok(!Object.keys(manifest.scripts).some((name) => name.startsWith("site")), "no site scripts in package.json");
});

test("release workflows do not know about the site", () => {
  ["release.yml", "native-release.yml", "ros-fs-assets.yml"].forEach((file) => {
    const workflow = read(`.github/workflows/${file}`);
    assert.ok(!/site\/|site-pages|pages/i.test(workflow), file);
  });
});

test("the npm package does not ship the public site", () => {
  const manifest = JSON.parse(read("package.json"));
  assert.ok(!manifest.files.some((entry) => /^(site|scripts\/site|docs\/site)/.test(entry)), manifest.files.join(", "));
});

test("assembly refuses to write outside the repository", () => {
  assert.throws(() => assemble("../elsewhere"), /outside the repository/);
});

test("the assembled artifact is exactly the checked site", () => {
  const { target, problems } = assemble("_site");
  assert.deepEqual(problems, []);
  assert.equal(readFileSync(`${target}/index.html`, "utf8"), read("site/index.html"));
  assert.ok(existsSync(`${target}/.nojekyll`));
});

test("the Pages workflow mirrors echelon-foundry's deploy-pages.yml and deploys only the checked artifact", () => {
  // Same shape as kemiller2002/echelon-foundry .github/workflows/deploy-pages.yml.
  const workflow = read(".github/workflows/deploy-pages.yml");
  assert.match(workflow, /^name: Deploy Site$/m);
  assert.match(workflow, /^on:\n  push:\n    branches:\n      - main\n  workflow_dispatch:$/m);
  assert.match(workflow, /^permissions:\n  contents: read\n  pages: write\n  id-token: write$/m);
  assert.match(workflow, /^concurrency:\n  group: pages\n  cancel-in-progress: true$/m);
  const build = workflow.slice(workflow.indexOf("  build:"), workflow.indexOf("  deploy:"));
  const deploy = workflow.slice(workflow.indexOf("  deploy:"));
  ["Checkout repository", "Set up Node.js", "Build site", "Configure Pages", "Upload Pages artifact"].forEach((step) =>
    assert.ok(build.includes(`- name: ${step}`), step)
  );
  assert.ok(build.indexOf("node site-tools/verify.mjs") < build.indexOf("upload-pages-artifact"), "checks run before upload");
  assert.match(build, /node site-tools\/assemble\.mjs dist/);
  assert.match(build, /path: dist/);
  assert.ok(!/npm run/.test(build), "no npm scripts: package.json stays out of the site");
  assert.match(deploy, /needs: build/);
  assert.match(deploy, /- name: Deploy to GitHub Pages\n        id: deployment\n        uses: actions\/deploy-pages@v4/);
  assert.ok(!existsSync(new URL("../../.github/workflows/site-pages.yml", import.meta.url)), "old workflow removed");
});

test("the deployment document never reports a deployment that has not happened", () => {
  const doc = read("docs/site/site-deployment.md");
  assert.match(doc, /Source: GitHub Actions/);
  // Every deployment the document claims names the Actions run that did it.
  const deployments = doc.slice(doc.indexOf("## Deployments"));
  const claimed = deployments.split("\n- ").slice(1);
  claimed.forEach((entry) => assert.match(entry, /Actions run \d+/, entry));
});
