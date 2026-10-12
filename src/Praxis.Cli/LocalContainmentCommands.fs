namespace Praxis.Cli

open Praxis.Infrastructure.Planning

/// `praxis local containment qualify|status --host-config FILE [--json]`.
/// Qualification launches real probes through the confined adapter and
/// writes `praxis.containment-evidence/1`; status reports the launch gate.
/// Exit codes: 0 qualified/admitted, 2 invalid arguments, 3 refused or
/// unqualified (including unsupported platforms).
[<RequireQualifiedAccess>]
module LocalContainmentCommands =
    let usage = "local containment qualify|status --host-config FILE [--json]"

    let private hostConfig (arguments: string list) =
        arguments |> List.pairwise |> List.tryPick (fun (flag, value) -> if flag = "--host-config" then Some value else None)

    let private emit json (result: Result<LocalContainmentReport, LocalCommandFailure>) =
        match result with
        | Ok report ->
            if json then printfn "%s" report.Json else report.Lines |> List.iter (printfn "%s")
            if report.Qualified then 0 else 3
        | Error(LocalCommandFailure.Invalid message) ->
            eprintfn "ERROR %s" message
            2
        | Error(LocalCommandFailure.Refused message) ->
            eprintfn "REFUSED %s" message
            3

    let run root (arguments: string list) =
        let json = List.contains "--json" arguments
        match arguments, hostConfig arguments with
        | "qualify" :: _, Some config -> LocalContainmentService.qualify root config |> emit json
        | "status" :: _, Some config -> LocalContainmentService.status root config |> emit json
        | _ ->
            eprintfn "Usage: praxis %s" usage
            2
