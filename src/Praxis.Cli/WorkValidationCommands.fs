namespace Praxis.Cli

open System
open Praxis.Application.Work
open Praxis.Domain.Provenance
open Praxis.Infrastructure.Work

/// `praxis work validate-run` (PRAXIS-REL-005): parse, call
/// `ValidationRuns.run`, render one JSON document. Everything after `--` is
/// the command, passed as arguments (never through a shell). Exit codes: 0
/// the validation passed, 3 it failed or was unavailable, or the caller was
/// refused (`PRX.CLAIM.*`, `PRX.VALIDATION.DIRTY_WORKTREE`), 2 invalid
/// arguments, 1 storage or event failure.
[<RequireQualifiedAccess>]
module WorkValidationCommands =
    let usage =
        "work validate-run --id ID [--claimant NAME] --token TOKEN --generation N --validation NAME --evidence-id EVIDENCE-ID "
        + "[--timeout-seconds N] [--store local|git] [--remote NAME] -- COMMAND [ARGUMENT...]"

    /// Options before the first `--`, and the command after it.
    let split (arguments: string list) =
        match List.tryFindIndex ((=) "--") arguments with
        | Some index -> List.take index arguments, List.skip (index + 1) arguments
        | None -> arguments, []

    let private optionValue (name: string) (arguments: string list) =
        arguments
        |> List.pairwise
        |> List.tryPick (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private required name arguments =
        optionValue name arguments |> Option.map Ok |> Option.defaultValue (Error $"{name} is required")

    let private positive name (fallback: float) arguments =
        match optionValue name arguments with
        | None -> Ok fallback
        | Some text ->
            match Double.TryParse text with
            | true, value when value > 0.0 -> Ok value
            | _ -> Error $"{name} must be a positive number"

    let private request (actor: Actor) (options: string list) (command: string list) =
        required "--id" options
        |> Result.bind (fun id ->
            required "--token" options
            |> Result.bind (fun token ->
                match optionValue "--generation" options |> Option.map Int64.TryParse with
                | Some(true, generation) ->
                    required "--validation" options
                    |> Result.bind (fun validation ->
                        required "--evidence-id" options
                        |> Result.bind (fun evidenceId ->
                            positive "--timeout-seconds" 1800.0 options
                            |> Result.bind (fun seconds ->
                                if List.isEmpty command then
                                    Error "a command is required after '--'"
                                else
                                    Ok
                                        { WorkItemId = id
                                          Fence =
                                            { Claimant = optionValue "--claimant" options |> Option.defaultValue actor.Id
                                              ClaimToken = token
                                              Generation = generation }
                                          Validation = validation
                                          EvidenceId = evidenceId
                                          Command = command
                                          Timeout = TimeSpan.FromSeconds seconds })))
                | Some _ -> Error "--generation must be a number"
                | None -> Error "--generation is required"))

    /// Runs one validation for the current claim holder. `choice` is the
    /// already-parsed claim store; `options` excludes the command.
    let run root (actor: Actor) choice (options: string list) (command: string list) =
        match request actor options command with
        | Error message ->
            eprintfn "ERROR %s" message
            eprintfn "usage: %s" usage
            2
        | Ok request ->
            let ports = FileValidationRuns.ports root choice (Some actor) (optionValue "--execution" options)
            let text, code = ValidationRuns.run ports request |> ValidationRunOutcomeJson.render
            printfn "%s" text
            code
