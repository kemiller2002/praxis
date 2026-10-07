namespace Ros.Tests

open System
open System.IO
open Ros.Domain.Provenance
open Ros.Domain.Work
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module WorkCaptureEffectTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-work-capture-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    let private writeQueue root (json: string) =
        let workDirectory = Path.Combine(root, ".ros", "work")
        Directory.CreateDirectory workDirectory |> ignore
        File.WriteAllText(Path.Combine(workDirectory, "queue.json"), json)

    let private plan id : WorkCapturePlan =
        { Item =
            { Id = id
              Title = "A new task"
              Description = Some "details"
              Tags = [ "a"; "b" ]
              Priority = "medium"
              Status = "captured"
              CreatedAt = "2026-09-09T18:00:00.000Z"
              UpdatedAt = "2026-09-09T18:00:00.000Z"
              CreatedBy = "unknown"
              Source = "manual"
              SourceReference = None }
          NextSeq = 2 }

    let tests =
        [ { Name = "readNextSeq defaults to 1 when the file is absent, matching production's loadQueue default"
            Run = fun () -> withTemporaryRoot (fun root -> Assert.equal 1 (FileBacklogQueueRepository.readNextSeq root)) }
          { Name = "readNextSeq reads the persisted value"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeQueue root """{"schemaVersion":"1.0.0","repository":"r","nextSeq":9,"items":[]}"""
                      Assert.equal 9 (FileBacklogQueueRepository.readNextSeq root)) }
          { Name = "capturing on an absent queue file synthesizes production's own default document, then appends"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      match FileBacklogQueueRepository.captureItem root (plan "WI-0001") Actor.unknown [] with
                      | Error message -> failwith message
                      | Ok row ->
                          Assert.equal "WI-0001" row.Id
                          Assert.equal "captured" row.Status

                      let queueContent = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.json"))
                      Assert.isTrue (queueContent.Contains "\"schemaVersion\": \"1.0.0\"") "a synthesized document must carry the schema version"
                      Assert.isTrue (queueContent.Contains "\"nextSeq\": 2") "nextSeq must be the decided plan's value"
                      Assert.isTrue (queueContent.Contains "\"attachments\": []") "a captured item always starts with no attachments"
                      Assert.isTrue (queueContent.EndsWith "}\n") "the file must end with a single trailing newline") }
          { Name = "capturing on an existing queue preserves every prior item and field verbatim"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeQueue
                          root
                          """{"schemaVersion":"1.0.0","repository":"repository","nextSeq":2,"items":[{"id":"WI-0001","title":"It's a \"quoted\" title","description":null,"tags":[],"priority":"high","status":"ready","attachments":[{"id":"att-1"}],"createdAt":"2026-01-01T00:00:00.000Z","updatedAt":"2026-01-01T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null}]}"""

                      match FileBacklogQueueRepository.captureItem root (plan "WI-0002") Actor.unknown [] with
                      | Error message -> failwith message
                      | Ok row -> Assert.equal "WI-0002" row.Id

                      let queueContent = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.json"))
                      Assert.isTrue (not (queueContent.Contains "\\u0027")) "an apostrophe must not be HTML-safe-escaped"
                      Assert.isTrue (queueContent.Contains "It's a \\\"quoted\\\" title") "the prior item's title must survive verbatim"
                      Assert.isTrue (queueContent.Contains "\"att-1\"") "the prior item's attachments must survive untouched"
                      Assert.isTrue (queueContent.Contains "\"id\": \"WI-0002\"") "the new item must be appended"

                      let markdownContent = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.md"))
                      Assert.isTrue (markdownContent.Contains "| WI-0001 |") "the prior item must still appear in the regenerated table"
                      Assert.isTrue (markdownContent.Contains "| WI-0002 | A new task | captured | a, b | medium |") "the new item must appear in the regenerated table") }
          { Name = "a live-context-only item is reflected in the regenerated markdown alongside a fresh capture"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeQueue root """{"schemaVersion":"1.0.0","repository":"repository","nextSeq":1,"items":[]}"""

                      let contextItems =
                          [ { Id = "WI-LIVE"
                              WorkType = "task"
                              LocalState = "state"
                              SemanticState = LiveWorkState.Active
                              Evidence = []
                              BlockReason = None
                              UpdatedAt = None
                              CompletedAt = None
                              TelemetryExecutionIds = [] } ]

                      FileBacklogQueueRepository.captureItem root (plan "WI-0001") Actor.unknown contextItems |> ignore

                      let markdownContent = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.md"))
                      Assert.isTrue (markdownContent.Contains "| WI-LIVE | WI-LIVE | active |  |  |") "a live-only item must appear in the merged table") } ]
