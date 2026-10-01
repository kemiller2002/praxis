namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Xml.Linq

/// Migration guard for PRAXIS-INTERNAL-NAMESPACES: the public boundary the
/// internal `Ros.*` -> `Praxis.*` project, namespace and solution rename must
/// not move. Every assertion locates projects, launchers and workflows by what
/// they produce rather than by their internal names, so the same tests hold on
/// either side of the rename (DF-ROS-2026-A050 lists what stays `ros`).
[<RequireQualifiedAccess>]
module InternalNamingGuardTests =
    let private root () = CliPort.repositoryRoot.Value

    let private version () = CliPort.releaseVersion.Value

    let private repositoryPath (relative: string) = Path.Combine(root (), relative)

    let private posixOnly body =
        if not (OperatingSystem.IsWindows()) then body ()

    let private succeeded (result: CliHarness.Run) =
        CliPort.exitCode 0 result
        result

    let private property (name: string) (project: XDocument) =
        project.Descendants(XName.Get name) |> Seq.tryHead |> Option.map _.Value.Trim()

    /// Every source project, repository-relative with forward slashes.
    let private sourceProjects () =
        Directory.GetFiles(repositoryPath "src", "*.fsproj", SearchOption.AllDirectories)
        |> Array.map (fun path -> Path.GetRelativePath(root (), path).Replace('\\', '/'))
        |> List.ofArray

    /// The one source project whose assembly is the public `praxis` CLI.
    let private cliProject () =
        sourceProjects ()
        |> List.filter (fun path -> property "AssemblyName" (XDocument.Load(repositoryPath path)) = Some "praxis")
        |> Assert.single

    let private workflow name =
        File.ReadAllText(repositoryPath (Path.Combine(".github", "workflows", name)))

    /// `.fsproj`/`.slnx` paths a workflow passes to `dotnet`, in order.
    let private dotnetProjectPaths (text: string) =
        Regex.Matches(text, @"dotnet\s+(?:restore|build|publish|pack|run\s+--project)\s+([^\s]+\.(?:fsproj|slnx))")
        |> Seq.map _.Groups[1].Value
        |> List.ofSeq

    let tests =
        [ { Name = "internal-rename guard: exactly one source project builds the praxis CLI, packed as the praxis tool EchelonFoundry.Praxis"
            Run =
              fun () ->
                  let project = XDocument.Load(repositoryPath (cliProject ()))
                  Assert.equal (Some "praxis") (property "ToolCommandName" project)
                  Assert.equal (Some "EchelonFoundry.Praxis") (property "PackageId" project)
                  Assert.equal (Some "Exe") (property "OutputType" project)
                  Assert.isTrue (File.Exists CliHarness.cli) $"the built CLI assembly is praxis.dll: {CliHarness.cli}"

                  // No other project may claim the executable identity or ship
                  // an assembly named after the internal CLI project.
                  for path in sourceProjects () do
                      let name = property "AssemblyName" (XDocument.Load(repositoryPath path))
                      Assert.isTrue (name <> Some "ros-fs") $"{path} must not revive the pre-rename ros-fs assembly"

                  for stale in [ "Ros.Cli.dll"; "Praxis.Cli.dll"; "ros-fs.dll" ] do
                      Assert.isTrue (not (File.Exists(Path.Combine(AppContext.BaseDirectory, stale)))) $"{stale} must not be built" }

          { Name = "internal-rename guard: ./praxis runs the CLI project's praxis.dll and ./ros --version equals ./praxis --version"
            Run =
              fun () ->
                  let launcher = File.ReadAllText(repositoryPath "praxis")
                  let cliDirectory = Path.GetDirectoryName(cliProject ()).Replace('\\', '/')
                  let expected = $"$repo_root/{cliDirectory}/bin/Release/net10.0/praxis.dll"
                  Assert.isTrue (launcher.Contains expected) $"./praxis must default to {expected}"

                  posixOnly (fun () ->
                      let run name = succeeded (CliHarness.runIn (Some(root ())) "sh" [ repositoryPath name; "--version" ] [])
                      let canonical = run "praxis"
                      Assert.equal $"praxis {version ()}" (canonical.Out.Trim())
                      Assert.equal canonical.Out (run "ros").Out) }

          { Name = "internal-rename guard: this ROS-era repository's persisted .ros/ state and ros.json stay readable"
            Run =
              fun () ->
                  // The earliest recorded work item predates both the F# CLI
                  // and the Praxis name; reading it proves `.ros/` and
                  // `ros.json` are still the persisted contract.
                  Assert.isTrue (Directory.Exists(repositoryPath ".ros")) ".ros/ is the state directory"
                  Assert.isTrue (File.Exists(repositoryPath "ros.json")) "ros.json is the configuration file"
                  let shown = succeeded (CliHarness.ros (root ()) [ "work"; "show"; "TASK-20260816-PROMPTS"; "--json" ])
                  let item = JsonNode.Parse shown.Out
                  Assert.equal "TASK-20260816-PROMPTS" (CliPort.text item["id"])
                  Assert.equal "complete" (CliPort.text item["status"])
                  let status = succeeded (CliHarness.ros (root ()) [ "status"; "--json"; "--offline" ])
                  let summary = JsonNode.Parse status.Out
                  Assert.equal "repository-operating-system" (CliPort.text summary["repository"]) }

          { Name = "internal-rename guard: legacy ROS_* identity variables still map into the canonical Praxis identity"
            Run =
              fun () ->
                  let scratch = CliHarness.temporaryDirectory "internal-rename-ros-env"

                  try
                      let legacy =
                          [ "ROS_ACTOR", "guard-agent"
                            "ROS_ACTOR_KIND", "agent"
                            "ROS_TELEMETRY_PROVIDER", "guard-provider"
                            "ROS_TELEMETRY_RUNTIME", "guard-runtime" ]

                      let identity = JsonNode.Parse (succeeded (CliHarness.rosWith scratch legacy [ "provenance"; "identity"; "--json" ])).Out
                      let actor = identity["actor"]
                      Assert.equal "agent" (CliPort.text actor["kind"])
                      Assert.equal "guard-agent" (CliPort.text actor["id"])
                      Assert.equal "guard-provider" (CliPort.text actor["provider"])
                      Assert.equal "guard-runtime" (CliPort.text actor["runtime"])
                  finally
                      CliHarness.removeDirectory scratch }

          { Name = "internal-rename guard: historical DF-ROS, RQ-ROS, EV-ROS, HY-ROS and EX-ROS records stay valid and registered"
            Run =
              fun () ->
                  for identifier in [ "DF-ROS-2026-A050"; "RQ-ROS-2026-A025"; "EV-ROS-2026-A060"; "HY-ROS-2026-A021"; "EX-ROS-2026-A021" ] do
                      Assert.isTrue (Ros.Domain.Artifacts.ArtifactPolicy.isValidIdentifier identifier) $"{identifier} must stay a valid identifier"

                  for directory, prefix in [ "decisions", "DF-ROS-"; "requirements", "RQ-ROS-"; "evidence", "EV-ROS-"; "hypotheses", "HY-ROS-"; "experiments", "EX-ROS-" ] do
                      let records = Directory.GetFiles(repositoryPath (Path.Combine("research", directory)), prefix + "*.md")
                      Assert.isTrue (records.Length > 0) $"research/{directory} keeps its {prefix}* records"

                  let check = succeeded (CliHarness.ros (root ()) [ "registry"; "check" ])
                  Assert.isTrue (check.Out.Contains "registries are current") check.Out }

          { Name = "internal-rename guard: every workflow building this checkout names a project that exists; pinned-ref builds keep the legacy fallback"
            Run =
              fun () ->
                  // These workflows build the commit they run on, so every path
                  // they hand to `dotnet` must exist in this checkout.
                  for name in [ "praxis-validation.yml"; "native-release.yml"; "release.yml"; "site.yml" ] do
                      let paths = dotnetProjectPaths (workflow name)
                      Assert.isTrue (not paths.IsEmpty) $"{name} builds a project"

                      for path in paths do
                          Assert.isTrue (File.Exists(repositoryPath path)) $"{name} builds {path}, which does not exist"

                  Assert.isTrue (dotnetProjectPaths (workflow "native-release.yml") |> List.contains (cliProject ())) "native-release.yml publishes and packs the praxis CLI project"

                  // These build a caller-pinned or tagged ref that may predate
                  // the rename, so they must still find the pre-rename project.
                  for name in [ "foundations-verify.yml"; "ros-fs-assets.yml" ] do
                      let text = workflow name
                      Assert.isTrue (text.Contains "src/Ros.Cli/Ros.Cli.fsproj") $"{name} keeps the pre-rename project fallback"
                      Assert.isTrue (text.Contains(cliProject ())) $"{name} builds the current CLI project" }

          { Name = "internal-rename guard: the release keeps building praxis plus the legacy ros-fs-<rid> assets"
            Run =
              fun () ->
                  let native = workflow "native-release.yml"
                  let legacy = workflow "ros-fs-assets.yml"

                  for rid in [ "linux-x64"; "linux-arm64"; "osx-x64"; "osx-arm64"; "win-x64" ] do
                      Assert.isTrue (legacy.Contains rid) $"ros-fs-assets.yml still builds {rid}"

                  Assert.isTrue (legacy.Contains "ros-fs-$rid") "ros-fs-assets.yml names its assets ros-fs-<rid>"
                  Assert.isTrue (native.Contains "praxis --version") "the native release smoke-tests the praxis command" } ]
