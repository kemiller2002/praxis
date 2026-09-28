/// PRAXIS-SITE-21/22: the site's CI and deployment stay isolated from releases.
module Site.Tests.WorkflowTests

open System
open System.IO
open System.Text.RegularExpressions
open Praxis.Site
open Site.Tests.Fixture

let private siteTool = "dotnet run --project site-tools/SiteTools.fsproj -c Release --"

/// A GitHub Actions path glob as a regular expression: `**` crosses
/// directories, `*` does not.
let private glob (pattern: string) =
    let escaped = Regex.Replace(pattern, @"[.+^${}()|[\]\\]", @"\$&")
    Regex("^" + escaped.Replace("**", "\u0000").Replace("*", "[^/]*").Replace("\u0000", ".*") + "$")

/// The `on.push.paths` filter of a workflow, or [] when it has none.
let private pushPaths (workflow: string) =
    let parts = Regex.Split(workflow, "\n  push:\n")
    let push = if parts.Length > 1 then parts[1] else ""
    let block = Regex.Split(push, "\n  [a-z_]+:")[0]

    Regex.Matches(block, "^\\s+- \"([^\"]+)\"$", RegexOptions.Multiline)
    |> Seq.map (fun found -> found.Groups[1].Value)
    |> List.ofSeq

let private releaseWorkflows =
    [ "native-release.yml"; "publish.yml" ]
    |> List.map (fun file -> $".github/workflows/{file}")
    |> List.filter exists

/// Every file under `directory`, skipping build output.
let rec private sources (directory: string) =
    if not (Directory.Exists directory) then
        []
    else
        let files = Directory.GetFiles directory |> List.ofArray

        let nested =
            Directory.GetDirectories directory
            |> List.ofArray
            |> List.filter (fun child -> Path.GetFileName child <> "bin" && Path.GetFileName child <> "obj")
            |> List.collect sources

        files @ nested

