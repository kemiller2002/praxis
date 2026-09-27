import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { buildSite, publishedFiles, rewriteLinks, siteConfig, sitePath } from "../scripts/build-pages-site.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourceUrl = (file) => `https://github.com/o/r/blob/abc/${file}`;
const published = new Set(["README.md", "AGENTS.md", "docs/cli.md", "docs/00-governance/README.md"]);

test("links to published pages stay relative; the README becomes the home page", () => {
  assert.equal(sitePath("README.md"), "index.md");
  assert.equal(sitePath("docs/cli.md"), "docs/cli.md");
  assert.equal(rewriteLinks("[cli](docs/cli.md#exit-codes)", "README.md", published, sourceUrl), "[cli](docs/cli.md#exit-codes)");
  assert.equal(rewriteLinks("[home](../README.md#install)", "docs/cli.md", published, sourceUrl), "[home](../index.md#install)");
  assert.equal(rewriteLinks("[gov](00-governance/README.md)", "docs/cli.md", published, sourceUrl), "[gov](00-governance/README.md)");
});

test("links to unpublished repository files point at their GitHub source", () => {
  assert.equal(
    rewriteLinks("[wf](.github/workflows/publish.yml)", "README.md", published, sourceUrl),
    "[wf](https://github.com/o/r/blob/abc/.github/workflows/publish.yml)"
  );
  assert.equal(
    rewriteLinks("[m](../telemetry/metrics.json#L3)", "docs/cli.md", published, sourceUrl),
    "[m](https://github.com/o/r/blob/abc/telemetry/metrics.json#L3)"
  );
});

test("absolute URLs, anchors, and links above the repository root are untouched", () => {
  for (const text of ["[a](https://example.com/x.md)", "[b](#section)", "[c](mailto:x@example.com)", "[d](../../outside.md)"]) {
    assert.equal(rewriteLinks(text, "docs/cli.md", published, sourceUrl), text);
  }
});

test("the published set is the README, root guides, docs, research, and requirements -- never source or work state", () => {
  const files = publishedFiles(repositoryRoot);
  assert.ok(files.includes("README.md"));
  assert.ok(files.includes("docs/cli.md"));
  assert.ok(files.some((file) => file.startsWith("research/decisions/")));
  assert.ok(files.every((file) => /^(README|AGENTS|PACKAGE-USAGE|BOOTSTRAP|SDE-MAP)\.md$|^LICENSE$|^(docs|research|requirements)\//.test(file)), files.join("\n"));
  assert.ok(!files.some((file) => file.startsWith(".ros/") || file.startsWith("src/") || file.startsWith("starter/")));
});

test("the staged site resolves every relative link and carries the base URL", (t) => {
  const output = fs.mkdtempSync(path.join(os.tmpdir(), "praxis-pages-"));
  t.after(() => fs.rmSync(output, { recursive: true, force: true }));
  const files = buildSite({ root: repositoryRoot, output, repository: "o/r", ref: "abc", baseurl: "/r" });

  assert.ok(fs.existsSync(path.join(output, "index.md")));
  assert.match(fs.readFileSync(path.join(output, "_config.yml"), "utf8"), /^baseurl: "\/r"$/m);

  const unresolved = files
    .filter((file) => file.endsWith(".md"))
    .flatMap((file) => {
      const staged = path.join(output, sitePath(file));
      return [...fs.readFileSync(staged, "utf8").matchAll(/\]\(([^)\s]+)\)/g)]
        .map((match) => match[1])
        .filter((target) => !/^(?:[a-z][a-z0-9+.-]*:|\/\/|#)/i.test(target))
        .map((target) => decodeURI(target.split("#")[0]))
        .filter((target) => !fs.existsSync(path.join(path.dirname(staged), target)))
        .map((target) => `${file} -> ${target}`);
    });
  assert.deepEqual(unresolved, []);
});

test("site configuration enables the GitHub Pages Markdown link and title plugins", () => {
  const config = siteConfig({ title: "T", description: "D", repository: "o/r", baseurl: "/r" });
  for (const plugin of ["jekyll-relative-links", "jekyll-optional-front-matter", "jekyll-titles-from-headings"]) {
    assert.match(config, new RegExp(`- ${plugin}$`, "m"));
  }
});
