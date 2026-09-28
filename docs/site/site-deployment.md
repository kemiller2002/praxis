# Public site deployment

The public site (`site/`) is deployed to GitHub Pages by
[`.github/workflows/deploy-pages.yml`](../../.github/workflows/deploy-pages.yml)
("Deploy Site"). It has the same shape as
`kemiller2002/echelon-foundry`'s `.github/workflows/deploy-pages.yml`, so the
Echelon Foundry sites deploy the same way. It is separate from the release
workflows (`native-release.yml`), and it cannot publish a package or a
release.

## What the workflow does

1. Runs on every push to `main`, or when started by hand
   (`workflow_dispatch`). Concurrent runs share the `pages` group, and a newer
   run cancels one in progress.
2. **Set up .NET** installs the .NET 10 SDK (`actions/setup-dotnet`). The site
   tooling is the F# console project `site-tools/SiteTools.fsproj`
   (`praxis-site`), with no package dependencies.
3. **Build site** runs `dotnet run --project site-tools/SiteTools.fsproj -c
   Release -- verify`: structure, references, accessibility rules, the
   public/private boundary, the evidence check against `.ros` records, and the
   site tests (`tests/Site.Tests`). A failure stops the deployment. It then
   assembles `site/` into `dist/` with `-- assemble dist` and checks the copy
   again.
4. Uploads `dist/` as the Pages artifact and deploys it with
   `actions/deploy-pages` to the `github-pages` environment.

Where it differs from echelon-foundry: Praxis has no `npm run build`. The site
is static, and its tooling lives in `site-tools/` and `tests/Site.Tests/`,
outside every path `native-release.yml` watches on pushes to `main` (it
re-uploads release assets when those change). So "Build site" runs the F#
site tool directly.
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
