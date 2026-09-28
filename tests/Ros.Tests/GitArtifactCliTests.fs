namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes

/// `git status --json`, `registry build|check` and `artifacts validate`
/// through the real CLI (ported from the retired tests/git- and
/// artifact-fsharp-differential.test.mjs; Git goldens frozen from the former
/// Node observer, artifact goldens from tests/fixtures/artifacts).
[<RequireQualifiedAccess>]
module GitArtifactCliTests =
    // ------------------------------------------------------------ git status

    let private gitGolden = CliGolden.golden "git-status"

    let private withGitRepository test =
        let root = CliHarness.temporaryDirectory "ros-git-differential"

        try
            CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
            CliHarness.write root "original.txt" "original\n"
            CliHarness.write root "modified.txt" "baseline\n"
            CliHarness.commitAll root "baseline"
            test root
        finally
            CliHarness.removeDirectory root

    let private gitStatus root =
        let result = CliHarness.ros root [ "git"; "status"; "--json" ]
        result, CliGolden.parse result.Out

    /// Git's own porcelain v1 -z listing (code, path, rename origin), sorted
    /// by path: the independent cross-check the retired suite made.
    let private porcelain root =
        let fields =
            CliHarness.run "git" [ "-C"; root; "status"; "--porcelain=v1"; "-z"; "--untracked-files=all" ] []
            |> fun result -> result.Out.Split('\000', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

        let rec entries (remaining: string list) =
            match remaining with
            | [] -> []
            | entry :: rest ->
                let code = entry.Substring(0, 2)
                let path = entry.Substring 3

                if code.IndexOfAny([| 'R'; 'C' |]) >= 0 then
                    match rest with
                    | origin :: tail -> (code, path, Some origin) :: entries tail
                    | [] -> failwith "rename without origin"
                else
                    (code, path, None) :: entries rest

        entries fields |> List.sortWith (fun (_, left, _) (_, right, _) -> String.CompareOrdinal(left, right))

    let private projected (changes: JsonNode) =
        CliGolden.items changes
        |> List.map (fun change -> change.["code"].GetValue<string>(), change.["path"].GetValue<string>(), CliGolden.text change "originalPath")

    let private gitTests =
        [ { Name = "git status: a clean repository reports the clean porcelain outcome"
            Run =
              fun () ->
                  withGitRepository (fun root ->
                      let result, json = gitStatus root
                      CliGolden.expectExit 0 result
                      CliGolden.jsonEqual (gitGolden "clean") json) }

          { Name = "git status: changed paths, statuses and the rename origin match Git's porcelain"
            Run =
              fun () ->
                  withGitRepository (fun root ->
                      CliHarness.git root [ "mv"; "original.txt"; "renamed.txt" ] |> ignore
                      File.AppendAllText(Path.Combine(root, "modified.txt"), "changed\n")
                      CliHarness.write root "untracked.txt" "new\n"
                      let expected = porcelain root
                      let result, json = gitStatus root
                      CliGolden.expectExit 0 result
                      Assert.equal "changed" (json.["outcome"].GetValue<string>())
                      Assert.equal expected (projected json.["changes"])
                      CliGolden.jsonEqual (gitGolden "changed") json

                      let rename =
                          CliGolden.items json.["changes"]
                          |> List.find (fun change -> change.["code"].GetValue<string>().StartsWith "R")

                      CliGolden.jsonEqual
                          (CliGolden.parse
                              """{ "code": "R ", "kind": "tracked", "index": "renamed", "workTree": "unmodified", "path": "renamed.txt", "originalPath": "original.txt" }""")
                          rename) }

          { Name = "git status: outside a repository the outcome is unavailable rather than clean"
            Run =
              fun () ->
                  let root = CliHarness.temporaryDirectory "ros-git-unavailable"

                  try
                      let result, json = gitStatus root
                      CliGolden.expectExit 1 result
                      Assert.equal "unavailable" (json.["outcome"].GetValue<string>())
                      Assert.equal "not-repository" (json.["failure"].["reason"].GetValue<string>())
                      Assert.equal 128 (json.["failure"].["exitCode"].GetValue<int>())
                      CliGolden.jsonEqual (gitGolden "unavailable") json
                  finally
                      CliHarness.removeDirectory root } ]

    // -------------------------------------------------------------- artifacts

    let private fixtureRoot () = Path.Combine(CliGolden.repositoryRoot (), "tests", "fixtures", "artifacts")

    let private manifestCase (id: string) =
        let manifest = CliGolden.parse (File.ReadAllText(Path.Combine(fixtureRoot (), "manifest.json")))
        CliGolden.find manifest.["cases"] id

    let rec private copyDirectory source destination =
        Directory.CreateDirectory destination |> ignore

        for file in Directory.GetFiles source do
            File.Copy(file, Path.Combine(destination, Path.GetFileName file), true)

        for child in Directory.GetDirectories source do
            copyDirectory child (Path.Combine(destination, Path.GetFileName child))

    /// A disposable copy of one artifact fixture (kept under its own name, as
    /// the retired suite did), removed afterwards.
    let private withFixtureCopy (name: string) test =
        let temporary = CliHarness.temporaryDirectory $"ros-fsharp-differential-{name}"
        let root = Path.Combine(temporary, name)
        copyDirectory (Path.Combine(fixtureRoot (), name)) root

        try
            test root
        finally
            CliHarness.removeDirectory temporary

    let private cli (arguments: string list) = CliHarness.run "dotnet" (CliHarness.cli :: arguments) []

    let private artifactTests =
        [ { Name = "artifact cli: registry build reproduces the frozen registry bytes and artifacts validate the characterized findings"
            Run =
              fun () ->
                  let valid = manifestCase "valid-all-kinds"
                  let validRoot = valid.["root"].GetValue<string>()

                  let registryNames =
                      valid.["registrySha256"].AsObject() |> Seq.map (fun entry -> entry.Key) |> Seq.sort |> Seq.toList

                  withFixtureCopy validRoot (fun root ->
                      Directory.Delete(Path.Combine(root, "registries"), true)
                      cli [ "--root"; root; "registry"; "build" ] |> CliGolden.expectExit 0

                      for name in registryNames do
                          Assert.equal
                              (File.ReadAllText(Path.Combine(fixtureRoot (), validRoot, "registries", name)))
                              (File.ReadAllText(Path.Combine(root, "registries", name)))

                      let repeat = cli [ "--root"; root; "registry"; "build" ]
                      CliGolden.expectExit 0 repeat
                      Assert.equal "0 registry file(s) changed\n" repeat.Out)

                  let invalid = manifestCase "invalid-mixed"

                  withFixtureCopy (invalid.["root"].GetValue<string>()) (fun root ->
                      let result = cli [ "--root"; root; "artifacts"; "validate"; "--json" ]
                      CliGolden.expectExit 1 result

                      let findings =
                          CliGolden.items (CliGolden.parse result.Out).["findings"]
                          |> List.map (fun finding ->
                              let projection = JsonObject()

                              for field in [ "path"; "field"; "message" ] do
                                  projection.[field] <- CliGolden.clone finding.[field]

                              projection :> JsonNode)

                      CliGolden.jsonEqual invalid.["expectedFindings"] (CliGolden.jsonList findings)) }

          { Name = "artifact cli: the Praxis repository itself validates and its registries are current (read-only)"
            Run =
              fun () ->
                  let root = CliGolden.repositoryRoot ()
                  let validation = cli [ "--root"; root; "artifacts"; "validate"; "--json" ]
                  CliGolden.expectExit 0 validation
                  CliGolden.jsonEqual (CliGolden.parse """{ "valid": true, "findings": [] }""") (CliGolden.parse validation.Out)
                  let check = cli [ "--root"; root; "registry"; "check" ]
                  CliGolden.expectExit 0 check
                  Assert.equal "registries are current\n" check.Out }

          { Name = "artifact cli: registry build replays a pending artifact-registries transaction"
            Run =
              fun () ->
                  withFixtureCopy ((manifestCase "valid-all-kinds").["root"].GetValue<string>()) (fun root ->
                      let registryPath = "registries/evidence.json"
                      let expected = CliHarness.read root registryPath
                      let transaction = JsonObject()
                      transaction.["schemaVersion"] <- JsonValue.Create "1.0.0"
                      transaction.["resource"] <- JsonValue.Create "artifact-registries"
                      let write = JsonObject()
                      write.["path"] <- JsonValue.Create registryPath
                      write.["content"] <- JsonValue.Create expected
                      transaction.["writes"] <- CliGolden.jsonList [ write ]
                      CliHarness.write root ".ros/transactions/artifact-registries.json" (transaction.ToJsonString() + "\n")
                      CliHarness.write root registryPath "[]\n"

                      let result = cli [ "--root"; root; "registry"; "build" ]
                      CliGolden.expectExit 0 result
                      Assert.equal "0 registry file(s) changed\n" result.Out
                      Assert.equal expected (CliHarness.read root registryPath)

                      Assert.isTrue
                          (not (File.Exists(Path.Combine(root, ".ros", "transactions", "artifact-registries.json"))))
                          "the replayed transaction must be removed") }

          { Name = "artifact cli: an unknown command is a usage error"
            Run =
              fun () ->
                  let result = cli [ "not-a-command" ]
                  CliGolden.expectExit 2 result
                  CliGolden.contains "Usage: ros-fs" result.Err } ]

    let tests = gitTests @ artifactTests
