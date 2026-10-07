# Public site deployment

The public site (`site/`) is deployed to GitHub Pages by
[`.github/workflows/deploy-pages.yml`](../../.github/workflows/deploy-pages.yml)
("Deploy Site"). It has the same shape as
`kemiller2002/echelon-foundry`'s `.github/workflows/deploy-pages.yml`, so the
Echelon Foundry sites deploy the same way. It is separate from the npm
(`publish.yml`) and native (`native-release.yml`) release workflows, and it
cannot publish a package or a release.

## What the workflow does

1. Runs on every push to `main`, or when started by hand
   (`workflow_dispatch`). Concurrent runs share the `pages` group, and a newer
   run cancels one in progress.
2. **Build site** runs `node site-tools/verify.mjs`: structure, references,
   accessibility rules, the public/private boundary, the evidence check against
   `.ros` records, and the site tests. A failure stops the deployment. It then
   assembles `site/` into `dist/` with `site-tools/assemble.mjs` and checks the
   copy again.
3. Uploads `dist/` as the Pages artifact and deploys it with
   `actions/deploy-pages` to the `github-pages` environment.

Where it differs from echelon-foundry: Praxis has no `npm run build`. The site
is static and its tooling stays out of `package.json`, because
`native-release.yml` re-uploads release assets on pushes to `main` that touch
`package.json` or `scripts/**`. So "Build site" calls the site tools directly.
Permissions are set once for the workflow (`contents: read`, `pages: write`,
`id-token: write`), as in echelon-foundry.

The pull-request side is [`.github/workflows/site.yml`](../../.github/workflows/site.yml).
It runs the same checks with read-only permissions and uploads the assembled
artifact for review without deploying.

## Repository settings

- **Settings > Pages > Build and deployment > Source: GitHub Actions.** This is
  already set: the first run's `configure-pages` step succeeded.
- **Settings > Environments > `github-pages`.** Keep its deployment branch rule
  limited to `main`.

The site is served at `https://kemiller2002.github.io/praxis/` unless a custom
domain is configured. All references in the site are relative, so it works
under that sub-path and at a domain root.

## Deployments

- 2026-09-27: first deployment, by the previous workflow
  (`site-pages.yml`, since replaced by `deploy-pages.yml`), on the merge of
  PR #85 (`7086625`). Actions run 36310800569: build and deploy both succeeded.
  The build environment could not reach `github.io`, so the served page was
  not inspected from here.
