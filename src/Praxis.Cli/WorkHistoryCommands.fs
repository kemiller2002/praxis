namespace Praxis.Cli

open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module WorkHistoryCommands =
    let usage = "work history merge [--apply]"
    let run root arguments =
        match arguments with
        | [ "merge" ] | [ "merge"; "--apply" ] ->
            let apply = arguments |> List.contains "--apply"
            match FileWorkHistoryMerge.run root apply with
            | Error error -> eprintfn "ERROR %s" error; 1
            | Ok merged ->
                printfn "%s: %d work items; %d preserved events" (if apply then "history merged; review and git add the two native history files" else "history merge preview; no history files changed") merged.Items.Length merged.Events.Length
                0
        | _ -> eprintfn "ERROR expected %s" usage; 2

    /// Related native history repairs share a thin route; the root dispatcher does not grow.
    let route root operation arguments =
        match operation with
        | "history" -> run root arguments
        | "reconcile" -> ProvenanceCommands.withResolvedActor arguments (ReconciliationCommands.run root arguments)
        | _ -> eprintfn "ERROR unknown native history repair"; 2
