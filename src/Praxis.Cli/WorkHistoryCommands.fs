namespace Praxis.Cli

open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module WorkHistoryCommands =
    let usage = "work history merge [--apply]"
    let run root arguments =
        match arguments with
        | [ "merge" ] | [ "merge"; "--apply" ] ->
            let apply = arguments |> List.contains "--apply"
            match FileWorkHistoryMerge.merge root apply with
            | Error error -> eprintfn "ERROR %s" error; 1
            | Ok merged ->
                let files =
                    ".ros/context/current.json .ros/events/events.jsonl"
                    + (if merged.Backlog.IsSome then " .ros/work/queue.json .ros/work/queue.md" else "")
                printfn "%s: %d work items; %d preserved events" (if apply then "history merged; review and git add " + files else "history merge preview; no history files changed") merged.History.Items.Length merged.History.Events.Length
                match merged.Backlog with
                | None -> ()
                | Some backlog when backlog.Regenerated ->
                    printfn "backlog queue: queue.json merged cleanly (%d rows, nextSeq %d); queue.md %s from it" backlog.MergedRows backlog.NextSeq (if apply then "regenerated" else "would be regenerated")
                | Some backlog ->
                    printfn "backlog queue: base %d, HEAD %d, MERGE_HEAD %d rows -> %d merged rows, nextSeq %d; queue.json and queue.md %s" backlog.BaseRows backlog.LeftRows backlog.RightRows backlog.MergedRows backlog.NextSeq (if apply then "written" else "would be written")
                0
        | _ -> eprintfn "ERROR expected %s" usage; 2

    /// Related native history repairs share a thin route; the root dispatcher does not grow.
    let route root operation arguments =
        match operation with
        | "history" -> run root arguments
        | "reconcile" -> ProvenanceCommands.withResolvedActor arguments (ReconciliationCommands.run root arguments)
        | _ -> eprintfn "ERROR unknown native history repair"; 2