let tests =
    [ test "the site workflow checks, assembles and uploads with read-only permissions" (fun () ->
          let workflow = read ".github/workflows/site.yml"
          Assert.matches "permissions:\n  contents: read" workflow "read-only permissions"
          Assert.matches @"uses: actions/setup-dotnet@v6\n        with:\n          dotnet-version: ""10\.0\.x""" workflow ".NET 10 SDK"
          Assert.isTrue (workflow.Contains($"run: {siteTool} verify")) "verify"
          Assert.isTrue (workflow.Contains($"run: {siteTool} assemble _site")) "assemble"
          Assert.matches @"actions/upload-artifact@v4" workflow "upload"

          Assert.notMatches
              @"\bnpm\b|\bnpx\b|setup-node|\bnode |dotnet (publish|pack|nuget)|release"
              (workflow.Replace("release workflow", ""))
              "no Node, no packages, no publishing, no releases")

      test "no site file can trigger a release workflow on main" (fun () ->
          // native-release.yml republishes release assets on pushes to main that
          // touch its paths, so the site must live entirely outside them.
          let sitePaths =
              [ "site/index.html"
                "site-tools/SiteTools.fsproj"
                "site-tools/Evidence.fs"
                "tests/Site.Tests/Site.Tests.fsproj"
                "tests/Site.Tests/SiteTests.fs"
                "docs/site/site-deployment.md"
                "docs/public-site.md"
                ".github/workflows/site.yml"
                ".github/workflows/deploy-pages.yml" ]

          for file in releaseWorkflows do
              // No path filter: runs on every push regardless of the site (publish.yml).
              for pattern in pushPaths (read file) do
                  for sitePath in sitePaths do
                      Assert.isFalse ((glob pattern).IsMatch sitePath) $"{sitePath} matches {file} push path {pattern}"

          Assert.isFalse (exists "scripts/site") "site tooling must not live under scripts/"
          Assert.isFalse (exists "tests/site") "the Node site tests are gone"

          let scripted =
              sources (path "site-tools") @ sources (path "tests/Site.Tests")
              |> List.filter (fun file ->
                  Regex.IsMatch(Path.GetFileName file, @"\.(m?js|cjs|ts|mts)\z|^package(-lock)?\.json\z"))

          Assert.empty scripted)

      test "release workflows do not know about the site" (fun () ->
          for file in releaseWorkflows do
              Assert.notMatches "(?i)site/|site-pages|pages" (read file) file)

      test "the release payload does not ship the public site" (fun () ->
          let infrastructure = "src/Ros.Infrastructure/Ros.Infrastructure.fsproj"

          if exists infrastructure then
              let embedded =
                  Regex.Matches(read infrastructure, @"<EmbeddedResource Include=""\$\(RosRepositoryRoot\)([^""]+)""")
                  |> Seq.map (fun found -> found.Groups[1].Value)
                  |> List.ofSeq

              Assert.isTrue (not embedded.IsEmpty) "the payload is declared"

              for entry in embedded do
                  Assert.notMatches "^(site|site-tools|docs/site|tests/Site\\.Tests)(/|$)" entry entry)

      test "assembly refuses to write outside the repository" (fun () ->
          match Assemble.assemble root "../elsewhere" with
          | Error message -> Assert.matches "outside the repository" message message
          | Ok _ -> failwith "assembled outside the repository")

      test "the assembled artifact is exactly the checked site" (fun () ->
          match Assemble.assemble root "_site" with
          | Error message -> failwith message
          | Ok assembled ->
              Assert.empty assembled.Problems
              Assert.equal (read "site/index.html") (File.ReadAllText(Path.Combine(assembled.Target, "index.html")))
              Assert.isTrue (File.Exists(Path.Combine(assembled.Target, ".nojekyll"))) ".nojekyll")

      test "the Pages workflow mirrors echelon-foundry's deploy-pages.yml and deploys only the checked artifact" (fun () ->
          // Same shape as kemiller2002/echelon-foundry .github/workflows/deploy-pages.yml.
          let workflow = read ".github/workflows/deploy-pages.yml"
          Assert.matches "(?m)^name: Deploy Site$" workflow "name"
          Assert.matches "(?m)^on:\n  push:\n    branches:\n      - main\n  workflow_dispatch:$" workflow "triggers"
          Assert.matches "(?m)^permissions:\n  contents: read\n  pages: write\n  id-token: write$" workflow "permissions"
          Assert.matches "(?m)^concurrency:\n  group: pages\n  cancel-in-progress: true$" workflow "concurrency"
          let buildStart = workflow.IndexOf("  build:", StringComparison.Ordinal)
          let deployStart = workflow.IndexOf("  deploy:", StringComparison.Ordinal)
          let build = workflow.Substring(buildStart, deployStart - buildStart)
          let deploy = workflow.Substring deployStart

          for step in [ "Checkout repository"; "Set up .NET"; "Build site"; "Configure Pages"; "Upload Pages artifact" ] do
              Assert.isTrue (build.Contains($"- name: {step}")) step

          Assert.matches @"uses: actions/setup-dotnet@v6\n        with:\n          dotnet-version: ""10\.0\.x""" build ".NET 10 SDK"
          let verify = build.IndexOf($"{siteTool} verify", StringComparison.Ordinal)
          Assert.isTrue (verify >= 0 && verify < build.IndexOf("upload-pages-artifact", StringComparison.Ordinal)) "checks run before upload"
          Assert.isTrue (build.Contains($"{siteTool} assemble dist")) "assembles dist"
          Assert.matches "path: dist" build "uploads dist"
          Assert.notMatches @"\bnpm\b|\bnpx\b|setup-node|\bnode " build "no Node: the site tooling is F#"
          Assert.matches "needs: build" deploy "deploy needs build"

          Assert.matches
              "- name: Deploy to GitHub Pages\n        id: deployment\n        uses: actions/deploy-pages@v4"
              deploy
              "deploy step"

          Assert.isFalse (exists ".github/workflows/site-pages.yml") "old workflow removed")

      test "the deployment document never reports a deployment that has not happened" (fun () ->
          let doc = read "docs/site/site-deployment.md"
          Assert.matches "Source: GitHub Actions" doc "source"
          // Every deployment the document claims names the Actions run that did it.
          let deployments = doc.Substring(doc.IndexOf("## Deployments", StringComparison.Ordinal))

          for entry in deployments.Split("\n- ") |> Array.skip 1 do
              Assert.matches @"Actions run \d+" entry entry) ]
