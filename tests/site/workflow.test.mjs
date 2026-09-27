// PRAXIS-SITE-21/22: the site's CI and deployment stay isolated from releases.
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { assemble } from "../../scripts/site/assemble.mjs";

const read = (file) => readFileSync(new URL(`../../${file}`, import.meta.url), "utf8");

test("the site workflow checks, assembles and uploads with read-only permissions", () => {
  const workflow = read(".github/workflows/site.yml");
  assert.match(workflow, /permissions:\n  contents: read/);
  assert.match(workflow, /run: npm run site:check/);
  assert.match(workflow, /run: node scripts\/site\/assemble\.mjs _site/);
  assert.match(workflow, /actions\/upload-artifact@v4/);
  assert.ok(!/npm (ci|install)|dotnet|npm publish|release/.test(workflow.replace(/release workflows/g, "")), "no installs, builds or releases");
});

test("release workflows do not know about the site", () => {
  ["publish.yml", "native-release.yml"].forEach((file) => {
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
