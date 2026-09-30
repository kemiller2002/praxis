namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes

/// Tests for the repository shell script bin/echelon.sh (`doctor` and
/// `inventory`), ported from the former tests/echelon-doctor.test.mjs. Each
/// test runs `sh bin/echelon.sh ...` against a fake ECHELON_HOME (versioned
/// native tools, command shims) and a consumer project that pins a toolchain,
/// declares repository components and has an Echelon npm package installed
/// in its node_modules -- which is what the script inspects in a consumer
/// repository. POSIX only, like the script itself.
[<RequireQualifiedAccess>]
module EchelonDoctorTests =
    type private Environment =
        { Root: string
          Bin: string
          Project: string
          Variables: (string * string) list }

    let private script () =
        Path.Combine(CliPort.repositoryRoot.Value, "bin", "echelon.sh")

    let private writeScript (path: string) (body: string) =
        File.WriteAllText(path, "#!/bin/sh\n" + body)
        CliPort.makeExecutable path

    let private reports (version: string) = $"printf '%%s\\n' '{version}'\n"

    let private makeEnvironment (manifestPraxis: string) (binOnPath: bool) =
        let root = CliHarness.temporaryDirectory "echelon-doctor"
        let home = Path.Combine(root, "home")
        let bin = Path.Combine(home, "bin")
        let tools = Path.Combine(home, "tools")
        let project = Path.Combine(root, "project")
        Directory.CreateDirectory bin |> ignore
        Directory.CreateDirectory project |> ignore

        for tool, version in [ "ordo", "1.4.0"; "praxis", "3.4.0" ] do
            let versionRoot = Path.Combine(tools, tool, version)
            Directory.CreateDirectory versionRoot |> ignore
            File.WriteAllText(Path.Combine(versionRoot, "VERSION"), version + "\n")
            Directory.CreateSymbolicLink(Path.Combine(tools, tool, "current"), versionRoot) |> ignore

        Directory.CreateDirectory(Path.Combine(tools, "limen", "0.9.0")) |> ignore

        for name, version in [ "ordo", "1.4.0"; "sde", "1.4.0"; "praxis", "3.4.0"; "ros", "3.4.0" ] do
            // Releases up to v3.4.0 print the pre-rename `ros-fs X.Y.Z`; doctor
            // must keep reading that format (DF-ROS-2026-A050).
            let reported = if name = "praxis" || name = "ros" then $"ros-fs {version}" else version
            writeScript (Path.Combine(bin, name)) (reports reported)

        writeScript (Path.Combine(bin, "echelon")) "exit 0\n"

        CliHarness.write project ".echelon/toolchain.json" (CliPort.indented $"""{{"schemaVersion":1,"ordo":"1.4.0","praxis":"{manifestPraxis}"}}""" + "\n")
        CliHarness.write project ".echelon/visual-engineering.json" (CliPort.indented """{"schemaVersion":1,"tool":"visual-engineering","installedVersion":"1.0.0"}""" + "\n")

        CliHarness.write
            project
            ".echelon/limen.json"
            (CliPort.indented """{"schemaVersion":1,"tool":"limen","package":"@echelon-foundry/typescript-wasm-kernel","installedVersion":"0.6.2"}""" + "\n")

        CliHarness.write
            project
            "node_modules/@echelon-foundry/typescript-wasm-kernel/package.json"
            (CliPort.indented """{"name":"@echelon-foundry/typescript-wasm-kernel","version":"0.6.2"}""" + "\n")

        CliHarness.git project [ "init"; "-q" ] |> ignore
        let basePath = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue ""
        let path = if binOnPath then bin + string Path.PathSeparator + basePath else basePath

        { Root = root
          Bin = bin
          Project = project
          Variables = [ "ECHELON_HOME", home; "PATH", path ] }

    /// Runs `body` against a fresh environment; skipped on Windows, where the
    /// POSIX script does not run.
    let private withEnvironment (manifestPraxis: string) (binOnPath: bool) (body: Environment -> unit) =
        if not (OperatingSystem.IsWindows()) then
            let environment = makeEnvironment manifestPraxis binOnPath

            try
                body environment
            finally
                CliHarness.removeDirectory environment.Root

    let private healthy body = withEnvironment "3.4.0" true body

    let private echelonIn (directory: string) (environment: Environment) (arguments: string list) =
        CliHarness.runIn (Some directory) "sh" (script () :: arguments) environment.Variables

    let private echelon (environment: Environment) arguments = echelonIn environment.Project environment arguments

    let private doctor environment arguments = echelon environment ("doctor" :: arguments)

    let private succeeded (expected: int) (result: CliHarness.Run) =
        CliPort.exitCode expected result
        result.Out

    let private find (array: JsonNode) (field: string) (value: string) =
        CliPort.items array |> List.find (fun item -> CliPort.stringOf item field = Some value)

    let private hasCode (report: JsonNode) (code: string) =
        CliPort.items (report["findings"]) |> List.exists (fun finding -> CliPort.stringOf finding "code" = Some code)

    let private doctorTests =
        [ { Name = "echelon doctor: reports a healthy pinned toolchain"
            Run = fun () ->
                healthy (fun environment ->
                    let out = doctor environment [] |> succeeded 0

                    for pattern in
                        [ "Echelon Doctor"; @"ordo active\s+1\.4\.0"; @"praxis active\s+3\.4\.0"; @"Other installed tools\s+limen \(0\.9\.0\)"
                          @"Ordo requirement\s+1\.4\.0"; @"Praxis requirement\s+3\.4\.0"; "Repository components"; @"visual-engineering\s+1\.0\.0"
                          @"limen\s+0\.6\.2"; "Installed Echelon npm packages"; @"@echelon-foundry/typescript-wasm-kernel\s+0\.6\.2"
                          @"Errors:\s+0"; @"Warnings:\s+0"; @"Environment healthy\." ] do
                        CliPort.matches pattern out) }
          { Name = "echelon doctor: a binary directory missing from PATH is a warning, not an error"
            Run = fun () ->
                withEnvironment "3.4.0" false (fun environment ->
                    let out = doctor environment [] |> succeeded 0

                    for pattern in [ @"PATH\s+.*is not on PATH"; @"Errors:\s+0"; @"Warnings:\s+1"; @"Environment usable with warnings\." ] do
                        CliPort.matches pattern out) }
          { Name = "echelon doctor: fails when the active version violates the repository manifest"
            Run = fun () ->
                withEnvironment "9.9.9" true (fun environment ->
                    let out = doctor environment [] |> succeeded 1

                    for pattern in [ @"Praxis requirement\s+required 9\.9\.9; active 3\.4\.0"; @"Environment requires attention\."; "echelon doctor --fix" ] do
                        CliPort.matches pattern out) }
          { Name = "echelon doctor: --verbose exposes activation and working paths"
            Run = fun () ->
                healthy (fun environment ->
                    let out = doctor environment [ "--verbose" ] |> succeeded 0

                    for text in [ "Paths"; "Binary directory"; "Tools directory"; "Working directory"; "Ordo activation"; "Praxis activation" ] do
                        CliPort.contains text out) }
          { Name = "echelon doctor: --fix is a safe no-op on a healthy toolchain"
            Run = fun () ->
                healthy (fun environment ->
                    let out = doctor environment [ "--fix" ] |> succeeded 0

                    for pattern in [ "Repairs"; @"Ordo\s+no mechanical repair needed"; @"Praxis\s+no mechanical repair needed"; @"Environment healthy\." ] do
                        CliPort.matches pattern out) }
          { Name = "echelon doctor: resolves the manifest and repository state from a nested directory"
            Run = fun () ->
                healthy (fun environment ->
                    let nested = Path.Combine(environment.Project, "src", "feature")
                    Directory.CreateDirectory nested |> ignore
                    let out = echelonIn nested environment [ "doctor" ] |> succeeded 0

                    for pattern in [ @"Toolchain manifest\s+.*\.echelon/toolchain\.json"; @"Praxis requirement\s+3\.4\.0"; @"Environment healthy\." ] do
                        CliPort.matches pattern out) }
          { Name = "echelon doctor: delegates repository validation from the Git root"
            Run = fun () ->
                healthy (fun environment ->
                    Directory.CreateDirectory(Path.Combine(environment.Project, ".sde")) |> ignore
                    Directory.CreateDirectory(Path.Combine(environment.Project, ".ros")) |> ignore

                    // Each shim succeeds only when run from the project's Git root.
                    writeScript
                        (Path.Combine(environment.Bin, "ordo"))
                        ($"if [ \"$1\" = verify ]; then [ \"$PWD\" = \"{environment.Project}\" ]; exit $?; fi\n" + reports "1.4.0")

                    writeScript
                        (Path.Combine(environment.Bin, "praxis"))
                        ($"if [ \"$1\" = validate ]; then [ \"$PWD\" = \"{environment.Project}\" ]; exit $?; fi\n" + reports "3.4.0")

                    let nested = Path.Combine(environment.Project, "src", "nested")
                    Directory.CreateDirectory nested |> ignore
                    let out = echelonIn nested environment [ "doctor" ] |> succeeded 0
                    CliPort.matches @"Ordo repository\s+verify passed" out
                    CliPort.matches @"Praxis repository\s+validation passed" out) }
          { Name = "echelon doctor: --json emits the stable agent-readable health contract"
            Run = fun () ->
                healthy (fun environment ->
                    let report = doctor environment [ "--json" ] |> succeeded 0 |> CliPort.parse
                    Assert.equal 1.0 (CliPort.number (report["schemaVersion"]))
                    Assert.equal "echelon" (CliPort.text (report["tool"]))
                    Assert.equal "doctor" (CliPort.text (report["command"]))
                    Assert.equal "healthy" (CliPort.text (report["health"]))
                    Assert.equal 0.0 (CliPort.number (report["exitCode"]))
                    Assert.equal 0.0 (CliPort.number (report["summary"]["errors"]))
                    Assert.equal 0.0 (CliPort.number (report["summary"]["warnings"]))
                    CliPort.deepEqual "[]" (report["findings"])
                    Assert.isTrue (isNull (report["updates"])) "updates must be null unless requested"
                    Assert.equal true (CliPort.boolean (report["machine"]["pathConfigured"]))
                    Assert.equal "1.4.0" (CliPort.text (CliPort.at [ "repository"; "requirements"; "ordo" ] report))
                    Assert.equal "3.4.0" (CliPort.text (CliPort.at [ "repository"; "requirements"; "praxis" ] report))

                    let praxisCommand = find (report["commands"]) "name" "praxis"
                    Assert.equal "3.4.0" (CliPort.text (praxisCommand["version"]))
                    Assert.equal "3.4.0" (CliPort.text (praxisCommand["expectedVersion"]))
                    Assert.equal true (CliPort.boolean (praxisCommand["healthy"]))
                    Assert.isTrue (isNull ((find (report["commands"]) "name" "echelon")["expectedVersion"])) "echelon has no expected version"

                    let praxis = find (report["nativeTools"]) "name" "praxis"
                    Assert.equal "3.4.0" (CliPort.text (praxis["activeVersion"]))
                    Assert.isTrue (CliPort.items (praxis["installedVersions"]) |> List.exists (fun version -> CliPort.text version = "3.4.0")) "3.4.0 is installed"

                    Assert.equal "1.0.0" (CliPort.text ((find (report["repository"]["components"]) "tool" "visual-engineering")["installedVersion"]))
                    Assert.equal "0.6.2" (CliPort.text ((find (report["repository"]["components"]) "tool" "limen")["installedVersion"]))
                    Assert.equal "0.6.2" (CliPort.text ((find (report["repository"]["npmPackages"]) "package" "@echelon-foundry/typescript-wasm-kernel")["version"]))) }
          { Name = "echelon doctor: --json carries stable finding codes for warnings and errors"
            Run = fun () ->
                withEnvironment "3.4.0" false (fun environment ->
                    let report = doctor environment [ "--json" ] |> succeeded 0 |> CliPort.parse
                    Assert.equal "warning" (CliPort.text (report["health"]))
                    Assert.isTrue (hasCode report "ECHELON-DOC-001") "a PATH warning carries ECHELON-DOC-001")

                withEnvironment "9.9.9" true (fun environment ->
                    let report = doctor environment [ "--json" ] |> succeeded 1 |> CliPort.parse
                    Assert.equal "error" (CliPort.text (report["health"]))
                    Assert.isTrue (hasCode report "ECHELON-DOC-034") "a requirement violation carries ECHELON-DOC-034") }
          { Name = "echelon doctor: detects a command alias that reports the wrong active version"
            Run = fun () ->
                healthy (fun environment ->
                    writeScript (Path.Combine(environment.Bin, "ros")) (reports "ros-fs 2.0.0")
                    let report = doctor environment [ "--json" ] |> succeeded 1 |> CliPort.parse
                    let ros = find (report["commands"]) "name" "ros"
                    Assert.equal false (CliPort.boolean (ros["healthy"]))
                    Assert.equal "2.0.0" (CliPort.text (ros["version"]))
                    Assert.isTrue (hasCode report "ECHELON-DOC-021") "a mismatched alias carries ECHELON-DOC-021") }
          { Name = "echelon doctor: refuses to mix repair side effects with JSON output"
            Run = fun () ->
                healthy (fun environment ->
                    let result = doctor environment [ "--fix"; "--json" ]
                    CliPort.exitCode 2 result
                    CliPort.contains "cannot be combined" result.Err
                    Assert.equal "" result.Out) }
          { Name = "echelon doctor: --updates reports release awareness without changing health"
            Run = fun () ->
                healthy (fun baseEnvironment ->
                    let environment =
                        { baseEnvironment with
                            Variables = baseEnvironment.Variables @ [ "ECHELON_ORDO_LATEST_VERSION", "1.5.0"; "ECHELON_PRAXIS_LATEST_VERSION", "3.4.0" ] }

                    let report = doctor environment [ "--json"; "--updates" ] |> succeeded 0 |> CliPort.parse
                    Assert.equal "healthy" (CliPort.text (report["health"]))
                    Assert.equal 0.0 (CliPort.number (report["summary"]["errors"]))
                    Assert.equal 0.0 (CliPort.number (report["summary"]["warnings"]))
                    let ordo = (report["updates"]["ordo"])
                    Assert.equal "checked" (CliPort.text (ordo["status"]))
                    Assert.equal "1.4.0" (CliPort.text (ordo["activeVersion"]))
                    Assert.equal "1.5.0" (CliPort.text (ordo["latestStable"]))
                    Assert.equal true (CliPort.boolean (ordo["available"]))
                    Assert.equal "3.4.0" (CliPort.text (CliPort.at [ "updates"; "praxis"; "latestStable" ] report))
                    Assert.equal false (CliPort.boolean (CliPort.at [ "updates"; "praxis"; "available" ] report))

                    let out = doctor environment [ "--updates" ] |> succeeded 0
                    CliPort.contains "Updates" out
                    CliPort.matches @"Ordo\s+active 1\.4\.0; latest stable 1\.5\.0" out
                    CliPort.matches @"Praxis\s+3\.4\.0 is current" out) } ]

    let private inventoryTests =
        [ { Name = "echelon inventory: reports native, repository and npm installations without health validation"
            Run = fun () ->
                healthy (fun environment ->
                    let out = echelon environment [ "inventory" ] |> succeeded 0
                    CliPort.contains "Echelon Inventory" out
                    CliPort.matches @"visual-engineering\s+1\.0\.0" out
                    CliPort.matches @"@echelon-foundry/typescript-wasm-kernel\s+0\.6\.2" out

                    let report = echelon environment [ "inventory"; "--json" ] |> succeeded 0 |> CliPort.parse
                    Assert.equal 1.0 (CliPort.number (report["schemaVersion"]))
                    Assert.equal "inventory" (CliPort.text (report["command"]))
                    find (report["nativeTools"]) "name" "ordo" |> ignore
                    find (report["repository"]["components"]) "tool" "limen" |> ignore
                    find (report["repository"]["npmPackages"]) "package" "@echelon-foundry/typescript-wasm-kernel" |> ignore) }
          { Name = "echelon doctor and inventory: the JSON schemas are versioned and shipped from schemas/"
            Run = fun () ->
                let schema name = CliPort.readJson CliPort.repositoryRoot.Value $"schemas/{name}"
                let required (document: JsonNode) = CliPort.items (document["required"]) |> List.map CliPort.text
                let doctorSchema = schema "echelon-doctor-v1.schema.json"
                Assert.equal 1.0 (CliPort.number (CliPort.at [ "properties"; "schemaVersion"; "const" ] doctorSchema))
                Assert.equal "doctor" (CliPort.text (CliPort.at [ "properties"; "command"; "const" ] doctorSchema))

                for field in [ "findings"; "nativeTools"; "repository"; "updates" ] do
                    Assert.isTrue (required doctorSchema |> List.contains field) $"the doctor schema must require '{field}'"

                let inventorySchema = schema "echelon-inventory-v1.schema.json"
                Assert.equal 1.0 (CliPort.number (CliPort.at [ "properties"; "schemaVersion"; "const" ] inventorySchema))
                Assert.equal "inventory" (CliPort.text (CliPort.at [ "properties"; "command"; "const" ] inventorySchema))

                for field in [ "nativeTools"; "repository" ] do
                    Assert.isTrue (required inventorySchema |> List.contains field) $"the inventory schema must require '{field}'" } ]

    let tests = doctorTests @ inventoryTests
