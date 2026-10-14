namespace Praxis.Tests

open System
open System.IO

/// What a freshly installed repository looks like and whether the installed
/// validator works in it, ported from the parts of the former
/// tests/npm-bootstrap.test.mjs that exercised the F# CLI. The Node bootstrap
/// (lib/bootstrap.mjs) that used to install these fixtures is gone; the F#
/// `init` installs them now.
[<RequireQualifiedAccess>]
module InstalledRepositoryTests =
    let private ros = CliHarness.ros

    let private init root (arguments: string list) =
        CliPort.exitCode 0 (ros root ("init" :: arguments))

    let private installedProject (project: string) (run: string -> unit) =
        CliPort.withDirectory "ros-installed" (fun root ->
            init root [ "--project"; project ]
            run root)

    let private passes (expectedOutput: string) (result: CliHarness.Run) =
        CliPort.exitCode 0 result
        CliPort.contains expectedOutput result.Out

    let tests =
        [ { Name = "installed repository: a greenfield install is self-contained and immediately valid"
            Run = fun () ->
                installedProject "Communication Engineering" (fun root ->
                    CliPort.contains "Communication Engineering" (CliHarness.read root "README.md")

                    if not (OperatingSystem.IsWindows()) then
                        Assert.isTrue (File.GetUnixFileMode(Path.Combine(root, "praxis")).HasFlag UnixFileMode.UserExecute) "./praxis must be executable"

                    Assert.isTrue (File.Exists(Path.Combine(root, ".ros", "installation.json"))) "the installation snapshot must be written"
                    let context = CliPort.readJson root ".ros/context/current.json"
                    let installItem = (context["workItems"]).[0]
                    Assert.equal "complete" (CliPort.text (installItem["semanticState"]))
                    Assert.isTrue ((CliPort.text (installItem["id"])).StartsWith "ROS-INSTALL-") "the install is recorded as a work item"
                    Assert.isTrue (File.Exists(Path.Combine(root, ".ros", "events", "events.jsonl"))) "the install event must be written"
                    Assert.isTrue (File.Exists(Path.Combine(root, ".github", "workflows", "praxis-validation.yml"))) "the validation workflow must be installed"
                    ros root [ "registry"; "check" ] |> passes "registries are current"
                    ros root [ "validate" ] |> passes "validation passed") }
          { Name = "installed repository: the project name is derived from the folder when omitted"
            Run = fun () ->
                CliPort.withDirectory "ros-installed-parent" (fun parent ->
                    let root = Path.Combine(parent, "communication-engineering")
                    Directory.CreateDirectory root |> ignore
                    init root []
                    Assert.equal "Communication Engineering" (CliPort.text ((CliPort.readJson root ".ros/installation.json")["project"]))
                    CliPort.contains "Communication Engineering" (CliHarness.read root "PROJECT-CHARTER.md")) }
          { Name = "installed repository: a dry run does not create the target"
            Run = fun () ->
                CliPort.withDirectory "ros-installed-parent" (fun parent ->
                    let root = Path.Combine(parent, "not-created")
                    init root [ "--dry-run"; "--project"; "Communication Engineering" ]
                    Assert.isTrue (not (Directory.Exists root)) "a dry run must not create the target") }
          { Name = "installed repository: common project-owned files are preserved and recorded as unmanaged"
            Run = fun () ->
                CliPort.withDirectory "ros-installed-preserved" (fun root ->
                    CliHarness.write root "README.md" "existing project readme\n"
                    CliHarness.write root ".gitattributes" "* text=auto\n"
                    // Also an agent contract the repository already has: the Node
                    // bootstrap refused to install over it; F# `init` treats it as
                    // shared and preserves it.
                    CliHarness.write root "AGENTS.md" "user work\n"
                    init root [ "--project"; "Communication Engineering" ]
                    Assert.equal "existing project readme\n" (CliHarness.read root "README.md")
                    // The repository's own attributes are kept; only the rules
                    // the byte-verified CRLF launchers need are appended.
                    Assert.equal "* text=auto\npraxis.cmd text eol=crlf\nros.cmd text eol=crlf\n" (CliHarness.read root ".gitattributes")
                    Assert.equal "user work\n" (CliHarness.read root "AGENTS.md")

                    let readme =
                        CliPort.items ((CliPort.readJson root ".ros/installation.json")["files"])
                        |> List.find (fun entry -> CliPort.stringOf entry "path" = Some "README.md")

                    Assert.equal false (CliPort.boolean (readme["managed"]))
                    Assert.equal "preserved-existing" (CliPort.text (readme["disposition"]))
                    CliPort.exitCode 0 (ros root [ "verify" ])) }
          { Name = "installed repository: installing into an existing Git repository validates"
            Run = fun () ->
                CliPort.withDirectory "ros-installed-git" (fun root ->
                    CliHarness.write root "README.md" "existing\n"
                    CliHarness.git root [ "init"; "-q" ] |> ignore
                    CliHarness.commitAll root "baseline"
                    init root [ "--project"; "Existing Repository" ]
                    CliPort.exitCode 0 (ros root [ "validate" ])) }
          { Name = "installed repository: the validator catches broken lineage and accepts it once repaired"
            Run = fun () ->
                installedProject "Communication Engineering" (fun root ->
                    CliHarness.write
                        root
                        "research/hypotheses/HY-COMM-2026-A001--first-claim.md"
                        "---\nid: HY-COMM-2026-A001\ntitle: First claim\nstatus: proposed\nconfidence: low\nsupporting_evidence: [EV-COMM-2026-A002]\n---\n\n# First claim\n"

                    let broken = ros root [ "validate" ]
                    CliPort.exitCode 1 broken
                    CliPort.contains "broken reference 'EV-COMM-2026-A002'" broken.Err

                    CliHarness.write
                        root
                        "research/evidence/EV-COMM-2026-A002--first-observation.md"
                        "---\nid: EV-COMM-2026-A002\ntitle: First observation\nstatus: accepted\nconfidence: medium\nsupports: [HY-COMM-2026-A001]\n---\n\n# First observation\n"

                    CliPort.exitCode 0 (ros root [ "registry"; "build" ])
                    CliPort.exitCode 0 (ros root [ "validate" ])) }
          { Name = "installed repository: the validator accepts a preserved legacy REP identity and confidence"
            Run = fun () ->
                installedProject "Compatibility Pilot" (fun root ->
                    CliHarness.write
                        root
                        "research/packages/RP-2026-07-30-NHE-COMPARATIVE-REVIEW.md"
                        "---\nidentifier: RP-2026-07-30-NHE-COMPARATIVE-REVIEW\ntitle: Legacy review\nstatus: draft\nconfidence: medium-high\n---\n"

                    CliPort.exitCode 0 (ros root [ "registry"; "build" ])
                    CliPort.exitCode 0 (ros root [ "validate" ])) }
          { Name = "installed repository: the project-administration profile installs a valid hub"
            Run = fun () ->
                CliPort.withDirectory "ros-installed-hub" (fun root ->
                    init root [ "--profile"; "project-administration"; "--project"; "Org Hub" ]

                    if not (OperatingSystem.IsWindows()) then
                        Assert.isTrue (File.GetUnixFileMode(Path.Combine(root, "praxis-hub")).HasFlag UnixFileMode.UserExecute) "./praxis-hub must be executable"

                    CliPort.deepEqual "[]" ((CliPort.readJson root ".ros/hub/registry.json")["repos"])
                    CliPort.contains "Registered Repositories" (CliHarness.read root ".ros/hub/registry.md")
                    ros root [ "validate" ] |> passes "validation passed"
                    CliPort.exitCode 0 (ros root [ "registry"; "check" ])) }
          { Name = "installed repository: an unsupported profile lists the available profiles"
            Run = fun () ->
                CliPort.withDirectory "ros-installed-profile" (fun root ->
                    let result = ros root [ "init"; "--profile"; "nonexistent"; "--project"; "X" ]
                    Assert.isTrue (result.Exit <> 0) "an unsupported profile must fail"
                    CliPort.contains "unsupported profile 'nonexistent'; available profiles: greenfield, project-administration" result.Err) } ]
