namespace Ros.Tests

open System
open System.IO
open Ros.Domain.Work
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module WorkUpdateEffectTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-work-update-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    let private writeQueue root (json: string) =
        let workDirectory = Path.Combine(root, ".ros", "work")
        Directory.CreateDirectory workDirectory |> ignore
        File.WriteAllText(Path.Combine(workDirectory, "queue.json"), json)

    let private keepAll: WorkUpdatePlan =
        { UpsertNew = false
          Title = WorkTitleChange.Keep
          Description = WorkDescriptionChange.Keep
          Tags = WorkTagsChange.Keep
          Priority = WorkPriorityChange.Keep
          UpdatedAt = "2026-09-09T18:00:00.000Z" }

    let tests =
        [ { Name = "an update preserves every unrelated field and item verbatim, changing only what was decided"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeQueue
                          root
                          """{"schemaVersion":"1.0.0","repository":"repository","nextSeq":2,"items":[{"id":"WI-0001","title":"It's a \"quoted\" title","description":null,"tags":["a"],"priority":"high","status":"ready","attachments":[{"id":"att-1"}],"createdAt":"2026-01-01T00:00:00.000Z","updatedAt":"2026-01-01T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null},{"id":"WI-0002","title":"Untouched","tags":[],"priority":null,"status":"ready"}]}"""

                      let plan =
                          { keepAll with
                              Title = WorkTitleChange.Set "New title"
                              Priority = WorkPriorityChange.Set "low" }

                      match FileBacklogQueueRepository.applyUpdate root "WI-0001" plan ProvenanceFixtures.contribution [] with
                      | Error message -> failwith message
                      | Ok row ->
                          Assert.equal "New title" row.Title
                          Assert.equal (Some "low") row.Priority

                      let queueContent = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.json"))
                      Assert.isTrue (not (queueContent.Contains "\\u0027")) "an apostrophe must not be HTML-safe-escaped"
                      Assert.isTrue (queueContent.Contains "\"att-1\"") "unrelated nested fields must survive untouched"
                      Assert.isTrue (queueContent.Contains "\"id\": \"WI-0002\"") "an unrelated item must survive untouched"
                      Assert.isTrue (queueContent.Contains "\"tags\": [\n        \"a\"\n      ]") "an untouched field (tags) must survive verbatim") }
          { Name = "setting a description to whitespace clears it to null rather than an empty string"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeQueue
                          root
                          """{"schemaVersion":"1.0.0","repository":"repository","nextSeq":2,"items":[{"id":"WI-0001","title":"First","description":"kept","tags":[],"priority":"medium","status":"ready"}]}"""

                      let plan = { keepAll with Description = WorkDescriptionChange.Set None }

                      FileBacklogQueueRepository.applyUpdate root "WI-0001" plan ProvenanceFixtures.contribution [] |> ignore

                      let queueContent = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.json"))
                      Assert.isTrue (queueContent.Contains "\"description\": null") "a cleared description must be written as JSON null") }
          { Name = "an id absent from the queue is upserted with production's exact minimal-record defaults, then the requested fields applied"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeQueue root """{"schemaVersion":"1.0.0","repository":"repository","nextSeq":1,"items":[]}"""

                      let plan = { keepAll with Tags = WorkTagsChange.Set [ "x" ] }

                      match FileBacklogQueueRepository.applyUpdate root "WI-LIVE" plan ProvenanceFixtures.contribution [] with
                      | Error message -> failwith message
                      | Ok row ->
                          Assert.equal "WI-LIVE" row.Id
                          Assert.equal "WI-LIVE" row.Title
                          Assert.equal "captured" row.Status
                          Assert.equal (Some "medium") row.Priority
                          Assert.equal [ "x" ] row.Tags

                      let queueContent = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.json"))
                      Assert.isTrue (queueContent.Contains "\"attachments\": []") "an upserted record starts with no attachments") }
          { Name = "an update on a completely absent queue.json synthesizes production's own default document"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      match FileBacklogQueueRepository.applyUpdate root "WI-0001" keepAll ProvenanceFixtures.contribution [] with
                      | Error message -> failwith message
                      | Ok row -> Assert.equal "WI-0001" row.Id

                      let queueContent = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.json"))
                      Assert.isTrue (queueContent.Contains "\"nextSeq\": 1") "a synthesized document must carry the default nextSeq") } ]
