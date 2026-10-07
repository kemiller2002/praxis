namespace Praxis.Tests

open System.IO
open System.Text.RegularExpressions
open System.Text.Json.Nodes
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
                  let project = XDocument.Load(repositoryFile "src/Praxis.Cli/Praxis.Cli.fsproj")

                  let property name =
                      project.Descendants(XName.Get name) |> Seq.map (fun element -> element.Value.Trim()) |> List.ofSeq

                  Assert.equal [ "true" ] (property "PackAsTool")
                  Assert.equal [ "praxis" ] (property "ToolCommandName")
                  Assert.equal [ "EchelonFoundry.Praxis" ] (property "PackageId")
                  let native = workflow "native-release.yml"
                  contains "EchelonFoundry.Praxis" native "native release packs the tool"
                  Assert.isTrue (Regex.IsMatch(native, "dotnet pack")) "the native release packs the tool" }

          { Name = "fence release: the native release publishes an attested echelon-release.json manifest"
            Run = fun () -> contains "echelon-release.json" (workflow "native-release.yml") "release manifest" }

          { Name = "fence release: release.json declares the state compatibility this build reads and writes"
            Run =
              fun () ->
                  let declared = (JsonNode.Parse(readRepositoryFile "release.json")).["compatibility"]
                  let compiled = Praxis.Domain.Remote.StateCompatibility.current
                  Assert.equal compiled.RemoteProtocol (declared["remoteProtocol"].GetValue<string>())

                  let schemas = declared["stateSchemas"].AsObject()
                  Assert.equal (compiled.Reads |> Map.keys |> Set.ofSeq) (schemas |> Seq.map (fun entry -> entry.Key) |> Set.ofSeq)

                  for entry in schemas do
                      let reads = entry.Value["reads"].AsArray() |> Seq.map (fun value -> value.GetValue<string>()) |> Seq.toList
                      let compiledReads = compiled.Reads[entry.Key]
                      let compiledWrites = compiled.Writes[entry.Key]
                      Assert.equal compiledReads reads
                      Assert.equal compiledWrites (entry.Value["writes"].GetValue<string>())
                      Assert.isTrue (List.contains compiledWrites reads) $"{entry.Key}: a release reads what it writes" }

          { Name = "fence release: the self-hosting pin names a published release compatible with the repository state"
            Run =
              fun () ->
                  // PRX-QUAL-010: compatibility, not version equality, is the
                  // authority. Source may advance release.json past the pin;
                  // the pin must be a recorded (published, verified) release
                  // whose reads cover the committed state, or the gap must be
                  // an owned, unexpired exception.
                  let semver (value: string) =
                      let parts = value.Split '.'
                      Assert.equal 3 parts.Length
                      parts |> Array.map int |> fun numbers -> numbers[0], numbers[1], numbers[2]

                  let pinned = (JsonNode.Parse(readRepositoryFile ".echelon/toolchain.json")).["praxis"].GetValue<string>()
                  let released = (JsonNode.Parse(readRepositoryFile "release.json")).["version"].GetValue<string>()
                  Assert.isTrue (semver pinned <= semver released) $"the pin {pinned} must not be ahead of the source version {released}"

                  let record = JsonNode.Parse(readRepositoryFile "quality/release-compatibility.json")
                  let release = record["releases"][pinned]
                  Assert.isTrue (not (isNull release)) $"the pinned release {pinned} has no recorded compatibility in quality/release-compatibility.json"

                  let reads =
                      release["reads"].AsObject()
                      |> Seq.map (fun entry -> entry.Key, entry.Value.AsArray() |> Seq.map (fun value -> value.GetValue<string>()) |> Seq.toList)
                      |> Map.ofSeq

                  let compatibility = { Praxis.Domain.Remote.StateCompatibility.current with Reads = reads }

                  let observed =
                      match Praxis.Infrastructure.Remote.FileStateCompatibility.observe (repositoryFile ".") with
                      | Ok observed -> observed
                      | Error message -> failwith message

                  let today = System.DateOnly.FromDateTime System.DateTime.UtcNow

                  let excepted (gap: Praxis.Domain.Remote.StateIncompatibility) =
                      record["exceptions"].AsArray()
                      |> Seq.exists (fun entry ->
                          entry["release"].GetValue<string>() = pinned
                          && entry["document"].GetValue<string>() = gap.Document
                          && entry["version"].GetValue<string>() = gap.Version
                          && entry["owner"].GetValue<string>() <> ""
                          && entry["rationale"].GetValue<string>() <> ""
                          && System.DateOnly.Parse(entry["expires"].GetValue<string>()) >= today)

                  Praxis.Domain.Remote.StateCompatibility.check compatibility observed
                  |> List.filter (excepted >> not)
                  |> List.map Praxis.Domain.Remote.StateCompatibility.describe
                  |> Assert.empty }

          { Name = "fence release: the version bump never moves the pin; the pin advances only after assets and attestations verify"
            Run =
              fun () ->
                  let bump = readRepositoryFile "scripts/praxis-release-bump.sh"
                  Assert.isTrue (not (bump.Contains "git add release.json .echelon/toolchain.json")) "the bump must not commit the pin"
                  contains "git add release.json .ros" bump "the bump commits release.json and the Praxis state"
                  let enable = readRepositoryFile "scripts/praxis-remote-enable.sh"
                  let verify = enable.IndexOf("gh attestation verify")
                  let pin = enable.IndexOf("toolchain[\"praxis\"] = version")
                  Assert.isTrue (verify > 0 && pin > verify) "the pin is written only after the attestation verifies"
                  contains "quality/release-compatibility.json" enable "the pinned release's compatibility is recorded" }

          { Name = "fence release: the version bump and remote enablement stamp work transitions with real millisecond timestamps"
            Run =
              fun () ->
                  // A whole-second stamp can predate the execution `work start`
                  // opened moments earlier in the same second, and validation
                  // then rejects the checkpoint as dated before its execution
                  // started (ordo#54).
                  for script in [ "scripts/praxis-release-bump.sh"; "scripts/praxis-remote-enable.sh" ] do
                      let definition =
                          (readRepositoryFile script).Split '\n'
                          |> Array.tryFind (fun line -> line.StartsWith "now()")
                          |> Option.defaultWith (fun () -> failwith $"no now() in {script}")

                      let info = System.Diagnostics.ProcessStartInfo("bash", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
                      info.ArgumentList.Add "-c"
                      info.ArgumentList.Add(definition + "\nfor _ in 1 2 3 4; do now; sleep 0.02; done")
                      use proc = System.Diagnostics.Process.Start info
                      let output = proc.StandardOutput.ReadToEnd()
                      proc.WaitForExit()
                      Assert.equal 0 proc.ExitCode

                      let stamps = output.Split('\n', System.StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
                      Assert.equal 4 stamps.Length

                      for stamp in stamps do
                          Assert.isTrue (Regex.IsMatch(stamp, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")) $"not a millisecond UTC timestamp: {stamp}"

                      // Calls 20ms apart must be distinct and ordered, which a
                      // whole-second clock padded with .000 cannot be.
                      for earlier, later in List.pairwise stamps do
                          Assert.isTrue (System.String.CompareOrdinal(earlier, later) < 0) $"timestamps must strictly increase: {earlier} then {later}" } ]
