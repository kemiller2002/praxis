namespace Ros.Domain.Work

type BacklogQueueRow =
    { Id: string
      Title: string
      Tags: string list
      Priority: string option
      Status: string }

/// Mirrors production `mergedRows`/`effectiveStatus`/`renderQueueMarkdown`
/// (`tools/ros_cli.mjs`): the projection used to regenerate `queue.md`
/// alongside every real backlog-queue write.
[<RequireQualifiedAccess>]
module QueuePresentation =
    let private semanticStateCode state =
        match state with
        | LiveWorkState.Ready -> "ready"
        | LiveWorkState.Active -> "active"
        | LiveWorkState.Blocked -> "blocked"
        | LiveWorkState.Complete -> "complete"

    /// An active/blocked/complete live item always wins; otherwise the
    /// backlog's own status; otherwise a "ready" live item alone.
    let effectiveStatus (queueStatus: string option) (contextState: LiveWorkState option) =
        match contextState with
        | Some state when state <> LiveWorkState.Ready -> Some(semanticStateCode state)
        | _ ->
            match queueStatus with
            | Some status -> Some status
            | None -> contextState |> Option.map semanticStateCode

    /// Every id present in the queue or the live context, ordinal-sorted;
    /// backlog fields default when only a live item exists for an id.
    let mergedRows (queueItems: BacklogQueueRow list) (contextItems: LiveWorkItem list) : BacklogQueueRow list =
        let queueById = queueItems |> List.map (fun item -> item.Id, item) |> Map.ofList
        let contextById = contextItems |> List.map (fun item -> item.Id, item) |> Map.ofList

        let ids =
            (queueItems |> List.map (fun item -> item.Id))
            @ (contextItems |> List.map (fun item -> item.Id))
            |> List.distinct
            |> List.sortWith (fun left right -> System.String.CompareOrdinal(left, right))

        ids
        |> List.map (fun id ->
            let queueItem = queueById.TryFind id
            let contextItem = contextById.TryFind id

            { Id = id
              Title = queueItem |> Option.map (fun item -> item.Title) |> Option.defaultValue id
              Tags = queueItem |> Option.map (fun item -> item.Tags) |> Option.defaultValue []
              Priority = queueItem |> Option.bind (fun item -> item.Priority)
              Status =
                effectiveStatus
                    (queueItem |> Option.map (fun item -> item.Status))
                    (contextItem |> Option.map (fun item -> item.SemanticState))
                |> Option.defaultValue "" })

    /// Byte-identical to production's own trailing-newline-only-when-nonempty
    /// rendering: an empty queue yields the header with no trailing row.
    let renderMarkdown (rows: BacklogQueueRow list) =
        let header = "# Work Queue\n\n| ID | Work | Status | Tags | Priority |\n|---|---|---|---|---|\n"

        let body =
            rows
            |> List.map (fun row ->
                let tags = String.concat ", " row.Tags
                let priority = row.Priority |> Option.defaultValue ""
                $"| {row.Id} | {row.Title} | {row.Status} | {tags} | {priority} |")
            |> String.concat "\n"

        if body = "" then header else header + body + "\n"
