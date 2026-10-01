namespace Praxis.Tests

open System.IO
open Praxis.Domain.Architecture

/// The F#/.NET-only repository invariant (DF-ROS-2026-A049): the pure
/// policy, the `architecture check` command, its place in the unified
/// `validate`, and this repository's own compliance.
[<RequireQualifiedAccess>]
module ImplementationLanguagePolicyTests =
    let private enforced =
        { ProhibitNodeArtifacts = true
          Exceptions = [] }

    let private prohibitedSamples =
        [ "tools/cli.js"; "web/app.jsx"; "bin/ros.mjs"; "lib/x.cjs"; "web/app.ts"; "web/view.tsx"; "types/index.d.ts"
          "package.json"; "web/package-lock.json"; "npm-shrinkwrap.json"; "yarn.lock"; "pnpm-lock.yaml"; "bun.lock"
          "bun.lockb"; "web/tsconfig.json" ]

    let private permittedSamples =
        [ "src/Praxis.Cli/Program.fs"; "Praxis.slnx"; "release.json"; "ros.json"; "docs/node.md"; "site/index.html"
          "scripts/install-native.sh"; "bin/echelon.ps1"; "notes/package.json.md"; "Package.json"; "tools/json.tsv" ]

    let private policyJson (body: string) = $"{{\n  \"implementationPolicy\": {body}\n}}\n"

    let private repositoryWith (policy: string option) (files: (string * string) list) =
        let root = CliHarness.temporaryDirectory "ros-language-policy"
        CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
        policy |> Option.iter (fun body -> CliHarness.write root "ros.json" (policyJson body))
        files |> List.iter (fun (path, content) -> CliHarness.write root path content)
        root

    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "release.json"))
           && File.Exists(Path.Combine(directory.FullName, "Praxis.slnx")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate repository root"
        else
            repositoryRoot directory.Parent

    let tests =
        [ { Name = "language policy: every prohibited Node/JavaScript/TypeScript artifact kind is classified"
            Run =
              fun () ->
                  prohibitedSamples
                  |> List.filter (ImplementationLanguagePolicy.classify >> Option.isNone)
                  |> Assert.empty }

          { Name = "language policy: F#, shell, documentation and data files are permitted"
            Run =
              fun () ->
                  permittedSamples
                  |> List.filter (ImplementationLanguagePolicy.classify >> Option.isSome)
                  |> Assert.empty }

          { Name = "language policy: extensions match case-insensitively, manifest names exactly"
            Run =
              fun () ->
                  Assert.equal (Some(ProhibitedArtifact.NodeSource ".ts")) (ImplementationLanguagePolicy.classify "web/App.TS")
                  Assert.equal None (ImplementationLanguagePolicy.classify "Package.json")
                  Assert.equal (Some(ProhibitedArtifact.NodeToolchainManifest "package.json")) (ImplementationLanguagePolicy.classify "a\\b\\package.json") }

          { Name = "language policy: violations are sorted, de-duplicated and exclude .git internals"
            Run =
              fun () ->
                  let found =
                      ImplementationLanguagePolicy.violations enforced [ "z.js"; "a.ts"; "z.js"; ".git/hooks/x.js"; "src/A.fs" ]

                  Assert.equal [ "a.ts"; "z.js" ] (found |> List.map (fun violation -> violation.Path)) }

          { Name = "language policy: a disabled policy reports nothing"
            Run =
              fun () ->
                  Assert.empty (ImplementationLanguagePolicy.violations ImplementationLanguagePolicy.disabled prohibitedSamples) }

          { Name = "language policy: exceptions cover an exact file or a directory prefix, never more"
            Run =
              fun () ->
                  let policy =
                      { enforced with
                          Exceptions =
                              [ { Path = "vendor/approved.js"; Decision = "DF-ROS-2026-A999" }
                                { Path = "third_party/"; Decision = "DF-ROS-2026-A999" } ] }

                  let found =
                      ImplementationLanguagePolicy.violations
                          policy
                          [ "vendor/approved.js"; "vendor/approved.js.map.js"; "third_party/lib/a.js"; "third_party.js" ]

                  Assert.equal [ "third_party.js"; "vendor/approved.js.map.js" ] (found |> List.map (fun violation -> violation.Path)) }

          { Name = "language policy: an exception must cite a DF- decision and must not use wildcards"
            Run =
              fun () ->
                  let policy =
                      { enforced with
                          Exceptions =
                              [ { Path = "vendor/*.js"; Decision = "because" }
                                { Path = ""; Decision = "DF-ROS-2026-A999" }
                                { Path = "ok.js"; Decision = "DF-ROS-2026-A999" } ] }

                  let fields = ImplementationLanguagePolicy.configurationFindings policy |> List.map (fun finding -> finding.Field)

                  Assert.equal
                      [ "implementationPolicy.exceptions[0].path"
                        "implementationPolicy.exceptions[0].decision"
                        "implementationPolicy.exceptions[1].path" ]
                      fields }

          { Name = "architecture check: fails with exact offending paths and exit 1 when Node artifacts are present"
            Run =
              fun () ->
                  let root =
                      repositoryWith
                          (Some """{ "prohibitNodeArtifacts": true, "exceptions": [] }""")
                          [ "src/App.fs", "module App"; "tools/build.mjs", "export {}"; "web/package.json", "{}" ]

                  try
                      let text = CliHarness.ros root [ "architecture"; "check" ]
                      Assert.equal 1 text.Exit
                      Assert.isTrue (text.Err.Contains "ERROR tools/build.mjs:implementation_language:") text.Err
                      Assert.isTrue (text.Err.Contains "ERROR web/package.json:implementation_language:") text.Err
                      Assert.isTrue (text.Err.Contains "architecture check failed with 2 violation(s)") text.Err

                      let json = CliHarness.ros root [ "architecture"; "check"; "--json" ]
                      Assert.equal 1 json.Exit
                      let document = CliHarness.json json.Out
                      Assert.equal false (document["valid"].GetValue<bool>())

                      let paths =
                          document["findings"].AsArray()
                          |> Seq.map (fun finding -> finding["path"].GetValue<string>())
                          |> List.ofSeq

                      Assert.equal [ "tools/build.mjs"; "web/package.json" ] paths
                  finally
                      CliHarness.removeDirectory root }

          { Name = "architecture check: ignored files are not repository-owned; untracked unignored files are"
            Run =
              fun () ->
                  let root =
                      repositoryWith
                          (Some """{ "prohibitNodeArtifacts": true }""")
                          [ ".gitignore", "node_modules/\n"; "node_modules/x/index.js", ""; "new.ts", "" ]

                  try
                      let result = CliHarness.ros root [ "architecture"; "check"; "--json" ]
                      let document = CliHarness.json result.Out
                      let paths = document["findings"].AsArray() |> Seq.map (fun f -> f["path"].GetValue<string>()) |> List.ofSeq
                      Assert.equal [ "new.ts" ] paths
                  finally
                      CliHarness.removeDirectory root }

          { Name = "architecture check: passes on a clean repository and honours a documented exception"
            Run =
              fun () ->
                  let root =
                      repositoryWith
                          (Some """{ "prohibitNodeArtifacts": true, "exceptions": [ { "path": "vendor/", "decision": "DF-ROS-2026-A999" } ] }""")
                          [ "src/App.fs", "module App"; "vendor/tool.js", "" ]

                  try
                      let result = CliHarness.ros root [ "architecture"; "check" ]
                      Assert.equal 0 result.Exit
                      Assert.isTrue (result.Out.Contains "architecture check passed") result.Out
                  finally
                      CliHarness.removeDirectory root }

          { Name = "architecture check: a repository that never opted in is not enforced"
            Run =
              fun () ->
                  let root = repositoryWith None [ "app.js", "" ]

                  try
                      let result = CliHarness.ros root [ "architecture"; "check" ]
                      Assert.equal 0 result.Exit
                      Assert.isTrue (result.Out.Contains "nothing is enforced") result.Out
                  finally
                      CliHarness.removeDirectory root }

          { Name = "architecture check: invalid arguments exit 2"
            Run =
              fun () ->
                  let root = repositoryWith None []

                  try
                      Assert.equal 2 (CliHarness.ros root [ "architecture"; "check"; "--bogus" ]).Exit
                      Assert.equal 2 (CliHarness.ros root [ "architecture" ]).Exit
                  finally
                      CliHarness.removeDirectory root }

          { Name = "validate: an opted-in initialized repository fails validation when a Node artifact appears"
            Run =
              fun () ->
                  let root = CliHarness.initializedRepository "ros-language-validate" None

                  try
                      let configuration = CliHarness.read root "ros.json"
                      let marker = "\n  \"provenance\""

                      let updated =
                          if configuration.Contains marker then
                              configuration.Replace(marker, "\n  \"implementationPolicy\": { \"prohibitNodeArtifacts\": true },\n  \"provenance\"")
                          else
                              failwith "initialized ros.json has no provenance section to anchor the policy"

                      CliHarness.write root "ros.json" updated
                      CliHarness.commitAll root "opt in to the implementation-language policy"
                      let clean = CliHarness.ros root [ "validate" ]
                      Assert.isTrue (clean.Exit = 0) $"{clean.Out}{clean.Err}"

                      CliHarness.write root "scripts/helper.js" "console.log(1)"
                      let result = CliHarness.ros root [ "validate"; "--json" ]
                      Assert.equal 1 result.Exit

                      let document = CliHarness.json result.Out

                      let fields =
                          document["findings"].AsArray()
                          |> Seq.map (fun finding -> finding["path"].GetValue<string>(), finding["field"].GetValue<string>())
                          |> List.ofSeq

                      Assert.isTrue (fields |> List.contains ("scripts/helper.js", "implementation_language")) $"{fields}"
                  finally
                      CliHarness.removeDirectory root }

          { Name = "init: no starter profile scaffolds a Node/JavaScript/TypeScript artifact into a project"
            Run =
              fun () ->
                  [ "greenfield"; "project-administration" ]
                  |> List.iter (fun profile ->
                      let root = CliHarness.initializedRepository "ros-language-scaffold" (Some profile)

                      try
                          let scaffolded =
                              Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                              |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'))
                              |> List.ofSeq

                          let offending = ImplementationLanguagePolicy.violations enforced scaffolded |> List.map (fun violation -> violation.Path)
                          Assert.isTrue offending.IsEmpty $"{profile} scaffolds {offending}"
                      finally
                          CliHarness.removeDirectory root) }

          { Name = "architecture check: this repository contains no repository-owned Node/JavaScript/TypeScript"
            Run =
              fun () ->
                  let root = repositoryRoot (DirectoryInfo(System.AppContext.BaseDirectory))
                  let result = CliHarness.ros root [ "architecture"; "check" ]
                  Assert.isTrue (result.Exit = 0) $"exit {result.Exit}\n{result.Out}{result.Err}" } ]
