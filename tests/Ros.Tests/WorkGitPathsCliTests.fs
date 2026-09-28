namespace Ros.Tests

open System.IO
open System.Text.Json.Nodes

/// `work context-plan` performing its own real Git observation against a
/// real repository: baseline capture, housekeeping exclusion, configured
/// meaningful/ignored patterns and ROS_BASE_REF ranges (ported from the
/// retired tests/work-git-paths-fsharp-differential.test.mjs; the expected
/// path sets are the goldens frozen from the former Node implementation).
[<RequireQualifiedAccess>]
module WorkGitPathsCliTests =
    let private strings (node: JsonNode) =
        CliGolden.items node |> List.map (fun value -> value.GetValue<string>())

    let private patterns (values: string list) =
        CliGolden.jsonList (values |> List.map (fun value -> JsonValue.Create value :> JsonNode))

    /// Telemetry disabled, optional path patterns, and a clean-slate context
    /// (no startedAt, no work items) so the first-begin baseline capture is
    /// really exercised; then one baseline commit.
    let private withRepository (meaningful: string list option) (ignored: string list option) test =
        CliGolden.withRepository
            "ros-git-paths-differential"
            "Git Paths Differential"
            (fun root ->
                CliGolden.updateJson root "ros.json" (fun config ->
                    config.["telemetry"].["enabled"] <- JsonValue.Create false
                    meaningful |> Option.iter (fun values -> config.["workProtocol"].["meaningfulPaths"] <- patterns values)
                    ignored |> Option.iter (fun values -> config.["workProtocol"].["ignoredPaths"] <- patterns values))

                CliGolden.writeJson
                    root
                    CliGolden.contextPath
                    (CliGolden.parse """{ "schemaVersion": "1.0.0", "repository": "git-paths-differential", "workItems": [] }"""))
            true
            test

    let private contextPlan root (context: JsonNode) action (environment: (string * string) list) (evidence: string list) =
        let snapshot = Path.Combine(Path.GetTempPath(), $"ros-git-paths-context-{System.Guid.NewGuid():N}.json")
        File.WriteAllText(snapshot, context.ToJsonString())

        try
            let result =
                CliHarness.rosWith
                    root
                    environment
                    ([ "work"; "context-plan"; "--context"; snapshot; "--action"; action; "--occurred-at"; "2026-09-09T00:00:00Z" ]
                     @ [ "--repository"; "repository"; "--protocol-version"; "1.0.0"; "--actor"; "unknown"; "--type"; "task" ]
                     @ [ "--id"; "TASK-GIT" ]
                     @ (evidence |> List.collect (fun value -> [ "--evidence"; value ])))

            CliGolden.expectExit 0 result
            (CliGolden.parse result.Out).["plan"]
        finally
            File.Delete snapshot

    /// A real `work start`, only to advance the fixture's on-disk state the way
    /// a begin would before the next plan observes the working tree.
    let private beginWork root =
        CliHarness.ros root [ "work"; "start"; "--id"; "TASK-GIT"; "--occurred-at"; CliGolden.at (); "--type"; "task" ]
        |> CliGolden.expectExit 0

        Assert.equal 1 (CliGolden.items (CliGolden.context root).["workItems"]).Length

    let private completionPaths (plan: JsonNode) =
        let event = CliGolden.items plan.["events"] |> List.head
        strings event.["paths"]

    let tests =
        [ { Name = "work git paths: context-plan captures the real Git baseline on first begin"
            Run =
              fun () ->
                  withRepository None None (fun root ->
                      let before = CliGolden.context root
                      File.AppendAllText(Path.Combine(root, "README.md"), "\nmore\n")
                      CliHarness.write root "untracked.txt" "new\n"
                      let plan = contextPlan root before "begin" [] []
                      Assert.equal [ "README.md"; "untracked.txt" ] (List.sort (strings plan.["baselineDirtyPaths"]))) }

          { Name = "work git paths: context-plan excludes Praxis housekeeping paths from completion paths"
            Run =
              fun () ->
                  withRepository None None (fun root ->
                      beginWork root
                      let afterBegin = CliGolden.context root
                      CliHarness.write root "src.txt" "meaningful change\n"

                      let plan =
                          contextPlan root afterBegin "complete" [] [ "implementation=src.txt"; "tests=src.txt" ]

                      Assert.equal [ "src.txt" ] (completionPaths plan)) }

          { Name = "work git paths: context-plan applies configured meaningful and ignored patterns"
            Run =
              fun () ->
                  withRepository (Some [ "src/**" ]) (Some [ "src/generated/**" ]) (fun root ->
                      beginWork root
                      let afterBegin = CliGolden.context root
                      CliHarness.write root "src/app.ts" "meaningful\n"
                      CliHarness.write root "src/generated/out.js" "ignored\n"
                      CliHarness.write root "docs.md" "not meaningful (outside src/**)\n"

                      let plan =
                          contextPlan root afterBegin "complete" [] [ "implementation=src/app.ts"; "tests=src/app.ts" ]

                      Assert.equal [ "src/app.ts" ] (completionPaths plan)) }

          { Name = "work git paths: context-plan includes a resolvable ROS_BASE_REF committed range"
            Run =
              fun () ->
                  withRepository None None (fun root ->
                      CliHarness.git root [ "checkout"; "-qb"; "feature" ] |> ignore
                      CliHarness.write root "committed-change.txt" "committed\n"
                      CliHarness.commitAll root "feature commit"
                      let baseRef = CliHarness.git root [ "rev-parse"; "HEAD~1" ]
                      let plan = contextPlan root (CliGolden.context root) "begin" [ "ROS_BASE_REF", baseRef ] []
                      Assert.equal [ "committed-change.txt" ] (List.sort (strings plan.["baselineDirtyPaths"]))) }

          { Name = "work git paths: context-plan silently skips an unresolvable ROS_BASE_REF"
            Run =
              fun () ->
                  withRepository None None (fun root ->
                      let before = CliGolden.context root
                      CliHarness.write root "untracked.txt" "new\n"

                      let plan =
                          contextPlan root before "begin" [ "ROS_BASE_REF", "refs/does-not-exist" ] []

                      Assert.equal [ "untracked.txt" ] (List.sort (strings plan.["baselineDirtyPaths"]))) } ]
