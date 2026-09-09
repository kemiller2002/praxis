namespace Ros.Tests

open Ros.Domain.Work

[<RequireQualifiedAccess>]
module QueuePresentationTests =
    let private row id title tags priority status : BacklogQueueRow =
        { Id = id; Title = title; Tags = tags; Priority = priority; Status = status }

    let private liveItem id state : LiveWorkItem =
        { Id = id
          WorkType = "task"
          LocalState = "state"
          SemanticState = state
          Evidence = []
          BlockReason = None
          UpdatedAt = None
          CompletedAt = None
          TelemetryExecutionIds = [] }

    let tests =
        [ { Name = "effective status prefers an active or blocked live item over the backlog's own status"
            Run =
              fun () ->
                  Assert.equal (Some "active") (QueuePresentation.effectiveStatus (Some "ready") (Some LiveWorkState.Active))
                  Assert.equal (Some "blocked") (QueuePresentation.effectiveStatus (Some "captured") (Some LiveWorkState.Blocked)) }
          { Name = "effective status falls back to the backlog's own status when the live item is merely ready"
            Run = fun () -> Assert.equal (Some "captured") (QueuePresentation.effectiveStatus (Some "captured") (Some LiveWorkState.Ready)) }
          { Name = "effective status uses the live item's own state when there is no backlog entry at all"
            Run =
              fun () ->
                  Assert.equal (Some "active") (QueuePresentation.effectiveStatus None (Some LiveWorkState.Active))
                  Assert.equal (Some "ready") (QueuePresentation.effectiveStatus None (Some LiveWorkState.Ready)) }
          { Name = "effective status is absent with neither a backlog entry nor a live item"
            Run = fun () -> Assert.equal None (QueuePresentation.effectiveStatus None None) }
          { Name = "merged rows include every id from either source, ordinal-sorted, defaulting absent backlog fields"
            Run =
              fun () ->
                  let queueItems = [ row "WI-0002" "Second" [ "a" ] (Some "high") "ready" ]
                  let contextItems = [ liveItem "WI-0001" LiveWorkState.Active ]

                  let merged = QueuePresentation.mergedRows queueItems contextItems

                  Assert.equal
                      [ { Id = "WI-0001"; Title = "WI-0001"; Tags = []; Priority = None; Status = "active" }
                        { Id = "WI-0002"; Title = "Second"; Tags = [ "a" ]; Priority = Some "high"; Status = "ready" } ]
                      merged }
          { Name = "an empty row set renders only the markdown header, with no trailing newline"
            Run = fun () -> Assert.equal "# Work Queue\n\n| ID | Work | Status | Tags | Priority |\n|---|---|---|---|---|\n" (QueuePresentation.renderMarkdown []) }
          { Name = "rows render as a pipe-delimited table matching production's column order and blank-cell defaults"
            Run =
              fun () ->
                  let rows =
                      [ row "WI-0001" "First" [ "a"; "b" ] (Some "high") "ready"
                        row "WI-0002" "Second" [] None "captured" ]

                  Assert.equal
                      "# Work Queue\n\n| ID | Work | Status | Tags | Priority |\n|---|---|---|---|---|\n| WI-0001 | First | ready | a, b | high |\n| WI-0002 | Second | captured |  |  |\n"
                      (QueuePresentation.renderMarkdown rows) } ]
