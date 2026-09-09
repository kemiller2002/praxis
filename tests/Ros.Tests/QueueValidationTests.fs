namespace Ros.Tests

open System
open System.IO
open Ros.Domain.Work
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module QueueValidationTests =
    let private item id status priority : BacklogQueueItemRecord =
        { Id = id; Status = status; Priority = priority }

    let tests =
        [ { Name = "an empty queue yields no findings"
            Run = fun () -> Assert.empty (BacklogQueueValidation.findings []) }
          { Name = "a well-formed item yields no findings"
            Run =
              fun () ->
                  Assert.empty (BacklogQueueValidation.findings [ item "WI-0001" "ready" (Some "high") ]) }
          { Name = "a missing priority is not a finding"
            Run =
              fun () ->
                  Assert.empty (BacklogQueueValidation.findings [ item "WI-0001" "ready" None ]) }
          { Name = "a duplicate id is flagged on its second occurrence, not its first"
            Run =
              fun () ->
                  let finding =
                      Assert.single (BacklogQueueValidation.findings [ item "WI-0001" "ready" None; item "WI-0001" "blocked" None ])

                  Assert.equal ".ros/work/queue.json" finding.Path
                  Assert.equal "id" finding.Field
                  Assert.equal "duplicate backlog id 'WI-0001'" finding.Message }
          { Name = "an invalid id is flagged"
            Run =
              fun () ->
                  let finding = Assert.single (BacklogQueueValidation.findings [ item "not-an-id" "ready" None ])
                  Assert.equal "id" finding.Field
                  Assert.equal "invalid backlog id 'not-an-id'" finding.Message }
          { Name = "an invalid status is flagged with the offending id"
            Run =
              fun () ->
                  let finding = Assert.single (BacklogQueueValidation.findings [ item "WI-0001" "in-progress" None ])
                  Assert.equal "status" finding.Field
                  Assert.equal "invalid status 'in-progress' for 'WI-0001'" finding.Message }
          { Name = "an invalid priority is flagged with the offending id"
            Run =
              fun () ->
                  let finding = Assert.single (BacklogQueueValidation.findings [ item "WI-0001" "ready" (Some "urgent") ])
                  Assert.equal "priority" finding.Field
                  Assert.equal "invalid priority 'urgent' for 'WI-0001'" finding.Message }
          { Name = "every valid semantic status and priority passes"
            Run =
              fun () ->
                  let items =
                      [ item "WI-0001" "captured" None
                        item "WI-0002" "ready" (Some "high")
                        item "WI-0003" "blocked" (Some "medium")
                        item "WI-0004" "abandoned" (Some "low") ]

                  Assert.empty (BacklogQueueValidation.findings items) }
          { Name = "file backlog queue repository reads real items and defaults to empty when the file is absent"
            Run =
              fun () ->
                  let root = Path.Combine(Path.GetTempPath(), $"ros-queue-repo-{Guid.NewGuid():N}")
                  let workDirectory = Path.Combine(root, ".ros", "work")
                  Directory.CreateDirectory workDirectory |> ignore

                  try
                      Assert.empty (FileBacklogQueueRepository.readItems root)

                      File.WriteAllText(
                          Path.Combine(workDirectory, "queue.json"),
                          """{"schemaVersion":"1.0.0","items":[{"id":"WI-0001","status":"ready","priority":"high"},{"id":"WI-0002","status":"captured"}]}"""
                      )

                      Assert.equal
                          [ item "WI-0001" "ready" (Some "high"); item "WI-0002" "captured" None ]
                          (FileBacklogQueueRepository.readItems root)
                  finally
                      Directory.Delete(root, true) } ]
