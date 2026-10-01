namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions

/// End-to-end lifecycle tests (init, status, verify, upgrade, doctor) run
/// directly against the built `praxis` CLI, ported from the former
/// tests/lifecycle-package.test.mjs, which packed the npm tarball and drove the
/// same commands through its Node launcher. The npm packaging, tarball
/// content and Node launcher assertions went with the npm distribution; every
/// lifecycle behaviour is kept here. `LifecycleTests` covers the same rules
/// at the domain level; these prove the CLI wires them up.
///
/// A "legacy installation" used to be produced by the Node `ros-bootstrap
/// init`. The F# `init` writes the identical legacy `.ros/installation.json`
/// snapshot on a first install, so a legacy installation is that install with
/// `.echelon/ros.json` removed -- exactly the state a pre-manifest install
/// left behind.
[<RequireQualifiedAccess>]
module LifecycleCliTests =
    let private ros = CliHarness.ros
    let private version () = CliPort.releaseVersion.Value

    let private json root arguments =
        let result = ros root arguments
        // stdout must be the document and nothing else.
        result, CliPort.parse result.Out

    let private exit (expected: int) root arguments =
        CliPort.exitCode expected (ros root arguments)

    let private manifest root = CliPort.readJson root ".echelon/ros.json"

    let private artifact (manifest: JsonNode) (path: string) =
        CliPort.items (manifest["managedArtifacts"]) |> List.find (fun entry -> CliPort.stringOf entry "path" = Some path)

    let private installed (prefix: string) (run: string -> unit) =
        CliPort.withDirectory prefix (fun root ->
            exit 0 root [ "init" ]
            run root)

    let private legacy (project: string) (run: string -> unit) =
        CliPort.withDirectory "ros-lifecycle-legacy" (fun root ->
            exit 0 root [ "init"; "--project"; project ]
            File.Delete(Path.Combine(root, ".echelon", "ros.json"))
            Assert.isTrue (File.Exists(Path.Combine(root, ".ros", "installation.json"))) "a legacy install keeps its snapshot"
            run root)

    let private commandTests =
        [ { Name = "lifecycle cli: --version reports the release version and --help documents every command"
            Run = fun () ->
                CliPort.withDirectory "ros-lifecycle-help" (fun root ->
                    let reported = ros root [ "--version" ]
                    CliPort.exitCode 0 reported
                    Assert.equal $"praxis {version ()}" (reported.Out.Trim())

                    let help = ros root [ "--help" ]
                    CliPort.exitCode 0 help

                    for command in [ "init"; "status"; "verify"; "upgrade"; "doctor" ] do
                        CliPort.matches $"^\\s+{command}\\s" help.Out
                        let commandHelp = ros root [ command; "--help" ]
                        CliPort.exitCode 0 commandHelp
                        CliPort.matches $"^praxis {command} --" commandHelp.Out
                        CliPort.contains "Usage:" commandHelp.Out) }
          { Name = "lifecycle cli: an unknown option is an argument error that touches nothing"
            Run = fun () ->
                CliPort.withDirectory "ros-lifecycle-badargs" (fun root ->
                    let result = ros root [ "init"; "--nonsense" ]
                    CliPort.exitCode 2 result
                    CliPort.contains "unknown option '--nonsense'" result.Err
                    Assert.equal 0 (Directory.GetFileSystemEntries root).Length) }
          { Name = "lifecycle cli: an unsupported profile names the profiles that exist"
            Run = fun () ->
                CliPort.withDirectory "ros-lifecycle-profile" (fun root ->
                    let result = ros root [ "init"; "--profile"; "nope" ]
                    Assert.isTrue (result.Exit <> 0) "an unsupported profile must fail"
                    CliPort.contains "unsupported profile 'nope'" result.Err
                    CliPort.contains "greenfield" result.Err) } ]

    let private installTests =
        [ { Name = "lifecycle cli: init --dry-run reports a full plan and writes nothing"
            Run = fun () ->
                CliPort.withDirectory "ros-lifecycle-dryrun" (fun root ->
                    let before = CliPort.snapshot root
                    let result, plan = json root [ "init"; "--dry-run"; "--json" ]
                    CliPort.exitCode 0 result
                    Assert.equal "init" (CliPort.text (plan["command"]))
                    Assert.equal true (CliPort.boolean (plan["dryRun"]))
                    Assert.equal false (CliPort.boolean (plan["applied"]))
                    Assert.equal true (CliPort.boolean (plan["changesRequired"]))
                    Assert.isTrue ((CliPort.items (plan["changes"])).Length > 50) "a fresh install plans the whole scaffold"
                    CliPort.deepEqual "[]" (plan["conflicts"])
                    Assert.isTrue ((CliPort.items (plan["manifest"]["managedArtifacts"])).Length > 50) "the planned manifest covers the scaffold"
                    Assert.equal before (CliPort.snapshot root)) }
          { Name = "lifecycle cli: a second init is a byte-identical no-op"
            Run = fun () ->
                installed "ros-lifecycle-idempotent" (fun root ->
                    Assert.isTrue (File.Exists(Path.Combine(root, ".echelon", "ros.json"))) "init must write the manifest"
                    let afterFirst = CliPort.snapshot root
                    let second = ros root [ "init" ]
                    CliPort.exitCode 0 second
                    CliPort.contains "no changes needed" second.Out
                    Assert.equal afterFirst (CliPort.snapshot root)

                    let _, plan = json root [ "init"; "--json" ]
                    Assert.equal false (CliPort.boolean (plan["changesRequired"]))
                    CliPort.deepEqual "[]" (plan["changes"])
                    exit 0 root [ "init"; "--check" ]) }
          { Name = "lifecycle cli: the distributed work protocol has no broken local Markdown references"
            Run = fun () ->
                installed "ros-lifecycle-doc-links" (fun root ->
                    let document = Path.Combine(root, "docs", "work-protocol.md")

                    let broken =
                        Regex.Matches(File.ReadAllText document, @"\[[^\]]*\]\(([^)]+)\)")
                        |> Seq.map (fun m -> (m.Groups[1]).Value)
                        |> Seq.filter (fun target -> not (Regex.IsMatch(target, "^(?:[a-z]+:|#)", RegexOptions.IgnoreCase)))
                        |> Seq.map (fun target -> target.Split('#', 2)[0])
                        |> Seq.filter (fun target -> target <> "")
                        |> Seq.filter (fun target ->
                            let resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName document, target))
                            not (File.Exists resolved || Directory.Exists resolved))
                        |> Seq.toList

                    Assert.equal [] broken) }
          { Name = "lifecycle cli: status, verify and doctor agree on a fresh install and never write"
            Run = fun () ->
                installed "ros-lifecycle-healthy" (fun root ->
                    let statusResult, status = json root [ "status"; "--json" ]
                    CliPort.exitCode 0 statusResult
                    let installation = (status["installation"])
                    Assert.equal "installed" (CliPort.text (installation["state"]))
                    Assert.equal (version ()) (CliPort.text (installation["cliVersion"]))
                    Assert.equal (version ()) (CliPort.text (installation["installedVersion"]))
                    Assert.equal true (CliPort.boolean (installation["verified"]))

                    if CliPort.text (status["validation"]) <> "passed" then
                        let validation = ros root [ "validate"; "--json" ]
                        failwith $"a fresh install must validate cleanly; validate reported: {validation.Out}{validation.Err}"

                    let verifyResult, verify = json root [ "verify"; "--json" ]
                    CliPort.exitCode 0 verifyResult
                    Assert.equal true (CliPort.boolean (verify["valid"]))
                    CliPort.deepEqual "[]" (verify["failures"])

                    let strictResult, strict = json root [ "verify"; "--strict"; "--json" ]
                    CliPort.exitCode 0 strictResult
                    Assert.equal true (CliPort.boolean (strict["strict"]))
                    Assert.equal true (CliPort.boolean (strict["valid"]))

                    let doctorResult, doctor = json root [ "doctor"; "--json" ]
                    CliPort.exitCode 0 doctorResult
                    Assert.equal true (CliPort.boolean (doctor["healthy"]))
                    Assert.equal 0.0 (CliPort.number (doctor["errorCount"]))

                    let before = CliPort.snapshot root

                    for command in [ "status"; "verify"; "doctor" ] do
                        ros root [ command ] |> ignore

                    Assert.equal before (CliPort.snapshot root)) }
          { Name = "lifecycle cli: a damaged install fails verify and doctor names a remedy that works"
            Run = fun () ->
                installed "ros-lifecycle-damaged" (fun root ->
                    File.Delete(Path.Combine(root, "framework", "REP-SPECIFICATION.md"))

                    let verifyResult, verify = json root [ "verify"; "--json" ]
                    CliPort.exitCode 3 verifyResult
                    Assert.equal false (CliPort.boolean (verify["valid"]))
                    let failures = CliPort.items (verify["failures"])
                    Assert.equal 1 failures.Length
                    Assert.equal "managed-artifact-missing" (CliPort.text (failures.Head["code"]))

                    let doctorResult, doctor = json root [ "doctor"; "--json" ]
                    CliPort.exitCode 3 doctorResult
                    Assert.equal false (CliPort.boolean (doctor["healthy"]))

                    let finding =
                        CliPort.items (doctor["diagnoses"])
                        |> List.find (fun item -> CliPort.stringOf item "code" = Some "managed-artifact-missing")

                    Assert.equal "error" (CliPort.text (finding["severity"]))
                    Assert.equal "framework/REP-SPECIFICATION.md" (CliPort.text (finding["path"]))
                    Assert.isTrue (not (String.IsNullOrWhiteSpace(CliPort.text (finding["remedy"])))) "doctor must say how to fix what it reports"

                    exit 0 root [ "init" ]
                    exit 0 root [ "verify" ]) }
          { Name = "lifecycle cli: a locally modified tool-owned file blocks init instead of being overwritten"
            Run = fun () ->
                installed "ros-lifecycle-conflict" (fun root ->
                    let target = Path.Combine(root, "framework", "REP-SPECIFICATION.md")
                    File.AppendAllText(target, "\nlocal change that must survive\n")
                    let edited = File.ReadAllText target
                    let result = ros root [ "init" ]
                    CliPort.exitCode 4 result
                    CliPort.contains "tool-owned file was modified locally" result.Err
                    Assert.equal edited (File.ReadAllText target)) }
          { Name = "lifecycle cli: a user-owned file is never overwritten and is recorded at its own hash"
            Run = fun () ->
                CliPort.withDirectory "ros-lifecycle-userowned" (fun root ->
                    CliHarness.write root "README.md" "# My project\n"
                    exit 0 root [ "init" ]
                    Assert.equal "# My project\n" (CliHarness.read root "README.md")
                    let readme = artifact (manifest root) "README.md"
                    Assert.equal "user-owned" (CliPort.text (readme["ownership"]))
                    Assert.equal (CliPort.sha256Text "# My project\n") (CliPort.text (readme["sha256"]))
                    exit 0 root [ "init" ]
                    Assert.equal "# My project\n" (CliHarness.read root "README.md")
                    exit 0 root [ "verify" ]) }
          { Name = "lifecycle cli: a populated registry is generated output, not a local edit to a tool-owned file"
            Run = fun () ->
                installed "ros-lifecycle-registries" (fun root ->
                    let registries =
                        CliPort.items ((manifest root)["managedArtifacts"])
                        |> List.filter (fun entry -> Regex.IsMatch(CliPort.text (entry["path"]), @"^registries/.+\.json$"))

                    Assert.isTrue (not registries.IsEmpty) "the scaffold must install registry seeds"

                    Assert.equal
                        []
                        (registries
                         |> List.filter (fun entry -> CliPort.text (entry["ownership"]) <> "generated")
                         |> List.map (fun entry -> CliPort.text (entry["path"])))

                    let template = CliHarness.read root "templates/research/THEORY-TEMPLATE.md"

                    let theory =
                        Regex.Replace(template, "^id: .*$", "id: TH-DEMO-2026-0001", RegexOptions.Multiline)
                        |> fun text -> Regex.Replace(text, "^title: .*$", "title: A theory that reaches the registry", RegexOptions.Multiline)
                        |> fun text -> Regex.Replace(text, "^research_area: .*$", "research_area: demo", RegexOptions.Multiline)
                        |> fun text -> text.Replace("YYYY-MM-DD", "2026-09-17")

                    CliHarness.write root "research/theories/TH-DEMO-2026-0001--registry-ownership.md" theory
                    exit 0 root [ "registry"; "build" ]
                    Assert.isTrue ((CliPort.items (CliPort.readJson root "registries/theories.json")).Length > 0) "the theory must reach the registry"
                    exit 0 root [ "verify" ]
                    exit 0 root [ "init" ]) }
          { Name = "lifecycle cli: a configuration version newer than this CLI is refused, not guessed at"
            Run = fun () ->
                installed "ros-lifecycle-future" (fun root ->
                    let current = manifest root
                    current["configurationVersion"] <- JsonValue.Create 99
                    CliPort.writeJson root ".echelon/ros.json" current
                    let result = ros root [ "upgrade" ]
                    CliPort.exitCode 4 result
                    CliPort.contains "newer than this CLI supports" result.Err) }
          { Name = "lifecycle cli: a malformed manifest is diagnosed, not thrown"
            Run = fun () ->
                installed "ros-lifecycle-malformed" (fun root ->
                    CliHarness.write root ".echelon/ros.json" "{ not json"
                    let result, doctor = json root [ "doctor"; "--json" ]
                    CliPort.exitCode 3 result

                    let finding =
                        CliPort.items (doctor["diagnoses"])
                        |> List.tryFind (fun item -> CliPort.stringOf item "code" = Some "manifest-unreadable")

                    match finding with
                    | Some diagnosis -> Assert.isTrue (not (String.IsNullOrWhiteSpace(CliPort.text (diagnosis["remedy"])))) "the diagnosis must carry a remedy"
                    | None ->
                        let diagnoses = CliPort.compact (doctor["diagnoses"])
                        failwith $"expected a manifest-unreadable diagnosis, got {diagnoses}") }
          { Name = "lifecycle cli: the project-administration profile installs, verifies and re-initializes"
            Run = fun () ->
                CliPort.withDirectory "ros-lifecycle-hub" (fun root ->
                    exit 0 root [ "init"; "--profile"; "project-administration"; "--project"; "Project Administration" ]
                    Assert.equal "project-administration" (CliPort.text ((manifest root)["profile"]))
                    exit 0 root [ "verify" ]
                    exit 0 root [ "init" ]) } ]

    let private upgradeTests =
        [ { Name = "lifecycle cli: a legacy installation upgrades, keeping user edits and the legacy snapshot"
            Run = fun () ->
                legacy "Legacy Project" (fun root ->
                    let edited = "# Charter owned by the project\n"
                    CliHarness.write root "PROJECT-CHARTER.md" edited

                    let _, before = json root [ "status"; "--json" ]
                    Assert.equal "upgrade-required" (CliPort.text (before["installation"]["state"]))
                    Assert.equal (version ()) (CliPort.text (before["installation"]["upgradeAvailable"]))

                    let snapshotBefore = CliPort.snapshot root
                    exit 3 root [ "upgrade"; "--check" ]
                    let dryResult, dry = json root [ "upgrade"; "--dry-run"; "--json" ]
                    CliPort.exitCode 0 dryResult
                    Assert.equal false (CliPort.boolean (dry["applied"]))
                    let migrations = CliPort.items (dry["migrations"])
                    Assert.equal 1 migrations.Length
                    Assert.equal 0.0 (CliPort.number (migrations.Head["fromVersion"]))
                    Assert.equal 1.0 (CliPort.number (migrations.Head["toVersion"]))
                    Assert.isTrue (CliPort.items (dry["preserved"]) |> List.exists (fun path -> CliPort.text path = "PROJECT-CHARTER.md")) "the edited charter is preserved"
                    Assert.equal snapshotBefore (CliPort.snapshot root)

                    exit 0 root [ "upgrade" ]
                    Assert.equal edited (CliHarness.read root "PROJECT-CHARTER.md")
                    Assert.isTrue (File.Exists(Path.Combine(root, ".ros", "installation.json"))) "the legacy snapshot must be left in place"

                    let _, after = json root [ "status"; "--json" ]
                    Assert.equal "installed" (CliPort.text (after["installation"]["state"]))
                    Assert.equal 1.0 (CliPort.number (after["installation"]["configurationVersion"]))
                    exit 0 root [ "verify"; "--strict" ]
                    exit 0 root [ "upgrade"; "--check" ]) }
          { Name = "lifecycle cli: upgrade adopts an older legacy snapshot using its recorded tool-owned hashes"
            Run = fun () ->
                legacy "Older Legacy" (fun root ->
                    let snapshot = CliPort.readJson root ".ros/installation.json"

                    let bootstrap =
                        CliPort.items (snapshot["files"]) |> List.find (fun entry -> CliPort.stringOf entry "path" = Some "BOOTSTRAP.md")

                    Assert.equal true (CliPort.boolean (bootstrap["managed"]))
                    let oldBytes = "# BOOTSTRAP from ROS 2.x\n"
                    CliHarness.write root "BOOTSTRAP.md" oldBytes
                    bootstrap["sha256"] <- JsonValue.Create(CliPort.sha256Text oldBytes)
                    snapshot["package_version"] <- JsonValue.Create "2.0.0"
                    CliPort.writeJson root ".ros/installation.json" snapshot

                    exit 0 root [ "upgrade" ]
                    Assert.isTrue (CliHarness.read root "BOOTSTRAP.md" <> oldBytes) "the recorded tool-owned file must be updated"
                    let current = manifest root
                    Assert.equal (version ()) (CliPort.text (current["installedVersion"]))
                    Assert.equal 1.0 (CliPort.number (current["configurationVersion"]))
                    exit 0 root [ "verify"; "--strict" ]) }
          { Name = "lifecycle cli: legacy upgrade preserves a customized validation workflow as shared integration"
            Run = fun () ->
                legacy "Shared Workflow" (fun root ->
                    let workflow = ".github/workflows/praxis-validation.yml"
                    let customized = CliHarness.read root workflow + "\n# repository-specific validation wrapper\n"
                    CliHarness.write root workflow customized
                    exit 0 root [ "upgrade" ]
                    Assert.equal customized (CliHarness.read root workflow)
                    let entry = artifact (manifest root) workflow
                    Assert.equal "shared" (CliPort.text (entry["ownership"]))
                    Assert.equal (CliPort.sha256Text customized) (CliPort.text (entry["sha256"]))) }
          { Name = "lifecycle cli: upgrade blocks a tool-owned file edited after a legacy install"
            Run = fun () ->
                legacy "Older Legacy Edited" (fun root ->
                    let edited = CliHarness.read root "BOOTSTRAP.md" + "\nlocal change after legacy installation\n"
                    CliHarness.write root "BOOTSTRAP.md" edited
                    let result = ros root [ "upgrade" ]
                    CliPort.exitCode 5 result
                    CliPort.contains "tool-owned file was modified locally" result.Err
                    Assert.equal edited (CliHarness.read root "BOOTSTRAP.md")
                    Assert.isTrue (not (File.Exists(Path.Combine(root, ".echelon", "ros.json")))) "a blocked upgrade must not claim a current installation") }
          { Name = "lifecycle cli: the legacy snapshot's profile is authoritative during adoption"
            Run = fun () ->
                legacy "Legacy Profile" (fun root ->
                    let snapshot = CliPort.readJson root ".ros/installation.json"
                    snapshot["profile"] <- JsonValue.Create "project-administration"
                    CliPort.writeJson root ".ros/installation.json" snapshot
                    exit 0 root [ "upgrade" ]
                    Assert.equal "project-administration" (CliPort.text ((manifest root)["profile"]))
                    Assert.isTrue (File.Exists(Path.Combine(root, "praxis-hub"))) "the project-administration payload must be selected") }
          { Name = "lifecycle cli: an unedited legacy installation upgrades and verifies strictly"
            Run = fun () ->
                legacy "Clean Legacy" (fun root ->
                    exit 0 root [ "upgrade" ]
                    exit 0 root [ "verify"; "--strict" ]) } ]

    /// Copies the built CLI (top-level files only) outside the checkout, so
    /// neither the walk up from the executable nor from the working directory
    /// can find a package root.
    let private withDetachedBinary (run: string -> string -> unit) =
        CliPort.withDirectory "ros-lifecycle-detached" (fun detached ->
            for file in Directory.GetFiles(Path.GetDirectoryName CliHarness.cli) do
                File.Copy(file, Path.Combine(detached, Path.GetFileName file))

            CliPort.withDirectory "ros-lifecycle-standalone" (fun project -> run (Path.Combine(detached, "praxis.dll")) project))

    let private standalone (assembly: string) (project: string) (arguments: string list) =
        let result = CliHarness.runIn (Some(Path.GetDirectoryName assembly)) "dotnet" ([ assembly; "--root"; project ] @ arguments) []

        if result.Err.Contains "packaged scaffold" then
            let command = String.concat " " arguments
            failwith $"'{command}' fell back to needing a package root: {result.Err}"

        result

    let private payloadTests =
        [ { Name = "lifecycle cli: a detached binary installs, heals and upgrades from its embedded scaffold"
            Run = fun () ->
                withDetachedBinary (fun assembly project ->
                    let run = standalone assembly project
                    CliPort.exitCode 0 (run [ "init"; "--project"; "Standalone Service" ])
                    Assert.isTrue (File.Exists(Path.Combine(project, ".echelon", "ros.json"))) "the manifest must be written"
                    Assert.isTrue (File.Exists(Path.Combine(project, "framework", "REP-SPECIFICATION.md"))) "the scaffold must be installed"

                    let again = run [ "init" ]
                    CliPort.exitCode 0 again
                    CliPort.contains "no changes needed" again.Out
                    CliPort.exitCode 0 (run [ "verify"; "--strict" ])

                    File.Delete(Path.Combine(project, "framework", "REP-SPECIFICATION.md"))
                    CliPort.exitCode 3 (run [ "verify" ])
                    CliPort.exitCode 0 (run [ "init" ])
                    CliPort.exitCode 0 (run [ "verify" ])
                    Assert.isTrue (File.Exists(Path.Combine(project, "framework", "REP-SPECIFICATION.md"))) "init must restore the file"

                    CliPort.exitCode 0 (run [ "upgrade" ])
                    CliPort.exitCode 0 (run [ "upgrade"; "--check" ])
                    CliPort.exitCode 0 (run [ "registry"; "check" ])
                    CliPort.exitCode 0 (run [ "validate" ])) }
          { Name = "lifecycle cli: --package-root installs the same managed artifacts as the embedded scaffold"
            Run = fun () ->
                withDetachedBinary (fun assembly embeddedProject ->
                    CliPort.withDirectory "ros-lifecycle-package-root" (fun directoryProject ->
                        CliPort.exitCode 0 (standalone assembly embeddedProject [ "init"; "--project"; "Payload Parity" ])

                        CliPort.exitCode
                            0
                            (standalone assembly directoryProject [ "--package-root"; CliPort.repositoryRoot.Value; "init"; "--project"; "Payload Parity" ])

                        let artifacts root =
                            CliPort.items ((manifest root)["managedArtifacts"])
                            |> List.map (fun entry -> CliPort.text (entry["path"]), CliPort.text (entry["ownership"]), CliPort.text (entry["sha256"]))

                        Assert.equal (artifacts embeddedProject) (artifacts directoryProject)
                        CliPort.exitCode 0 (standalone assembly directoryProject [ "--package-root"; CliPort.repositoryRoot.Value; "verify"; "--strict" ]))) } ]

    let tests = commandTests @ installTests @ upgradeTests @ payloadTests
