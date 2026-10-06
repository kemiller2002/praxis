namespace Ros.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes

/// `status` and the unified `validate` through the real CLI, compared with
/// the goldens frozen from the former Node implementation (ported from the
/// retired tests/status- and validate-unified-fsharp-differential.test.mjs).
[<RequireQualifiedAccess>]
module StatusValidateCliTests =
    let private work root (arguments: string list) =
        CliHarness.ros root ("work" :: arguments) |> CliGolden.expectExit 0

    // ----------------------------------------------------------------- status

    let private statusGolden = CliGolden.golden "status"

    let private sha256 (path: string) =
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

    /// The retired status fixtures were installed by the legacy Node
    /// bootstrap (`ros-bootstrap init`), which recorded its state only in
    /// `.ros/installation.json` and wrote no `.echelon/ros.json`. That is what
    /// an existing consumer repository still looks like, so the fixture is
    /// reproduced from a real `praxis init`: the current manifest is removed and
    /// the legacy snapshot is written literally in the shape the bootstrap
    /// wrote (`installationManifest` in the retired lib/bootstrap.mjs).
    let private legacyInstallation root =
        File.Delete(Path.Combine(root, ".echelon", "ros.json"))

        let file (relative: string) =
            $"""    {{ "path": "{relative}", "sha256": "{sha256 (Path.Combine(root, relative))}", "managed": true, "disposition": "installed" }}"""

        let files = [ "AGENTS.md"; "BOOTSTRAP.md"; "ros.json" ] |> List.map file |> String.concat ",\n"

        CliHarness.write
            root
            ".ros/installation.json"
            ($"""{{
  "schema_version": "1.0.0",
  "package": "{CliGolden.releasePackage ()}",
  "package_version": "{CliGolden.releaseVersion ()}",
  "profile": "greenfield",
  "project": "Status Differential",
  "project_slug": "status-differential",
  "installed_on": "2026-09-28",
  "update_policy": "additive; collisions require explicit migration",
  "files": [
{files}
  ]
}}"""
             + "\n")

    let private withLegacyRepository test =
        CliGolden.withRepository "ros-status" "Status Differential" legacyInstallation false test

    let private status root (arguments: string list) =
        let result = CliHarness.ros root ("status" :: arguments)
        CliGolden.expectExit 0 result
        result

    /// The retired `normalize`: the additive `installation` block (asserted
    /// separately) removed and execution ids reduced to presence.
    let private normalized (record: JsonNode) =
        // `continuity` (DF-ROS-2026-A042) is additive like `installation`; its
        // behaviour is pinned by CheckpointCliTests, as main's golden did.
        let copy = CliGolden.without (set [ "installation"; "continuity" ]) record

        for item in CliGolden.items copy.["workItems"] do
            let ids = item.["telemetryExecutionIds"]
            item.AsObject().Remove "telemetryExecutionIds" |> ignore
            item.["hasExecIds"] <- JsonValue.Create((CliGolden.items ids).Length > 0)

        copy

    let private assertCommonInstallation (installation: JsonNode) =
        Assert.isTrue (not (isNull installation)) "status must carry the installation block"
        Assert.equal 1 (installation.["schemaVersion"].GetValue<int>())
        Assert.equal (Some "ros") (CliGolden.text installation "tool")
        Assert.equal (Some(CliGolden.releasePackage ())) (CliGolden.text installation "package")
        // One authoritative version source: the CLI reports release.json's version.
        Assert.equal (Some(CliGolden.releaseVersion ())) (CliGolden.text installation "cliVersion")
        Assert.isTrue (not (installation.AsObject().ContainsKey "managedArtifacts")) "managedArtifacts only appears with --verbose"

    let private assertLegacyInstallation (installation: JsonNode) =
        assertCommonInstallation installation
        Assert.equal (Some "upgrade-required") (CliGolden.text installation "state")
        Assert.equal (Some "legacy") (CliGolden.text installation "installedVersion")
        Assert.equal (Some(CliGolden.releaseVersion ())) (CliGolden.text installation "upgradeAvailable")
        CliGolden.jsonEqual null installation.["configurationVersion"]
        CliGolden.jsonEqual null installation.["managedArtifactCount"]
        // A legacy install is a warning, never an error.
        Assert.equal true (installation.["verified"].GetValue<bool>())

    let private assertCurrentInstallation (installation: JsonNode) =
        assertCommonInstallation installation
        Assert.equal (Some "installed") (CliGolden.text installation "state")
        Assert.equal (Some(CliGolden.releaseVersion ())) (CliGolden.text installation "installedVersion")
        Assert.equal 1 (installation.["configurationVersion"].GetValue<int>())
        Assert.equal (Some "greenfield") (CliGolden.text installation "profile")
        Assert.equal true (installation.["verified"].GetValue<bool>())
        CliGolden.jsonEqual null installation.["upgradeAvailable"]
        Assert.isTrue (installation.["managedArtifactCount"].GetValue<int>() > 0) "managed artifacts must be counted"

    let private statusTests =
        [ { Name = "status: a clean legacy installation reports passed validation and the legacy installation block"
            Run =
              fun () ->
                  withLegacyRepository (fun root ->
                      let parsed = CliGolden.parse (status root []).Out
                      CliGolden.jsonEqual (statusGolden "test1") (normalized parsed)
                      assertLegacyInstallation parsed.["installation"]) }

          { Name = "status: --json is identical to status, and --verbose adds the managed artifact list"
            Run =
              fun () ->
                  withLegacyRepository (fun root ->
                      let plain = status root []
                      Assert.equal plain.Out (status root [ "--json" ]).Out

                      // Adopt the .echelon manifest so there is an artifact list to show.
                      CliHarness.ros root [ "upgrade" ] |> CliGolden.expectExit 0
                      let verbose = (CliGolden.parse (status root [ "--verbose" ]).Out).["installation"]
                      assertCurrentInstallation (CliGolden.parse (status root []).Out).["installation"]
                      let artifacts = CliGolden.items verbose.["managedArtifacts"]
                      Assert.equal (verbose.["managedArtifactCount"].GetValue<int>()) artifacts.Length

                      for artifact in artifacts do
                          let ownership = artifact.["ownership"].GetValue<string>()

                          Assert.isTrue
                              (List.contains ownership [ "tool-owned"; "generated"; "user-owned"; "shared" ])
                              $"unexpected ownership {ownership}"

                          Assert.isTrue
                              (RegularExpressions.Regex.IsMatch(artifact.["sha256"].GetValue<string>(), "^[0-9a-f]{64}$"))
                              "sha256 must be 64 lowercase hex digits") }

          { Name = "status: active and blocked work, telemetry counts and a real finding are reported"
            Run =
              fun () ->
                  withLegacyRepository (fun root ->
                      for id, title in [ "WI-READY", "Ready item"; "WI-ACTIVE", "Active item" ] do
                          work root [ "capture"; "--id"; id; "--title"; title; "--occurred-at"; CliGolden.at () ]
                          work root [ "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; CliGolden.at () ]
                          work root [ "start"; "--id"; id; "--occurred-at"; CliGolden.at () ]

                      work root [ "block"; "--id"; "WI-ACTIVE"; "--reason"; "waiting"; "--occurred-at"; CliGolden.at () ]

                      CliGolden.updateJson root CliGolden.queuePath (fun queue ->
                          queue.["items"].AsArray().Add(
                              CliGolden.parse
                                  """{ "id": "WI-BAD", "title": "bad", "status": "not-a-status", "tags": [], "priority": "medium", "attachments": [],
                                       "createdAt": "2026-01-01T00:00:00.000Z", "updatedAt": "2026-01-01T00:00:00.000Z", "createdBy": "unknown", "source": "manual" }"""
                          ))

                      let parsed = CliGolden.parse (status root []).Out
                      CliGolden.jsonEqual (statusGolden "test2") (normalized parsed)
                      assertLegacyInstallation parsed.["installation"]) } ]

    // --------------------------------------------------------------- validate

    let private validateGolden = CliGolden.golden "validate-unified"

    let private withValidateRepository test =
        CliGolden.withRepository "ros-validate-unified" "Validate Unified Differential" CliGolden.noPreparation true test

    let private validate root (arguments: string list) = CliHarness.ros root ("validate" :: arguments)

    let private validateJson root = CliGolden.parse (validate root [ "--json" ]).Out

    let private validateTests =
        [ { Name = "validate: a clean installation passes in both text and --json output"
            Run =
              fun () ->
                  withValidateRepository (fun root ->
                      let text = validate root []
                      CliGolden.expectExit 0 text
                      Assert.equal "validation passed\n" text.Out
                      CliGolden.jsonEqual (validateGolden "clean").["json"] (validateJson root)) }

          { Name = "validate: a backlog-queue finding and a disabled-telemetry finding are combined"
            Run =
              fun () ->
                  withValidateRepository (fun root ->
                      CliGolden.updateJson root CliGolden.queuePath (fun queue ->
                          queue.["items"].AsArray().Add(
                              CliGolden.parse
                                  """{ "id": "WI-BAD", "title": "bad", "status": "not-a-status", "tags": [], "priority": "medium", "attachments": [],
                                       "createdAt": "2026-01-01T00:00:00.000Z", "updatedAt": "2026-01-01T00:00:00.000Z", "createdBy": "unknown", "source": "manual" }"""
                          ))

                      CliGolden.updateJson root "ros.json" (fun config -> config.["telemetry"] <- CliGolden.parse """{ "enabled": false }""")
                      let json = validateJson root
                      CliGolden.jsonEqual (validateGolden "combined").["json"] json
                      Assert.equal 2 (CliGolden.items json.["findings"]).Length) }

          { Name = "validate: a stale registry is reported"
            Run =
              fun () ->
                  withValidateRepository (fun root ->
                      CliHarness.write root "registries/decisions.json" """{"broken":true}"""
                      CliGolden.jsonEqual (validateGolden "stale").["json"] (validateJson root)) }

          { Name = "validate: artifact parse findings and a work-attribution finding render identically as JSON and text"
            Run =
              fun () ->
                  withValidateRepository (fun root ->
                      CliHarness.write
                          root
                          "research/decisions/DF-BROKEN--test.md"
                          "---\nid: not valid!!\ntitle: Broken\nstatus: accepted\n---\nBody.\n"

                      let expected = validateGolden "artifactAndAttribution"
                      CliGolden.jsonEqual expected.["json"] (validateJson root)
                      let text = validate root []
                      Assert.equal (expected.["textStatus"].GetValue<int>()) text.Exit
                      Assert.equal (expected.["textStdout"].GetValue<string>()) text.Out
                      Assert.equal (expected.["textStderr"].GetValue<string>()) text.Err) }

          { Name = "validate and registry commands govern concept and glossary records like other artifact kinds"
            Run =
              fun () ->
                  withValidateRepository (fun root ->
                      let conceptPath = "research/concepts/CN-TEST-2026-A001--term-boundary.md"
                      let glossaryPath = "research/glossary/GL-TEST-2026-A002--glossary-entry.md"

                      CliHarness.write
                          root
                          conceptPath
                          "---\nid: CN-TEST-2026-A001\ntitle: Term boundary\nstatus: established\ncreated: 2026-10-01\nrelated_documents: [GL-TEST-2026-FFFF]\n---\nBody.\n"

                      CliHarness.write root glossaryPath "---\nid: GL-TEST-2026-A002\ntitle: Glossary entry\nstatus: draft\n---\nBody.\n"

                      let findingsAt path =
                          CliGolden.items (validateJson root).["findings"]
                          |> List.filter (fun finding -> finding.["path"].GetValue<string>() = path)
                          |> List.map (fun finding -> finding.["field"].GetValue<string>(), finding.["message"].GetValue<string>())

                      let concept = findingsAt conceptPath
                      let has field (fragment: string) = concept |> List.exists (fun (actual, message) -> actual = field && message.Contains(fragment, StringComparison.Ordinal))
                      Assert.isTrue (has "status" "'established' is not allowed for CN") $"status finding missing: {concept}"
                      Assert.isTrue (has "related_documents" "broken reference 'GL-TEST-2026-FFFF'") $"reference finding missing: {concept}"
                      Assert.isTrue (has "provenance" "records no provenance") $"provenance finding missing: {concept}"

                      let registry = CliHarness.ros root [ "registry"; "check" ]
                      Assert.isTrue (registry.Exit <> 0) "registry check must report the missing concept and glossary registries"
                      CliGolden.contains "registries/concepts.json" (registry.Out + registry.Err)
                      CliGolden.contains "registries/glossary.json" (registry.Out + registry.Err)

                      CliGolden.expectExit 0 (CliHarness.ros root [ "registry"; "build" ])
                      Assert.isTrue (File.Exists(Path.Combine(root, "registries", "concepts.json"))) "concept registry written"
                      Assert.isTrue (File.Exists(Path.Combine(root, "registries", "glossary.json"))) "glossary registry written"
                      CliGolden.expectExit 0 (CliHarness.ros root [ "registry"; "check" ])
                      CliGolden.contains "GL-TEST-2026-A002" (File.ReadAllText(Path.Combine(root, "registries", "glossary.json")))) } ]

    let tests = statusTests @ validateTests
