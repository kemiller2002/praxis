namespace Praxis.Cli

open System
open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// `praxis streams check`: the integration contract of parallel workstreams
/// checked against Git facts (docs/work-protocol.md, "Integrating parallel
/// workstreams"). Read-only; exit 0 when every reported stream is ready, 3
/// otherwise, 2 for invalid arguments or an unreadable contract.
[<RequireQualifiedAccess>]
module StreamsCommands =
    let usage = "streams check [--stream ID]* [--against REF] [--contract FILE] [--json]"

    type private Options =
        { Streams: string list
          Against: string option
          Contract: string option
          Json: bool }

    let rec private parse (options: Options) (arguments: string list) =
        match arguments with
        | [] -> Ok options
        | "--json" :: rest -> parse { options with Json = true } rest
        | "--stream" :: value :: rest -> parse { options with Streams = options.Streams @ [ value ] } rest
        | "--against" :: value :: rest when options.Against.IsNone -> parse { options with Against = Some value } rest
        | "--contract" :: value :: rest when options.Contract.IsNone -> parse { options with Contract = Some value } rest
        | other :: _ -> Error $"unexpected argument '{other}'"

    let private printText (report: IntegrationReport) =
        let target = report.TargetCommit |> Option.map (fun commit -> $" ({commit.Substring(0, min 12 commit.Length)})") |> Option.defaultValue ""
        printfn "integration target: %s%s" report.Target target

        for finding in report.ContractFindings do
            printfn "contract: %s" finding

        match report.Cycles with
        | [] -> printfn "integration sequence: %s" (String.Join(" -> ", report.Sequence))
        | cycles ->
            for cycle in cycles do
                printfn "dependency cycle: %s" (String.Join(" -> ", cycle))

        for stream in report.Streams do
            let tip = stream.Tip |> Option.map (fun commit -> commit.Substring(0, min 12 commit.Length)) |> Option.defaultValue "unresolved"
            printfn ""
            printfn "stream %s (%s, %s @ %s): %s" stream.Stream stream.WorkItem stream.Ref tip (Readiness.code stream.Readiness)
            printfn "  ownership: %s%s" stream.OwnershipOrigin (if stream.Integrated then "; already integrated" else "")

            for finding in stream.Findings do
                match finding with
                | PathFinding.OutsideOwnership(path, owners) ->
                    let owned = if owners.IsEmpty then "" else $""" (owned by {String.Join(", ", owners)})"""
                    printfn "  outside-ownership %s%s" path owned
                | PathFinding.InterfaceUnapproved(path, id, owner) -> printfn "  interface-unapproved %s (interface %s, owner %s)" path id owner
                | PathFinding.DecisionNotFound(path, id, decision) -> printfn "  approval-decision-not-found %s (interface %s cites %s)" path id decision

            if not stream.TargetOverlap.IsEmpty then
                printfn "  overlap with target: %s" (String.Join(", ", stream.TargetOverlap))

            for condition in stream.Conditions do
                let status =
                    match condition.Met with
                    | Some true -> "met"
                    | Some false -> "unmet"
                    | None -> "unverified"

                printfn "  %-20s %-10s %s" (ReadinessCondition.code condition.Condition) status condition.Detail

            for test in stream.RequiredTests do
                printfn "  required test %s: %s" test.Test test.Status

        for overlap in report.SiblingOverlaps |> List.filter (fun overlap -> not overlap.Paths.IsEmpty) do
            printfn ""
            printfn "sibling overlap %s <-> %s: %s" overlap.Left overlap.Right (String.Join(", ", overlap.Paths))

        printfn ""
        printfn "passed: %s" (if IntegrationReport.passes report then "yes" else "no")

    let run (root: string) (arguments: string list) =
        match arguments with
        | "check" :: rest ->
            match parse { Streams = []; Against = None; Contract = None; Json = false } rest with
            | Error message ->
                eprintfn "ERROR %s; expected %s" message usage
                2
            | Ok options ->
                let selected = if options.Streams.IsEmpty then None else Some options.Streams

                match FileIntegrationStreams.check root options.Contract options.Against selected with
                | Error message ->
                    eprintfn "ERROR %s" message
                    2
                | Ok report ->
                    if options.Json then printf "%s" (IntegrationContractJson.renderJson report) else printText report
                    if IntegrationReport.passes report then 0 else 3
        | _ ->
            eprintfn "ERROR expected %s" usage
            2
