namespace Ros.Tests

open System
open System.IO
open Ros.Domain.Work
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module PathFilterTests =
    let private cases =
        [ "**", "a/b/c.txt", true
          "src/**/*.ts", "src/foo/bar.ts", true
          "src/**/*.ts", "src/foo.ts", false
          "src/*.ts", "src/foo/bar.ts", false
          ".git/**", ".git/HEAD", true
          ".ros/context/**", ".ros/context/current.json", true
          "*.md", "README.md", true
          "*.md", "docs/README.md", false
          "a.b*c", "a.bXc", true
          "a.b*c", "aXbYc", false
          "file?.txt", "file1.txt", false
          "file?.txt", "fileA.txt", false
          "[abc]/*", "a/x", false
          "", "", true
          "**", "", true ]

    let tests =
        [ { Name = "glob match mirrors production's escape/double-star/single-star semantics"
            Run =
              fun () ->
                  for pattern, value, expected in cases do
                      Assert.equal expected (PathFilter.globMatch pattern value) }
          { Name = "default config marks a path meaningful only outside every ignored pattern"
            Run =
              fun () ->
                  let config = PathFilterConfig.defaultConfig
                  Assert.equal true (PathFilter.isMeaningful config "src/feature.fs")
                  Assert.equal false (PathFilter.isMeaningful config ".ros/context/current.json")
                  Assert.equal false (PathFilter.isMeaningful config ".ros/telemetry/executions/EXE-1.json")
                  Assert.equal
                      [ "src/feature.fs" ]
                      (PathFilter.meaningfulPaths config [ "src/feature.fs"; ".ros/events/events.jsonl" ]) }
          { Name = "custom configured patterns override the defaults entirely"
            Run =
              fun () ->
                  let config =
                      { MeaningfulPatterns = [ "src/**" ]
                        IgnoredPatterns = [ "src/generated/**" ] }

                  Assert.equal true (PathFilter.isMeaningful config "src/app.ts")
                  Assert.equal false (PathFilter.isMeaningful config "src/generated/out.js")
                  Assert.equal false (PathFilter.isMeaningful config "docs/readme.md") }
          { Name = "file work config repository falls back to defaults field by field"
            Run =
              fun () ->
                  let root = Path.Combine(Path.GetTempPath(), $"ros-work-config-{Guid.NewGuid():N}")
                  Directory.CreateDirectory root |> ignore

                  try
                      Assert.equal PathFilterConfig.defaultConfig (FileWorkConfigRepository.readPathFilterConfig root)

                      File.WriteAllText(
                          Path.Combine(root, "ros.json"),
                          """{"workProtocol":{"meaningfulPaths":["src/**"]}}"""
                      )

                      Assert.equal
                          { MeaningfulPatterns = [ "src/**" ]
                            IgnoredPatterns = PathFilterConfig.defaultConfig.IgnoredPatterns }
                          (FileWorkConfigRepository.readPathFilterConfig root)

                      File.WriteAllText(
                          Path.Combine(root, "ros.json"),
                          """{"workProtocol":{"meaningfulPaths":["src/**"],"ignoredPaths":["src/generated/**"]}}"""
                      )

                      Assert.equal
                          { MeaningfulPatterns = [ "src/**" ]
                            IgnoredPatterns = [ "src/generated/**" ] }
                          (FileWorkConfigRepository.readPathFilterConfig root)
                  finally
                      Directory.Delete(root, true) } ]
