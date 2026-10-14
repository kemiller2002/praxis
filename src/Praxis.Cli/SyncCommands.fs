namespace Praxis.Cli

open System
open Praxis.Contracts.Git
open Praxis.Domain.Git
open Praxis.Infrastructure.Git

[<RequireQualifiedAccess>]
module SyncCommands =
    let usage = "sync status [--json] | sync check [--start] [--json]"

    let private outcome report =
        match report.Availability with
        | UpstreamSyncAvailability.Disabled -> "disabled"
        | UpstreamSyncAvailability.Available -> "available"
        | UpstreamSyncAvailability.Unavailable _ -> "unavailable"

    let private printText report =
        printfn "upstream sync: %s (%s/%s; %.0f-minute maximum age)" (outcome report) report.Config.Remote report.Config.Branch report.Config.MaxAge.TotalMinutes

        match report.LastSuccessfulCheckAt with
        | Some checkedAt -> printfn "last successful check: %s%s" (checkedAt.ToUniversalTime().ToString("O")) (if report.Stale then " (stale)" else "")
        | None -> printfn "last successful check: unavailable (check is due)"

        match report.Ahead, report.Behind with
        | Some ahead, Some behind -> printfn "relation: %d ahead, %d behind" ahead behind
        | _ -> printfn "relation: unavailable"

        if not report.OverlapPaths.IsEmpty then
            printfn "overlap: %s" (String.Join(", ", report.OverlapPaths))

        printfn "safe for final validation: %s" (if report.SafeForFinalValidation then "yes" else "no")

    let private print asJson report =
        if asJson then printf "%s" (UpstreamSyncContract.renderJson report) else printText report

    let statusNode root = FileUpstreamSynchronization.status root |> UpstreamSyncContract.toNode

    let run root arguments =
        let asJson = arguments |> List.contains "--json"

        match arguments |> List.filter ((<>) "--json") with
        | [ "status" ] ->
            let report = FileUpstreamSynchronization.status root
            print asJson report

            match report.Availability with
            | UpstreamSyncAvailability.Unavailable _ -> 1
            | _ -> 0
        | [ "check" ]
        | [ "check"; "--start" ] as command ->
            let report, succeeded = FileUpstreamSynchronization.check root (command |> List.contains "--start")
            print asJson report
            if succeeded then 0 else 1
        | _ ->
            eprintfn "ERROR expected %s" usage
            2
