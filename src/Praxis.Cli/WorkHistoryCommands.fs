namespace Praxis.Cli

open System
open System.Globalization
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module WorkHistoryCommands =
    let usage = "work history merge [--apply] [--canonical-incoming-order --expected-head SHA --expected-incoming SHA --approval-path DECISION --approval-digest DIGEST --reason TEXT --occurred-at UTC]"
    let private render apply (result: Result<Praxis.Domain.Work.WorkHistorySnapshot, string>) =
        match result with
        | Error error -> eprintfn "ERROR %s" error; 1
        | Ok merged ->
            printfn "%s: %d work items; %d preserved events" (if apply then "history merged; review and git add the two native history files" else "history merge preview; no history files changed") merged.Items.Length merged.Events.Length
            0

    let run root arguments =
        match arguments with
        | [ "merge" ] | [ "merge"; "--apply" ] ->
            let apply = arguments |> List.contains "--apply"
            FileWorkHistoryMerge.run root apply |> render apply
        | "merge" :: rest when rest |> List.contains "--canonical-incoming-order" ->
            let valued = Set.ofList [ "--expected-head"; "--expected-incoming"; "--approval-path"; "--approval-digest"; "--reason"; "--occurred-at" ]
            let rec parse remaining flags (values: Map<string, string>) =
                match remaining with
                | [] -> if values.Count = valued.Count then Ok(flags, values) else Error "canonical amendment requires every explicit binding"
                | flag :: tail when flag = "--apply" || flag = "--canonical-incoming-order" ->
                    if Set.contains flag flags then Error "duplicate history flag" else parse tail (Set.add flag flags) values
                | flag :: value :: tail when valued.Contains flag ->
                    if Map.containsKey flag values then Error "duplicate history binding" else parse tail flags (Map.add flag value values)
                | _ -> Error "unknown or incomplete history amendment arguments"
            match parse rest Set.empty Map.empty with
            | Error error -> eprintfn "ERROR %s; expected %s" error usage; 2
            | Ok(flags, values) ->
                match DateTimeOffset.TryParse(values["--occurred-at"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
                | false, _ -> eprintfn "ERROR invalid amendment UTC time"; 2
                | true, occurredAt ->
                    ProvenanceCommands.withResolvedActor [] (fun actor ->
                        let request =
                            { ExpectedHead = values["--expected-head"]; ExpectedIncoming = values["--expected-incoming"]
                              ApprovalPath = values["--approval-path"]; ApprovalDigest = values["--approval-digest"]
                              Reason = values["--reason"]; OccurredAt = occurredAt; Actor = actor }
                        let apply = flags.Contains "--apply"
                        FileWorkHistoryMerge.runAmended root apply request |> render apply)
        | _ -> eprintfn "ERROR expected %s" usage; 2

    /// Related native history repairs share a thin route; the root dispatcher does not grow.
    let route root operation arguments =
        match operation with
        | "history" -> run root arguments
        | "reconcile" -> ProvenanceCommands.withResolvedActor arguments (ReconciliationCommands.run root arguments)
        | _ -> eprintfn "ERROR unknown native history repair"; 2
