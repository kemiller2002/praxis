namespace Ros.Tests

open System
open System.IO
open Ros.Domain.Work
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module BacklogTransitionEffectTests =
    let private writeQueue root (json: string) =
        let workDirectory = Path.Combine(root, ".ros", "work")
        Directory.CreateDirectory workDirectory |> ignore
        File.WriteAllText(Path.Combine(workDirectory, "queue.json"), json)

    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-backlog-transition-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    let tests =
        [ { Name = "applying a state change preserves every unrelated field and item verbatim"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeQueue
                          root
                          """{"schemaVersion":"1.0.0","repository":"repository","nextSeq":3,"items":[{"id":"WI-0001","title":"It's a \"quoted\" title","description":null,"tags":["code","testing"],"priority":"high","status":"ready","attachments":[{"id":"att-1","name":"x.txt"}],"createdAt":"2026-01-01T00:00:00.000Z","updatedAt":"2026-01-01T00:00:00.000Z","sourceReference":null},{"id":"WI-0002","title":"Second","tags":[],"priority":null,"status":"ready"}]}"""

                      let outcome =
                          FileBacklogQueueRepository.applyStateChange
                              root
                              "WI-0001"
                              BacklogState.Blocked
                              (BacklogFieldChange.Set "needs review")
                              BacklogFieldChange.Keep
                              "2026-09-09T17:00:00.000Z"
                              []

                      match outcome with
                      | Error message -> failwith message
                      | Ok row ->
                          Assert.equal "WI-0001" row.Id
                          Assert.equal "blocked" row.Status

                      let queueContent = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.json"))
                      Assert.isTrue (not (queueContent.Contains "\\u0027")) "an apostrophe must not be HTML-safe-escaped like production's own JSON.stringify"
                      Assert.isTrue (queueContent.Contains "It's a \\\"quoted\\\" title") "title must be preserved verbatim (JSON-escaped quotes only)"
                      Assert.isTrue (queueContent.Contains "\"blockedReason\": \"needs review\"") "blockedReason must be written"
                      Assert.isTrue (queueContent.Contains "\"att-1\"") "unrelated nested fields must survive untouched"
                      Assert.isTrue (queueContent.Contains "\"id\": \"WI-0002\"") "an unrelated item must survive untouched"
                      Assert.isTrue (queueContent.EndsWith "}\n") "the file must end with a single trailing newline"

                      let markdownContent = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.md"))
                      Assert.isTrue
                          (markdownContent.Contains "| WI-0001 | It's a \"quoted\" title | blocked | code, testing | high |")
                          "queue.md must reflect the mutated row") }
          { Name = "clearing a reason removes the field entirely rather than nulling it"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeQueue
                          root
                          """{"schemaVersion":"1.0.0","repository":"repository","nextSeq":2,"items":[{"id":"WI-0001","title":"First","tags":[],"priority":null,"status":"blocked","blockedReason":"was blocked","updatedAt":"2026-01-01T00:00:00.000Z"}]}"""

                      let outcome =
                          FileBacklogQueueRepository.applyStateChange
                              root
                              "WI-0001"
                              BacklogState.Ready
                              BacklogFieldChange.Clear
                              BacklogFieldChange.Keep
                              "2026-09-09T17:00:00.000Z"
                              []

                      match outcome with
                      | Error message -> failwith message
                      | Ok row -> Assert.equal "ready" row.Status

                      let queueContent = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.json"))
                      Assert.isTrue (not (queueContent.Contains "blockedReason")) "a cleared field must be removed, not written as null") }
          { Name = "a merged live-context item is reflected in the regenerated markdown even though it is untouched by the effect"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeQueue
                          root
                          """{"schemaVersion":"1.0.0","repository":"repository","nextSeq":2,"items":[{"id":"WI-0001","title":"First","tags":[],"priority":null,"status":"ready","updatedAt":"2026-01-01T00:00:00.000Z"}]}"""

                      let contextItems =
                          [ { Id = "WI-0009"
                              WorkType = "task"
                              LocalState = "state"
                              SemanticState = LiveWorkState.Active
                              Evidence = []
                              BlockReason = None
                              UpdatedAt = None
                              CompletedAt = None
                              TelemetryExecutionIds = [] } ]

                      FileBacklogQueueRepository.applyStateChange
                          root
                          "WI-0001"
                          BacklogState.Blocked
                          (BacklogFieldChange.Set "reason")
                          BacklogFieldChange.Keep
                          "2026-09-09T17:00:00.000Z"
                          contextItems
                      |> ignore

                      let markdownContent = File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.md"))
                      Assert.isTrue (markdownContent.Contains "| WI-0009 | WI-0009 | active |  |  |") "a live-only item must appear in the merged table") }
          { Name = "an unknown id is rejected without touching either file"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeQueue root """{"schemaVersion":"1.0.0","repository":"repository","nextSeq":1,"items":[]}"""

                      match
                          FileBacklogQueueRepository.applyStateChange
                              root
                              "WI-9999"
                              BacklogState.Ready
                              BacklogFieldChange.Keep
                              BacklogFieldChange.Keep
                              "2026-09-09T17:00:00.000Z"
                              []
                      with
                      | Ok row -> failwith $"expected rejection but got {row}"
                      | Error message -> Assert.equal "'WI-9999' is not a captured local work item" message

                      Assert.isTrue
                          (not (File.Exists(Path.Combine(root, ".ros", "work", "queue.md"))))
                          "an unknown id must never cause a queue.md write") }
          { Name = "a missing queue file is rejected the same way as an unknown id"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      match
                          FileBacklogQueueRepository.applyStateChange
                              root
                              "WI-0001"
                              BacklogState.Ready
                              BacklogFieldChange.Keep
                              BacklogFieldChange.Keep
                              "2026-09-09T17:00:00.000Z"
                              []
                      with
                      | Ok row -> failwith $"expected rejection but got {row}"
                      | Error message -> Assert.equal "'WI-0001' is not a captured local work item" message) } ]
