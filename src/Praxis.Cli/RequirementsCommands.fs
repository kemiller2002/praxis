namespace Praxis.Cli

open Praxis.Application.Requirements
open Praxis.Contracts.Requirements
open Praxis.Infrastructure.Requirements

/// `praxis requirements preservation verify|sections` (REQ-PRES-001): the
/// deterministic requirements-preservation gate. The CLI parses and renders;
/// the gate lives in Domain, decoding in Contracts, reading in Infrastructure.
/// Exit codes: 0 pass, 3 fail or blocked, 2 invalid arguments or unreadable
/// input, so a gate can branch on the code and read `verdict` for detail.
[<RequireQualifiedAccess>]
module RequirementsCommands =
    let usage =
        "requirements preservation verify --baseline FILE --disposition FILE --derived DIR --source DIR [--approvals FILE] [--output FILE] [--json] | requirements preservation sections --source DIR --document PATH [--pattern REGEX] [--json]"

    let rec private option (name: string) (arguments: string list) =
        match arguments with
        | flag :: value :: _ when flag = name -> Some value
        | _ :: rest -> option name rest
        | [] -> None

    let private invalid (message: string) =
        eprintfn "ERROR %s" message
        2

    let private verify (arguments: string list) =
        match option "--baseline" arguments, option "--disposition" arguments, option "--derived" arguments, option "--source" arguments with
        | Some baseline, Some disposition, Some derived, Some source ->
            let request: PreservationGate.Request =
                { Baseline = baseline
                  Disposition = disposition
                  Approvals = option "--approvals" arguments
                  Derived = derived
                  Source = source }

            match PreservationGate.verify (FilePreservationInputs.create ()) request with
            | Error reason -> invalid reason
            | Ok outcome ->
                let json = PreservationJson.renderReport outcome.Inputs outcome.Report

                let written =
                    match option "--output" arguments with
                    | None -> Ok()
                    | Some path -> FilePreservationInputs.writeEvidence path json |> Result.mapError (fun reason -> $"could not write --output '{path}': {reason}")

                match written with
                | Error reason ->
                    eprintfn "ERROR %s" reason
                    1
                | Ok() ->
                    if List.contains "--json" arguments then printf "%s" json
                    else printfn "%s" (PreservationJson.renderText outcome.Inputs outcome.Report)

                    PreservationJson.exitCodeOf outcome.Report.Verdict
        | _ -> invalid "requirements preservation verify requires --baseline FILE, --disposition FILE, --derived DIR and --source DIR"

    let private sections (arguments: string list) =
        match option "--source" arguments, option "--document" arguments with
        | Some source, Some document ->
            let pattern = option "--pattern" arguments

            match PreservationGate.sections (FilePreservationInputs.create ()) source document pattern with
            | Error reason -> invalid reason
            | Ok(sha, entries) ->
                if List.contains "--json" arguments then
                    printf "%s" (PreservationJson.renderSections document entries sha)
                else
                    printfn "%s sha256 %s" document sha

                    for entry in entries do
                        let id = entry.Id |> Option.defaultValue "-"
                        printfn "  %s %s %s" entry.Sha256 id entry.Heading

                0
        | _ -> invalid "requirements preservation sections requires --source DIR and --document PATH"

    let run (arguments: string list) =
        match arguments with
        | "preservation" :: "verify" :: rest -> verify rest
        | "preservation" :: "sections" :: rest -> sections rest
        | _ -> invalid $"usage: praxis {usage}"
