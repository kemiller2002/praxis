namespace Ros.Tests

open System
open System.IO
open Ros.Domain.Git
open Ros.Domain.Work
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module WorkAttributionTests =
    let private request =
        { Enforce = true
          ObservedGitPaths = []
          PathFilterConfig = PathFilterConfig.defaultConfig
          BaselineDirtyPaths = []
          AttributedPaths = Set.empty
          HasActiveOrBlockedWork = false }

    let tests =
        [ { Name = "disabled enforcement yields no findings regardless of other fields"
            Run =
              fun () ->
                  let disabled =
                      { request with
                          Enforce = false
                          ObservedGitPaths = [ "src/feature.fs" ] }

                  Assert.empty (WorkAttribution.findings disabled) }
          { Name = "no meaningful paths yields no findings"
            Run =
              fun () ->
                  let noMeaningfulChange =
                      { request with ObservedGitPaths = [ ".ros/events/events.jsonl" ] }

                  Assert.empty (WorkAttribution.findings noMeaningfulChange) }
          { Name = "meaningful paths already in the baseline are excluded"
            Run =
              fun () ->
                  let baselined =
                      { request with
                          ObservedGitPaths = [ "src/feature.fs" ]
                          BaselineDirtyPaths = [ "src/feature.fs" ] }

                  Assert.empty (WorkAttribution.findings baselined) }
          { Name = "unattributed meaningful change with no active or blocked work yields a finding"
            Run =
              fun () ->
                  let unattributed =
                      { request with ObservedGitPaths = [ "src/feature.fs" ] }

                  let finding = Assert.single (WorkAttribution.findings unattributed)
                  Assert.equal "src/feature.fs" finding.Path
                  Assert.equal "work_items" finding.Field
                  Assert.equal "meaningful change has no active or completed work-item attribution" finding.Message }
          { Name = "a path recorded in the event log is treated as attributed"
            Run =
              fun () ->
                  let attributed =
                      { request with
                          ObservedGitPaths = [ "src/feature.fs" ]
                          AttributedPaths = Set.ofList [ "src/feature.fs" ] }

                  Assert.empty (WorkAttribution.findings attributed) }
          { Name = "active or blocked work in context excuses every meaningful change"
            Run =
              fun () ->
                  let excused =
                      { request with
                          ObservedGitPaths = [ "src/feature.fs"; "src/other.fs" ]
                          HasActiveOrBlockedWork = true }

                  Assert.empty (WorkAttribution.findings excused) }
          { Name = "file work config repository reads enforceAttribution field by field"
            Run =
              fun () ->
                  let root = Path.Combine(Path.GetTempPath(), $"ros-attribution-config-{Guid.NewGuid():N}")
                  Directory.CreateDirectory root |> ignore

                  try
                      Assert.equal false (FileWorkConfigRepository.readEnforceAttribution root)

                      File.WriteAllText(Path.Combine(root, "ros.json"), """{"workProtocol":{"enforceAttribution":true}}""")

                      Assert.equal true (FileWorkConfigRepository.readEnforceAttribution root)

                      File.WriteAllText(Path.Combine(root, "ros.json"), """{"workProtocol":{"enforceAttribution":false}}""")

                      Assert.equal false (FileWorkConfigRepository.readEnforceAttribution root)
                  finally
                      Directory.Delete(root, true) }
          { Name = "file event log repository collects attributed paths from every recorded event"
            Run =
              fun () ->
                  let root = Path.Combine(Path.GetTempPath(), $"ros-attribution-events-{Guid.NewGuid():N}")
                  let eventsDirectory = Path.Combine(root, ".ros", "events")
                  Directory.CreateDirectory eventsDirectory |> ignore

                  try
                      Assert.equal Set.empty (FileEventLogRepository.readAttributedPaths root)

                      File.WriteAllLines(
                          Path.Combine(eventsDirectory, "events.jsonl"),
                          [| """{"paths":["src/a.fs","src/b.fs"]}"""
                             ""
                             """{"paths":["src/c.fs"]}""" |]
                      )

                      Assert.equal
                          (Set.ofList [ "src/a.fs"; "src/b.fs"; "src/c.fs" ])
                          (FileEventLogRepository.readAttributedPaths root)
                  finally
                      Directory.Delete(root, true) }
          { Name = "unavailable-reason codes match production's exact gitFailure.reason literals"
            Run =
              fun () ->
                  Assert.equal "tool-unavailable" (GitUnavailableReason.code GitUnavailableReason.ToolUnavailable)
                  Assert.equal "not-repository" (GitUnavailableReason.code GitUnavailableReason.NotRepository)
                  Assert.equal "command-failed" (GitUnavailableReason.code GitUnavailableReason.CommandFailed)
                  Assert.equal "malformed-output" (GitUnavailableReason.code GitUnavailableReason.MalformedOutput) }
          { Name = "the CLI's synthetic .git finding message matches production's workFindings catch template"
            Run =
              fun () ->
                  let failure =
                      { Operation = "git status"
                        Reason = GitUnavailableReason.ToolUnavailable
                        Message = "spawn git ENOENT"
                        ExitCode = None }

                  let message =
                      $"cannot verify work attribution because {failure.Operation} is unavailable: {GitUnavailableReason.code failure.Reason}"

                  Assert.equal
                      "cannot verify work attribution because git status is unavailable: tool-unavailable"
                      message } ]
