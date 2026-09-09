namespace Ros.Tests

open System
open System.IO
open System.Text
open Ros.Domain.Work
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module WorkAttachmentEffectTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-work-attach-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    let private writeQueue root (json: string) =
        let workDirectory = Path.Combine(root, ".ros", "work")
        Directory.CreateDirectory workDirectory |> ignore
        File.WriteAllText(Path.Combine(workDirectory, "queue.json"), json)

    let tests =
        [ { Name = "readAttachmentSequences defaults to empty when the file is absent, the id is absent, or the item has none"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      Assert.equal [] (FileBacklogQueueRepository.readAttachmentSequences root "WI-0001")

                      writeQueue root """{"schemaVersion":"1.0.0","repository":"r","nextSeq":2,"items":[{"id":"WI-0001","status":"ready"}]}"""
                      Assert.equal [] (FileBacklogQueueRepository.readAttachmentSequences root "WI-0001")
                      Assert.equal [] (FileBacklogQueueRepository.readAttachmentSequences root "WI-9999")) }
          { Name = "readAttachmentSequences reads real seq values, treating a missing seq as zero"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeQueue
                          root
                          """{"schemaVersion":"1.0.0","repository":"r","nextSeq":2,"items":[{"id":"WI-0001","status":"ready","attachments":[{"id":"ATT-1","seq":1},{"id":"ATT-2","seq":5},{"id":"ATT-legacy"}]}]}"""

                      Assert.equal (Set.ofList [ 1; 5; 0 ]) (Set.ofList (FileBacklogQueueRepository.readAttachmentSequences root "WI-0001"))) }
          { Name = "attaching to an existing item writes the file bytes, appends the record, and preserves every unrelated field verbatim"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeQueue
                          root
                          """{"schemaVersion":"1.0.0","repository":"repository","nextSeq":2,"items":[{"id":"WI-0001","title":"It's a \"quoted\" title","description":null,"tags":["a"],"priority":"high","status":"ready","attachments":[{"id":"ATT-1","seq":1,"name":"old.txt","file":"1-old.txt","size":3,"contentType":null,"uploadedAt":"2026-01-01T00:00:00.000Z"}],"createdAt":"2026-01-01T00:00:00.000Z","updatedAt":"2026-01-01T00:00:00.000Z"},{"id":"WI-0002","title":"Untouched","tags":[],"priority":null,"status":"ready"}]}"""

                      let plan: WorkAttachmentPlan =
                          { UpsertNew = false
                            Record =
                              { Id = "ATT-2"
                                Seq = 2
                                Name = "My Cool File!.txt"
                                File = "2-My_Cool_File_.txt"
                                Size = 5L
                                ContentType = None
                                UploadedAt = "2026-09-09T18:00:00.000Z" }
                            UpdatedAt = "2026-09-09T18:00:00.000Z" }

                      let bytes = Encoding.UTF8.GetBytes "hello"

                      match FileBacklogQueueRepository.applyAttachment root "WI-0001" plan bytes [] with
                      | Error message -> failwith message
                      | Ok row -> Assert.equal "WI-0001" row.Id

                      let writtenBytes =
                          File.ReadAllBytes(Path.Combine(root, ".ros", "work", "attachments", "WI-0001", "2-My_Cool_File_.txt"))

                      Assert.equal bytes writtenBytes

                      let queueContent = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.json"))
                      Assert.isTrue (not (queueContent.Contains "\\u0027")) "an apostrophe must not be HTML-safe-escaped"
                      Assert.isTrue (queueContent.Contains "\"file\": \"1-old.txt\"") "the prior attachment must survive untouched"
                      Assert.isTrue (queueContent.Contains "\"file\": \"2-My_Cool_File_.txt\"") "the new attachment must be appended"
                      Assert.isTrue (queueContent.Contains "\"id\": \"WI-0002\"") "an unrelated item must survive untouched") }
          { Name = "attaching to an id known only to the live context upserts the minimal default record first"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeQueue root """{"schemaVersion":"1.0.0","repository":"repository","nextSeq":1,"items":[]}"""

                      let plan: WorkAttachmentPlan =
                          { UpsertNew = true
                            Record =
                              { Id = "ATT-1"
                                Seq = 1
                                Name = "report.txt"
                                File = "1-report.txt"
                                Size = 3L
                                ContentType = None
                                UploadedAt = "2026-09-09T18:00:00.000Z" }
                            UpdatedAt = "2026-09-09T18:00:00.000Z" }

                      match FileBacklogQueueRepository.applyAttachment root "WI-LIVE" plan (Encoding.UTF8.GetBytes "abc") [] with
                      | Error message -> failwith message
                      | Ok row ->
                          Assert.equal "WI-LIVE" row.Id
                          Assert.equal "WI-LIVE" row.Title
                          Assert.equal "captured" row.Status

                      let queueContent = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.json"))
                      Assert.isTrue (queueContent.Contains "\"file\": \"1-report.txt\"") "the upserted item must carry the new attachment") } ]
