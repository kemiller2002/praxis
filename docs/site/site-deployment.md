# Public site deployment

The public site (`site/`) is deployed to GitHub Pages by
[`.github/workflows/site-pages.yml`](../../.github/workflows/site-pages.yml).
It is separate from the npm (`publish.yml`) and native (`native-release.yml`)
release workflows, and it cannot publish a package or a release.

## What the workflow does

1. Runs on a push to `main` that changes `site/`, `site-tools/` or the
   workflow itself, or when started by hand (`workflow_dispatch`).
2. Runs `node site-tools/verify.mjs`: structure, references, accessibility rules, the
   public/private boundary, the evidence check against `.ros` records, and the
   site tests. A failure stops the deployment.
3. Assembles `site/` into `_site/` with `site-tools/assemble.mjs` and checks
   the copy again.
4. Uploads `_site/` as the Pages artifact and deploys it with
   `actions/deploy-pages`. Only the deploy job has `pages: write` and
   `id-token: write`; the build job can only read.

The pull-request side is [`.github/workflows/site.yml`](../../.github/workflows/site.yml),
which runs the same checks and uploads the assembled artifact for review
without deploying.

## One-time setup a maintainer must do

This cannot be done from a workflow or by an agent without repository admin
rights.

1. **Settings > Pages > Build and deployment > Source: GitHub Actions.**
   Until this is set, `actions/configure-pages` fails and nothing is deployed.
2. **Settings > Environments > `github-pages`.** GitHub creates this environment
   the first time Pages is set to GitHub Actions. Its default deployment branch
   rule allows `main`; keep it that way.
3. After merging, run **Actions > Public site deployment > Run workflow** once,
   or push a change under `site/`.
4. The site is then served at `https://kemiller2002.github.io/praxis/` unless a
   custom domain is configured. All references in the site are relative, so it
   works under that sub-path and at a domain root.

## Status as of this change (2026-09-27)

- The workflow is committed on branch `claude/gh-84`, not yet on `main`, so it
  has never run.
- Whether Pages is enabled for the repository is **unknown**: the build
  environment could not reach the Pages API or `github.io`.
- **Nothing has been deployed.** Do not treat the site as published until the
  first successful run of "Public site deployment" on `main` is visible in the
  Actions tab and the Pages URL serves the page.
