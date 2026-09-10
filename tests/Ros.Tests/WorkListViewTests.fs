namespace Ros.Tests

open Ros.Domain.Work

/// Typed tests for `Ros.Domain.Work.WorkListView.mergedRows`, the pure
/// merge behind `work`/`work list`/`work show` -- production's own
/// `mergedRows` (`tools/ros_cli.mjs`) at its full fidelity, unlike
/// `QueuePresentation.mergedRows`'s narrower `queue.md`-only projection.
[<RequireQualifiedAccess>]
module WorkListViewTests =
    let private queueItem id title status : QueueItemDetail =
        { Id = id
          Title = title
          Description = None
          Tags = []
          Priority = None
          Status = status
          BlockedReason = None
          Attachments = [] }

    let private localStateCode state =
        match state with
        | LiveWorkState.Ready -> "ready"
        | LiveWorkState.Active -> "active"
        | LiveWorkState.Blocked -> "blocked"
        | LiveWorkState.Complete -> "complete"

    let private liveItem id state : LiveWorkItem =
        { Id = id
          WorkType = "task"
          LocalState = localStateCode state
          SemanticState = state
          Evidence = []
          BlockReason = None
          UpdatedAt = None
          CompletedAt = None
          TelemetryExecutionIds = [] }

    let tests =
        [ { Name = "a backlog-only item exposes backlogActions computed from its status and carries no liveWorkItem"
            Run =
              fun () ->
                  let merged = WorkListView.mergedRows [ queueItem "WI-0001" "Captured" "captured" ] []

                  match merged with
                  | [ row ] ->
                      Assert.equal [ "abandon"; "ready" ] row.BacklogActions
                      Assert.equal None row.LiveWorkItem
                      Assert.equal "captured" row.Status
                  | _ -> failwith "expected exactly one row" }

          { Name = "a live-only item defaults title to its id, carries no backlogActions, and exposes a full liveWorkItem summary"
            Run =
              fun () ->
                  let merged = WorkListView.mergedRows [] [ liveItem "WI-0002" LiveWorkState.Active ]

                  match merged with
                  | [ row ] ->
                      Assert.equal "WI-0002" row.Title
                      Assert.equal [] row.BacklogActions
                      Assert.equal
                          (Some { State = "active"; SemanticState = "active"; AllowedActions = [ "block"; "complete" ] })
                          row.LiveWorkItem
                  | _ -> failwith "expected exactly one row" }

          { Name = "a blocked live item overrides status and blockedReason, even when the backlog record disagrees"
            Run =
              fun () ->
                  let queue = { queueItem "WI-0003" "Blocked item" "ready" with BlockedReason = Some "stale backlog reason" }
                  let live = { liveItem "WI-0003" LiveWorkState.Blocked with BlockReason = Some "waiting on review" }

                  match WorkListView.mergedRows [ queue ] [ live ] with
                  | [ row ] ->
                      Assert.equal "blocked" row.Status
                      Assert.equal (Some "waiting on review") row.BlockedReason
                      Assert.equal [] row.BacklogActions
                  | _ -> failwith "expected exactly one row" }

          { Name = "blockedReason is genuinely absent (not merely null) when neither source names one"
            Run =
              fun () ->
                  match WorkListView.mergedRows [ queueItem "WI-0004" "Ready item" "ready" ] [] with
                  | [ row ] -> Assert.equal None row.BlockedReason
                  | _ -> failwith "expected exactly one row" }

          { Name = "attachments carry through verbatim, including a null content type"
            Run =
              fun () ->
                  let attachment: WorkAttachmentSummary =
                      { Id = "ATT-1"; Name = "note.txt"; Size = 7; ContentType = None; UploadedAt = "2026-01-01T00:00:00.000Z" }

                  let queue = { queueItem "WI-0005" "Has attachment" "ready" with Attachments = [ attachment ] }

                  match WorkListView.mergedRows [ queue ] [] with
                  | [ row ] -> Assert.equal [ attachment ] row.Attachments
                  | _ -> failwith "expected exactly one row" }

          { Name = "every id from either source is present, ordinal-sorted"
            Run =
              fun () ->
                  let merged =
                      WorkListView.mergedRows
                          [ queueItem "WI-0002" "Second" "ready" ]
                          [ liveItem "WI-0001" LiveWorkState.Active ]

                  Assert.equal [ "WI-0001"; "WI-0002" ] (merged |> List.map (fun row -> row.Id)) } ]
