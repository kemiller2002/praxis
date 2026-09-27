#!/usr/bin/env node
// Stages the documentation that GitHub Pages publishes (see
// .github/workflows/pages.yml): the repository README as the home page, the
// root guides, docs/, and the canonical research and requirement records.
// Relative links to files that are not published (source, workflows, work
// state) are rewritten to their GitHub source so the site never links to a
// missing page. Jekyll, run by the workflow, renders the staged Markdown.
//
//   node scripts/build-pages-site.mjs OUTPUT_DIR [--repository OWNER/REPO] [--ref REF] [--baseurl PATH]
//
// --repository and --ref default to GITHUB_REPOSITORY and GITHUB_SHA; --baseurl
// is the site's path prefix (`/praxis` for a project site; the workflow passes
// actions/configure-pages' base_path).

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT_PAGES = ["README.md", "AGENTS.md", "PACKAGE-USAGE.md", "BOOTSTRAP.md", "SDE-MAP.md", "LICENSE"];
const PUBLISHED_TREES = ["docs", "research", "requirements"];

// ---- pure core -----------------------------------------------------------

/** Where a repository file is published inside the site. */
export const sitePath = (repoPath) => (repoPath === "README.md" ? "index.md" : repoPath);

const isExternal = (target) => /^(?:[a-z][a-z0-9+.-]*:|\/\/|#)/i.test(target);

/**
 * Rewrites relative Markdown link targets in `text` (a file at `repoPath`)
 * that resolve to an unpublished repository path into GitHub source URLs.
 * Published targets, anchors, and absolute URLs are left untouched; links to
 * the README are pointed at the site's home page.
 */
export function rewriteLinks(text, repoPath, published, sourceUrl) {
  const directory = path.posix.dirname(repoPath);

  return text.replace(/(\]\()([^)\s]+)(\))/g, (match, open, target, close) => {
    if (isExternal(target)) return match;
    const [file, anchor = ""] = target.split(/(?=#)/);
    const resolved = path.posix.normalize(path.posix.join(directory, decodeURI(file)));
    if (resolved.startsWith("..")) return match;
    if (published.has(resolved)) {
      if (resolved !== "README.md") return match;
      const relative = path.posix.relative(path.posix.dirname(sitePath(repoPath)), "index.md");
      return `${open}${relative}${anchor}${close}`;
    }
    return `${open}${sourceUrl(resolved)}${anchor}${close}`;
  });
}

/** Jekyll configuration for the staged site. */
export const siteConfig = ({ title, description, repository, baseurl }) =>
  [
    `title: ${JSON.stringify(title)}`,
    `baseurl: ${JSON.stringify(baseurl)}`,
    `description: ${JSON.stringify(description)}`,
    `repository: ${JSON.stringify(repository)}`,
    "theme: jekyll-theme-primer",
    "markdown: kramdown",
    "plugins:",
    "  - jekyll-relative-links",
    "  - jekyll-optional-front-matter",
    "  - jekyll-titles-from-headings",
    "  - jekyll-default-layout",
    "relative_links:",
    "  enabled: true",
    "  collections: false",
    "optional_front_matter:",
    "  remove_originals: true",
    "titles_from_headings:",
    "  enabled: true",
    "  strip_title: false",
    ""
  ].join("\n");

// ---- effects -------------------------------------------------------------

const listMarkdown = (root, tree) => {
  const directory = path.join(root, tree);
  if (!fs.existsSync(directory)) return [];
  return fs
    .readdirSync(directory, { recursive: true, withFileTypes: true })
    .filter((entry) => entry.isFile() && entry.name.endsWith(".md") && !entry.name.startsWith("."))
    .map((entry) => path.relative(root, path.join(entry.parentPath ?? entry.path, entry.name)).split(path.sep).join("/"));
};

export function publishedFiles(root) {
  return [
    ...ROOT_PAGES.filter((file) => fs.existsSync(path.join(root, file))),
    ...PUBLISHED_TREES.flatMap((tree) => listMarkdown(root, tree))
  ].sort();
}

export function buildSite({ root, output, repository, ref, baseurl = "" }) {
  const files = publishedFiles(root);
  const published = new Set(files);
  const sourceUrl = (repoPath) => `https://github.com/${repository}/blob/${ref}/${repoPath}`;

  fs.rmSync(output, { recursive: true, force: true });

  for (const file of files) {
    const target = path.join(output, sitePath(file));
    fs.mkdirSync(path.dirname(target), { recursive: true });
    const text = fs.readFileSync(path.join(root, file), "utf8");
    fs.writeFileSync(target, file.endsWith(".md") ? rewriteLinks(text, file, published, sourceUrl) : text);
  }

  fs.writeFileSync(
    path.join(output, "_config.yml"),
    siteConfig({
      title: "Praxis",
      description: "Repository Operating System: durable research, engineering, decisions, and handoffs.",
      repository,
      baseurl
    })
  );

  return files;
}

// ---- command line ----------------------------------------------------------

const option = (args, name) => {
  const index = args.indexOf(name);
  return index >= 0 ? args[index + 1] : undefined;
};

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const output = args.find((arg, index) => !arg.startsWith("--") && !args[index - 1]?.startsWith("--"));
  const repository = option(args, "--repository") ?? process.env.GITHUB_REPOSITORY;
  const ref = option(args, "--ref") ?? process.env.GITHUB_SHA ?? "main";
  const baseurl = option(args, "--baseurl") ?? "";

  if (!output || !repository) {
    console.error("usage: node scripts/build-pages-site.mjs OUTPUT_DIR [--repository OWNER/REPO] [--ref REF] [--baseurl PATH]");
    process.exit(2);
  }

  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const files = buildSite({ root, output: path.resolve(output), repository, ref, baseurl });
  console.log(`staged ${files.length} page(s) into ${output}`);
}
