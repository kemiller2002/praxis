namespace Praxis.Tests

open System
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq

/// The permanent internal-naming invariant (PRAXIS-INTERNAL-NAMESPACES,
/// docs/migrations/praxis-internal-namespaces.md). Current implementation
/// identity is Praxis: no current artifact may reintroduce the obsolete
/// internal `Ros.*` projects, namespaces, solution, MSBuild properties or
/// resource prefix.
///
/// This is a classifier, not a ban on the string "ros". Every tracked file is
/// checked unless it belongs to an explicitly retained category, each with
/// its reason; the forbidden forms are specific internal identities, so the
/// public `ros` alias, `ROS_*` variables, `.ros/`, `ros.json`, `ros-fs-*`
/// assets and `*-ROS-*` record IDs never match them.
[<RequireQualifiedAccess>]
module InternalNamingTests =
    /// Why a path is exempt from the invariant.
    type Retained =
        { Category: string
          Reason: string }

    type Finding =
        { Path: string
          Line: int
          Form: string
          Text: string }

    let private retained category reason = { Category = category; Reason = reason }

    /// Retained categories, by path prefix (a trailing '/' is a directory).
    let retainedPrefixes: (string * Retained) list =
        [ ".ros/", retained "persisted state" "Praxis state and events record paths as they were when written"
          "research/", retained "historical identity" "immutable DF/RQ/EV/HY/EX/JR records and experiment inputs/outputs describe what was true then"
          "registries/", retained "historical identity" "generated from the research records"
          "docs/migrations/", retained "migration record" "migration ledgers, including this rename's own manifest, name the names they migrate"
          "docs/upgrades/", retained "historical evidence" "dated SDE upgrade reviews of the tree as it was"
          "docs/00-governance/Governance-Decision-Log.md", retained "historical evidence" "decision-log entries cite the evidence of their day"
          "docs/site/claim-audit.md", retained "historical evidence" "a dated claim audit of the code as it was"
          "tests/Praxis.Tests/Fixtures/", retained "recorded fixture" "recorded provider input replayed verbatim"
          "tests/Praxis.Tests/InternalNamingTests.fs", retained "invariant definition" "this file defines the forbidden forms" ]

    /// Exact legacy tokens a current file may carry, with the reason. Each is
    /// removed before matching, so anything else in the same file still fails.
    let allowedTokens: (string * string * string) list =
        [ ".github/workflows/foundations-verify.yml", "src/Ros.Cli/Ros.Cli.fsproj", "builds a caller-pinned ref that may predate the rename"
          ".github/workflows/ros-fs-assets.yml", "src/Ros.Cli/Ros.Cli.fsproj", "builds a tagged ref that may predate the rename"
          "tests/Praxis.Tests/InternalNamingGuardTests.fs", "src/Ros.Cli/Ros.Cli.fsproj", "asserts the pinned-ref fallback above"
          "tests/Praxis.Tests/InternalNamingGuardTests.fs", "Ros.Cli.dll", "asserts no assembly is named after the internal project" ]

    /// The obsolete internal forms, each with what it means.
    let forbiddenForms: (string * Regex) list =
        [ "F# declaration in an obsolete Ros.* namespace", Regex(@"^\s*(?:namespace|module|open)\s+(?:global\.)?Ros\.")
          "ProjectReference to an obsolete Ros.* project", Regex(@"ProjectReference\s+Include=""[^""]*\bRos\.")
          "obsolete internal project, assembly or namespace identity", Regex(@"\bRos\.(?:Domain|Contracts|Application|Infrastructure|Cli|Tests)\b")
          "obsolete solution file", Regex(@"\bRos\.slnx\b")
          "obsolete project directory", Regex(@"\b(?:src|tests)[/\\]Ros\.")
          "obsolete internal MSBuild property", Regex(@"\bRos(?:RepositoryRoot|PackageVersion)\b")
          "obsolete embedded-resource prefix", Regex(@"\bros\.payload/") ]

    let classify (path: string) : Retained option =
        retainedPrefixes
        |> List.tryFind (fun (prefix, _) -> if prefix.EndsWith "/" then path.StartsWith(prefix, StringComparison.Ordinal) else path = prefix)
        |> Option.map snd

    let private withoutAllowedTokens (path: string) (text: string) =
        allowedTokens
        |> List.filter (fun (owner, _, _) -> owner = path)
        |> List.fold (fun (current: string) (_, token, _) -> current.Replace(token, "")) text

    /// Every obsolete form in one file's text, or none for a retained path.
    let findings (path: string) (text: string) : Finding list =
        match classify path with
        | Some _ -> []
        | None ->
            (withoutAllowedTokens path text).Split('\n')
            |> Array.toList
            |> List.indexed
            |> List.collect (fun (index, line) ->
                forbiddenForms
                |> List.filter (fun (_, pattern) -> pattern.IsMatch line)
                |> List.map (fun (form, _) ->
                    { Path = path
                      Line = index + 1
                      Form = form
                      Text = line.Trim() }))

    let private root () = CliPort.repositoryRoot.Value

    let private trackedFiles () =
        let listing = CliHarness.git (root ()) [ "ls-files"; "-z" ]
        listing.Split('\000', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

    let private readText (path: string) =
        let full = Path.Combine(root (), path)

        if File.Exists full then
            let bytes = File.ReadAllBytes full
            if Array.contains 0uy bytes then None else Some(Text.Encoding.UTF8.GetString bytes)
        else
            None

    let private render (finding: Finding) =
        $"{finding.Path}:{finding.Line}: {finding.Form}: {finding.Text}"

    let tests =
        [ { Name = "internal naming: no current artifact reintroduces an obsolete Ros.* internal name"
            Run =
              fun () ->
                  let found =
                      trackedFiles ()
                      |> List.collect (fun path -> readText path |> Option.map (findings path) |> Option.defaultValue [])

                  Assert.isTrue found.IsEmpty (found |> List.map render |> String.concat "\n") }

          { Name = "internal naming: the active solution and project tree are Praxis.*"
            Run =
              fun () ->
                  let at relative = Path.Combine(root (), relative)
                  Assert.isTrue (not (File.Exists(at "Ros.slnx"))) "Ros.slnx must not exist"
                  Assert.isTrue (not (Directory.Exists(at "tests/Ros.Tests"))) "tests/Ros.Tests must not exist"
                  Assert.empty (Directory.GetDirectories(at "src", "Ros.*") |> List.ofArray)

                  let projects =
                      XDocument.Load(at "Praxis.slnx").Descendants(XName.Get "Project")
                      |> Seq.map (fun project -> project.Attribute(XName.Get "Path").Value)
                      |> Set.ofSeq

                  let expected =
                      [ "Domain"; "Contracts"; "Application"; "Infrastructure"; "Cli" ]
                      |> List.map (fun name -> $"src/Praxis.{name}/Praxis.{name}.fsproj")
                      |> List.append [ "tests/Praxis.Tests/Praxis.Tests.fsproj"; "site-tools/SiteTools.fsproj"; "tests/Site.Tests/Site.Tests.fsproj" ]
                      |> Set.ofList

                  Assert.equal expected projects

                  for project in expected do
                      Assert.isTrue (File.Exists(at project)) $"{project} must exist"

                  let testProject = XDocument.Load(at "tests/Praxis.Tests/Praxis.Tests.fsproj")
                  Assert.equal "Praxis.Tests" (testProject.Descendants(XName.Get "AssemblyName") |> Seq.exactlyOne).Value }

          { Name = "internal naming: the classifier rejects every obsolete internal form in a current file"
            Run =
              fun () ->
                  let samples =
                      [ "src/Praxis.Domain/New.fs", "namespace Ros.Domain.Work"
                        "src/Praxis.Cli/New.fs", "module Ros.Cli.Extra"
                        "tests/Praxis.Tests/New.fs", "open Ros.Infrastructure.Work"
                        "src/Praxis.Cli/Praxis.Cli.fsproj", """<ProjectReference Include="../Ros.Domain/Ros.Domain.fsproj" />"""
                        "src/Praxis.Cli/Program.fs", "let value = Ros.Contracts.JsonRendering.renderIndented"
                        ".github/workflows/praxis-validation.yml", "run: dotnet build Ros.slnx --configuration Release"
                        ".github/workflows/praxis-validation.yml", "run: dotnet tests/Ros.Tests/bin/Release/net10.0/Ros.Tests.dll"
                        "docs/cli.md", "see src/Ros.Cli/Program.fs"
                        "Directory.Build.props", "<RosRepositoryRoot>$(MSBuildThisFileDirectory)</RosRepositoryRoot>"
                        "src/Praxis.Infrastructure/Lifecycle/Payload.fs", "let private EmbeddedPrefix = \"ros.payload/\""
                        // An allowed token excuses only itself, never another form in the same file.
                        ".github/workflows/foundations-verify.yml", "dotnet build praxis-verifier/src/Ros.Domain/Ros.Domain.fsproj" ]

                  for path, text in samples do
                      Assert.isTrue (not (findings path text).IsEmpty) $"{path}: '{text}' must be rejected" }

          { Name = "internal naming: the classifier allows compatibility, persisted, historical and serialized ROS names"
            Run =
              fun () ->
                  let current =
                      [ "praxis", "exec \"$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd)/praxis\" \"$@\""
                        "ros", "# Compatibility alias: `ros` is the pre-rename name of `praxis`"
                        "src/Praxis.Domain/Telemetry/Identity.fs", "RosTelemetryProvider: string option // ROS_TELEMETRY_PROVIDER"
                        "src/Praxis.Cli/Lifecycle.fs", "let manifest = \".echelon/ros.json\" // .ros/ and ros.json stay; rosVersion"
                        ".github/workflows/ros-fs-assets.yml", "cp dist/publish/$rid/praxis dist/ros-fs/ros-fs-$rid"
                        ".github/workflows/foundations-verify.yml", "[ -f \"$project\" ] || project=praxis-verifier/src/Ros.Cli/Ros.Cli.fsproj"
                        "docs/cli.md", "Decision DF-ROS-2026-A050 and RQ-ROS-2026-A025; ROS-INSTALL-3-6-0; schemas/ros-fs.schema.json" ]

                  for path, text in current do
                      Assert.empty (findings path text)

                  let historical =
                      [ ".ros/events/events.jsonl"; "research/decisions/DF-ROS-2026-A027--staged-fsharp-application-boundary.md"
                        "registries/evidence.json"; "docs/migrations/fsharp/ARCHITECTURE.md"; "docs/upgrades/SDE-1.3.0-STRUCTURAL-REVIEW.md"
                        "tests/Praxis.Tests/Fixtures/claude-session-transcript.jsonl" ]

                  for path in historical do
                      Assert.isTrue (classify path).IsSome $"{path} is a retained category"
                      Assert.empty (findings path "namespace Ros.Domain.Work // src/Ros.Cli/Program.fs in Ros.slnx") } ]
