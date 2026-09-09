namespace Ros.Domain.Work

type BacklogQueueItemRecord =
    { Id: string
      Status: string
      Priority: string option }

type BacklogQueueFinding =
    { Path: string
      Field: string
      Message: string }

/// Mirrors production `queueFindings` (`tools/ros_cli.mjs`): a pure decision
/// over the raw backlog queue rows, never a validated `BacklogState`, since
/// production reports an unrecognized status/priority as a finding rather
/// than rejecting parse.
[<RequireQualifiedAccess>]
module BacklogQueueValidation =
    let private queuePath = ".ros/work/queue.json"
    let private statusValues = set [ "captured"; "ready"; "blocked"; "abandoned" ]
    let private priorityValues = set [ "high"; "medium"; "low" ]

    let findings (items: BacklogQueueItemRecord list) : BacklogQueueFinding list =
        let seen = System.Collections.Generic.HashSet<string>()

        items
        |> List.collect (fun item ->
            let duplicate: BacklogQueueFinding list =
                if seen.Contains item.Id then
                    [ { Path = queuePath; Field = "id"; Message = $"duplicate backlog id '{item.Id}'" } ]
                else
                    []

            seen.Add item.Id |> ignore

            let invalidId: BacklogQueueFinding list =
                if not (WorkItemId.isValid item.Id) then
                    [ { Path = queuePath; Field = "id"; Message = $"invalid backlog id '{item.Id}'" } ]
                else
                    []

            let invalidStatus: BacklogQueueFinding list =
                if not (statusValues.Contains item.Status) then
                    [ { Path = queuePath
                        Field = "status"
                        Message = $"invalid status '{item.Status}' for '{item.Id}'" } ]
                else
                    []

            let invalidPriority: BacklogQueueFinding list =
                match item.Priority with
                | Some priority when not (priorityValues.Contains priority) ->
                    [ { Path = queuePath
                        Field = "priority"
                        Message = $"invalid priority '{priority}' for '{item.Id}'" } ]
                | _ -> []

            duplicate @ invalidId @ invalidStatus @ invalidPriority)
