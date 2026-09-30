namespace Ros.Tests

open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq

/// Release and packaging behavior main added after PR #92 diverged
/// (DF-ROS-2026-A044 on main: npm retired, native bundles plus a .NET global
/// tool, legacy ros-fs assets kept for already-scaffolded projects, one-click
/// release). Ported from main's Node checks in tests/npm-bootstrap.test.mjs so
/// PR #92's removal of Node does not leave it untested
/// (PRAXIS-PR92-PREMERGE-REGRESSION-FENCE). Assertions name artifacts and
/// channels, never the repository's version-source file, so they hold on
/// either side of the version-source reconciliation.
[<RequireQualifiedAccess>]
module PremergeReleaseTests =
    open PremergeFence

    let private workflow name =
        readRepositoryFile (Path.Combine(".github", "workflows", name))

    let private workflows () =
        Directory.GetFiles(repositoryFile ".github/workflows", "*.yml") |> Array.map File.ReadAllText |> List.ofArray

    let tests =
        [ { Name = "fence release: npm publishing stays retired; no workflow publishes to npm"
            Run =
              fun () ->
                  Assert.isTrue (not (File.Exists(repositoryFile ".github/workflows/publish.yml"))) "publish.yml must stay retired"
                  // Commands only: comments may still mention the retired workflow.
                  workflows ()
                  |> List.collect (fun text -> text.Split('\n') |> List.ofArray)
                  |> List.filter (fun line -> not (line.TrimStart().StartsWith("#", System.StringComparison.Ordinal)))
                  |> List.iter (fun line -> excludes "npm publish" line "workflow command") }

          { Name = "fence release: native bundles keep their own checksums, separate from the legacy ros-fs checksums"
            Run =
              fun () ->
                  let native = workflow "native-release.yml"
                  contains "native-checksums.txt" native "native release"
                  excludes "dist/native/checksums.txt" native "native release"
                  contains "make_posix_bundle linux-x64" native "native release"
                  contains "praxis-win-x64" native "native release" }

          { Name = "fence release: every release still carries the ros-fs-<rid> assets already-scaffolded projects download"
            Run =
              fun () ->
                  // Projects scaffolded before the rename run a launcher that downloads
                  // ros-fs-<rid> and checksums.txt for their pinned version. Dropping
                  // these assets is a product decision, not a merge side effect.
                  contains "uses: ./.github/workflows/ros-fs-assets.yml" (workflow "native-release.yml") "native release"
                  let assets = workflow "ros-fs-assets.yml"

                  [ "linux-x64"; "linux-arm64"; "osx-x64"; "osx-arm64"; "win-x64" ]
                  |> List.iter (fun rid -> contains rid assets "legacy asset platforms")

                  contains "ros-fs-$rid" assets "legacy asset name"
                  contains "sha256sum ros-fs-* > checksums.txt" assets "legacy checksums"
                  // An existing release must not short-circuit the upload, and existing assets are never replaced.
                  contains "\"checksums.txt\"" assets "legacy completeness check"
                  excludes "--clobber" assets "assets are immutable" }

          { Name = "fence release: the one-click release dispatches the native release"
            Run =
              fun () ->
                  let release = workflow "release.yml"
                  contains "gh workflow run native-release.yml" release "one-click release"
                  excludes "publish.yml" release "one-click release" }

          { Name = "fence release: the CLI still packs as the EchelonFoundry.Praxis .NET global tool whose command is praxis"
            Run =
              fun () ->
                  let project = XDocument.Load(repositoryFile "src/Ros.Cli/Ros.Cli.fsproj")

                  let property name =
                      project.Descendants(XName.Get name) |> Seq.map (fun element -> element.Value.Trim()) |> List.ofSeq

                  Assert.equal [ "true" ] (property "PackAsTool")
                  Assert.equal [ "praxis" ] (property "ToolCommandName")
                  Assert.equal [ "EchelonFoundry.Praxis" ] (property "PackageId")
                  let native = workflow "native-release.yml"
                  contains "EchelonFoundry.Praxis" native "native release packs the tool"
                  Assert.isTrue (Regex.IsMatch(native, "dotnet pack")) "the native release packs the tool" }

          { Name = "fence release: the native release publishes an attested echelon-release.json manifest"
            Run = fun () -> contains "echelon-release.json" (workflow "native-release.yml") "release manifest" } ]
