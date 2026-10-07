namespace Praxis.Cli

open System
open System.Globalization
open Praxis.Infrastructure.Tutela

/// `praxis tutela ingest|metrics`: Tutela security assessments in, security
/// metrics over time out (TUT-1..3). The CLI parses and renders; ingestion
/// and derivation live in Infrastructure and Domain.
[<RequireQualifiedAccess>]
module TutelaCommands =
    let usage = "tutela ingest --input FILE [--collected-at TIMESTAMP] [--json] | tutela metrics [--repository NAME] [--json]"

    let rec private option (name: string) (arguments: string list) =
        match arguments with
        | flag :: value :: _ when flag = name -> Some value
        | _ :: rest -> option name rest
        | [] -> None

    let private ingest root arguments =
        let collectedAt =
            match option "--collected-at" arguments with
            | None -> Ok None
            | Some raw ->
                match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
                | true, at -> Ok(Some at)
                | _ -> Error $"--collected-at is not a timestamp: {raw}"

        match option "--input" arguments, collectedAt with
        | None, _ ->
            eprintfn "ERROR tutela ingest requires --input FILE"
            2
        | _, Error message ->
            eprintfn "ERROR %s" message
            2
        | Some input, Ok at ->
            match TutelaStore.ingestFile root input at with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok outcome ->
                let state, observation =
                    match outcome with
                    | TutelaStore.Recorded o -> "recorded", o
                    | TutelaStore.AlreadyRecorded o -> "already-recorded", o

                let commit = observation.Commit |> Option.defaultValue "unresolved"

                if List.contains "--json" arguments then
                    printfn
                        "{\"schema\":\"praxis.tutela-ingest/1\",\"outcome\":\"%s\",\"repository\":%s,\"ref\":%s,\"commit\":%s,\"sourceSha256\":\"%s\"}"
                        state
                        (Text.Json.JsonSerializer.Serialize observation.Repository)
                        (Text.Json.JsonSerializer.Serialize observation.Ref)
                        (observation.Commit |> Option.map Text.Json.JsonSerializer.Serialize |> Option.defaultValue "null")
                        observation.SourceSha256
                else
                    printfn "%s Tutela assessment for %s %s (commit %s)" state observation.Repository observation.Ref commit

                0

    let private metrics root arguments =
        match TutelaStore.metrics root with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok points ->
            let selected =
                match option "--repository" arguments with
                | Some repository -> points |> List.filter (fun p -> p.Repository = repository)
                | None -> points

            if List.contains "--json" arguments then printfn "%s" (TutelaStore.render selected)
            else printfn "%s" (TutelaStore.renderText selected)

            0

    let run (root: string) (arguments: string list) =
        match arguments with
        | "ingest" :: rest -> ingest root rest
        | "metrics" :: rest -> metrics root rest
        | _ ->
            eprintfn "ERROR usage: praxis %s" usage
            2
